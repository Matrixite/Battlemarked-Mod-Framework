using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace Battlemarked.ModFramework;

// The enemy deck is created in the authoritative simulation before the
// replacement is serialized. The HUD can still be showing the old hero's
// hand: wait for the matching replacement and its card IDs in the live
// presentation model, then run the game's own hand-refresh path once.
internal static class HeroFormHandRefresh
{
    private static ModuleContext ctx;
    private static int mainThread;
    private static MethodInfo currentPiece, tryGetCard;
    private static readonly Dictionary<Type, MethodInfo> presenters = new();
    private static RefreshTicket pending;

    private sealed class RefreshTicket
    {
        internal LevelEditor.Context Context;
        internal int PieceId;
        internal ulong Owner;
        internal string Sheet, FormKey;
        internal int[] CardIds;
        internal DateTime Expires;
    }

    private static Type Model(string name) =>
        Assembly.Load(new AssemblyName("Jade.GameModel")).GetType("Jade.GameModel." + name, true);
    private static object Get(object value, string name) => DebugWorkspace.ReadMember(value, name);
    private static object Call(object value, string name) => DebugWorkspace.Call(value, name);
    private static bool SameNative(object a, object b) => a is Il2CppObjectBase x && b is Il2CppObjectBase y &&
        x.Pointer != IntPtr.Zero && x.Pointer == y.Pointer;
    private static MethodInfo Exact(Type type, string name, bool isStatic, Type result, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != result)
            throw new InvalidOperationException("Hand refresh binding missing: " + type.Name + "." + name);
        return method;
    }

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            currentPiece = Exact(Model("BoardHelpers"), "TryGetCurrentPiece", true, typeof(bool),
                Model("Board"), Model("Piece").MakeByRefType());
            tryGetCard = Exact(Model("CardInventory"), "TryGetCard", false, typeof(bool),
                typeof(int), Model("CardState").MakeByRefType());
            var gameView = Assembly.Load(new AssemblyName("GameView"));
            var hook = typeof(HeroFormHandRefresh).GetMethod(nameof(AfterTick), BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var name in new[] { "Jade.Input.CardControl.NonVR.CardHandPresenterNonVR", "Jade.GameView.CardHandPresenterVR" })
            {
                try
                {
                    var presenter = gameView.GetType(name, true);
                    var tick = Exact(presenter, "Tick", false, typeof(void));
                    var refresh = Exact(presenter, "RefreshCardHandAfterLoad", false, typeof(void), Model("PlayerId"));
                    context.Patches.Patch(tick, postfix: new HarmonyMethod(hook));
                    presenters.Add(presenter, refresh);
                    context.Log.LogInfo("BMF hero form hand refresh: " + presenter.Name + " hook attached.");
                }
                catch (Exception ex)
                { context.Log.LogWarning("BMF hero form hand refresh: " + name + ": " + ex.GetBaseException().Message); }
            }
            if (presenters.Count != 0) ctx = context;
        }
        catch (Exception ex)
        {
            Stop();
            context.Log.LogWarning("BMF hero form hand refresh unavailable: " + ex.GetBaseException().Message);
        }
    }

    internal static void Stop()
    {
        pending = null; ctx = null; currentPiece = tryGetCard = null; presenters.Clear();
    }

    internal static void Queue(LevelEditor.Context context, object piece, ulong owner, string sheet, string formKey,
        IEnumerable<int> cardIds)
    {
        if (ctx == null || Environment.CurrentManagedThreadId != mainThread) return;
        // Do not throw into the inventory publication hook after it succeeds.
        try
        {
            pending = new RefreshTicket {
                Context = context, PieceId = (int)Call(Get(piece, "pieceId"), "get_Id"),
                Owner = owner, Sheet = sheet, FormKey = formKey, CardIds = cardIds.ToArray(),
                Expires = DateTime.UtcNow.AddSeconds(30)
            };
        }
        catch (Exception ex)
        { ctx.Log.LogWarning("BMF hero form hand refresh not queued: " + ex.GetBaseException().Message); }
    }

    private static void AfterTick(object __instance)
    {
        var ticket = pending;
        if (ticket == null || ctx == null || Environment.CurrentManagedThreadId != mainThread) return;
        try
        {
            if (DateTime.UtcNow > ticket.Expires || Plugin.Instance?.VerifiedGame != true ||
                Plugin.Instance?.HeroFormsEnabled != true)
            {
                pending = null;
                ctx.Log.LogWarning("BMF hero form hand refresh expired or disabled.");
                return;
            }
            if (!HouseRules.TryGetHeroFormsContext(out var live, out _)) return;
            if (!ticket.Context.SameSession(live)) { pending = null; return; }
            // The simulation and HUD can have different GameState instances.
            // Never pass a simulation Piece to the presenter. Use the HUD's
            // own state and current turn piece, once the native queue is idle.
            if (!SameNative(Get(__instance, "gameState"), live.State) ||
                Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy ||
                Call(__instance, "get_IsEnabled") is not bool enabled || !enabled) return;
            if (!presenters.TryGetValue(__instance.GetType(), out var refresh)) return;
            if (__instance.GetType().FullName == "Jade.Input.CardControl.NonVR.CardHandPresenterNonVR" &&
                (Call(Get(__instance, "requestProcessingSignalReader"), "get_IsProcessingRequests") is not bool processing || processing))
                return;
            var localOwner = Call(Get(__instance, "playerIdProvider"), "get_LocalPlayerID");
            if (Call(localOwner, "get_Id") is not ulong owner || owner != ticket.Owner) return;
            var args = new object[] { live.Board, null };
            // TryGetCurrentPiece returns the native turn-queue piece, not a
            // stale card-hand selection or the earlier despawned hero.
            if (currentPiece.Invoke(null, args) is not bool found || !found || args[1] == null) return;
            var piece = args[1];
            var type = Get(piece, "typeId");
            if (Call(Get(piece, "pieceId"), "get_Id") is not int id || id != ticket.PieceId ||
                Get(piece, "isTransformedPiece") is not bool transformed || !transformed ||
                Get(piece, "isPlayerControlled") is not bool controlled || !controlled ||
                Get(piece, "team")?.ToString() != "Players" ||
                Call(Get(piece, "ownerId"), "get_Id") is not ulong pieceOwner || pieceOwner != ticket.Owner ||
                Call(Get(piece, "characterSheetId"), "get_Id") as string != ticket.Sheet ||
                (Call(type, "get_Id") as string) + " | " + (Call(type, "get_Guid") as string) != ticket.FormKey)
                return;
            var hand = Call(Get(piece, "inventory"), "get_Hand");
            if (hand == null || ticket.CardIds.Length == 0) return;
            // Do not refresh early against a partially applied snapshot.
            // TryGetCard confirms each actual simulation-generated instance.
            foreach (var cardId in ticket.CardIds)
            {
                var cardArgs = new object[] { cardId, null };
                if (tryGetCard.Invoke(hand, cardArgs) is not bool hasCard || !hasCard || cardArgs[1] == null ||
                    Get(cardArgs[1], "source")?.ToString() != "Hand") return;
            }
            pending = null; // One refresh, no new game request or movement.
            refresh.Invoke(__instance, new[] { localOwner });
            ctx.Log.LogInfo("BMF hero form hand refresh: displayed replacement deck (" + ticket.CardIds.Length + " cards).");
        }
        catch (Exception ex)
        {
            pending = null;
            ctx.Log.LogWarning("BMF hero form hand refresh failed: " + ex.GetBaseException().Message);
        }
    }
}
