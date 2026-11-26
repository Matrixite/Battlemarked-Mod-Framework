using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Battlemarked.ModFramework;

// Local, fail-closed baseline journal. Never stores native addresses or raw sheet IDs.
// A known hero must present precisely its original maximum, even after a restart.
internal sealed class HealthSafetyLedger
{
    private const string Header = "BMF-HEALTH-BASELINES-V1";
    private const int Limit = 4096;
    private readonly string path;
    private readonly Dictionary<string, Baseline> known = new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr, Baseline> native = new();
    private readonly record struct Baseline(int Raw, int Effective, int Count);
    internal bool Ready { get; private set; }
    internal int Rejections { get; private set; }
    internal string Summary => $"Baseline ledger: {(Ready ? "ready" : "blocked")} | identities={known.Count} | rejections={Rejections}";

    internal HealthSafetyLedger(string file)
    {
        path = file;
        try
        {
            foreach (var entry in Read()) known.Add(entry.Key, entry.Value);
            Ready = true;
        }
        catch { Ready = false; }
    }

    private static bool Valid(Baseline b) => b.Raw > 0 && b.Raw <= 10000 && b.Effective >= b.Raw &&
        b.Effective <= 10000 && b.Count >= 0 && b.Count <= 16 && (b.Count != 0 || b.Raw == b.Effective);

    private Dictionary<string, Baseline> Read()
    {
        var result = new Dictionary<string, Baseline>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("Ledger too large");
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0] != Header || lines.Length > Limit + 1)
            throw new InvalidDataException("Ledger header or count invalid");
        foreach (string line in lines.Skip(1))
        {
            var parts = line.Split('|');
            if (parts.Length != 4 || parts[0].Length != 64 || parts[0].Any(c => !"0123456789ABCDEF".Contains(c)) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int raw) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int effective) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                throw new InvalidDataException("Ledger record invalid");
            var b = new Baseline(raw, effective, count);
            if (!Valid(b) || !result.TryAdd(parts[0], b)) throw new InvalidDataException("Ledger baseline invalid");
        }
        return result;
    }

    private bool Reject(string detail, out string reason) { Rejections++; reason = detail; return false; }

    internal bool TryAuthorize(string sheetId, IntPtr key, int raw, int effective, int count, out string reason)
    {
        reason = null;
        var baseline = new Baseline(raw, effective, count);
        if (!Ready || string.IsNullOrWhiteSpace(sheetId) || key == IntPtr.Zero || !Valid(baseline))
            return Reject("baseline ledger unavailable or invalid identity/readings", out reason);
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sheetId)));
        // Refuse changed raw maxima, even if the game supplies a new stat object.
        if (native.TryGetValue(key, out var priorNative) && priorNative != baseline)
            return Reject("native stat baseline changed", out reason);
        string temp = null;
        try
        {
            using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var disk = Read();
            if (known.Any(e => !disk.TryGetValue(e.Key, out var b) || b != e.Value))
                throw new InvalidDataException("Ledger disappeared or changed");
            foreach (var e in disk) known[e.Key] = e.Value;
            if (known.TryGetValue(id, out var prior))
            {
                if (prior != baseline) return Reject("known hero baseline changed; use an unmodified fresh game", out reason);
            }
            else
            {
                if (known.Count >= Limit) return Reject("baseline ledger capacity reached", out reason);
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var lines = new List<string> { Header };
                foreach (var e in known.Append(new KeyValuePair<string, Baseline>(id, baseline)).OrderBy(e => e.Key, StringComparer.Ordinal))
                    lines.Add(FormattableString.Invariant($"{e.Key}|{e.Value.Raw}|{e.Value.Effective}|{e.Value.Count}"));
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(file, new UTF8Encoding(false), 1024, leaveOpen: true))
                        foreach (string line in lines) writer.WriteLine(line);
                    file.Flush(true);
                }
                File.Move(temp, path, true); // same-directory atomic replacement
                known.Add(id, baseline);
            }
            native[key] = baseline;
            return true;
        }
        catch
        {
            Ready = false;
            return Reject("baseline ledger could not be verified or safely saved", out reason);
        }
        finally { if (temp != null) try { File.Delete(temp); } catch { } }
    }
}
