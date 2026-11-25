namespace Battlemarked.ModFramework;

// Pure guards shared by the runtime hook and standalone tests.
internal static class ApRefundBudget
{
    internal static bool CanRefund(int bonus, int used, bool normalBefore, int before, int amount, int after, bool normalAfter)
        => bonus >= 1 && bonus <= 3 && used >= 0 && used < bonus && normalBefore && normalAfter
           && before == 2 && amount == 1 && after == 1;

    internal static bool CanReset(bool normalBefore, int before, bool fullNormalAfter)
        => normalBefore && before == 0 && fullNormalAfter;
}
