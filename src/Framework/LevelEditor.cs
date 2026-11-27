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
    private readonly List<Enemy> forms = new();
    private bool showForms;
    private SpawnCommand pendingForm;
    private RemovalTarget heroTarget;
    private TileCell[,] cells = new TileCell[WindowSize, WindowSize];
    private int minX, minY, sizeX, sizeY, startX, startY, enemyPage;
    private int panX, panY;
    private bool refresh, pan, confirming, guiErrorLogged;
    private string filter = "", status = "Refresh in a disposable local solo campaign.";
    private TileCell selectedTile;
    private Enemy selectedEnemy;
    private SpawnCommand pending;
    private RemovalCommand pendingRemoval;
    private RemovalTarget removalTarget;
    private RemovalTarget moveSource;
    private MoveCommand pendingMove;
    // 0 = full editor, 1 = enemy placement, 2 = piece removal, 3 = hero form, 4 = move enemy.
    private int mapMode;
    private bool mapPickRequested;
    private float mapClickX, mapClickY;

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

    private sealed class RemovalTarget
    {
        internal readonly Il2CppObjectBase Piece;
        internal readonly int Id;
        internal readonly string TypeKey;
        internal readonly TileCell Tile;
        internal RemovalTarget(Il2CppObjectBase piece, int id, string typeKey, TileCell tile)
        { Piece = piece; Id = id; TypeKey = typeKey; Tile = tile; }
        internal bool SamePiece(RemovalTarget other) => other != null && Id == other.Id &&
            TypeKey == other.TypeKey && Piece.Pointer != IntPtr.Zero && Piece.Pointer == other.Piece.Pointer;
    }
    private sealed class RemovalCommand
    {
        internal readonly Context Snapshot;
        internal readonly RemovalTarget Target;
        internal RemovalCommand(Context snapshot, RemovalTarget target) { Snapshot = snapshot; Target = target; }
    }

    private sealed class MoveCommand
    {
        internal readonly Context Snapshot;
        internal readonly RemovalTarget Source;
        internal readonly TileCell Destination;
        internal MoveCommand(Context snapshot, RemovalTarget source, TileCell destination)
        { Snapshot = snapshot; Source = source; Destination = destination; }
    }

    internal LevelEditor(Plugin plugin, ManualLogSource log) { this.plugin = plugin; this.log = log; }

    private void Clear()
    {
        snapshot = null;
        cells = new TileCell[WindowSize, WindowSize];
        selectedTile = null; selectedEnemy = null; pending = null;
        pendingRemoval = null; removalTarget = null; mapMode = 0; mapPickRequested = false;
        enemies.Clear(); matches.Clear(); forms.Clear();
        pendingForm = null; heroTarget = null; HeroForms.Cancel();
        pendingMove = null; moveSource = null; EnemyMover.Cancel();
        confirming = refresh = pan = false;
        panX = panY = enemyPage = 0;
        filter = "";
        status = "Refresh in a disposable local solo campaign.";
    }

    internal void Leave() { Clear(); scene = null; }

    // Both pages share exactly the same verified session and map picker, but
    // never carry target/selection/queued state from one page into the other.
    private void SelectView(bool heroView)
    {
        if (showForms == heroView) return;
        Leave();
        showForms = heroView;
    }

    internal void Tick(bool enabled, bool heroView = false)
    {
        if (!enabled)
        {
            if (scene.HasValue) Leave();
            return;
        }
        SelectView(heroView);
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
            var remove = pendingRemoval;
            var form = pendingForm;
            var move = pendingMove;
            pendingMove = null;
            pendingForm = null;
            pending = null; // Never retry a submitted or rejected command.
            pendingRemoval = null;
            if (command != null) { Spawn(command); return; }
            if (remove != null) { Remove(remove); return; }
            if (form != null) { TransformHero(form); return; }
            if (move != null) { Move(move); return; }
            if (mapMode != 0)
            {
                if (mapPickRequested)
                {
                    mapPickRequested = false;
                    PickMapTile();
                }
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
        internal Il2CppObjectBase PieceAt(object point)
        {
            var args = new[] { Board, point, (object)null };
            if (occupied.Invoke(null, args) is not bool found || !found) return null;
            return args[2] as Il2CppObjectBase;
        }
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
        Context live;
        string reason;
        if (!(showForms ? HouseRules.TryGetHeroFormsContext(out live, out reason) : HouseRules.TryGetLevelEditorContext(out live, out reason)))
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
            enemies.Clear(); matches.Clear(); forms.Clear(); enemyPage = 0;
            try
            {
                if (live.Registry == null) throw new InvalidOperationException("Current HUD piece registry unavailable.");
                var config = ResolveConfig(live);
                ScanEnemies(live, config);
            }
            catch (Exception ex)
            {
                enemies.Clear(); matches.Clear(); forms.Clear();
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

    private static bool ValidateFormDefinition(Context context, UObject config, UObject definition, out object id, out string key, out string formSize)
    {
        id = null; key = formSize = null;
        if (context.Registry == null || config == null || definition == null) return false;
        var defType = Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition");
        var idType = ModelType("PieceTypeId");
        if (!defType.IsInstanceOfType(definition)) return false;
        id = Call(definition, "get_PieceTypeId");
        if (!idType.IsInstanceOfType(id) || Call(id, "get_Id") is not string name || string.IsNullOrWhiteSpace(name) ||
            Call(id, "get_Guid") is not string guid || string.IsNullOrWhiteSpace(guid)) return false;
        key = name + " | " + guid;
        if (Call(definition, "get_Hp") is not int hp || hp <= 0) return false;
        var footprint = HeroForms.FormFootprint(definition, 0f);
        formSize = Get(footprint, "x") + "x" + Get(footprint, "y");
        if (Require(context.Registry.GetType(), "GetPieceDefinition", false, defType, idType)
            .Invoke(context.Registry, new[] { id }) is not UObject registered || registered == null || registered != definition) return false;
        var qualitiesType = Load("GameDefinitions", "Jade.GameDefinitions.PieceQualities");
        var qualities = Constructor(qualitiesType, defType, ConfigType()).Invoke(new object[] { definition, config });
        return Call(qualities, "get_IsNonNpcCreature") is bool creature && creature &&
            Call(qualities, "get_UsesHealth") is bool health && health;
    }

    internal static MethodInfo FindCast(Type type)
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
        var seenForms = new HashSet<string>(StringComparer.Ordinal);
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
                    if (!showForms && ValidateEnemy(context, config, def, out _, out _, out string key) && seen.Add(key))
                        enemies.Add(new Enemy(def, def.name ?? key, key));
                    if (showForms && plugin.HeroFormsEnabled && ValidateFormDefinition(context, config, def, out _, out string formKey, out string formSize) && seenForms.Add(formKey))
                        forms.Add(new Enemy(def, (def.name ?? formKey) + " [" + formSize + "]", formKey));
                }
                catch (Exception ex)
                {
                    if (++skipped == 1) log.LogWarning("BMF level editor skipped enemy definition: " + ex.GetBaseException().Message);
                }
            }
        enemies.Sort(CompareEnemies);
        forms.Sort(CompareEnemies);
        ApplyFilter();
        status = (showForms ? forms.Count + " eligible hero forms / " : enemies.Count + " eligible enemies / ") + Math.Min(inspected, ScanLimit) + " scanned" +
            (inspected > ScanLimit ? " (capped)" : "") + (skipped > 0 ? " | " + skipped + " unreadable" : "") + ".";
        log.LogInfo("BMF level editor: " + status + " Source: current HUD registry and loaded native classification config.");
    }
    private static int CompareEnemies(Enemy a, Enemy b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    private void ApplyFilter()
    {
        matches.Clear();
        foreach (var enemy in (showForms ? forms : enemies))
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

    // Generated interop classes can implement a native IL2CPP interface
    // without implementing the corresponding CLR wrapper type. Validate with
    // the same native checked cast used for current room and asset proof.
    internal static object CheckedRequest(object message, Type messageType, Type requestType)
    {
        if (!messageType.IsInstanceOfType(message) || message is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
            throw new InvalidOperationException("Native request construction failed: " + messageType.Name);
        var cast = FindCast(requestType).Invoke(native, null) as Il2CppObjectBase;
        if (cast == null || cast.Pointer != native.Pointer || !requestType.IsInstanceOfType(cast))
            throw new InvalidOperationException("Native IRequest cast failed: " + messageType.Name);
        return cast;
    }

    private void Spawn(SpawnCommand command)
    {
        if (!plugin.VerifiedGame) throw new InvalidOperationException("Spawn rejected: unverified build.");
        if (!HouseRules.TryGetLevelEditorContext(out var live, out var reason))
            throw new InvalidOperationException("Spawn rejected: " + reason);
        if (!command.Snapshot.SameSession(live)) throw new InvalidOperationException("Session changed. Refresh.");
        if (mapMode == 1)
        {
            if (!HouseRules.TryGetLevelEditorPicker(live, out var handler, out reason))
                throw new InvalidOperationException(reason);
            RequireMapIdle(handler);
        }
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
        if (!roomType.IsInstanceOfType(live.Room))
            throw new InvalidOperationException("Native local spawn room type unavailable.");
        var send = Require(roomType, "Send", false, typeof(void), requestType);
        var data = MakeSpawnData(id, point, team);
        var message = Constructor(messageType, ModelType("SpawnData")).Invoke(new[] { data });
        var request = CheckedRequest(message, messageType, requestType);
        if (Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy)
            throw new InvalidOperationException("Local room busy; refresh and retry manually.");
        // Recheck the live scope immediately before dispatch. Do not call the
        // spawner directly or bypass the normal request handler.
        if (!HouseRules.TryGetLevelEditorContext(out var final, out reason) || !live.SameSession(final))
            throw new InvalidOperationException("Session changed before dispatch: " + reason);
        send.Invoke(live.Room, new[] { request });
        log.LogInfo("BMF level editor enemy request submitted: " + command.Enemy.Name + " at " + tile.X + "," + tile.Y);
        selectedTile = null; confirming = false;
        if (mapMode == 1)
        {
            snapshot = live;
            status = "Enemy request submitted. Check the game; middle-click another tile.";
        }
        else
        {
            selectedEnemy = null;
            cells = new TileCell[WindowSize, WindowSize]; snapshot = null;
            status = "Enemy request submitted. Check the game, then Refresh.";
        }
    }

    private static void RequireMapIdle(object handler)
    {
        var signal = Get(handler, "requestProcessingSignalReader");
        if (Call(signal, "get_IsProcessingRequests") is not bool processing || processing)
            throw new InvalidOperationException("Game is processing an action; wait and try again.");
        if (Call(handler, "get_IsGrabbingWithPointer") is not bool grabbing || grabbing)
            throw new InvalidOperationException("Release the currently grabbed piece first.");
    }

    private void BeginMapTool(int mode)
    {
        if (snapshot == null || ((mode == 1 || mode == 3) && selectedEnemy == null)) return;
        if (mode == 3 && (!showForms || !plugin.HeroFormsEnabled)) return;
        if (mode == 4 && (showForms || !EnemyMover.Ready)) { status = EnemyMover.Status; return; }
        pendingMove = null; moveSource = null; EnemyMover.Cancel();
        mapMode = mode;
        selectedTile = null; removalTarget = null; confirming = false;
        pending = null; pendingRemoval = null; pendingForm = null; heroTarget = null; mapPickRequested = false; refresh = pan = false;
        status = mode == 1 ? "Middle-click a free tile on the 3D board." : mode == 3 ? "Middle-click any eligible hero in your party." : mode == 4 ? "Middle-click a living enemy to move." : "Middle-click an enemy or destructible prop.";
    }

    private void EndMapTool()
    {
        mapMode = 0; mapPickRequested = false;
        pending = null; pendingRemoval = null; removalTarget = null; pendingForm = null; heroTarget = null; HeroForms.Cancel();
        pendingMove = null; moveSource = null; EnemyMover.Cancel();
        selectedTile = null; confirming = false;
        refresh = true; pan = false;
    }

    // Pick using the currently ticking game input handler and actual active
    // game camera. Coordinates are obtained by the native board hit-test, not
    // guessed from a horizontal world plane (which fails on elevated tiles).
    private void PickMapTile()
    {
        selectedTile = null;
        removalTarget = null; heroTarget = null;
        try
        {
            bool heroForm = mapMode == 3;
            if (heroForm != showForms) throw new InvalidOperationException("Map tool changed; select again.");
            Context live;
            string reason;
            if (!(heroForm ? HouseRules.TryGetHeroFormsContext(out live, out reason) : HouseRules.TryGetLevelEditorContext(out live, out reason)))
                throw new InvalidOperationException(reason);
            if (snapshot == null || !snapshot.SameSession(live))
                throw new InvalidOperationException("Campaign changed; return to editor and refresh.");
            object handler;
            if (!(heroForm ? HouseRules.TryGetHeroFormsPicker(live, out handler, out reason) :
                HouseRules.TryGetLevelEditorPicker(live, out handler, out reason)))
                throw new InvalidOperationException(reason);
            RequireMapIdle(handler);
            var cameraRoot = Call(Get(handler, "cameraControlModel"), "get_ActiveCamera");
            if (Call(cameraRoot, "get_Camera") is not Camera camera || camera == null || !camera.isActiveAndEnabled)
                throw new InvalidOperationException("Active game camera unavailable.");
            var pixel = new Vector2(mapClickX, Screen.height - mapClickY);
            if (!camera.pixelRect.Contains(pixel))
                throw new InvalidOperationException("Click inside the active game viewport.");
            var ray = camera.ScreenPointToRay(new Vector3(pixel.x, pixel.y, 0f));
            var hover = ModelType("TileSetHelpers+TileHoverData");
            var hit = handler.GetType().GetMethod("TryGetHoveredTile",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(Ray), hover.MakeByRefType() }, null);
            if (hit == null || hit.ReturnType != typeof(bool) || hit.IsAbstract || hit.ContainsGenericParameters)
                throw new InvalidOperationException("Exact native board hit-test unavailable.");
            var args = new object[] { ray, null };
            if (hit.Invoke(handler, args) is not bool found || !found || args[1] == null ||
                Call(args[1], "get_IsValid") is not bool valid || !valid)
                throw new InvalidOperationException("No board tile hit. Middle-click a visible tile.");
            var point = Get(args[1], "tilePoint");
            var reader = new BoardReader(live);
            var tile = reader.Read((int)Get(point, "x"), (int)Get(point, "y"));
            if (!tile.Exists) throw new InvalidOperationException("No live tile at that point.");
            if (mapMode == 1)
            {
                if (!tile.Available || !reader.CanSpawnPieceOnPosition(reader.Point(tile.X, tile.Y)))
                    throw new InvalidOperationException("That tile is occupied or blocked.");
                selectedTile = tile;
                status = "Selected (" + tile.X + "," + tile.Y + "). Confirm placement.";
            }
            else if (mapMode == 2)
            {
                removalTarget = ValidateRemovalTarget(reader, tile);
                selectedTile = tile;
                status = "Selected " + removalTarget.TypeKey + ". Confirm removal.";
            }
            else if (mapMode == 3)
            {
                heroTarget = ValidateHeroTarget(live, reader, tile);
                if (selectedEnemy == null) throw new InvalidOperationException("Select a creature form first.");
                HeroForms.ValidateFormFit(live.Board, heroTarget.Piece, selectedEnemy.Definition);
                selectedTile = tile;
                status = "Selected " + heroTarget.TypeKey + ". Confirm hero form.";
            }
            else if (mapMode == 4)
            {
                if (moveSource == null)
                {
                    moveSource = ValidateMoveTarget(reader, tile);
                    status = "Enemy selected. Middle-click its destination.";
                }
                else
                {
                    var source = ValidateMoveTarget(reader, reader.Read(moveSource.Tile.X, moveSource.Tile.Y));
                    if (!moveSource.SamePiece(source))
                    {
                        moveSource = null;
                        throw new InvalidOperationException("Enemy changed; select it again.");
                    }
                    if (!tile.Available || !reader.CanSpawnPieceOnPosition(reader.Point(tile.X, tile.Y)))
                        throw new InvalidOperationException("Destination must be a free, walkable tile.");
                    selectedTile = tile;
                    status = "Destination selected. Confirm move, or choose another tile.";
                }
            }
        }
        catch (Exception ex)
        {
            selectedTile = null; removalTarget = null; heroTarget = null;
            status = ex.GetBaseException().Message;
            log.LogInfo("BMF level editor map pick: " + status);
        }
    }

    private static RemovalTarget ValidateHeroTarget(Context live, BoardReader reader, TileCell tile)
    {
        if (!tile.Exists || !tile.Occupied) throw new InvalidOperationException("Select an occupied hero tile.");
        var piece = reader.PieceAt(reader.Point(tile.X, tile.Y));
        HeroForms.ValidatePiece(live.State, live.Board, piece);
        if (!HouseRules.TryValidateLocalHero(live, piece, out var reason)) throw new InvalidOperationException(reason);
        var id = (int)Call(Get(piece, "pieceId"), "get_Id");
        var key = (string)Call(Get(piece, "typeId"), "get_Id");
        return new RemovalTarget(piece, id, key, tile);
    }

    private void TransformHero(SpawnCommand command)
    {
        try
        {
            if (!showForms || !plugin.HeroFormsEnabled) throw new InvalidOperationException("Hero Forms disabled or tab changed.");
            if (heroTarget == null || !HouseRules.TryGetHeroFormsContext(out var live, out _) || !command.Snapshot.SameSession(live))
                throw new InvalidOperationException("Hero form target or session changed.");
            var reader = new BoardReader(live);
            var hero = ValidateHeroTarget(live, reader, reader.Read(command.Tile.X, command.Tile.Y));
            if (!heroTarget.SamePiece(hero)) throw new InvalidOperationException("Hero moved or changed; select again.");
            if (!HouseRules.TryGetHeroFormsPicker(live, out var handler, out var reason)) throw new InvalidOperationException(reason);
            RequireMapIdle(handler);
            if (!ValidateFormDefinition(live, ResolveConfig(live), command.Enemy.Definition, out var id, out var key, out _) || key != command.Enemy.Key)
                throw new InvalidOperationException("Creature definition no longer registered.");
            status = HeroForms.Queue(live, hero.Piece, command.Enemy.Definition, id);
        }
        catch (Exception ex)
        {
            status = ex.GetBaseException().Message;
            log.LogWarning("BMF hero forms: " + status);
        }
        finally { heroTarget = null; selectedTile = null; }
    }

    // Removal means the native damage/destruction process for an eligible
    // hostile creature or attackable prop. Never destroy Unity scene objects,
    // directly edit the board, down heroes, remove NPCs or touch POI pieces.
    private static RemovalTarget ValidateRemovalTarget(BoardReader board, TileCell tile)
    {
        if (!tile.Exists || !tile.Occupied) throw new InvalidOperationException("No occupied piece at this tile.");
        var piece = board.PieceAt(board.Point(tile.X, tile.Y));
        if (piece == null || piece.Pointer == IntPtr.Zero || piece.GetType().FullName != "Jade.GameModel.Piece")
            throw new InvalidOperationException("Piece identity unavailable.");
        if (Get(piece, "isPlayerControlled") is not bool controlled || controlled ||
            Get(piece, "isDownable") is not bool downable || downable ||
            Get(piece, "isPOI") is not bool poi || poi ||
            Get(piece, "usesHealth") is not bool usesHealth || !usesHealth ||
            Get(piece, "isCreatureNonNpc") is not bool creature ||
            Get(piece, "isAttackableProp") is not bool prop)
            throw new InvalidOperationException("Protected or non-destructible piece.");
        var team = Get(piece, "team")?.ToString();
        if (!(creature && team == "EnemyOfPlayers") && !(prop && !creature && (team == "None" || team == "EnemyOfPlayers")))
            throw new InvalidOperationException("Only hostile creatures and destructible non-player props can be removed.");
        var pieceType = ModelType("Piece");
        if (Require(ModelType("PieceHelpers"), "IsDead", true, typeof(bool), pieceType)
            .Invoke(null, new object[] { piece }) is not bool dead || dead)
            throw new InvalidOperationException("Piece is already dead or unavailable.");
        var pieceId = Get(piece, "pieceId");
        if (!ModelType("PieceId").IsInstanceOfType(pieceId) || Call(pieceId, "get_Id") is not int id || id < 0)
            throw new InvalidOperationException("Piece ID unavailable.");
        var typeId = Get(piece, "typeId");
        if (Call(typeId, "get_Id") is not string key || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Piece type unavailable.");
        return new RemovalTarget(piece, id, key, tile);
    }

    // Read the registered physical damage type from the live board's native
    // audio/visual manager; both definitions registries originate in the
    // current board presenter. No fabricated damage IDs or arbitrary assets.
    private static object PhysicalDamageId(Context context, object handler)
    {
        var presenter = Get(handler, "boardPresenter");
        var state = Get(presenter, "gameState") as Il2CppObjectBase;
        if (state == null || state.Pointer != context.State.Pointer) throw new InvalidOperationException("Board presenter changed.");
        var manager = Get(presenter, "audioHapticVisualManager");
        var pieces = Get(manager, "pieceRegistry") as Il2CppObjectBase;
        if (context.Registry == null || pieces == null || pieces.Pointer != context.Registry.Pointer)
            throw new InvalidOperationException("Current damage registry identity not verified.");
        var registry = Get(manager, "damageTypeRegistry");
        var registryType = Load("GameDefinitions", "Jade.GameDefinitions.DamageTypeRegistry");
        var idType = ModelType("DamageTypeId");
        var defType = Load("GameDefinitions", "Jade.GameDefinitions.DamageTypeDefinition");
        if (!registryType.IsInstanceOfType(registry)) throw new InvalidOperationException("Current damage registry unavailable.");
        // The supplied build's DamageTypeId.PHYSICAL_DAMAGE_TYPE_NAME
        // metadata constant is "Physical".
        var args = new object[] { "Physical", null };
        if (Require(registryType, "TryGetDamageTypeIdByName", false, typeof(bool), typeof(string), idType.MakeByRefType())
            .Invoke(registry, args) is not bool found || !found || !idType.IsInstanceOfType(args[1]) ||
            Call(args[1], "get_Id") is not string name || name != "Physical" ||
            Require(registryType, "GetDamageTypeDefinition", false, defType, idType).Invoke(registry, new[] { args[1] }) is not UObject def ||
            def == null)
            throw new InvalidOperationException("Registered physical damage type unavailable.");
        return args[1];
    }

    private static object MakeRemovalRequest(RemovalTarget target, object damageId)
    {
        var stats = Get(target.Piece, "stats");
        var statsType = ModelType("StatsFacade");
        var statType = ModelType("Stats+Type");
        if (!statsType.IsInstanceOfType(stats) || !statType.IsEnum)
            throw new InvalidOperationException("Native piece stats unavailable.");
        var get = Require(statsType, "GetStat", false, typeof(int), statType);
        int hp = (int)get.Invoke(stats, new[] { Enum.Parse(statType, "Health") });
        // Not every native piece defines PhysicalArmor. GetStat throws for
        // those pieces; the verified TryGetStat(Type, out int, int) overload
        // explicitly provides a default value when that stat is absent.
        var tryGet = Require(statsType, "TryGetStat", false, typeof(bool), statType, typeof(int).MakeByRefType(), typeof(int));
        var armorArgs = new object[] { Enum.Parse(statType, "PhysicalArmor"), 0, 0 };
        if (tryGet.Invoke(stats, armorArgs) is not bool armorFound || armorArgs[1] is not int armor ||
            (!armorFound && armor != 0))
            throw new InvalidOperationException("Physical armour read failed.");
        if (hp <= 0 || hp > 10000 || armor < 0 || armor > 10000)
            throw new InvalidOperationException("Piece health or armor outside supported removal range.");
        int damage = checked(hp + armor + 1);
        var elementType = Load("Jade.Messaging", "Jade.Messaging.DamageTypeWithProperties");
        var damageIdType = ModelType("DamageTypeId");
        ConstructorInfo ctor = null;
        foreach (var c in elementType.GetConstructors())
        {
            var p = c.GetParameters();
            if (p.Length != 3 || p[0].ParameterType != typeof(int) || p[1].ParameterType != damageIdType ||
                p[0].Name != "damage" || p[1].Name != "typeId" || p[2].Name != "additionalTags" ||
                p[2].ParameterType.IsValueType || p[2].ParameterType.IsByRef) continue;
            if (ctor != null) throw new InvalidOperationException("Ambiguous damage element constructor.");
            ctor = c;
        }
        if (ctor == null) throw new InvalidOperationException("Damage element constructor unavailable.");
        // Verified native constructor explicitly replaces a null additionalTags
        // reference with its own empty tags collection (GameAssembly 0x1822a8ed0).
        var element = ctor.Invoke(new[] { (object)damage, damageId, (object)null });
        var requestType = Load("Jade.Messaging", "Jade.Messaging.RequestTryDealDamageToPiece");
        return Constructor(requestType, elementType, ModelType("PieceId")).Invoke(new[] { element, Get(target.Piece, "pieceId") });
    }

    private void Move(MoveCommand command)
    {
        try
        {
            if (mapMode != 4 || showForms || !plugin.VerifiedGame || !plugin.DeveloperModeEnabled)
                throw new InvalidOperationException("Move tool no longer enabled.");
            if (!HouseRules.TryGetLevelEditorContext(out var live, out var reason) || !command.Snapshot.SameSession(live))
                throw new InvalidOperationException("Move rejected: " + reason);
            if (!HouseRules.TryGetLevelEditorPicker(live, out var handler, out reason))
                throw new InvalidOperationException(reason);
            RequireMapIdle(handler);
            var reader = new BoardReader(live);
            var source = ValidateMoveTarget(reader, reader.Read(command.Source.Tile.X, command.Source.Tile.Y));
            if (!command.Source.SamePiece(source)) throw new InvalidOperationException("Enemy moved or changed; select it again.");
            var destination = reader.Read(command.Destination.X, command.Destination.Y);
            if (!destination.Available || !reader.CanSpawnPieceOnPosition(reader.Point(destination.X, destination.Y)))
                throw new InvalidOperationException("Destination no longer free or walkable.");
            status = EnemyMover.Queue(live, source.Piece, source.Tile.X, source.Tile.Y, destination.X, destination.Y);
            snapshot = live;
        }
        catch (Exception ex)
        {
            status = ex.GetBaseException().Message;
            log.LogWarning("BMF enemy move: " + status);
        }
        finally { moveSource = null; selectedTile = null; }
    }

    private static RemovalTarget ValidateMoveTarget(BoardReader reader, TileCell tile)
    {
        if (!tile.Exists || !tile.Occupied) throw new InvalidOperationException("Middle-click a living enemy.");
        var piece = reader.PieceAt(reader.Point(tile.X, tile.Y));
        EnemyMover.ValidateEnemy(piece, tile.X, tile.Y);
        var id = (int)Call(Get(piece, "pieceId"), "get_Id");
        var key = (string)Call(Get(piece, "typeId"), "get_Id");
        return new RemovalTarget(piece, id, key, tile);
    }

    private void Remove(RemovalCommand command)
    {
        if (!plugin.VerifiedGame) throw new InvalidOperationException("Removal rejected: unverified build.");
        if (!HouseRules.TryGetLevelEditorContext(out var live, out var reason)) throw new InvalidOperationException(reason);
        if (!command.Snapshot.SameSession(live)) throw new InvalidOperationException("Session changed. Refresh.");
        var reader = new BoardReader(live);
        var tile = reader.Read(command.Target.Tile.X, command.Target.Tile.Y);
        var target = ValidateRemovalTarget(reader, tile);
        if (!command.Target.SamePiece(target)) throw new InvalidOperationException("Selected piece moved or changed.");
        if (!HouseRules.TryGetLevelEditorPicker(live, out var handler, out reason)) throw new InvalidOperationException(reason);
        RequireMapIdle(handler);
        var nativeMessage = MakeRemovalRequest(target, PhysicalDamageId(live, handler));
        var message = Load("Jade.Messaging", "Jade.Messaging.RequestTryDealDamageToPiece");
        var roomType = Load("Jade.Networking", "Jade.Networking.LocalNetworkRoom");
        var requestType = Load("Jade.Messaging", "Jade.Messaging.IRequest");
        if (!roomType.IsInstanceOfType(live.Room))
            throw new InvalidOperationException("Native local removal room type unavailable.");
        var request = CheckedRequest(nativeMessage, message, requestType);
        var send = Require(roomType, "Send", false, typeof(void), requestType);
        if (Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy)
            throw new InvalidOperationException("Local room busy; select again after it becomes idle.");
        if (!HouseRules.TryGetLevelEditorContext(out var final, out reason) || !live.SameSession(final))
            throw new InvalidOperationException("Session changed before dispatch: " + reason);
        send.Invoke(live.Room, new[] { request });
        log.LogInfo("BMF level editor removal damage request submitted: " + target.TypeKey + " at " + tile.X + "," + tile.Y);
        selectedTile = null; removalTarget = null; snapshot = live;
        status = "Removal damage submitted. Check result; game death/loot rules apply.";
    }

    // A compact overlay leaves the game map visible. Middle-click is reserved
    // for picking so ordinary left-click piece/card interactions are not used.
    internal bool DrawMapTool()
    {
        if (mapMode == 0) return false;
        if (mapMode == 3 && !plugin.HeroFormsEnabled) { EndMapTool(); return false; }
        if (mapMode == 4 && !plugin.DeveloperModeEnabled) { EndMapTool(); return false; }
        try
        {
            float width = Math.Min(610f, Screen.width - 24f);
            float x = (Screen.width - width) * 0.5f, y = 10f;
            var panel = new Rect(x, y, width, mapMode == 4 ? 183f : 152f);
            GUI.Box(panel, mapMode == 1 ? "BMF | Place enemy on game map" : mapMode == 3 ? "BMF | Hero forms (experimental)" : mapMode == 4 ? "BMF | Move enemy on game map" : "BMF | Remove piece from game map");
            bool formWaiting = mapMode == 3 && HeroForms.Waiting;
            bool moveWaiting = mapMode == 4 && EnemyMover.Waiting;
            bool ready = !mapPickRequested && pending == null && pendingRemoval == null && pendingForm == null && pendingMove == null && !formWaiting && !moveWaiting;
            GUI.Label(new Rect(x + 12, y + 23, width - 24, 22), mapMode == 1 ?
                "Enemy: " + (selectedEnemy?.Name ?? "(select enemy first)") : mapMode == 3 ? "Play as: " + selectedEnemy?.Name + " | Native form, card rules may change." : mapMode == 4 ? (moveSource == null ? "Step 1: Middle-click a living 1x1 enemy." : "Enemy: " + moveSource.TypeKey + " (" + moveSource.Tile.X + "," + moveSource.Tile.Y + ")") : "Enemies and destructible props only. Normal death effects apply.");
            GUI.Label(new Rect(x + 12, y + 45, width - 24, 22), selectedTile == null ?
                (mapMode == 4 && moveSource != null ? "Step 2: Middle-click any free destination tile." : "Middle-click a tile/piece on the 3D game board.") : "Selected tile (" + selectedTile.X + "," + selectedTile.Y + ") " + selectedTile.Detail);
            float half = (width - 30) * 0.5f;
            if (GUI.Button(new Rect(x + 12, y + 72, half, 27), "Back to editor / cancel"))
            { EndMapTool(); return true; }
            if (ready && mapMode == 1 && selectedTile?.Available == true && selectedEnemy != null)
            {
                if (GUI.Button(new Rect(x + 18 + half, y + 72, half, 27), "Confirm placement"))
                    pending = new SpawnCommand(snapshot, selectedTile, selectedEnemy);
            }
            else if (ready && mapMode == 2 && removalTarget != null)
            {
                if (GUI.Button(new Rect(x + 18 + half, y + 72, half, 27), "Confirm removal (damage)"))
                    pendingRemoval = new RemovalCommand(snapshot, removalTarget);
            }
            else if (ready && mapMode == 3 && heroTarget != null && selectedEnemy != null)
            {
                if (GUI.Button(new Rect(x + 18 + half, y + 72, half, 27), "Confirm hero form"))
                    pendingForm = new SpawnCommand(snapshot, selectedTile, selectedEnemy);
            }
            else if (ready && mapMode == 4 && moveSource != null && selectedTile?.Available == true)
            {
                if (GUI.Button(new Rect(x + 18 + half, y + 72, half, 27), "Confirm move"))
                    pendingMove = new MoveCommand(snapshot, moveSource, selectedTile);
            }
            else GUI.Label(new Rect(x + 18 + half, y + 72, half, 27), moveWaiting ? "Waiting for game..." : "Select an eligible target");
            GUI.Label(new Rect(x + 12, y + 102, width - 24, 25), mapMode == 3 && status.StartsWith("Hero form request submitted", StringComparison.Ordinal) ? HeroForms.Status : mapMode == 4 && status.StartsWith("Enemy move submitted", StringComparison.Ordinal) ? EnemyMover.Status : status);
            GUI.Label(new Rect(x + 12, y + 128, width - 24, 20), "Disposable local solo only | F10 cancels and closes");
            if (mapMode == 4 && ready && moveSource != null &&
                GUI.Button(new Rect(x + 12, y + 151, width - 24, 25), "Choose different enemy"))
            {
                moveSource = null; selectedTile = null; status = "Middle-click a living enemy to move.";
            }
            var e = Event.current;
            if (ready && e != null && e.type == EventType.MouseDown && e.button == 2 && !panel.Contains(e.mousePosition))
            {
                mapClickX = e.mousePosition.x; mapClickY = e.mousePosition.y;
                mapPickRequested = true; selectedTile = null; removalTarget = null; heroTarget = null;
                status = "Resolving live board tile...";
                e.Use();
            }
            return true;
        }
        catch (Exception ex)
        {
            EndMapTool();
            status = "Map toolbar unavailable: " + ex.GetBaseException().Message;
            log.LogWarning("BMF level editor toolbar: " + ex.GetBaseException());
            return false;
        }
    }

    internal void Draw(float x, float y, float width, float height)
    {
        SelectView(false);
        try
        {
            float inner = width - 30, left = inner * 0.48f, right = inner - left - 10, rx = x + 25 + left;
            float cell = Math.Min(31f, Math.Min((left - 4) / WindowSize, (height - 266) / WindowSize));
            void Label(float top, string value) => GUI.Label(new Rect(x + 15, y + top, inner, 22), value);
            bool Button(float top, float l, float w, string value) => GUI.Button(new Rect(x + l, y + top, w, 24), value);
            Label(91, "Runtime level editor | Disposable local solo only; back up saves.");
            float actionWidth = inner / 4f;
            if (Button(115, 15, actionWidth - 3, "Refresh"))
            { refresh = true; pan = confirming = false; pending = null; }
            if (Button(115, 15 + actionWidth, actionWidth - 3, "Place on map"))
            {
                if (snapshot != null && selectedEnemy != null && !refresh && !pan)
                { BeginMapTool(1); return; }
                status = "Refresh and select an enemy first.";
            }
            if (Button(115, 15 + 2 * actionWidth, actionWidth - 3, "Remove on map"))
            {
                if (snapshot != null && !refresh && !pan) { BeginMapTool(2); return; }
                status = "Refresh the current solo board first.";
            }
            if (Button(115, 15 + 3 * actionWidth, actionWidth - 3, "Move on map"))
            {
                if (!EnemyMover.Ready) status = EnemyMover.Status;
                else if (snapshot != null && !refresh && !pan) { BeginMapTool(4); return; }
                else status = "Refresh the current solo board first.";
            }
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
            Label(footer + 20, "Enemy: " + (selectedEnemy?.Name ?? "select registered 1x1 enemy from list"));
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

    internal void DrawHeroForms(float x, float y, float width, float height)
    {
        if (!plugin.HeroFormsEnabled) return;
        SelectView(true);
        try
        {
            float inner = width - 30;
            void Label(float top, string value) => GUI.Label(new Rect(x + 15, y + top, inner, 22), value);
            Label(91, "Hero forms (experimental) | Disposable local solo only; back up saves.");
            float half = (inner - 6) / 2;
            if (GUI.Button(new Rect(x + 15, y + 115, half, 25), "Refresh forms"))
            { refresh = true; selectedEnemy = null; pendingForm = null; }
            if (GUI.Button(new Rect(x + 21 + half, y + 115, half, 25), "Transform hero"))
            {
                if (snapshot != null && selectedEnemy != null && !refresh)
                { BeginMapTool(3); return; }
                status = "Refresh and select a creature form first.";
            }
            if (snapshot == null)
            {
                Label(146, status);
                Label(172, "House rules must be running; start a new local solo campaign.");
                return;
            }
            var typed = GUI.TextField(new Rect(x + 15, y + 145, inner, 25), filter, 100);
            if (typed != filter) { filter = typed; ApplyFilter(); selectedEnemy = null; pendingForm = null; }
            float footer = height - 103;
            int rows = Math.Max(1, (int)((footer - 203) / 24));
            int pages = Math.Max(1, (matches.Count + rows - 1) / rows);
            enemyPage = Math.Clamp(enemyPage, 0, pages - 1);
            for (int i = 0; i < rows && enemyPage * rows + i < matches.Count; i++)
            {
                var form = matches[enemyPage * rows + i];
                if (GUI.Button(new Rect(x + 15, y + 174 + i * 24, inner, 23), (ReferenceEquals(selectedEnemy, form) ? "* " : "") + form.Name))
                { selectedEnemy = form; pendingForm = null; }
            }
            if (GUI.Button(new Rect(x + 15, y + footer - 25, 65, 24), "<")) enemyPage = Math.Max(0, enemyPage - 1);
            GUI.Label(new Rect(x + 84, y + footer - 25, inner - 140, 23), (enemyPage + 1) + "/" + pages + " | " + matches.Count + " forms");
            if (GUI.Button(new Rect(x + 15 + inner - 65, y + footer - 25, 65, 24), ">")) enemyPage = Math.Min(pages - 1, enemyPage + 1);
            Label(footer, "Hero form: " + (selectedEnemy?.Name ?? "select registered creature or boss from list"));
            Label(footer + 24, "Select a form, press Transform hero, then middle-click any eligible hero during a player turn.");
            Label(footer + 48, "Large forms need open space. Native rules may change cards and stats.");
            Label(height - 27, status);
        }
        catch (Exception ex)
        {
            Clear();
            if (!guiErrorLogged) log.LogWarning("BMF Hero Forms GUI: " + ex.GetBaseException().Message);
            guiErrorLogged = true;
            GUI.Label(new Rect(x + 15, y + 118, width - 30, 24), "Hero Forms unavailable; see BepInEx log.");
        }
    }

    private void QueuePan(int dx, int dy)
    {
        panX = dx; panY = dy; pan = true; confirming = false; pending = null;
    }
}
