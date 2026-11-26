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

// Main-menu proof must be fresh, consecutive and inside an entered state.
var menuGate = new MainMenuGate();
var menuKey = new IntPtr(1);
var staleMenuKey = new IntPtr(2);
Check(!menuGate.Ready(0), "no menu evidence at startup");
menuGate.Observe(menuKey, 10); menuGate.Observe(menuKey, 11);
Check(!menuGate.Ready(11), "pulse without menu entry cannot authorize");
menuGate.Enter(IntPtr.Zero); menuGate.Observe(IntPtr.Zero, 12); menuGate.Observe(IntPtr.Zero, 13);
Check(!menuGate.Ready(13), "invalid menu identity rejected");
menuGate.Enter(menuKey); menuGate.Observe(menuKey, 20); menuGate.Observe(menuKey, 20);
Check(!menuGate.Ready(20), "duplicate observations cannot authorize");
menuGate.Observe(staleMenuKey, 21);
Check(!menuGate.Ready(21), "stale menu instance cannot authorize");
menuGate.Observe(menuKey, 21);
Check(menuGate.Ready(21) && menuGate.Ready(22), "two consecutive main-menu frames authorize now/next frame");
Check(!menuGate.Ready(23), "old observations expire");
menuGate.Observe(menuKey, 25);
Check(!menuGate.Ready(25), "nonconsecutive frames fail closed");
menuGate.Observe(menuKey, 26);
Check(menuGate.Ready(26), "fresh proof recovers");
menuGate.Leave(); menuGate.Observe(menuKey, 27);
Check(!menuGate.Ready(27), "exit invalidates and late callbacks can't reopen");
menuGate.Enter(staleMenuKey); menuGate.Observe(menuKey, 30); menuGate.Observe(staleMenuKey, 30);
Check(!menuGate.Ready(30), "new entry does not reuse proof");
menuGate.Observe(staleMenuKey, 31);
Check(menuGate.Ready(31), "re-entered menu verified");
foreach (bool ready in new[] { false, true })
foreach (bool restartable in new[] { false, true })
foreach (bool blocked in new[] { false, true })
foreach (bool running in new[] { false, true })
foreach (bool desired in new[] { false, true })
{
    var expected = !ready || !restartable || running == desired ? ModuleChange.None :
        running ? ModuleChange.Stop : blocked ? ModuleChange.None : ModuleChange.Start;
    Check(ModuleSwitchPolicy.Plan(ready, restartable, blocked, running, desired) == expected, "switch plan matrix");
}
var toggleHost = new ModuleHost<string>((id, value, error) => failures.Add(id));
Check(toggleHost.Add("mod", "first"), "start module instance");
int cleaned = 0;
Check(toggleHost.Remove("mod", _ => cleaned++) && cleaned == 1, "stop removes module and cleans up once");
Check(toggleHost.Add("mod", "fresh"), "re-enable same ID with fresh instance");
var live = new List<string>();
toggleHost.Dispatch(live.Add, _ => {});
Check(live.SequenceEqual(new[] { "fresh" }), "only new instance receives callbacks");
Console.WriteLine("PASS: menu-gated lifecycle policy and repeated module transitions.");

// Developer mode affects menu visibility only; known diagnostics are opt-in.
var developmentModuleIds = new[] { "org.battlemarked.diagnostics.corelogic", "org.battlemarked.diagnostics.gameplay", "org.battlemarked.example.scenelogger" };
foreach (string id in developmentModuleIds)
{
    Check(!MenuModuleVisibility.IsVisible(id, false), "diagnostic hidden by default: " + id);
    Check(MenuModuleVisibility.IsVisible(id, true), "diagnostic visible in developer mode: " + id);
}
foreach (string id in new[] { "org.battlemarked.houserules", "org.example.custom", "org.battlemarked.diagnostics.custom" })
{
    Check(MenuModuleVisibility.IsVisible(id, false), "ordinary module visible by default: " + id);
    Check(MenuModuleVisibility.IsVisible(id, true), "ordinary module visible in developer mode: " + id);
}
Console.WriteLine("PASS: developer-mode menu visibility.");

