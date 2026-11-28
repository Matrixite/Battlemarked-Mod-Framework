using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using IEnumerator = Il2CppSystem.Collections.IEnumerator;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Direct relocation is a native simulation action, not a new piece or a
// presentation transform. A one-use local debug request routes the action
// through the authoritative runner with its real movement/view-event helpers.
internal static class EnemyMover
{
    private static ModuleContext ctx;
    private static int mainThread;
    private static Type requestType, commandType, handlerType, interactType, requestIdType, handlerInterface;
    private static MethodInfo forceMove, makeContext;
    private static MemberInfo coroutineMember;
    private static Type animationType, hierarchyType;
    private static object direct, noCorrection;
    private static Command pending;
    internal static string Status { get; private set; } = "Native enemy movement unavailable.";
    private sealed class Command
    {
        internal LevelEditor.Context Context;
        internal string Token, Session, TypeKey, Level, StartingKey, FormKey;
        internal long StartingEpoch;
        internal UObject Definition;
        internal ulong Host;
        internal int Id, X, Y, DestX, DestY;
        internal DateTime Expires;
        internal Il2CppObjectBase Handler, Helper, State;
    }

    private static Type Load(string asm, string type) => Assembly.Load(new AssemblyName(asm)).GetType(type, true);
    private static Type Model(string name) => Load("Jade.GameModel", "Jade.GameModel." + name);
    private static Type Logic(string name) => Load("GameLogic", "Jade.GameLogic." + name);
    private static Type Definition(string name) => Load("GameDefinitions", "Jade.GameDefinitions." + name);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    private static bool CaptureAllowed => ctx != null && Plugin.Instance?.VerifiedGame == true &&
        Environment.CurrentManagedThreadId == mainThread;
    private static bool Active => CaptureAllowed && Plugin.Instance?.DeveloperModeEnabled == true;
    internal static bool StartingReady => CaptureAllowed && forceMove != null &&
        Plugin.Instance?.HeroFormsEnabled == true && HeroStartingForms.Enabled;
    private static bool Allowed(Command command) => command.StartingKey == null ? Active : StartingReady;
    private static MethodInfo Exact(Type type, string name, bool isStatic, Type result, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != result)
            throw new InvalidOperationException("Enemy move binding missing: " + type.Name + "." + name);
        return method;
    }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(EnemyMover).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    private static Il2CppObjectBase Cast(object value, Type type)
    {
        if (value is not Il2CppObjectBase obj || obj.Pointer == IntPtr.Zero) return null;
        if (type.IsInstanceOfType(obj)) return obj;
        var cast = LevelEditor.FindCast(type).Invoke(obj, null) as Il2CppObjectBase;
        return cast != null && cast.Pointer == obj.Pointer && type.IsInstanceOfType(cast) ? cast : null;
    }
    private static bool SameNative(object a, object b) => a is Il2CppObjectBase x && b is Il2CppObjectBase y &&
        x.Pointer != IntPtr.Zero && x.Pointer == y.Pointer;
    private static Type MemberType(MemberInfo member) => member is PropertyInfo p ? p.PropertyType :
        member is FieldInfo f ? f.FieldType : null;

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            requestType = Load("Jade.Messaging", "Jade.Messaging.IRequest");
            commandType = Load("Jade.Messaging", "Jade.Messaging.RequestDebugCommand");
            requestIdType = Load("Jade.Messaging", "Jade.Messaging.RequestID");
            handlerInterface = Logic("IRequestHandler");
            handlerType = Logic("RequestHandlerDebugCommand");
            interactType = Logic("RequestHandlerInteract");
            var runner = Logic("GameLogicRunner");
            var helper = Logic("TeleportPieceHelper");
            var piece = Model("Piece");
            var point = Model("IntPoint2D");
            var actionContext = Definition("ActionContext");
            animationType = Definition("TeleportAnimationUseDefaultTeleport");
            hierarchyType = Logic("ActionHierarchyNode");
            makeContext = Exact(Logic("PieceOverlapResolutionHelper"), "CreateActionContext", true, actionContext, piece, point);
            var expected = new[] { piece, point, Definition("TeleportAnimationBase"), actionContext, hierarchyType };
            foreach (var method in helper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "ForceMovePiece" || method.IsAbstract || method.ContainsGenericParameters) continue;
                var p = method.GetParameters();
                if (p.Length != 7 || !p.Take(5).Select(a => a.ParameterType).SequenceEqual(expected) ||
                    !p[5].ParameterType.IsEnum || p[5].ParameterType.Name != "TeleportPathingMode" ||
                    !p[6].ParameterType.IsEnum || p[6].ParameterType.Name != "TeleportCorrectionMode") continue;
                if (forceMove != null) throw new InvalidOperationException("Ambiguous native force movement.");
                forceMove = method;
                direct = Enum.Parse(p[5].ParameterType, "TeleportDirectly");
                noCorrection = Enum.Parse(p[6].ParameterType, "DoNotCorrect");
            }
            if (forceMove == null) throw new InvalidOperationException("Native force movement signature unavailable.");
            // Verified metadata: ValueTuple<System.Collections.IEnumerator,
            // Jade.Utils.IProgressMarker>. Native ForceMoveSinglePieceActionExecutor
            // returns Item1 to its action runner. We do the same.
            var result = forceMove.ReturnType;
            const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            coroutineMember = (MemberInfo)result.GetProperty("Item1", members) ?? result.GetField("Item1", members);
            var progress = (MemberInfo)result.GetProperty("Item2", members) ?? result.GetField("Item2", members);
            if (result.Name != "ValueTuple`2" || MemberType(coroutineMember) != typeof(IEnumerator) ||
                MemberType(progress)?.FullName != "Jade.Utils.IProgressMarker")
                throw new InvalidOperationException("Native movement coroutine result signature unavailable.");
            if (animationType.GetConstructor(Type.EmptyTypes) == null || hierarchyType.GetConstructor(new[] { typeof(string) }) == null)
                throw new InvalidOperationException("Native movement animation or hierarchy unavailable.");
            var process = Exact(runner, "ProcessRequest", false, typeof(void), requestType);
            var handle = Exact(handlerType, "HandleRequest", false, typeof(IEnumerator), requestType);
            context.Patches.Patch(process, prefix: Hook(nameof(CaptureRunner)));
            context.Patches.Patch(handle, prefix: Hook(nameof(HandleCommand)));
            ctx = context;
            Status = "Native enemy movement ready.";
            context.Log.LogInfo("BMF enemy move: native request and force-movement bindings attached.");
        }
        catch (Exception ex)
        {
            Stop();
            Status = "Enemy movement unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF " + Status);
        }
    }

    internal static bool Ready => Active && forceMove != null;
    internal static bool Waiting
    {
        get
        {
            if (pending != null && DateTime.UtcNow > pending.Expires)
            {
                bool starting = pending.StartingKey != null;
                pending = null;
                Status = starting ? "Starting hero relocation request timed out." : "Move request timed out; select the enemy again.";
                ctx?.Log.LogWarning("BMF enemy move: " + Status);
            }
            return pending != null;
        }
    }
    // Closing the level editor or turning Developer mode off cancels only
    // manual enemy moves. Starting Forms owns its separate automatic request.
    internal static void Cancel()
    {
        if (pending?.StartingKey != null && StartingReady) return;
        pending = null;
    }
    internal static void CancelStarting() { if (pending?.StartingKey != null) pending = null; }
    internal static void Stop() { pending = null; ctx = null; forceMove = null; makeContext = null; coroutineMember = null; }

    private static int Id(object piece) => (int)Call(Get(piece, "pieceId"), "get_Id");
    private static string TypeKey(object piece)
    {
        var id = Get(piece, "typeId");
        if (Call(id, "get_Id") is not string key || string.IsNullOrWhiteSpace(key) ||
            Call(id, "get_Guid") is not string guid || string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Enemy type identity unavailable.");
        return key + " | " + guid;
    }
    internal static void ValidateEnemy(object piece, int x, int y)
    {
        if (Cast(piece, Model("Piece")) == null ||
            Get(piece, "team")?.ToString() != "EnemyOfPlayers" ||
            Get(piece, "isPlayerControlled") is not bool controlled || controlled ||
            Get(piece, "isCreatureNonNpc") is not bool creature || !creature ||
            Get(piece, "isPOI") is not bool poi || poi ||
            Get(piece, "isTransformedPiece") is not bool transformed || transformed ||
            Exact(Model("PieceHelpers"), "IsDead", true, typeof(bool), Model("Piece")).Invoke(null, new[] { piece }) is not bool dead || dead ||
            Id(piece) < 0)
            throw new InvalidOperationException("Select a living hostile creature (not a hero, NPC or prop).");
        var size = Exact(Model("PieceSizeHelper"), "GetPieceTileSize", true, Model("IntPoint2D"), Model("Piece"))
            .Invoke(null, new[] { piece });
        if ((int)Get(size, "x") != 1 || (int)Get(size, "y") != 1)
            throw new InvalidOperationException("Move currently supports 1x1 enemies only.");
        var pos = Get(piece, "gridPos");
        if ((int)Get(pos, "x") != x || (int)Get(pos, "y") != y)
            throw new InvalidOperationException("Enemy moved; select it again.");
    }

    private static object Point(int x, int y) => Model("IntPoint2D").GetConstructor(new[] { typeof(int), typeof(int) })
        ?.Invoke(new object[] { x, y }) ?? throw new InvalidOperationException("Native grid point unavailable.");
    private static object PieceAt(object board, object point)
    {
        var args = new[] { board, point, (object)null };
        return Exact(Model("BoardHelpers"), "TryGetPieceAtPoint", true, typeof(bool), Model("Board"), Model("IntPoint2D"), Model("Piece").MakeByRefType())
            .Invoke(null, args) is bool found && found ? args[2] : null;
    }
    private static void ValidateDestination(object board, int x, int y)
    {
        var tiles = Get(board, "tileSet");
        var helpers = Model("TileSetHelpers");
        var tileSet = Model("TileSet");
        var point = Point(x, y);
        if (Exact(helpers, "TileExist", true, typeof(bool), tileSet, typeof(int), typeof(int))
            .Invoke(null, new[] { tiles, (object)x, y }) is not bool exists || !exists)
            throw new InvalidOperationException("Destination is outside the loaded board.");
        var tile = Exact(helpers, "GetTile", true, Model("Tile"), tileSet, typeof(int), typeof(int))
            .Invoke(null, new[] { tiles, (object)x, y });
        if (Call(tile, "get_IsWalkable") is not bool walk || !walk ||
            Get(tile, "tileIsTemporarilyBlocked") is not bool blocked || blocked ||
            PieceAt(board, point) != null ||
            Exact(Model("BoardHelpers"), "IsWalkable", true, typeof(bool), Model("Board"), Model("IntPoint2D"))
                .Invoke(null, new[] { board, point }) is not bool walkable || !walkable ||
            Exact(Model("BoardHelpers"), "CanSpawnPieceOnPosition", true, typeof(bool), Model("Board"), Model("IntPoint2D"), Model("PieceId"))
                .Invoke(null, new[] { board, point, null }) is not bool valid || !valid)
            throw new InvalidOperationException("Destination is occupied, blocked or invalid.");
    }
    private static string Session(object state) => Get(state, "sessionId") as string;
    private static string Level(object board) => Get(board, "currentLevelSceneName") as string;
    private static bool MatchState(object state, Command command) =>
        !string.IsNullOrWhiteSpace(command.Session) && Session(state) == command.Session &&
        Call(Get(state, "hostPlayerId"), "get_Id") is ulong host && host == command.Host &&
        !string.IsNullOrWhiteSpace(command.Level) && Level(Get(state, "board")) == command.Level;

    internal static string Queue(LevelEditor.Context context, object piece, int x, int y, int destX, int destY)
    {
        if (!Ready) throw new InvalidOperationException(Status);
        if (Waiting) throw new InvalidOperationException("An enemy move is already pending.");
        if (!HouseRules.TryGetLevelEditorContext(out var live, out var reason) || !context.SameSession(live))
            throw new InvalidOperationException("Move rejected: " + reason);
        ValidateEnemy(piece, x, y);
        if (!SameNative(PieceAt(live.Board, Point(x, y)), piece))
            throw new InvalidOperationException("Enemy changed; select it again.");
        ValidateDestination(live.Board, destX, destY);
        if (Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy)
            throw new InvalidOperationException("Local request queue busy; retry when idle.");
        var command = new Command { Context = live, Token = "bmf-enemy-move-" + Guid.NewGuid().ToString("N"),
            Session = Session(live.State), Host = (ulong)Call(Get(live.State, "hostPlayerId"), "get_Id"), Level = Level(live.Board),
            Id = Id(piece), TypeKey = TypeKey(piece), X = x, Y = y, DestX = destX, DestY = destY, Expires = DateTime.UtcNow.AddSeconds(15) };
        return Submit(live, command);
    }

    internal static string QueueStarting(LevelEditor.Context context, object piece, UObject definition,
        string key, string formKey, int destX, int destY)
    {
        if (!StartingReady || Waiting) throw new InvalidOperationException("Starting hero movement not ready.");
        if (!HouseRules.TryGetHeroFormsContext(out var live, out var reason) || !context.SameSession(live))
            throw new InvalidOperationException("Starting move rejected: " + reason);
        var pos = Get(piece, "gridPos");
        var command = new Command { Context = live, Token = "bmf-starting-move-" + Guid.NewGuid().ToString("N"),
            Session = Session(live.State), Host = (ulong)Call(Get(live.State, "hostPlayerId"), "get_Id"), Level = Level(live.Board),
            Id = Id(piece), TypeKey = TypeKey(piece), X = (int)Get(pos, "x"), Y = (int)Get(pos, "y"),
            DestX = destX, DestY = destY, Expires = DateTime.UtcNow.AddSeconds(15),
            StartingKey = key, FormKey = formKey, StartingEpoch = HeroStartingForms.Epoch, Definition = definition };
        ValidateStartingSource(live, live.State, live.Board, piece, command);
        if (!SameNative(PieceAt(live.Board, Point(command.X, command.Y)), piece))
            throw new InvalidOperationException("Starting hero changed before relocation.");
        ValidateStartingDestination(live.Board, piece, command);
        return Submit(live, command);
    }

    private static bool CurrentContext(Command command, out LevelEditor.Context live, out string reason) =>
        command.StartingKey == null ? HouseRules.TryGetLevelEditorContext(out live, out reason) :
            HouseRules.TryGetHeroFormsContext(out live, out reason);

    private static void ValidateStartingSource(LevelEditor.Context live, object state, object board, object piece, Command command)
    {
        if (!StartingReady || !HeroStartingForms.AuthorizesRelocation(command.StartingKey, command.FormKey, command.StartingEpoch, state, piece) ||
            !HouseRules.TryValidateLocalHero(live, piece, out _) || Id(piece) != command.Id || TypeKey(piece) != command.TypeKey)
            throw new InvalidOperationException("Starting hero or assigned form changed.");
        HeroForms.ValidatePiece(state, board, piece);
        if (command.Definition == null || HeroFormPersistence.Key(Call(command.Definition, "get_PieceTypeId")) != command.FormKey ||
            !LevelEditor.ValidateRegisteredStartingForm(live, command.Definition))
            throw new InvalidOperationException("Starting monster definition changed.");
        var pos = Get(piece, "gridPos");
        if ((int)Get(pos, "x") != command.X || (int)Get(pos, "y") != command.Y)
            throw new InvalidOperationException("Starting hero moved before relocation.");
    }

    private static void ValidateStartingDestination(object board, object piece, Command command)
    {
        // The authoritative board must independently identify the same
        // nearest reachable space, with the full monster footprint available.
        if (!StartingFormRelocation.TryFind(board, piece, command.Definition, out int x, out int y) ||
            x != command.DestX || y != command.DestY)
            throw new InvalidOperationException("Starting relocation destination no longer fits or is not reachable.");
        ValidateDestination(board, x, y);
    }

    private static string Submit(LevelEditor.Context live, Command command)
    {
        if (!MatchState(live.State, command)) throw new InvalidOperationException("Session identity unavailable.");
        if (Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy)
            throw new InvalidOperationException("Local request queue busy; retry when idle.");
        var message = commandType.GetConstructor(new[] { typeof(string) })?.Invoke(new object[] { command.Token });
        var request = LevelEditor.CheckedRequest(message, commandType, requestType);
        var send = Exact(Load("Jade.Networking", "Jade.Networking.LocalNetworkRoom"), "Send", false, typeof(void), requestType);
        if (!Allowed(command) || !CurrentContext(command, out var final, out _) || !live.SameSession(final))
            throw new InvalidOperationException("Session changed before move dispatch.");
        pending = command;
        Status = command.StartingKey == null ? "Enemy move submitted; waiting for game." : "Starting hero move submitted; waiting for game.";
        try { send.Invoke(live.Room, new[] { request }); }
        catch { pending = null; throw; }
        ctx.Log.LogInfo("BMF native move request submitted: " + command.TypeKey + " to " + command.DestX + "," + command.DestY);
        return Status;
    }

    private static bool Matches(object request, Command command)
    {
        var msg = Cast(request, commandType);
        return msg != null && Call(msg, "get_Command") is string token && token == command.Token;
    }
    private static Il2CppObjectBase Handler(object dictionary, string key, Type type)
    {
        var args = new[] { Enum.Parse(requestIdType, key), (object)null };
        var lookup = Exact(dictionary.GetType(), "TryGetValue", false, typeof(bool), requestIdType, handlerInterface.MakeByRefType());
        return lookup.Invoke(dictionary, args) is bool found && found ? Cast(args[1], type) : null;
    }
    private static void CaptureRunner(object __instance, object __0)
    {
        var command = pending;
        if (command == null || !Allowed(command)) return;
        try
        {
            if (!Matches(__0, command) || DateTime.UtcNow > command.Expires) return;
            // Only read the live runner for our exact request. Native fallback
            // runners are followed in their normal dispatch order, bounded.
            Il2CppObjectBase debug = null, interact = null;
            var runner = __instance as Il2CppObjectBase;
            var visited = new HashSet<IntPtr>();
            for (int i = 0; i < 8 && runner != null && runner.Pointer != IntPtr.Zero && visited.Add(runner.Pointer); i++)
            {
                var dictionary = Get(runner, "requestHandlers");
                debug ??= Handler(dictionary, "DebugCommand", handlerType);
                interact ??= Handler(dictionary, "Interact", interactType);
                if (debug != null && interact != null) break;
                runner = Get(runner, "runnerForRequestIDontKnowOf") as Il2CppObjectBase;
            }
            if (debug == null || interact == null) return;
            var state = Cast(Get(interact, "gameState"), Model("GameState"));
            var helper = Cast(Get(interact, "teleportPieceHelper"), Logic("TeleportPieceHelper"));
            if (state == null || helper == null || !MatchState(state, command) || !SameNative(Get(helper, "gameState"), state)) return;
            command.Handler = debug; command.State = state; command.Helper = helper;
        }
        catch (Exception ex) { ctx?.Log.LogWarning("BMF enemy movement runner capture: " + ex.GetBaseException().Message); }
    }

    private static bool HandleCommand(object __instance, object __0, ref IEnumerator __result)
    {
        var command = pending;
        if (command == null || !Allowed(command)) return true;
        try
        {
            if (!Matches(__0, command)) return true;
            pending = null; // Consume exactly once before any native action.
            if (DateTime.UtcNow > command.Expires ||
                !CurrentContext(command, out var live, out _) || !command.Context.SameSession(live) ||
                !MatchState(live.State, command) || command.State == null || command.Helper == null ||
                !SameNative(command.Handler, __instance) || !MatchState(command.State, command) ||
                !SameNative(Get(command.Helper, "gameState"), command.State) ||
                (command.StartingKey != null && !HeroFormPersistence.MatchesStage(command.State, live.State)))
                throw new InvalidOperationException("Native move request does not match the confirmed solo session.");
            var board = Get(command.State, "board");
            if (Call(board, "get_IsLoaded") is not bool loaded || !loaded)
                throw new InvalidOperationException("Native board not loaded.");
            var piece = PieceAt(board, Point(command.X, command.Y));
            if (command.StartingKey != null)
            {
                ValidateStartingSource(live, command.State, board, piece, command);
                ValidateStartingDestination(board, piece, command);
            }
            else
            {
                ValidateEnemy(piece, command.X, command.Y);
                if (Id(piece) != command.Id || TypeKey(piece) != command.TypeKey)
                    throw new InvalidOperationException("Enemy identity changed before move.");
                ValidateDestination(board, command.DestX, command.DestY);
            }
            var destination = Point(command.DestX, command.DestY);
            var action = makeContext.Invoke(null, new[] { piece, destination });
            var animation = Activator.CreateInstance(animationType);
            var hierarchy = Activator.CreateInstance(hierarchyType, new object[] { command.StartingKey == null ? "bmf-enemy-move" : "bmf-starting-move" });
            // Native simulation retains the piece ID and operates its usual
            // movement/interaction/view events. No path or correction to a
            // different tile is requested.
            var result = forceMove.Invoke(command.Helper, new[] { piece, destination, animation, action, hierarchy, direct, noCorrection });
            var coroutine = coroutineMember is PropertyInfo property ? property.GetValue(result) : ((FieldInfo)coroutineMember).GetValue(result);
            __result = Cast(coroutine, typeof(IEnumerator)) as IEnumerator;
            if (__result == null) throw new InvalidOperationException("Native move coroutine unavailable.");
            Status = command.StartingKey == null ? "Native move accepted; check the enemy at the destination." :
                "Native starting hero move accepted; waiting for arrival.";
            ctx.Log.LogInfo("BMF enemy move: native force movement coroutine returned.");
            return false;
        }
        catch (Exception ex)
        {
            pending = null;
            Status = "Move rejected: " + ex.GetBaseException().Message;
            ctx?.Log.LogWarning("BMF " + Status);
            return true;
        }
    }
}
