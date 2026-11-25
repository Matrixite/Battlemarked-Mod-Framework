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
    private readonly Queue<(string Id, BattlemarkedModule Module)> pending = new();
    private readonly HashSet<string> registered = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private ModuleHost<Entry> host;
    private bool accepting;
    private SceneInfo lastScene;
    private string gameVersion = "unknown", hash = "", dataDir;
    private ConfigEntry<bool> diagnostics;
    private bool initialised;
    private ModMenu menu;
    private readonly Dictionary<string, MenuModule> menuModules = new(StringComparer.Ordinal);
    internal IEnumerable<MenuModule> MenuModules => menuModules.Values.OrderBy(x => x.Id, StringComparer.Ordinal);
    internal bool IsRunning(string id) => host.Contains(id);
    internal string GameVersion => gameVersion;
    internal string SceneName => lastScene?.Name ?? "unknown";
    internal void DrawMenu() => menu?.Draw();
    private sealed class Entry
    {
        public BattlemarkedModule Module { get; }
        public ModuleContext Context { get; }
        public Entry(BattlemarkedModule Module, ModuleContext Context)
        {
            this.Module = Module;
            this.Context = Context;
        }
    }

    public override void Load()
    {
        dataDir = Path.Combine(Paths.ConfigPath, "BattlemarkedModFramework");
        Directory.CreateDirectory(dataDir);
        diagnostics = Config.Bind("Diagnostics", "WriteStartupReport", true, "Write a local diagnostic report on first update.");
        host = new ModuleHost<Entry>((id, entry, ex) => Log.LogError($"Module {id}: {ex}"));
        Instance = this;
        accepting = true;
        try { AddComponent<FrameworkBehaviour>(); }
        catch { accepting = false; Instance = null; throw; }
        Enqueue(new GameplayProbe());
        Enqueue(new CoreLogicProbe());
        Enqueue(new HouseRules());
        menu = new ModMenu(this, Config, Log);
        Log.LogInfo($"BMF {Framework.Version} loaded; waiting for first Unity update.");
    }

    internal bool Enqueue(BattlemarkedModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        string id = module.Id;
        if (id == null || !Regex.IsMatch(id, @"\A[a-z][a-z0-9_-]*(\.[a-z0-9_-]+)+\z") || id == Framework.Guid)
            throw new ArgumentException("Module ID must be a unique lowercase reverse-domain ID.");
        lock (sync)
        {
            if (!accepting || !registered.Add(id)) return false;
            pending.Enqueue((id, module));
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
        (string Id, BattlemarkedModule Module)[] batch;
        lock (sync) { batch = pending.ToArray(); pending.Clear(); }
        foreach (var item in batch) Start(item.Id, item.Module);
        foreach (var signal in GameplayProbe.Drain())
            host.Dispatch(e => e.Module.OnGameplaySignal(signal), Stop);
        foreach (var signal in CoreLogicProbe.Drain())
            host.Dispatch(e => e.Module.OnCoreSignal(signal), Stop);
        host.Dispatch(e => e.Module.OnUpdate(), Stop);
        menu?.Tick();
    }

    private void Start(string id, BattlemarkedModule module)
    {
        Entry entry = null;
        try
        {
            var moduleDir = Path.Combine(dataDir, id);
            Directory.CreateDirectory(moduleDir);
            var config = new ConfigFile(Path.Combine(moduleDir, "settings.cfg"), true);
            var enabled = config.Bind("General", "Enabled", true, "Enable this module on next launch.");
            menuModules[id] = new MenuModule(id, module.Name, enabled);
            if (!enabled.Value)
            { Log.LogInfo($"Module disabled: {id}"); return; }
            var context = new ModuleContext(config, BepInEx.Logging.Logger.CreateLogSource(id), id, gameVersion, hash, moduleDir);
            entry = new Entry(module, context);
            module.OnStart(context);
            if (lastScene != null) module.OnSceneChanged(lastScene);
            host.Add(id, entry);
            Log.LogInfo($"Module started: {id}");
        }
        catch (Exception ex)
        {
            Log.LogError($"Module {id} failed to start: {ex}");
            if (entry != null) Stop(entry);
        }
    }

    private void Stop(Entry entry)
    {
        try { entry.Module.OnStop(); }
        catch (Exception ex) { Log.LogError($"OnStop: {ex}"); }
        try { entry.Context.Patches.UnpatchSelf(); }
        catch (Exception ex) { Log.LogError($"Unpatch: {ex}"); }
        finally { BepInEx.Logging.Logger.Sources.Remove(entry.Context.Log); }
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
        host?.Clear(Stop);
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
    // Hot-unloading is deliberately unsupported; shutdown happens on application quit.
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
