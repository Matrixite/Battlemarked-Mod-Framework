using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace Battlemarked.ModFramework;

// A live override of the game's capacity queries, not a stat or save edit.
// Both native CardInventory.TryAdd overloads call get_Full; get_Capacity
// is also patched for callers which display/query the capacity property.
internal static class HandSize
{
    private static ModuleContext ctx;
    private static int mainThread;
    private static Type handType, pieceType, stateType;
    private static bool checking, warningLogged, capacityObserved, fullObserved;
    private static readonly Dictionary<IntPtr, Il2CppObjectBase> simulations = new();
    private static string status = "Native hand size hook unavailable; House Rules must be running.";

    internal static string Status => status;
    private static Type Model(string name) => Assembly.Load(new AssemblyName("Jade.GameModel")).GetType("Jade.GameModel." + name, true);
    private static Type Logic(string name) => Assembly.Load(new AssemblyName("GameLogic")).GetType("Jade.GameLogic." + name, true);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    private static bool SameNative(object a, object b) => a is Il2CppObjectBase x && b is Il2CppObjectBase y &&
        x.Pointer != IntPtr.Zero && x.Pointer == y.Pointer;

    private static MethodInfo Exact(Type type, string name, Type result, params Type[] args)
    {
        var m = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
        if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.ReturnType != result)
            throw new InvalidOperationException("Hand Size binding unavailable: " + type.Name + "." + name);
        return m;
    }
    private static HarmonyMethod Hook(string name) =>
        new HarmonyMethod(typeof(HandSize).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            handType = Model("CardInventory");
            pieceType = Model("Piece");
            stateType = Model("GameState");
            var capacity = Exact(handType, "get_Capacity", typeof(int));
            var full = Exact(handType, "get_Full", typeof(bool));
            var handler = Logic("RequestHandlerSpawnPiece");
            var ctor = handler.GetConstructor(new[] { Logic("PieceSpawner") });
            if (ctor == null) throw new InvalidOperationException("Native simulation capture unavailable.");
            // Capture even when the preference is off, so it can be enabled
            // mid-campaign. The capture alone never changes native behavior.
            context.Patches.Patch(ctor, postfix: Hook(nameof(CaptureSimulation)));
            context.Patches.Patch(capacity, postfix: Hook(nameof(AfterCapacity)));
            context.Patches.Patch(full, postfix: Hook(nameof(AfterFull)));
            ctx = context;
            status = "Ready; applies to verified local solo hero hands.";
            ctx.Log.LogInfo("BMF Hand Size: native capacity/full hooks ready.");
        }
        catch (Exception ex)
        {
            Stop();
            status = "Hand Size unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF " + status);
        }
    }

    internal static void Stop()
    {
        ctx = null; simulations.Clear(); checking = warningLogged = capacityObserved = fullObserved = false;
        status = "Native hand size hook unavailable; House Rules must be running.";
    }

    private static void CaptureSimulation(object __instance)
    {
        if (ctx == null || Environment.CurrentManagedThreadId != mainThread || Plugin.Instance?.VerifiedGame != true) return;
        try
        {
            var state = Get(Get(__instance, "pieceSpawner"), "gameState") as Il2CppObjectBase;
            if (state == null || state.Pointer == IntPtr.Zero || !stateType.IsInstanceOfType(state)) return;
            if (simulations.Count >= 8 && !simulations.ContainsKey(state.Pointer)) simulations.Clear();
            simulations[state.Pointer] = state;
        }
        catch (Exception ex) { Warn(ex); }
    }

    private static void Warn(Exception ex)
    {
        if (!warningLogged)
        {
            warningLogged = true;
            ctx?.Log.LogWarning("BMF Hand Size: " + ex.GetBaseException().Message);
        }
    }

    private static bool MatchHand(object board, object hand, ulong host, LevelEditor.Context live)
    {
        if (board == null || Call(board, "get_IsLoaded") is not bool loaded || !loaded) return false;
        var pieces = Get(Get(board, "turnQueue"), "pieces");
        if (Call(pieces, "get_Count") is not int count || count < 0 || count > 4096) return false;
        var getItem = Exact(pieces.GetType(), "get_Item", pieceType, typeof(int));
        for (int i = 0; i < count; i++)
        {
            var piece = getItem.Invoke(pieces, new object[] { i });
            if (piece == null || Get(piece, "isPlayerControlled") is not bool player || !player ||
                Get(piece, "team")?.ToString() != "Players" ||
                Call(Get(piece, "ownerId"), "get_Id") is not ulong owner || owner != host) continue;
            var inventory = Get(piece, "inventory");
            if (inventory == null || !SameNative(Call(inventory, "get_Hand"), hand)) continue;
            // Both the HUD and simulation copies must represent a current
            // hero, not a stale transformed/despawned piece.
            if (!SameNative(board, live.Board) && !HasMatchingLiveHero(live.Board, piece, host)) return false;
            return HouseRules.TryValidateHandSizeHero(live, piece, out _);
        }
        return false;
    }

    private static bool HasMatchingLiveHero(object board, object candidate, ulong host)
    {
        if (Call(Get(candidate, "pieceId"), "get_Id") is not int id ||
            Call(Get(candidate, "characterSheetId"), "get_Id") is not string sheet || string.IsNullOrWhiteSpace(sheet))
            return false;
        var pieces = Get(Get(board, "turnQueue"), "pieces");
        if (Call(pieces, "get_Count") is not int count || count < 0 || count > 4096) return false;
        var getItem = Exact(pieces.GetType(), "get_Item", pieceType, typeof(int));
        for (int i = 0; i < count; i++)
        {
            var piece = getItem.Invoke(pieces, new object[] { i });
            if (piece != null && Get(piece, "isPlayerControlled") is bool player && player &&
                Get(piece, "team")?.ToString() == "Players" &&
                Call(Get(piece, "pieceId"), "get_Id") is int currentId && currentId == id &&
                Call(Get(piece, "ownerId"), "get_Id") is ulong owner && owner == host &&
                (Call(Get(piece, "characterSheetId"), "get_Id") as string) == sheet) return true;
        }
        return false;
    }

    private static bool TryLimit(object hand, out int limit)
    {
        limit = 0;
        if (ctx == null || checking || Environment.CurrentManagedThreadId != mainThread ||
            Plugin.Instance?.VerifiedGame != true || Plugin.Instance?.HandSizeEnabled != true ||
            hand is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero || !handType.IsInstanceOfType(hand)) return false;
        checking = true;
        try
        {
            if (Call(hand, "get_Source")?.ToString() != "Hand" ||
                !HouseRules.TryGetHandSizeContext(out var live, out _) ||
                Get(live.State, "sessionId") is not string session || string.IsNullOrWhiteSpace(session) ||
                Call(Get(live.State, "hostPlayerId"), "get_Id") is not ulong host ||
                Call(live.State, "IsInMultiplayerContext") is not bool multi || multi) return false;

            bool match = MatchHand(live.Board, hand, host, live);
            if (!match)
            {
                // The HUD and the authoritative simulation can be different
                // native copies. Only inspect captured states in this same
                // verified session, not arbitrary CardInventory instances.
                foreach (var state in simulations.Values)
                {
                    try
                    {
                        if (SameNative(state, live.State) || state.Pointer == IntPtr.Zero ||
                            Get(state, "sessionId") is not string candidateSession ||
                            Call(Get(state, "hostPlayerId"), "get_Id") is not ulong candidateHost ||
                            !HandSizePolicy.MatchesSession(session, host, candidateSession, candidateHost) ||
                            Call(state, "IsInMultiplayerContext") is not bool candidateMulti || candidateMulti) continue;
                        if (MatchHand(Get(state, "board"), hand, host, live)) { match = true; break; }
                    }
                    catch (Exception ex) { Warn(ex); }
                }
            }
            if (!match) return false;
            limit = HandSizePolicy.Clamp(Plugin.Instance.MaxHandSize);
            return true;
        }
        catch (Exception ex) { Warn(ex); return false; }
        finally { checking = false; }
    }

    private static void AfterCapacity(object __instance, ref int __result)
    {
        if (!TryLimit(__instance, out int limit)) return;
        __result = limit;
        if (!capacityObserved)
        {
            capacityObserved = true;
            ctx?.Log.LogInfo("BMF Hand Size: verified hero capacity query overridden.");
        }
    }

    private static void AfterFull(object __instance, ref bool __result)
    {
        if (!TryLimit(__instance, out int limit)) return;
        try
        {
            if (Call(Get(__instance, "cards"), "get_Count") is int count && count >= 0)
            {
                __result = HandSizePolicy.IsFull(count, limit);
                if (!fullObserved)
                {
                    fullObserved = true;
                    status = "Verified hero hand-full check observed.";
                    ctx?.Log.LogInfo("BMF Hand Size: verified hero full query overridden at limit " + limit + ".");
                }
            }
        }
        catch (Exception ex) { Warn(ex); }
    }
}
