using System.Reflection;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;

namespace Battlemarked.ModFramework;

// Only observes entry to presentation handlers. No original arguments or results are touched.
internal sealed class GameplayProbe : BattlemarkedModule
{
    public override string Id => "org.battlemarked.diagnostics.gameplay";
    public override string Name => "Gameplay event logger";
    private sealed class Target
    {
        public string Kind { get; }
        public string TypeName { get; }
        public string Signature { get; }
        public string Prefix { get; }
        public Target(string Kind, string TypeName, string Signature, string Prefix)
        {
            this.Kind = Kind;
            this.TypeName = TypeName;
            this.Signature = Signature;
            this.Prefix = Prefix;
        }
    }
    private static readonly Target[] Targets =
    {
        new("turn.start", "ViewEventHandlerTurnStart", "20011275128B35", nameof(TurnStart)),
        new("turn.end", "ViewEventHandlerTurnEnd", "20011275128B29", nameof(TurnEnd)),
        new("card.executed", "ViewEventHandlerCardExecuted", "20011275128955", nameof(CardExecuted)),
        new("piece.damaged", "ViewEventHandlerPieceDamaged", "20011275128339", nameof(PieceDamaged)),
        new("dice.spawn", "ViewEventHandlerSpawnDice", "20011275128AE9", nameof(DiceSpawn)),
        new("dice.encounter.resolve", "ViewEventHandlerResolveEncounterDiceRoll", "20011275128ACD", nameof(DiceResolve))
    };
    private static readonly long[] Totals = new long[Targets.Length];
    internal static string[] Summary() => Targets.Select((t, i) => t.Kind + ": " + Interlocked.Read(ref Totals[i])).ToArray();
    private static readonly long[] Delivered = new long[Targets.Length];
    private readonly string[] statuses = new string[Targets.Length];
    private ModuleContext context;
    private bool active;
    private static int observing;
    private bool dirty;
    private readonly Stopwatch timer = new();
    private string scene = "unknown";

    public override void OnStart(ModuleContext ctx)
    {
        context = ctx;
        Array.Clear(Totals, 0, Totals.Length);
        Array.Clear(Delivered, 0, Delivered.Length);
        Array.Fill(statuses, "not attempted");
        if (!ctx.Config.Bind("Probe", "EnablePresentationHooks", true,
                "Observe six exact metadata-matched view methods. Restart to change. Test solo.").Value)
        { Array.Fill(statuses, "disabled"); WriteReport(); return; }
        active = true;
        for (int i = 0; i < Targets.Length; i++)
        {
            var target = Targets[i];
            try
            {
                var assembly = Assembly.Load(new AssemblyName("GameView"));
                var type = assembly.GetType("Jade.GameView." + target.TypeName, true);
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == "HandleEvent" && !m.IsStatic && !m.IsAbstract &&
                        !m.ContainsGenericParameters && m.GetParameters().Length == 1 &&
                        Convert.ToHexString(m.Module.ResolveSignature(m.MetadataToken)) == target.Signature)
                    .ToArray();
                if (methods.Length != 1)
                { statuses[i] = "skipped: exact metadata signature missing or ambiguous"; continue; }
                var prefix = typeof(GameplayProbe).GetMethod(target.Prefix, BindingFlags.Static | BindingFlags.NonPublic);
                ctx.Patches.Patch(methods[0], prefix: new HarmonyMethod(prefix));
                statuses[i] = "patched";
                ctx.Log.LogInfo($"BMF probe attached: {target.Kind}");
            }
            catch (Exception ex)
            {
                statuses[i] = "failed: " + ex.GetType().Name + ": " + ex.Message;
                ctx.Log.LogWarning($"BMF probe {target.Kind}: {statuses[i]}");
            }
        }
        Volatile.Write(ref observing, 1);
        WriteReport();
        timer.Start();
    }
    private static void Observe(int index)
    {
        if (Volatile.Read(ref observing) != 0) Interlocked.Increment(ref Totals[index]);
    }
    private static void TurnStart() => Observe(0);
    private static void TurnEnd() => Observe(1);
    private static void CardExecuted() => Observe(2);
    private static void PieceDamaged() => Observe(3);
    private static void DiceSpawn() => Observe(4);
    private static void DiceResolve() => Observe(5);

    internal static IEnumerable<GameplaySignal> Drain()
    {
        if (Volatile.Read(ref observing) == 0) yield break;
        for (int i = 0; i < Targets.Length; i++)
        {
            long total = Interlocked.Read(ref Totals[i]);
            long delta = total - Delivered[i];
            if (delta <= 0) continue;
            Delivered[i] = total;
            yield return new GameplaySignal(Targets[i].Kind, delta, total);
        }
    }
    public override void OnGameplaySignal(GameplaySignal signal)
    {
        dirty = true;
        if (signal.TotalCalls == signal.CallsSinceLastUpdate)
            context.Log.LogInfo($"BMF probe observed first call: {signal.Kind}");
    }
    public override void OnSceneChanged(SceneInfo info) { scene = info.Name; dirty = true; }
    public override void OnUpdate()
    {
        if (active && dirty && timer.Elapsed.TotalSeconds >= 5)
        { WriteReport(); timer.Restart(); }
    }
    private void WriteReport()
    {
        try
        {
            var lines = new List<string> { "BMF presentation probe (calls, not authoritative outcomes)",
                "Framework=" + Framework.Version, "GameVersion=" + context.GameVersion,
                "GameAssemblySHA256=" + context.GameAssemblySha256, "LastActiveScene=" + scene,
                "UTC=" + DateTime.UtcNow.ToString("O") };
            for (int i = 0; i < Targets.Length; i++)
                {
                long count = Interlocked.Read(ref Totals[i]);
                string status = statuses[i] == "patched" ? (count > 0 ? "observed" : "patched; awaiting calls") : statuses[i];
                lines.Add($"{Targets[i].Kind} | {status} | calls={count} | {Targets[i].TypeName}.HandleEvent | {Targets[i].Signature}");
            }
            File.WriteAllLines(Path.Combine(context.DataDirectory, "gameplay-probe.txt"), lines);
            dirty = false;
        }
        catch (Exception ex) { context.Log.LogWarning("Probe report: " + ex.Message); }
    }
    public override void OnStop()
    {
        Volatile.Write(ref observing, 0);
        if (context != null) WriteReport();
        active = false;
    }
}
