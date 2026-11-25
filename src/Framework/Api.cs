using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Battlemarked.ModFramework;

/// <summary>Observed presentation handler calls, not authoritative game actions.
/// Calls are coalesced per Unity update; replay and duplicate calls are possible.</summary>
public sealed class GameplaySignal : IEquatable<GameplaySignal>
{
    public string Kind { get; }
    public long CallsSinceLastUpdate { get; }
    public long TotalCalls { get; }
    public GameplaySignal(string Kind, long CallsSinceLastUpdate, long TotalCalls)
    {
        this.Kind = Kind;
        this.CallsSinceLastUpdate = CallsSinceLastUpdate;
        this.TotalCalls = TotalCalls;
    }
    public bool Equals(GameplaySignal other) => other is not null && Kind == other.Kind && CallsSinceLastUpdate == other.CallsSinceLastUpdate && TotalCalls == other.TotalCalls;
    public override bool Equals(object obj) => obj is GameplaySignal other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, CallsSinceLastUpdate, TotalCalls);
    public static bool operator ==(GameplaySignal left, GameplaySignal right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));
    public static bool operator !=(GameplaySignal left, GameplaySignal right) => !(left == right);
    public void Deconstruct(out string Kind, out long CallsSinceLastUpdate, out long TotalCalls)
    {
        Kind = this.Kind;
        CallsSinceLastUpdate = this.CallsSinceLastUpdate;
        TotalCalls = this.TotalCalls;
    }
}

public sealed class SceneInfo : IEquatable<SceneInfo>
{
    public string Name { get; }
    public int Handle { get; }
    public int BuildIndex { get; }
    public SceneInfo(string Name, int Handle, int BuildIndex)
    {
        this.Name = Name;
        this.Handle = Handle;
        this.BuildIndex = BuildIndex;
    }
    public bool Equals(SceneInfo other) => other is not null && Name == other.Name && Handle == other.Handle && BuildIndex == other.BuildIndex;
    public override bool Equals(object obj) => obj is SceneInfo other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Name, Handle, BuildIndex);
    public static bool operator ==(SceneInfo left, SceneInfo right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));
    public static bool operator !=(SceneInfo left, SceneInfo right) => !(left == right);
    public void Deconstruct(out string Name, out int Handle, out int BuildIndex)
    {
        Name = this.Name;
        Handle = this.Handle;
        BuildIndex = this.BuildIndex;
    }
}

/// <summary>Callbacks run on the Unity main thread. Registration takes effect on the next update.</summary>
public abstract class BattlemarkedModule
{
    public abstract string Id { get; }
    public virtual string Name => Id;
    public virtual void OnStart(ModuleContext context) { }
    public virtual void OnSceneChanged(SceneInfo scene) { }
    public virtual void OnGameplaySignal(GameplaySignal signal) { }
    /// <summary>Observed core method entries; not verified outcomes or authority.</summary>
    public virtual void OnCoreSignal(GameplaySignal signal) { }
    public virtual void OnUpdate() { }
    public virtual void OnStop() { }
}

public sealed class ModuleContext
{
    public ConfigFile Config { get; }
    public ManualLogSource Log { get; }
    public Harmony Patches { get; }
    public string GameVersion { get; }
    public string GameAssemblySha256 { get; }
    public string DataDirectory { get; }
    internal ModuleContext(ConfigFile config, ManualLogSource log, string id, string version, string hash, string data)
    {
        Config = config; Log = log; Patches = new Harmony(id);
        GameVersion = version; GameAssemblySha256 = hash; DataDirectory = data;
    }
    // Exact signature only; missing or ambiguous targets fail closed.
    public MethodInfo ResolveMethod(string assemblyName, string fullTypeName, string name, params Type[] parameters)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == assemblyName).ToArray();
        if (matches.Length == 0)
        {
            try { matches = new[] { Assembly.Load(new AssemblyName(assemblyName)) }; }
            catch (Exception ex) { Log.LogWarning($"Cannot load {assemblyName}: {ex.Message}"); return null; }
        }
        if (matches.Length != 1) { Log.LogWarning($"Ambiguous assembly {assemblyName}"); return null; }
        try
        {
            var type = matches[0].GetType(fullTypeName, false);
            var method = type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly,
                null, CallingConventions.Any, parameters, null);
            if (method == null) Log.LogWarning($"Missing target: {fullTypeName}.{name}");
            return method;
        }
        catch (Exception ex) { Log.LogWarning($"Target resolution failed: {ex.Message}"); return null; }
    }
    public bool IsVerifiedBuild(params string[] sha256Allowlist) =>
        GameAssemblySha256.Length == 64 && sha256Allowlist.Any(s =>
            string.Equals(s, GameAssemblySha256, StringComparison.OrdinalIgnoreCase));
}

public static class Framework
{
    public const string Guid = "org.battlemarked.modframework";
    public const string Version = "0.4.6";
    public static bool Register(BattlemarkedModule module) => Plugin.Instance?.Enqueue(module) ?? false;
}
