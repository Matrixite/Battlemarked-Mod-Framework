using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Let native level completion revert and record the original hero normally.
// Carry confirmed BMF enemy forms across local adventures by character sheet,
// then use the ordinary validated BMF request on the next playable board. Never edit native
// transformedPieces, recorded pieces, progression snapshots or saved decks.
internal static class HeroFormPersistence
{
    private static ModuleContext ctx;
    private static int mainThread;
    private static Type pieceType, pieceIdType, formIdType;
    private static MethodInfo tryGetPiece, isDead;
    private static ConstructorInfo pieceIdCtor, formIdCtor;
    private static readonly Dictionary<Type, MemberInfo> transitionProcessors = new();
    private static readonly Dictionary<IntPtr, Il2CppObjectBase> seenTransitions = new();
    private static readonly List<Form> active = new(), carry = new();
    private static Form armed;
    private static Stage destination;
    private static long loadSequence;
    private static DateTime nextTick;
    internal static string Status { get; private set; } = "Carry-over unavailable.";

    private sealed class Stage
    {
        internal string Campaign, Scenario, Step, Run;
        internal int Index;
        internal bool SameEvent(Stage other) => other != null &&
            Campaign == other.Campaign && Scenario == other.Scenario && Step == other.Step;
        internal bool SameLevel(Stage other) => SameEvent(other) && Index == other.Index && Run == other.Run;
    }

    private sealed class Form
    {
        internal Il2CppObjectBase State, Board, Spawner, Room;
        internal string Session, Sheet, Account, OriginalKey, FormKey, FormName, FormId, FormGuid, AttemptSession;
        internal ulong Owner;
        internal int SourceId, OriginalId, ReplacementId;
        internal Stage Stage, AttemptStage;
        internal long SourceLoadSequence;
        internal IntPtr ViewBoard;
        internal DateTime Expires;
        internal bool BlockedReported, Automatic, SheetVerified, VerifiedLive;
    }

