using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Battlemarked.ModFramework;

// Separate read-only lifecycle hooks, independent of enabled gameplay modules.
// No scene-name guess or "not in a game" heuristic is used.
internal sealed class MainMenuProbe : IDisposable
{
    private readonly MainMenuGate gate = new();
    private readonly Harmony patches = new(Framework.Guid + ".menugate");
    private readonly int mainThread = Environment.CurrentManagedThreadId;
    private readonly ManualLogSource log;
    private static MainMenuProbe active;
    private PropertyInfo pointer;
    internal bool Available { get; private set; }
    private string detail = "Unverified game fingerprint; restart required";
    internal string DiagnosticDetail => detail;
    internal bool Ready
    {
        get
        {
            if (!Available || Environment.CurrentManagedThreadId != mainThread) return false;
            try { return gate.Ready(Time.frameCount); }
            catch { gate.Leave(); return false; }
        }
    }
    internal string Status => Available ? (Ready ? "Verified main menu" : "Waiting for verified main menu") : "Main-menu detection unavailable; restart required";

    internal MainMenuProbe(bool verified, ManualLogSource log)
    {
        this.log = log;
        if (!verified) return;
        try
        {
            var assembly = Assembly.Load(new AssemblyName("Jade.Application"));
            var type = assembly.GetType("Jade.Application.AppStateMainMenu", true);
            pointer = type.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            if (pointer?.PropertyType != typeof(IntPtr) || !pointer.CanRead)
                throw new InvalidOperationException("Main-menu native identity unavailable");
            // The interop report identifies this exact state type and its
            // transition flag. Validate lifecycle method shape at runtime.
            var enter = Lifecycle(type, "Enter");
            var exit = Lifecycle(type, "Exit");
            if (enter == null || exit == null)
                throw new InvalidOperationException("Main-menu enter/exit metadata unavailable: " + DescribeLifecycle(type));
            var tick = Exact(type, "Tick", "200001");
            var transition = Exact(type, "get_ShouldGoToHeroManagement", "200002");
            if (tick == null && transition == null)
                throw new InvalidOperationException("Main-menu observation metadata unavailable");
            if (tick != null && tick.ReturnType != typeof(void)) throw new InvalidOperationException("Unexpected menu tick type");
            if (transition != null && transition.ReturnType != typeof(bool)) throw new InvalidOperationException("Unexpected menu transition flag type");
            // Additional fail-closed guards use exact campaign hooks already
            // verified against the target's supplied interop metadata.
            var playing = assembly.GetType("Jade.Application.AppStatePlaying", true);
            var start = Exact(playing, "StartNewCampaign", "200212814C11831908");
            var join = Exact(playing, "JoinExistingCampaign", "200412814C122D1184541512837D0112351281C5");
            if (start == null || join == null) throw new InvalidOperationException("Campaign transition metadata unavailable");
            active = this;
            patches.Patch(enter, postfix: Hook(nameof(Entered)));
            patches.Patch(exit, prefix: Hook(nameof(Leaving)));
            if (tick != null) patches.Patch(tick, postfix: Hook(nameof(Observed)));
            if (transition != null) patches.Patch(transition, postfix: Hook(nameof(TransitionObserved)));
            patches.Patch(start, prefix: Hook(nameof(Leaving)));
            patches.Patch(join, prefix: Hook(nameof(Leaving)));
            detail = "Enter/exit and campaign guards bound; tick=" + (tick != null) + ", transition flag=" + (transition != null);
            Available = true;
            log.LogInfo("BMF main-menu lifecycle bound: enter/exit, tick=" + (tick != null) + ", transition flag=" + (transition != null));
        }
        catch (Exception ex)
        {
            detail = "Main-menu detection unavailable: " + ex.Message;
            log.LogWarning("BMF main-menu lifecycle: " + ex.Message);
            Dispose();
        }
    }

    // Enter/Exit are virtual state lifecycle methods, either immediate or
    // coroutine-returning. Do not assume a metadata token from another state.
    private static MethodInfo Lifecycle(Type type, string name)
    {
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name && m.IsVirtual && !m.IsAbstract && !m.ContainsGenericParameters &&
                m.GetParameters().Length == 0 &&
                (m.ReturnType == typeof(void) || m.ReturnType.FullName == "Il2CppSystem.Collections.IEnumerator" ||
                 m.ReturnType.FullName == "System.Collections.IEnumerator")).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static string DescribeLifecycle(Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "Enter" || m.Name == "Exit" || m.Name == "Tick")
            .Select(m => m.Name + "/" + m.GetParameters().Length + ":" + m.ReturnType.FullName + " virtual=" + m.IsVirtual);
        return string.Join("; ", methods);
    }

    private static MethodInfo Exact(Type type, string name, string signature)
    {
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters &&
                Convert.ToHexString(m.Module.ResolveSignature(m.MetadataToken)) == signature).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(MainMenuProbe).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    private static void Entered(object __instance)
    {
        var probe = active;
        if (probe == null) return;
        try
        {
            if (Environment.CurrentManagedThreadId == probe.mainThread &&
                probe.pointer.GetValue(__instance) is IntPtr key && key != IntPtr.Zero)
                probe.gate.Enter(key);
            else probe.gate.Leave();
        }
        catch { probe.gate.Leave(); }
    }
    private static void Leaving() => active?.gate.Leave();
    private static void Observed(object __instance)
    {
        var probe = active;
        if (probe == null) return;
        try
        {
            if (Environment.CurrentManagedThreadId == probe.mainThread &&
                probe.pointer.GetValue(__instance) is IntPtr key && key != IntPtr.Zero)
                probe.gate.Observe(key, Time.frameCount);
            else probe.gate.Leave();
        }
        catch { probe.gate.Leave(); }
    }
    private static void TransitionObserved(object __instance, bool __result)
    {
        if (__result) Leaving();
        else Observed(__instance);
    }
    public void Dispose()
    {
        Available = false;
        gate.Leave();
        if (ReferenceEquals(active, this))
        {
            active = null;
            try { patches.UnpatchSelf(); }
            catch (Exception ex) { log.LogWarning("BMF main-menu lifecycle cleanup: " + ex.Message); }
        }
    }
}
