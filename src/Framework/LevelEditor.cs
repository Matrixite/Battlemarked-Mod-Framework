using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Runtime board editor, not the Unity level-authoring component.
// Tile reads are bounded, explicit snapshots. Only normal local game requests write.
internal sealed class LevelEditor
{
    private const int WindowSize = 8, ScanLimit = 4096;
    private readonly Plugin plugin;
    private readonly ManualLogSource log;
    private int? scene;
    private Context snapshot;
    private readonly List<Enemy> enemies = new();
    private readonly List<Enemy> matches = new();
    private TileCell[,] cells = new TileCell[WindowSize, WindowSize];
    private int minX, minY, sizeX, sizeY, startX, startY, enemyPage;
    private int panX, panY;
    private bool refresh, pan, confirming, guiErrorLogged;
    private string filter = "", status = "Refresh in a disposable local solo campaign.";
    private TileCell selectedTile;
    private Enemy selectedEnemy;
    private SpawnCommand pending;

    internal sealed class Context
    {
        internal readonly int Scene;
        internal readonly IntPtr Model;
        internal readonly Il2CppObjectBase State, Board, TileSet, Registry;
        internal readonly object Room, Spawner;
        internal Context(int scene, IntPtr model, Il2CppObjectBase state, Il2CppObjectBase board, Il2CppObjectBase tileSet, object room, Il2CppObjectBase registry, object spawner)
        { Scene = scene; Model = model; State = state; Board = board; TileSet = tileSet; Room = room; Registry = registry; Spawner = spawner; }
        internal bool SameSession(Context other) => other != null && Scene == other.Scene && Model == other.Model &&
            State.Pointer == other.State.Pointer && Board.Pointer == other.Board.Pointer && TileSet.Pointer == other.TileSet.Pointer &&
            Room is Il2CppObjectBase a && other.Room is Il2CppObjectBase b && a.Pointer != IntPtr.Zero && a.Pointer == b.Pointer &&
            ((Registry == null && other.Registry == null) ||
                (Registry != null && other.Registry != null && Registry.Pointer == other.Registry.Pointer));
    }
    private sealed class TileCell
    {
        internal readonly int X, Y;
        internal readonly bool Exists, Walkable, Occupied;
        internal readonly string Detail;
        internal TileCell(int x, int y, bool exists, bool walkable, bool occupied, string detail)
        { X = x; Y = y; Exists = exists; Walkable = walkable; Occupied = occupied; Detail = detail; }
        internal bool Available => Exists && Walkable && !Occupied;
        internal string Symbol => !Exists ? "-" : Occupied ? "P" : Walkable ? "." : "#";
    }
    private sealed class Enemy
    {
        internal readonly UObject Definition;
        internal readonly string Name, Key;
        internal Enemy(UObject definition, string name, string key) { Definition = definition; Name = name; Key = key; }
    }
    private sealed class SpawnCommand
    {
        internal readonly Context Snapshot;
        internal readonly TileCell Tile;
        internal readonly Enemy Enemy;
        internal SpawnCommand(Context snapshot, TileCell tile, Enemy enemy) { Snapshot = snapshot; Tile = tile; Enemy = enemy; }
    }

    internal LevelEditor(Plugin plugin, ManualLogSource log) { this.plugin = plugin; this.log = log; }

    private void Clear()
    {
        snapshot = null;
        cells = new TileCell[WindowSize, WindowSize];
        selectedTile = null; selectedEnemy = null; pending = null;
        enemies.Clear(); matches.Clear();
        confirming = refresh = pan = false;
        panX = panY = enemyPage = 0;
        filter = "";
        status = "Refresh in a disposable local solo campaign.";
    }

    internal void Leave() { Clear(); scene = null; }

