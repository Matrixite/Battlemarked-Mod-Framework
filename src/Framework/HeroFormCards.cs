using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Only a confirmed BMF enemy transformation can arm this one-use spawn hook.
// Create the enemy's deck on the new piece before native PieceSnapshot/view
// events; never alter its original hero, a registry asset, or an ordinary spawn.
internal static class HeroFormCards
{
    private const int MaxCards = 128, MaxAiNodes = 2048, MaxAiDepth = 32;
    private static ModuleContext ctx;
    private static int mainThread;
    private static MethodInfo inventorySetter, addToHand, nextCardId;
    private static ConstructorInfo inventoryCtor;
    private static Type pieceType, cardType, cardIdType, inventoryType, handType, cardStateType;
    private static Ticket armed;

    private sealed class Ticket
    {
        internal LevelEditor.Context Context;
        internal Il2CppObjectBase State, Board, Spawner, OriginalPiece, OriginalInventory;
        internal int SourceId;
        internal ulong Owner;
        internal string Sheet, FormKey, FormName;
        internal DateTime Expires;
        internal readonly List<object> CardIds = new();
    }

    private static Type Load(string assembly, string name) => Assembly.Load(new AssemblyName(assembly)).GetType(name, true);
    private static Type Model(string name) => Load("Jade.GameModel", "Jade.GameModel." + name);
    private static Type Definition(string name) => Load("GameDefinitions", "Jade.GameDefinitions." + name);
    private static Type Logic(string name) => Load("GameLogic", "Jade.GameLogic." + name);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    private static MethodInfo Exact(Type type, string name, bool isStatic, Type result, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != result)
            throw new InvalidOperationException("Enemy deck binding unavailable: " + type.Name + "." + name);
        return method;
    }
    private static bool SameNative(object a, object b) => a is Il2CppObjectBase x && b is Il2CppObjectBase y &&
        x.Pointer != IntPtr.Zero && x.Pointer == y.Pointer;
    private static Il2CppObjectBase Native(object value, Type expected)
    {
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero || !expected.IsInstanceOfType(value))
            throw new InvalidOperationException("Enemy deck native object unavailable: " + expected.Name);
        return native;
    }
    private static string Key(object id)
    {
        if (Call(id, "get_Id") is not string name || string.IsNullOrWhiteSpace(name) ||
            Call(id, "get_Guid") is not string guid || string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Registered card or piece identity missing.");
        return name + " | " + guid;
    }
    private static string Sheet(object piece) => Call(Get(piece, "characterSheetId"), "get_Id") as string;

    internal static string Status { get; private set; } = "Enemy card deck unavailable.";
    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            pieceType = Model("Piece");
            cardType = Definition("CardDefinition");
            cardIdType = Model("CardTypeId");
            cardStateType = Model("CardState");
            inventoryType = Model("Inventory");
            handType = Model("CardInventory");
            var statType = Model("Stat");
            inventoryCtor = inventoryType.GetConstructor(new[] { statType });
            inventorySetter = pieceType.GetProperty("inventory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);
            if (inventoryCtor == null || inventorySetter == null || inventorySetter.ReturnType != typeof(void) ||
                !inventorySetter.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { inventoryType }))
                throw new InvalidOperationException("Native inventory construction or piece inventory setter unavailable.");
            addToHand = Exact(handType, "TryAdd", false, typeof(bool), cardIdType, typeof(int), typeof(bool), cardStateType.MakeByRefType());
            nextCardId = Exact(Model("GameState"), "GenerateNewCardInstanceId", false, typeof(int));
            var spawn = Exact(Logic("PieceSpawner"), "SpawnPieceInternal", false, pieceType, Model("SpawnData"), typeof(int));
            var hook = typeof(HeroFormCards).GetMethod(nameof(ApplySpawnDeck), BindingFlags.NonPublic | BindingFlags.Static);
            context.Patches.Patch(spawn, postfix: new HarmonyMethod(hook));
            ctx = context;
            Status = "Enemy card deck hook ready.";
            ctx.Log.LogInfo("BMF hero form cards: native spawn-inventory hook attached.");
        }
        catch (Exception ex)
        {
            Stop();
            Status = "Enemy card deck unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF " + Status);
        }
    }

    internal static void Stop()
    {
        CancelArmed();
        ctx = null; inventoryCtor = null; inventorySetter = addToHand = nextCardId = null;
    }
    internal static void CancelArmed() { armed = null; }
    internal static bool Waiting
    {
        get
        {
            if (armed != null && DateTime.UtcNow > armed.Expires)
            {
                armed = null;
                Report("Enemy card deck spawn not observed; check the game and BepInEx log.", true);
            }
            return armed != null;
        }
    }

    private static void Report(string message, bool warning = false)
    {
        Status = message;
        HeroForms.ReportCardDeck(message);
        if (warning) ctx?.Log.LogWarning("BMF hero form cards: " + message);
        else ctx?.Log.LogInfo("BMF hero form cards: " + message);
    }

    // Validate the selected form's real registered deck before the original
    // hero is despawned. The catalog is the game's PieceDefinition.Cards,
    // not a name match over all loaded CardDefinitions or guessed abilities.
    internal static string Arm(LevelEditor.Context context, object state, object helper, object original, UObject definition)
    {
        if (Call(definition, "get_PlayerControllable") is not bool normalHero)
            throw new InvalidOperationException("Form control type unavailable.");
        if (normalHero) return "Native transform accepted. Check movement, cards and turn order."; // Native hero-to-hero forms stay unchanged.
        if (ctx == null || Plugin.Instance?.VerifiedGame != true || Plugin.Instance?.HeroFormsEnabled != true ||
            Environment.CurrentManagedThreadId != mainThread)
            throw new InvalidOperationException(Status);
        if (Waiting) throw new InvalidOperationException("Previous enemy card deck still pending.");
        var spawner = Native(Get(helper, "pieceSpawner"), Logic("PieceSpawner"));
        var nativeState = Native(state, Model("GameState"));
        var board = Native(Get(state, "board"), Model("Board"));
        if (!SameNative(Get(spawner, "gameState"), nativeState) || !SameNative(Get(helper, "gameState"), nativeState))
            throw new InvalidOperationException("Native enemy card registry session mismatch.");
        var registry = Native(Get(spawner, "cardRegistry"), Definition("CardRegistry"));
        var ticket = new Ticket {
            Context = context, State = nativeState, Board = board, Spawner = spawner,
            OriginalPiece = Native(original, pieceType), OriginalInventory = Native(Get(original, "inventory"), inventoryType),
            SourceId = (int)Call(Get(original, "pieceId"), "get_Id"), Owner = (ulong)Call(Get(original, "ownerId"), "get_Id"),
            Sheet = Sheet(original), FormKey = Key(Call(definition, "get_PieceTypeId")), FormName = definition.name,
            Expires = DateTime.UtcNow.AddSeconds(30)
        };
        if (ticket.SourceId < 0 || string.IsNullOrWhiteSpace(ticket.Sheet))
            throw new InvalidOperationException("Original hero identity unavailable for enemy deck.");
        var lookup = Exact(registry.GetType(), "GetCardDefinition", false, cardType, cardIdType);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(object value) => AddRegisteredCard(value, registry, lookup, ticket, seen);
        var cards = Call(definition, "get_Cards");
        if (cards != null)
        {
            var cardList = cards.GetType();
            if (Call(cards, "get_Count") is not int count || count < 0 || count > MaxCards)
                throw new InvalidOperationException("Enemy card list exceeds the verified scan bound.");
            var item = Exact(cardList, "get_Item", false, cardType, typeof(int));
            for (int i = 0; i < count; i++) Add(item.Invoke(cards, new object[] { i }));
        }
        ReadSequentialCards(definition, Add);
        if (ticket.CardIds.Count == 0)
        {
            ctx.Log.LogWarning("BMF hero form cards: " + definition.name + " has no registered deck; using native form inventory.");
            return "Native transform accepted; this creature has no registered cards.";
        }
        armed = ticket;
        ctx.Log.LogInfo("BMF hero form cards: armed " + ticket.CardIds.Count + " registered cards for " + ticket.FormName);
        return "Native transform accepted; preparing enemy card deck.";
    }

    private static void AddRegisteredCard(object value, object registry, MethodInfo lookup, Ticket ticket, HashSet<string> seen)
    {
        if (value == null) return;
        if (value is not UObject card || !cardType.IsInstanceOfType(card))
            throw new InvalidOperationException("Unexpected native enemy card definition.");
        if (card == null) return; // Native spawn ignores empty card entries.
        var id = Native(Call(card, "get_CardTypeId"), cardIdType);
        if (lookup.Invoke(registry, new object[] { id }) is not UObject registered || registered == null || registered != card)
            throw new InvalidOperationException("Unregistered enemy card: " + card.name);
        if (seen.Add(Key(id)))
        {
            if (ticket.CardIds.Count >= MaxCards) throw new InvalidOperationException("Enemy card deck exceeds the verified scan bound.");
            ticket.CardIds.Add(id);
        }
    }

    private static Il2CppObjectBase Cast(object value, Type type)
    {
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero) return null;
        if (type.IsInstanceOfType(value)) return native;
        var cast = LevelEditor.FindCast(type).Invoke(native, null) as Il2CppObjectBase;
        return cast != null && cast.Pointer == native.Pointer && type.IsInstanceOfType(cast) ? cast : null;
    }

    // Bosses can execute a fixed card without an inventory instance. Follow
    // only the verified sequential AI config's enabled actions, branches,
    // fallbacks, named sequences and pre-turn. No registry-wide asset search.
    private static void ReadSequentialCards(UObject definition, Action<object> add)
    {
        var behaviour = Cast(Call(definition, "get_AiBehaviourDefinition"), Definition("SequentialAIBehaviourDefinition"));
        if (behaviour == null) return;
        var config = Get(behaviour, "config");
        if (config == null) return;
        if (!Definition("SequentialAI.SequentialAIConfigDefinition").IsInstanceOfType(config))
            throw new InvalidOperationException("Unknown sequential AI card configuration.");
        var fixedCard = Definition("SequentialAI.TargetableActionUseSpecificCard");
        var targetable = Definition("SequentialAI.ActionTargetable");
        var fallback = Definition("SequentialAI.ActionTargetableWithFallback");
        var branches = new[] { "ActionConditionalByUserHealthPercentage", "ActionConditionalByUserStatusEffect",
            "ActionConditionalByUserFlag", "ActionRandom" }.Select(n => Definition("SequentialAI." + n)).ToArray();
        var visited = new HashSet<IntPtr>();
        int nodes = 0;
        void CountNode()
        {
            if (++nodes > MaxAiNodes) throw new InvalidOperationException("Enemy AI card scan exceeded its bound.");
        }
        void Each(object array, Action<object> visit)
        {
            if (array == null) return;
            if (array is System.Collections.IEnumerable items)
            {
                foreach (var child in items) { CountNode(); visit(child); }
                return;
            }
            // Il2CppInterop native arrays also expose Length and indexed
            // access; support either integer index width in the wrapper.
            long length = Convert.ToInt64(Get(array, "Length"));
            if (length < 0 || length > MaxAiNodes) throw new InvalidOperationException("Enemy AI sequence exceeds its scan bound.");
            var method = array.GetType().GetMethod("get_Item", new[] { typeof(int) }) ??
                array.GetType().GetMethod("get_Item", new[] { typeof(long) });
            if (method == null || method.ContainsGenericParameters || method.ReturnType == typeof(void))
                throw new InvalidOperationException("Native AI array indexer unavailable.");
            bool wide = method.GetParameters()[0].ParameterType == typeof(long);
            for (long i = 0; i < length; i++)
            { CountNode(); visit(method.Invoke(array, new[] { wide ? (object)i : (int)i })); }
        }
        void Target(object value)
        {
            CountNode();
            var action = Cast(value, fixedCard);
            if (action != null) add(Get(action, "card"));
        }
        void Walk(object value, int depth)
        {
            if (value == null) return;
            CountNode();
            if (depth > MaxAiDepth) throw new InvalidOperationException("Enemy AI card scan exceeded its depth bound.");
            if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero || !visited.Add(native.Pointer)) return;
            var withFallback = Cast(value, fallback);
            if (withFallback != null)
            { Target(Get(withFallback, "action")); Walk(Get(withFallback, "fallback"), depth + 1); return; }
            var withoutFallback = Cast(value, targetable);
            if (withoutFallback != null) { Target(Get(withoutFallback, "action")); return; }
            foreach (var branch in branches)
            {
                var conditional = Cast(value, branch);
                if (conditional == null) continue;
                Each(Get(conditional, "options"), option => { if (option != null) Walk(Get(option, "action"), depth + 1); });
                return;
            }
        }
        void Sequence(object value)
        {
            if (value == null) return;
            Each(Call(value, "get_Sequence"), row =>
            {
                if (row != null && Call(row, "get_Enabled") is bool enabled && enabled)
                    Walk(Call(row, "get_Action"), 0);
            });
        }
        Sequence(Call(config, "get_PreTurn"));
        Each(Call(config, "get_Turns"), Sequence);
        Each(Call(config, "get_NamedSequences"), Sequence);
    }

    private static void ApplySpawnDeck(object __instance, object __0, object __result)
    {
        var ticket = armed;
        if (ticket == null || ctx == null || Environment.CurrentManagedThreadId != mainThread) return;
        try
        {
            if (DateTime.UtcNow > ticket.Expires) { _ = Waiting; return; }
            // SpawnData contains the exact native replacement piece ID. Match
            // this as well as the authoritative spawner/state, form, owner
            // and character sheet. Never affect other spawns or native reverts.
            if (!SameNative(__instance, ticket.Spawner) ||
                !SameNative(Get(__instance, "gameState"), ticket.State) ||
                !SameNative(Get(ticket.State, "board"), ticket.Board) ||
                Get(__0, "isTransformedPiece") is not bool transformed || !transformed ||
                Get(__0, "isPlayerControlled") is not bool controlled || !controlled ||
                Get(__0, "team")?.ToString() != "Players" ||
                Get(__0, "pieceToReplace") is not Il2CppObjectBase replaced || replaced.Pointer == IntPtr.Zero ||
                (int)Call(replaced, "get_Id") != ticket.SourceId ||
                Key(Get(__0, "pieceTypeId")) != ticket.FormKey ||
                (ulong)Call(Get(__0, "owner"), "get_Id") != ticket.Owner ||
                Call(Get(__0, "characterSheetId"), "get_Id") as string != ticket.Sheet)
                return;

            armed = null; // Consume before any mutation; never retry.
            if (Plugin.Instance?.VerifiedGame != true ||
                !HouseRules.TryGetHeroFormsContext(out var live, out _) || !ticket.Context.SameSession(live))
                throw new InvalidOperationException("Enemy form session changed before its deck was created.");
            var piece = Native(__result, pieceType);
            var current = Native(Get(piece, "inventory"), inventoryType);
            if (SameNative(piece, ticket.OriginalPiece) || SameNative(current, ticket.OriginalInventory) ||
                Get(piece, "isTransformedPiece") is not bool isForm || !isForm ||
                Get(piece, "isPlayerControlled") is not bool player || !player ||
                Get(piece, "team")?.ToString() != "Players" ||
                (ulong)Call(Get(piece, "ownerId"), "get_Id") != ticket.Owner ||
                Sheet(piece) != ticket.Sheet || Key(Get(piece, "typeId")) != ticket.FormKey)
                throw new InvalidOperationException("Enemy form piece identity mismatch.");

            // Stage a completely separate native inventory with the same
            // capacity statistic. hand.TryAdd(ignoreCapacity: true) assigns
            // the actual enemy cards to Hand regardless of the default
            // AI/hand tags or the native capacity; no global asset is edited.
            // The original hero's inventory remains in its native reversible
            // transformation record. Do not publish a partial deck on error.
            var oldHand = Native(Call(current, "get_Hand"), handType);
            var capacity = Native(Get(oldHand, "capacityStat"), Model("Stat"));
            var staged = Native(inventoryCtor.Invoke(new object[] { capacity }), inventoryType);
            var hand = Native(Call(staged, "get_Hand"), handType);
            var instanceIds = new HashSet<int>();
            foreach (var id in ticket.CardIds)
            {
                if (nextCardId.Invoke(ticket.State, null) is not int instanceId || instanceId < 0 || !instanceIds.Add(instanceId))
                    throw new InvalidOperationException("Native card instance ID unavailable.");
                var args = new object[] { id, instanceId, true, null };
                if (addToHand.Invoke(hand, args) is not bool added || !added || args[3] == null ||
                    (int)Get(args[3], "cardInstanceId") != instanceId ||
                    Key(Get(args[3], "cardTypeId")) != Key(id) || Get(args[3], "source")?.ToString() != "Hand")
                    throw new InvalidOperationException("Could not create enemy card " + Key(id));
            }
            // SpawnPieceInternal has not yet returned to SpawnPieces, so the
            // next PieceSnapshot already contains the new hand and IDs.
            inventorySetter.Invoke(piece, new object[] { staged });
            Report("Enemy deck ready: " + ticket.CardIds.Count + " cards for " + ticket.FormName + ".");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(armed, ticket)) armed = null;
            Report("Enemy card deck unavailable; native inventory retained: " + ex.GetBaseException().Message, true);
        }
    }
}
