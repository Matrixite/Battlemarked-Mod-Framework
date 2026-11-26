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
Check(ApRefundBudget.CanReset(true, 0, 3, true, true), "empty to full refill resets");
Check(ApRefundBudget.CanReset(true, 1, 3, true, true), "partial refill resets when enabled");
Check(ApRefundBudget.CanReset(true, 2, 3, true, true), "near-full refill resets when enabled");
Check(!ApRefundBudget.CanReset(true, 1, 3, true, false), "partial refill disabled fallback");
Check(ApRefundBudget.CanReset(true, 0, 3, true, false), "empty reset retained in fallback");
Check(!ApRefundBudget.CanReset(true, 3, 3, true, true), "duplicate full refill does not reset");
Check(!ApRefundBudget.CanReset(false, 0, 3, true, true), "unverified empty does not reset");
Check(!ApRefundBudget.CanReset(true, 0, 3, false, true), "unverified full does not reset");
Check(!ApRefundBudget.CanReset(true, -1, 3, true, true), "invalid negative remaining rejected");
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

var rate = new HealthRefreshWindow();
for (int i = 0; i < 8; i++) Check(rate.TryReserve(1000), "initial burst accepted");
Check(!rate.TryReserve(1001), "burst bounded");
Check(!rate.TryReserve(5999), "rolling window still full");
Check(rate.TryReserve(6000), "expired slots recover");
var longAdventure = new HealthRefreshWindow();
for (int i = 0; i < 100; i++) Check(longAdventure.TryReserve(i * 1000L), "no lifetime refresh ceiling");
var stagger = new HealthRefreshWindow();
for (int i = 0; i < 8; i++) Check(stagger.TryReserve(i * 100), "staggered burst accepted");
Check(stagger.TryReserve(5000), "oldest slot expires");
Check(!stagger.TryReserve(5000), "only oldest slot expires");
Console.WriteLine("PASS: rolling health refresh limits and long adventure scenarios.");

foreach (int hp in new[] { 1, 16, 18, 22, 25, 10000 })
foreach (int pct in new[] { 100, 125, 150, 175, 200 })
    Check(HealthBudget.ComputedTarget(hp, pct) == Math.Ceiling(hp * (double)pct / 100), "computed native max");
Check(HealthBudget.ComputedTarget(16.5f, 125) == 16.5f, "fractional max unchanged");
Check(HealthBudget.ComputedTarget(0, 125) == 0, "zero max unchanged");
Check(HealthBudget.ComputedTarget(10001, 125) == 10001, "oversize max unchanged");
Check(float.IsNaN(HealthBudget.ComputedTarget(float.NaN, 125)), "invalid max unchanged");
Check(HealthBudget.ComputedTarget(16, 125) == 20 && HealthBudget.ComputedTarget(16, 125) == 20, "repeat native getter does not stack");
Console.WriteLine("PASS: computed health target guards.");

Check(HealthBudget.CanNativeTrial(125,16,16,16,16,16,16,0), "native trial full unmodified hero");
Check(!HealthBudget.CanNativeTrial(125,25,25,25,25,25,22,1), "native trial skips existing modifiers");
Check(!HealthBudget.CanNativeTrial(125,14,16,14,16,16,16,0), "native trial skips injured");
Check(!HealthBudget.CanNativeTrial(100,16,16,16,16,16,16,0), "native trial disabled");
Check(!HealthBudget.CanNativeTrial(125,16,16,16,20,16,16,0), "getter mismatch rejected");
Console.WriteLine("PASS: native health trial guards.");

var armDir = Path.Combine(Path.GetTempPath(), "bmf-arm-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(armDir);
try
{
    var request = new NativeHealthArmRequest(armDir);
    Check(!request.Pending && !request.Consume(125, out _), "default is read-only");
    bool invalidArm = false;
    try { request.Queue(100); } catch (ArgumentOutOfRangeException) { invalidArm = true; }
    Check(invalidArm && !request.Pending, "100 percent cannot queue a request");

    request.Queue(125);
    Check(request.Pending, "arming creates a pending request");
    var nextLaunch = new NativeHealthArmRequest(armDir);
    Check(nextLaunch.Pending && nextLaunch.Consume(125, out _), "request survives a new instance and activates once");
    Check(!nextLaunch.Pending && !new NativeHealthArmRequest(armDir).Consume(125, out _), "claimed request cannot activate on another launch");

    request.Queue(150);
    Check(!new NativeHealthArmRequest(armDir).Consume(125, out _) && !request.Pending,
        "mismatched saved target is rejected and consumed");
    request.Queue(125);
    request.Cancel();
    Check(!request.Pending && !new NativeHealthArmRequest(armDir).Consume(125, out _), "cancel removes the pending request");

    File.WriteAllText(Path.Combine(armDir, NativeHealthArmRequest.PendingFileName), "corrupt");
    Check(!request.Consume(125, out _) && !request.Pending, "malformed request consumed without activating");

    request.Queue(125);
    var claimedPath = Path.Combine(armDir, NativeHealthArmRequest.ClaimedFileName);
    File.Delete(claimedPath);
    Directory.CreateDirectory(claimedPath);
    bool failedClaim = false;
    try { request.Consume(125, out _); } catch (IOException) { failedClaim = true; }
    catch (UnauthorizedAccessException) { failedClaim = true; }
    Check(failedClaim && request.Pending, "failed claim cannot activate a trial");
    request.Cancel();
}
finally { Directory.Delete(armDir, true); }
Console.WriteLine("PASS: one-shot native health arm request lifecycle.");
