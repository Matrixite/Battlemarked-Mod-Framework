using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BepInEx.Configuration;

namespace Battlemarked.ModFramework;

// No game or Unity API calls take place on background threads. The only remote
// executable code trusted is a versioned, pinned snapshot of the owner's repo.
internal sealed class FrameworkUpdater
{
    private sealed class Offer
    {
        internal string Version, Sha;
    }
    private sealed class Stage
    {
        internal string Folder, Source, Worker, Version, Sha;
    }
    private readonly string gameDir, updatesDir;
    private readonly ConfigFile config;
    private readonly ConfigEntry<bool> checkAutomatically, installAutomatically;
    private Task<Offer> check;
    private Task<Stage> stage;
    private bool stageAutomatic;
    private Offer available;
    private Stage scheduled;
    private Process worker;
    private bool workerReady;
    private DateTime nextCheck = DateTime.MinValue, nextResultRead = DateTime.MinValue;
    private string lastResult = "No previous automatic update";
    internal string Status { get; private set; } = "Waiting for first update check";
    internal string AvailableVersion => available?.Version ?? "None";
    internal bool Busy => check != null || stage != null;
    internal bool Scheduled => scheduled != null;
    internal bool CanInstall => available != null && !Busy && !Scheduled;
    internal bool AutoCheck => checkAutomatically.Value;
    internal bool AutoInstall => installAutomatically.Value;
    internal string LastResult => lastResult;
    private string ReadLastResult()
    {
        try
        {
            string path = Path.Combine(updatesDir, "last-update.txt");
            if (!File.Exists(path)) return "No previous automatic update";
            string value = File.ReadAllText(path).Trim();
            return value.Length > 160 ? value.Substring(0, 160) : value;
        }
        catch { return "Update result unavailable"; }
    }
    private bool AlreadyAttempted(string sha)
    {
        try
        {
            string path = Path.Combine(updatesDir, "last-attempt.sha");
            return File.Exists(path) && File.ReadAllText(path).Trim() == sha;
        }
        catch { return true; }
    }
    internal FrameworkUpdater(string root, ConfigFile settings)
    {
        gameDir = Path.GetFullPath(root);
        updatesDir = Path.Combine(gameDir, "BepInEx", "BMF-Updates");
        config = settings;
        checkAutomatically = config.Bind("Updates", "CheckAutomatically", true, "Check the public Matrixite GitHub repository at startup and every six hours.");
        installAutomatically = config.Bind("Updates", "InstallAutomatically", false, "Opt in to downloading and scheduling newer versioned source updates automatically. Installs after the game closes.");
    }
    internal void ToggleAutoCheck()
    {
        try
        {
            checkAutomatically.Value = !AutoCheck;
            if (!AutoCheck) installAutomatically.Value = false;
            config.Save();
            if (AutoCheck) nextCheck = DateTime.MinValue;
        }
        catch (Exception ex) { Error(ex); }
    }
    internal void ToggleAutoInstall()
    {
        try
        {
            installAutomatically.Value = !AutoInstall;
            if (AutoInstall) { checkAutomatically.Value = true; nextCheck = DateTime.MinValue; }
            config.Save();
        }
        catch (Exception ex) { Error(ex); }
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Battlemarked-Mod-Framework/" + Framework.Version);
        return client;
    }
    private static async Task<byte[]> Download(HttpClient client, Uri uri, int limit)
    {
        // No credentials, redirects, arbitrary URLs, or unbounded buffered bodies.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Update download too large");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Update download too large");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private static async Task<Offer> FindUpdate()
    {
        using var client = NewClient();
        byte[] body = await Download(client, new Uri(UpdatePolicy.RefUrl), 32 * 1024).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        string sha = doc.RootElement.GetProperty("object").GetProperty("sha").GetString();
        if (!UpdatePolicy.ValidCommit(sha)) throw new InvalidDataException("Invalid GitHub commit");
        string json = Encoding.UTF8.GetString(await Download(client, UpdatePolicy.ManifestUrl(sha), 4096).ConfigureAwait(false));
        string version = UpdatePolicy.ReadVersion(json);
        if (version == null) throw new InvalidDataException("Invalid repository update manifest");
        return new Offer { Sha = sha, Version = version };
    }

    private bool OtherWorkerRunning()
    {
        if (!Directory.Exists(updatesDir)) return false;
        try
        {
            using var gate = new FileStream(Path.Combine(updatesDir, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    private void Error(Exception ex)
    {
        string detail = ex.GetBaseException().Message.Replace('\r', ' ').Replace('\n', ' ');
        Status = "Update failed: " + (detail.Length > 120 ? detail.Substring(0, 120) : detail);
    }
    internal void CheckNow()
    {
        if (Busy || Scheduled) return;
        available = null;
        nextCheck = DateTime.UtcNow.AddHours(6);
        Status = "Checking GitHub...";
        check = Task.Run(FindUpdate);
    }

    internal void Install(bool automatic = false)
    {
        if (!CanInstall) return;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installer requires Windows");
            if (OtherWorkerRunning()) throw new InvalidOperationException("Another update worker is active; wait for it to finish");
            string expected = Path.GetFullPath(Path.Combine(gameDir, "BepInEx", "plugins", "BattlemarkedModFramework", "Battlemarked.ModFramework.dll"));
            if (!string.Equals(Path.GetFullPath(typeof(FrameworkUpdater).Assembly.Location), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Install BMF in its standard plugin folder before using automatic updates");
            var offer = available;
            Status = "Downloading and validating source; keep the game open";
            stageAutomatic = automatic;
            stage = Task.Run(() => DownloadStage(offer));
        }
        catch (Exception ex) { Error(ex); }
    }

    private async Task<Stage> DownloadStage(Offer offer)
    {
        if (!UpdatePolicy.IsNewer(offer.Version, Framework.Version)) throw new InvalidDataException("Update is not newer");
        string folder = Path.Combine(updatesDir, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var client = NewClient();
            byte[] bytes = await Download(client, UpdatePolicy.ArchiveUrl(offer.Sha), 16 * 1024 * 1024).ConfigureAwait(false);
            string root = "Battlemarked-Mod-Framework-" + offer.Sha;
            using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                if (archive.Entries.Count > 10000) throw new InvalidDataException("Too many archive entries");
                long expanded = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    if (!UpdatePolicy.SafeEntry(entry.FullName, root) || !seen.Add(entry.FullName.TrimEnd('/')) ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("Unsafe or duplicate archive entry");
                    if (entry.Length < 0 || entry.Length > 16 * 1024 * 1024 || (expanded += entry.Length) > 64 * 1024 * 1024)
                        throw new InvalidDataException("Archive expansion exceeds limit");
                    string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                    if (!target.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Archive escapes stage directory");
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using var input = entry.Open();
                    using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if ((written += read) > entry.Length) throw new InvalidDataException("Archive entry size mismatch");
                        file.Write(buffer, 0, read);
                    }
                    if (written != entry.Length) throw new InvalidDataException("Incomplete archive entry");
                }
            }
            string source = Path.Combine(folder, root);
            if (UpdatePolicy.ReadVersion(File.ReadAllText(Path.Combine(source, UpdatePolicy.ManifestName))) != offer.Version ||
                XDocument.Load(Path.Combine(source, "Directory.Build.props")).Descendants("Version").Single().Value.Trim() != offer.Version ||
                !File.Exists(Path.Combine(source, "scripts", "Build-Install.ps1")) ||
                !File.Exists(Path.Combine(source, "src", "Framework", "Battlemarked.ModFramework.csproj")))
                throw new InvalidDataException("Source archive does not match offered version");
            string script = Path.Combine(folder, "UpdateWorker.ps1");
            using (var resource = typeof(FrameworkUpdater).Assembly.GetManifestResourceStream("BMF.UpdateWorker.ps1")
                ?? throw new InvalidDataException("Embedded update worker missing"))
            using (var output = new FileStream(script, FileMode.CreateNew, FileAccess.Write)) resource.CopyTo(output);
            return new Stage { Folder = folder, Source = source, Worker = script, Version = offer.Version, Sha = offer.Sha };
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch { }
            throw;
        }
    }

    private void LaunchWorker(Stage prepared)
    {
        if (OtherWorkerRunning()) throw new InvalidOperationException("Another updater is active");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell not found");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = prepared.Folder };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", prepared.Worker,
            "-GameDir", gameDir, "-SourceDir", prepared.Source, "-Version", prepared.Version,
            "-Commit", prepared.Sha, "-GamePid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        // Suppress unattended reattempts for this commit after a failure or
        // cancellation. A deliberate Install click can always retry.
        File.WriteAllText(Path.Combine(updatesDir, "last-attempt.sha"), prepared.Sha);
        worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start updater");
        scheduled = prepared;
        workerReady = false;
        Status = "Starting update worker; keep the game open";
    }
    internal void CancelScheduled()
    {
        if (!Scheduled) return;
        try
        {
            File.WriteAllText(Path.Combine(scheduled.Folder, "cancel.request"), "cancel");
            Status = "Cancellation requested; waiting for updater";
        }
        catch (Exception ex) { Error(ex); }
    }
    internal void Tick()
    {
        try
        {
            if (DateTime.UtcNow >= nextResultRead)
            {
                lastResult = ReadLastResult();
                nextResultRead = DateTime.UtcNow.AddSeconds(5);
            }
            if (worker != null && worker.HasExited)
            {
                int exit = worker.ExitCode;
                worker.Dispose();
                worker = null; scheduled = null; available = null; workerReady = false;
                lastResult = ReadLastResult();
                Status = exit == 0 ? LastResult : "Updater exited with code " + exit + "; see update-install.log";
                nextCheck = DateTime.UtcNow.AddHours(6); // no unattended retry loop
            }
            if (scheduled != null && !workerReady && File.Exists(Path.Combine(scheduled.Folder, "worker.ready")))
            {
                workerReady = true;
                if (!File.Exists(Path.Combine(scheduled.Folder, "cancel.request")))
                    Status = "Update queued. Exit game and wait for installer to finish";
            }
            if (check?.IsCompleted == true)
            {
                var finished = check; check = null;
                var offer = finished.GetAwaiter().GetResult();
                available = UpdatePolicy.IsNewer(offer.Version, Framework.Version) ? offer : null;
                Status = available != null ? "Update available: " + offer.Version : "You're up to date (repository: " + offer.Version + ")";
                if (AutoCheck && AutoInstall && available != null)
                {
                    if (AlreadyAttempted(offer.Sha)) Status += " (manual retry available)";
                    else Install(automatic: true);
                }
            }
            if (stage?.IsCompleted == true)
            {
                var finished = stage; stage = null;
                var prepared = finished.GetAwaiter().GetResult();
                if (stageAutomatic && (!AutoCheck || !AutoInstall))
                {
                    Status = "Automatic installation disabled; update not queued";
                    try { Directory.Delete(prepared.Folder, true); } catch { }
                }
                else LaunchWorker(prepared);
            }
            if (!Busy && !Scheduled && AutoCheck && DateTime.UtcNow >= nextCheck && !OtherWorkerRunning()) CheckNow();
        }
        catch (Exception ex) { Error(ex); nextCheck = DateTime.UtcNow.AddHours(6); }
    }
}