// Independent switches: developer mode controls Reports, the house-rules
// enable preference controls AP, Health and Presets. Keep other tabs reachable.
foreach (bool developerMode in new[] { false, true })
foreach (bool houseRulesEnabled in new[] { false, true })
{
    foreach (string name in new[] { "Overview", "Mods", "Updates" })
        Check(MenuModuleVisibility.IsTabVisible(name, developerMode, houseRulesEnabled), "regular tab always visible: " + name);
    Check(MenuModuleVisibility.IsTabVisible("Reports", developerMode, houseRulesEnabled) == developerMode, "reports follows developer mode");
    Check(MenuModuleVisibility.IsTabVisible("AP rules", developerMode, houseRulesEnabled) == houseRulesEnabled, "AP follows house-rules enabled preference");
    Check(MenuModuleVisibility.IsTabVisible("Health", developerMode, houseRulesEnabled) == houseRulesEnabled, "health follows house-rules enabled preference");
    Check(MenuModuleVisibility.IsTabVisible("Presets", developerMode, houseRulesEnabled) == houseRulesEnabled, "presets follows house-rules enabled preference");
}
Check(UpdatePolicy.ForDisplay("Checking for updates...", "Update status unavailable") == "Checking for updates...", "normal status preserved");
Check(UpdatePolicy.ForDisplay("Update failed: GITHUB.com unavailable", "Update failed; see reports") == "Update failed; see reports", "provider in status hidden");
Check(UpdatePolicy.ForDisplay("Update failed: raw.githubusercontent.com", "Update failed; see reports") == "Update failed; see reports", "provider host hidden");
Check(UpdatePolicy.ForDisplay(UpdatePolicy.Repository, "See reports") == "See reports", "repository name hidden");
Check(UpdatePolicy.ForDisplay("Installed v0.6.8 successfully.", "See reports") == "Installed v0.6.8 successfully.", "normal result preserved");
Console.WriteLine("PASS: developer-only Reports tab and provider-neutral update display.");

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
Check(!HealthBudget.CanNativeTrial(125,25,25,25,25,25,22,1), "unverified modifier still excluded");
Check(HealthBudget.NativeTrialRejection(125,25,25,25,25,25,22,1,3) == null, "verified permanent +3 modifier eligible");
Check(HealthBudget.Target(25,125) == 32 && HealthBudget.RawTarget(22,25,32) == 29, "scale effective HP once while preserving original +3");
Check(HealthBudget.NativeTrialRejection(125,25,25,25,25,25,22,1,2) != null, "modifier total must explain native maximum");
Check(HealthBudget.NativeTrialRejection(125,25,25,25,25,26,22,1,3) != null, "permanent/effective mismatch excluded");
Check(HealthBudget.NativeTrialRejection(125,25,25,25,25,25,22,17,3) != null, "too many modifiers excluded");
Check(HealthBudget.NativeTrialRejection(125,25,25,25,25,25,22.5f,1,3) != null, "fractional raw max excluded");
Check(HealthBudget.NativeTrialRejection(125,25,25,25,25,25,22,0,3) != null, "unmodified stat cannot claim additive bonus");
var stamps = new[] { new HealthModifierStamp(new IntPtr(101), 3) };
Check(HealthBudget.TryAdditiveBonus(stamps, out int additive) && additive == 3, "verified additive stamp summed");
Check(!HealthBudget.TryAdditiveBonus(new[] { new HealthModifierStamp(IntPtr.Zero, 3) }, out _), "unknown modifier identity rejected");
Check(!HealthBudget.TryAdditiveBonus(new[] { stamps[0], stamps[0] }, out _), "duplicate modifier identity rejected");
Check(!HealthBudget.TryAdditiveBonus(new[] { new HealthModifierStamp(new IntPtr(101), -3) }, out _), "negative modifier left read-only");
Check(!HealthBudget.SameModifierStamps(stamps, new[] { new HealthModifierStamp(new IntPtr(102), 3) }), "replacement modifier identity rejected");
Check(!HealthBudget.SameModifierStamps(stamps, new[] { new HealthModifierStamp(new IntPtr(101), 4) }), "changed modifier value rejected");
Check(HealthBudget.CanNativeTrial(125,14,16,14,16,16,16,0), "consistent injured hero eligible for maximum-only adjustment");
Check(!HealthBudget.CanNativeTrial(125,0,16,0,16,16,16,0), "zero current or downed excluded");
Check(!HealthBudget.CanNativeTrial(125,-1,16,-1,16,16,16,0), "negative current excluded");
Check(!HealthBudget.CanNativeTrial(125,17,16,17,16,16,16,0), "current above maximum excluded");
Check(!HealthBudget.CanNativeTrial(125,14,16,13.5f,16,16,16,0), "fractional or inconsistent value excluded");
Check(!HealthBudget.CanNativeTrial(125,14,16,14,16,16,14,0), "raw maximum disagreement excluded");
Check(!HealthBudget.CanNativeTrial(125,14,16,14,float.NaN,16,16,0), "non-finite maximum excluded");
Check(!HealthBudget.CanNativeTrial(125,14,10001,14,10001,10001,10001,0), "oversize maximum excluded");
Check(HealthBudget.NativeTrialRejection(125,14,16,14,16,16,16,-1) == "modifier count unavailable", "unknown modifiers diagnosed");
Check(HealthBudget.NativeTrialRejection(125,14,16,14,16,16,16,1) == "existing health modifiers", "existing modifiers diagnosed");
Check(HealthBudget.NativeTrialRejection(125,14,16,14,16,16,16,0) == null, "consistent injured baseline is not rejected");
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

