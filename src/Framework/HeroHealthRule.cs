using System.Reflection;
using BepInEx.Configuration;

namespace Battlemarked.ModFramework;

// Experimental health-only rule. Native temporary modifier, never direct max/value setters.
// Caller has already verified the exact build, local solo active hero, and Unity main thread.
internal sealed class HeroHealthRule
{
    private readonly ModuleContext ctx;
    private readonly ConfigEntry<int> nextPercent;
    private readonly int percent;
    private MethodInfo usesHealth, isDowned, stats, getObject, getStat, getMax, value, max, permanent, raw, modifiers, add;
    private PropertyInfo nativePointer, count;
    private ConstructorInfo modifierCtor;
    private object healthType, additive, temporary;
    private bool ready, faulted, dirty = true;
    private int observations, attempts, applied, errors, lost, nextId;
    private DateTime lastWrite;
    private readonly List<string> samples = new();
    private sealed class Entry
    {
        internal int Id, Target, Base, LastCurrent = -1, LastMax = -1;
        internal bool Attempted, Applied, Lost;
    }
    // Keep attempted identities for the process lifetime; do not retry a disappeared modifier.
    // This may conservatively skip a reused native address. No native wrappers retained.
    private readonly Dictionary<IntPtr, Entry> entries = new();
    internal int NextPercent => HealthBudget.Normalize(nextPercent.Value);
    internal bool Available => ready && !faulted;
    internal string Status => !ready ? "Health metadata unavailable" : faulted ? "Health rule stopped; see report" : percent == 100 ? "Normal health (read-only)" : $"Active: {percent}% max HP (temporary)";
    internal string Counts => $"Applied: {applied} | Attempts: {attempts} | Lost: {lost} | Errors: {errors}";
    internal void ChangePercent(int delta)
    {
        if (!Available) return;
        nextPercent.Value = Math.Clamp(NextPercent + delta, 100, 200);
        ctx.Config.Save();
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
    private static object VerifiedEnum(Type type, string name, int expected)
    {
        if (!type.IsEnum || !Enum.IsDefined(type, name)) throw new InvalidOperationException("Missing health enum member " + name);
        object result = Enum.Parse(type, name);
        if (Convert.ToInt32(result) != expected) throw new InvalidOperationException("Unexpected enum value " + name);
        return result;
    }
    internal HeroHealthRule(ModuleContext context, Type piece, Type facade, Type stat, Type enumType)
    {
        ctx = context;
        nextPercent = ctx.Config.Bind("HouseRules", "HeroMaxHealth050", 100,
            "Experimental max health 100/125/150/175/200%; temporary modifier; restart; fresh disposable local solo adventures only.");
        percent = NextPercent;
        try
        {
            if (enumType.FullName != "Jade.GameModel.Stats+Type") throw new InvalidOperationException("Unexpected stats enum");
            healthType = VerifiedEnum(enumType, "Health", 2);
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
            add = Exact(stat, "AddModifier", "2001011282F8");
            var modifier = add.GetParameters()[0].ParameterType;
            if (modifier.FullName != "Jade.GameModel.StatModifier" || add.ReturnType != typeof(void))
                throw new InvalidOperationException("Unexpected modifier type");
            var ctors = modifier.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(c => Convert.ToHexString(c.Module.ResolveSignature(c.MetadataToken)) == "2004011185C00C1185AC1185B0").ToArray();
            if (ctors.Length != 1) throw new InvalidOperationException("Exact modifier constructor unavailable");
            modifierCtor = ctors[0];
            var args = modifierCtor.GetParameters();
            if (args.Length != 4 || args[0].ParameterType != enumType || args[1].ParameterType != typeof(float)
                || args[2].ParameterType.Name != "StatModifierType" || args[3].ParameterType.Name != "StatModifierPermanency")
                throw new InvalidOperationException("Unexpected modifier constructor parameters");
            additive = VerifiedEnum(args[2].ParameterType, "AdditionAndSubtraction", 0);
            temporary = VerifiedEnum(args[3].ParameterType, "Temporary", 0);
            nativePointer = stat.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            count = modifiers.ReturnType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (nativePointer == null || nativePointer.PropertyType != typeof(IntPtr) || !nativePointer.CanRead
                || count == null || count.PropertyType != typeof(int) || !count.CanRead)
                throw new InvalidOperationException("Health pointer or modifier count unavailable");
            ready = true;
            ctx.Log.LogInfo("Health metadata verified: Health=2, additive=0, temporary=0");
        }
        catch (Exception ex) { Stop("metadata", ex); }
        Write();
    }

    private float F(MethodInfo method, object obj) => (float)method.Invoke(obj, null);
    private int I(MethodInfo method, object facade) => (int)method.Invoke(facade, new[] { healthType });
    private int ModifierCount(object stat)
    {
        var list = modifiers.Invoke(stat, null);
        // Unknown/null list is not proof that there are no modifiers.
        return list == null ? -1 : (int)count.GetValue(list);
    }
    private string Snapshot(object facade, object stat) =>
        $"current={I(getStat, facade)} max={I(getMax, facade)} objectValue={F(value, stat)} objectMax={F(max, stat)} permanentMax={F(permanent, stat)} rawMax={F(raw, stat)} modifiers={ModifierCount(stat)}";
    internal void Observe(object piece)
    {
        if (!Available) return;
        try
        {
            if (usesHealth.Invoke(piece, null) is not true || isDowned.Invoke(piece, null) is not false) return;
            var facade = stats.Invoke(piece, null);
            if (facade == null) return;
            var stat = getObject.Invoke(facade, new[] { healthType });
            if (stat == null) return;
            var key = (IntPtr)nativePointer.GetValue(stat);
            if (key == IntPtr.Zero) return;
            if (!entries.TryGetValue(key, out var e))
            {
                if (entries.Count >= 128) return;
                e = new Entry { Id = ++nextId };
                entries.Add(key, e);
            }
            observations++;
            int current = I(getStat, facade), effectiveMax = I(getMax, facade);
            if (current != e.LastCurrent || effectiveMax != e.LastMax)
            {
                Sample($"health#{e.Id} observed | {Snapshot(facade, stat)}");
                e.LastCurrent = current; e.LastMax = effectiveMax;
            }
            if (e.Applied && !e.Lost && (effectiveMax != e.Target || F(permanent, stat) != e.Base || F(raw, stat) != e.Base))
            {
                e.Lost = true; lost++;
                Sample($"health#{e.Id} modifier no longer matches; no retry | {Snapshot(facade, stat)}");
            }
            if (percent == 100 || e.Attempted) return;
            if (!HealthBudget.CanApply(percent, current, effectiveMax, F(value, stat), F(max, stat),
                    F(permanent, stat), F(raw, stat), ModifierCount(stat))) return;
            int target = HealthBudget.Target(effectiveMax, percent);
            if (target <= effectiveMax) return;
            e.Attempted = true; e.Base = effectiveMax; e.Target = target; attempts++;
            Sample($"health#{e.Id} attempt target={target} | before {Snapshot(facade, stat)}");
            // Add exactly one native temporary modifier. No direct HP refill or permanent max write.
            object modifier = modifierCtor.Invoke(new object[] { healthType, (float)(target - effectiveMax), additive, temporary });
            add.Invoke(stat, new[] { modifier });
            int afterCurrent = I(getStat, facade);
            bool verified = I(getMax, facade) == target && F(max, stat) == target
                && F(permanent, stat) == effectiveMax && F(raw, stat) == effectiveMax
                && afterCurrent >= current && afterCurrent <= target && F(value, stat) == afterCurrent
                && ModifierCount(stat) == 1;
            Sample($"health#{e.Id} applied verified={verified} | after {Snapshot(facade, stat)}");
            if (!verified) throw new InvalidOperationException("Temporary HP modifier result not verified; no further health writes");
            e.Applied = true; applied++; e.LastCurrent = afterCurrent; e.LastMax = target;
        }
        catch (Exception ex) { Stop("observation/apply", ex); }
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
            var lines = new List<string> { "BMF hero maximum health probe (temporary modifier experiment)",
                "Framework=" + Framework.Version, "GameVersion=" + ctx.GameVersion, "GameAssemblySHA256=" + ctx.GameAssemblySha256,
                "UTC=" + DateTime.UtcNow.ToString("O"), "Status=" + Status,
                $"ConfiguredPercent={percent} NextPercent={NextPercent} Observations={observations} Attempts={attempts} Applied={applied} Lost={lost} Errors={errors}",
                "Anonymous native health stat objects; not necessarily distinct heroes. No direct current/max setter." };
            foreach (var e in entries.Values) lines.Add($"health#{e.Id} attempted={e.Attempted} applied={e.Applied} lost={e.Lost} baseline={e.Base} target={e.Target} lastCurrent={e.LastCurrent} lastMax={e.LastMax}");
            lines.AddRange(samples);
            File.WriteAllLines(Path.Combine(ctx.DataDirectory, "hero-health-probe.txt"), lines);
            lastWrite = DateTime.UtcNow; dirty = false;
        }
        catch (Exception ex) { ctx.Log.LogWarning("Health report: " + ex.Message); }
    }
}
