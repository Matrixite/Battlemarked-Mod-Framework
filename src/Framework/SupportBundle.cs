using System.IO.Compression;
using System.Text;

namespace Battlemarked.ModFramework;

// A deliberately small, local-only collection of BMF-authored reports.
// Never enumerate user directories or collect settings, saves, ledgers or raw logs.
internal static class SupportBundle
{
    internal const int MaxReportBytes = 1024 * 1024;
    private const string OutputDirectory = "support";

    internal static string Create(string reportsDirectory, string gameDirectory, string version)
    {
        string output = Path.Combine(reportsDirectory, OutputDirectory);
        Directory.CreateDirectory(output);
        string name = "BMF-support-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip";
        string target = Path.Combine(output, name), temp = target + ".tmp";
        var manifest = new List<string> {
            "Battlemarked Mod Framework support bundle", "Framework=" + version,
            "UTC=" + DateTime.UtcNow.ToString("O"),
            "Local export only. Review the included report text for private information before sharing.",
            "Settings, native health baseline ledger, saves, raw BepInEx logs and installer transcript are excluded.",
            "Reports may be from earlier runs; see each version and last-write timestamp.", ""
        };

        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add(archive, manifest, Path.Combine(reportsDirectory, "compatibility-report.txt"), "compatibility-report.txt", version);
                Add(archive, manifest, Path.Combine(reportsDirectory, "startup-report.txt"), "startup-report.txt", version);
                Add(archive, manifest, Path.Combine(reportsDirectory, "org.battlemarked.houserules", "house-rules-probe.txt"), "house-rules-probe.txt", version);
                Add(archive, manifest, Path.Combine(reportsDirectory, "org.battlemarked.houserules", "hero-health-probe.txt"), "hero-health-probe.txt", version);
                Add(archive, manifest, Path.Combine(reportsDirectory, "org.battlemarked.diagnostics.corelogic", "core-probe.txt"), "core-probe.txt", version);
                Add(archive, manifest, Path.Combine(reportsDirectory, "org.battlemarked.diagnostics.gameplay", "gameplay-probe.txt"), "gameplay-probe.txt", version);
                Add(archive, manifest, Path.Combine(gameDirectory, "BepInEx", "BMF-Updates", "last-update.txt"), "last-update.txt", null);
                Write(archive, "manifest.txt", Encoding.UTF8.GetBytes(string.Join("\n", manifest) + "\n"));
            }
            File.Move(temp, target);
            return target;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    private static void Add(ZipArchive archive, List<string> manifest, string source, string name, string expectedVersion)
    {
        byte[] bytes;
        DateTime modified;
        try
        {
            if (!File.Exists(source)) { manifest.Add(name + ": omitted (missing)"); return; }
            // Refuse redirected individual files, and cap both declared and actual read length.
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            { manifest.Add(name + ": omitted (redirected file)"); return; }
            modified = File.GetLastWriteTimeUtc(source);
            using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaxReportBytes) { manifest.Add(name + ": omitted (too large)"); return; }
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxReportBytes) { manifest.Add(name + ": omitted (too large)"); return; }
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
            if (File.GetLastWriteTimeUtc(source) != modified)
            { manifest.Add(name + ": omitted (changed during export; retry)"); return; }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        { manifest.Add(name + ": omitted (unreadable)"); return; }
        string state = "update history";
        if (expectedVersion != null)
        {
            state = "version not reported";
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
            for (int i = 0; i < 24 && !reader.EndOfStream; i++)
            {
                string line = reader.ReadLine();
                if (line != null && line.StartsWith("Framework=", StringComparison.Ordinal))
                {
                    state = line.Substring("Framework=".Length).Trim() == expectedVersion ? "current version" : "previous version";
                    break;
                }
            }
        }
        Write(archive, name, bytes);
        manifest.Add($"{name}: included ({state}; modified {modified:O}; {bytes.Length} bytes)");
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        entry.Write(bytes, 0, bytes.Length);
    }
}
