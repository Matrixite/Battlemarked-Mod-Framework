using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// A BMF-owned IMGUI inspector. Does not depend on the retail debug facade.
// Native reads are explicit snapshots; writes are one-shot Update requests.
internal sealed class DebugWorkspace
{
    private static readonly string[] Pages = { "Scene", "Board", "Pieces", "Cards", "Encounters", "Events", "Native" };
    private const int ScanLimit = 4096;
    private readonly Plugin plugin;
    private readonly ManualLogSource log;
    private readonly List<Entry> entries = new();
    private List<Entry> hits = new();
    private bool guiErrorLogged;
    private int page, index;
    private int? sceneHandle;
    private int? scanPage;
    private string filter = "", status = "Select Refresh to inspect loaded objects.";
    private Entry selected, target, card;
    private bool confirming;
    private Grant pending;
    // Keep game-reference DTOs as ordinary classes: the user's generated
    // interop references have previously failed Roslyn record synthesis
    // with CS0656 (missing NullableAttribute constructors).
    private sealed class Entry
    {
        internal readonly UObject Object;
        internal readonly string Name, Detail, Key;
        internal Entry(UObject obj, string name, string detail, string key)
        { Object = obj; Name = name; Detail = detail; Key = key; }
    }
    private sealed class Grant
    {
        internal readonly Entry Target, Card;
        internal readonly int Scene;
        internal Grant(Entry target, Entry card, int scene)
        { Target = target; Card = card; Scene = scene; }
    }

    internal DebugWorkspace(Plugin plugin, ManualLogSource log)
    { this.plugin = plugin; this.log = log; }

    private void ClearSession()
    {
        entries.Clear();
        hits.Clear();
        filter = "";
        selected = target = card = null;
        pending = null;
        confirming = false;
        scanPage = null;
        index = 0;
        status = "Select Refresh to inspect the current scene.";
    }

