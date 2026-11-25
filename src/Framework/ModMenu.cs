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

// Desktop IMGUI. Changes are saved for the next launch, not applied mid-session.
internal sealed class ModMenu
{
    private readonly Plugin plugin;
    private readonly ManualLogSource log;
    private readonly ConfigEntry<bool> enabled;
    private bool open, broken, guiSeen, usingPolling;
    private int page;
    private bool diagnostics, houseRules;
    private string message = "Settings apply on next launch.";
    private Func<bool> poll;
    private bool inputChecked;
    internal ModMenu(Plugin plugin, ConfigFile config, ManualLogSource log)
    {
        this.plugin = plugin; this.log = log;
        enabled = config.Bind("Menu", "Enabled", true, "Enable experimental desktop mod manager.");
        open = config.Bind("Menu", "StartOpen", false, "Open menu automatically; fallback if F10 is unavailable.").Value;
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
    internal void Tick()
    {
        if (!enabled.Value || broken) return;
        if (!inputChecked) { inputChecked = true; ResolveInput(); }
        if (poll == null) return;
        try { usingPolling = true; if (poll()) open = !open; }
        catch (Exception ex) { poll = null; usingPolling = false; log.LogWarning("BMF hotkey polling unavailable; using GUI keyboard events: " + ex.Message); }
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
                if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F10) { open = !open; e.Use(); }
            }
            if (!open) return;
            float width = Math.Min(650f, Screen.width - 24f), height = Math.Min(430f, Screen.height - 24f);
            if (width < 300f || height < 250f) return;
            float x = (Screen.width - width) / 2f, y = (Screen.height - height) / 2f;
            GUI.Box(new Rect(x, y, width, height), "Battlemarked Mod Manager | BMF " + Framework.Version);
            if (GUI.Button(new Rect(x + width - 80, y + 24, 68, 25), "Close")) { open = false; return; }
            GUI.Label(new Rect(x + 12, y + 27, width - 105, 25), "Game: " + plugin.GameVersion + " | F10");
            if (GUI.Button(new Rect(x + 12, y + 58, 130, 26), "Installed mods")) { diagnostics = false; houseRules = false; }
            if (GUI.Button(new Rect(x + 150, y + 58, 130, 26), "Diagnostics")) { diagnostics = true; houseRules = false; }
            if (GUI.Button(new Rect(x + 288, y + 58, 130, 26), "House rules")) { diagnostics = false; houseRules = true; }
            if (houseRules)
            {
                GUI.Label(new Rect(x + 12, y + 92, width - 24, 24), "Extra hero action points (experimental, solo only)");
                GUI.Label(new Rect(x + 12, y + 124, width - 24, 24), "Next launch: +" + HouseRules.NextBonus + " AP" + (HouseRules.NextBonus == 0 ? " (off)" : ""));
                if (HouseRules.CanConfigure)
                {
                    if (GUI.Button(new Rect(x + 12, y + 154, 80, 27), "-1 AP")) HouseRules.ChangeBonus(-1);
                    if (GUI.Button(new Rect(x + 100, y + 154, 80, 27), "+1 AP")) HouseRules.ChangeBonus(1);
                }
                if (GUI.Button(new Rect(x + 188, y + 154, 100, 27), "Disable")) HouseRules.ChangeBonus(-3);
                GUI.Label(new Rect(x + 12, y + 187, width - 24, 24), HouseRules.Status);
                GUI.Label(new Rect(x + 12, y + 216, width - 24, 24), HouseRules.Counts);
                GUI.Label(new Rect(x + 12, y + 245, width - 24, 25), "Restart to apply. Target: " + (2 + HouseRules.NextBonus) + (HouseRules.NextRefundMode ? " one-AP actions (cap unchanged)." : " AP (normal hero)."));
                if (height >= 335 && HouseRules.CanConfigure && GUI.Button(new Rect(x + 12, y + 274, width - 24, 27), "Adjust existing AP caps: " + (HouseRules.NextAdjustExisting ? "ON" : "OFF"))) HouseRules.ToggleAdjustExisting();
                if (height >= 335) GUI.Label(new Rect(x + 12, y + 302, width - 24, 25), HouseRules.NextRefundMode ? "Refunds count AP already above 2; cap is unchanged." : "Existing cap adjustment: disposable test saves only.");
                if (height >= 365 && HouseRules.CanConfigure && GUI.Button(new Rect(x + 12, y + 330, width - 24, 27), "AP refund mode: " + (HouseRules.NextRefundMode ? "ON" : "OFF"))) HouseRules.ToggleRefundMode();
                if (height >= 390) GUI.Label(new Rect(x + 12, y + 360, width - 24, 24), "Disposable solo only. Restart to apply.");
                return;
            }
            if (diagnostics)
            {
                GUI.Label(new Rect(x + 12, y + 90, width - 24, 25), "Core method entries (not game outcomes)");
                var summaries = CoreLogicProbe.Summary();
                for (int i = 0; i < summaries.Length && 120 + i * 24 < height - 34; i++)
                    GUI.Label(new Rect(x + 12, y + 120 + i * 24, width - 24, 24), summaries[i]);
                GUI.Label(new Rect(x + 12, y + height - 30, width - 24, 24), "See core-probe.txt for attachment status.");
                return;
            }
            var modules = plugin.MenuModules.ToArray();
            int rows = Math.Max(1, (int)((height - 155) / 49));
            int pages = Math.Max(1, (modules.Length + rows - 1) / rows);
            page = Math.Max(0, Math.Min(page, pages - 1));
            for (int i = 0; i < rows && page * rows + i < modules.Length; i++)
            {
                var m = modules[page * rows + i]; float top = y + 91 + i * 49;
                GUI.Label(new Rect(x + 12, top, width - 120, 23), m.Name == m.Id ? m.Id : m.Name);
                GUI.Label(new Rect(x + 12, top + 22, width - 120, 23), (plugin.IsRunning(m.Id) ? "Running" : "Not running") + " | Next launch: " + (m.Enabled.Value ? "enabled" : "disabled"));
                if (GUI.Button(new Rect(x + width - 105, top + 7, 93, 27), m.Enabled.Value ? "Disable" : "Enable"))
                {
                    try { m.Enabled.Value = !m.Enabled.Value; message = "Saved; restart Battlemarked to apply."; }
                    catch (Exception ex) { message = "Could not save settings; see BepInEx log."; log.LogWarning(ex.Message); }
                }
            }
            if (GUI.Button(new Rect(x + 12, y + height - 62, 70, 24), "Previous")) page = Math.Max(0, page - 1);
            GUI.Label(new Rect(x + 90, y + height - 62, 100, 24), $"{page + 1}/{pages}");
            if (GUI.Button(new Rect(x + 150, y + height - 62, 70, 24), "Next")) page = Math.Min(pages - 1, page + 1);
            GUI.Label(new Rect(x + 12, y + height - 32, width - 24, 24), message);
        }
        catch (Exception ex) { broken = true; log.LogWarning("BMF desktop menu disabled for this session: " + ex); }
    }
}
