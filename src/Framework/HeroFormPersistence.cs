using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Let native level completion revert and record the original hero normally.
// Carry only confirmed BMF enemy forms, then use the ordinary validated BMF
// transformation request on the next playable board. Never edit native
// transformedPieces, recorded pieces, progression snapshots or saved decks.
internal static class HeroFormPersistence
{
    private static ModuleContext ctx;
    private static int mainThread;
    private static Type pieceType, pieceIdType, formIdType, recordType;
    private static MethodInfo tryGetPiece, isDead;
    private static ConstructorInfo pieceIdCtor, formIdCtor;
    private static readonly Dictionary<Type, MemberInfo> transitionProcessors = new();
    private static readonly Dictionary<IntPtr, Il2CppObjectBase> seenTransitions = new();
    private static readonly List<Form> active = new(), carry = new();
    private static Form armed;
    private static Stage source, destination;
    private static DateTime nextTick;
    internal static string Status { get; private set; } = "Carry-over unavailable.";

    private sealed class Stage
    {
        internal string Campaign, Scenario, Step;
        internal int Index;
        internal bool SameEvent(Stage other) => other != null &&
            Campaign == other.Campaign && Scenario == other.Scenario && Step == other.Step;
        internal bool SameLevel(Stage other) => SameEvent(other) && Index == other.Index;
    }

    private sealed class Form
    {
        internal Il2CppObjectBase State, Board, Spawner;
        internal string Session, Sheet, OriginalKey, FormKey, FormName, FormId, FormGuid;
        internal ulong Owner;
        internal int OriginalId, ReplacementId;
        internal Stage Stage;
        internal DateTime Expires;
        internal bool BlockedReported, Automatic;
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
    private static string Key(object id)
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
    private static string StageKey(object id)
    {
        var name = Call(id, "get_Id") as string;
        var guid = Call(id, "get_Guid") as string;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Native stage identity unavailable.");
        return name + " | " + guid;
    }
    private static Stage ReadStage(object state)
    {
        var selection = Get(state, "scenarioSelection");
        return new Stage {
            Campaign = StageKey(Get(selection, "campaignId")), Scenario = StageKey(Get(selection, "scenarioId")),
            Step = StageKey(Get(selection, "stepId")), Index = (int)Get(selection, "currentScenarioStepIndex")
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
            recordType = Model("TransformedPiece");
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
            ctx = context;
            Status = "Forms carry over; large forms need open space.";
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
        source = destination = null; nextTick = DateTime.MinValue;
        if (ctx != null) Status = "Forms carry over; large forms need open space.";
    }
    internal static void Stop()
    {
        ctx = null; Clear(); transitionProcessors.Clear();
        Status = "Carry-over unavailable.";
    }

    // Only a confirmed HeroForms request calls Arm. A separate spawn ticket
    // tracks enemy forms even when the definition has no registered cards.
    internal static void CancelArmed() { armed = null; }

    internal static void Arm(LevelEditor.Context live, object state, object helper, object original, UObject definition, bool automatic)
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
                State = nativeState, Board = board, Spawner = spawner,
                Session = Get(state, "sessionId") as string,
                Owner = (ulong)Call(Get(original, "ownerId"), "get_Id"), Sheet = Sheet(original),
                OriginalId = PieceId(original), OriginalKey = Key(Get(original, "typeId")),
                FormId = (string)Call(id, "get_Id"), FormGuid = (string)Call(id, "get_Guid"),
                FormKey = Key(id), FormName = definition.name, Stage = ReadStage(state),
                Expires = DateTime.UtcNow.AddSeconds(30), ReplacementId = -1, Automatic = automatic
            };
            if (spawner == null || spawner.Pointer == IntPtr.Zero || nativeState == null || nativeState.Pointer == IntPtr.Zero ||
                board == null || board.Pointer == IntPtr.Zero ||
                !SameNative(Get(spawner, "gameState"), state) ||
                !MatchesSession(form, live.State) || string.IsNullOrWhiteSpace(form.Sheet) ||
                form.OriginalId < 0 || !form.Stage.SameLevel(ReadStage(live.State))) return;
            armed = form;
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over arm: " + ex.GetBaseException().Message); }
    }

    private static bool MatchesSession(Form form, object state) =>
        !string.IsNullOrWhiteSpace(form.Session) && (Get(state, "sessionId") as string) == form.Session &&
        Call(Get(state, "hostPlayerId"), "get_Id") is ulong host && host == form.Owner &&
        Call(state, "IsInMultiplayerContext") is bool multiplayer && !multiplayer;

