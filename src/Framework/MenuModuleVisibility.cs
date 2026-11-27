namespace Battlemarked.ModFramework;

// Presentation only: never use this policy to start, stop or report modules.
internal static class MenuModuleVisibility
{
    internal static bool IsTabVisible(string title, bool developerMode, bool houseRulesEnabled, bool heroFormsEnabled = true)
    {
        if (title == null) return false; // Reserved tab IDs are not shown.
        if (title == "Reports" || title == "Dev Tools") return developerMode;
        if (title == "Level Editor") return developerMode;
        if (title == "Hero Forms") return heroFormsEnabled;
        if (title == "House Rules" || title == "Presets") return houseRulesEnabled;
        return true;
    }

    internal static bool IsVisible(string id, bool developerMode) =>
        developerMode || (id != "org.battlemarked.diagnostics.corelogic" &&
                          id != "org.battlemarked.diagnostics.gameplay" &&
                          id != "org.battlemarked.example.scenelogger");
}
