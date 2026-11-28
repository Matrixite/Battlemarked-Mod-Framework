using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Only a confirmed local request can invoke the simulation's native reversible
// transformation. Never start a simulation coroutine from the presentation UI.
internal static class HeroForms
{
    private static ModuleContext ctx;
    private static int mainThread;
    private static MethodInfo transform;
    private static Type commandType, requestType, helperType;
    private static readonly Dictionary<IntPtr, Il2CppObjectBase> handlers = new();
    private static readonly Dictionary<IntPtr, Il2CppObjectBase> helpers = new();
    private static Command pending;
    internal static string Status { get; private set; } = "Hero forms require a new disposable solo campaign.";
    private sealed class Command
    {
        internal LevelEditor.Context Context;
        internal string Token, Session, Sheet, Source, Destination, StartingKey;
        internal ulong Owner;
        internal int Id;
        internal bool Automatic;
        internal long StartingEpoch;
        internal IntPtr AutomaticState;
        internal UObject Definition;
        internal DateTime Expires;
    }

    private static Type Load(string assembly, string name) => Assembly.Load(new AssemblyName(assembly)).GetType(name, true);
    private static Type Model(string name) => Load("Jade.GameModel", "Jade.GameModel." + name);
    private static Type Logic(string name) => Load("GameLogic", "Jade.GameLogic." + name);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    // Read-only capture is retained even while Hero Forms is disabled; only
    // the explicit feature switch authorizes a transformation request.
    private static bool CaptureAllowed => ctx != null && Plugin.Instance?.VerifiedGame == true &&
        Environment.CurrentManagedThreadId == mainThread;
    private static bool Active => CaptureAllowed && Plugin.Instance?.HeroFormsEnabled == true;
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(HeroForms).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    private static MethodInfo Exact(Type type, string name, bool isStatic, Type result, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != result)
            throw new InvalidOperationException("Hero forms binding missing: " + type.Name + "." + name);
        return method;
    }
    private static Il2CppObjectBase Cast(Il2CppObjectBase obj, Type type)
    {
        if (obj == null || obj.Pointer == IntPtr.Zero) return null;
        if (type.IsInstanceOfType(obj)) return obj;
        var cast = LevelEditor.FindCast(type).Invoke(obj, null) as Il2CppObjectBase;
        return cast != null && cast.Pointer == obj.Pointer && type.IsInstanceOfType(cast) ? cast : null;
    }

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            var state = Model("GameState");
            var handler = Logic("RequestHandlerDebugCommand");
            helperType = Logic("SwapPieceHelper");
            commandType = Load("Jade.Messaging", "Jade.Messaging.RequestDebugCommand");
            requestType = Load("Jade.Messaging", "Jade.Messaging.IRequest");
            var factory = Exact(Logic("RequestHandlerDebugCommandFactory"), "Create", true, handler,
                Logic("IDebugCommands"), state, Logic("PieceAIRunner"), Logic("PieceDespawner"),
                Logic("CharacterSheets.ICharacterSheetUpdater"), Load("GameDefinitions", "Jade.GameDefinitions.PieceAnimationConfig"),
                Logic("ITelegraphedTilesHelper"), Load("GameTags", "Jade.GameTags.AffectedPiecesConfig"),
                Load("GameDefinitions", "Jade.GameDefinitions.PieceRegistry"), Logic("IPieceSpawner"),
                Logic("ParallelBufferingViewEventPlayer"));
            var processor = Logic("SpawnHeroPieceForPlayers");
            var process = processor.GetMethod("Process", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (process == null || process.IsAbstract || process.ContainsGenericParameters || process.ReturnType.Name != "Result")
                throw new InvalidOperationException("Native hero spawn processor unavailable.");
            var sheet = Model("Piece").GetProperty("characterSheetId")?.PropertyType;
            if (sheet == null) throw new InvalidOperationException("Hero sheet type unavailable.");
            string[] names = { "pieceToBeReplaced", "newPieceTypeId", "playerId", "team", "isPlayerControllable", "characterSheetId",
                "isTransformationReversible", "showDespawnAnimation", "transformedPieceAction" };
            var types = new[] { Model("Piece"), Model("PieceTypeId"), Model("PlayerId"), Model("Team"), typeof(bool), sheet, typeof(bool), typeof(bool) };
            foreach (var method in helperType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "TransformIntoNewPiece" || method.ReturnType != typeof(Il2CppSystem.Collections.IEnumerator)) continue;
                var p = method.GetParameters();
                if (p.Length != names.Length || p[8].ParameterType.IsValueType) continue;
                bool exact = true;
                for (int i = 0; i < p.Length; i++)
                    exact &= p[i].Name == names[i] && (i == 8 || p[i].ParameterType == types[i]);
                if (!exact) continue;
                if (transform != null) throw new InvalidOperationException("Ambiguous native transformation.");
                transform = method;
            }
            if (transform == null) throw new InvalidOperationException("Native transformation signature unavailable.");
            var handle = Exact(handler, "HandleRequest", false, typeof(Il2CppSystem.Collections.IEnumerator), requestType);
            context.Patches.Patch(factory, postfix: Hook(nameof(CaptureHandler)));
            context.Patches.Patch(process, prefix: Hook(nameof(CaptureSwap)));
            context.Patches.Patch(handle, prefix: Hook(nameof(HandleCommand)));
            ctx = context;
            HeroFormCards.Attach(context);
            HeroFormPersistence.Attach(context);
            HeroStartingForms.Attach(context);
            Status = "Hero forms ready for a new disposable solo campaign.";
            context.Log.LogInfo("BMF hero forms: native request and transformation capture hooks attached.");
        }
        catch (Exception ex)
        {
            Stop();
            Status = "Hero forms unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF " + Status);
        }
    }
    internal static void ReportCardDeck(string message) { Status = message; }
    internal static void Cancel()
    {
        // Closing the map tool must not cancel a level carry-over already
        // submitted by the background restoration worker.
        if (pending != null && pending.Automatic && Plugin.Instance?.HeroFormsEnabled == true) return;
        pending = null;
    }
    internal static void Stop()
    {
        HeroFormCards.Stop();
        HeroFormPersistence.Stop();
        HeroStartingForms.Stop();
        pending = null; handlers.Clear(); helpers.Clear(); transform = null; ctx = null;
    }
    private static void CaptureHandler(object __1, object __result)
    {
        if (!CaptureAllowed) return;
        try
        {
            if (__1 is not Il2CppObjectBase state || state.Pointer == IntPtr.Zero ||
                __result is not Il2CppObjectBase handler || handler.Pointer == IntPtr.Zero) return;
            if (handlers.Count >= 8 && !handlers.ContainsKey(handler.Pointer)) handlers.Clear();
            handlers[handler.Pointer] = state;
            ctx.Log.LogInfo("BMF hero forms: native debug request handler captured.");
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero forms handler capture: " + ex.GetBaseException().Message); }
    }
    private static void CaptureSwap(object __instance)
    {
        if (!CaptureAllowed) return;
        try
        {
            var state = Get(__instance, "gameState") as Il2CppObjectBase;
            var helper = Cast(Get(__instance, "swapPieceHelper") as Il2CppObjectBase, helperType);
            if (state == null || state.Pointer == IntPtr.Zero || helper == null ||
                Get(helper, "gameState") is not Il2CppObjectBase linked || linked.Pointer != state.Pointer) return;
            if (helpers.Count >= 8 && !helpers.ContainsKey(state.Pointer)) helpers.Clear();
            if (!helpers.ContainsKey(state.Pointer)) ctx.Log.LogInfo("BMF hero forms: native hero transformation helper captured.");
            helpers[state.Pointer] = helper;
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero forms helper capture: " + ex.GetBaseException().Message); }
    }
    private static string Session(object state) => Get(state, "sessionId") as string;
    private static string Sheet(object piece) => Call(Get(piece, "characterSheetId"), "get_Id") as string;
    private static ulong Owner(object piece) => (ulong)Call(Get(piece, "ownerId"), "get_Id");
    private static string Key(object id) => (string)Call(id, "get_Id") + " | " + (string)Call(id, "get_Guid");
    private static bool MatchState(object state, Command command) =>
        !string.IsNullOrWhiteSpace(command.Session) && Session(state) == command.Session &&
        Call(Get(state, "hostPlayerId"), "get_Id") is ulong host && host == command.Owner &&
        (!command.Automatic || command.AutomaticState == IntPtr.Zero ||
            state is Il2CppObjectBase native && native.Pointer == command.AutomaticState);

    // Any living local party hero during an idle player turn, including an
    // existing transformed creature. The authoritative handler checks the
    // native original record before allowing a repeated transformation.
    internal static void ValidatePiece(object state, object board, object piece)
    {
        if (piece is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero ||
            !Model("Piece").IsInstanceOfType(piece) ||
            Get(piece, "isPlayerControlled") is not bool controlled || !controlled ||
            Get(piece, "isCreatureNonNpc") is not bool creature || !creature ||
            Get(piece, "isTransformedPiece") is not bool transformed ||
            Get(piece, "isPOI") is not bool poi || poi ||
            Get(piece, "team")?.ToString() != "Players" ||
            string.IsNullOrWhiteSpace(Sheet(piece)) ||
            Call(Get(state, "hostPlayerId"), "get_Id") is not ulong host || host != Owner(piece))
            throw new InvalidOperationException("Select a locally owned hero or transformed creature.");
        var type = Model("Piece");
        if (Exact(Model("PieceHelpers"), "IsDead", true, typeof(bool), type).Invoke(null, new[] { piece }) is not bool dead || dead)
            throw new InvalidOperationException("Hero has no living piece.");
        var stats = Get(piece, "stats");
        var statType = Model("Stats+Type");
        var hpArgs = new object[] { Enum.Parse(statType, "Health"), 0, 0 };
        if (Exact(stats.GetType(), "TryGetStat", false, typeof(bool), statType, typeof(int).MakeByRefType(), typeof(int))
            .Invoke(stats, hpArgs) is not bool hpFound || !hpFound || hpArgs[1] is not int hp || hp <= 0)
            throw new InvalidOperationException("Only a living hero with positive health can transform.");
        var size = Exact(Model("PieceSizeHelper"), "GetPieceTileSize", true, Model("IntPoint2D"), type).Invoke(null, new[] { piece });
        int width = (int)Get(size, "x"), height = (int)Get(size, "y");
        if (width is < 1 or > 4 || height is < 1 or > 4 ||
            (!transformed && (width != 1 || height != 1)))
            throw new InvalidOperationException("Unsupported source hero/form footprint.");
        // Native SpawnPieces replaces the old piece at its actual turn-queue
        // index. It need not be the current index; preserve the active turn
        // and refuse pieces outside the live queue or an enemy's turn.
        var queue = Get(board, "turnQueue");
        if (Exact(Model("TurnQueue"), "GetTurnOrderForPiece", false, typeof(int), Model("PieceId"))
            .Invoke(queue, new[] { Get(piece, "pieceId") }) is not int index || index < 0)
            throw new InvalidOperationException("Hero is not in the current turn order.");
        var args = new object[] { board, null };
        if (Exact(Model("BoardHelpers"), "TryGetCurrentPiece", true, typeof(bool), Model("Board"), type.MakeByRefType())
            .Invoke(null, args) is not bool current || !current || args[1] == null ||
            Get(args[1], "team")?.ToString() != "Players" ||
            Get(args[1], "isPlayerControlled") is not bool currentControlled || !currentControlled ||
            Owner(args[1]) != Owner(piece))
            throw new InvalidOperationException("Wait until a local hero's turn.");
    }

    // The native size helper understands square monsters and rotated
    // rectangular creatures. Do not infer footprint from the asset name.
    internal static object FormFootprint(UObject definition, float orientation)
    {
        var size = Call(definition, "get_PieceSize");
        if (size == null || !size.GetType().IsEnum || size.GetType().FullName != "Jade.SharedEnums.PieceSize" ||
            !float.IsFinite(orientation))
            throw new InvalidOperationException("Native form footprint unavailable.");
        var footprint = Exact(Model("PieceSizeHelper"), "PieceSizeToTileSize", true, Model("IntPoint2D"),
            size.GetType(), typeof(float)).Invoke(null, new[] { size, (object)orientation });
        // Verified game size enum: Tiny/Small/Medium 1x1, Large 2x2,
        // Huge 3x3, Gargantuan 4x4, and TwoByOne 2x1 or 1x2.
        if ((int)Get(footprint, "x") is < 1 or > 4 || (int)Get(footprint, "y") is < 1 or > 4)
            throw new InvalidOperationException("Unsupported native form footprint.");
        return footprint;
    }

    internal static void ValidateFormFit(object board, object piece, UObject definition)
    {
        // TransformIntoNewPiece captures the original piece's TileRect center
        // and orientation before despawn. Native spawn placement constructs
        // TileRect(desiredPosition, orientedSize) from these exact values.
        var point = Model("IntPoint2D");
        var rect = Model("TileRect");
        var center = Exact(Model("Piece"), "get_Center", false, point).Invoke(piece, null);
        var footprint = FormFootprint(definition, (float)Get(piece, "orientation"));
        var area = rect.GetConstructor(new[] { point, point })?.Invoke(new[] { center, footprint });
        if (area == null) throw new InvalidOperationException("Native form tile rectangle unavailable.");
        // Check every tile through the same native spawn-area helper, excluding
        // only the hero that the transformation is about to replace.
        if (Exact(Model("BoardHelpers"), "CanSpawnPieceInArea", true, typeof(bool), Model("Board"), rect, Model("PieceId"))
            .Invoke(null, new[] { board, area, Get(piece, "pieceId") }) is not bool fits || !fits)
            throw new InvalidOperationException("No room for " + Get(footprint, "x") + "x" + Get(footprint, "y") +
                " form; move your hero to an open area.");
    }

    internal static bool Waiting
    {
        get
        {
            if (pending != null && DateTime.UtcNow > pending.Expires)
            {
                pending = null; Status = "Native transformation request not observed (timed out).";
                ctx?.Log.LogWarning("BMF hero forms: " + Status);
            }
            return pending != null || HeroFormCards.Waiting;
        }
    }

    internal static string Queue(LevelEditor.Context context, object piece, UObject definition, object id, bool automatic = false, string startingKey = null)
    {
        if (Plugin.Instance?.HeroFormsEnabled != true) throw new InvalidOperationException("Hero Forms disabled in Mods.");
        if (!Active || transform == null) throw new InvalidOperationException(Status);
        if (Waiting) throw new InvalidOperationException("A hero form request is already pending.");
        if (!HouseRules.TryGetHeroFormsContext(out var live, out var reason) || !context.SameSession(live) ||
            !HouseRules.TryValidateLocalHero(live, piece, out reason))
            throw new InvalidOperationException("Hero form rejected: " + reason);
        ValidatePiece(live.State, live.Board, piece);
        ValidateFormFit(live.Board, piece, definition);
        if (Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy)
            throw new InvalidOperationException("Local request queue busy.");
        var command = new Command { Context = live, Token = "bmf-hero-form-" + Guid.NewGuid().ToString("N"),
            Session = Session(live.State), Sheet = Sheet(piece), Owner = Owner(piece),
            Id = (int)Call(Get(piece, "pieceId"), "get_Id"), Source = Key(Get(piece, "typeId")), Destination = Key(id),
            Definition = definition, Automatic = automatic, StartingKey = startingKey, StartingEpoch = HeroStartingForms.Epoch, Expires = DateTime.UtcNow.AddSeconds(15) };
        if (command.Source == command.Destination) throw new InvalidOperationException("Hero already has this form.");
        var matches = new HashSet<IntPtr>();
        foreach (var state in handlers.Values)
            if (helpers.ContainsKey(state.Pointer) && MatchState(state, command) &&
                (!automatic || HeroFormPersistence.MatchesStage(state, live.State))) matches.Add(state.Pointer);
        if (matches.Count != 1) throw new InvalidOperationException("No unique native transformation session. Start a verified local solo campaign.");
        if (automatic)
            foreach (var match in matches) command.AutomaticState = match;
        var message = commandType.GetConstructor(new[] { typeof(string) })?.Invoke(new object[] { command.Token });
        var request = LevelEditor.CheckedRequest(message, commandType, requestType);
        var send = Exact(Load("Jade.Networking", "Jade.Networking.LocalNetworkRoom"), "Send", false, typeof(void), requestType);
        if (!HouseRules.TryGetHeroFormsContext(out var final, out reason) || !live.SameSession(final))
            throw new InvalidOperationException("Session changed before hero form dispatch.");
        pending = command;
        Status = "Hero form request submitted; waiting for native handler.";
        try { send.Invoke(live.Room, new[] { request }); }
        catch { pending = null; throw; }
        ctx.Log.LogInfo("BMF hero forms: request submitted for " + definition.name);
        return "Hero form request submitted; waiting for native handler.";
    }

    private static bool HandleCommand(object __instance, object __0, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        // The native dispatcher returns null for an unknown command. Our
        // unique token is never registered and cannot mutate if a hook fails.
        var command = pending;
        if (command == null || !Active) return true;
        try
        {
            var msg = Cast(__0 as Il2CppObjectBase, commandType);
            if (msg == null || Call(msg, "get_Command") is not string token || token != command.Token) return true;
            pending = null; // Consume once, before validation or invoking native logic.
            if (DateTime.UtcNow > command.Expires ||
                !HouseRules.TryGetHeroFormsContext(out var live, out _) || !command.Context.SameSession(live) ||
                __instance is not Il2CppObjectBase handler || !handlers.TryGetValue(handler.Pointer, out var state) ||
                !MatchState(state, command) || !helpers.TryGetValue(state.Pointer, out var helper) ||
                (command.Automatic && !HeroFormPersistence.MatchesStage(state, live.State)))
                throw new InvalidOperationException("Native request no longer matches the confirmed session.");
            var board = Get(state, "board");
            if (Call(board, "get_IsLoaded") is not bool loaded || !loaded)
                throw new InvalidOperationException("Native board not loaded.");
            var pieceId = Model("PieceId").GetConstructor(new[] { typeof(int) })?.Invoke(new object[] { command.Id });
            var piece = Exact(Model("BoardHelpers"), "GetPiece", true, Model("Piece"), Model("Board"), Model("PieceId"))
                .Invoke(null, new[] { board, pieceId });
            ValidatePiece(state, board, piece);
            if (Sheet(piece) != command.Sheet || Owner(piece) != command.Owner || Key(Get(piece, "typeId")) != command.Source ||
                !HouseRules.TryValidateLocalHero(live, piece, out _))
                throw new InvalidOperationException("Native hero identity changed.");
            if (command.StartingKey != null && !HeroStartingForms.Authorizes(command.StartingKey, command.Destination, command.StartingEpoch, state, piece))
                throw new InvalidOperationException("Starting form preference changed or character no longer matches.");
            var registry = Get(helper, "pieceRegistry");
            var id = Call(command.Definition, "get_PieceTypeId");
            var registered = Exact(registry.GetType(), "GetPieceDefinition", false,
                Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition"), Model("PieceTypeId"))
                .Invoke(registry, new[] { id }) as UObject;
            if (registered == null || registered != command.Definition || Key(id) != command.Destination)
                throw new InvalidOperationException("Native form definition changed.");
            ValidateFormFit(board, piece, registered);
            // Native CreateOrUpdateExistingTransformRecord reuses the original
            // record by replacement ID. Verify that record before requesting a
            // switch rather than accidentally recording the intermediate form.
            var original = HeroFormPersistence.RequireOriginalHero(state, piece);
            string deckStatus = HeroFormCards.Arm(live, state, helper, piece, original, registered);
            HeroFormPersistence.Arm(live, state, helper, piece, original, registered, command.Automatic);
            // Keep owner, Players team and real sheet. Native transformation
            // manages despawn, turn queue, form record, card/trait rules and events.
            __result = transform.Invoke(helper, new[] { piece, id, Get(piece, "ownerId"), Get(piece, "team"),
                (object)true, Get(piece, "characterSheetId"), true, true, null }) as Il2CppSystem.Collections.IEnumerator;
            if (__result == null) throw new InvalidOperationException("Native transform coroutine unavailable.");
            Status = deckStatus;
            ctx.Log.LogInfo("BMF hero forms: native transformation coroutine returned for " + command.Definition.name);
            return false;
        }
        catch (Exception ex)
        {
            pending = null;
            HeroFormCards.CancelArmed();
            HeroFormPersistence.CancelArmed();
            Status = "Hero form rejected: " + ex.GetBaseException().Message;
            ctx?.Log.LogWarning("BMF " + Status);
            return true;
        }
    }
}
