using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Read-only catalog from the active native main-menu state. No HUD, board,
// native play request or character-save mutation is needed to set preferences.
internal static class StartingFormMenu
{
    private const int MaxHeroes = 256, MaxDefinitions = 4096;
    private static ModuleContext ctx;
    private static int mainThread;
    private static Il2CppObjectBase menuState;
    private static Type menuModelType, storageType, sheetType, sheetIdType, registryType, definitionType, formIdType, wrapperType;
    private static ConstructorInfo formIdCtor;
    private static MethodInfo allSheets, ownsSheet, getDefinition;
    private static readonly List<HeroStartingForms.Choice> heroes = new();
    private static readonly List<Monster> monsters = new();
    private static DateTime nextRefresh;
    private static bool ready;
    internal static string Status { get; private set; } = "Waiting for the main menu.";
    internal static bool IsOpen => CaptureAllowed && Plugin.Instance?.HeroFormsEnabled == true && menuState != null && menuState.Pointer != IntPtr.Zero;
    internal static bool NeedsRefresh => IsOpen && !ready && DateTime.UtcNow >= nextRefresh;
    internal static IReadOnlyList<HeroStartingForms.Choice> Heroes => heroes;
    internal static IReadOnlyList<Monster> Monsters => monsters;
    private static bool CaptureAllowed => ctx != null && Plugin.Instance?.VerifiedGame == true && Environment.CurrentManagedThreadId == mainThread;

    internal sealed class Monster
    {
        internal string FormId, FormGuid, Name;
        internal string Key => FormId + " | " + FormGuid;
    }
    private sealed class Menu
    {
        internal Il2CppObjectBase Storage, Registry;
        internal string Account;
    }

