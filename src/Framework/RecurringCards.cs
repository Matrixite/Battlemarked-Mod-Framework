using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Session-only per-instance choices. The authoritative card execution still
// uses the game's normal AP, effects, targeting and ViewEventCardUsed. Only
// the selected card's usage rule is substituted in its private coroutine.
internal static class RecurringCards
{
    private const int CooldownTurns = 1, MaxCards = 256;
    private static ModuleContext ctx;
    private static int mainThread, heroIndex, cardPage;
    private static bool faulted;
    private static DateTime nextScan;
    private static LevelEditor.Context selectionContext, viewContext;
    private static Type pieceType, definitionType, cardStateType;
    private static MethodInfo tryGetPiece, tryGetCard, registryLookup, dead;
    private static ConstructorInfo cooldownCtor;
    private static MemberInfo definitionMember, usageMember, executorMember;
    private static readonly HashSet<CardKey> selected = new();
    private static readonly Dictionary<IntPtr, Il2CppObjectBase> simulations = new();
    private static readonly List<HeroRow> heroes = new();
    private static CardKey? queued;
    private static string status = "Start a disposable local solo campaign.";

    private readonly record struct CardKey(int PieceId, ulong Owner, string Sheet, int InstanceId, string TypeKey);
    private sealed class CardRow
    {
        internal readonly CardKey Key;
        internal readonly string Name, Source;
        internal CardRow(CardKey key, string name, string source) { Key = key; Name = name; Source = source; }
    }
    private sealed class HeroRow
    {
        internal readonly int Id;
        internal readonly string Name;
        internal readonly List<CardRow> Cards;
        internal HeroRow(int id, string name, List<CardRow> cards) { Id = id; Name = name; Cards = cards; }
    }
    private sealed class RestoreState
    {
        internal object Enumerator, Original;
        internal UObject Clone;
        internal CardKey Key;
        internal bool Restored;
    }

