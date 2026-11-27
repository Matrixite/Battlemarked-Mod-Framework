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
    private static MethodInfo currentPiece, tryGetPiece, tryGetCard;
    private static readonly Dictionary<Type, (MethodInfo Refresh, MethodInfo Show)> presenters = new();
    private static readonly Dictionary<int, RefreshTicket> pending = new();

    private sealed class RefreshTicket
    {
        internal LevelEditor.Context Context;
        internal int PieceId, SourceId;
        internal object NativePieceId;
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
            tryGetPiece = Exact(Model("BoardHelpers"), "TryGetPiece", true, typeof(bool),
                Model("Board"), Model("PieceId"), Model("Piece").MakeByRefType());
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
                    var show = Exact(presenter, "ShowForPiece", false, typeof(void), Model("Piece"));
                    context.Patches.Patch(tick, postfix: new HarmonyMethod(hook));
                    presenters.Add(presenter, (refresh, show));
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
        pending.Clear(); ctx = null; currentPiece = tryGetPiece = tryGetCard = null; presenters.Clear();
    }

    internal static void Queue(LevelEditor.Context context, object piece, int sourceId, ulong owner, string sheet, string formKey,
        IEnumerable<int> cardIds)
    {
        if (ctx == null || Environment.CurrentManagedThreadId != mainThread) return;
        // Do not throw into the inventory publication hook after it succeeds.
        try
        {
            var nativeId = Get(piece, "pieceId");
            int id = (int)Call(nativeId, "get_Id");
            foreach (var old in pending.Values.ToArray())
                if (!old.Context.SameSession(context) || DateTime.UtcNow > old.Expires) pending.Remove(old.PieceId);
            pending[id] = new RefreshTicket {
                Context = context, PieceId = id, SourceId = sourceId, NativePieceId = nativeId,
                Owner = owner, Sheet = sheet, FormKey = formKey, CardIds = cardIds.ToArray(),
                Expires = DateTime.UtcNow.AddSeconds(30)
            };
        }
        catch (Exception ex)
        { ctx.Log.LogWarning("BMF hero form hand refresh not queued: " + ex.GetBaseException().Message); }
    }

    private static void AfterTick(object __instance)
    {
        if (pending.Count == 0 || ctx == null || Environment.CurrentManagedThreadId != mainThread) return;
        try
        {
            if (Plugin.Instance?.VerifiedGame != true || Plugin.Instance?.HeroFormsEnabled != true)
            { pending.Clear(); return; }
            foreach (var old in pending.Values.ToArray())
                if (DateTime.UtcNow > old.Expires)
                {
                    pending.Remove(old.PieceId);
                    ctx.Log.LogWarning("BMF hero form hand refresh expired for piece " + old.PieceId);
                }
            if (pending.Count == 0 || !HouseRules.TryGetHeroFormsContext(out var live, out _)) return;
            // The simulation and HUD can have different GameState instances.
            // Never pass a simulation Piece to the presenter.
            if (!SameNative(Get(__instance, "gameState"), live.State) ||
                Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy ||
                Call(__instance, "get_IsEnabled") is not bool enabled || !enabled) return;
            if (!presenters.TryGetValue(__instance.GetType(), out var methods)) return;
            var (refresh, show) = methods;
            if (__instance.GetType().FullName == "Jade.Input.CardControl.NonVR.CardHandPresenterNonVR" &&
                (Call(Get(__instance, "requestProcessingSignalReader"), "get_IsProcessingRequests") is not bool processing || processing))
                return;
            var localOwner = Call(Get(__instance, "playerIdProvider"), "get_LocalPlayerID");
            if (Call(localOwner, "get_Id") is not ulong owner) return;
            var currentArgs = new object[] { live.Board, null };
            if (currentPiece.Invoke(null, currentArgs) is not bool hasCurrent || !hasCurrent || currentArgs[1] == null) return;

            foreach (var ticket in pending.Values.ToArray())
            {
                if (!ticket.Context.SameSession(live)) { pending.Remove(ticket.PieceId); continue; }
                if (owner != ticket.Owner) continue;
                try
                {
                    // Find the replacement by its own ID, regardless of
                    // whose turn it is. Wait for the complete HUD snapshot.
                    var args = new object[] { live.Board, ticket.NativePieceId, null };
                    if (tryGetPiece.Invoke(null, args) is not bool found || !found || args[2] == null) continue;
                    var piece = args[2];
                    var type = Get(piece, "typeId");
                    if (Call(Get(piece, "pieceId"), "get_Id") is not int id || id != ticket.PieceId ||
                        Get(piece, "isTransformedPiece") is not bool transformed || !transformed ||
                        Get(piece, "isPlayerControlled") is not bool controlled || !controlled ||
                        Get(piece, "team")?.ToString() != "Players" ||
                        Call(Get(piece, "ownerId"), "get_Id") is not ulong pieceOwner || pieceOwner != ticket.Owner ||
                        Call(Get(piece, "characterSheetId"), "get_Id") as string != ticket.Sheet ||
                        (Call(type, "get_Id") as string) + " | " + (Call(type, "get_Guid") as string) != ticket.FormKey)
                        continue;
                    var hand = Call(Get(piece, "inventory"), "get_Hand");
                    if (hand == null || ticket.CardIds.Length == 0) continue;
                    bool complete = true;
                    foreach (var cardId in ticket.CardIds)
                    {
                        var cardArgs = new object[] { cardId, null };
                        if (tryGetCard.Invoke(hand, cardArgs) is not bool hasCard || !hasCard || cardArgs[1] == null ||
                            Get(cardArgs[1], "source")?.ToString() != "Hand") { complete = false; break; }
                    }
                    if (!complete) continue;

                    bool isCurrent = SameNative(currentArgs[1], piece);
                    // If a non-current hero's hand is already being viewed,
                    // rebuild that hand only. Otherwise normal selection
                    // will show its new inventory; don't switch heroes.
                    var shown = Get(__instance, "showingForPiece");
                    bool isShown = shown is Il2CppObjectBase native && native.Pointer != IntPtr.Zero &&
                        Call(Get(shown, "pieceId"), "get_Id") is int shownId &&
                        (shownId == ticket.SourceId || shownId == ticket.PieceId) &&
                        Call(Get(shown, "ownerId"), "get_Id") is ulong shownOwner && shownOwner == ticket.Owner &&
                        Call(Get(shown, "characterSheetId"), "get_Id") as string == ticket.Sheet;
                    pending.Remove(ticket.PieceId); // Consume only this hero's refresh.
                    if (isCurrent) refresh.Invoke(__instance, new[] { localOwner });
                    else if (isShown) show.Invoke(__instance, new[] { piece });
                    ctx.Log.LogInfo("BMF hero form hand refresh: replacement piece " + ticket.PieceId +
                        " ready (" + ticket.CardIds.Length + " cards; " + (isCurrent || isShown ? "refreshed display" : "available on selection") + ").");
                }
                catch (Exception ex)
                {
                    pending.Remove(ticket.PieceId);
                    ctx.Log.LogWarning("BMF hero form hand refresh failed for piece " + ticket.PieceId + ": " + ex.GetBaseException().Message);
                }
            }
        }
        catch (Exception ex)
        {
            pending.Clear();
            ctx.Log.LogWarning("BMF hero form hand refresh failed: " + ex.GetBaseException().Message);
        }
    }
}
