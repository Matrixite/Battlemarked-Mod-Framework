using System.Reflection;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.InteropTypes;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Starting preferences are persistent; the current adventure's attempt
// history is not. Only the ordinary HeroForms native request may transform.
internal static class HeroStartingForms
{
    private static ModuleContext ctx;
    private static ConfigEntry<bool> enabled;
    private static StartingFormStore store;
    private static StartingFormRun run = new();
    private static DateTime nextTick;
    private static int mainThread;
    private static Type pieceType, formIdType, definitionType;
    private static ConstructorInfo idCtor;
    private static readonly List<Choice> party = new();
    internal static string Status { get; private set; } = "Starting forms not available.";
    internal static bool Enabled => enabled?.Value == true;
    internal static bool Available => store != null;
    internal static long Epoch => run.Epoch;

    internal sealed class Choice
    {
        internal string Key, Name;
        internal bool Present;
        internal StartingFormAssignment Assignment;
    }
    private sealed class Hero
    {
        internal Choice Choice;
        internal object Piece;
    }

    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    private static Type Load(string assembly, string name) => Assembly.Load(new AssemblyName(assembly)).GetType(name, true);
    private static bool Active => ctx != null && store != null && Plugin.Instance?.VerifiedGame == true &&
        Plugin.Instance?.HeroFormsEnabled == true && Environment.CurrentManagedThreadId == mainThread;

