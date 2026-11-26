namespace Battlemarked.ModFramework;

// A scene name is not proof of the main menu. Require observations from
// the actual app-state lifecycle, in two distinct consecutive frames.
internal sealed class MainMenuGate
{
    private readonly object sync = new();
    private IntPtr active;
    private int previous = -1, latest = -1;
    internal void Enter(IntPtr identity)
    {
        lock (sync) { active = identity; previous = latest = -1; }
    }
    internal void Leave()
    {
        lock (sync) { active = IntPtr.Zero; previous = latest = -1; }
    }
    internal void Observe(IntPtr identity, int frame)
    {
        lock (sync)
        {
            if (active == IntPtr.Zero || identity != active || frame < 0 || frame == latest) return;
            previous = latest;
            latest = frame;
        }
    }
    internal bool Ready(int frame)
    {
        lock (sync)
            return active != IntPtr.Zero && previous >= 0 && latest > previous &&
                latest - previous == 1 && frame >= latest && frame - latest <= 1;
    }
}

internal enum ModuleChange { None, Start, Stop }

internal static class ModuleSwitchPolicy
{
    internal static ModuleChange Plan(bool menuReady, bool restartable, bool restartBlocked, bool running, bool desired)
    {
        if (!menuReady || !restartable || running == desired) return ModuleChange.None;
        if (running) return ModuleChange.Stop;
        return restartBlocked ? ModuleChange.None : ModuleChange.Start;
    }
}