    private static Type Load(string assembly, string name) => Assembly.Load(new AssemblyName(assembly)).GetType(name, true);
    private static Type Model(string name) => Load("Jade.GameModel", "Jade.GameModel." + name);
    private static Type Logic(string name) => Load("GameLogic", "Jade.GameLogic." + name);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    private static bool SameNative(object a, object b) => a is Il2CppObjectBase x && b is Il2CppObjectBase y &&
        x.Pointer != IntPtr.Zero && x.Pointer == y.Pointer;
    private static bool Active => ctx != null && Plugin.Instance?.VerifiedGame == true &&
        Plugin.Instance?.HeroFormsEnabled == true && Environment.CurrentManagedThreadId == mainThread;
    private static MethodInfo Exact(Type type, string name, bool isStatic, Type result, params Type[] args)
    {
        var m = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
        if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.ReturnType != result)
            throw new InvalidOperationException("Hero form carry-over binding missing: " + type.Name + "." + name);
        return m;
    }
    private static HarmonyMethod Hook(string name) =>
        new HarmonyMethod(typeof(HeroFormPersistence).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

    // Generated interop wrapper names for the captured <>4__this field may
    // vary; match the exact unique processor type, preferring its property.
    private static MemberInfo ProcessorMember(Type machine, Type processor)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        PropertyInfo property = null;
        foreach (var p in machine.GetProperties(flags))
        {
            if (p.PropertyType != processor || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
            if (property != null) throw new InvalidOperationException("Ambiguous transition processor property.");
            property = p;
        }
        if (property != null) return property;
        FieldInfo field = null;
        foreach (var f in machine.GetFields(flags))
        {
            if (f.FieldType != processor) continue;
            if (field != null) throw new InvalidOperationException("Ambiguous transition processor field.");
            field = f;
        }
        return field ?? throw new InvalidOperationException("Transition processor field missing.");
    }
    private static object Read(MemberInfo m, object obj) => m is PropertyInfo p ? p.GetValue(obj) : ((FieldInfo)m).GetValue(obj);
    internal static string Key(object id)
    {
        if (Call(id, "get_Id") is not string name || string.IsNullOrWhiteSpace(name) ||
            Call(id, "get_Guid") is not string guid || string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Native form or scenario identity missing.");
        return name + " | " + guid;
    }
    private static string Sheet(object piece) => Call(Get(piece, "characterSheetId"), "get_Id") as string;
    private static int PieceId(object piece) => (int)Call(Get(piece, "pieceId"), "get_Id");
    // Scene wrappers may use either the stable ID or GUID. Require at
    // least one and compare the complete pair, not a display name.
    internal static string StageKey(object id)
    {
        if (id == null) return "";
        var name = Call(id, "get_Id") as string;
        var guid = Call(id, "get_Guid") as string;
        return string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(guid) ? "" : name + " | " + guid;
    }
    private static Stage ReadStage(object state)
    {
        var selection = Get(state, "scenarioSelection");
        if (selection == null) return new Stage { Campaign = "", Scenario = "", Step = "", Run = "", Index = -1 };
        return new Stage {
            Campaign = StageKey(Get(selection, "campaignId")), Scenario = StageKey(Get(selection, "scenarioId")),
            Step = StageKey(Get(selection, "stepId")), Index = (int)Get(selection, "currentScenarioStepIndex"),
            Run = Get(selection, "currentScenarioRunId") as string ?? ""
        };
    }

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            pieceType = Model("Piece");
            pieceIdType = Model("PieceId");
            formIdType = Model("PieceTypeId");
            pieceIdCtor = pieceIdType.GetConstructor(new[] { typeof(int) });
            formIdCtor = formIdType.GetConstructor(new[] { typeof(string), typeof(string) });
            if (pieceIdCtor == null || formIdCtor == null) throw new InvalidOperationException("Native piece ID constructors unavailable.");
            tryGetPiece = Exact(Model("BoardHelpers"), "TryGetPiece", true, typeof(bool),
                Model("Board"), pieceIdType, pieceType.MakeByRefType());
            isDead = Exact(Model("PieceHelpers"), "IsDead", true, typeof(bool), pieceType);
            var spawn = Exact(Logic("PieceSpawner"), "SpawnPieceInternal", false, pieceType, Logic("SpawnData"), typeof(int));
            var processor = Logic("EndOfScenarioStepStateProcessor");
            foreach (var name in new[] { "<EndCurrentScenarioStepAndMoveToNext>d__16", "<CompleteScenario>d__17" })
            {
                Type machine = null;
                // Il2CppInterop can sanitize angle brackets in nested names.
                string core = name.Substring(1, name.IndexOf('>') - 1);
                string suffix = name.Substring(name.IndexOf('>') + 1);
                foreach (var nested in processor.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!nested.Name.Contains(core, StringComparison.Ordinal) || !nested.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    if (machine != null) throw new InvalidOperationException("Ambiguous native transition coroutine: " + name);
                    machine = nested;
                }
                if (machine == null) throw new InvalidOperationException("Native level transition coroutine unavailable: " + name);
                transitionProcessors.Add(machine, ProcessorMember(machine, processor));
                var move = Exact(machine, "MoveNext", false, typeof(bool));
                context.Patches.Patch(move, prefix: Hook(nameof(BeforeTransition)));
            }
            var loaded = Exact(Load("GameView", "Jade.GameView.ViewEventScenarioStepLoadedHandler"), "HandleEvent", false,
                typeof(Il2CppSystem.Collections.IEnumerator), Load("Jade.Messaging", "Jade.Messaging.ViewEventScenarioStepLoaded"));
            context.Patches.Patch(loaded, prefix: Hook(nameof(OnStepLoaded)));
            context.Patches.Patch(spawn, postfix: Hook(nameof(TrackSpawn)));
            // Both the simulation's explicit leave request and the local room
            // close path may precede native cleanup. Either can snapshot the
            // still-live forms. A later board-change check is the fallback.
            try
            {
                var leave = Exact(Logic("RequestHandlerLeaveGame"), "HandleRequest", false,
                    typeof(Il2CppSystem.Collections.IEnumerator), Load("Jade.Messaging", "Jade.Messaging.IRequest"));
                context.Patches.Patch(leave, prefix: Hook(nameof(BeforeLeaveGame)));
            }
            catch (Exception ex) { context.Log.LogWarning("BMF hero form leave request hook: " + ex.GetBaseException().Message); }
            try
            {
                var room = Load("Jade.Networking", "Jade.Networking.LocalNetworkRoom");
                var reasonType = Load("Jade.Networking", "Jade.Networking.INetworkRoom+LeaveReason");
                context.Patches.Patch(Exact(room, "Leave", false, typeof(void), reasonType), prefix: Hook(nameof(BeforeLocalLeave)));
            }
            catch (Exception ex) { context.Log.LogWarning("BMF hero form room leave hook: " + ex.GetBaseException().Message); }
            ctx = context;
            Status = "Forms remembered across local adventures; large forms need space.";
            ctx.Log.LogInfo("BMF hero form carry-over: native spawn and level transition hooks ready.");
        }
        catch (Exception ex)
        {
            Stop();
            Status = "Carry-over unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF hero forms: " + Status);
        }
    }

    internal static void Clear()
    {
        armed = null; active.Clear(); carry.Clear(); seenTransitions.Clear();
        destination = null; loadSequence = 0; nextTick = DateTime.MinValue;
        if (ctx != null) Status = "Forms remembered across local adventures; large forms need space.";
    }
    // Clearing a saved default also forgets earlier automatic carry-over
    // for that exact saved character; leave the current native piece alone.
    internal static void ForgetStartingChoice(string key)
    {
        if (key == null) return;
        if (armed != null && StartingKey(armed) == key) armed = null;
        for (int i = active.Count - 1; i >= 0; i--)
            if (StartingKey(active[i]) == key) active.RemoveAt(i);
        for (int i = carry.Count - 1; i >= 0; i--)
            if (StartingKey(carry[i]) == key) carry.RemoveAt(i);
    }
    private static string StartingKey(Form form) => form.SheetVerified ?
        StartingFormStore.CharacterKey(form.Sheet, form.OriginalKey, form.Account) : null;

    internal static void Stop()
    {
        ctx = null; Clear(); transitionProcessors.Clear();
        Status = "Carry-over unavailable.";
    }

    // Only a confirmed HeroForms request calls Arm. A separate spawn ticket
    // tracks enemy forms even when the definition has no registered cards.
    internal static void CancelArmed() { armed = null; }

    internal static void Arm(LevelEditor.Context live, object state, object helper, object current, object original, UObject definition, bool automatic)
    {
        if (!Active) return;
        armed = null;
        try
        {
            if (Call(definition, "get_PlayerControllable") is not bool normalHero || normalHero) return;
            var spawner = Get(helper, "pieceSpawner") as Il2CppObjectBase;
            var nativeState = state as Il2CppObjectBase;
            var board = Get(state, "board") as Il2CppObjectBase;
            var id = Call(definition, "get_PieceTypeId");
            var form = new Form {
                State = nativeState, Board = board, Spawner = spawner, Room = live.Room as Il2CppObjectBase,
                Session = Get(state, "sessionId") as string,
                Owner = (ulong)Call(Get(original, "ownerId"), "get_Id"), Sheet = Sheet(original),
                SourceId = PieceId(current), OriginalId = PieceId(original), OriginalKey = Key(Get(original, "typeId")),
                FormId = (string)Call(id, "get_Id"), FormGuid = (string)Call(id, "get_Guid"),
                FormKey = Key(id), FormName = definition.name, Stage = ReadStage(state),
                SourceLoadSequence = loadSequence, ViewBoard = live.Board.Pointer,
                Expires = DateTime.UtcNow.AddSeconds(30), ReplacementId = -1, Automatic = automatic
            };
            if (spawner == null || spawner.Pointer == IntPtr.Zero || nativeState == null || nativeState.Pointer == IntPtr.Zero ||
                board == null || board.Pointer == IntPtr.Zero || form.Room == null || form.Room.Pointer == IntPtr.Zero ||
                !SameNative(Get(spawner, "gameState"), state) ||
                !MatchesSession(form, live.State) || string.IsNullOrWhiteSpace(form.Sheet) ||
                form.SourceId < 0 || form.OriginalId < 0 || !form.Stage.SameLevel(ReadStage(live.State))) return;
            form.SheetVerified = TryReadHolder(state, original, form.OriginalKey, out form.Account);
            if (!form.SheetVerified) ctx.Log.LogWarning("BMF hero form carry-over: saved character sheet unavailable; same-session carry-over only.");
            armed = form;
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over arm: " + ex.GetBaseException().Message); }
    }

    private static bool MatchesSession(Form form, object state) =>
        !string.IsNullOrWhiteSpace(form.Session) && (Get(state, "sessionId") as string) == form.Session &&
        Call(Get(state, "hostPlayerId"), "get_Id") is ulong host && host == form.Owner &&
        Call(state, "IsInMultiplayerContext") is bool multiplayer && !multiplayer;

    private static bool IsOwnedPiece(Form form, object piece, bool transformed, string key, ulong? ownerOverride = null)
    {
        return piece is Il2CppObjectBase native && native.Pointer != IntPtr.Zero &&
            Get(piece, "isPlayerControlled") is bool controlled && controlled &&
            Get(piece, "isTransformedPiece") is bool isForm && isForm == transformed &&
            Get(piece, "team")?.ToString() == "Players" &&
            Call(Get(piece, "ownerId"), "get_Id") is ulong owner && owner == (ownerOverride ?? form.Owner) &&
            Sheet(piece) == form.Sheet && Key(Get(piece, "typeId")) == key;
    }

    private static void TrackSpawn(object __instance, object __0, object __result)
    {
        if (!Active) return;
        try
        {
            ObserveNativeReversion(__instance, __0, __result);
            var form = armed;
            if (form == null || DateTime.UtcNow > form.Expires) return;
            if (!SameNative(__instance, form.Spawner) || !SameNative(Get(__instance, "gameState"), form.State) ||
                !SameNative(Get(form.State, "board"), form.Board) ||
                Get(__0, "isTransformedPiece") is not bool transformed || !transformed ||
                Get(__0, "pieceToReplace") is not Il2CppObjectBase replaced || replaced.Pointer == IntPtr.Zero ||
                (int)Call(replaced, "get_Id") != form.SourceId ||
                Key(Get(__0, "pieceTypeId")) != form.FormKey ||
                Call(Get(__0, "owner"), "get_Id") is not ulong owner || owner != form.Owner ||
                Call(Get(__0, "characterSheetId"), "get_Id") as string != form.Sheet)
                return;
            armed = null;
            if (!MatchesSession(form, form.State) || !IsOwnedPiece(form, __result, true, form.FormKey)) return;
            form.ReplacementId = PieceId(__result);
            if (form.ReplacementId < 0) return;
            for (int i = active.Count - 1; i >= 0; i--)
                if (SameCharacterChoice(active[i], form)) active.RemoveAt(i);
            for (int i = carry.Count - 1; i >= 0; i--)
                if (SameCharacterChoice(carry[i], form)) carry.RemoveAt(i);
            if (active.Count >= 16) throw new InvalidOperationException("Too many recorded local forms.");
            active.Add(form);
            if (form.Automatic) Status = "Restored " + form.FormName + " on the new level.";
            ctx.Log.LogInfo("BMF hero form carry-over: " + (form.Automatic ? "restored " : "recorded ") +
                form.FormName + " for piece " + form.ReplacementId + ".");
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over spawn: " + ex.GetBaseException().Message); }
    }

    // Native Player.HasCharacterSheet resolves the actual selected character,
    // unlike a party slot or the room-scoped PlayerId. CharacterSheetId.GenerateNew
    // uses a GUID; a non-GUID ID additionally requires the same account.
    private static bool TryReadHolder(object state, object piece, string originalKey, out string account)
    {
        if (!TryReadCharacterSheet(state, piece, out var selected, out account)) return false;
        try { return Key(Get(selected, "pieceTypeId")) == originalKey; }
        catch { return false; }
    }

    internal static bool TryReadCharacterSheet(object state, object piece, out object selected, out string account)
    {
        selected = null;
        account = null;
        try
        {
            var playerId = Get(piece, "ownerId");
            var sheetId = Get(piece, "characterSheetId");
            if (Call(playerId, "get_Id") is not ulong owner ||
                Call(Get(state, "hostPlayerId"), "get_Id") is not ulong host || host != owner ||
                Call(state, "IsInMultiplayerContext") is not bool multiplayer || multiplayer ||
                string.IsNullOrWhiteSpace(Sheet(piece))) return false;
            var playerType = Model("Player");
            var sheetType = Model("CharacterSheet");
            var playerArgs = new object[] { playerId, null };
            if (Exact(Model("GameState"), "TryGetPlayerForPlayerId", false, typeof(bool), Model("PlayerId"), playerType.MakeByRefType())
                .Invoke(state, playerArgs) is not bool found || !found || playerArgs[1] == null) return false;
            var player = playerArgs[1];
            if (Call(Get(player, "playerId"), "get_Id") is not ulong playerOwner || playerOwner != owner) return false;
            var sheetArgs = new object[] { sheetId, null, false };
            if (Exact(playerType, "HasCharacterSheet", false, typeof(bool), sheetId.GetType(), sheetType.MakeByRefType(), typeof(bool))
                .Invoke(player, sheetArgs) is not bool hasSheet || !hasSheet || sheetArgs[1] == null) return false;
            var sheet = sheetArgs[1];
            if (Call(Get(sheet, "characterSheetId"), "get_Id") as string != Sheet(piece) ||
                Get(sheet, "isRetired") is not bool retired || retired) return false;
            selected = sheet;
            account = Get(player, "accountId") as string;
            return true;
        }
        catch { return false; }
    }

    private static bool StableSheet(string sheet) => Guid.TryParse(sheet, out var id) && id != Guid.Empty;
    private static bool SameCharacterChoice(Form a, Form b) =>
        a.Sheet == b.Sheet && a.OriginalKey == b.OriginalKey &&
        ((a.Session == b.Session && a.Owner == b.Owner) ||
            (a.SheetVerified && b.SheetVerified &&
                (StableSheet(a.Sheet) || (!string.IsNullOrWhiteSpace(a.Account) && a.Account == b.Account))));

    private static bool MatchesCharacter(Form form, object state, object piece)
    {
        if (Call(Get(state, "hostPlayerId"), "get_Id") is not ulong host ||
            !IsOwnedPiece(form, piece, false, form.OriginalKey, host)) return false;
        if (MatchesSession(form, state)) return true;
        return form.SheetVerified && TryReadHolder(state, piece, form.OriginalKey, out var account) &&
            (StableSheet(form.Sheet) || (!string.IsNullOrWhiteSpace(form.Account) && form.Account == account));
    }

    // A native reversion during ordinary play should not become a new
    // adventure choice. Successful transition/leave hooks first move the
    // live choice out of active, so their cleanup cannot erase it.
    private static void ObserveNativeReversion(object spawner, object spawn, object piece)
    {
        if (active.Count == 0 || Get(spawn, "isTransformedPiece") is not bool transformed || transformed ||
            Get(spawn, "pieceToReplace") is not Il2CppObjectBase replaced || replaced.Pointer == IntPtr.Zero ||
            Call(replaced, "get_Id") is not int replacedId) return;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var form = active[i];
            if (SameNative(spawner, form.Spawner) && SameNative(Get(spawner, "gameState"), form.State) &&
                SameNative(Get(form.State, "board"), form.Board) && MatchesSession(form, form.State) &&
                replacedId == form.ReplacementId && IsOwnedPiece(form, piece, false, form.OriginalKey))
                active.RemoveAt(i);
        }
    }

    private static object FindPiece(object board, int id)
    {
        var args = new object[] { board, pieceIdCtor.Invoke(new object[] { id }), null };
        return tryGetPiece.Invoke(null, args) is bool found && found ? args[2] : null;
    }

    // A repeated transform must keep the game's first original hero, not
    // the currently displayed creature. Read the unique native record in
    // the authoritative simulation; HUD state need not expose these records.
    // This lookup is also available when optional carry-over hooks cannot attach.
    internal static object RequireOriginalHero(object state, object piece)
    {
        if (Get(piece, "isTransformedPiece") is not bool transformed)
            throw new InvalidOperationException("Native transformation state missing.");
        if (!transformed) return piece;
        var original = GetNativeOriginal(state, PieceId(piece));
        if (original is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero ||
            !Model("Piece").IsInstanceOfType(original) || SameNative(piece, original) ||
            Get(original, "isTransformedPiece") is not bool originalTransformed || originalTransformed ||
            Get(original, "isPlayerControlled") is not bool controlled || !controlled ||
            Get(original, "isCreatureNonNpc") is not bool creature || !creature ||
            Get(original, "isPOI") is not bool poi || poi ||
            Get(original, "team")?.ToString() != "Players" ||
            Call(Get(original, "ownerId"), "get_Id") is not ulong owner ||
            Call(Get(piece, "ownerId"), "get_Id") is not ulong sourceOwner || owner != sourceOwner ||
            string.IsNullOrWhiteSpace(Sheet(piece)) || Sheet(original) != Sheet(piece) ||
            PieceId(original) < 0 || PieceId(original) == PieceId(piece))
            throw new InvalidOperationException("This form has no verified original hero record; switching was cancelled.");
        return original;
    }

    private static object GetNativeOriginal(object state, int replacementId)
    {
        var records = Get(state, "transformedPieces");
        if (records == null || Call(records, "get_Count") is not int count || count < 0 || count > 128) return null;
        var item = Exact(records.GetType(), "get_Item", false, Model("TransformedPiece"), typeof(int));
        object match = null;
        for (int i = 0; i < count; i++)
        {
            var r = item.Invoke(records, new object[] { i });
            if (r == null || Call(Get(r, "replacementPieceId"), "get_Id") is not int id || id != replacementId) continue;
            if (match != null) return null; // Ambiguous native record.
            match = r;
        }
        return match == null ? null : Get(match, "piece");
    }

    private static bool HasNativeOriginal(Form form, object state)
    {
        var original = GetNativeOriginal(state, form.ReplacementId);
        return IsOwnedPiece(form, original, false, form.OriginalKey) && PieceId(original) == form.OriginalId;
    }

    private static bool IsLiveForm(Form form, object state, object board)
    {
        return SameNative(form.State, state) && SameNative(form.Board, board) && MatchesSession(form, state) &&
            FindPiece(board, form.ReplacementId) is object piece && IsOwnedPiece(form, piece, true, form.FormKey) &&
            isDead.Invoke(null, new[] { piece }) is bool dead && !dead && HasNativeOriginal(form, state);
    }

    private static void MoveToCarry(Form form)
    {
        for (int i = carry.Count - 1; i >= 0; i--)
            if (SameCharacterChoice(carry[i], form)) carry.RemoveAt(i);
        active.Remove(form);
        form.State = form.Board = form.Spawner = null;
        form.Room = null;
        if (carry.Count < 64) carry.Add(form);
        Status = carry.Count + " remembered form(s) waiting for a playable adventure.";
    }

    // Both native success paths revert before progression recording. The
    // explicit leave request/room close path can also discard the old board.
    private static void SnapshotForms(object state)
    {
        var board = Get(state, "board");
        if (Call(board, "get_IsLoaded") is not bool loaded || !loaded) return;
        var stage = ReadStage(state);
        bool captured = false;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var form = active[i];
            if (!SameNative(form.State, state) || !SameNative(form.Board, board) ||
                !MatchesSession(form, state) || !form.Stage.SameLevel(stage)) continue;
            if (!IsLiveForm(form, state, board)) { active.RemoveAt(i); continue; }
            form.VerifiedLive = true;
            MoveToCarry(form);
            captured = true;
        }
        if (captured) ctx.Log.LogInfo("BMF hero form carry-over: " + Status);
    }

    private static void BeforeTransition(object __instance)
    {
        if (!Active || active.Count == 0 || __instance is not Il2CppObjectBase transition ||
            transition.Pointer == IntPtr.Zero || !transitionProcessors.TryGetValue(__instance.GetType(), out var member)) return;
        if (seenTransitions.Count >= 32) seenTransitions.Clear();
        if (seenTransitions.ContainsKey(transition.Pointer)) return;
        seenTransitions.Add(transition.Pointer, transition);
        try { SnapshotForms(Get(Read(member, __instance), "gameState")); }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over transition: " + ex.GetBaseException().Message); }
    }

    private static void BeforeLeaveGame(object __instance)
    {
        if (!Active || active.Count == 0) return;
        try { SnapshotForms(Get(__instance, "gameState")); }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over leave: " + ex.GetBaseException().Message); }
    }

    private static void BeforeLocalLeave(object __instance)
    {
        if (!Active || active.Count == 0) return;
        try
        {
            // The HUD may already have been torn down. The room was captured
            // by the verified local request; accept only that exact native
            // room and its matching local authoritative state.
            var states = new List<Il2CppObjectBase>();
            foreach (var form in active)
            {
                if (!SameNative(__instance, form.Room) || !MatchesSession(form, form.State)) continue;
                if (!states.Contains(form.State)) states.Add(form.State);
            }
            foreach (var state in states) SnapshotForms(state);
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over room leave: " + ex.GetBaseException().Message); }
    }

    private static void OnStepLoaded(object __0)
    {
        if (!Active || (carry.Count == 0 && active.Count == 0)) return;
        try
        {
            destination = new Stage {
                Campaign = StageKey(Call(__0, "get_CampaignId")), Scenario = StageKey(Call(__0, "get_ScenarioId")),
                Step = StageKey(Call(__0, "get_ScenarioStepId"))
            };
            loadSequence++;
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over load event: " + ex.GetBaseException().Message); }
    }

    private static bool IsNewPlayableBoard(Form form, LevelEditor.Context live, Stage stage)
    {
        var session = Get(live.State, "sessionId") as string;
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(form.Session)) return false;
        // New local room or new scenario run can retain the same character
        // while changing owner, campaign and step IDs. No old request runs.
        if (form.Session != session) return true;
        if (stage.SameLevel(form.Stage)) return false;
        // Same-room changes require evidence of a fresh playable board,
        // not merely an updated scenario selection while the old board loads.
        if (loadSequence > form.SourceLoadSequence && destination != null &&
            !string.IsNullOrWhiteSpace(destination.Step) && stage.SameEvent(destination)) return true;
        return form.ViewBoard != IntPtr.Zero && live.Board.Pointer != form.ViewBoard &&
            !string.IsNullOrWhiteSpace(stage.Run) && !string.IsNullOrWhiteSpace(form.Stage.Run) && stage.Run != form.Stage.Run;
    }

    private static bool AlreadyAttempted(Form form, LevelEditor.Context live, Stage stage) =>
        form.AttemptSession == Get(live.State, "sessionId") as string && form.AttemptStage != null &&
        form.AttemptStage.SameLevel(stage);

    // When leaving through another native route, remember forms last proven
    // live on a stable board. Never reapply to that same board. Ordinary
    // gameplay reversion and death remove the choice before a later exit.
    private static void ObserveActiveForms(LevelEditor.Context live, Stage stage)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var form = active[i];
            if (IsNewPlayableBoard(form, live, stage))
            {
                if (form.VerifiedLive) MoveToCarry(form);
                else active.RemoveAt(i);
                continue;
            }
            if (!MatchesSession(form, live.State) || !form.Stage.SameLevel(stage) ||
                !SameNative(Get(form.State, "board"), form.Board) ||
                Call(form.Board, "get_IsLoaded") is not bool loaded || !loaded) continue;
            if (IsLiveForm(form, form.State, form.Board)) form.VerifiedLive = true;
            else if (form.VerifiedLive || DateTime.UtcNow >= form.Expires) active.RemoveAt(i);
        }
    }

    internal static bool MatchesStage(object state, object liveState)
    {
        try
        {
            return ReadStage(state).SameLevel(ReadStage(liveState)) &&
                Call(Get(state, "board"), "get_IsLoaded") is bool loaded && loaded;
        }
        catch { return false; }
    }

    private static object FindHero(LevelEditor.Context live, Form form)
    {
        if (Call(Get(live.State, "hostPlayerId"), "get_Id") is not ulong host) return null;
        var pieces = Get(Get(live.Board, "turnQueue"), "pieces");
        if (Call(pieces, "get_Count") is not int count || count < 0 || count > 4096) return null;
        var item = Exact(pieces.GetType(), "get_Item", false, pieceType, typeof(int));
        object target = null;
        for (int i = 0; i < count; i++)
        {
            var piece = item.Invoke(pieces, new object[] { i });
            if (piece == null || Get(piece, "isPlayerControlled") is not bool controlled || !controlled ||
                Get(piece, "team")?.ToString() != "Players" ||
                Call(Get(piece, "ownerId"), "get_Id") is not ulong owner || owner != host ||
                Sheet(piece) != form.Sheet) continue;
            if (target != null) return null;
            target = piece;
        }
        return target;
    }

    // Runs even when F10 is closed. A fresh verified local board and matching
    // character are required; exact session/scene checks still apply to each
    // newly created native request.
    // Queue one request at a time; a rejected dispatch is not retried on that board.
    internal static void Tick()
    {
        if (!Active || (carry.Count == 0 && active.Count == 0) || DateTime.UtcNow < nextTick) return;
        nextTick = DateTime.UtcNow.AddMilliseconds(500);
        try
        {
            if (!HouseRules.TryGetHeroFormsContext(out var live, out _)) return;
            var stage = ReadStage(live.State);
            if (HeroForms.Waiting || Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy) return;
            ObserveActiveForms(live, stage);
            if (carry.Count == 0 || live.Registry == null) return;
            for (int i = 0; i < carry.Count; i++)
            {
                var form = carry[i];
                if (!IsNewPlayableBoard(form, live, stage) || AlreadyAttempted(form, live, stage)) continue;
                var piece = FindHero(live, form);
                if (piece == null || !HouseRules.TryValidateLocalHero(live, piece, out _)) continue;
                // Never replace a different form or different character,
                // and never resurrect a fallen/downed hero.
                if (!MatchesCharacter(form, live.State, piece)) continue;
                // A saved default supersedes the previous adventure's
                // temporary form (including when the default needs space).
                if (HeroStartingForms.PreferStartingForm(live, piece))
                { carry.RemoveAt(i); i--; continue; }
                try { HeroForms.ValidatePiece(live.State, live.Board, piece); }
                catch { continue; } // Wait for a living hero's turn.
                var id = formIdCtor.Invoke(new object[] { form.FormId, form.FormGuid });
                var registered = Exact(live.Registry.GetType(), "GetPieceDefinition", false,
                    Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition"), formIdType)
                    .Invoke(live.Registry, new[] { id }) as UObject;
                if (registered == null || Key(Call(registered, "get_PieceTypeId")) != form.FormKey ||
                    Call(registered, "get_PlayerControllable") is not bool normalHero || normalHero)
                { continue; } // A location may not register this creature yet.
                try { HeroForms.ValidateFormFit(live.Board, piece, registered); }
                catch (Exception ex)
                {
                    if (!form.BlockedReported)
                    {
                        form.BlockedReported = true;
                        Status = "Waiting for space for " + form.FormName + ".";
                        ctx.Log.LogWarning("BMF hero form carry-over: " + ex.GetBaseException().Message);
                    }
                    continue;
                }
                // Same native path as a confirmed manual transformation,
                // including the new enemy deck and hand refresh.
                HeroForms.Queue(live, piece, registered, id, automatic: true);
                // Only a matching native spawn consumes the choice. If this
                // request is rejected, keep it for a later adventure, but
                // do not flood the current board with repeat attempts.
                form.AttemptSession = Get(live.State, "sessionId") as string;
                form.AttemptStage = stage;
                if (carry.Contains(form)) Status = "Restoration requested for " + form.FormName + "; check log if not applied.";
                ctx.Log.LogInfo("BMF hero form carry-over: queued " + form.FormName + " for the matching character.");
                return;
            }
        }
        catch (Exception ex)
        {
            // A transient not-ready state is safe; throttle diagnostics.
            var message = "Carry-over waiting: " + ex.GetBaseException().Message;
            if (Status != message) ctx.Log.LogWarning("BMF hero form carry-over: " + message);
            Status = message;
            nextTick = DateTime.UtcNow.AddSeconds(2);
        }
    }
}
