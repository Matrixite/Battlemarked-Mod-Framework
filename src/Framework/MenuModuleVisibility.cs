namespace Battlemarked.ModFramework;

// Presentation only: never use this policy to start, stop or report modules.
internal static class MenuModuleVisibility
{
    internal static bool IsVisible(string id, bool developerMode) =>
        developerMode || (id != "org.battlemarked.diagnostics.corelogic" &&
                          id != "org.battlemarked.diagnostics.gameplay" &&
                          id != "org.battlemarked.example.scenelogger");
}
