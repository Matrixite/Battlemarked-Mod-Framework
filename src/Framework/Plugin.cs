using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Battlemarked.ModFramework;

[BepInPlugin(Framework.Guid, "Battlemarked Mod Framework", Framework.Version)]
public sealed class Plugin : BasePlugin
{
    internal static Plugin Instance { get; private set; }
    private readonly Queue<Registration> pending = new();
    private readonly HashSet<string> registered = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private ModuleHost<Entry> host;
    private bool accepting;
    private SceneInfo lastScene;
    private string gameVersion = "unknown", hash = "", dataDir;
    private ConfigEntry<bool> diagnostics;
    private bool initialised;
    private ModMenu menu;
    private MainMenuProbe mainMenu;
    internal FrameworkUpdater Updater { get; private set; }
    private readonly Dictionary<string, MenuModule> menuModules = new(StringComparer.Ordinal);
    internal IEnumerable<MenuModule> MenuModules => menuModules.Values.OrderBy(x => x.Id, StringComparer.Ordinal);
    internal bool IsRunning(string id) => host.Contains(id);
    internal bool IsModuleRequested(string id) => menuModules.TryGetValue(id, out var module) && module.Enabled.Value;
    internal string ModuleSwitchStatus => mainMenu?.Status ?? "Waiting for startup";
    internal string DescribeModule(MenuModule module)
    {
        bool running = IsRunning(module.Id);
        string state = running ? "Running" : "Not running";
        if (module.RestartBlocked && !running) return state + " | Restart required (safety lock)";
        if (running == module.Enabled.Value) return state;
        string change = module.Enabled.Value ? "enable" : "disable";
        if (module.Factory == null || mainMenu?.Available != true)
            return state + " | " + change + " on restart";
        return state + " | Pending " + change + (mainMenu.Ready ? " (applying)" : " (return to main menu)");
    }
    private DateTime lastCompatibilityReport;
    internal bool VerifiedGame => string.Equals(hash, CoreLogicProbe.VerifiedHash, StringComparison.OrdinalIgnoreCase);
    internal string CompatibilityStatus => VerifiedGame ? "Verified game fingerprint" :
        string.IsNullOrEmpty(hash) ? "Game fingerprint unavailable" : "Unverified game build";
    internal string GameVersion => gameVersion;
    internal string SceneName => lastScene?.Name ?? "unknown";
    internal void DrawMenu() => menu?.Draw();
    private sealed class Registration
    {
        internal readonly string Id;
        internal readonly BattlemarkedModule Module;
        internal readonly Func<BattlemarkedModule> Factory;
        internal Registration(string id, BattlemarkedModule module, Func<BattlemarkedModule> factory)
        { Id = id; Module = module; Factory = factory; }
    }
    private sealed class Entry
    {
        public BattlemarkedModule Module { get; }
        public ModuleContext Context { get; }
        internal MenuModule Registration { get; }
        public Entry(BattlemarkedModule module, ModuleContext context, MenuModule registration)
        { Module = module; Context = context; Registration = registration; }
    }
    private static BattlemarkedModule CreateGameplayProbe() => new GameplayProbe();
    private static BattlemarkedModule CreateCoreProbe() => new CoreLogicProbe();
    private static BattlemarkedModule CreateHouseRules() => new HouseRules();

    public override void Load()
    {
        dataDir = Path.Combine(Paths.ConfigPath, "BattlemarkedModFramework");
        Directory.CreateDirectory(dataDir);
        diagnostics = Config.Bind("Diagnostics", "WriteStartupReport", true, "Write a local diagnostic report on first update.");
        host = new ModuleHost<Entry>(OnModuleFailure);
        Instance = this;
        accepting = true;
        try { AddComponent<FrameworkBehaviour>(); }
        catch { accepting = false; Instance = null; throw; }
        EnqueueFactory(CreateGameplayProbe);
        EnqueueFactory(CreateCoreProbe);
        EnqueueFactory(CreateHouseRules);
        try { Updater = new FrameworkUpdater(Paths.GameRootPath, Config); }
        catch (Exception ex) { Log.LogWarning("BMF updater unavailable: " + ex.Message); }
        menu = new ModMenu(this, Config, Log);
        Log.LogInfo($"BMF {Framework.Version} loaded; waiting for first Unity update.");
    }

    internal bool EnqueueFactory(Func<BattlemarkedModule> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Enqueue(factory(), factory);
    }

    internal bool Enqueue(BattlemarkedModule module, Func<BattlemarkedModule> factory = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        string id = module.Id;
        if (id == null || !Regex.IsMatch(id, @"\A[a-z][a-z0-9_-]*(\.[a-z0-9_-]+)+\z") || id == Framework.Guid)
            throw new ArgumentException("Module ID must be a unique lowercase reverse-domain ID.");
        lock (sync)
        {
            if (!accepting || !registered.Add(id)) return false;
            pending.Enqueue(new Registration(id, module, factory));
            return true;
        }
    }