    internal void Tick(bool enabled)
    {
        if (!enabled)
        {
            if (scene.HasValue) Leave();
            return;
        }
        try
        {
            var current = SceneManager.GetActiveScene();
            if (!current.IsValid() || !current.isLoaded) { Clear(); scene = null; return; }
            if (scene != current.handle)
            {
                Clear();
                scene = current.handle;
                refresh = true;
            }
            var command = pending;
            pending = null; // Never retry a submitted or rejected command.
            if (command != null)
            {
                Spawn(command);
                return;
            }
            if (refresh || pan)
            {
                bool full = refresh;
                refresh = pan = false;
                Scan(full);
            }
        }
        catch (Exception ex)
        {
            Clear();
            status = "Editor unavailable: " + ex.GetBaseException().Message;
            log.LogWarning("BMF level editor: " + ex.GetBaseException());
        }
    }

    private static Type Load(string assembly, string name) => Assembly.Load(new AssemblyName(assembly)).GetType(name, true);
    private static Type ModelType(string name) => Load("Jade.GameModel", "Jade.GameModel." + name);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string method) => DebugWorkspace.Call(obj, method);
    private static MethodInfo Require(Type type, string name, bool isStatic, Type result, params Type[] parameters)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var m = type.GetMethod(name, flags, null, parameters, null);
        if (m == null || m.IsStatic != isStatic || m.IsAbstract || m.ContainsGenericParameters || m.ReturnType != result)
            throw new InvalidOperationException("Exact binding unavailable: " + type.Name + "." + name);
        return m;
    }
    private static ConstructorInfo Constructor(Type type, params Type[] parameters) =>
        type.GetConstructor(parameters) ?? throw new InvalidOperationException("Constructor unavailable: " + type.Name);

    // Resolve all board methods by exact signatures on the verified build.
    private sealed class BoardReader
    {
        internal readonly object Board, Tiles;
        internal readonly Type PointType, BoardType;
        private readonly Type helpers;
        private readonly ConstructorInfo pointCtor;
        private readonly MethodInfo exists, getTile, occupied, walkable, canSpawn;
        internal readonly int MinX, MinY, SizeX, SizeY;
        internal BoardReader(Context context)
        {
            Board = context.Board;
            BoardType = ModelType("Board");
            var tileSetType = ModelType("TileSet");
            var tileType = ModelType("Tile");
            var pieceType = ModelType("Piece");
            PointType = ModelType("IntPoint2D");
            var tileHelpers = ModelType("TileSetHelpers");
            helpers = ModelType("BoardHelpers");
            Tiles = context.TileSet;
            if (!BoardType.IsInstanceOfType(Board) || !tileSetType.IsInstanceOfType(Tiles))
                throw new InvalidOperationException("Unexpected board or tile set.");
            SizeX = (int)Require(tileHelpers, "SizeX", true, typeof(int), tileSetType).Invoke(null, new[] { Tiles });
            SizeY = (int)Require(tileHelpers, "SizeY", true, typeof(int), tileSetType).Invoke(null, new[] { Tiles });
            var min = Require(tileHelpers, "MinCoordinate", true, PointType, tileSetType).Invoke(null, new[] { Tiles });
            MinX = (int)Get(min, "x"); MinY = (int)Get(min, "y");
            // Reject impossible dimensions and overflowing coordinates before reading a tile.
            if (SizeX < 1 || SizeY < 1 || SizeX > 100000 || SizeY > 100000 ||
                (long)MinX + SizeX - 1 > int.MaxValue || (long)MinY + SizeY - 1 > int.MaxValue)
                throw new InvalidOperationException("Board bounds unavailable.");
            pointCtor = Constructor(PointType, typeof(int), typeof(int));
            exists = Require(tileHelpers, "TileExist", true, typeof(bool), tileSetType, typeof(int), typeof(int));
            getTile = Require(tileHelpers, "GetTile", true, tileType, tileSetType, typeof(int), typeof(int));
            occupied = Require(helpers, "TryGetPieceAtPoint", true, typeof(bool), BoardType, PointType, pieceType.MakeByRefType());
            walkable = Require(helpers, "IsWalkable", true, typeof(bool), BoardType, PointType);
            // Only needed for placement; absence must not disable board inspection.
            canSpawn = helpers.GetMethod("CanSpawnPieceOnPosition", BindingFlags.Public | BindingFlags.Static,
                null, new[] { BoardType, PointType, ModelType("PieceId") }, null);
        }
        internal object Point(int x, int y) => pointCtor.Invoke(new object[] { x, y });
        internal TileCell Read(int x, int y)
        {
            if (x < MinX || y < MinY || (long)x >= (long)MinX + SizeX || (long)y >= (long)MinY + SizeY ||
                exists.Invoke(null, new object[] { Tiles, x, y }) is not bool found || !found)
                return new TileCell(x, y, false, false, false, "No tile");
            var tile = getTile.Invoke(null, new object[] { Tiles, x, y });
            var point = Point(x, y);
            var args = new[] { Board, point, (object)null };
            bool hasPiece = (bool)occupied.Invoke(null, args);
            bool canWalk = Call(tile, "get_IsWalkable") is bool tileWalk && tileWalk &&
                walkable.Invoke(null, new[] { Board, point }) is bool boardWalk && boardWalk &&
                Get(tile, "tileIsTemporarilyBlocked") is bool blocked && !blocked;
            string detail = "Height " + Call(tile, "get_Height") + " | " + Get(tile, "flags");
            if (hasPiece) detail += " | Occupied: " + (args[2] == null ? "piece" : Call(Get(args[2], "typeId"), "get_Id"));
            return new TileCell(x, y, true, canWalk, hasPiece, detail);
        }
        internal bool CanSpawnPieceOnPosition(object point) => canSpawn != null && canSpawn.ReturnType == typeof(bool) &&
            canSpawn.Invoke(null, new[] { Board, point, (object)null }) is bool yes && yes;
    }

    private static int ClampStart(long value, int min, int size) =>
        (int)Math.Clamp(value, (long)min, (long)min + Math.Max(0, size - WindowSize));
    private void Scan(bool full)
    {
        selectedTile = null; confirming = false;
        if (!plugin.VerifiedGame) { Clear(); status = "Unverified game build."; return; }
        if (!HouseRules.TryGetLevelEditorContext(out var live, out var reason))
        {
            Clear();
            status = reason;
            return;
        }
        if (snapshot == null || !snapshot.SameSession(live)) full = true;
        var reader = new BoardReader(live);
        minX = reader.MinX; minY = reader.MinY; sizeX = reader.SizeX; sizeY = reader.SizeY;
        startX = ClampStart(full ? minX : (long)startX + panX, minX, sizeX);
        startY = ClampStart(full ? minY : (long)startY + panY, minY, sizeY);
        var result = new TileCell[WindowSize, WindowSize];
        for (int row = 0; row < WindowSize && (long)startY + row < (long)minY + sizeY; row++)
            for (int col = 0; col < WindowSize && (long)startX + col < (long)minX + sizeX; col++)
                result[col, row] = reader.Read(startX + col, startY + row);
        cells = result;
        snapshot = live;
        status = "Board snapshot ready. Select a tile.";
        if (full)
        {
            selectedEnemy = null;
            enemies.Clear(); matches.Clear(); enemyPage = 0;
            try
            {
                if (live.Registry == null) throw new InvalidOperationException("Current HUD piece registry unavailable.");
                var config = ResolveConfig(live);
                ScanEnemies(live, config);
            }
            catch (Exception ex)
            {
                enemies.Clear(); matches.Clear();
                status = "Tiles ready; " + ex.GetBaseException().Message;
                log.LogWarning("BMF level editor enemy registry: " + ex.GetBaseException());
            }
        }
    }

    private static Type ConfigType() => Load("GameTags", "Jade.GameTags.AffectedPiecesConfig");

    // Prefer the exact captured configuration when it matches both the HUD
    // state and registry. Otherwise the game has a ScriptableObject config:
    // resolve it from loaded assets, only if there is exactly one live choice.
    // No asset loading, synthetic tag rules, or selection by asset name.
    private static UObject ResolveConfig(Context context)
    {
        var type = ConfigType();
        if (!typeof(UObject).IsAssignableFrom(type))
            throw new InvalidOperationException("Unexpected affected-pieces config type.");
        if (context.Spawner != null && context.Registry != null)
        {
            try
            {
                var state = Get(context.Spawner, "gameState") as Il2CppObjectBase;
                var registry = Get(context.Spawner, "pieceRegistry") as Il2CppObjectBase;
                if (state != null && state.Pointer == context.State.Pointer &&
                    registry != null && registry.Pointer == context.Registry.Pointer &&
                    Get(context.Spawner, "affectedPiecesConfig") is UObject captured &&
                    captured != null && captured.Pointer != IntPtr.Zero && type.IsInstanceOfType(captured))
                    return captured;
            }
            catch { /* A stale capture never authorizes placement. Use the unique loaded config instead. */ }
        }
        var cast = FindCast(type);
        UObject config = null;
        int inspected = 0;
        var objects = Resources.FindObjectsOfTypeAll(Il2CppType.From(type));
        if (objects != null)
            foreach (var obj in objects)
            {
                if (++inspected > ScanLimit) throw new InvalidOperationException("Affected-pieces config scan capped.");
                if (obj == null) continue;
                if (cast.Invoke(obj, null) is not UObject candidate || candidate == null || candidate.Pointer == IntPtr.Zero ||
                    !type.IsInstanceOfType(candidate))
                    throw new InvalidOperationException("Affected-pieces config cannot be read.");
                if (config != null && config.Pointer != candidate.Pointer)
                    throw new InvalidOperationException("Ambiguous affected-pieces config; placement unavailable.");
                config = candidate;
            }
        if (config == null) throw new InvalidOperationException("No loaded affected-pieces config.");
        return config;
    }

    // Classification uses the current HUD registry and the game's PieceQualities
    // logic, not names, guesses, or the entire loaded asset list.
    private static bool ValidateEnemy(Context context, UObject config, UObject definition, out object id, out object team, out string key)
    {
        id = team = null; key = null;
        if (context.Registry == null || config == null || definition == null) return false;
        var defType = Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition");
        var aiType = Load("GameDefinitions", "Jade.GameDefinitions.PieceAIBehaviourDefinition");
        var idType = ModelType("PieceTypeId");
        var teamType = ModelType("Team");
        var pointType = ModelType("IntPoint2D");
        if (!defType.IsInstanceOfType(definition) || !teamType.IsEnum) return false;
        id = Call(definition, "get_PieceTypeId");
        if (!idType.IsInstanceOfType(id) || Call(id, "get_Id") is not string name || string.IsNullOrWhiteSpace(name) ||
            Call(id, "get_Guid") is not string guid || string.IsNullOrWhiteSpace(guid)) return false;
        key = name + " | " + guid;
        if (Call(definition, "get_PlayerControllable") is not bool player || player ||
            Call(definition, "get_Hp") is not int hp || hp <= 0 ||
            Call(definition, "get_AiBehaviourDefinition") is not Il2CppObjectBase ai ||
            ai.Pointer == IntPtr.Zero || !aiType.IsInstanceOfType(ai)) return false;
        var size = Call(definition, "get_PieceSize");
        if (size == null || !size.GetType().IsEnum || size.GetType().FullName != "Jade.SharedEnums.PieceSize") return false;
        var footprint = Require(ModelType("PieceSizeHelper"), "PieceSizeToTileSize", true, pointType,
            size.GetType(), typeof(float)).Invoke(null, new[] { size, (object)0f });
        if ((int)Get(footprint, "x") != 1 || (int)Get(footprint, "y") != 1) return false;
        var registry = context.Registry;
        if (registry == null || registry.GetType().FullName != "Jade.GameDefinitions.PieceRegistry" ||
            Require(registry.GetType(), "GetPieceDefinition", false, defType, idType).Invoke(registry, new[] { id }) is not UObject registered ||
            registered == null || registered != definition) return false;
        var configType = ConfigType();
        if (!configType.IsInstanceOfType(config)) return false;
        var qualitiesType = Load("GameDefinitions", "Jade.GameDefinitions.PieceQualities");
        var qualities = Constructor(qualitiesType, defType, configType).Invoke(new object[] { definition, config });
        if (Call(qualities, "get_IsNonNpcCreature") is not bool creature || !creature) return false;
        team = Require(qualitiesType, "GetDefaultNonPlayerTeam", false, teamType).Invoke(qualities, null);
        return team != null && team.ToString() == "EnemyOfPlayers";
    }

    private static MethodInfo FindCast(Type type)
    {
        MethodInfo cast = null;
        foreach (var method in typeof(Il2CppObjectBase).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != "TryCast" || !method.IsGenericMethodDefinition ||
                method.GetGenericArguments().Length != 1 || method.GetParameters().Length != 0) continue;
            if (cast != null) throw new InvalidOperationException("Ambiguous definition cast.");
            cast = method.MakeGenericMethod(type);
        }
        if (cast == null) throw new InvalidOperationException("Native checked cast unavailable: " + type.Name);
        return cast;
    }

    private void ScanEnemies(Context context, UObject config)
    {
        var type = Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition");
        if (!typeof(UObject).IsAssignableFrom(type)) throw new InvalidOperationException("Invalid definition type.");
        var cast = FindCast(type);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var objects = Resources.FindObjectsOfTypeAll(Il2CppType.From(type));
        int inspected = 0, skipped = 0;
        if (objects != null)
            foreach (var obj in objects)
            {
                if (++inspected > ScanLimit) break;
                if (obj == null) continue;
                try
                {
                    var def = cast.Invoke(obj, null) as UObject;
                    if (ValidateEnemy(context, config, def, out _, out _, out string key) && seen.Add(key))
                        enemies.Add(new Enemy(def, def.name ?? key, key));
                }
                catch (Exception ex)
                {
                    if (++skipped == 1) log.LogWarning("BMF level editor skipped enemy definition: " + ex.GetBaseException().Message);
                }
            }
        enemies.Sort(CompareEnemies);
        ApplyFilter();
        status = enemies.Count + " eligible enemies / " + Math.Min(inspected, ScanLimit) + " scanned" +
            (inspected > ScanLimit ? " (capped)" : "") + (skipped > 0 ? " | " + skipped + " unreadable" : "") + ".";
        log.LogInfo("BMF level editor: " + status + " Source: current HUD registry and loaded native classification config.");
    }
    private static int CompareEnemies(Enemy a, Enemy b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    private void ApplyFilter()
    {
        matches.Clear();
        foreach (var enemy in enemies)
            if (enemy.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || enemy.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                matches.Add(enemy);
        enemyPage = 0;
    }

    // IL2CPP value types may appear as managed wrappers; construct an actual
    // default value, never pass a null wrapper in place of a native struct.
    private static object NativeDefault(Type type)
    {
        if (type.IsValueType) return Activator.CreateInstance(type);
        for (var t = type; t != null; t = t.BaseType)
            if (t.FullName == "Il2CppSystem.ValueType")
                return Constructor(type).Invoke(null);
        return null;
    }

    private static object MakeSpawnData(object id, object point, object team)
    {
        var type = ModelType("SpawnData");
        var player = ModelType("PlayerId");
        string[] names = { "pieceTypeId", "position", "orientation", "owner", "team", "isPlayerControlled",
            "pieceToReplace", "customPieceId", "summonerId", "poseState", "isPOI", "poiMarkerPosition",
            "characterSheetId", "awakeAI", "statusEffectTypesWithDuration", "forceFollowPlayersAiBehaviour",
            "isTransformedPiece", "modifiedStats", "spawnAnimation", "lootTableOverride", "lootChanceOverride",
            "customBaseplateId", "isElite", "additionCardSpawnData" };
        ConstructorInfo ctor = null;
        foreach (var candidate in type.GetConstructors())
        {
            var p = candidate.GetParameters();
            if (p.Length != names.Length || p[0].ParameterType != ModelType("PieceTypeId") ||
                p[1].ParameterType != ModelType("IntPoint2D") || p[2].ParameterType != typeof(float) ||
                p[3].ParameterType != player || p[4].ParameterType != ModelType("Team") || p[5].ParameterType != typeof(bool)) continue;
            bool exact = true;
            for (int i = 0; i < names.Length; i++) exact &= p[i].Name == names[i];
            if (!exact) continue;
            foreach (int i in new[] { 10, 13, 15, 16, 22 }) exact &= p[i].ParameterType == typeof(bool);
            if (!exact) continue;
            if (ctor != null) throw new InvalidOperationException("Ambiguous spawn data constructor.");
            ctor = candidate;
        }
        if (ctor == null) throw new InvalidOperationException("Verified spawn data signature unavailable.");
        var parameters = ctor.GetParameters();
        var args = new object[names.Length];
        args[0] = id; args[1] = point; args[2] = 0f;
        args[3] = Require(player, "get_None", true, player).Invoke(null, null);
        if (args[3] == null) throw new InvalidOperationException("No native unowned player ID.");
        args[4] = team; args[5] = false;
        // The supplied build's native optional values are default(T)/false.
        for (int i = 6; i < args.Length; i++) args[i] = NativeDefault(parameters[i].ParameterType);
        return ctor.Invoke(args);
    }

    private void Spawn(SpawnCommand command)
    {
        if (!plugin.VerifiedGame) throw new InvalidOperationException("Spawn rejected: unverified build.");
        if (!HouseRules.TryGetLevelEditorContext(out var live, out var reason))
            throw new InvalidOperationException("Spawn rejected: " + reason);
        if (!command.Snapshot.SameSession(live)) throw new InvalidOperationException("Session changed. Refresh.");
        var board = new BoardReader(live);
        var tile = board.Read(command.Tile.X, command.Tile.Y);
        var point = board.Point(tile.X, tile.Y);
        if (!tile.Available || !board.CanSpawnPieceOnPosition(point))
            throw new InvalidOperationException("Tile no longer available for spawn.");
        var config = ResolveConfig(live);
        if (!ValidateEnemy(live, config, command.Enemy.Definition, out var id, out var team, out string key) || key != command.Enemy.Key)
            throw new InvalidOperationException("Enemy definition is no longer eligible.");
        var roomType = Load("Jade.Networking", "Jade.Networking.LocalNetworkRoom");
        var requestType = Load("Jade.Messaging", "Jade.Messaging.IRequest");
        var messageType = Load("Jade.Messaging", "Jade.Messaging.RequestSpawnPiece");
        if (!roomType.IsInstanceOfType(live.Room) || !requestType.IsAssignableFrom(messageType))
            throw new InvalidOperationException("Native local spawn request unavailable.");
        var send = Require(roomType, "Send", false, typeof(void), requestType);
        var data = MakeSpawnData(id, point, team);
        var request = Constructor(messageType, ModelType("SpawnData")).Invoke(new[] { data });
        if (Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy)
            throw new InvalidOperationException("Local room busy; refresh and retry manually.");
        // Recheck the live scope immediately before dispatch. Do not call the
        // spawner directly or bypass the normal request handler.
        if (!HouseRules.TryGetLevelEditorContext(out var final, out reason) || !live.SameSession(final))
            throw new InvalidOperationException("Session changed before dispatch: " + reason);
        send.Invoke(live.Room, new[] { request });
        log.LogInfo("BMF level editor enemy request submitted: " + command.Enemy.Name + " at " + tile.X + "," + tile.Y);
        selectedTile = null; selectedEnemy = null; confirming = false;
        cells = new TileCell[WindowSize, WindowSize]; snapshot = null;
        status = "Enemy request submitted. Check the game, then Refresh.";
    }

    internal void Draw(float x, float y, float width, float height)
    {
        try
        {
            float inner = width - 30, left = inner * 0.48f, right = inner - left - 10, rx = x + 25 + left;
            float cell = Math.Min(31f, Math.Min((left - 4) / WindowSize, (height - 266) / WindowSize));
            void Label(float top, string value) => GUI.Label(new Rect(x + 15, y + top, inner, 22), value);
            bool Button(float top, float l, float w, string value) => GUI.Button(new Rect(x + l, y + top, w, 24), value);
            Label(91, "Runtime level editor | Disposable local solo only; back up saves.");
            if (Button(115, 15, left, "Refresh board & enemies"))
            { refresh = true; pan = confirming = false; pending = null; }
            if (snapshot == null)
            {
                Label(145, status);
                Label(171, "House rules must be running; start a new local solo campaign.");
                return;
            }
            float bw = left / 4;
            if (Button(141, 15, bw - 2, "X-")) QueuePan(-WindowSize, 0);
            if (Button(141, 15 + bw, bw - 2, "X+")) QueuePan(WindowSize, 0);
            if (Button(141, 15 + bw * 2, bw - 2, "Y-")) QueuePan(0, -WindowSize);
            if (Button(141, 15 + bw * 3, bw - 2, "Y+")) QueuePan(0, WindowSize);
            for (int row = 0; row < WindowSize; row++)
                for (int col = 0; col < WindowSize; col++)
                {
                    var tile = cells[col, row];
                    if (tile == null) continue;
                    if (GUI.Button(new Rect(x + 15 + col * cell, y + 168 + (WindowSize - 1 - row) * cell,
                        cell - 1, cell - 1), ReferenceEquals(tile, selectedTile) ? "*" : tile.Symbol) && tile.Exists)
                    { selectedTile = tile; confirming = false; pending = null; }
                }
            GUI.Label(new Rect(rx, y + 115, right, 24), "Enemies (registered, 1x1)");
            var typed = GUI.TextField(new Rect(rx, y + 141, right, 24), filter, 100);
            if (typed != filter) { filter = typed; ApplyFilter(); confirming = false; selectedEnemy = null; pending = null; }
            float footer = height - 96;
            int rows = Math.Max(1, (int)((footer - 193) / 24));
            int pages = Math.Max(1, (matches.Count + rows - 1) / rows);
            enemyPage = Math.Clamp(enemyPage, 0, pages - 1);
            for (int i = 0; i < rows && enemyPage * rows + i < matches.Count; i++)
            {
                var enemy = matches[enemyPage * rows + i];
                if (GUI.Button(new Rect(rx, y + 167 + i * 24, right, 23), (ReferenceEquals(selectedEnemy, enemy) ? "* " : "") + enemy.Name))
                { selectedEnemy = enemy; confirming = false; pending = null; }
            }
            if (GUI.Button(new Rect(rx, y + footer - 25, 34, 24), "<")) enemyPage = Math.Max(0, enemyPage - 1);
            GUI.Label(new Rect(rx + 37, y + footer - 25, right - 75, 23), (enemyPage + 1) + "/" + pages + " | " + matches.Count);
            if (GUI.Button(new Rect(rx + right - 34, y + footer - 25, 34, 24), ">")) enemyPage = Math.Min(pages - 1, enemyPage + 1);
            Label(footer - 2, selectedTile == null ? "Board " + sizeX + "x" + sizeY + " | origin (" + startX + "," + startY + ") | . free # blocked P piece" :
                "(" + selectedTile.X + "," + selectedTile.Y + ") " + selectedTile.Detail);
            Label(footer + 20, "Enemy: " + (selectedEnemy?.Name ?? "select from list"));
            if (selectedTile?.Available == true && selectedEnemy != null && !refresh && !pan && pending == null)
            {
                if (Button(footer + 42, 15, inner, confirming ? "Confirm enemy spawn (local solo)" : "Place selected enemy"))
                {
                    if (confirming) { pending = new SpawnCommand(snapshot, selectedTile, selectedEnemy); confirming = false; }
                    else confirming = true;
                }
            }
            else Label(footer + 42, "Select a free tile and eligible enemy.");
            Label(height - 27, status);
        }
        catch (Exception ex)
        {
            Clear();
            if (!guiErrorLogged) log.LogWarning("BMF level editor GUI: " + ex.GetBaseException().Message);
            guiErrorLogged = true;
            GUI.Label(new Rect(x + 15, y + 118, width - 30, 24), "Editor interface unavailable; see BepInEx log.");
        }
    }

    private void QueuePan(int dx, int dy)
    {
        panX = dx; panY = dy; pan = true; confirming = false; pending = null;
    }
}
