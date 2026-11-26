namespace Battlemarked.ModFramework;

// Pure guards shared by the runtime hook and standalone tests.
internal static class ApRefundBudget
{
    internal static bool CanApplyMode(bool idleScope, int trackedObjects, bool inNativeWrite)
        => idleScope && trackedObjects == 0 && !inNativeWrite;

    // Cycle observations are independent of the current bonus or eligibility
    // toggle, so disabling temporarily does not reset or freeze the ledger.
    internal static bool CanObserveCycle(int cap, int remaining)
        => cap >= 2 && cap <= 5 && remaining >= 0 && remaining <= cap;

    internal static bool EligibleCap(int cap, bool adjustExisting)
        => cap == 2 || (adjustExisting && cap >= 3 && cap <= 5);

    // Count the AP already present above the normal two against the selected bonus.
    // A cap above the target is left untouched; refund mode never lowers a cap.
    internal static int Limit(int bonus, int cap, bool adjustExisting)
        => bonus >= 1 && bonus <= 3 && EligibleCap(cap, adjustExisting)
           ? Math.Max(0, 2 + bonus - cap) : 0;

    internal static bool CanRefund(int bonus, int used, int cap, bool adjustExisting, bool verifiedBefore, int before, int amount, int after, bool verifiedAfter)
        => used >= 0 && used < Limit(bonus, cap, adjustExisting) && verifiedBefore && verifiedAfter
           && before == cap && amount == 1 && after == cap - 1;

    internal static bool CanReset(bool verifiedBefore, int before, int cap, bool verifiedFullAfter, bool partialRefills)
        => verifiedBefore && verifiedFullAfter && cap >= 2 && cap <= 5
           && before >= 0 && before < cap && (before == 0 || partialRefills);
}