    internal static void Attach(ModuleContext context)
    {
        Stop();
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            enabled = context.Config.Bind("StartingForms", "Enabled", false, "Apply saved monster forms on the next playable local adventure.");
            pieceType = Load("Jade.GameModel", "Jade.GameModel.Piece");
            formIdType = Load("Jade.GameModel", "Jade.GameModel.PieceTypeId");
            definitionType = Load("GameDefinitions", "Jade.GameDefinitions.PieceDefinition");
            idCtor = formIdType.GetConstructor(new[] { typeof(string), typeof(string) });
            if (idCtor == null) throw new InvalidOperationException("Native monster ID constructor unavailable.");
            store = new StartingFormStore(Path.Combine(context.DataDirectory, "starting-forms.json"));
            ctx = context;
            Status = "Choose a saved character and monster for the next adventure.";
        }
        catch (Exception ex)
        {
            Stop();
            Status = "Starting forms unavailable: " + ex.GetBaseException().Message;
            context.Log.LogWarning("BMF starting forms: " + Status);
        }
    }
    internal static void Stop()
    {
        ctx = null; enabled = null; store = null; party.Clear();
        run = new StartingFormRun(); nextTick = DateTime.MinValue;
        Status = "Starting forms not available.";
    }

    private static bool Observe(object state)
    {
        var selection = Get(state, "scenarioSelection");
        if (selection == null || Get(selection, "currentScenarioStepIndex") is not int index) return false;
        return run.Observe(Get(state, "sessionId") as string,
            HeroFormPersistence.StageKey(Get(selection, "campaignId")),
            HeroFormPersistence.StageKey(Get(selection, "scenarioId")),
            Get(selection, "currentScenarioRunId") as string ?? "",
            HeroFormPersistence.StageKey(Get(selection, "stepId")), index);
    }

    // Resolve the real saved character, not the current creature or party
    // index. Duplicate or unverified identities cannot receive an assignment.
    private static Choice Identify(object state, object piece)
    {
        try
        {
            if (piece is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero ||
                Get(piece, "isPlayerControlled") is not bool controlled || !controlled ||
                Get(piece, "team")?.ToString() != "Players" ||
                Get(piece, "isTransformedPiece") is not bool transformed ||
                !HeroFormPersistence.TryReadCharacterSheet(state, piece, out var sheet, out var account)) return null;
            var originalId = Get(sheet, "pieceTypeId");
            string original = HeroFormPersistence.Key(originalId);
            if (!transformed && HeroFormPersistence.Key(Get(piece, "typeId")) != original) return null;
            string key = StartingFormStore.CharacterKey(Call(Get(piece, "characterSheetId"), "get_Id") as string, original, account);
            if (key == null) return null;
            string name = Call(originalId, "get_Id") as string ?? "Hero";
            // Distinguish two saved characters of the same hero class.
            name += " [" + key.Substring(0, 6) + "]";
            return new Choice { Key = key, Name = name, Present = true, Assignment = store.Get(key) };
        }
        catch { return null; }
    }

    private static List<Hero> ReadParty(LevelEditor.Context live)
    {
        var found = new List<Hero>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        var pieces = Get(Get(live.Board, "turnQueue"), "pieces");
        if (Call(pieces, "get_Count") is not int count || count < 0 || count > 4096) return found;
        var item = pieces.GetType().GetMethod("get_Item", new[] { typeof(int) });
        if (item == null || item.ReturnType != pieceType) return found;
        for (int i = 0; i < count; i++)
        {
            var piece = item.Invoke(pieces, new object[] { i });
            var choice = Identify(live.State, piece);
            if (choice == null || !HouseRules.TryValidateLocalHero(live, piece, out _)) continue;
            if (!seen.Add(choice.Key)) { duplicate.Add(choice.Key); continue; }
            found.Add(new Hero { Choice = choice, Piece = piece });
        }
        for (int i = found.Count - 1; i >= 0; i--)
            if (duplicate.Contains(found[i].Choice.Key)) found.RemoveAt(i);
        return found;
    }

    internal static List<Choice> Choices()
    {
        var result = new List<Choice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hero in party)
        {
            result.Add(new Choice { Key = hero.Key, Name = hero.Name, Present = true, Assignment = store?.Get(hero.Key) });
            seen.Add(hero.Key);
        }
        if (store != null)
            foreach (var item in store.List())
                if (seen.Add(item.CharacterKey))
                    result.Add(new Choice { Key = item.CharacterKey, Name = item.HeroName, Assignment = item });
        return result;
    }

    internal static void SetEnabled(bool value)
    {
        if (!Active) return;
        try
        {
            if (value && !Enabled && HouseRules.TryGetHeroFormsContext(out var live, out _))
            {
                if (!Observe(live.State)) throw new InvalidOperationException("Current adventure identity unavailable.");
                foreach (var choice in store.List()) run.Defer(choice.CharacterKey);
            }
            enabled.Value = value;
            ctx.Config.Save();
            Status = value ? "Starting forms enabled for your next adventure." : "Starting forms off; saved choices kept.";
        }
        catch (Exception ex) { Report(ex); }
    }

    internal static void Assign(LevelEditor.Context context, string key, UObject definition, object id, string name)
    {
        if (!Active) return;
        try
        {
            if (!HouseRules.TryGetHeroFormsContext(out var live, out _) || !context.SameSession(live) || !Observe(live.State))
                throw new InvalidOperationException("Refresh on a playable local solo board.");
            Choice selected = null;
            foreach (var hero in ReadParty(live))
                if (hero.Choice.Key == key) selected = hero.Choice;
            if (selected == null) throw new InvalidOperationException("Saved character no longer in the local party.");
            string formId = Call(id, "get_Id") as string, guid = Call(id, "get_Guid") as string;
            var assignment = new StartingFormAssignment { CharacterKey = key, HeroName = selected.Name,
                FormId = formId, FormGuid = guid, FormName = name };
            if (Resolve(live, assignment, out _) is not UObject registered || registered != definition)
                throw new InvalidOperationException("Selected monster is no longer registered.");
            store.Set(assignment);
            run.Defer(key);
            Status = "Saved " + name + " for " + selected.Name + " on the next adventure.";
        }
        catch (Exception ex) { Report(ex); }
    }

    internal static void ClearChoice(string key)
    {
        if (!Active || store.Get(key) == null) return;
        try
        {
            store.Clear(key);
            run.Defer(key);
            HeroFormPersistence.ForgetStartingChoice(key);
            Status = "Starting form cleared; original hero on the next adventure.";
        }
        catch (Exception ex) { Report(ex); }
    }

    private static UObject Resolve(LevelEditor.Context live, StartingFormAssignment assignment, out object id)
    {
        id = null;
        if (live.Registry == null) return null;
        id = idCtor.Invoke(new object[] { assignment.FormId, assignment.FormGuid });
        var method = live.Registry.GetType().GetMethod("GetPieceDefinition", new[] { formIdType });
        if (method == null || method.ReturnType != definitionType) return null;
        var definition = method.Invoke(live.Registry, new[] { id }) as UObject;
        if (definition == null || HeroFormPersistence.Key(Call(definition, "get_PieceTypeId")) != assignment.FormKey ||
            Call(definition, "get_PlayerControllable") is not bool controllable || controllable ||
            !LevelEditor.ValidateRegisteredStartingForm(live, definition)) return null;
        return definition;
    }

    // Recheck the actual authoritative character and persisted choice when
    // the native request is consumed, so a toggle/edit/clear cancels it.
    internal static bool Authorizes(string key, string destination, long epoch, object state, object piece)
    {
        if (!Active || !Enabled || epoch != run.Epoch || !run.HasPriority(key) ||
            store.Get(key)?.FormKey != destination ||
            Get(piece, "isTransformedPiece") is not bool transformed || transformed) return false;
        var selection = Get(state, "scenarioSelection");
        if (selection == null || Get(selection, "currentScenarioStepIndex") is not int index ||
            !run.MatchesFirstBoard(Get(state, "sessionId") as string,
                HeroFormPersistence.StageKey(Get(selection, "campaignId")),
                HeroFormPersistence.StageKey(Get(selection, "scenarioId")),
                Get(selection, "currentScenarioRunId") as string ?? "",
                HeroFormPersistence.StageKey(Get(selection, "stepId")), index)) return false;
        return Identify(state, piece)?.Key == key;
    }

    internal static bool PreferStartingForm(LevelEditor.Context live, object piece)
    {
        if (!Active || !Enabled || !Observe(live.State)) return false;
        var choice = Identify(live.State, piece);
        return choice != null && choice.Assignment != null && run.HasPriority(choice.Key);
    }

    internal static void Tick()
    {
        if (!Active) { party.Clear(); return; }
        if (DateTime.UtcNow < nextTick) return;
        nextTick = DateTime.UtcNow.AddMilliseconds(500);
        try
        {
            party.Clear();
            if (!HouseRules.TryGetHeroFormsContext(out var live, out _)) return;
            if (!Observe(live.State)) return;
            var heroes = ReadParty(live);
            foreach (var hero in heroes) party.Add(hero.Choice);
            if (!Enabled || !run.FirstBoard || HeroForms.Waiting ||
                Call(live.Room, "get_HasRequestsInQueue") is not bool busy || busy) return;
            foreach (var hero in heroes)
            {
                var choice = hero.Choice;
                var assignment = choice.Assignment;
                if (assignment == null || !run.CanApply(choice.Key)) continue;
                if (Get(hero.Piece, "isTransformedPiece") is not bool transformed || transformed)
                { run.MarkAttempt(choice.Key); continue; }
                UObject definition;
                object id;
                try
                {
                    HeroForms.ValidatePiece(live.State, live.Board, hero.Piece);
                    definition = Resolve(live, assignment, out id);
                    if (definition == null) { Status = "Waiting for registered " + assignment.FormName + "."; continue; }
                    HeroForms.ValidateFormFit(live.Board, hero.Piece, definition);
                }
                catch (Exception ex) { Status = "Starting form waiting: " + ex.GetBaseException().Message; continue; }
                HeroForms.Queue(live, hero.Piece, definition, id, automatic: true, startingKey: choice.Key);
                run.MarkAttempt(choice.Key);
                Status = "Starting form requested: " + assignment.FormName + " for " + choice.Name + ".";
                ctx.Log.LogInfo("BMF starting forms: " + Status);
                return;
            }
        }
        catch (Exception ex)
        {
            Report(ex);
            nextTick = DateTime.UtcNow.AddSeconds(2);
        }
    }

    internal static void ReportSelectionNeeded() => Status = "Refresh, choose a local hero and registered monster.";

    private static void Report(Exception ex)
    {
        string message = "Starting forms: " + ex.GetBaseException().Message;
        if (Status != message) ctx?.Log.LogWarning("BMF " + message);
        Status = message;
    }
}
