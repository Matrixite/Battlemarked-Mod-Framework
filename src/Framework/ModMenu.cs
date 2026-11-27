using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace Battlemarked.ModFramework;

internal sealed class MenuModule
{
    internal readonly string Id, Name;
    internal readonly ConfigEntry<bool> Enabled;
    internal MenuModule(string id, string name, ConfigEntry<bool> enabled) { Id = id; Name = name; Enabled = enabled; }
}

// Desktop IMGUI: live rule preferences, explicit health queue, next-launch module changes.
internal sealed class ModMenu
{
    private readonly Plugin plugin;
    private readonly NativeDebugMenu nativeDebugMenu;
    private readonly ManualLogSource log;
    private readonly ConfigEntry<bool> enabled, developerMode;
    private readonly ConfigFile config;
    private readonly ConfigEntry<int> savedX, savedY;
    private readonly MenuPosition position;
    private bool open, broken, guiSeen, usingPolling;
    private int page;
    private int tab;
    private static readonly string[] Tabs = { "Overview", "AP rules", "Health", "Presets", "Mods", "Reports", "Updates", "Dev Tools" };
    private string message = "Settings apply on next launch.";
    private Func<bool> poll;
    private bool inputChecked;
    private bool warningStyleChecked;
    private GUIStyle warningStyle;
    private static readonly string[] UpdateWarningLines =
    {
        "AFTER THE UPDATE IS QUEUED, QUIT.",
        "WAIT UNTIL STEAM SHOWS PLAY.",
        "NOT STOP OR STOPPING."
    };
    internal ModMenu(Plugin plugin, ConfigFile config, ManualLogSource log)
    {
        this.plugin = plugin; this.log = log; this.config = config;
        nativeDebugMenu = new NativeDebugMenu(plugin, log);
        enabled = config.Bind("Menu", "Enabled", true, "Enable experimental desktop mod manager.");
        developerMode = config.Bind("Menu", "DeveloperMode", false, "Show built-in diagnostic and example modules, Reports and Dev Tools. Disabling also closes a native debug menu opened through BMF; module settings are unchanged.");
        open = config.Bind("Menu", "StartOpen", false, "Open menu automatically; fallback if F10 is unavailable.").Value;
        savedX = config.Bind("Menu", "PositionX", -1, "Last menu left position in pixels; -1 centers horizontally.");
        savedY = config.Bind("Menu", "PositionY", -1, "Last menu top position in pixels; -1 centers vertically.");
        position = new MenuPosition(savedX.Value, savedY.Value);
    }
    private void ResolveInput()
    {
        // Prefer the new Input System when available. Use reflection so it is optional.
        try
        {
            var asm = Assembly.Load(new AssemblyName("Unity.InputSystem"));
            var keyboard = asm.GetType("UnityEngine.InputSystem.Keyboard", true);
            var current = keyboard.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            var f10 = keyboard.GetProperty("f10Key");
            var pressed = f10?.PropertyType.GetProperty("wasPressedThisFrame");
            if (current != null && f10 != null && pressed != null)
            {
                poll = () => { var k = current.GetValue(null); if (k == null) return false; var key = f10.GetValue(k); return key != null && pressed.GetValue(key) is bool b && b; };
                return;
            }
        }
        catch { }
        try
        {
            var asm = Assembly.Load(new AssemblyName("UnityEngine.InputLegacyModule"));
            var input = asm.GetType("UnityEngine.Input", true);
            var method = input.GetMethod("GetKeyDown", new[] { typeof(KeyCode) });
            if (method != null) poll = () => method.Invoke(null, new object[] { KeyCode.F10 }) is bool b && b;
        }
        catch { }
    }
    private void FinishDrag()
    {
        if (!position.End()) return;
        try
        {
            // Save once when the mouse is released, never on every drag event.
            savedX.Value = position.SavedX;
            savedY.Value = position.SavedY;
            config.Save();
        }
        catch (Exception ex) { log.LogWarning("BMF menu position could not be saved: " + ex.Message); }
    }
    private void SetOpen(bool value)
    {
        if (!value) FinishDrag();
        open = value;
    }
    private void HandleDrag(Event e, float x, float y, float width, float height)
    {
        if (e == null) return;
        // Use the title strip only; the Close button, tabs and rule controls
        // keep their existing events. Mouse release outside the title is valid.
        if (e.type == EventType.MouseDown)
        {
            FinishDrag(); // recover if a mouse release occurred outside the game
            var mouse = e.mousePosition;
            if (e.button == 0 && mouse.x >= x && mouse.x < x + width &&
                mouse.y >= y && mouse.y < y + 24f)
            {
                position.Begin(mouse.x, mouse.y, x, y);
                e.Use();
            }
        }
        else if (position.Dragging && e.type == EventType.MouseDrag && e.button == 0)
        {
            var mouse = e.mousePosition;
            position.Move(mouse.x, mouse.y, Screen.width, Screen.height, width, height);
            e.Use();
        }
        else if (position.Dragging && e.type == EventType.MouseUp && e.button == 0)
        {
            var mouse = e.mousePosition;
            position.Move(mouse.x, mouse.y, Screen.width, Screen.height, width, height);
            FinishDrag();
            e.Use();
        }
    }
    internal void Tick()
    {
        if (!developerMode.Value) nativeDebugMenu.CloseOwned();
        if (!enabled.Value || broken) return;
        if (!inputChecked) { inputChecked = true; ResolveInput(); }
        if (poll == null) return;
        try { usingPolling = true; if (poll()) SetOpen(!open); }
        catch (Exception ex) { poll = null; usingPolling = false; log.LogWarning("BMF hotkey polling unavailable; using GUI keyboard events: " + ex.Message); }
    }
    private void DrawUpdateWarning(float x, float y, float width)
    {
        if (!warningStyleChecked)
        {
            warningStyleChecked = true;
            try
            {
                // Clone the skin style; do not alter labels elsewhere in the manager.
                warningStyle = new GUIStyle(GUI.skin.label);
                warningStyle.fontSize = 18;
                warningStyle.richText = true;
                warningStyle.normal.textColor = new Color(1f, 0.85f, 0.25f, 1f);
            }
            catch (Exception ex)
            {
                warningStyle = null;
                log.LogWarning("BMF update warning style unavailable: " + ex.Message);
            }
        }
        for (int i = 0; i < UpdateWarningLines.Length; i++)
        {
            var rect = new Rect(x + 15, y + 317 + 22 * i, width - 30, 22);
            if (warningStyle != null) GUI.Label(rect, "<b>" + UpdateWarningLines[i] + "</b>", warningStyle);
            else GUI.Label(rect, UpdateWarningLines[i]);
        }
    }