    internal void Tick()
    {
        if (!initialised)
        {
            initialised = true;
            try { gameVersion = Application.version; }
            catch (Exception ex) { Log.LogWarning(ex.Message); }
            try
            {
                using var file = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
                using var sha = SHA256.Create();
                hash = Convert.ToHexString(sha.ComputeHash(file));
            }
            catch (Exception ex) { Log.LogWarning($"Cannot fingerprint game: {ex.Message}"); }
            mainMenu = new MainMenuProbe(VerifiedGame, Log);
            if (diagnostics.Value) WriteReport();
            Log.LogInfo($"BMF ready. Game version: {gameVersion}");
        }
        SceneInfo current = lastScene;
        try
        {
            var scene = SceneManager.GetActiveScene();
            current = new SceneInfo(scene.name ?? "", scene.handle, scene.buildIndex);
        }
        catch (Exception ex) { Log.LogDebug($"Scene lookup: {ex.Message}"); }
        if (current != null && current != lastScene)
        {
            lastScene = current;
            host.Dispatch(e => e.Module.OnSceneChanged(current), Stop);
        }
        Registration[] batch;
        lock (sync) { batch = pending.ToArray(); pending.Clear(); }
        foreach (var item in batch) RegisterModule(item);
        ApplyModuleChanges();
        foreach (var signal in GameplayProbe.Drain())
            host.Dispatch(e => e.Module.OnGameplaySignal(signal), Stop);
        foreach (var signal in CoreLogicProbe.Drain())
            host.Dispatch(e => e.Module.OnCoreSignal(signal), Stop);
        host.Dispatch(e => e.Module.OnUpdate(), Stop);
        Updater?.Tick();
        menu?.Tick();
        if (diagnostics.Value && (DateTime.UtcNow - lastCompatibilityReport).TotalSeconds >= 15)
            RefreshCompatibilityReport();
    }

    private void RegisterModule(Registration registration)
    {
        string id = registration.Id;
        try
        {
            var moduleDir = Path.Combine(dataDir, id);
            Directory.CreateDirectory(moduleDir);
            var config = new ConfigFile(Path.Combine(moduleDir, "settings.cfg"), true);
            var enabled = config.Bind("General", "Enabled", true, "Enable this module. Menu-safe modules apply at the verified main menu; others on next launch.");
            var slot = new MenuModule(id, registration.Module.Name, moduleDir, config, enabled, registration.Factory);
            menuModules[id] = slot;
            if (enabled.Value) StartModule(slot, registration.Module);
            else { slot.LastInstance = registration.Module; Log.LogInfo($"Module disabled: {id}"); }
        }
        catch (Exception ex) { Log.LogError($"Module {id} registration failed: {ex}"); }
    }

    private void StartModule(MenuModule slot, BattlemarkedModule module)
    {
        Entry entry = null;
        try
        {
            if (module == null || module.Id != slot.Id || ReferenceEquals(slot.LastInstance, module))
                throw new InvalidOperationException("Factory must create a fresh module with the same ID");
            slot.LastInstance = module;
            var context = new ModuleContext(slot.Config, BepInEx.Logging.Logger.CreateLogSource(slot.Id), slot.Id, gameVersion, hash, slot.DataDirectory);
            entry = new Entry(module, context, slot);
            module.OnStart(context);
            if (lastScene != null) module.OnSceneChanged(lastScene);
            if (!host.Add(slot.Id, entry)) throw new InvalidOperationException("Module already running");
            Log.LogInfo($"Module started: {slot.Id}");
        }
        catch (Exception ex)
        {
            slot.RestartBlocked = true;
            Log.LogError($"Module {slot.Id} failed to start: {ex}");
            if (entry != null) Stop(entry);
        }
    }

    // Run only from Unity Update; the GUI saves a preference, not a patch operation.
    // Stop requested modules first; then start fresh instances. Failures are not retried.
    private void ApplyModuleChanges()
    {
        if (mainMenu?.Ready != true) return;
        var slots = menuModules.Values.ToArray();
        for (int i = slots.Length - 1; i >= 0; i--)
        {
            var slot = slots[i];
            if (ModuleSwitchPolicy.Plan(mainMenu.Ready, slot.Factory != null, slot.RestartBlocked, IsRunning(slot.Id), slot.Enabled.Value) == ModuleChange.Stop)
                host.Remove(slot.Id, Stop);
        }
        foreach (var slot in slots)
        {
            if (ModuleSwitchPolicy.Plan(mainMenu.Ready, slot.Factory != null, slot.RestartBlocked, IsRunning(slot.Id), slot.Enabled.Value) != ModuleChange.Start) continue;
            try
            {
                var module = slot.Factory();
                if (mainMenu.Ready && slot.Enabled.Value) StartModule(slot, module);
            }
            catch (Exception ex)
            {
                slot.RestartBlocked = true;
                Log.LogError($"Module {slot.Id} factory failed: {ex}");
            }
        }
    }

