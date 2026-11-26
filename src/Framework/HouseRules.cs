using System.Reflection;
using System.Threading;
using HarmonyLib;
using BepInEx.Configuration;

namespace Battlemarked.ModFramework;

// Read-only by default; opt-in repeated AP bonus, local solo scope only.
internal sealed class HouseRules : BattlemarkedModule
{
    public override string Id => "org.battlemarked.houserules";
    public override string Name => "House rules (extra hero AP)";
    private ModuleContext ctx;
    private HeroHealthRule health;
    internal static bool NativeHealthArmed => running?.health?.Armed ?? false;
    internal static void ToggleNativeHealthArm()
    {
        var r = running;
        if (r?.health == null || Environment.CurrentManagedThreadId != r.mainThread) return;
        if (r.health.Armed) { r.health.Disarm(); return; }
        try
        {
            // The launcher normally has no captured model yet. Once any AP hook
            // has run, or a captured model is in an adventure, new arms are denied.
            bool observedAP = r.refillCalls != 0 || r.spendCalls != 0 ||
                Interlocked.Read(ref r.otherThread) != 0;
            var m = model;
            bool activeAdventure = m != null && (r.Flag(r.inGame, m) || r.Flag(r.playing, m));
            if (r.faulted) { r.health.RejectArm("House rules unavailable"); return; }
            r.health.ArmCurrentLaunch(observedAP, activeAdventure);
        }
        catch (Exception ex)
        {
            r.health.RejectArm("Could not verify pre-game state");
            r.ctx.Log.LogWarning("Health activation rejected: " + ex.Message);
        }
    }
    internal static int NextHealthPercent => running?.health?.NextPercent ?? 100;
    internal static bool CanConfigureHealth => running?.health?.Available ?? false;
    internal static string HealthStatus => running?.health?.Status ?? "Health rule unavailable";
    internal static string HealthCounts => running?.health?.Counts ?? "No health observations";
    internal static void ChangeHealthPercent(int delta) => running?.health?.ChangePercent(delta);
    private static HouseRules running;
    private static object model;
    private MethodInfo solo, local, multi, inGame, playing, host, activeHero, localHero, sheet, allowance, remaining;
    private MethodInfo stats, getStat, getMax, getObject, getValue, getObjectMax, getPermanentMax;
    private object apType;
    private MethodInfo setStat, setMax;
    private PropertyInfo pointer, statPointer;
    private ConfigEntry<int> nextBonus;
    private ConfigEntry<bool> nextAdjustExisting;
    private bool adjustExisting;
    private ConfigEntry<bool> nextRefundMode;
    private bool refundMode;
    private int refunds, refundResets, refundElevatedSkipped, partialRefundResets;
    private bool partialRefills;
    private int adjustedCaps, elevatedSkipped;
    private int bonus, writing, grants, restores, duplicateRefills, nestedSkipped, unresolved, openingGrants;
    private int nextId;
    private sealed class HeroState
    {
        internal int Id, Grants, Restores, Spent, SpentSinceGrant, BonusCycles;
        internal bool PendingRestore;
        internal int RefundsThisCycle, Refunds, RefundCycleCap;
    }
    // Key by native stat object, not managed wrapper; distinct native copies remain distinct.
    private readonly Dictionary<IntPtr, HeroState> heroes = new();
    internal static int NextBonus => running == null || running.nextBonus == null ? 0 : Math.Clamp(running.nextBonus.Value, 0, 3);
    internal static bool NextAdjustExisting => running?.nextAdjustExisting?.Value ?? false;
    internal static bool NextRefundMode => running?.nextRefundMode?.Value ?? false;
    internal static void ToggleRefundMode()
    {
        if (!CanConfigure || running.nextRefundMode == null) return;
        running.nextRefundMode.Value = !running.nextRefundMode.Value;
        running.ctx.Config.Save();
    }
    internal static void ToggleAdjustExisting()
    {
        if (!CanConfigure || running.nextAdjustExisting == null) return;
        running.nextAdjustExisting.Value = !running.nextAdjustExisting.Value;
        running.ctx.Config.Save();
    }
    internal static bool CanConfigure => running != null && running.setStat != null && running.setMax != null && running.pointer != null && running.statPointer != null && !running.faulted;
    internal static void ChangeBonus(int delta)
    {
        if (running == null || running.nextBonus == null || (!CanConfigure && delta != -3)) return;
        running.nextBonus.Value = Math.Clamp(NextBonus + delta, 0, 3);
        running.ctx.Config.Save();
    }
    private int mainThread, refillCalls, spendCalls, samplesTaken, skipped, errors;
    private long otherThread;
    private bool faulted, dirty;
    private string status = "not started", scene = "unknown";
    private DateTime lastWrite;
    private readonly List<string> samples = new();
    internal static string Status => running == null ? "Module not running" : running.status;
    internal static string Counts => running == null ? "No active module" : $"Grants: {running.grants} | Refunds: {running.refunds} | Errors: {running.errors}";
    private static MethodInfo Exact(Type type, string name, string signature)
    {
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters && Convert.ToHexString(m.Module.ResolveSignature(m.MetadataToken)) == signature).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Exact method unavailable: " + type.FullName + "." + name);
        return matches[0];
    }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(HouseRules).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    public override void OnStart(ModuleContext context)
    {
        ctx = context; mainThread = Environment.CurrentManagedThreadId;
        nextBonus = ctx.Config.Bind("HouseRules", "ExtraHeroAP044", 0, "Experimental repeated extra AP, 0-3. Restart to apply. Disposable local solo only.");
        bonus = Math.Clamp(nextBonus.Value, 0, 3);
        nextAdjustExisting = ctx.Config.Bind("HouseRules", "AdjustExistingAP046", false,
            "Disposable solo only: include consistent existing 3-5 AP caps with allowance 2 in the bonus calculation. Cap mode may overwrite legitimate modifiers; refund mode preserves the cap. Restart to apply.");
        nextRefundMode = ctx.Config.Bind("HouseRules", "RefundMode047", false,
            "Experimental alternative: refund single paid AP actions without changing max AP. Disposable solo adventures only. Restart to apply.");
        refundMode = nextRefundMode.Value;
        adjustExisting = nextAdjustExisting.Value;
        if (!ctx.IsVerifiedBuild(CoreLogicProbe.VerifiedHash)) { status = "Skipped: unverified game fingerprint"; Write(); return; }
        try
        {
            var logic = Assembly.Load(new AssemblyName("GameLogic"));
            var view = Assembly.Load(new AssemblyName("GameView"));
            var gameModel = Assembly.Load(new AssemblyName("Jade.GameModel"));
            var refill = Exact(logic.GetType("Jade.GameLogic.ActionPointUpdateHelper", true), "RefillActionPoints", "0001011269");
            var piece = refill.GetParameters()[0].ParameterType;
            if (piece.FullName != "Jade.GameModel.Piece") throw new InvalidOperationException("Unexpected piece type");
            var hero = view.GetType("Jade.GameView.UI.HeroSelectModel", true);
            solo = Exact(hero, "get_IsSinglePlayer", "200002");
            local = Exact(hero, "get_IsLocalGameMode", "200002");
            multi = Exact(hero, "get_IsMultiplayer", "200002");
            inGame = Exact(hero, "get_InGame", "200002");
            playing = Exact(hero, "get_GameStateIsPlayingLevel", "200002");
            host = Exact(hero, "get_LocalIsHost", "200002");
            activeHero = Exact(hero, "IsAnyActiveHero", "200102128335");
            localHero = Exact(hero, "IsActiveLocalPlayerCharacterSheet", "200102128335");
            sheet = Exact(piece, "get_characterSheetId", "2000128490");
            allowance = Exact(piece, "get_actionPointsPerTurn", "200008");
            remaining = Exact(gameModel.GetType("Jade.GameModel.PieceHelpers", true), "GetRemainingActionPoints", "00010812821C");
            if (activeHero.GetParameters()[0].ParameterType != sheet.ReturnType || localHero.GetParameters()[0].ParameterType != sheet.ReturnType || remaining.GetParameters()[0].ParameterType != piece)
                throw new InvalidOperationException("Incompatible hero or AP parameter types");
            stats = Exact(piece, "get_stats", "2000128310");
            var facade = stats.ReturnType;
            if (facade.FullName != "Jade.GameModel.StatsFacade") throw new InvalidOperationException("Unexpected stats type");
            getStat = Exact(facade, "GetStat", "2001081185C0");
            getMax = Exact(facade, "GetStatMax", "2001081185C0");
            getObject = Exact(facade, "GetStatObject", "20011283041185C0");
            var stat = getObject.ReturnType;
            if (stat.FullName != "Jade.GameModel.Stat") throw new InvalidOperationException("Unexpected stat type");
            var enumType = getStat.GetParameters()[0].ParameterType;
            // Metadata from the supplied Jade.GameModel.dll: Stats (plural) owns Type;
            // Stat (singular) is the object returned by GetStatObject, not the enum owner.
            if (!enumType.IsEnum || enumType.FullName != "Jade.GameModel.Stats+Type")
                throw new InvalidOperationException("Unexpected AP enum type: " + enumType.FullName + "; IsEnum=" + enumType.IsEnum);
            if (getMax.GetParameters()[0].ParameterType != enumType || getObject.GetParameters()[0].ParameterType != enumType)
                throw new InvalidOperationException("AP getter parameter type mismatch");
            if (!Enum.IsDefined(enumType, "ActionPoints"))
                throw new InvalidOperationException("ActionPoints not found in " + enumType.FullName + "; names=" + string.Join(",", Enum.GetNames(enumType)));
            apType = Enum.Parse(enumType, "ActionPoints");
            if (Convert.ToInt32(apType) != 1)
                throw new InvalidOperationException("Unexpected ActionPoints enum value: " + Convert.ToInt32(apType));
            ctx.Log.LogInfo("BMF AP enum verified: " + enumType.FullName + ".ActionPoints=1");
            getValue = Exact(stat, "get_Value", "20000C");
            getObjectMax = Exact(stat, "get_Max", "20000C");
            getPermanentMax = Exact(stat, "get_PermanentMax", "20000C");
            var spend = Exact(refill.DeclaringType, "TrySubtractActionPoints", "000202081269");
            if (spend.ReturnType != typeof(bool) || spend.GetParameters()[0].ParameterType != typeof(int) || spend.GetParameters()[1].ParameterType != piece) throw new InvalidOperationException("Unexpected AP spend signature");
            // The supplied Jade.GameModel.dll confirms both setters return Boolean and take (Stats.Type, Single).
            try
            {
                setStat = Exact(facade, "TrySetStat", "2002021185C00C");
                setMax = Exact(facade, "TrySetStatMax", "2002021185C00C");
                if (new[] { setStat, setMax }.Any(m => m.IsStatic || m.ReturnType != typeof(bool) || m.GetParameters()[0].ParameterType != enumType || m.GetParameters()[1].ParameterType != typeof(float))) throw new InvalidOperationException("Unexpected setter types");
                pointer = piece.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                statPointer = stat.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                if (pointer == null || pointer.PropertyType != typeof(IntPtr) || !pointer.CanRead || statPointer == null || statPointer.PropertyType != typeof(IntPtr) || !statPointer.CanRead) throw new InvalidOperationException("Native pointer unavailable");
            }
            catch (Exception ex) { setStat = setMax = null; pointer = statPointer = null; bonus = 0; ctx.Log.LogWarning("AP trial unavailable; read-only diagnostics: " + ex.Message); }
            try { health = new HeroHealthRule(ctx, piece, facade, stat, enumType); }
            catch (Exception ex) { ctx.Log.LogWarning("Health rule unavailable: " + ex.Message); }
            var teardown = Exact(hero, "TearDown", "200001");
            running = this; model = null;
            foreach (var method in new[] { solo, local, playing }) ctx.Patches.Patch(method, postfix: Hook(nameof(Capture)));
            ctx.Patches.Patch(teardown, prefix: Hook(nameof(ClearModel)), postfix: Hook(nameof(ClearModel)));
            ctx.Patches.Patch(refill, prefix: Hook(nameof(BeforeRefill)), postfix: Hook(nameof(AfterRefill)));
            ctx.Patches.Patch(spend, prefix: Hook(nameof(BeforeSpend)), postfix: Hook(nameof(AfterSpend)));
            partialRefills = ctx.Config.Bind("HouseRules", "PartialAPRefills052", true,
                "Reset refund budget on a verified same-cap partial-to-full native refill. Full-to-full remains guarded. Restart to apply.").Value;
            status = bonus == 0 ? "Read-only AP stat tracing" : refundMode ? $"Experimental +{bonus} AP via capped refunds" : $"Experimental +{bonus} AP on refill or first paid action";
            ctx.Log.LogInfo("BMF house rules: " + status);
        }
        catch (Exception ex)
        {
            running = null; model = null; faulted = true; status = "Skipped: " + ex.GetType().Name + ": " + ex.Message;
            try { ctx.Patches.UnpatchSelf(); } catch (Exception cleanup) { ctx.Log.LogWarning("House rules cleanup: " + cleanup.Message); }
            ctx.Log.LogWarning(status);
        }
        Write();
    }
    private static void Capture(object __instance)
    { var r = running; if (r != null && Environment.CurrentManagedThreadId == r.mainThread) model = __instance; }
    private static void ClearModel() { var r = running; if (r != null && Environment.CurrentManagedThreadId == r.mainThread) { r.LoseScope("session teardown"); model = null; } }
    private void LoseScope(string reason)
    {
        health?.ClearScope();
        int pending = heroes.Values.Count(h => h.PendingRestore);
        unresolved += pending;
        if (pending > 0) Sample($"Scope ended: {pending} outstanding cap(s) not restored: {reason}; discard test adventure");
        foreach (var h in heroes.Values) Sample($"Scope summary stat#{h.Id}: grants={h.Grants} restores={h.Restores} spent={h.Spent} bonusCycles={h.BonusCycles} refunds={h.Refunds} refundBudgetUsed={h.RefundsThisCycle}");
        heroes.Clear();
        dirty = true;
    }
    private bool Flag(MethodInfo method, object target, params object[] args) => method.Invoke(target, args) is bool b && b;
    private int Allowance(object piece) => (int)allowance.Invoke(piece, null);
    private int Remaining(object piece) => (int)remaining.Invoke(null, new[] { piece });
    private string Scope(object piece)
    {
        var m = model;
        if (m == null) return "no session model";
        if (!Flag(solo, m) || !Flag(local, m) || Flag(multi, m) || !Flag(inGame, m) || !Flag(playing, m) || !Flag(host, m)) return "not verified local solo gameplay";
        var id = sheet.Invoke(piece, null);
        if (id == null || !Flag(activeHero, m, id) || !Flag(localHero, m, id)) return "not verified local active hero";
        return "local solo hero";
    }
    private string Snapshot(object piece)
    {
        var facade = stats.Invoke(piece, null);
        if (facade == null) throw new InvalidOperationException("No stats facade");
        var args = new[] { apType };
        var obj = getObject.Invoke(facade, args);
        if (obj == null) throw new InvalidOperationException("No AP stat object");
        return $"allowance={Allowance(piece)} remaining={Remaining(piece)} stat={getStat.Invoke(facade, args)} statMax={getMax.Invoke(facade, args)} objectValue={getValue.Invoke(obj, null)} objectMax={getObjectMax.Invoke(obj, null)} permanentMax={getPermanentMax.Invoke(obj, null)}";
    }
    private HeroState Identify(object piece)
    {
        if (pointer == null || statPointer == null || (IntPtr)pointer.GetValue(piece) == IntPtr.Zero) return null;
        var f = stats.Invoke(piece, null);
        var obj = getObject.Invoke(f, new[] { apType });
        if (obj == null) return null;
        var key = (IntPtr)statPointer.GetValue(obj);
        if (key == IntPtr.Zero) return null;
        if (heroes.TryGetValue(key, out var h)) return h;
        if (heroes.Count >= 128) return null;
        h = new HeroState { Id = ++nextId };
        heroes.Add(key, h);
        return h;
    }
    private sealed class TraceState
    {
        internal HouseRules Owner;
        internal object Piece;
        internal HeroState Hero;
        internal string Before, Operation;
        internal int SpendAmount, BeforeRemaining, BeforeMax;
        internal bool VerifiedBefore;
    }
    private static TraceState Begin(object piece, string operation, bool isSpend)
    {
        var r = running;
        if (r == null || r.faulted || piece == null) return null;
        if (Environment.CurrentManagedThreadId != r.mainThread) { Interlocked.Increment(ref r.otherThread); return null; }
        // Native stat setters may invoke refill again; never grant or restore from such reentry.
        if (r.writing != 0) { r.nestedSkipped++; r.dirty = true; return null; }
        if (isSpend) r.spendCalls++; else r.refillCalls++;
        r.dirty = true;
        try
        {
            if (r.Scope(piece) != "local solo hero") { r.skipped++; return null; }
            // Health has its own fault handling and never changes the AP budget.
            if (r.health != null && r.health.Available)
            {
                r.writing++;
                try { r.health.Observe(piece, allowWrite: false); } finally { r.writing--; }
            }
            var h = r.Identify(piece);
            if (!isSpend && h != null && h.PendingRestore && r.Remaining(piece) <= 2)
                r.RestoreMaximum(piece, h, r.Remaining(piece));
            int beforeRemaining = r.Remaining(piece);
            int beforeMax = r.StatMax(piece);
            return new TraceState { Owner = r, Piece = piece, Hero = h, Operation = operation, Before = r.Snapshot(piece), BeforeRemaining = beforeRemaining, BeforeMax = beforeMax,
                VerifiedBefore = r.refundMode && r.bonus > 0 && ApRefundBudget.EligibleCap(beforeMax, r.adjustExisting) && beforeRemaining >= 0 && beforeRemaining <= beforeMax && r.ValuesMatch(piece, beforeRemaining, beforeMax) };
        }
        catch (Exception ex) { r.Fail("before " + operation, ex); return null; }
    }
    private static void End(TraceState state, string result)
    {
        if (state == null) return;
        var r = state.Owner;
        if (r.faulted) return;
        try
        {
            r.Sample($"scope=local solo hero | stat#{state.Hero?.Id ?? 0} | {state.Operation} | {result} | before {state.Before} | after {r.Snapshot(state.Piece)}");
            r.samplesTaken++;
        }
        catch (Exception ex) { r.Fail("after " + state.Operation, ex); }
        // Apply after a successful original AP operation: the game may clear modifiers
        // during the original call. Health failure remains independent of AP.
        if (!r.faulted && r.health != null && r.health.Available && result != "result=False")
        {
            try
            {
                if (r.Scope(state.Piece) == "local solo hero")
                {
                    r.writing++;
                    try { r.health.Observe(state.Piece); } finally { r.writing--; }
                }
            }
            catch (Exception ex) { r.ctx.Log.LogWarning("Health observation skipped: " + ex.Message); }
        }
    }
    private static void BeforeRefill(object __0, out TraceState __state) => __state = Begin(__0, "refill", false);
    private static void AfterRefill(TraceState __state)
    {
        End(__state, "returned");
        if (__state == null) return;
        var r = __state.Owner;
        if (r.faulted || r.bonus == 0 || __state.Hero == null) return;
        try
        {
            var h = __state.Hero;
            if (r.refundMode)
            {
                if (r.Scope(__state.Piece) != "local solo hero" || !ReferenceEquals(r.Identify(__state.Piece), h)) return;
                // Reset on a verified actual same-cap increase to full, never a full-to-full call.
                // A refund itself is never a reset; nested hooks are suppressed.
                int refillCap = __state.BeforeMax;
                if (ApRefundBudget.CanReset(__state.VerifiedBefore, __state.BeforeRemaining, refillCap, r.ValuesMatch(__state.Piece, refillCap, refillCap), r.partialRefills))
                {
                    h.RefundsThisCycle = 0; h.RefundCycleCap = refillCap; r.refundResets++;
                    if (__state.BeforeRemaining > 0) r.partialRefundResets++;
                    r.Sample($"Refund budget reset stat#{h.Id} before={__state.BeforeRemaining} cap={refillCap} budget={ApRefundBudget.Limit(r.bonus, refillCap, r.adjustExisting)}");
                }
                return;
            }
            if (r.Remaining(__state.Piece) > __state.BeforeRemaining) h.SpentSinceGrant = 0;
            if (h.PendingRestore || (h.Grants > 0 && __state.BeforeRemaining >= 2)) { r.duplicateRefills++; return; }
            int cap = r.StatMax(__state.Piece);
            if (cap == 2) r.TryGrant(__state.Piece, h);
            else if (cap >= 3 && cap <= 5 && r.ValuesMatch(__state.Piece, cap, cap))
            {
                if (r.adjustExisting) r.TryGrant(__state.Piece, h, cap, false, cap);
                else { r.elevatedSkipped++; r.Sample($"Skip stat#{h.Id}: existing AP cap={cap}; enable Adjust existing caps only for disposable test saves"); }
            }
        }
        catch (Exception ex) { r.Fail("AP grant", ex); }
    }
    private static void BeforeSpend(int __0, object __1, out TraceState __state)
    { __state = Begin(__1, "spend amount=" + __0, true); if (__state != null) __state.SpendAmount = __0; }
    private static void AfterSpend(bool __result, TraceState __state)
    {
        End(__state, "result=" + __result);
        if (__state == null) return;
        var r = __state.Owner;
        var h = __state.Hero;
        if (r.faulted || h == null || !__result || __state.SpendAmount <= 0) return;
        try
        {
            if (r.Scope(__state.Piece) != "local solo hero" || !ReferenceEquals(r.Identify(__state.Piece), h)) return;
            int current = r.Remaining(__state.Piece);
            // Count only a real matching deduction, not merely a true return value.
            if (__state.BeforeRemaining - current != __state.SpendAmount) return;
            int cap = r.StatMax(__state.Piece);
            if (r.refundMode)
            {
                h.Spent += __state.SpendAmount;
                if (r.bonus == 0) return;
                if (!ApRefundBudget.EligibleCap(cap, r.adjustExisting)) { r.refundElevatedSkipped++; return; }
                if (__state.BeforeMax != cap || (h.RefundCycleCap != 0 && h.RefundCycleCap != cap)) return;
                if (ApRefundBudget.CanRefund(r.bonus, h.RefundsThisCycle, cap, r.adjustExisting, __state.VerifiedBefore, __state.BeforeRemaining, __state.SpendAmount, current, r.ValuesMatch(__state.Piece, cap - 1, cap)))
                    r.TryRefund(__state.Piece, h, cap);
                return;
            }
            bool opening = r.bonus > 0 && h.Spent == 0 && h.Grants == 0 && !h.PendingRestore &&
                __state.SpendAmount == 1 && __state.BeforeRemaining == cap && current == cap - 1 &&
                (cap == 2 || (r.adjustExisting && cap >= 3 && cap <= 5));
            h.Spent += __state.SpendAmount;
            if (opening && r.ValuesMatch(__state.Piece, current, cap))
            {
                r.TryGrant(__state.Piece, h, current, true, cap);
                return;
            }
            if (h.Grants > 0)
            {
                h.SpentSinceGrant += __state.SpendAmount;
                if (current == 0 && h.SpentSinceGrant == 2 + r.bonus) h.BonusCycles++;
            }
            if (h.PendingRestore && current <= 2) r.RestoreMaximum(__state.Piece, h, current);
        }
        catch (Exception ex) { r.Fail("AP spend observation", ex); }
    }
    private void TryRefund(object piece, HeroState h, int cap)
    {
        if (!CanConfigure || !refundMode || bonus == 0 || Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h) || !ApRefundBudget.EligibleCap(cap, adjustExisting) || !ValuesMatch(piece, cap - 1, cap)) return;
        int limit = ApRefundBudget.Limit(bonus, cap, adjustExisting);
        if (h.RefundsThisCycle >= limit || (h.RefundCycleCap != 0 && h.RefundCycleCap != cap)) return;
        h.RefundCycleCap = cap;
        // Reserve the refund before writing; a failed or ambiguous write never retries.
        h.RefundsThisCycle++;
        writing++;
        try
        {
            var facade = stats.Invoke(piece, null);
            bool applied = Set(setStat, facade, (float)cap);
            bool verified = ValuesMatch(piece, cap, cap);
            Sample($"Refund stat#{h.Id} cap={cap} targetActions={2 + bonus} used={h.RefundsThisCycle}/{limit} applied={applied} verified={verified} | {Snapshot(piece)}");
            if (!applied || !verified) throw new InvalidOperationException("AP refund not verified; no further writes");
            h.Refunds++; refunds++;
        }
        finally { writing--; }
    }
    private int StatMax(object piece) => (int)getMax.Invoke(stats.Invoke(piece, null), new[] { apType });
    private bool ValuesMatch(object piece, int value, int max)
    {
        var f = stats.Invoke(piece, null); var args = new[] { apType }; var o = getObject.Invoke(f, args);
        return Allowance(piece) == 2 && Remaining(piece) == value && (int)getStat.Invoke(f, args) == value && (int)getMax.Invoke(f, args) == max &&
            (float)getValue.Invoke(o, null) == value && (float)getObjectMax.Invoke(o, null) == max && (float)getPermanentMax.Invoke(o, null) == max;
    }
    private bool Set(MethodInfo method, object facade, float value) => method.Invoke(facade, new object[] { apType, value }) is bool b && b;
    private void TryGrant(object piece, HeroState h, int baseline = 2, bool opening = false, int baselineMax = 2)
    {
        if (!CanConfigure || bonus == 0 || Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h) || !ValuesMatch(piece, baseline, baselineMax)) return;
        bool existing = baselineMax != 2;
        if (existing && (!adjustExisting || baselineMax < 3 || baselineMax > 5)) return;
        if (baseline != baselineMax - (opening ? 1 : 0)) return;
        var facade = stats.Invoke(piece, null);
        // Absolute target from the normal allowance, not the possibly persisted cap.
        int target = 2 + bonus - (opening ? 1 : 0);
        int targetMax = Math.Max(2, target);
        string source = (opening ? "opening-spend" : "refill") + (existing ? "-existing-cap" : "");
        Sample($"Grant stat#{h.Id} source={source} target={target}/{targetMax} baseline | {Snapshot(piece)}");
        h.PendingRestore = true;
        writing++;
        try
        {
            // Raise max before raising value; lower value before lowering max.
            bool applied = ApplyValues(piece, facade, target, targetMax);
            Sample($"Grant stat#{h.Id} applied={applied} | {Snapshot(piece)}");
            if (!applied || !ValuesMatch(piece, target, targetMax)) throw new InvalidOperationException("Bonus AP not retained");
            h.PendingRestore = targetMax > 2;
            h.Grants++; h.SpentSinceGrant = opening ? 1 : 0; grants++;
            if (existing) adjustedCaps++;
            if (opening) openingGrants++;
            dirty = true;
        }
        catch (Exception ex)
        {
            Sample("Grant failed: " + ex.GetType().Name + ": " + ex.Message);
            try
            {
                // On failure restore exactly the pre-write state, not an assumed normal cap.
                if (Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h))
                    throw new InvalidOperationException("Cannot verify AP object for rollback");
                bool rollback = ApplyValues(piece, facade, baseline, baselineMax) && ValuesMatch(piece, baseline, baselineMax);
                Sample($"Grant rollback stat#{h.Id} verified={rollback} | {Snapshot(piece)}");
                if (rollback) h.PendingRestore = false;
            }
            finally { faulted = true; errors++; status = "Bonus stopped: grant failed; discard test adventure"; dirty = true; }
        }
        finally { writing--; }
    }
    private bool ApplyValues(object piece, object facade, int targetValue, int targetMax)
    {
        int currentMax = StatMax(piece);
        if (targetMax > currentMax && !Set(setMax, facade, targetMax)) return false;
        if (Remaining(piece) != targetValue && !Set(setStat, facade, targetValue)) return false;
        if (targetMax < currentMax && !Set(setMax, facade, targetMax)) return false;
        return ValuesMatch(piece, targetValue, targetMax);
    }
    private void RestoreMaximum(object piece, HeroState h, int targetValue)
    {
        if (!h.PendingRestore) return;
        if (Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h)) throw new InvalidOperationException("Cannot verify AP stat for restoration");
        if (targetValue < 0 || targetValue > 2) throw new InvalidOperationException("Unexpected restoration value");
        var facade = stats.Invoke(piece, null);
        bool currentOk = true, maxOk = false;
        writing++;
        try
        {
            if (Remaining(piece) != targetValue) { try { currentOk = Set(setStat, facade, targetValue); } catch (Exception ex) { currentOk = false; Sample("Current rollback exception: " + ex.GetType().Name); } }
            try { maxOk = Set(setMax, facade, 2f); } catch (Exception ex) { Sample("Max rollback exception: " + ex.GetType().Name); }
            bool restored = ValuesMatch(piece, targetValue, 2);
            Sample($"Restore stat#{h.Id} current={currentOk} max={maxOk} verified={restored} | {Snapshot(piece)}");
            if (!restored) throw new InvalidOperationException("Rollback not verified; discard test adventure");
            h.PendingRestore = false; h.Restores++; restores++; dirty = true;
        }
        finally { writing--; }
    }
    private void Sample(string line) { if (samples.Count < 250) samples.Add(line); dirty = true; }
    private void Fail(string stage, Exception ex)
    { faulted = true; errors++; status = "Tracing stopped: " + stage; Sample(status + " | " + ex.GetType().Name); ctx.Log.LogWarning(status + ": " + ex.Message); }
    public override void OnSceneChanged(SceneInfo info) { LoseScope("scene changed"); scene = info.Name; model = null; dirty = true; }
    public override void OnUpdate() { if (dirty && (DateTime.UtcNow - lastWrite).TotalSeconds >= 5) Write(); health?.Tick(); }
    private void Write()
    {
        if (ctx == null) return;
        try
        {
            var lines = new List<string> { "BMF AP bonus probe (experimental; read-only when bonus=0)", "Framework=" + Framework.Version, "GameVersion=" + ctx.GameVersion, "GameAssemblySHA256=" + ctx.GameAssemblySha256, "Scene=" + scene, "UTC=" + DateTime.UtcNow.ToString("O"), "Status=" + status, $"ConfiguredBonus={bonus} NextBonus={nextBonus?.Value ?? 0} RefundMode={refundMode} NextRefundMode={nextRefundMode?.Value ?? false} Refunds={refunds} RefundResets={refundResets} PartialRefundResets={partialRefundResets} PartialRefills={partialRefills} RefundElevatedSkipped={refundElevatedSkipped} Grants={grants} OpeningGrants={openingGrants} AdjustExisting={adjustExisting} NextAdjustExisting={nextAdjustExisting?.Value ?? false} AdjustedCaps={adjustedCaps} ElevatedSkipped={elevatedSkipped} Restores={restores} Pending={heroes.Values.Count(h => h.PendingRestore)} Unresolved={unresolved} DuplicateRefills={duplicateRefills} NestedSkipped={nestedSkipped}", $"Refills={refillCalls} Spends={spendCalls} Samples={samplesTaken} Skipped={skipped} Errors={errors} OtherThread={Interlocked.Read(ref otherThread)}", "Current scope anonymous native AP stat objects (not necessarily distinct heroes):" };
            foreach (var h in heroes.Values) lines.Add($"stat#{h.Id} grants={h.Grants} restores={h.Restores} spent={h.Spent} bonusCycles={h.BonusCycles} refunds={h.Refunds} refundBudgetUsed={h.RefundsThisCycle} refundCycleCap={h.RefundCycleCap} pending={h.PendingRestore}");
            lines.Add("Samples (max 250; no player identifiers or raw pointers):");
            lines.AddRange(samples); File.WriteAllLines(Path.Combine(ctx.DataDirectory, "house-rules-probe.txt"), lines); dirty = false; lastWrite = DateTime.UtcNow;
        }
        catch (Exception ex) { ctx.Log.LogWarning("House rules report: " + ex.Message); }
    }
    public override void OnStop() { LoseScope("module stopped"); if (ReferenceEquals(running, this)) { running = null; model = null; } Write(); health?.Write(); }
}