var party = new NativeHealthPartyTrial();
Check(!party.EndScope() && !party.Ended, "startup scene changes do not consume an unused party trial");
Check(party.Reserve("", new IntPtr(1), 16, 20, 16) == null, "missing sheet ID rejected");
Check(party.Reserve("hero-A", IntPtr.Zero, 16, 20, 16) == null, "missing stat identity rejected");
var heroA = party.Reserve("hero-A", new IntPtr(1), 16, 20, 16);
Check(heroA != null && heroA.Id == 1 && heroA.Baseline == 16 && heroA.Target == 20, "first hero reserved at original baseline");
Check(party.Find("hero-A") == heroA, "same logical hero found");
Check(party.Reserve("hero-A", new IntPtr(2), 20, 25, 20) == null, "same sheet cannot stack on a replacement stat");
Check(party.Reserve("hero-B", new IntPtr(1), 20, 25, 20) == null, "same native stat cannot scale under a different sheet");
var injuredHero = party.Reserve("hero-B", new IntPtr(2), 12, 15, 9);
Check(injuredHero != null && injuredHero.OriginalCurrent == 9 && injuredHero.LastCurrent == 9 && injuredHero.Target == 15, "second distinct injured hero reserves exact current HP");
Check(party.Reserve("hero-C", new IntPtr(3), 22, 28, 0) == null, "zero HP cannot reserve");
Check(party.Reserve("hero-C", new IntPtr(3), 22, 28, 23) == null, "HP above baseline cannot reserve");
Check(party.Reserve("hero-C", new IntPtr(3), 25, 32, 25, 22, new[] { new HealthModifierStamp(new IntPtr(101), 2) }) == null, "reservation rejects inconsistent modifier");
var modifiedHero = party.Reserve("hero-C", new IntPtr(3), 25, 32, 25, 22, stamps);
Check(modifiedHero != null && modifiedHero.RawBaseline == 22 && modifiedHero.RawTarget == 29 && modifiedHero.Modifiers.Length == 1, "third hero preserves verified original raw/modified maxima");
Check(modifiedHero.OriginalCurrent == 25 && modifiedHero.Target == 32, "verified modified hero preserves current HP");
Check(party.Reserve("hero-D", new IntPtr(4), 24, 30, 24) != null, "fourth hero allowed");
Check(party.Reserve("hero-E", new IntPtr(5), 16, 20, 16) == null && party.Entries.Count == 4, "bounded four-hero trial");
Check(party.EndScope() && party.Ended && !party.EndScope(), "first departure permanently seals trial");
Check(party.Reserve("hero-F", new IntPtr(6), 16, 20, 16) == null, "new scope cannot renew trial");
var earlyEnd = new NativeHealthPartyTrial();
Check(earlyEnd.Reserve("hero-A", new IntPtr(1), 16, 20, 16) != null && earlyEnd.EndScope(), "departure seals a partially applied party");
Check(earlyEnd.Reserve("hero-B", new IntPtr(2), 12, 15, 12) == null, "cannot extend to another adventure");
Console.WriteLine("PASS: party health identity and lifecycle guards.");