    private void OnModuleFailure(string id, Entry entry, Exception error)
    {
        entry.Registration.RestartBlocked = true;
        Log.LogError($"Module {id}: {error}");
    }

    private void Stop(Entry entry)
    {
        // A health/AP safety fault or unresolved write cannot be cleared by toggling.
        if (entry.Module is HouseRules rules && rules.PreventsReactivation)
            entry.Registration.RestartBlocked = true;
        try { entry.Module.OnStop(); }
        catch (Exception ex) { OnModuleFailure(entry.Registration.Id, entry, ex); }
        try { entry.Context.Patches.UnpatchSelf(); }
        catch (Exception ex) { OnModuleFailure(entry.Registration.Id, entry, ex); }
        finally
        {
            try { BepInEx.Logging.Logger.Sources.Remove(entry.Context.Log); }
            catch (Exception ex) { OnModuleFailure(entry.Registration.Id, entry, ex); }
            if (entry.Module is HouseRules stoppedRules && stoppedRules.PreventsReactivation)
                entry.Registration.RestartBlocked = true;
        }
        Log.LogInfo("Module stopped: " + entry.Registration.Id +
            (entry.Registration.RestartBlocked ? " | restart required (safety lock)" : ""));
    }

    internal bool RefreshCompatibilityReport()
    {
        lastCompatibilityReport = DateTime.UtcNow;
        try
        {
            var lines = new List<string> {
                $"Framework={Framework.Version}", $"GameVersion={gameVersion}", $"GameAssemblySHA256={hash}",
                "Compatibility=" + CompatibilityStatus, "Scene=" + SceneName,
                "UTC=" + DateTime.UtcNow.ToString("O"),
                "Save safety: Native health trials only in explicitly queued fresh disposable local solo campaigns.",
                "AP changes are local solo only; cap mode may persist changes. Prefer refunds.",
                "No automated save editing, backup or repair. Back up saves separately before experimenting.",
                "Unknown builds and unknown native identities are ineligible for house-rule writes.",
                "Previously modified saves (including pre-v0.6) are not proven clean by the baseline ledger.",
                "Module switch gate: " + ModuleSwitchStatus,
                "Module switch binding: " + (mainMenu?.DiagnosticDetail ?? "not initialized"),
                "Modules:"
            };
            lines.AddRange(MenuModules.Select(m => $"{m.Id}: running={IsRunning(m.Id)} requested={m.Enabled.Value} menuSafe={m.Factory != null} restartBlocked={m.RestartBlocked}"));
            lines.AddRange(HouseRules.CompatibilityLines());
            if (Updater != null)
            {
                lines.Add("Framework update: " + Updater.Status);
                lines.Add($"Update auto-check={Updater.AutoCheck} auto-install={Updater.AutoInstall}");
                lines.Add("Last update: " + Updater.LastResult);
            }
            lines.AddRange(CoreLogicProbe.Summary());
            File.WriteAllLines(Path.Combine(dataDir, "compatibility-report.txt"), lines);
            return true;
        }
        catch (Exception ex) { Log.LogWarning("Compatibility report: " + ex.Message); return false; }
    }

    internal string ExportSupportBundle()
    {
        if (!RefreshCompatibilityReport())
            throw new IOException("Could not refresh the compatibility report");
        HouseRules.RefreshReports();
        return Path.GetFileName(SupportBundle.Create(dataDir, Paths.GameRootPath, Framework.Version));
    }

    private void WriteReport()
    {
        try
        {
            var lines = new List<string> { $"Framework={Framework.Version}", $"GameVersion={gameVersion}",
                $"GameAssemblySHA256={hash}", $"Unity={Application.unityVersion}", "Loaded managed assemblies:" };
            lines.AddRange(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().FullName).OrderBy(s => s, StringComparer.Ordinal));
            File.WriteAllLines(Path.Combine(dataDir, "startup-report.txt"), lines);
        }
        catch (Exception ex) { Log.LogWarning($"Report failed: {ex.Message}"); }
    }

    internal void Shutdown()
    {
        lock (sync) { accepting = false; pending.Clear(); }
        mainMenu?.Dispose();
        host?.Clear(Stop);
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
    // The BepInEx plugin itself is never hot-unloaded; opt-in modules can restart at the main menu.
    public override bool Unload() => false;
}

public sealed class FrameworkBehaviour : MonoBehaviour
{
    public FrameworkBehaviour(IntPtr ptr) : base(ptr) { }
    public void Update() => Plugin.Instance?.Tick();
    public void OnGUI() => Plugin.Instance?.DrawMenu();
    public void OnApplicationQuit() => Plugin.Instance?.Shutdown();
    public void OnDestroy() => Plugin.Instance?.Shutdown();
}
