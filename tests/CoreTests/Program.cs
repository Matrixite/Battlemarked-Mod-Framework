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

for (int bonus = 0; bonus <= 3; bonus++)
{
    int current = 2, used = 0, actions = 0;
    while (current > 0)
    {
        int before = current;
        current--; actions++;
        if (ApRefundBudget.CanRefund(bonus, used, true, before, 1, current, current == 1))
        { used++; current = 2; }
        Check(actions <= 5, "refund budget bounded");
    }
    Check(actions == 2 + bonus && used == bonus, "expected action count");
    Check(!ApRefundBudget.CanRefund(bonus, used, true, 2, 1, 1, true), "budget exhausted");
}
Check(!ApRefundBudget.CanRefund(3, 0, false, 2, 1, 1, true), "unverified before");
Check(!ApRefundBudget.CanRefund(3, 0, true, 2, 1, 1, false), "unverified after");
Check(!ApRefundBudget.CanRefund(3, 0, true, 1, 1, 0, false), "no refund after empty");
Check(!ApRefundBudget.CanRefund(3, 0, true, 2, 2, 0, false), "two-point spend not refunded");
Check(!ApRefundBudget.CanRefund(3, 0, true, 2, 0, 2, false), "free action not refunded");
Check(ApRefundBudget.CanReset(true, 0, true), "empty to full refill resets");
Check(!ApRefundBudget.CanReset(true, 1, true), "partial refill does not reset");
Check(!ApRefundBudget.CanReset(true, 2, true), "duplicate full refill does not reset");
Check(!ApRefundBudget.CanReset(false, 0, true), "unverified empty does not reset");
Check(!ApRefundBudget.CanReset(true, 0, false), "unverified full does not reset");
Console.WriteLine("PASS: AP refund guard and budget scenarios.");