var idle = new NativeHealthPartyTrial();
Check(idle.CanQueue(125), "unused health trial can queue regardless of earlier AP activity");
Check(!idle.CanQueue(100), "normal health cannot queue");
Check(!idle.CanQueue(126), "invalid percentage cannot queue");
Check(idle.Reserve("hero-A", new IntPtr(7), 16, 20, 16) != null, "first attempt reserved");
Check(!idle.CanQueue(125), "an attempted hero prevents a second authorization");
Check(idle.EndScope() && !idle.CanQueue(125), "ended scope cannot queue");

var activation = new FreshHealthAuthorization();
var roomA = new IntPtr(101);
var roomB = new IntPtr(102);
Check(!activation.Allows(roomA), "no health write before explicit queue and fresh start");
activation.Queue();
Check(activation.Queued && !activation.Armed && !activation.Allows(roomA), "queue cannot authorize current adventure");
Check(!activation.ClaimFreshStart(2, roomA) && !activation.Queued && !activation.Armed, "multiplayer start consumes and rejects queue");
activation.Queue();
Check(!activation.ClaimFreshStart(1, IntPtr.Zero), "unverified room consumes and rejects queue");
activation.Queue();
activation.Cancel();
Check(!activation.ClaimFreshStart(1, roomA), "existing campaign or cancellation cannot authorize fresh start");
activation.Queue();
Check(activation.ClaimFreshStart(1, roomA) && !activation.Queued && activation.Armed, "one-player fresh room claims queued authorization");
Check(!activation.Allows(roomB) && !activation.Allows(IntPtr.Zero), "different or unknown room cannot authorize health");
Check(activation.Allows(roomA), "matching native room is eligible");
Check(activation.MarkObserved(roomA) && activation.Observed, "verified gameplay binds health scope");
activation.DisableWrites();
Check(!activation.Armed && !activation.Allows(roomA) && activation.MatchesRoom(roomA), "disarm stops writes while preserving read-only room observation");
activation.Cancel();
Check(!activation.Allows(roomA) && !activation.Observed, "departure cancels authorization");
Check(!activation.ClaimFreshStart(1, roomA), "new campaign without another explicit queue cannot reactivate");
activation.Queue();
activation.DisableWrites();
Check(!activation.Queued && !activation.ClaimFreshStart(1, roomA), "cancel queue cannot authorize a later campaign");
activation.Queue();
Check(activation.ClaimFreshStart(1, roomA), "unused trial may claim after explicit requeue");
Check(!activation.ClaimFreshStart(1, roomB) && !activation.HasRoom, "unrequested new campaign invalidates previous room");
Console.WriteLine("PASS: fresh-campaign health authorization guards.");

