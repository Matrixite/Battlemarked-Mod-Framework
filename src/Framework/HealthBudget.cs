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

    // First alpha deliberately excludes injured, downed, fractional, or already modified stats.
    internal static bool CanApply(int percent, int current, int effectiveMax, float objectValue,
        float objectMax, float permanentMax, float rawMax, int modifierCount)
        => Normalize(percent) > 100 && effectiveMax > 0 && effectiveMax <= 10000
           && current == effectiveMax && objectValue == current && objectMax == effectiveMax
           && permanentMax == effectiveMax && rawMax == effectiveMax && modifierCount == 0;
}
