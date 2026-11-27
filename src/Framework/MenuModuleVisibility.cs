namespace Battlemarked.ModFramework;

// Presentation only: never use this policy to start, stop or report modules.
internal static class MenuModuleVisibility
{
    internal static bool IsTabVisible(string title, bool developerMode, bool houseRulesEnabled, bool heroFormsEnabled = true)
    {
        if (title == "Reports" || title == "Dev Tools") return developerMode;
        if (title == "Level Editor") return developerMode;
        if (title == "Hero Forms") return developerMode && heroFormsEnabled;
        if (title == "AP rules" || title == "Health" || title == "Presets") return houseRulesEnabled;
        return true;
    }

    internal static bool IsVisible(string id, bool developerMode) =>
        developerMode || (id != "org.battlemarked.diagnostics.corelogic" &&
                          id != "org.battlemarked.diagnostics.gameplay" &&
                          id != "org.battlemarked.example.scenelogger");
}