Check(ApRefundBudget.CanApplyMode(true, 0, false), "mode change applies at idle menu");
Check(!ApRefundBudget.CanApplyMode(false, 0, false), "mode change withheld without known idle scope");
Check(!ApRefundBudget.CanApplyMode(true, 1, false), "mode change withheld with live tracked AP state");
Check(!ApRefundBudget.CanApplyMode(true, 0, true), "mode change withheld during native write");
Check(ApRefundBudget.CanObserveCycle(3, 0), "cycle tracked even when elevated cap adjustment disabled");
Check(!ApRefundBudget.CanObserveCycle(6, 0) && !ApRefundBudget.CanObserveCycle(3, 4), "invalid cycles not tracked");
// A live change never gives a second allowance for refunds already granted in this cycle.
int liveUsed = 1;
Check(!ApRefundBudget.CanRefund(1, liveUsed, 2, false, true, 2, 1, 1, true), "decreasing bonus honours refunds already used");
Check(!ApRefundBudget.CanRefund(0, liveUsed, 2, false, true, 2, 1, 1, true), "disable stops refunds");
Check(ApRefundBudget.CanRefund(3, liveUsed, 2, false, true, 2, 1, 1, true), "increasing permits only remaining allowance");
liveUsed = 3;
Check(!ApRefundBudget.CanRefund(3, liveUsed, 2, false, true, 2, 1, 1, true), "re-enable cannot reset exhausted budget");
Check(ApRefundBudget.CanReset(true, 0, 2, true, true), "verified refill remains a reset while disabled");
Check(!ApRefundBudget.CanReset(true, 2, 2, true, true), "full-to-full duplicate never resets a live budget");
Console.WriteLine("PASS: live AP settings and refund-cycle guards.");

Check(SessionModelGuard.RetainCapture(12, 12), "scene poll retains a model captured in the new scene");
Check(!SessionModelGuard.RetainCapture(11, 12), "old-scene capture cannot authorize a new scene");
Check(!SessionModelGuard.RetainCapture(null, 12), "unknown capture scene is rejected");
Check(SessionModelGuard.IsSameScene(12, "Main", 12, "Main"), "duplicate scene poll cannot end a new scope");
Check(!SessionModelGuard.IsSameScene(11, "Main", 12, "Main"), "different scene handle still resets scope");
Check(SessionModelGuard.MatchesTeardown(new IntPtr(1), new IntPtr(1)), "current model teardown clears its capture");
Check(!SessionModelGuard.MatchesTeardown(new IntPtr(1), new IntPtr(2)), "departed model cannot clear a replacement capture");
Check(!SessionModelGuard.MatchesTeardown(IntPtr.Zero, IntPtr.Zero), "unknown identity is not a match");
Console.WriteLine("PASS: session model lifecycle guards.");

Check(SessionModelGuard.PreferExistingHud(true, 12, 12, false, new IntPtr(1), new IntPtr(2)), "unrelated getter cannot overwrite current HUD");
Check(!SessionModelGuard.PreferExistingHud(true, 12, 13, false, new IntPtr(1), new IntPtr(2)), "new scene permits capture");
Check(!SessionModelGuard.PreferExistingHud(true, 12, 12, true, new IntPtr(1), new IntPtr(2)), "new HUD capture can replace previous HUD");
Check(!SessionModelGuard.PreferExistingHud(true, 12, 12, false, new IntPtr(1), new IntPtr(1)), "same model wrapper can refresh");
Check(SessionModelGuard.SessionRejection(true, true, false, true, true, true, false) == null, "ordinary verified solo remains eligible");
Check(SessionModelGuard.SessionRejection(false, true, false, true, true, true, true) == null, "false UI solo flag can be corroborated by an independently verified solo local room");
Check(SessionModelGuard.SessionRejection(false, true, false, true, true, true, false) != null, "false solo without room proof rejected");
Check(SessionModelGuard.SessionRejection(null, true, false, true, true, true, true) != null, "unknown solo flag rejected");
Check(SessionModelGuard.SessionRejection(false, true, true, true, true, true, true) != null, "explicit multiplayer always rejected");
Check(SessionModelGuard.SessionRejection(true, false, false, true, true, true, true) != null, "nonlocal game rejected");
Check(SessionModelGuard.SessionRejection(false, true, null, true, true, true, true) != null, "unknown multiplayer rejected");
Check(SessionModelGuard.SessionRejection(false, true, false, false, true, true, true) != null, "not in game rejected");
Check(SessionModelGuard.SessionRejection(false, true, false, true, null, true, true) != null, "unknown level state rejected");
Check(SessionModelGuard.SessionRejection(false, true, false, true, true, false, true) != null, "nonhost rejected");
Check(SessionModelGuard.LocalRoomProof(true, true, true, true, 1, 1), "HUD and native local one-player room with connected host corroborate solo");
Check(!SessionModelGuard.LocalRoomProof(false, true, true, true, 1, 1), "generic getter cannot authorize alternative solo proof");
Check(!SessionModelGuard.LocalRoomProof(true, false, true, true, 1, 1), "not a native local room rejected");
Check(!SessionModelGuard.LocalRoomProof(true, true, null, true, 1, 1), "unknown connection rejected");
Check(!SessionModelGuard.LocalRoomProof(true, true, true, false, 1, 1), "nonhost room rejected");
Check(!SessionModelGuard.LocalRoomProof(true, true, true, true, 2, 1), "multiple room players rejected");
Check(!SessionModelGuard.LocalRoomProof(true, true, true, true, 1, 2), "multiple local players rejected");
Check(!SessionModelGuard.LocalRoomProof(true, true, true, true, null, 1), "unknown room player count rejected");
Console.WriteLine("PASS: HUD priority and independent solo room guards.");

