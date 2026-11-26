namespace Battlemarked.ModFramework;

// The framework polls Unity scenes after native callbacks may already have run.
internal static class SessionModelGuard
{
    internal static bool RetainCapture(int? capturedScene, int newScene)
        => capturedScene.HasValue && capturedScene.Value == newScene;

    internal static bool IsSameScene(int? previous, string previousName, int next, string nextName)
        => previous.HasValue && previous.Value == next && previousName == nextName;

    internal static bool MatchesTeardown(IntPtr retired, IntPtr tracked)
        => retired != IntPtr.Zero && retired == tracked;
}
