namespace Battlemarked.ModFramework;

// Integer-only target math shared by the runtime rule and standalone tests.
internal static class HealthBudget
{
    internal static int Normalize(int percent) => percent >= 100 && percent <= 200 && percent % 25 == 0 ? percent : 100;
    internal static int Target(int baseMax, int percent)
    {
        if (baseMax <= 0 || baseMax > 10000) return 0;
        return (baseMax * Normalize(percent) + 99) / 100;
    }

    // Always scale the native (unpatched) getter result, not a previous computed result.
    internal static float ComputedTarget(float nativeMax, int percent)
        => float.IsFinite(nativeMax) && nativeMax > 0 && nativeMax <= 10000 && nativeMax == MathF.Truncate(nativeMax)
            ? Target((int)nativeMax, percent) : nativeMax;

    // Initial application requires full HP and no net temporary max change.
    // A positive, integral raw maximum plus a consistent permanent maximum can
    // include existing native permanent modifiers; their count is preserved.
    internal static bool CanApply(int percent, int current, int effectiveMax, float objectValue,
        float objectMax, float permanentMax, float rawMax, int modifierCount)
        => Normalize(percent) > 100 && current == effectiveMax && effectiveMax > 0 && effectiveMax <= 10000
           && rawMax > 0 && rawMax <= 10000 && rawMax == MathF.Truncate(rawMax)
           && modifierCount >= 0 && modifierCount <= 16
           && (modifierCount != 0 || rawMax == effectiveMax)
           && objectValue == current && objectMax == effectiveMax && permanentMax == effectiveMax;

    // Only restore the known missing bonus, even if now injured. Unknown changes
    // or remaining modifiers beyond the exact baseline are never overwritten.
    internal static bool CanRefresh(int current, int effectiveMax, float objectValue, float objectMax,
        float permanentMax, float rawMax, int modifierCount, int baseMax, int baseRaw, int baseModifiers)
        => baseMax > 0 && baseMax <= 10000 && baseRaw > 0 && baseRaw <= 10000
           && baseModifiers >= 0 && baseModifiers <= 16
           && current > 0 && current <= baseMax && objectValue == current
           && effectiveMax == baseMax && objectMax == baseMax && permanentMax == baseMax
           && rawMax == baseRaw && modifierCount == baseModifiers;
}

// A rolling window, not a lifetime maximum. All access is on the main thread.
// Reserve only immediately before a verified write; failed writes still count.
internal sealed class HealthRefreshWindow
{
    internal const int Maximum = 8;
    internal const long WindowMs = 5000;
    private readonly Queue<long> attempts = new();
    internal bool TryReserve(long now)
    {
        while (attempts.Count != 0 && now - attempts.Peek() >= WindowMs) attempts.Dequeue();
        if (attempts.Count >= Maximum) return false;
        attempts.Enqueue(now);
        return true;
    }
}