// v0.6: a sealed trial cannot be reused, but can be replaced at a verified fresh start.
Check(party.CanQueueNext(125, false), "ended party permits a new fresh campaign");
Check(!party.CanQueueNext(125, true), "bound room blocks second queue");
Check(!party.CanQueueNext(100, false), "vanilla health cannot queue");
var repeat = new FreshHealthAuthorization();
repeat.Queue();
Check(repeat.ClaimFreshStart(1, new IntPtr(501)), "first room claims explicit queue");
Check(!party.CanQueueNext(125, repeat.HasRoom), "no requeue while room bound");
repeat.Cancel();
Check(party.CanQueueNext(125, repeat.HasRoom), "new queue after departure");
repeat.Queue();
Check(!repeat.Allows(new IntPtr(501)) && repeat.ClaimFreshStart(1, new IntPtr(502)), "only new claim authorizes second room");
var nextParty = new NativeHealthPartyTrial();
Check(party.Ended && nextParty.Reserve("hero-A", new IntPtr(501), 16, 20, 16) != null, "fresh party allows original baseline identity");
Check(!nextParty.CanQueueNext(125, false) && !nextParty.CanReserveIdentity("hero-A", new IntPtr(502)), "attempted party cannot renew itself");
var ledgerDir = Path.Combine(Path.GetTempPath(), "bmf-ledger-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(ledgerDir);
try
{
    var path = Path.Combine(ledgerDir, "baseline.txt");
    var ledger = new HealthSafetyLedger(path);
    Check(ledger.Ready && ledger.TryAuthorize("hero-a", new IntPtr(1), 16, 16, 0, out _), "first baseline persisted");
    Check(!ledger.TryAuthorize("hero-a", new IntPtr(2), 20, 20, 0, out _), "compounded hero refused");
    Check(!ledger.TryAuthorize("hero-b", new IntPtr(1), 20, 20, 0, out _), "changed native identity refused");
    Check(ledger.TryAuthorize("hero-a", new IntPtr(2), 16, 16, 0, out _), "same original hero can repeat in fresh room");
    Check(ledger.TryAuthorize("hero-b", new IntPtr(4), 22, 25, 1, out _), "modified original baseline recorded");
    Check(!ledger.TryAuthorize("hero-b", new IntPtr(5), 29, 32, 1, out _), "compounded additive baseline rejected");
    Check(!ledger.TryAuthorize("hero-b", new IntPtr(6), 22, 25, 2, out _), "changed modifier count rejected");
    Check(!File.ReadAllText(path).Contains("hero-a"), "raw identity not persisted");
    var reopened = new HealthSafetyLedger(path);
    Check(!reopened.TryAuthorize("hero-a", new IntPtr(3), 20, 20, 0, out _), "compounding blocked across launches");
    Check(reopened.TryAuthorize("hero-a", new IntPtr(3), 16, 16, 0, out _), "original baseline accepted after restart");
    File.Delete(path);
    Check(!reopened.TryAuthorize("hero-a", new IntPtr(3), 16, 16, 0, out _) && !reopened.Ready, "missing known ledger fails closed");
    File.WriteAllText(path, "bad header");
    Check(!new HealthSafetyLedger(path).Ready, "corrupt ledger fails closed");
}
finally { Directory.Delete(ledgerDir, true); }
Console.WriteLine("PASS: v0.6 fresh-campaign and persistent baseline assertions.");
foreach (var preset in HouseRulePreset.All)
{
    Check(preset.Bonus >= 0 && preset.Bonus <= 3, "preset AP valid");
    Check(HealthBudget.Normalize(preset.HealthPercent) == preset.HealthPercent, "preset health valid");
    Check(preset.Refund, "presets prefer refunds");
    Check(HouseRulePreset.Match(preset.Bonus, preset.HealthPercent, preset.Refund, preset.AdjustExisting) == preset.Name, "preset round trip");
}
Check(HouseRulePreset.Match(2, 175, false, false) == "Custom", "custom preset detection");
Console.WriteLine("PASS: preset preference assertions.");

// Updater policy: accept only intentional versions, commit-pinned GitHub source,
// and safe source archive entries. Never downgrade or execute arbitrary URLs.
Check(UpdatePolicy.IsNewer("0.6.2", "0.6.1"), "newer version detected");
Check(UpdatePolicy.IsNewer("0.6.10", "0.6.9"), "numeric rather than lexical ordering");
Check(!UpdatePolicy.IsNewer("0.6.1", "0.6.1"), "same version ignored");
Check(!UpdatePolicy.IsNewer("0.5.13", "0.6.1"), "downgrade ignored");
Check(!UpdatePolicy.IsNewer("0.6.2-alpha", "0.6.1"), "unknown version format rejected");
Check(!UpdatePolicy.IsNewer("0.6.2.1", "0.6.1"), "unexpected version components rejected");
const string updateSha = "60b3e1d94f7e25271f900531c60b0634831e1710";
Check(UpdatePolicy.ValidCommit(updateSha), "valid pinned commit");
Check(!UpdatePolicy.ValidCommit("main") && !UpdatePolicy.ValidCommit("../evil"), "branch or path cannot pin archive");
Check(UpdatePolicy.ArchiveUrl(updateSha).Host == "codeload.github.com", "archive restricted to GitHub");
Check(UpdatePolicy.ReadVersion("{\"schema\":1,\"version\":\"0.6.2\"}") == "0.6.2", "manifest parsed");
Check(UpdatePolicy.ReadVersion("{\"schema\":2,\"version\":\"0.6.2\"}") == null, "unknown schema refused");
Check(UpdatePolicy.ReadVersion("{\"schema\":1,\"version\":\"garbage\"}") == null, "invalid version refused");
Check(UpdatePolicy.ReadVersion("broken") == null, "malformed manifest refused");
string archiveRoot = "Battlemarked-Mod-Framework-" + updateSha;
Check(UpdatePolicy.SafeEntry(archiveRoot + "/src/Framework/Api.cs", archiveRoot), "proper archive path");
Check(!UpdatePolicy.SafeEntry(archiveRoot + "/../outside.dll", archiveRoot), "zip traversal refused");
Check(!UpdatePolicy.SafeEntry(archiveRoot + "/src/evil.dll:stream", archiveRoot), "alternate stream refused");
Check(!UpdatePolicy.SafeEntry(archiveRoot + "/src/evil.dll ", archiveRoot), "ambiguous Windows name refused");
Check(!UpdatePolicy.SafeEntry(archiveRoot + "/CON", archiveRoot), "Windows device refused");
Check(!UpdatePolicy.SafeEntry("wrong-root/src/file", archiveRoot), "unknown archive root refused");
Console.WriteLine("PASS: updater policy assertions.");

// Window movement remains inside the current display and preserves the drag offset.
var position = new MenuPosition(-1, -1);
Check(position.X(1920, 800) == 560 && position.Y(1080, 530) == 275, "default menu centered");
position.Begin(580, 285, 560, 275);
position.Move(780, 435, 1920, 1080, 800, 530);
Check(position.X(1920, 800) == 760 && position.Y(1080, 530) == 425, "drag preserves pointer offset");
Check(position.End() && !position.Dragging && position.SavedX == 760 && position.SavedY == 425, "release persists position");
Check(position.X(1000, 800) == 200 && position.Y(700, 530) == 170, "screen shrink clamps window");
var reopenedPosition = new MenuPosition(position.SavedX, position.SavedY);
Check(reopenedPosition.X(1920, 800) == 760 && reopenedPosition.Y(1080, 530) == 425, "saved position restored");
reopenedPosition.Begin(780, 435, 760, 425);
reopenedPosition.Move(-100, -100, 1920, 1080, 800, 530);
Check(reopenedPosition.X(1920, 800) == 0 && reopenedPosition.Y(1080, 530) == 0, "left/top clamp");
reopenedPosition.Move(4000, 4000, 1920, 1080, 800, 530);
Check(reopenedPosition.X(1920, 800) == 1120 && reopenedPosition.Y(1080, 530) == 550, "right/bottom clamp");
Check(reopenedPosition.End(), "moved position saved once");
Check(!reopenedPosition.End(), "duplicate release ignored");
var clicked = new MenuPosition(-1, -1);
clicked.Begin(580, 285, 560, 275);
Check(!clicked.End() && clicked.SavedX == -1 && clicked.SavedY == -1, "title click does not persist center");
Console.WriteLine("PASS: menu position and drag assertions.");

// Diagnostic exports are local, bounded and allowlisted; never include the
// native health ledger, settings, or game saves.
var supportRoot = Path.Combine(Path.GetTempPath(), "bmf-support-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(supportRoot);
try
{
    var supportConfig = Path.Combine(supportRoot, "BepInEx", "config", "BattlemarkedModFramework");
    var houseDir = Path.Combine(supportConfig, "org.battlemarked.houserules");
    Directory.CreateDirectory(houseDir);
    File.WriteAllText(Path.Combine(supportConfig, "compatibility-report.txt"), "Framework=0.6.6\n");
    File.WriteAllText(Path.Combine(houseDir, "house-rules-probe.txt"), "Framework=0.6.5\n");
    File.WriteAllText(Path.Combine(houseDir, "hero-health-probe.txt"), new string('x', SupportBundle.MaxReportBytes + 1));
    File.WriteAllText(Path.Combine(houseDir, "health-baselines-v060.txt"), "private");
    File.WriteAllText(Path.Combine(houseDir, "settings.cfg"), "private");
    var supportFile = SupportBundle.Create(supportConfig, supportRoot, "0.6.6");
    using var supportZip = System.IO.Compression.ZipFile.OpenRead(supportFile);
    var names = supportZip.Entries.Select(e => e.FullName).OrderBy(v => v).ToArray();
    Check(names.SequenceEqual(new[] {"compatibility-report.txt", "house-rules-probe.txt", "manifest.txt"}), "only bounded allowlisted reports included");
    using var manifestReader = new StreamReader(supportZip.GetEntry("manifest.txt").Open());
    string supportManifest = manifestReader.ReadToEnd();
    Check(supportManifest.Contains("previous version") && supportManifest.Contains("too large") && supportManifest.Contains("missing"), "export explains old, oversized and missing reports");
}
finally { Directory.Delete(supportRoot, true); }
Console.WriteLine("PASS: support bundle allowlist and manifest assertions.");