    internal void Tick(bool enabled)
    {
        if (!enabled)
        {
            if (sceneHandle.HasValue) ClearSession();
            sceneHandle = null;
            return;
        }
        try
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            { ClearSession(); sceneHandle = null; return; }
            if (sceneHandle != scene.handle)
            {
                ClearSession();
                sceneHandle = scene.handle;
            }
            if (scanPage.HasValue)
            {
                int request = scanPage.Value;
                scanPage = null;
                if (request == page) Scan(request, scene.handle);
            }
            var command = pending;
            pending = null;
            if (command != null && command.Scene == scene.handle) GrantCard(command);
        }
        catch (Exception ex)
        {
            pending = null;
            scanPage = null;
            confirming = false;
            status = "Debug operation unavailable: " + ex.GetBaseException().Message;
            log.LogWarning("BMF debug workspace: " + ex.GetBaseException());
        }
    }

    private void Scan(int request, int scene)
    {
        entries.Clear();
        hits.Clear();
        selected = null;
        if (request == 1) target = null;
        if (request == 3) card = null;
        index = 0;
        var items = new List<Entry>();
        int skipped = 0;
        bool capped = false;
        Type type = request switch
        {
            0 => typeof(GameObject),
            1 => Load("GameView", "Jade.GameView.PieceView"),
            2 => Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition"),
            3 => Load("GameDefinitions", "Jade.GameDefinitions.CardDefinition"),
            4 => Load("GameDefinitions", "Jade.GameDefinitions.EncounterDefinition"),
            _ => throw new InvalidOperationException("Not a browser page.")
        };
        if (!typeof(UObject).IsAssignableFrom(type))
            throw new InvalidOperationException("Expected a Unity object: " + type.FullName);
        var cast = typeof(Il2CppObjectBase).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "TryCast" && m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0).MakeGenericMethod(type);
        var objects = Resources.FindObjectsOfTypeAll(Il2CppType.From(type));
        int inspected = 0;
        if (objects != null)
            foreach (var obj in objects)
            {
                if (++inspected > ScanLimit) { capped = true; break; }
                if (obj == null) continue;
                try
                {
                    var item = cast.Invoke(obj, null) as UObject;
                    if (item == null) continue;
                    string detail, key = null;
                    if (request == 0 && item is GameObject go)
                    {
                        if (!go.scene.IsValid() || !go.scene.isLoaded || go.scene.handle != scene) continue;
                        detail = (go.activeInHierarchy ? "Active" : "Inactive") + " | " +
                            go.transform.childCount + " children | " + go.transform.position;
                    }
                    else if (request == 1 && item is Component component)
                    {
                        var goScene = component.gameObject.scene;
                        if (!goScene.IsValid() || !goScene.isLoaded || goScene.handle != scene) continue;
                        key = PieceKey(Call(item, "get_PieceId"));
                        detail = "ID " + key + " | " + component.transform.position;
                    }
                    else if (request == 2)
                        detail = "ID " + Display(Call(item, "get_PieceTypeId")) + " | HP " + Display(Call(item, "get_Hp")) +
                            " | AP " + Display(Call(item, "get_Ap")) + " | ATK " + Display(Call(item, "get_Atk"));
                    else if (request == 3)
                    {
                        key = CardKey(Call(item, "get_CardTypeId"));
                        detail = "ID " + key;
                    }
                    else if (request == 4) detail = "GUID " + Display(Call(item, "get_CachedGuid"));
                    else continue;
                    items.Add(new Entry(item, item.name ?? "(unnamed)", detail, key));
                }
                catch (Exception ex)
                {
                    if (++skipped == 1) log.LogWarning("BMF debug snapshot skipped object: " + ex.GetBaseException().Message);
                }
            }
        entries.AddRange(items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        ApplyFilter();
        status = entries.Count + " loaded entries" + (capped ? " (scan capped at " + ScanLimit + ")" : "") +
            (skipped > 0 ? " | " + skipped + " unreadable" : "") + ".";
    }

    private static Type Load(string assembly, string type) => Assembly.Load(new AssemblyName(assembly)).GetType(type, true);
    private static object Call(object target, string method)
    {
        if (target == null) throw new InvalidOperationException("Missing native object.");
        var m = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.ReturnType == typeof(void))
            throw new InvalidOperationException("Unavailable getter: " + method);
        return m.Invoke(target, null);
    }
    internal static object ReadMember(object target, string name)
    {
        if (target == null) throw new InvalidOperationException("Missing native object: " + name);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = target.GetType();
        var property = type.GetProperty(name, flags);
        if (property != null && property.GetIndexParameters().Length == 0 && property.CanRead)
            return property.GetValue(target);
        var field = type.GetField(name, flags);
        if (field != null) return field.GetValue(target);
        throw new InvalidOperationException("Missing native member: " + type.FullName + "." + name);
    }
    private static string PieceKey(object pieceId)
    {
        var id = Call(pieceId, "get_Id");
        if (id == null) throw new InvalidOperationException("Missing piece ID.");
        return Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture);
    }
    private static string CardKey(object cardId)
    {
        if (Call(cardId, "get_Id") is not string id || string.IsNullOrWhiteSpace(id) ||
            Call(cardId, "get_Guid") is not string guid || string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Missing card ID or GUID.");
        return id + " | " + guid;
    }
    private static string Display(object item) => item?.ToString() ?? "(missing)";
    private static string Clip(string text, int length = 110) =>
        text == null ? "" : text.Length <= length ? text : text.Substring(0, length - 3) + "...";

    private void ApplyFilter()
    {
        hits = entries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        index = 0;
    }

    // Returns true only on the optional legacy native-tool page.
    internal bool Draw(float x, float y, float width, float height)
    {
        try
        {
            for (int i = 0; i < Pages.Length; i++)
            {
                float w = (width - 30) / 4;
                if (GUI.Button(new Rect(x + 15 + i % 4 * w, y + 96 + i / 4 * 29, w - 3, 26),
                    page == i ? "[" + Pages[i] + "]" : Pages[i]) && page != i)
                {
                    page = i;
                    entries.Clear(); hits.Clear(); selected = null; index = 0; filter = "";
                    confirming = false; scanPage = null; pending = null;
                    status = "Select Refresh to inspect loaded objects.";
                }
            }
            if (page == 6) return true;
            void Label(float top, string text) => GUI.Label(new Rect(x + 15, y + top, width - 30, 23), Clip(text));
            bool Button(float top, float left, float size, string text) =>
                GUI.Button(new Rect(x + left, y + top, size, 25), text);
            if (page == 5)
            {
                Label(158, "Observed calls since probe startup (not authoritative game state)");
                bool core = plugin.IsRunning("org.battlemarked.diagnostics.corelogic");
                bool view = plugin.IsRunning("org.battlemarked.diagnostics.gameplay");
                Label(182, "Core probe: " + (core ? "running" : "off") + " | View probe: " + (view ? "running" : "off"));
                var a = core ? CoreLogicProbe.Summary() : new[] { "Enable in Mods; restart." };
                var b = view ? GameplayProbe.Summary() : new[] { "Enable in Mods; restart." };
                float half = (width - 30) / 2;
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    if (i < a.Length) GUI.Label(new Rect(x + 15, y + 207 + i * 22, half, 22), a[i]);
                    if (i < b.Length) GUI.Label(new Rect(x + 15 + half, y + 207 + i * 22, half, 22), b[i]);
                }
                return false;
            }
            Label(157, page == 0 ? "Active scene: " + SceneManager.GetActiveScene().name :
                page == 1 ? "Current scene piece views | Select a hero for Cards" :
                page == 3 ? "Loaded cards | Grant: disposable local solo only; back up saves." : "Loaded definitions only (not the entire game catalogue)");
            if (Button(181, 15, 90, "Refresh")) { scanPage = page; confirming = false; }
            var typed = GUI.TextField(new Rect(x + 111, y + 181, width - 126, 25), filter, 100);
            if (typed != filter) { filter = typed; ApplyFilter(); confirming = false; }
            float footer = height - (page == 3 ? 128 : 99);
            int rows = Math.Max(1, (int)((footer - 211) / 25));
            int pages = Math.Max(1, (hits.Count + rows - 1) / rows);
            index = Math.Clamp(index, 0, pages - 1);
            for (int i = 0; i < rows && index * rows + i < hits.Count; i++)
            {
                var e = hits[index * rows + i];
                if (Button(211 + i * 25, 15, width - 30, Clip((ReferenceEquals(selected, e) ? "[Selected] " : "") + e.Name + " | " + e.Detail)))
                {
                    selected = e; confirming = false;
                    if (page == 1) target = e;
                    if (page == 3) card = e;
                }
            }
            if (Button(footer, 15, 80, "Previous")) index = Math.Max(0, index - 1);
            GUI.Label(new Rect(x + 102, y + footer, width - 220, 24), (index + 1) + "/" + pages + " | " + hits.Count + " matches");
            if (Button(footer, width - 95, 80, "Next")) index = Math.Min(pages - 1, index + 1);
            Label(footer + 25, selected == null ? "Select a row to inspect." : selected.Name + " | " + selected.Detail);
            if (page == 3)
            {
                Label(footer + 47, "Target: " + (target?.Name ?? "select Board hero") + " | Card: " + (card?.Name ?? "select card"));
                if (plugin.VerifiedGame && target != null && card != null && sceneHandle.HasValue && pending == null && !scanPage.HasValue)
                {
                    if (Button(footer + 70, 15, width - 30, confirming ? "Confirm grant (local solo; experimental)" : "Grant selected card (local solo only)"))
                    {
                        if (confirming)
                        {
                            pending = new Grant(target, card, sceneHandle.Value);
                            confirming = false;
                            status = "Card request queued for scope verification.";
                        }
                        else confirming = true;
                    }
                }
                else Label(footer + 70, "Grant requires a verified build, target and card.");
            }
            Label(height - 29, status);
            return false;
        }
        catch (Exception ex)
        {
            if (!guiErrorLogged) log.LogWarning("BMF debug workspace GUI: " + ex.GetBaseException().Message);
            guiErrorLogged = true;
            GUI.Label(new Rect(x + 15, y + 158, width - 30, 25), "Inspector unavailable; see BepInEx log.");
            return false;
        }
    }

    private void GrantCard(Grant command)
    {
        if (!plugin.VerifiedGame) throw new InvalidOperationException("Unverified game build.");
        if (command.Target.Object == null || command.Card.Object == null ||
            command.Target.Object is not Component view || view.gameObject.scene.handle != command.Scene ||
            !view.gameObject.scene.isLoaded || !view.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Selection expired or hero view inactive.");
        var id = Call(view, "get_PieceId");
        var cardId = Call(command.Card.Object, "get_CardTypeId");
        if (PieceKey(id) != command.Target.Key || CardKey(cardId) != command.Card.Key)
            throw new InvalidOperationException("Selection identity changed; refresh and select again.");
        var messageType = Load("Jade.Messaging", "Jade.Messaging.RequestGiveCard");
        var requestType = Load("Jade.Messaging", "Jade.Messaging.IRequest");
        var cardIdType = Load("Jade.GameModel", "Jade.GameModel.CardTypeId");
        var pieceIdType = Load("Jade.GameModel", "Jade.GameModel.PieceId");
        var roomType = Load("Jade.Networking", "Jade.Networking.LocalNetworkRoom");
        if (!cardIdType.IsInstanceOfType(cardId) || !pieceIdType.IsInstanceOfType(id) ||
            !requestType.IsAssignableFrom(messageType))
            throw new InvalidOperationException("Unexpected request or ID type.");
        var ctor = messageType.GetConstructor(new[] { cardIdType, pieceIdType, typeof(bool), typeof(bool) });
        var send = roomType.GetMethod("Send", BindingFlags.Public | BindingFlags.Instance, null, new[] { requestType }, null);
        if (ctor == null || send == null || send.ReturnType != typeof(void))
            throw new InvalidOperationException("Exact card request binding unavailable.");
        if (!HouseRules.TryGetDebugRoomForPieceId(id, cardId, command.Card.Object, out var room, out var reason) || !roomType.IsInstanceOfType(room))
            throw new InvalidOperationException("Grant rejected: " + reason);
        if (Call(room, "get_HasRequestsInQueue") is not bool busy || busy)
            throw new InvalidOperationException("Local room busy or queue state unavailable; retry manually.");
        var request = ctor.Invoke(new[] { cardId, id, (object)false, false });
        // One verified local request. Normal transform and notification behavior;
        // never force a grant, patch a request handler or retry automatically.
        send.Invoke(room, new[] { request });
        status = "Card request submitted; check the hero's hand for the result.";
        log.LogInfo("BMF debug card request submitted to verified local room: " + command.Card.Name);
    }
}
