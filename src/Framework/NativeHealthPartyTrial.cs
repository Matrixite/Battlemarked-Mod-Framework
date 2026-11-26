namespace Battlemarked.ModFramework;

// Session-local reservations only. Keep stable sheet IDs and stat identities private;
// once a write is attempted neither identity can be authorized a second time.
internal sealed class NativeHealthPartyTrial
{
    internal const int Maximum = 4;
    internal sealed class Entry
    {
        internal int Id, Baseline, Target, OriginalCurrent, LastCurrent, LastMax, HighestCurrent;
        internal IntPtr StatKey;
        internal bool Applied, RolledBack, RollbackFailed;
        internal int Observations, Verified, Lost, AboveBaseline, ReplacedStat;
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly HashSet<IntPtr> keys = new();
    internal IReadOnlyCollection<Entry> Entries => entries.Values;
    internal bool Ended { get; private set; }
    internal bool CanQueue(int percent)
        => HealthBudget.Normalize(percent) > 100 && !Ended && entries.Count == 0;

    internal Entry Find(string sheetId) => entries.TryGetValue(sheetId, out var entry) ? entry : null;

    internal Entry Reserve(string sheetId, IntPtr key, int baseline, int target, int current)
    {
        if (Ended || string.IsNullOrWhiteSpace(sheetId) || key == IntPtr.Zero ||
            entries.Count >= Maximum || entries.ContainsKey(sheetId) || keys.Contains(key) ||
            baseline <= 0 || target <= baseline || current <= 0 || current > baseline) return null;
        var entry = new Entry { Id = entries.Count + 1, StatKey = key, Baseline = baseline,
            Target = target, OriginalCurrent = current, LastCurrent = current, LastMax = baseline, HighestCurrent = current };
        entries.Add(sheetId, entry);
        keys.Add(key);
        return entry;
    }

    // Initial splash/menu transitions are allowed before any reservation, but once an
    // attempt occurs, teardown or scene change permanently seals the trial for this process.
    internal bool EndScope()
    {
        if (Ended || entries.Count == 0) return false;
        Ended = true;
        return true;
    }
}

// Authorization is in memory and bound to one native room only. A queued
// preference explicitly does not authorize the adventure already in progress.
internal sealed class FreshHealthAuthorization
{
    private IntPtr room;
    internal bool Queued { get; private set; }
    internal bool Armed { get; private set; }
    internal bool Observed { get; private set; }
    internal bool HasRoom => room != IntPtr.Zero;
    internal void Queue()
    {
        Cancel();
        Queued = true;
    }

    internal bool ClaimFreshStart(int localPlayers, IntPtr verifiedLocalRoom)
    {
        bool requested = Queued;
        Cancel();
        if (!requested || localPlayers != 1 || verifiedLocalRoom == IntPtr.Zero) return false;
        room = verifiedLocalRoom;
        Armed = true;
        return true;
    }

    internal bool MatchesRoom(IntPtr liveRoom) => HasRoom && liveRoom == room;
    internal bool Allows(IntPtr liveRoom) => Armed && MatchesRoom(liveRoom);
    internal bool MarkObserved(IntPtr liveRoom)
    {
        if (!MatchesRoom(liveRoom)) return false;
        Observed = true;
        return true;
    }

    internal void DisableWrites()
    {
        Queued = false;
        Armed = false;
        // Preserve only read-only observation of an already observed room.
        if (!Observed) Cancel();
    }

    internal void Cancel()
    {
        room = IntPtr.Zero;
        Queued = Armed = Observed = false;
    }
}
