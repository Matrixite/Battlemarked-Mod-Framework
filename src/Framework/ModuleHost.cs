namespace Battlemarked.ModFramework;

// Engine-independent lifecycle implementation, also linked into CoreTests.
internal sealed class ModuleHost<T> where T : class
{
    private readonly Dictionary<string, T> modules = new(StringComparer.Ordinal);
    private readonly List<string> order = new();
    private readonly Action<string, T, Exception> failed;
    internal ModuleHost(Action<string, T, Exception> failed) => this.failed = failed;
    internal int Count => modules.Count;
    internal bool Contains(string id) => modules.ContainsKey(id);
    internal bool Add(string id, T module)
    {
        if (!modules.TryAdd(id, module)) return false;
        order.Add(id);
        return true;
    }
    internal bool Remove(string id, Action<T> cleanup)
    {
        if (!modules.Remove(id, out var module)) return false;
        order.Remove(id);
        try { cleanup(module); }
        catch (Exception ex) { failed(id, module, ex); }
        return true;
    }
    internal void Dispatch(Action<T> action, Action<T> cleanup)
    {
        foreach (var id in order.ToArray())
        {
            if (!modules.TryGetValue(id, out var module)) continue;
            try { action(module); }
            catch (Exception ex)
            {
                failed(id, module, ex);
                Remove(id, cleanup);
            }
        }
    }
    internal void Clear(Action<T> cleanup)
    {
        foreach (var id in order.ToArray().Reverse()) Remove(id, cleanup);
    }
}