    private static Type Load(string assembly, string name) => Assembly.Load(new AssemblyName(assembly)).GetType(name, true);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    private static MethodInfo Exact(Type t, string name, Type result, params Type[] args)
    {
        var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, args, null);
        if (m == null || m.IsAbstract || m.ContainsGenericParameters || (result != null && m.ReturnType != result))
            throw new InvalidOperationException("Main menu binding unavailable: " + t.Name + "." + name);
        return m;
    }
    private static HarmonyMethod Hook(string name) =>
        new(typeof(StartingFormMenu).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            var stateType = Load("Jade.Application", "Jade.Application.AppStateMainMenu");
            menuModelType = Load("GameView", "Jade.GameView.UI.Menu.MainMenuModel");
            storageType = Load("Jade.Storage", "Jade.Storage.CharacterSheetsStorage");
            sheetType = Load("Jade.GameModel", "Jade.GameModel.CharacterSheet");
            sheetIdType = sheetType.GetProperty("characterSheetId")?.PropertyType;
            registryType = Load("GameDefinitions", "Jade.GameDefinitions.PieceRegistry");
            definitionType = Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition");
            wrapperType = Load("GameDefinitions", "Jade.GameDefinitions.PieceWithDefinition");
            formIdType = Load("Jade.GameModel", "Jade.GameModel.PieceTypeId");
            formIdCtor = formIdType.GetConstructor(new[] { typeof(string), typeof(string) });
            if (sheetIdType == null || formIdCtor == null) throw new InvalidOperationException("Native starting-form identities unavailable.");
            Exact(stateType, "get_MainMenuModel", menuModelType);
            Exact(menuModelType, "get_CharacterSheetsStorage", Load("Jade.Storage", "Jade.Storage.ICharacterSheetsStorage"));
            Exact(menuModelType, "get_PieceRegistry", registryType);
            allSheets = Exact(storageType, "GetAllCharacterSheets", null);
            if (!allSheets.ReturnType.IsGenericType || allSheets.ReturnType.GetGenericArguments().Length != 1 ||
                allSheets.ReturnType.GetGenericArguments()[0] != sheetType)
                throw new InvalidOperationException("Unexpected local character list.");
            ownsSheet = Exact(storageType, "DoesCharacterSheetBelongingToLocalPlayer", typeof(bool), sheetIdType);
            getDefinition = Exact(registryType, "GetPieceDefinition", definitionType, formIdType);
            var enter = Exact(stateType, "Enter", typeof(Il2CppSystem.Collections.IEnumerator));
            var exit = Exact(stateType, "Exit", typeof(Il2CppSystem.Collections.IEnumerator));
            // Install exit first; do not leave an uncancelled enter hook if binding fails.
            context.Patches.Patch(exit, prefix: Hook(nameof(ReleaseMenu)));
            context.Patches.Patch(enter, prefix: Hook(nameof(CaptureMenu)));
            ctx = context;
            context.Log.LogInfo("BMF starting forms: native main menu catalog attached.");
        }
        catch (Exception ex)
        {
            Stop();
            Status = "Main-menu catalog unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF starting forms: " + Status);
        }
    }
    internal static void Stop()
    {
        ctx = null;
        menuState = null;
        ClearCatalog();
    }
    private static void ClearCatalog()
    {
        heroes.Clear(); monsters.Clear(); ready = false;
        nextRefresh = DateTime.MinValue;
        Status = "Waiting for the main menu.";
    }
    private static void CaptureMenu(object __instance)
    {
        if (!CaptureAllowed || __instance is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero) return;
        menuState = native;
        ClearCatalog();
    }
    private static void ReleaseMenu(object __instance)
    {
        if (!CaptureAllowed || __instance is not Il2CppObjectBase native || menuState == null || native.Pointer != menuState.Pointer) return;
        menuState = null;
        ClearCatalog();
    }

    private static Il2CppObjectBase Cast(object value, Type type)
    {
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero) return null;
        if (type.IsInstanceOfType(native)) return native;
        var cast = LevelEditor.FindCast(type).Invoke(native, null) as Il2CppObjectBase;
        return cast != null && cast.Pointer == native.Pointer && type.IsInstanceOfType(cast) ? cast : null;
    }

    private static string Account(object model)
    {
        try
        {
            var source = Call(model, "get_UserSource");
            var user = Call(source, "get_CurrentUser");
            return Call(user, "get_AccountId") as string;
        }
        catch { return null; } // Non-GUID sheets require a verified account.
    }
    private static Menu ReadMenu()
    {
        if (!IsOpen) throw new InvalidOperationException("Main menu is no longer active.");
        var model = Cast(Call(menuState, "get_MainMenuModel"), menuModelType);
        if (model == null) throw new InvalidOperationException("Waiting for the main menu to finish loading.");
        var storage = Cast(Call(model, "get_CharacterSheetsStorage"), storageType);
        var registry = Cast(Call(model, "get_PieceRegistry"), registryType);
        if (storage == null || registry == null) throw new InvalidOperationException("Waiting for local hero and piece registries.");
        return new Menu { Storage = storage, Registry = registry, Account = Account(model) };
    }

    private static List<HeroStartingForms.Choice> ReadHeroes(Menu menu)
    {
        var result = new List<HeroStartingForms.Choice>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var sheets = allSheets.Invoke(menu.Storage, null);
        if (Call(sheets, "get_Count") is not int count || count < 0 || count > MaxHeroes)
            throw new InvalidOperationException("Local character list exceeds the supported limit.");
        var item = Exact(sheets.GetType(), "get_Item", sheetType, typeof(int));
        for (int i = 0; i < count; i++)
        {
            try
            {
                var sheet = item.Invoke(sheets, new object[] { i });
                if (sheet == null || Get(sheet, "isRetired") is not bool retired || retired) continue;
                var id = Get(sheet, "characterSheetId");
                if (id == null || !sheetIdType.IsInstanceOfType(id) ||
                    ownsSheet.Invoke(menu.Storage, new[] { id }) is not bool owned || !owned) continue;
                var original = Get(sheet, "pieceTypeId");
                string key = StartingFormStore.CharacterKey(Call(id, "get_Id") as string, HeroFormPersistence.Key(original), menu.Account);
                if (key == null) continue;
                if (!found.Add(key)) { duplicates.Add(key); continue; }
                string name = Call(sheet, "get_name") as string;
                if (string.IsNullOrWhiteSpace(name)) name = Call(original, "get_Id") as string ?? "Hero";
                if (name.Length > 200) name = name.Substring(0, 200);
                result.Add(new HeroStartingForms.Choice { Key = key, Name = name + " [" + key.Substring(0, 6) + "]", Present = true });
            }
            catch { /* An unreadable sheet is never assigned. */ }
        }
        for (int i = result.Count - 1; i >= 0; i--)
            if (duplicates.Contains(result[i].Key)) result.RemoveAt(i);
        return result;
    }

    private static UObject MenuConfig()
    {
        try { return LevelEditor.ResolveConfig(null); }
        catch { return null; } // The menu may not load gameplay tags until a board loads.
    }

    private static void AddCandidate(Menu menu, UObject config, object candidate, HashSet<string> seen)
    {
        try
        {
            if (candidate != null && wrapperType.IsInstanceOfType(candidate)) candidate = Call(candidate, "get_PieceDefinition");
            var definition = candidate as UObject ?? Cast(candidate, definitionType) as UObject;
            if (definition == null || !definitionType.IsInstanceOfType(definition) ||
                !LevelEditor.ValidateMenuStartingForm(menu.Registry, config, definition, out var id, out var key, out var size) ||
                !seen.Add(key)) return;
            monsters.Add(new Monster { FormId = (string)Call(id, "get_Id"), FormGuid = (string)Call(id, "get_Guid"),
                Name = (definition.name ?? key) + " [" + size + "]" });
        }
        catch { /* Skip unreadable, unregistered or ineligible native assets. */ }
    }

    private static void ReadMonsters(Menu menu, UObject config)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool readRegistry = false;
        // PieceRegistry owns the loaded pieceDefinitions collection. Support
        // its checked native list/array wrappers before using the loaded-asset
        // fallback. Every candidate is looked up again in this exact registry.
        try
        {
            var definitions = Get(menu.Registry, "pieceDefinitions");
            var countProperty = definitions.GetType().GetProperty("Count") ?? definitions.GetType().GetProperty("Length");
            int count = Convert.ToInt32(countProperty?.GetValue(definitions) ?? -1);
            if (count >= 0 && count <= MaxDefinitions)
            {
                var item = definitions.GetType().GetMethod("get_Item", new[] { typeof(int) });
                if (item == null) item = definitions.GetType().GetMethod("get_Item", new[] { typeof(long) });
                if (item != null && (definitionType.IsAssignableFrom(item.ReturnType) || wrapperType.IsAssignableFrom(item.ReturnType)))
                {
                    bool longIndex = item.GetParameters()[0].ParameterType == typeof(long);
                    for (int i = 0; i < count; i++)
                        AddCandidate(menu, config, item.Invoke(definitions, new object[] { longIndex ? (object)(long)i : i }), seen);
                    readRegistry = count > 0 && monsters.Count > 0;
                }
            }
        }
        catch { /* The verified registry lookup below still guards fallback assets. */ }

        if (readRegistry) { monsters.Sort(CompareMonsters); return; }
        var cast = LevelEditor.FindCast(definitionType);
        var assets = Resources.FindObjectsOfTypeAll(Il2CppType.From(definitionType));
        int inspected = 0;
        if (assets != null)
            foreach (var asset in assets)
            {
                if (++inspected > MaxDefinitions || monsters.Count >= MaxDefinitions) break;
                if (asset == null) continue;
                try { AddCandidate(menu, config, cast.Invoke(asset, null), seen); }
                catch { }
            }
        monsters.Sort(CompareMonsters);
    }
    private static int CompareMonsters(Monster a, Monster b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    internal static Monster FindMonster(string key)
    {
        if (key == null) return null;
        foreach (var monster in monsters) if (monster.Key == key) return monster;
        return null;
    }

    internal static void Refresh()
    {
        if (!IsOpen) return;
        heroes.Clear(); monsters.Clear(); ready = false;
        nextRefresh = DateTime.UtcNow.AddSeconds(2);
        try
        {
            var menu = ReadMenu();
            heroes.AddRange(ReadHeroes(menu));
            var config = MenuConfig();
            ReadMonsters(menu, config);
            ready = heroes.Count != 0 && monsters.Count != 0;
            Status = heroes.Count + " local heroes | " + monsters.Count + " registered monsters" +
                (config == null ? " (full validation at launch)." : ".");
            ctx.Log.LogInfo("BMF starting forms main menu: " + Status);
        }
        catch (Exception ex) { Status = "Main menu: " + ex.GetBaseException().Message; }
    }

    // Resolve from current storage and the current exact registry on Save,
    // rather than trusting stale GUI data or native references.
    internal static bool TryReadAssignment(string heroKey, string monsterKey, out StartingFormAssignment assignment)
    {
        assignment = null;
        try
        {
            var monster = FindMonster(monsterKey);
            if (monster == null || !IsOpen) return false;
            var menu = ReadMenu();
            HeroStartingForms.Choice hero = null;
            foreach (var candidate in ReadHeroes(menu))
                if (candidate.Key == heroKey) { hero = candidate; break; }
            if (hero == null) return false;
            var id = formIdCtor.Invoke(new object[] { monster.FormId, monster.FormGuid });
            var definition = getDefinition.Invoke(menu.Registry, new[] { id }) as UObject;
            if (!LevelEditor.ValidateMenuStartingForm(menu.Registry, MenuConfig(), definition, out _, out var key, out _) || key != monster.Key)
                return false;
            assignment = new StartingFormAssignment { CharacterKey = hero.Key, HeroName = hero.Name,
                FormId = monster.FormId, FormGuid = monster.FormGuid, FormName = monster.Name };
            return true;
        }
        catch (Exception ex) { Status = "Main menu selection: " + ex.GetBaseException().Message; return false; }
    }
}
