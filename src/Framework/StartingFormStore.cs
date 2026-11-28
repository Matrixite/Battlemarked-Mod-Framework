using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Battlemarked.ModFramework;

// Framework-owned preferences, not game saves. Store only a hashed character
// identity and the exact registered monster ID/GUID (never account IDs).
internal sealed class StartingFormAssignment
{
    public StartingFormAssignment() { }
    public string CharacterKey { get; set; }
    public string HeroName { get; set; }
    public string FormId { get; set; }
    public string FormGuid { get; set; }
    public string FormName { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string FormKey => FormId + " | " + FormGuid;
}

internal sealed class StartingFormStore
{
    internal const int MaxEntries = 64;
    private const int MaxBytes = 128 * 1024;
    private readonly string path;
    private Dictionary<string, StartingFormAssignment> entries = new(StringComparer.Ordinal);
    internal StartingFormStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Starting forms file too large.");
        var saved = JsonSerializer.Deserialize<StartingFormAssignment[]>(File.ReadAllBytes(path));
        if (saved == null || saved.Length > MaxEntries) throw new InvalidDataException("Invalid starting forms file.");
        foreach (var item in saved)
        {
            Validate(item);
            if (!entries.TryAdd(item.CharacterKey, item)) throw new InvalidDataException("Duplicate starting character.");
        }
    }

    internal static string CharacterKey(string sheet, string originalKey, string account)
    {
        if (string.IsNullOrWhiteSpace(sheet) || string.IsNullOrWhiteSpace(originalKey)) return null;
        string identity;
        if (Guid.TryParse(sheet, out var guid) && guid != Guid.Empty)
            identity = "guid:" + guid.ToString("N");
        else
        {
            if (string.IsNullOrWhiteSpace(account)) return null;
            identity = "account:" + account.Length + ":" + account + sheet.Length + ":" + sheet;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + originalKey.Length + ":" + originalKey)));
    }

    private static bool ValidText(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static void Validate(StartingFormAssignment item)
    {
        if (item == null || item.CharacterKey == null || item.CharacterKey.Length != 64 ||
            !ValidText(item.HeroName) || !ValidText(item.FormId) || !ValidText(item.FormGuid) || !ValidText(item.FormName))
            throw new InvalidDataException("Invalid starting-form assignment.");
        foreach (char c in item.CharacterKey)
            if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')) throw new InvalidDataException("Invalid character key.");
    }

    internal StartingFormAssignment Get(string key) => key != null && entries.TryGetValue(key, out var item) ? item : null;
    internal List<StartingFormAssignment> List() => new(entries.Values);
    internal void Set(StartingFormAssignment item)
    {
        Validate(item);
        var next = new Dictionary<string, StartingFormAssignment>(entries, StringComparer.Ordinal);
        next[item.CharacterKey] = item;
        Save(next);
    }
    internal void Clear(string key)
    {
        var next = new Dictionary<string, StartingFormAssignment>(entries, StringComparer.Ordinal);
        if (next.Remove(key)) Save(next);
    }
    private void Save(Dictionary<string, StartingFormAssignment> next)
    {
        if (next.Count > MaxEntries) throw new InvalidDataException("Too many saved starting forms.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new List<StartingFormAssignment>(next.Values));
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Starting forms file too large.");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
            entries = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// One starting-form attempt per character per adventure, not per level.
// A manual form/reversion or preference edit on the current board cannot
// trigger another automatic starting transformation in that adventure.
internal sealed class StartingFormRun
{
    private string session, campaign, scenario, run, firstStep;
    private int firstIndex;
    private readonly HashSet<string> attempted = new(StringComparer.Ordinal);
    private readonly HashSet<string> deferred = new(StringComparer.Ordinal);
    private readonly HashSet<string> relocated = new(StringComparer.Ordinal);
    internal bool FirstBoard { get; private set; }
    internal long Epoch { get; private set; }
    internal bool Observe(string session, string campaign, string scenario, string run, string step, int index)
    {
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(scenario) ||
            string.IsNullOrWhiteSpace(step) || index < 0) return false;
        bool sameRun = !string.IsNullOrWhiteSpace(run) && run == this.run;
        bool changedRun = !string.IsNullOrWhiteSpace(run) && !string.IsNullOrWhiteSpace(this.run) && run != this.run;
        if (this.session == null || this.campaign != campaign || this.scenario != scenario ||
            changedRun || (this.session != session && !sameRun))
        {
            this.campaign = campaign; this.scenario = scenario; this.run = run;
            firstStep = step; firstIndex = index; FirstBoard = true;
            attempted.Clear(); deferred.Clear(); relocated.Clear(); Epoch++;
        }
        else if (firstIndex != index || firstStep != step) FirstBoard = false;
        this.session = session;
        if (!string.IsNullOrWhiteSpace(run)) this.run = run;
        return true;
    }
    internal bool MatchesFirstBoard(string session, string campaign, string scenario, string run, string step, int index) =>
        FirstBoard && this.session == session && this.campaign == campaign && this.scenario == scenario &&
        (this.run == run || string.IsNullOrWhiteSpace(this.run)) && firstStep == step && firstIndex == index;
    internal bool CanRelocate(string key) => CanApply(key) && !relocated.Contains(key);
    internal void MarkRelocation(string key) => relocated.Add(key);
    internal void MarkAttempt(string key) => attempted.Add(key);
    internal void Defer(string key) { attempted.Add(key); deferred.Add(key); }
    internal bool CanApply(string key) => FirstBoard && !attempted.Contains(key);
    internal bool HasPriority(string key) => FirstBoard && !deferred.Contains(key);
}