    private static bool IsOwnedPiece(Form form, object piece, bool transformed, string key)
    {
        return piece is Il2CppObjectBase native && native.Pointer != IntPtr.Zero &&
            Get(piece, "isPlayerControlled") is bool controlled && controlled &&
            Get(piece, "isTransformedPiece") is bool isForm && isForm == transformed &&
            Get(piece, "team")?.ToString() == "Players" &&
            Call(Get(piece, "ownerId"), "get_Id") is ulong owner && owner == form.Owner &&
            Sheet(piece) == form.Sheet && Key(Get(piece, "typeId")) == key;
    }

    private static void TrackSpawn(object __instance, object __0, object __result)
    {
        var form = armed;
        if (!Active || form == null || DateTime.UtcNow > form.Expires) return;
        try
        {
            if (!SameNative(__instance, form.Spawner) || !SameNative(Get(__instance, "gameState"), form.State) ||
                !SameNative(Get(form.State, "board"), form.Board) ||
                Get(__0, "isTransformedPiece") is not bool transformed || !transformed ||
                Get(__0, "pieceToReplace") is not Il2CppObjectBase replaced || replaced.Pointer == IntPtr.Zero ||
                (int)Call(replaced, "get_Id") != form.OriginalId ||
                Key(Get(__0, "pieceTypeId")) != form.FormKey ||
                Call(Get(__0, "owner"), "get_Id") is not ulong owner || owner != form.Owner ||
                Call(Get(__0, "characterSheetId"), "get_Id") as string != form.Sheet)
                return;
            armed = null;
            if (!MatchesSession(form, form.State) || !IsOwnedPiece(form, __result, true, form.FormKey)) return;
            form.ReplacementId = PieceId(__result);
            if (form.ReplacementId < 0) return;
            for (int i = active.Count - 1; i >= 0; i--)
                if (active[i].Session != form.Session ||
                    (active[i].Owner == form.Owner && active[i].Sheet == form.Sheet)) active.RemoveAt(i);
            if (active.Count >= 16) throw new InvalidOperationException("Too many recorded local forms.");
            active.Add(form);
            if (form.Automatic) Status = "Restored " + form.FormName + " on the new level.";
            ctx.Log.LogInfo("BMF hero form carry-over: " + (form.Automatic ? "restored " : "recorded ") +
                form.FormName + " for piece " + form.ReplacementId + ".");
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over spawn: " + ex.GetBaseException().Message); }
    }

    private static object FindPiece(object board, int id)
    {
        var args = new object[] { board, pieceIdCtor.Invoke(new object[] { id }), null };
        return tryGetPiece.Invoke(null, args) is bool found && found ? args[2] : null;
    }

    private static bool HasNativeOriginal(Form form, object state)
    {
        var records = Get(state, "transformedPieces");
        if (Call(records, "get_Count") is not int count || count < 0 || count > 128) return false;
        var item = Exact(records.GetType(), "get_Item", false, recordType, typeof(int));
        for (int i = 0; i < count; i++)
        {
            var r = item.Invoke(records, new object[] { i });
            if (r == null || Call(Get(r, "replacementPieceId"), "get_Id") is not int id || id != form.ReplacementId) continue;
            var original = Get(r, "piece");
            return IsOwnedPiece(form, original, false, form.OriginalKey) && PieceId(original) == form.OriginalId;
        }
        return false;
    }

    // Both verified native success paths yield ForceRevertValidTransformations
    // first. Snapshot living tracked forms at the first MoveNext, not at
    // a generic scene change (which would also catch manual exits or deaths).
    private static void BeforeTransition(object __instance)
    {
        if (!Active || active.Count == 0 || __instance is not Il2CppObjectBase transition ||
            transition.Pointer == IntPtr.Zero || !transitionProcessors.TryGetValue(__instance.GetType(), out var member)) return;
        if (seenTransitions.Count >= 32) seenTransitions.Clear();
        if (seenTransitions.ContainsKey(transition.Pointer)) return;
        seenTransitions.Add(transition.Pointer, transition);
        try
        {
            var state = Get(Read(member, __instance), "gameState");
            var board = Get(state, "board");
            if (Call(board, "get_IsLoaded") is not bool loaded || !loaded) return;
            var stage = ReadStage(state);
            var found = new List<Form>();
            foreach (var form in active)
            {
                if (!SameNative(form.State, state) || !SameNative(form.Board, board) ||
                    !MatchesSession(form, state) || !form.Stage.SameLevel(stage)) continue;
                var piece = FindPiece(board, form.ReplacementId);
                if (!IsOwnedPiece(form, piece, true, form.FormKey) ||
                    isDead.Invoke(null, new[] { piece }) is not bool dead || dead ||
                    !HasNativeOriginal(form, state)) continue;
                found.Add(form);
            }
            if (found.Count == 0) return;
            // Replace old tickets for the same sheet, preserving separate
            // heroes. A native revert before this transition is not retained.
            if (carry.Count != 0 && (!MatchesSession(carry[0], state) || carry[0].Stage.Campaign != stage.Campaign))
                carry.Clear();
            foreach (var form in found)
            {
                for (int i = carry.Count - 1; i >= 0; i--)
                    if (carry[i].Sheet == form.Sheet && carry[i].Owner == form.Owner) carry.RemoveAt(i);
                // Retain only managed identities while the old board unloads.
                active.Remove(form);
                form.State = form.Board = form.Spawner = null;
                carry.Add(form);
            }
            source = stage; destination = null;
            Status = carry.Count + " enemy form(s) waiting for next level.";
            ctx.Log.LogInfo("BMF hero form carry-over: " + Status);
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over transition: " + ex.GetBaseException().Message); }
    }

    private static void OnStepLoaded(object __0)
    {
        if (!Active || carry.Count == 0 || source == null) return;
        try
        {
            var stage = new Stage {
                Campaign = StageKey(Call(__0, "get_CampaignId")), Scenario = StageKey(Call(__0, "get_ScenarioId")),
                Step = StageKey(Call(__0, "get_ScenarioStepId"))
            };
            if (stage.Campaign != source.Campaign) return;
            destination = stage;
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF hero form carry-over load event: " + ex.GetBaseException().Message); }
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
        var pieces = Get(Get(live.Board, "turnQueue"), "pieces");
        if (Call(pieces, "get_Count") is not int count || count < 0 || count > 4096) return null;
        var item = Exact(pieces.GetType(), "get_Item", false, pieceType, typeof(int));
        object target = null;
        for (int i = 0; i < count; i++)
        {
            var piece = item.Invoke(pieces, new object[] { i });
            if (piece == null || Get(piece, "isPlayerControlled") is not bool controlled || !controlled ||
                Get(piece, "team")?.ToString() != "Players" ||
                Call(Get(piece, "ownerId"), "get_Id") is not ulong owner || owner != form.Owner ||
                Sheet(piece) != form.Sheet) continue;
            if (target != null) return null;
            target = piece;
        }
        return target;
    }

    // Runs even when F10 is closed. Wait for scope, native load event,
    // matching destination, a current player turn and an idle local queue.
    // Queue one request at a time; a rejected dispatched command is not retried.
    internal static void Tick()
    {
        if (!Active || carry.Count == 0 || DateTime.UtcNow < nextTick) return;
        nextTick = DateTime.UtcNow.AddMilliseconds(500);
        try
        {
            if (!HouseRules.TryGetHeroFormsContext(out var live, out _)) return;
            var stage = ReadStage(live.State);
            if (!MatchesSession(carry[0], live.State) || source == null || stage.Campaign != source.Campaign)
            { Clear(); return; }
            if (destination == null || !stage.SameEvent(destination) || stage.SameLevel(source) ||
                HeroForms.Waiting || live.Registry == null ||
                Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy) return;
            for (int i = 0; i < carry.Count; i++)
            {
                var form = carry[i];
                var piece = FindHero(live, form);
                if (piece == null || !HouseRules.TryValidateLocalHero(live, piece, out _)) continue;
                // Never replace a different form or different character,
                // and never resurrect a fallen/downed hero.
                if (!IsOwnedPiece(form, piece, false, form.OriginalKey))
                { carry.Remove(form); i--; continue; }
                try { HeroForms.ValidatePiece(live.State, live.Board, piece); }
                catch { continue; } // Wait for a living hero's turn.
                var id = formIdCtor.Invoke(new object[] { form.FormId, form.FormGuid });
                var registered = Exact(live.Registry.GetType(), "GetPieceDefinition", false,
                    Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition"), formIdType)
                    .Invoke(live.Registry, new[] { id }) as UObject;
                if (registered == null || Key(Call(registered, "get_PieceTypeId")) != form.FormKey ||
                    Call(registered, "get_PlayerControllable") is not bool normalHero || normalHero)
                { carry.Remove(form); i--; continue; }
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
                carry.Remove(form);
                Status = carry.Count == 0 ? "Enemy form restoration queued." : carry.Count + " more enemy form(s) waiting.";
                ctx.Log.LogInfo("BMF hero form carry-over: queued " + form.FormName + " for " + form.Sheet + ".");
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
