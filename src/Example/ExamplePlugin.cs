using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Battlemarked.ModFramework;

namespace Battlemarked.Example;

[BepInPlugin("org.battlemarked.example", "BMF Example: Scene Logger", "0.6.15")]
[BepInDependency(Framework.Guid, Framework.Version)]
public sealed class ExamplePlugin : BasePlugin
{
    public override void Load()
    {
        if (!Framework.Register(new SceneLogger())) Log.LogWarning("BMF refused module registration.");
    }
}

public sealed class SceneLogger : BattlemarkedModule
{
    public override string Id => "org.battlemarked.example.scenelogger";
    public override string Name => "Scene logger";
    private ModuleContext context;
    private ConfigEntry<bool> logScenes;
    public override void OnStart(ModuleContext ctx)
    {
        context = ctx;
        logScenes = ctx.Config.Bind("SceneLogger", "LogScenes", true, "Log active scene changes.");
        ctx.Log.LogInfo("Example module ready. No game state is modified.");
    }
    public override void OnSceneChanged(SceneInfo scene)
    {
        if (logScenes.Value) context.Log.LogInfo($"Active scene: {scene.Name} (handle {scene.Handle}, index {scene.BuildIndex})");
    }
    public override void OnStop() => context?.Log.LogInfo("Example module stopped.");
}
