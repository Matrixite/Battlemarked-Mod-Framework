using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Battlemarked.ModFramework;

// Use the game's own existing debug-menu component. Never create a prefab,
// patch native methods, change feature flags or execute debug commands.
internal sealed class NativeDebugMenu
{
    private readonly Plugin plugin;
    private readonly ManualLogSource log;
    private Component owned;
    private MethodInfo show, hide;
    private Type menuType;
    private MethodInfo cast;
    internal string Status { get; private set; } = "Debug menu off. Select Show to look for the native menu.";

    internal NativeDebugMenu(Plugin plugin, ManualLogSource log)
    { this.plugin = plugin; this.log = log; }

    private void Bind()
    {
        if (menuType != null) return;
        var type = Assembly.Load(new AssemblyName("Jade.DebugMenu")).GetType("Jade.DebugMenu.DebugMenu", true);
        if (!typeof(Component).IsAssignableFrom(type))
            throw new InvalidOperationException("Unexpected native debug menu type.");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        MethodInfo Find(string name)
        {
            var methods = type.GetMethods(flags).Where(m => m.Name == name &&
                !m.IsAbstract && !m.ContainsGenericParameters && m.ReturnType == typeof(void) &&
                m.GetParameters().Length == 0).ToArray();
            if (methods.Length != 1) throw new InvalidOperationException("Missing or ambiguous " + name);
            return methods[0];
        }
        var nativeShow = Find("ShowDebugMenu");
        var nativeHide = Find("HideDebugMenu");
        var nativeCast = typeof(Il2CppObjectBase).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "TryCast" && m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0)
            .MakeGenericMethod(type);
        show = nativeShow;
        hide = nativeHide;
        cast = nativeCast;
        menuType = type;
    }

    private Component FindMenu()
    {
        // Include inactive scene objects, but never operate on a prefab asset.
        var matches = new List<Component>();
        var objects = Resources.FindObjectsOfTypeAll(Il2CppType.From(menuType));
        if (objects != null)
            foreach (var obj in objects)
            {
                if (obj == null) continue;
                var component = cast.Invoke(obj, null) as Component;
                if (component == null || !menuType.IsInstanceOfType(component)) continue;
                var scene = component.gameObject.scene;
                if (scene.IsValid() && scene.isLoaded) matches.Add(component);
            }
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0 ?
                "No native debug menu in the loaded scenes." : "Multiple native debug menus; cannot safely choose one.");
        return matches[0];
    }

    internal bool SetVisible(bool visible)
    {
        // These methods and their compiled implementations were checked against
        // the same fingerprint as the framework's existing verified-build guard.
        if (!plugin.VerifiedGame)
        {
            Status = "Unavailable: unverified game build.";
            return false;
        }
        try
        {
            Bind();
            var menu = FindMenu();
            var root = menu.gameObject;
            if (visible)
            {
                if (!root.activeInHierarchy)
                {
                    // Record ownership before calling Show, in case it partly
                    // succeeds and throws. Do not claim a successful open unless
                    // Unity actually reports that the object is active.
                    owned = menu;
                    show.Invoke(menu, null);
                }
                if (!root.activeInHierarchy)
                    throw new InvalidOperationException("The native menu did not become active.");
            }
            else
            {
                if (root.activeSelf) hide.Invoke(menu, null);
                if (root.activeSelf) throw new InvalidOperationException("The native menu did not close.");
                owned = null;
            }
            Status = visible ? "Native debug menu opened. F10 returns to BMF." : "Native debug menu closed.";
            return true;
        }
        catch (Exception ex)
        {
            var error = ex.GetBaseException();
            Status = "Unavailable: " + error.Message;
            log.LogWarning("BMF native debug menu: " + error);
            if (visible && owned != null) CloseOwned(preserveStatus: true);
            return false;
        }
    }

    internal void CloseOwned(bool preserveStatus = false)
    {
        var menu = owned;
        if (menu == null) { owned = null; return; }
        try
        {
            if (menu.gameObject.activeSelf) hide.Invoke(menu, null);
            if (menu.gameObject.activeSelf) throw new InvalidOperationException("The native menu did not close.");
            if (!preserveStatus) Status = "Native debug menu closed.";
        }
        catch (Exception ex)
        {
            log.LogWarning("BMF native debug menu cleanup: " + ex.GetBaseException());
            if (!preserveStatus) Status = "Could not close the native debug menu; see BepInEx log.";
        }
        finally { owned = null; }
    }
}
