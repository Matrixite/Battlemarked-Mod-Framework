namespace Battlemarked.ModFramework;

// Preferences only. Selecting a preset never creates a fresh-room authorization.
internal sealed class HouseRulePreset
{
    internal string Name { get; }
    internal int Bonus { get; }
    internal int HealthPercent { get; }
    internal bool Refund { get; }
    internal bool AdjustExisting { get; }
    private HouseRulePreset(string name, int bonus, int healthPercent, bool refund, bool adjustExisting)
    {
        Name = name; Bonus = bonus; HealthPercent = healthPercent; Refund = refund; AdjustExisting = adjustExisting;
    }
    internal static readonly IReadOnlyList<HouseRulePreset> All = Array.AsReadOnly(new[]
    {
        new HouseRulePreset("Vanilla", 0, 100, true, false),
        new HouseRulePreset("Relaxed", 1, 125, true, true),
        new HouseRulePreset("Heroic", 2, 150, true, true),
        new HouseRulePreset("Tested party", 3, 125, true, true),
        new HouseRulePreset("Epic", 3, 200, true, true)
    });
    internal static string Match(int bonus, int health, bool refund, bool adjust)
        => All.FirstOrDefault(p => p.Bonus == bonus && p.HealthPercent == health &&
            p.Refund == refund && p.AdjustExisting == adjust)?.Name ?? "Custom";
}