    private static Type Load(string asm, string name) => Assembly.Load(new AssemblyName(asm)).GetType(name, true);
    private static Type Model(string name) => Load("Jade.GameModel", "Jade.GameModel." + name);
    private static Type Definition(string name) => Load("GameDefinitions", "Jade.GameDefinitions." + name);
    private static Type Logic(string name) => Load("GameLogic", "Jade.GameLogic." + name);
    private static object Get(object value, string name) => DebugWorkspace.ReadMember(value, name);
    private static object Call(object value, string name) => DebugWorkspace.Call(value, name);
    private static bool SameNative(object a, object b) => a is Il2CppObjectBase x && b is Il2CppObjectBase y &&
        x.Pointer != IntPtr.Zero && x.Pointer == y.Pointer;
    private static bool Active => ctx != null && !faulted && Plugin.Instance?.VerifiedGame == true &&
        Plugin.Instance?.RecurringCardsEnabled == true && Environment.CurrentManagedThreadId == mainThread;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static MethodInfo Exact(Type type, string name, bool isStatic, Type result, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != result)
            throw new InvalidOperationException("Recurring cards binding missing: " + type.Name + "." + name);
        return method;
    }
    private static Type MemberType(MemberInfo m) => m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;
    private static MemberInfo Member(Type type, string name, Type expected, bool writable = false)
    {
        MemberInfo m = type.GetProperty(name, Instance);
        m ??= type.GetField(name, Instance);
        if (m == null || MemberType(m) != expected ||
            (m is PropertyInfo p && (!p.CanRead || writable && !p.CanWrite)) ||
            (writable && m is FieldInfo f && f.IsInitOnly))
            throw new InvalidOperationException("Recurring cards member missing: " + type.Name + "." + name);
        return m;
    }
    private static object Read(MemberInfo m, object target) => m is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)m).GetValue(target);
    private static void Set(MemberInfo m, object target, object value)
    {
        if (m is PropertyInfo p) p.SetValue(target, value);
        else ((FieldInfo)m).SetValue(target, value);
    }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(RecurringCards).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            pieceType = Model("Piece");
            cardStateType = Model("CardState");
            definitionType = Definition("CardDefinition");
            var cardIdType = Model("CardTypeId");
            var executor = Logic("CardExecutor");
            var usageType = Definition("CardUsageTypeBase");
            var cooldownType = Definition("SetCooldownOnUse");
            cooldownCtor = cooldownType.GetConstructor(new[] { typeof(int) });
            if (cooldownCtor == null || !usageType.IsAssignableFrom(cooldownType) || !typeof(UObject).IsAssignableFrom(definitionType))
                throw new InvalidOperationException("Native recurring card usage type unavailable.");
            usageMember = Member(definitionType, "cardUsageType", usageType, true);
            // The native UseAndExecuteCard state machine allocates the
            // UpdateCardInstance enumerator directly (factory is inlined).
            // Patch its real MoveNext rather than the unused factory/getter.
            var stateMachines = executor.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.Name.Contains("UpdateCardInstance", StringComparison.Ordinal) &&
                    t.GetMethod("MoveNext", Instance, null, Type.EmptyTypes, null)?.ReturnType == typeof(bool)).ToArray();
            if (stateMachines.Length != 1) throw new InvalidOperationException("Native card update coroutine unavailable.");
            var machine = stateMachines[0];
            definitionMember = Member(machine, "definition", definitionType, true);
            _ = Member(machine, "sourcePiece", pieceType);
            _ = Member(machine, "cardInstanceId", typeof(int));
            _ = Member(machine, "cardTypeId", cardIdType);
            var execMembers = machine.GetMembers(Instance).Where(m => (m is FieldInfo || m is PropertyInfo) &&
                MemberType(m) == executor).ToArray();
            if (execMembers.Length != 1) throw new InvalidOperationException("Native card executor reference unavailable.");
            executorMember = execMembers[0];
            tryGetPiece = Exact(Model("BoardHelpers"), "TryGetPiece", true, typeof(bool), Model("Board"), Model("PieceId"), pieceType.MakeByRefType());
            tryGetCard = Exact(Model("CardInventory"), "TryGetCard", false, typeof(bool), typeof(int), cardStateType.MakeByRefType());
            dead = Exact(Model("PieceHelpers"), "IsDead", true, typeof(bool), pieceType);
            registryLookup = Exact(Definition("CardRegistry"), "GetCardDefinition", false, definitionType, cardIdType);
            var request = Load("Jade.Messaging", "Jade.Messaging.IRequest");
            var handle = Exact(Logic("RequestHandlerPlayCard"), "HandleRequest", false, typeof(Il2CppSystem.Collections.IEnumerator), request);
            var move = Exact(machine, "MoveNext", false, typeof(bool));
            context.Patches.Patch(handle, prefix: Hook(nameof(CapturePlayRequest)));
            context.Patches.Patch(move, prefix: Hook(nameof(BeforeCardUpdate)), finalizer: Hook(nameof(RestoreDefinition)));
            ctx = context;
            status = "Select a hero and individual cards.";
            context.Log.LogInfo("BMF recurring cards: native card update hook ready.");
        }
        catch (Exception ex)
        {
            Stop(); status = "Recurring cards unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF " + status);
        }
    }

    internal static void ClearChoices()
    {
        selected.Clear(); simulations.Clear(); selectionContext = viewContext = null;
        heroes.Clear(); queued = null; heroIndex = cardPage = 0; nextScan = DateTime.MinValue;
    }
    internal static void Stop() { ClearChoices(); ctx = null; faulted = false; }
    private static void SyncSession(LevelEditor.Context live)
    {
        if (selectionContext == null || !selectionContext.SameSession(live))
        {
            selected.Clear(); simulations.Clear(); selectionContext = live;
        }
    }
    private static string Key(object id)
    {
        if (Call(id, "get_Id") is not string name || string.IsNullOrWhiteSpace(name) ||
            Call(id, "get_Guid") is not string guid || string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Missing card identity.");
        return name + " | " + guid;
    }
    private static CardKey CardIdentity(object piece, int instanceId, object cardTypeId) => new(
        (int)Call(Get(piece, "pieceId"), "get_Id"), (ulong)Call(Get(piece, "ownerId"), "get_Id"),
        (string)Call(Get(piece, "characterSheetId"), "get_Id"), instanceId, Key(cardTypeId));

    private static bool Eligible(LevelEditor.Context live, object piece) =>
        piece is Il2CppObjectBase p && p.Pointer != IntPtr.Zero && pieceType.IsInstanceOfType(piece) &&
        Get(piece, "isPlayerControlled") is bool player && player && Get(piece, "team")?.ToString() == "Players" &&
        Call(Get(piece, "ownerId"), "get_Id") is ulong owner &&
        Call(Get(live.State, "hostPlayerId"), "get_Id") is ulong host && owner == host &&
        dead.Invoke(null, new[] { piece }) is bool isDead && !isDead &&
        HouseRules.TryValidateRecurringHero(live, piece, out _);

    private static IEnumerable<object> ListItems(object list, int max, Type element)
    {
        if (list == null || Call(list, "get_Count") is not int count || count < 0 || count > max)
            throw new InvalidOperationException("Native list missing or beyond safe scan bound.");
        var item = Exact(list.GetType(), "get_Item", false, element, typeof(int));
        for (int i = 0; i < count; i++) yield return item.Invoke(list, new object[] { i });
    }
    private static List<HeroRow> Scan(LevelEditor.Context live)
    {
        var result = new List<HeroRow>();
        foreach (var piece in ListItems(Get(Get(live.Board, "turnQueue"), "pieces"), 4096, pieceType))
        {
            if (!Eligible(live, piece)) continue;
            var id = (int)Call(Get(piece, "pieceId"), "get_Id");
            var name = (string)Call(Get(piece, "typeId"), "get_Id");
            var rows = new List<CardRow>();
            foreach (string source in new[] { "Hand", "Backpack" })
            {
                var inventory = Call(Get(piece, "inventory"), source == "Hand" ? "get_Hand" : "get_Backpack");
                foreach (var card in ListItems(Get(inventory, "cards"), MaxCards, cardStateType))
                {
                    if (card == null || Get(card, "cardInstanceId") is not int instanceId || instanceId < 0 ||
                        Get(card, "source")?.ToString() != source) continue;
                    var type = Get(card, "cardTypeId");
                    rows.Add(new CardRow(CardIdentity(piece, instanceId, type), (string)Call(type, "get_Id"), source));
                }
            }
            result.Add(new HeroRow(id, name, rows));
        }
        return result;
    }

    // GUI callbacks only queue a choice; re-read live inventories on Unity
    // update before changing our managed selection set.
    internal static void Tick(bool showing)
    {
        if (!Active) { if (selected.Count != 0 || heroes.Count != 0) ClearChoices(); return; }
        if (!showing) { queued = null; heroes.Clear(); viewContext = null; return; }
        if (queued == null && DateTime.UtcNow < nextScan) return;
        nextScan = DateTime.UtcNow.AddMilliseconds(400);
        var choice = queued; queued = null;
        try
        {
            if (!HouseRules.TryGetRecurringCardsContext(out var live, out var reason))
            { heroes.Clear(); viewContext = null; status = reason; return; }
            bool sameView = viewContext != null && viewContext.SameSession(live);
            SyncSession(live);
            var current = Scan(live);
            var keys = current.SelectMany(h => h.Cards).Select(c => c.Key).ToHashSet();
            selected.RemoveWhere(k => !keys.Contains(k));
            status = null;
            if (choice.HasValue)
            {
                if (!sameView || !keys.Contains(choice.Value)) status = "Hero or card changed; select again.";
                else if (Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy)
                    status = "Game busy; wait and select the card again.";
                else if (!selected.Remove(choice.Value)) selected.Add(choice.Value);
            }
            status ??= selected.Count + " selected card(s) | Recurs with one turn of cooldown.";
            int previousHero = heroIndex >= 0 && heroIndex < heroes.Count ? heroes[heroIndex].Id : -1;
            heroes.Clear(); heroes.AddRange(current); viewContext = live;
            int index = heroes.FindIndex(h => h.Id == previousHero);
            heroIndex = index >= 0 ? index : Math.Clamp(heroIndex, 0, Math.Max(0, heroes.Count - 1));
        }
        catch (Exception ex)
        { heroes.Clear(); viewContext = null; status = "Recurring cards: " + ex.GetBaseException().Message; ctx.Log.LogWarning(status); }
    }

    internal static void Draw(float x, float y, float width, float height)
    {
        void Label(float top, string value) => GUI.Label(new Rect(x + 15, y + top, width - 30, 23), value);
        if (ctx == null || faulted) { Label(95, status); return; }
        Label(93, "Recurring Cards | Select individual cards (current local solo board only)");
        Label(118, "Selected consumables stay in hand; 1 turn cooldown; normal AP cost.");
        if (GUI.Button(new Rect(x + width - 105, y + 144, 90, 25), "Refresh")) nextScan = DateTime.MinValue;
        if (heroes.Count == 0) { Label(149, "No eligible heroes found."); Label(178, status); return; }
        if (GUI.Button(new Rect(x + 15, y + 144, 45, 25), "<")) { heroIndex = Math.Max(0, heroIndex - 1); cardPage = 0; }
        if (GUI.Button(new Rect(x + 65, y + 144, 45, 25), ">")) { heroIndex = Math.Min(heroes.Count - 1, heroIndex + 1); cardPage = 0; }
        var hero = heroes[heroIndex];
        GUI.Label(new Rect(x + 120, y + 145, width - 235, 25), "Hero " + (heroIndex + 1) + "/" + heroes.Count + ": " + hero.Name);
        int rows = Math.Max(1, (int)((height - 240) / 29));
        int pages = Math.Max(1, (hero.Cards.Count + rows - 1) / rows);
        cardPage = Math.Clamp(cardPage, 0, pages - 1);
        for (int i = 0; i < rows && cardPage * rows + i < hero.Cards.Count; i++)
        {
            var card = hero.Cards[cardPage * rows + i]; float top = y + 177 + 29 * i;
            bool on = selected.Contains(card.Key);
            GUI.Label(new Rect(x + 15, top + 2, width - 165, 23), card.Name + " | " + card.Source + " #" + card.Key.InstanceId);
            if (GUI.Button(new Rect(x + width - 145, top, 130, 26), on ? "Recurring: ON" : "Make recurring"))
            { queued = card.Key; nextScan = DateTime.MinValue; }
        }
        if (GUI.Button(new Rect(x + 15, y + height - 57, 65, 25), "<")) cardPage = Math.Max(0, cardPage - 1);
        GUI.Label(new Rect(x + 90, y + height - 55, width - 180, 24), (cardPage + 1) + "/" + pages + " | " + hero.Cards.Count + " cards");
        if (GUI.Button(new Rect(x + width - 80, y + height - 57, 65, 25), ">")) cardPage = Math.Min(pages - 1, cardPage + 1);
        Label(height - 29, status);
    }

    private static void CapturePlayRequest(object __instance)
    {
        if (!Active || selected.Count == 0) return;
        try
        {
            if (!HouseRules.TryGetRecurringCardsContext(out var live, out _)) return;
            SyncSession(live);
            if (selected.Count == 0) return;
            var state = Get(__instance, "gameState") as Il2CppObjectBase;
            var registry = Get(__instance, "actionRegistry") as Il2CppObjectBase;
            if (state == null || registry == null || state.Pointer == IntPtr.Zero || registry.Pointer == IntPtr.Zero ||
                Get(state, "sessionId") is not string session || string.IsNullOrWhiteSpace(session) ||
                session != Get(live.State, "sessionId") as string ||
                Call(Get(state, "hostPlayerId"), "get_Id") is not ulong host ||
                Call(Get(live.State, "hostPlayerId"), "get_Id") is not ulong liveHost || host != liveHost) return;
            if (simulations.Count >= 8 && !simulations.ContainsKey(registry.Pointer)) simulations.Clear();
            simulations[registry.Pointer] = state;
        }
        catch (Exception ex) { ctx.Log.LogWarning("BMF recurring card request capture: " + ex.GetBaseException().Message); }
    }

    private static void BeforeCardUpdate(object __instance, out RestoreState __state)
    {
        __state = null;
        if (!Active || selected.Count == 0) return;
        UObject rawClone = null;
        try
        {
            if (!HouseRules.TryGetRecurringCardsContext(out var live, out _)) return;
            SyncSession(live);
            var piece = Get(__instance, "sourcePiece");
            if (piece == null || Get(__instance, "cardInstanceId") is not int instanceId || instanceId < 0) return;
            var cardTypeId = Get(__instance, "cardTypeId");
            var key = CardIdentity(piece, instanceId, cardTypeId);
            if (!selected.Contains(key) || !Eligible(live, piece)) return;
            var executor = Read(executorMember, __instance);
            var actionRegistry = Get(executor, "actionRegistry") as Il2CppObjectBase;
            if (actionRegistry == null || !simulations.TryGetValue(actionRegistry.Pointer, out var state) ||
                Get(state, "sessionId") as string != Get(live.State, "sessionId") as string) return;
            var args = new object[] { Get(state, "board"), Get(piece, "pieceId"), null };
            if (tryGetPiece.Invoke(null, args) is not bool found || !found || !SameNative(args[2], piece)) return;
            var hand = Call(Get(piece, "inventory"), "get_Hand");
            var cardArgs = new object[] { instanceId, null };
            if (tryGetCard.Invoke(hand, cardArgs) is not bool hasCard || !hasCard || cardArgs[1] == null ||
                Key(Get(cardArgs[1], "cardTypeId")) != key.TypeKey || Get(cardArgs[1], "source")?.ToString() != "Hand" ||
                Call(cardArgs[1], "get_IsOnCooldown") is not bool cooling || cooling) return;
            // Existing recurring/immediately-reusable cards keep their own native rules.
            if (Read(definitionMember, __instance) is not UObject original || original == null ||
                Call(original, "get_IsConsumable") is not bool consume || !consume ||
                Key(Call(original, "get_CardTypeId")) != key.TypeKey ||
                !SameNative(registryLookup.Invoke(Get(actionRegistry, "cardRegistry"), new[] { cardTypeId }), original)) return;
            // Native ScriptableObject clone; never change the registered asset.
            var created = UObject.Instantiate(original);
            if (created == null || SameNative(created, original))
                throw new InvalidOperationException("Native card clone was not a distinct object.");
            rawClone = created;
            var clone = LevelEditor.FindCast(definitionType).Invoke(rawClone, null) as UObject;
            if (clone == null || SameNative(clone, original) || !SameNative(clone, rawClone))
                throw new InvalidOperationException("Native card clone unavailable.");
            var usage = cooldownCtor.Invoke(new object[] { CooldownTurns });
            if (Call(usage, "get_TurnsOfCooldown") is not int turns || turns != CooldownTurns)
                throw new InvalidOperationException("Native cooldown creation failed.");
            Set(usageMember, clone, usage);
            if (Call(clone, "get_IsReplenishable") is not bool replenishable || !replenishable ||
                Call(original, "get_IsConsumable") is not bool stillConsumable || !stillConsumable)
                throw new InvalidOperationException("Native recurring card clone validation failed.");
            __state = new RestoreState { Enumerator = __instance, Original = original, Clone = rawClone, Key = key };
            Set(definitionMember, __instance, clone);
            rawClone = null;
        }
        catch (Exception ex)
        {
            if (__state != null) { RestoreDefinition(null, __state); __state = null; rawClone = null; }
            if (rawClone != null) UObject.Destroy(rawClone);
            ctx.Log.LogWarning("BMF recurring card not applied: " + ex.GetBaseException().Message);
        }
    }

    // Native MoveNext yields only its card-state event. Restore the private
    // coroutine's definition before returning, even if native code throws.
    private static Exception RestoreDefinition(Exception __exception, RestoreState __state)
    {
        if (__state == null || __state.Restored) return __exception;
        try
        {
            Set(definitionMember, __state.Enumerator, __state.Original);
            __state.Restored = true;
            if (__state.Clone != null) UObject.Destroy(__state.Clone);
            if (__exception == null) ctx?.Log.LogInfo("BMF recurring card: native cooldown applied for piece " +
                __state.Key.PieceId + ", card #" + __state.Key.InstanceId + ".");
        }
        catch (Exception ex)
        {
            faulted = true; status = "Recurring cards stopped: " + ex.GetBaseException().Message;
            ctx?.Log.LogWarning("BMF " + status);
        }
        return __exception;
    }
}
