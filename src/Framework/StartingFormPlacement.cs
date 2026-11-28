namespace Battlemarked.ModFramework;

// Pure bounded nearest-reachable search; native terrain/footprint checks are
// supplied by the caller. Never search through walls or occupied tiles.
internal interface IStartingFormTerrain
{
    bool CanStep(int fromX, int fromY, int toX, int toY);
    bool FitsForm(int x, int y);
}

internal static class StartingFormPlacement
{
    internal const int MaxDistance = 10;
    private static readonly int[] dx = { 1, 0, -1, 0 }, dy = { 0, 1, 0, -1 };
    private sealed class Node
    {
        internal readonly int X, Y, Distance;
        internal Node(int x, int y, int distance) { X = x; Y = y; Distance = distance; }
    }
    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
    internal static bool TryFind(IStartingFormTerrain terrain, int x, int y, out int destX, out int destY)
    {
        destX = x; destY = y;
        // The original location is not a relocation target.
        if (terrain == null || terrain.FitsForm(x, y)) return false;
        var queue = new Queue<Node>();
        var visited = new HashSet<long>();
        queue.Enqueue(new Node(x, y, 0)); visited.Add(Key(x, y));
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (next.Distance != 0 && terrain.FitsForm(next.X, next.Y))
            { destX = next.X; destY = next.Y; return true; }
            if (next.Distance >= MaxDistance) continue;
            for (int i = 0; i < dx.Length; i++)
            {
                long a = (long)next.X + dx[i], b = (long)next.Y + dy[i];
                if (a < int.MinValue || a > int.MaxValue || b < int.MinValue || b > int.MaxValue) continue;
                int tx = (int)a, ty = (int)b;
                long key = Key(tx, ty);
                if (visited.Contains(key) || !terrain.CanStep(next.X, next.Y, tx, ty)) continue;
                visited.Add(key); queue.Enqueue(new Node(tx, ty, next.Distance + 1));
            }
        }
        return false;
    }
}
