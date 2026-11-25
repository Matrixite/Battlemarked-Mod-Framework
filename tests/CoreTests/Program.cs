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