    internal void Draw()
    {
        if (!enabled.Value || broken) return;
        try
        {
            if (!guiSeen) { guiSeen = true; log.LogInfo("BMF desktop menu OnGUI callback observed."); }
            if (!usingPolling)
            {
                var e = Event.current;
                if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F10) { SetOpen(!open); e.Use(); }
            }
            if (!open) return;
            float width = Math.Min(800f, Screen.width - 24f), height = Math.Min(530f, Screen.height - 24f);
            if (width < 460f || height < 410f) { FinishDrag(); return; }
            float x = position.X(Screen.width, width), y = position.Y(Screen.height, height);
            HandleDrag(Event.current, x, y, width, height);
            x = position.X(Screen.width, width);
            y = position.Y(Screen.height, height);
            GUI.Box(new Rect(x, y, width, height), "Battlemarked Mod Manager | BMF " + Framework.Version + " | Drag to move");
            if (GUI.Button(new Rect(x + width - 80, y + 24, 68, 26), "Close")) { SetOpen(false); return; }
            GUI.Label(new Rect(x + 12, y + 25, width - 100, 25), plugin.CompatibilityStatus + " | F10");
            // Keep the original tab IDs when optional tabs are hidden.
            bool houseRulesEnabled = plugin.IsEnabledForNextLaunch(HouseRules.ModuleId);
            var visibleTabs = new List<int>();
            for (int i = 0; i < Tabs.Length; i++)
                if (MenuModuleVisibility.IsTabVisible(Tabs[i], developerMode.Value, houseRulesEnabled)) visibleTabs.Add(i);
            if (!visibleTabs.Contains(tab)) tab = 4; // Mods, where developer mode can be restored.
            float tabWidth = (width - 24) / visibleTabs.Count;
            for (int column = 0; column < visibleTabs.Count; column++)
            {
                int i = visibleTabs[column];
                if (GUI.Button(new Rect(x + 12 + column * tabWidth, y + 58, tabWidth - 3, 28),
                    (tab == i ? "[" + Tabs[i] + "]" : Tabs[i]))) tab = i;
            }

            void Label(float top, string value) => GUI.Label(new Rect(x + 15, y + top, width - 30, 25), value);
            bool Button(float top, string value) => GUI.Button(new Rect(x + 15, y + top, width - 30, 28), value);
            bool Small(float top, float left, float size, string value) => GUI.Button(new Rect(x + left, y + top, size, 28), value);

            if (tab == 0)
            {
                Label(97, "Game version: " + plugin.GameVersion);
                Label(128, "Selected profile: " + HouseRules.PresetName);
                Label(159, "AP: +" + HouseRules.ActiveBonus + " | " + (HouseRules.ActiveRefundMode ? "refund mode" : "cap mode") + (HouseRules.ModePending ? " | mode change pending" : ""));
                Label(190, "Health preference: " + HouseRules.NextHealthPercent + "%");
                Label(221, HouseRules.HealthStatus);
                Label(252, HouseRules.HealthCounts);
                Label(283, "Rules: disposable local solo only. Back up saves separately.");
                Label(314, "Health: explicitly queue before each NEW campaign.");
                Label(345, "AP bonus edits apply live; module switches require restart.");
                Label(375, UpdatePolicy.ForDisplay(plugin.Updater?.Status ?? "Automatic updater unavailable", "Update status details: see development Reports."));
                return;
            }
            if (tab == 1)
            {
                Label(97, "Extra action points | Selected profile: " + HouseRules.PresetName);
                Label(128, "Active: +" + HouseRules.ActiveBonus + " AP");
                if (HouseRules.CanConfigure)
                {
                    if (Small(157, 15, 80, "-1 AP")) HouseRules.ChangeBonus(-1);
                    if (Small(157, 103, 80, "+1 AP")) HouseRules.ChangeBonus(1);
                    if (Small(157, 191, 100, "Disable")) HouseRules.ChangeBonus(-3);
                }
                Label(190, HouseRules.Status);
                Label(216, HouseRules.Counts);
                Label(242, "Target: " + (2 + HouseRules.ActiveBonus) + (HouseRules.ActiveRefundMode ? " one-AP actions; cap unchanged." : " AP (normal hero)."));
                if (HouseRules.CanConfigure && Button(270, "Include existing AP caps: " + (HouseRules.ActiveAdjustExisting ? "ON" : "OFF"))) HouseRules.ToggleAdjustExisting();
                if (HouseRules.CanConfigure && Button(302, "AP refund mode: " + (HouseRules.ActiveRefundMode ? "ON" : "OFF") + (HouseRules.ModePending ? " (pending)" : ""))) HouseRules.ToggleRefundMode();
                Label(334, HouseRules.ModePending ? "Mode pending: return to main menu before next game." : "Bonus live; mode applies between adventures.");
                Label(365, "Existing cap mode can change saves. Prefer refunds.");
                return;
            }
            if (tab == 2)
            {
                Label(97, "Native hero maximum health | fresh solo campaigns");
                Label(128, "Next trial: " + HouseRules.NextHealthPercent + "% | 10 base HP -> " + HealthBudget.Target(10, HouseRules.NextHealthPercent));
                if (HouseRules.CanConfigureHealth)
                {
                    if (Small(157, 15, 80, "-25%")) HouseRules.ChangeHealthPercent(-25);
                    if (Small(157, 103, 80, "+25%")) HouseRules.ChangeHealthPercent(25);
                    if (Small(157, 191, 100, "Normal")) HouseRules.ChangeHealthPercent(-100);
                }
                Label(190, HouseRules.HealthStatus);
                Label(220, HouseRules.HealthCounts);
                if (HouseRules.CanConfigureHealth && Button(250, HouseRules.NativeHealthRequested ? "Cancel / disarm health trial" : "Queue health for next NEW solo game")) HouseRules.ToggleNativeHealthArm();
                Label(285, "Leave the current game, queue, then start a new game.");
                Label(315, "Repeat with another fresh game; no restart required.");
                Label(345, "Existing or previously modified saves are not safe trials.");
                Label(375, "Report: hero-health-probe.txt");
                return;
            }
            if (tab == 3)
            {
                Label(97, "Selected preferences: " + HouseRules.PresetName);
                for (int i = 0; i < HouseRulePreset.All.Count; i++)
                {
                    var preset = HouseRulePreset.All[i];
                    string description = $"{preset.Name}  |  +{preset.Bonus} AP  |  {preset.HealthPercent}% HP";
                    if (Button(128 + 34 * i, description))
                        message = HouseRules.ApplyPreset(preset) ? "Preferences selected. Queue health separately." : "Preset unavailable: check compatibility and modules.";
                }
                Label(302, "Presets prefer refund mode (changes at idle scope).");
                Label(330, "Health is never armed by selecting a preset.");
                Label(357, "Vanilla stops new bonuses; does not undo past writes.");
                Label(384, message);
                return;
            }
            if (tab == 5)
            {
                Label(97, "Compatibility: " + plugin.CompatibilityStatus);
                Label(129, "AP: " + HouseRules.Counts);
                Label(159, "Health: " + HouseRules.HealthCounts);
                Label(189, "Save safety: no native health writes to loaded games.");
                Label(219, "Unknown builds do not enable house-rule writes.");
                Label(249, "No automatic save repair; backup before trials.");
                if (Button(275, "Refresh local compatibility report"))
                    message = plugin.RefreshCompatibilityReport() ? "Report refreshed." : "Report could not be written; check log.";
                if (Button(309, "Export support ZIP (local reports only)"))
                {
                    try { message = "Saved: " + plugin.ExportSupportBundle(); }
                    catch (Exception ex)
                    {
                        message = "Support export failed; check BepInEx log.";
                        log.LogWarning("BMF support export: " + ex.Message);
                    }
                }
                Label(342, "BepInEx/config/BattlemarkedModFramework/support/");
                Label(374, message);
                return;
            }

            if (tab == 6)
            {
                var updater = plugin.Updater;
                Label(97, "Framework updates");
                Label(122, "Installed: " + Framework.Version + " | Available: " + (updater?.AvailableVersion ?? "Unavailable"));
                if (updater == null) { Label(154, "Updater unavailable; see BepInEx log."); return; }
                Label(147, UpdatePolicy.ForDisplay(updater.Status, "Update status details: see development Reports."));
                if (!updater.Busy && !updater.Scheduled && Button(173, "Check for updates now")) updater.CheckNow();
                if (updater.CanInstall && Button(204, "Download and install when game closes")) updater.Install();
                if (updater.Scheduled && Button(204, "Cancel scheduled update")) updater.CancelScheduled();
                if (Button(235, "Automatic update checks: " + (updater.AutoCheck ? "ON" : "OFF"))) updater.ToggleAutoCheck();
                if (Button(266, "Automatic installation: " + (updater.AutoInstall ? "ON" : "OFF (opt-in)"))) updater.ToggleAutoInstall();
                GUI.Label(new Rect(x + 15, y + 295, width - 30, 20), "Source updates build locally; no DLL replacement while playing.");
                DrawUpdateWarning(x, y, width);
                Label(385, UpdatePolicy.ForDisplay(updater.LastResult, "Previous update details: see development Reports."));
                return;
            }

            if (tab == 7)
            {
                Label(97, "Dev Tools | experimental native game debug menu");
                Label(128, "Use only in a disposable local solo game. Back up saves first.");
                Label(159, "The native menu can contain commands that change game state.");
                if (!plugin.VerifiedGame) { Label(190, "Unavailable: unverified game build."); return; }
                if (developerMode.Value && Button(193, "Show game debug menu"))
                {
                    if (nativeDebugMenu.SetVisible(true)) SetOpen(false);
                }
                if (developerMode.Value && Button(227, "Hide game debug menu"))
                    nativeDebugMenu.SetVisible(false);
                Label(267, "Press F10 to return to the mod manager.");
                Label(298, "Opening is session-only; no debug setting is saved.");
                Label(329, "Status:");
                Label(355, nativeDebugMenu.Status);
                return;
            }

            var modules = new List<MenuModule>();
            foreach (var m in plugin.MenuModules)
                if (MenuModuleVisibility.IsVisible(m.Id, developerMode.Value)) modules.Add(m);
            Label(95, (developerMode.Value ? "Developer view" : "Installed modules") + " | module switches apply next launch");
            if (modules.Count == 0) Label(125, "No visible modules installed.");
            int rows = Math.Max(1, (int)((height - 184) / 49));
            int pages = Math.Max(1, (modules.Count + rows - 1) / rows);
            page = Math.Max(0, Math.Min(page, pages - 1));
            for (int i = 0; i < rows && page * rows + i < modules.Count; i++)
            {
                var m = modules[page * rows + i]; float top = y + 125 + i * 49;
                GUI.Label(new Rect(x + 15, top, width - 120, 23), m.Name);
                GUI.Label(new Rect(x + 15, top + 22, width - 120, 23), (plugin.IsRunning(m.Id) ? "Running" : "Not running") + " | Next launch: " + (m.Enabled.Value ? "enabled" : "disabled"));
                if (GUI.Button(new Rect(x + width - 105, top + 7, 93, 27), m.Enabled.Value ? "Disable" : "Enable"))
                {
                    try { m.Enabled.Value = !m.Enabled.Value; message = "Saved; restart Battlemarked to apply module switches."; }
                    catch (Exception ex) { message = "Could not save settings; see BepInEx log."; log.LogWarning(ex.Message); }
                }
            }
            if (Small(height - 59, 15, 85, "Previous")) page = Math.Max(0, page - 1);
            GUI.Label(new Rect(x + 108, y + height - 59, 100, 24), $"{page + 1}/{pages}");
            if (Small(height - 59, 170, 85, "Next")) page = Math.Min(pages - 1, page + 1);
            if (Small(height - 59, width - 185, 170, "Developer mode: " + (developerMode.Value ? "ON" : "OFF")))
            {
                try
                {
                    developerMode.Value = !developerMode.Value;
                    if (!developerMode.Value) nativeDebugMenu.CloseOwned();
                    page = 0;
                    config.Save();
                    message = developerMode.Value ? "Development modules, Reports and Dev Tools visible." : "Development modules, Reports and Dev Tools hidden.";
                }
                catch (Exception ex)
                {
                    message = "Could not save developer mode; see BepInEx log.";
                    log.LogWarning(ex.Message);
                }
            }
            Label(height - 30, message);
        }
        catch (Exception ex) { broken = true; log.LogWarning("BMF desktop menu disabled for this session: " + ex); }
    }
}
