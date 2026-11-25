using Battlemarked.ModFramework;
var trace = new List<string>();
var failures = new List<string>();
var host = new ModuleHost<string>((id, value, error) => failures.Add(id));
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
Check(host.Add("one", "one"), "initial registration");
Check(!host.Add("one", "duplicate"), "duplicate rejected");
Check(host.Add("two", "two") && host.Add("three", "three"), "register remaining modules");
host.Dispatch(m => { trace.Add(m); if (m == "two") throw new Exception("expected"); }, m => trace.Add("stop:" + m));
Check(trace.SequenceEqual(new[] {"one", "two", "stop:two", "three"}), "order, cleanup, failure isolation");
Check(host.Count == 2 && failures.SequenceEqual(new[] {"two"}), "failed module removed");
trace.Clear();
host.Dispatch(trace.Add, _ => {});
Check(trace.SequenceEqual(new[] {"one", "three"}), "failed module not called again");
trace.Clear();
host.Clear(m => trace.Add(m));
Check(host.Count == 0 && trace.SequenceEqual(new[] {"three", "one"}), "reverse cleanup order");
Check(!host.Remove("missing", _ => {}), "missing module removal");
Console.WriteLine("PASS: eight lifecycle assertions.");

for (int cap = 2; cap <= 5; cap++)
for (int bonus = 0; bonus <= 3; bonus++)
foreach (bool adjust in new[] { false, true })
{
    int current = cap, used = 0, actions = 0;
    int expected = cap == 2 || adjust ? Math.Max(cap, 2 + bonus) : cap;
    while (current > 0)
    {
        int before = current;
        current--; actions++;
        if (ApRefundBudget.CanRefund(bonus, used, cap, adjust, true, before, 1, current, true))
        { used++; current = cap; }
        Check(actions <= 5, "refund budget bounded");
    }
    Check(actions == expected && used == expected - cap, "expected action count");
    Check(!ApRefundBudget.CanRefund(bonus, used, cap, adjust, true, cap, 1, cap - 1, true), "budget exhausted");
}
Check(ApRefundBudget.Limit(3, 3, true) == 2, "reported 3 AP cap gets two refunds");
Check(ApRefundBudget.Limit(3, 3, false) == 0, "existing cap requires opt-in");
Check(ApRefundBudget.Limit(1, 5, true) == 0, "above-target cap unchanged");
Check(ApRefundBudget.Limit(3, 6, true) == 0, "unrecognized cap skipped");
Check(!ApRefundBudget.CanRefund(3, 0, 3, true, false, 3, 1, 2, true), "unverified before");
Check(!ApRefundBudget.CanRefund(3, 0, 3, true, true, 3, 1, 2, false), "unverified after");
Check(!ApRefundBudget.CanRefund(3, 0, 3, true, true, 1, 1, 0, true), "no refund after empty");
Check(!ApRefundBudget.CanRefund(3, 0, 3, true, true, 3, 2, 1, true), "two-point spend not refunded");
Check(!ApRefundBudget.CanRefund(3, 0, 3, true, true, 3, 0, 3, true), "free action not refunded");
Check(ApRefundBudget.CanReset(true, 0, true), "empty to full refill resets");
Check(!ApRefundBudget.CanReset(true, 1, true), "partial refill does not reset");
Check(!ApRefundBudget.CanReset(true, 3, true), "duplicate full refill does not reset");
Check(!ApRefundBudget.CanReset(false, 0, true), "unverified empty does not reset");
Check(!ApRefundBudget.CanReset(true, 0, false), "unverified full does not reset");
Console.WriteLine("PASS: 32 AP refund cap/bonus/opt-in combinations and guard scenarios.");

foreach (int hp in new[] { 1, 3, 7, 10, 11, 30, 10000 })
foreach (int percent in new[] { 100, 125, 150, 175, 200 })
{
    int expected = (int)Math.Ceiling(hp * (double)percent / 100);
    Check(HealthBudget.Target(hp, percent) == expected, "health target round up");
    Check(HealthBudget.CanApply(percent, hp, hp, hp, hp, hp, hp, 0) == (percent > 100), "health default and full baseline");
}
Check(HealthBudget.Target(0, 200) == 0 && HealthBudget.Target(10001, 200) == 0, "health bounds");
Check(HealthBudget.Target(10, 999) == 10, "invalid setting fail closed");
Check(!HealthBudget.CanApply(150, 8, 10, 8, 10, 10, 10, 0), "injured excluded");
Check(!HealthBudget.CanApply(150, 15, 15, 15, 15, 10, 10, 1), "existing modifier excluded");
Check(!HealthBudget.CanApply(150, 10, 10, 10, 10, 10, 10, -1), "unknown modifiers excluded");
Check(HealthBudget.CanApply(150, 25, 25, 25, 25, 25, 22, 1), "existing permanent modifier baseline");
Check(HealthBudget.Target(25, 125) == 32, "reported modified hero baseline");
Check(!HealthBudget.CanApply(150, 10, 10, 10.5f, 10, 10, 10, 0), "fractional mismatch excluded");
Console.WriteLine("PASS: health target and conservative baseline guards.");

Check(HealthBudget.CanRefresh(11, 16, 11, 16, 16, 16, 0, 16, 16, 0), "reported lost bonus on injured hero");
Check(HealthBudget.CanRefresh(7, 16, 7, 16, 16, 16, 0, 16, 16, 0), "injured refresh preserves current");
Check(!HealthBudget.CanRefresh(11, 20, 11, 20, 16, 16, 1, 16, 16, 0), "existing own bonus cannot stack");
Check(!HealthBudget.CanRefresh(11, 16, 11, 16, 16, 16, 1, 16, 16, 0), "unknown modifier cannot stack");
Check(!HealthBudget.CanRefresh(11, 17, 11, 17, 17, 16, 1, 16, 16, 0), "changed permanent baseline skipped");
Check(!HealthBudget.CanRefresh(0, 16, 0, 16, 16, 16, 0, 16, 16, 0), "zero current skipped");
Check(HealthBudget.CanRefresh(24, 25, 24, 25, 25, 22, 1, 25, 22, 1), "existing baseline modifiers preserved");
Check(!HealthBudget.CanRefresh(24, 25, 24, 25, 25, 22, 2, 25, 22, 1), "additional native modifier skipped");
Console.WriteLine("PASS: health refresh and stacking guards.");
