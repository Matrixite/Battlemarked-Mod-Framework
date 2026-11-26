using System.Reflection;
using BepInEx.Configuration;

namespace Battlemarked.ModFramework;

// One explicitly armed native-max trial. Caller verifies build, main thread and local solo hero.
// No repeated writes, no temporary modifiers, no getter patches. May persist in a disposable save.
internal sealed class HeroHealthRule
{
    private readonly ModuleContext ctx;
    private readonly ConfigEntry<int> nextPercent;
    private readonly NativeHealthArmRequest armRequest;
    private string armRequestStatus = "Not checked";
    private readonly int percent;
    private bool armed, consumed, ready, faulted, dirty = true;
    private MethodInfo usesHealth, isDowned, stats, getObject, getStat, getMax, value, max, permanent, raw, modifiers, setMax, setValue;
    private PropertyInfo pointer, count;
    private object healthType;
    private IntPtr trialKey;
    private int baseline, target, originalCurrent, observations, verifiedObservations, lost, errors, skipped;
    private bool applied, rolledBack, rollbackFailed;
    private DateTime lastWrite;
    private readonly List<string> samples = new();
    internal int NextPercent => HealthBudget.Normalize(nextPercent.Value);
    internal bool NextArmed => armRequest.Pending;
    internal bool Available => ready;
    internal string Status => !ready ? "Native health metadata unavailable" : faulted ? "Health trial stopped; see report" : applied ? $"Native trial: {baseline} -> {target} HP" : armed && !consumed ? $"Armed: {percent}% (one hero)" : NextArmed ? "Request saved; restart once" : "Read-only; arm for next launch";
    internal string Counts => $"Verified: {verifiedObservations} | Lost: {lost} | Errors: {errors}";
    internal void ChangePercent(int delta)
    {
        if (!Available) return;
        int selected = Math.Clamp(NextPercent + delta, 100, 200);
        if (selected == NextPercent) return;
        try
        {
            // Changing the target invalidates any previously authorized request.
            if (NextArmed)
            {
                armRequest.Cancel();
                armRequestStatus = "Request cancelled because target changed";
                Sample(armRequestStatus);
            }
            nextPercent.Value = selected;
            ctx.Config.Save();
        }
        catch (Exception ex) { ArmError(ex); }
        Write();
    }
    internal void ToggleArm()
    {
        if (!Available) return;
        try
        {
            if (NextArmed)
            {
                armRequest.Cancel();
                armRequestStatus = "Request cancelled";
            }
            else if (NextPercent > 100)
            {
                // The selected percentage remains a normal preference. Authorization
                // itself is an independent, verified request file, not a config bool.
                ctx.Config.Save();
                armRequest.Queue(NextPercent);
                armRequestStatus = "Request saved and verified; restart once";
            }
            else return;
            Sample(armRequestStatus);
            ctx.Log.LogInfo("BMF native health arm: " + armRequestStatus);
        }
        catch (Exception ex) { ArmError(ex); }
        Write();
    }
    private void ArmError(Exception ex)
    {
        errors++;
        armRequestStatus = "Arm request operation failed: " + ex.Message;
        Sample(armRequestStatus);
        ctx.Log.LogWarning("BMF native health arm: " + armRequestStatus);
    }
    internal void ClearScope()
    {
        if (trialKey != IntPtr.Zero)
        {
            Sample("Trial scope ended; no restoration attempted on departed native objects. Discard test adventure.");
            trialKey = IntPtr.Zero;
        }
        // The arm is never renewed by a scene change or teardown.
        dirty = true;
    }
    private static MethodInfo Exact(Type type, string name, string signature)
    {
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters &&
                Convert.ToHexString(m.Module.ResolveSignature(m.MetadataToken)) == signature).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Exact health method unavailable: " + type.FullName + "." + name);
        return matches[0];
    }
    internal HeroHealthRule(ModuleContext context, Type piece, Type facade, Type stat, Type enumType)
    {
        ctx = context;
        nextPercent = ctx.Config.Bind("HouseRules", "HeroMaxHealth050", 100,
            "Experimental max health target 100/125/150/175/200%. Native maximum trial requires separate one-shot arm. Fresh disposable solo only.");
        // Retire the old boolean. It is not an authorization in this build.
        var legacyArm = ctx.Config.Bind("HouseRules", "ArmNativeHealth054", false,
            "Retired v0.5.4 setting; ignored. Arm using the v0.5.5 House rules menu.");
        legacyArm.Value = false;
        ctx.Config.Save();
        percent = NextPercent;
        armRequest = new NativeHealthArmRequest(ctx.DataDirectory);
        try
        {
            armed = armRequest.Consume(percent, out armRequestStatus);
            Sample(armRequestStatus);
            ctx.Log.LogInfo("BMF native health arm: " + armRequestStatus);
        }
        catch (Exception ex)
        {
            armed = false;
            ArmError(ex);
            faulted = true; // Fail closed; do not touch native health if claim failed.
        }
        try
        {
            if (!enumType.IsEnum || enumType.FullName != "Jade.GameModel.Stats+Type" || !Enum.IsDefined(enumType, "Health"))
                throw new InvalidOperationException("Missing health enum");
            healthType = Enum.Parse(enumType, "Health");
            if (Convert.ToInt32(healthType) != 2) throw new InvalidOperationException("Unexpected Health enum value");
            usesHealth = Exact(piece, "get_usesHealth", "200002");
            isDowned = Exact(piece, "get_IsDowned", "200002");
            stats = Exact(piece, "get_stats", "2000128310");
            getObject = Exact(facade, "GetStatObject", "20011283041185C0");
            getStat = Exact(facade, "GetStat", "2001081185C0");
            getMax = Exact(facade, "GetStatMax", "2001081185C0");
            value = Exact(stat, "get_Value", "20000C");
            max = Exact(stat, "get_Max", "20000C");
            permanent = Exact(stat, "get_PermanentMax", "20000C");
            raw = Exact(stat, "get_maxValue", "20000C");
            modifiers = Exact(stat, "get_modifiers", "2000151221011282F8");
            setMax = Exact(facade, "TrySetStatMax", "2002021185C00C");
            setValue = Exact(facade, "TrySetStat", "2002021185C00C");
            foreach (var method in new[] { setMax, setValue })
            {
                var args = method.GetParameters();
                if (method.IsStatic || method.ReturnType != typeof(bool) || args.Length != 2 ||
                    args[0].ParameterType != enumType || args[1].ParameterType != typeof(float))
                    throw new InvalidOperationException("Unexpected health setter signature");
            }
            pointer = stat.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            count = modifiers.ReturnType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (pointer == null || pointer.PropertyType != typeof(IntPtr) || !pointer.CanRead ||
                count == null || count.PropertyType != typeof(int) || !count.CanRead)
                throw new InvalidOperationException("Native health identity or modifier count unavailable");
            ready = true;
            ctx.Log.LogInfo("BMF native health trial metadata verified; armed=" + armed);
        }
        catch (Exception ex) { Stop("metadata", ex); }
        Write();
    }
    private float F(MethodInfo method, object obj) => (float)method.Invoke(obj, null);
    private int I(MethodInfo method, object facade) => (int)method.Invoke(facade, new[] { healthType });
    private int ModifierCount(object stat)
    {
        var list = modifiers.Invoke(stat, null);
        return list == null ? -1 : (int)count.GetValue(list);
    }
    private string Snapshot(object facade, object stat) =>
        $"current={I(getStat, facade)} max={I(getMax, facade)} objectValue={F(value, stat)} objectMax={F(max, stat)} permanentMax={F(permanent, stat)} rawMax={F(raw, stat)} modifiers={ModifierCount(stat)}";
    private bool SameStat(object facade, IntPtr key)
    {
        var live = getObject.Invoke(facade, new[] { healthType });
        return live != null && (IntPtr)pointer.GetValue(live) == key;
    }
    private bool Matches(object facade, object stat, int current, int expectedMax)
        => I(getStat, facade) == current && F(value, stat) == current && MaxMatches(facade, stat, expectedMax);
    private bool MaxMatches(object facade, object stat, int expectedMax)
        => I(getMax, facade) == expectedMax && F(max, stat) == expectedMax &&
           F(permanent, stat) == expectedMax && F(raw, stat) == expectedMax && ModifierCount(stat) == 0;
    internal void Observe(object piece, bool allowWrite = true, string source = "AP")
    {
        if (!ready || faulted) return;
        try
        {
            if (usesHealth.Invoke(piece, null) is not true || isDowned.Invoke(piece, null) is not false) return;
            var facade = stats.Invoke(piece, null);
            if (facade == null) return;
            var stat = getObject.Invoke(facade, new[] { healthType });
            if (stat == null) return;
            var key = (IntPtr)pointer.GetValue(stat);
            if (key == IntPtr.Zero) return;
            if (consumed)
            {
                if (!applied || trialKey == IntPtr.Zero || trialKey != key) return;
                observations++;
                int current = I(getStat, facade);
                bool retained = MaxMatches(facade, stat, target) && current >= 0 && current <= target && F(value, stat) == current;
                if (retained) verifiedObservations++; else lost++;
                Sample($"trial observed source={source} retained={retained} | {Snapshot(facade, stat)}");
                return;
            }
            if (!armed || !allowWrite || percent == 100) return;
            int initial = I(getStat, facade), nativeMax = I(getMax, facade);
            if (!HealthBudget.CanNativeTrial(percent, initial, nativeMax, F(value, stat), F(max, stat), F(permanent, stat), F(raw, stat), ModifierCount(stat)))
            { skipped++; dirty = true; return; }
            // Reserve the one attempt before entering native code. Never scale a second object.
            consumed = true; trialKey = key; baseline = nativeMax; originalCurrent = initial;
            target = HealthBudget.Target(baseline, percent);
            Sample($"native trial target={target} | before {Snapshot(facade, stat)}");
            try
            {
                if (!SameStat(facade, key) || !Matches(facade, stat, initial, baseline))
                    throw new InvalidOperationException("Native health identity or baseline changed");
                bool wrote = setMax.Invoke(facade, new object[] { healthType, (float)target }) is true;
                if (!wrote || !SameStat(facade, key) || !MaxMatches(facade, stat, target))
                    throw new InvalidOperationException("Native maximum not verified");
                // Preserve pre-write HP if the native maximum setter changed it.
                if (I(getStat, facade) != initial && setValue.Invoke(facade, new object[] { healthType, (float)initial }) is not true)
                    throw new InvalidOperationException("Current HP preservation failed");
                if (!SameStat(facade, key) || !Matches(facade, stat, initial, target))
                    throw new InvalidOperationException("Native health trial verification failed");
                applied = true;
                Sample($"native trial verified=True | after {Snapshot(facade, stat)}");
            }
            catch (Exception ex)
            {
                try
                {
                    // Roll back only the same object with no other modifiers. No blind retries.
                    if (!SameStat(facade, key) || ModifierCount(stat) != 0)
                        throw new InvalidOperationException("Rollback identity/baseline unavailable");
                    bool maxOk = Matches(facade, stat, initial, baseline) ||
                        setMax.Invoke(facade, new object[] { healthType, (float)baseline }) is true;
                    bool hpOk = I(getStat, facade) == initial ||
                        setValue.Invoke(facade, new object[] { healthType, (float)initial }) is true;
                    rolledBack = maxOk && hpOk && SameStat(facade, key) && Matches(facade, stat, initial, baseline);
                    rollbackFailed = !rolledBack;
                    Sample($"native trial rollback verified={rolledBack} | {Snapshot(facade, stat)}");
                }
                catch (Exception rollback) { rollbackFailed = true; Sample("Rollback unverified: " + rollback.Message); }
                Stop("native trial", ex);
            }
        }
        catch (Exception ex) { Stop("observation", ex); }
    }
    private void Stop(string stage, Exception ex)
    {
        faulted = true; errors++;
        Sample($"Stopped: {stage}: {ex.GetType().Name}: {ex.Message}");
        ctx.Log.LogWarning("BMF health rule stopped: " + stage + ": " + ex.Message);
    }
    private void Sample(string line)
    {
        if (samples.Count < 200) samples.Add(line);
        dirty = true;
    }
    internal void Tick() { if (dirty && (DateTime.UtcNow - lastWrite).TotalSeconds >= 5) Write(); }
    internal void Write()
    {
        try
        {
            var lines = new List<string> { "BMF hero maximum health probe (one-shot native maximum trial)",
                "Framework=" + Framework.Version, "GameVersion=" + ctx.GameVersion, "GameAssemblySHA256=" + ctx.GameAssemblySha256,
                "UTC=" + DateTime.UtcNow.ToString("O"), "Status=" + Status,
                $"ConfiguredPercent={percent} NextPercent={NextPercent} Armed={armed} NextArmed={NextArmed} Consumed={consumed} Applied={applied} Baseline={baseline} Target={target} OriginalCurrent={originalCurrent} Observations={observations} VerifiedObservations={verifiedObservations} Lost={lost} Skipped={skipped} RolledBack={rolledBack} RollbackFailed={rollbackFailed} Errors={errors}",
                "ArmRequest=" + armRequestStatus,
                "One native health stat object only; no raw pointers. Native maximum may persist. Discard test adventure." };
            lines.AddRange(samples);
            File.WriteAllLines(Path.Combine(ctx.DataDirectory, "hero-health-probe.txt"), lines);
            lastWrite = DateTime.UtcNow; dirty = false;
        }
        catch (Exception ex) { ctx.Log.LogWarning("Health report: " + ex.Message); }
    }
}
