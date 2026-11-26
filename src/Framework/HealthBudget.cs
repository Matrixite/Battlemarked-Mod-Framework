namespace Battlemarked.ModFramework;

// The runtime records only checked permanent additive health modifiers. Native
// pointer identities are private and never serialized or printed.
internal readonly record struct HealthModifierStamp(IntPtr Key, int Amount);

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

    internal static bool CanNativeTrial(int percent, int current, int effectiveMax, float objectValue,
        float objectMax, float permanentMax, float rawMax, int modifierCount)
        => NativeTrialRejection(percent, current, effectiveMax, objectValue, objectMax,
            permanentMax, rawMax, modifierCount) == null;

    // Fresh-room authorization and the downed check are enforced by the caller.
    // Full HP is not needed for a maximum-only write: preserve the exact current
    // value even for an injured hero. Unknown modifiers and inconsistent native
    // readings remain ineligible, including fractional or non-finite values.
    internal static string NativeTrialRejection(int percent, int current, int effectiveMax, float objectValue,
        float objectMax, float permanentMax, float rawMax, int modifierCount, int? verifiedAdditiveBonus = null)
    {
        if (Normalize(percent) <= 100) return "health bonus disabled";
        if (modifierCount < 0) return "modifier count unavailable";
        if (modifierCount > 16) return "too many health modifiers";
        if (modifierCount != 0 && !verifiedAdditiveBonus.HasValue) return "existing health modifiers";
        int bonus = verifiedAdditiveBonus ?? 0;
        if (bonus < 0 || bonus > 10000 || (modifierCount == 0 && bonus != 0))
            return "invalid verified additive bonus";
        if (effectiveMax <= 0 || effectiveMax > 10000) return "invalid maximum health";
        if (current <= 0 || current > effectiveMax) return "current health outside valid range";
        if (objectValue != current) return "current health getter mismatch";
        if (!float.IsFinite(rawMax) || rawMax <= 0 || rawMax > 10000 ||
            rawMax != MathF.Truncate(rawMax) || rawMax + bonus != effectiveMax ||
            permanentMax != effectiveMax || objectMax != effectiveMax)
            return "native maximum getter mismatch";
        int target = Target(effectiveMax, percent);
        if (target <= effectiveMax || RawTarget((int)rawMax, effectiveMax, target) == 0)
            return "invalid health target";
        return null;
    }

    internal static int RawTarget(int rawBaseline, int effectiveBaseline, int effectiveTarget)
    {
        if (rawBaseline <= 0 || rawBaseline > 10000 || effectiveBaseline <= 0 ||
            effectiveBaseline > 10000 || effectiveTarget <= effectiveBaseline || effectiveTarget > 20000)
            return 0;
        long rawTarget = (long)rawBaseline + effectiveTarget - effectiveBaseline;
        return rawTarget > 0 && rawTarget <= 20000 ? (int)rawTarget : 0;
    }

    internal static bool TryAdditiveBonus(HealthModifierStamp[] stamps, out int bonus)
    {
        bonus = 0;
        if (stamps == null || stamps.Length > 16) return false;
        var keys = new HashSet<IntPtr>();
        foreach (var stamp in stamps)
        {
            if (stamp.Key == IntPtr.Zero || !keys.Add(stamp.Key) || stamp.Amount < 0 ||
                stamp.Amount > 10000 || bonus > 10000 - stamp.Amount) return false;
            bonus += stamp.Amount;
        }
        return true;
    }

    internal static bool SameModifierStamps(HealthModifierStamp[] before, HealthModifierStamp[] after)
        => before != null && after != null && before.SequenceEqual(after);

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
