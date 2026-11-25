using System.Reflection;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;

namespace Battlemarked.ModFramework;

// Counts method entries only; does not inspect arguments, alter results or assert authority.
internal sealed class CoreLogicProbe : BattlemarkedModule
{
    public override string Id => "org.battlemarked.diagnostics.corelogic";
    public override string Name => "Core gameplay diagnostics";
    internal const string VerifiedHash = "C195E177AB5986C26D9E168517A2712DB451A35B79847768883550B05B829280";
    private sealed class Target
    {
        internal readonly string Kind, TypeName, Method, Signature, Prefix;
        internal readonly int ParameterCount;
        internal readonly bool Static;
        internal Target(string kind, string type, string method, string signature, int count, bool isStatic, string prefix)
        { Kind = kind; TypeName = type; Method = method; Signature = signature; ParameterCount = count; Static = isStatic; Prefix = prefix; }
    }
    private static readonly Target[] Targets = {
        new("logic.ap.refill", "ActionPointUpdateHelper", "RefillActionPoints", "0001011269", 1, true, nameof(APRefill)),
        new("logic.ap.subtract", "ActionPointUpdateHelper", "TrySubtractActionPoints", "000202081269", 2, true, nameof(APSubtract)),
        new("logic.card.execute", "CardExecutor", "UseAndExecuteCard", "2004120912310812111269", 4, false, nameof(Card)),
        new("logic.damage", "DealDamageHelper", "DealDamageFromAction", "2005120912847D151284810112834911848512111215", 5, false, nameof(Damage)),
        new("logic.heal", "HealDamageHelper", "ApplyHealing", "200612091284E512691184891211121502", 6, false, nameof(Heal)),
        new("logic.turn.advance", "TurnQueueAdvancer", "AdvanceTurnQueue", "2002120912894D1269", 2, false, nameof(Turn)),
        new("logic.dice.advantage", "DiceRollAdvantageHelper", "UpdateDiceRollAdvantage", "200212091180DD1269", 2, false, nameof(Dice))
    };
    private static readonly long[] Totals = new long[Targets.Length], Delivered = new long[Targets.Length];
    private static int observing;
    private readonly string[] statuses = new string[Targets.Length];
    private ModuleContext context;
    private readonly Stopwatch timer = new();
    private string scene = "unknown";
    private bool dirty;

    public override void OnStart(ModuleContext ctx)
    {
        context = ctx;
        Array.Clear(Totals, 0, Totals.Length); Array.Clear(Delivered, 0, Delivered.Length);
        Array.Fill(statuses, "not attempted");
        if (!ctx.Config.Bind("Probe", "EnableCoreHooks", true, "Experimental read-only method counters; test solo. Restart to change.").Value)
        { Array.Fill(statuses, "disabled"); WriteReport(); return; }
        if (!ctx.IsVerifiedBuild(VerifiedHash))
        { Array.Fill(statuses, "skipped: game fingerprint not validated"); WriteReport(); ctx.Log.LogWarning("Core probe skipped: game build differs from scanned build."); return; }
        for (int i = 0; i < Targets.Length; i++)
        {
            var target = Targets[i];
            try
            {
                var assembly = Assembly.Load(new AssemblyName("GameLogic"));
                var type = assembly.GetType("Jade.GameLogic." + target.TypeName, true);
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == target.Method && m.IsStatic == target.Static && !m.IsAbstract && !m.ContainsGenericParameters &&
                        m.GetParameters().Length == target.ParameterCount && Convert.ToHexString(m.Module.ResolveSignature(m.MetadataToken)) == target.Signature).ToArray();
                if (methods.Length != 1) { statuses[i] = "skipped: exact signature missing or ambiguous"; continue; }
                var prefix = typeof(CoreLogicProbe).GetMethod(target.Prefix, BindingFlags.NonPublic | BindingFlags.Static);
                ctx.Patches.Patch(methods[0], prefix: new HarmonyMethod(prefix));
                statuses[i] = "patched";
                ctx.Log.LogInfo("BMF core probe attached: " + target.Kind);
            }
            catch (Exception ex) { statuses[i] = "failed: " + ex.GetType().Name + ": " + ex.Message; ctx.Log.LogWarning(target.Kind + ": " + statuses[i]); }
        }
        Volatile.Write(ref observing, 1); timer.Start(); WriteReport();
    }
    private static void Count(int index) { if (Volatile.Read(ref observing) != 0) Interlocked.Increment(ref Totals[index]); }
    private static void APRefill() => Count(0);
    private static void APSubtract() => Count(1);
    private static void Card() => Count(2);
    private static void Damage() => Count(3);
    private static void Heal() => Count(4);
    private static void Turn() => Count(5);
    private static void Dice() => Count(6);
    internal static IEnumerable<GameplaySignal> Drain()
    {
        if (Volatile.Read(ref observing) == 0) yield break;
        for (int i = 0; i < Targets.Length; i++)
        {
            long total = Interlocked.Read(ref Totals[i]), delta = total - Delivered[i];
            if (delta <= 0) continue;
            Delivered[i] = total;
            yield return new GameplaySignal(Targets[i].Kind, delta, total);
        }
    }
    internal static string[] Summary() => Targets.Select((t, i) => t.Kind + ": " + Interlocked.Read(ref Totals[i])).ToArray();
    public override void OnCoreSignal(GameplaySignal signal)
    {
        dirty = true;
        if (signal.TotalCalls == signal.CallsSinceLastUpdate) context.Log.LogInfo("BMF core probe observed first call: " + signal.Kind);
    }
    public override void OnSceneChanged(SceneInfo info) { scene = info.Name; dirty = true; }
    public override void OnUpdate() { if (dirty && timer.Elapsed.TotalSeconds >= 5) { WriteReport(); timer.Restart(); } }
    private void WriteReport()
    {
        try
        {
            var lines = new List<string> { "BMF core method entry probe (not authoritative outcomes)", "Framework=" + Framework.Version,
                "GameVersion=" + context.GameVersion, "GameAssemblySHA256=" + context.GameAssemblySha256, "LastActiveScene=" + scene, "UTC=" + DateTime.UtcNow.ToString("O") };
            for (int i = 0; i < Targets.Length; i++)
            {
                var t = Targets[i]; long count = Interlocked.Read(ref Totals[i]);
                string status = statuses[i] == "patched" ? (count > 0 ? "observed" : "patched; awaiting calls") : statuses[i];
                lines.Add($"{t.Kind} | {status} | calls={count} | {t.TypeName}.{t.Method} | {t.Signature}");
            }
            File.WriteAllLines(Path.Combine(context.DataDirectory, "core-probe.txt"), lines); dirty = false;
        }
        catch (Exception ex) { context.Log.LogWarning("Core report: " + ex.Message); }
    }
    public override void OnStop() { Volatile.Write(ref observing, 0); if (context != null) WriteReport(); }
}
