using System.Reflection;
using System.Threading;
using HarmonyLib;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine.SceneManagement;

namespace Battlemarked.ModFramework;

// Read-only by default; opt-in repeated AP bonus, local solo scope only.
internal sealed class HouseRules : BattlemarkedModule
{
    internal const string ModuleId = "org.battlemarked.houserules";
    public override string Id => ModuleId;
    public override string Name => "House rules (AP and hero health)";
    private ModuleContext ctx;
    private HeroHealthRule health;
    internal static bool NativeHealthRequested => running?.health is { } h && (h.Armed || h.Queued);
    internal static void ToggleNativeHealthArm()
    {
        var r = running;
        if (r?.health == null || Environment.CurrentManagedThreadId != r.mainThread) return;
        r.ConsumeUnverifiedHealthTransition();
        if (r.health.Armed || r.health.Queued) { r.health.Disarm(); return; }
        if (r.faulted || !r.healthStartBound)
        { r.health.RejectArm("Fresh campaign detection unavailable"); return; }
        // Queueing cannot change the current adventure, even after AP activity.
        r.health.QueueForFreshCampaign();
    }
    internal static int NextHealthPercent => running?.health?.NextPercent ?? 100;
    internal static bool CanConfigureHealth => running?.health?.Available ?? false;
    internal static string HealthStatus => running?.health?.Status ?? "Health rule unavailable";
    internal static string HealthCounts => running?.health?.Counts ?? "No health observations";
    internal static void ChangeHealthPercent(int delta) => running?.health?.ChangePercent(delta);
    private static HouseRules running;
    private static object model;
    private PropertyInfo modelPointer, healthRoomPointer;
    private MethodInfo playingRoom;
    private bool healthStartBound;
    private int unverifiedHealthTransition;
    private MethodInfo hudModel, networkRoom, localPlayerCount, roomTryCast, roomPlayers, roomConnected, roomHost;
    private Type localRoomType;
    // The actual request handler owns the card registry used for grants.
    // Keep a bounded set of captures, keyed by the native GameState identity.
    private readonly Dictionary<IntPtr, (Il2CppObjectBase State, object Registry)> debugCardRegistries = new();
    private bool debugRegistryCaptureError;
    private readonly Dictionary<IntPtr, (Il2CppObjectBase State, object Spawner)> debugSpawnContexts = new();
    private bool debugSpawnCaptureError;
    private bool modelFromHud;
    private int ignoredGetterCaptures;
    private string roomHook = "not attempted", lastFlags = "not observed", lastRejectedFlags = "not observed";
    private IntPtr modelKey, scopeModelKey;
    private int? sceneHandle, capturedSceneHandle;
    private int checkingScope, modelCaptures, hudCaptures, retainedCaptures, ignoredTeardowns, captureErrors;
    private string lastScope = "not observed", hudHook = "not attempted";
    private readonly Dictionary<string, int> scopeRejections = new(StringComparer.Ordinal);
    private MethodInfo solo, local, multi, inGame, playing, host, activeHero, localHero, sheet, allowance, remaining;
    private MethodInfo stats, getStat, getMax, getObject, getValue, getObjectMax, getPermanentMax;
    private object apType;
    private MethodInfo setStat, setMax;
    private PropertyInfo pointer, statPointer;
    private ConfigEntry<int> nextBonus;
    private ConfigEntry<bool> nextAdjustExisting;
    private bool adjustExisting;
    private ConfigEntry<bool> nextRefundMode;
    private bool refundMode, requestedRefundMode;
    private DateTime lastModeCheck;
    private bool localScopeSeen, teardownSinceLocalScope;
    private int refunds, refundResets, refundElevatedSkipped, partialRefundResets;
    private bool partialRefills;
    private int adjustedCaps, elevatedSkipped;
    private int bonus, writing, grants, restores, duplicateRefills, nestedSkipped, unresolved, openingGrants;
    private int nextId;
    private sealed class HeroState
    {
        internal int Id, Grants, Restores, Spent, SpentSinceGrant, BonusCycles;
        internal bool PendingRestore;
        internal int RefundsThisCycle, Refunds, RefundCycleCap;
    }
    // Key by native stat object, not managed wrapper; distinct native copies remain distinct.
    private readonly Dictionary<IntPtr, HeroState> heroes = new();
    internal static int ActiveBonus => running?.bonus ?? 0;
    internal static bool ActiveAdjustExisting => running?.adjustExisting ?? false;
    internal static bool ActiveRefundMode => running?.refundMode ?? false;
    internal static bool ModePending => running != null && running.requestedRefundMode != running.refundMode;
    internal static void ToggleRefundMode()
    {
        var r = running;
        if (!CanConfigure || r.nextRefundMode == null || Environment.CurrentManagedThreadId != r.mainThread) return;
        r.requestedRefundMode = !r.requestedRefundMode;
        r.TryApplyModeChange();
        r.SaveApPreference(() => r.nextRefundMode.Value = r.requestedRefundMode,
            r.requestedRefundMode == r.refundMode ? "AP mode active" : "AP mode change queued until idle scope");
    }
    internal static void ToggleAdjustExisting()
    {
        var r = running;
        if (!CanConfigure || r.nextAdjustExisting == null || Environment.CurrentManagedThreadId != r.mainThread) return;
        r.adjustExisting = !r.adjustExisting;
        r.SaveApPreference(() => r.nextAdjustExisting.Value = r.adjustExisting,
            "Live AP existing-cap adjustment=" + r.adjustExisting);
    }
    internal static bool CanConfigure => running != null && running.setStat != null && running.setMax != null && running.pointer != null && running.statPointer != null && !running.faulted;
    internal static void ChangeBonus(int delta)
    {
        var r = running;
        if (r == null || r.nextBonus == null || Environment.CurrentManagedThreadId != r.mainThread || (!CanConfigure && delta != -3)) return;
        r.bonus = CanConfigure ? Math.Clamp(r.bonus + delta, 0, 3) : 0;
        // Do not clear hero state, reset refund usage, or write any game stat here.
        // Disabling stops new grants; already outstanding cap restorations still run.
        r.RefreshApStatus();
        r.SaveApPreference(() => r.nextBonus.Value = r.bonus, "Live AP bonus=" + r.bonus);
    }
    internal static string PresetName => running == null ? "Unavailable" :
        HouseRulePreset.Match(ActiveBonus, NextHealthPercent, running.requestedRefundMode, ActiveAdjustExisting);
    internal static bool ApplyPreset(HouseRulePreset preset)
    {
        var r = running;
        if (r == null || !CanConfigure || !CanConfigureHealth || Environment.CurrentManagedThreadId != r.mainThread ||
            !HouseRulePreset.All.Contains(preset)) return false;
        // Preference changes only: no native writes, budget resets, or health authorization.
        if (r.health.Armed || r.health.Queued) r.health.Disarm("Preset selected; queue health separately");
        r.health.SetPercent(preset.HealthPercent);
        r.bonus = preset.Bonus;
        r.adjustExisting = preset.AdjustExisting;
        r.requestedRefundMode = preset.Refund;
        r.TryApplyModeChange();
        r.RefreshApStatus();
        r.SaveApPreference(() =>
        {
            r.nextBonus.Value = r.bonus;
            r.nextAdjustExisting.Value = r.adjustExisting;
            r.nextRefundMode.Value = r.requestedRefundMode;
        }, "House rule preset selected: " + preset.Name);
        return true;
    }

    internal static string[] CompatibilityLines()
    {
        var r = running;
        if (r == null) return new[] { "House rules not active; inspect module state and BepInEx log." };
        return new[] {
            "AP: " + Status,
            "AP: " + Counts,
            $"AP mode={(r.refundMode ? "refund" : "cap")} requested={(r.requestedRefundMode ? "refund" : "cap")} pending={ModePending}",
            $"AP pending cap restores={r.heroes.Values.Count(h => h.PendingRestore)} unresolved={r.unresolved}",
            "Scope: " + r.lastScope,
            "Session flags: " + r.lastFlags,
            "HUD hook: " + r.hudHook,
            "Room hook: " + r.roomHook,
            $"Fresh campaign hooks requested successfully={r.healthStartBound}",
            "Health: " + HealthStatus,
            "Health: " + HealthCounts,
            r.health?.SafetySummary ?? "Health ledger unavailable",
            "Selected preset: " + PresetName
        };
    }
    // Refresh the reports without touching native game objects or gameplay rules.
    internal static void RefreshReports()
    {
        var r = running;
        if (r == null) return;
        r.Write();
        r.health?.Write();
    }
    private void SaveApPreference(Action update, string detail)
    {
        try { update(); ctx.Config.Save(); }
        catch (Exception ex)
        {
            Sample("AP preference save failed; current-launch value retained");
            ctx.Log.LogWarning("AP preference save: " + ex.Message);
        }
        Sample(detail);
        ctx.Log.LogInfo(detail);
        Write();
    }
    private void RefreshApStatus()
    {
        if (faulted) return;
        status = bonus == 0 ? "AP disabled; no new bonuses" : refundMode ?
            $"Experimental +{bonus} AP via capped refunds" : $"Experimental +{bonus} AP on refill or first paid action";
        dirty = true;
    }
    private void TryApplyModeChange()
    {
        lastModeCheck = DateTime.UtcNow;
        if (requestedRefundMode == refundMode || !CanConfigure || writing != 0 || heroes.Count != 0) return;
        try
        {
            var m = model;
            // A scene change alone is not proof that an adventure ended. With
            // no captured model after gameplay, wait for an actual teardown.
            bool idleScope = m != null ? !Flag(inGame, m) && !Flag(playing, m)
                : !localScopeSeen || teardownSinceLocalScope;
            if (!ApRefundBudget.CanApplyMode(idleScope, heroes.Count, writing != 0)) return;
            refundMode = requestedRefundMode;
            RefreshApStatus();
            Sample("Live AP refund mode=" + refundMode);
            ctx.Log.LogInfo("Live AP refund mode=" + refundMode);
        }
        catch (Exception ex)
        {
            // Leave the requested mode pending rather than switching with unknown state.
            ctx.Log.LogWarning("AP mode change deferred: " + ex.Message);
        }
    }
    private int mainThread, refillCalls, spendCalls, samplesTaken, skipped, errors;
    private long otherThread;
    private bool faulted, dirty;
    private string status = "not started", scene = "unknown";
    private DateTime lastWrite;
    private readonly List<string> samples = new();
    internal static string Status => running == null ? "Module not running" : running.status;
    internal static string Counts => running == null ? "No active module" : $"Grants: {running.grants} | Refunds: {running.refunds} | Errors: {running.errors}";
    private static MethodInfo Exact(Type type, string name, string signature)
    {
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters && Convert.ToHexString(m.Module.ResolveSignature(m.MetadataToken)) == signature).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Exact method unavailable: " + type.FullName + "." + name);
        return matches[0];
    }
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(HouseRules).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    public override void OnStart(ModuleContext context)
    {
        ctx = context; mainThread = Environment.CurrentManagedThreadId;
        nextBonus = ctx.Config.Bind("HouseRules", "ExtraHeroAP044", 0, "Experimental repeated extra AP, 0-3. Applies live; refunds already granted are not reset. Disposable local solo only.");
        bonus = Math.Clamp(nextBonus.Value, 0, 3);
        nextAdjustExisting = ctx.Config.Bind("HouseRules", "AdjustExistingAP046", false,
            "Disposable solo only: include consistent existing 3-5 AP caps with allowance 2 in the bonus calculation. Cap mode may overwrite legitimate modifiers; refund mode preserves the cap. Applies live.");
        nextRefundMode = ctx.Config.Bind("HouseRules", "RefundMode047", false,
            "Experimental alternative: refund single paid AP actions without changing max AP. Applies at idle scope; no restart. Disposable solo only.");
        requestedRefundMode = refundMode = nextRefundMode.Value;
        adjustExisting = nextAdjustExisting.Value;
        if (!ctx.IsVerifiedBuild(CoreLogicProbe.VerifiedHash)) { status = "Skipped: unverified game fingerprint"; Write(); return; }
        try
        {
            var logic = Assembly.Load(new AssemblyName("GameLogic"));
            var view = Assembly.Load(new AssemblyName("GameView"));
            var gameModel = Assembly.Load(new AssemblyName("Jade.GameModel"));
            var refill = Exact(logic.GetType("Jade.GameLogic.ActionPointUpdateHelper", true), "RefillActionPoints", "0001011269");
            var piece = refill.GetParameters()[0].ParameterType;
            if (piece.FullName != "Jade.GameModel.Piece") throw new InvalidOperationException("Unexpected piece type");
            var hero = view.GetType("Jade.GameView.UI.HeroSelectModel", true);
            solo = Exact(hero, "get_IsSinglePlayer", "200002");
            local = Exact(hero, "get_IsLocalGameMode", "200002");
            multi = Exact(hero, "get_IsMultiplayer", "200002");
            inGame = Exact(hero, "get_InGame", "200002");
            playing = Exact(hero, "get_GameStateIsPlayingLevel", "200002");
            host = Exact(hero, "get_LocalIsHost", "200002");
            activeHero = Exact(hero, "IsAnyActiveHero", "200102128335");
            localHero = Exact(hero, "IsActiveLocalPlayerCharacterSheet", "200102128335");
            sheet = Exact(piece, "get_characterSheetId", "2000128490");
            allowance = Exact(piece, "get_actionPointsPerTurn", "200008");
            remaining = Exact(gameModel.GetType("Jade.GameModel.PieceHelpers", true), "GetRemainingActionPoints", "00010812821C");
            if (activeHero.GetParameters()[0].ParameterType != sheet.ReturnType || localHero.GetParameters()[0].ParameterType != sheet.ReturnType || remaining.GetParameters()[0].ParameterType != piece)
                throw new InvalidOperationException("Incompatible hero or AP parameter types");
            stats = Exact(piece, "get_stats", "2000128310");
            var facade = stats.ReturnType;
            if (facade.FullName != "Jade.GameModel.StatsFacade") throw new InvalidOperationException("Unexpected stats type");
            getStat = Exact(facade, "GetStat", "2001081185C0");
            getMax = Exact(facade, "GetStatMax", "2001081185C0");
            getObject = Exact(facade, "GetStatObject", "20011283041185C0");
            var stat = getObject.ReturnType;
            if (stat.FullName != "Jade.GameModel.Stat") throw new InvalidOperationException("Unexpected stat type");
            var enumType = getStat.GetParameters()[0].ParameterType;
            // Metadata from the supplied Jade.GameModel.dll: Stats (plural) owns Type;
            // Stat (singular) is the object returned by GetStatObject, not the enum owner.
            if (!enumType.IsEnum || enumType.FullName != "Jade.GameModel.Stats+Type")
                throw new InvalidOperationException("Unexpected AP enum type: " + enumType.FullName + "; IsEnum=" + enumType.IsEnum);
            if (getMax.GetParameters()[0].ParameterType != enumType || getObject.GetParameters()[0].ParameterType != enumType)
                throw new InvalidOperationException("AP getter parameter type mismatch");
            if (!Enum.IsDefined(enumType, "ActionPoints"))
                throw new InvalidOperationException("ActionPoints not found in " + enumType.FullName + "; names=" + string.Join(",", Enum.GetNames(enumType)));
            apType = Enum.Parse(enumType, "ActionPoints");
            if (Convert.ToInt32(apType) != 1)
                throw new InvalidOperationException("Unexpected ActionPoints enum value: " + Convert.ToInt32(apType));
            ctx.Log.LogInfo("BMF AP enum verified: " + enumType.FullName + ".ActionPoints=1");
            getValue = Exact(stat, "get_Value", "20000C");
            getObjectMax = Exact(stat, "get_Max", "20000C");
            getPermanentMax = Exact(stat, "get_PermanentMax", "20000C");
            var spend = Exact(refill.DeclaringType, "TrySubtractActionPoints", "000202081269");
            if (spend.ReturnType != typeof(bool) || spend.GetParameters()[0].ParameterType != typeof(int) || spend.GetParameters()[1].ParameterType != piece) throw new InvalidOperationException("Unexpected AP spend signature");
            // The supplied Jade.GameModel.dll confirms both setters return Boolean and take (Stats.Type, Single).
            try
            {
                setStat = Exact(facade, "TrySetStat", "2002021185C00C");
                setMax = Exact(facade, "TrySetStatMax", "2002021185C00C");
                if (new[] { setStat, setMax }.Any(m => m.IsStatic || m.ReturnType != typeof(bool) || m.GetParameters()[0].ParameterType != enumType || m.GetParameters()[1].ParameterType != typeof(float))) throw new InvalidOperationException("Unexpected setter types");
                pointer = piece.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                statPointer = stat.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                if (pointer == null || pointer.PropertyType != typeof(IntPtr) || !pointer.CanRead || statPointer == null || statPointer.PropertyType != typeof(IntPtr) || !statPointer.CanRead) throw new InvalidOperationException("Native pointer unavailable");
            }
            catch (Exception ex) { setStat = setMax = null; pointer = statPointer = null; bonus = 0; ctx.Log.LogWarning("AP trial unavailable; read-only diagnostics: " + ex.Message); }
            try { health = new HeroHealthRule(ctx, piece, facade, stat, enumType); }
            catch (Exception ex) { ctx.Log.LogWarning("Health rule unavailable: " + ex.Message); }
            var teardown = Exact(hero, "TearDown", "200001");
            modelPointer = hero.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            if (modelPointer?.PropertyType != typeof(IntPtr) || !modelPointer.CanRead)
                throw new InvalidOperationException("Native hero model identity unavailable");
            running = this; ClearCandidate();
            foreach (var method in new[] { solo, local, playing }) ctx.Patches.Patch(method, postfix: Hook(nameof(Capture)));
            // Additional read-only capture opportunities in the current UI model.
            foreach (var method in new[] { inGame, host, multi, activeHero, localHero })
            {
                try { ctx.Patches.Patch(method, postfix: Hook(nameof(Capture))); }
                catch (Exception ex) { ctx.Log.LogWarning("Optional model capture " + method.Name + ": " + ex.Message); }
            }
            // Independent room proof is optional; a missing binding preserves the
            // original strict solo flag requirement. Never instantiate a native wrapper
            // from a pointer or trust a managed type name as proof of native room type.
            try
            {
                networkRoom = Exact(hero, "get_NetworkRoom", "2000128191");
                localPlayerCount = Exact(hero, "get_LocalPlayerCount", "200008");
                var roomInterface = networkRoom.ReturnType;
                if (roomInterface.FullName != "Jade.Networking.INetworkRoom" ||
                    localPlayerCount.ReturnType != typeof(int))
                    throw new InvalidOperationException("Unexpected room or player count type");
                localRoomType = roomInterface.Assembly.GetType("Jade.Networking.LocalNetworkRoom", true);
                roomPlayers = Exact(localRoomType, "get_NumPlayers", "200008");
                roomConnected = Exact(localRoomType, "IsConnected", "200002");
                roomHost = Exact(localRoomType, "IsHost", "200002");
                if (roomPlayers.ReturnType != typeof(int) || roomConnected.ReturnType != typeof(bool) || roomHost.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Unexpected local room proof types");
                var casts = roomInterface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 &&
                        m.GetParameters().Length == 0 && m.ReturnType == m.GetGenericArguments()[0] &&
                        m.DeclaringType?.FullName == "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase").ToArray();
                if (casts.Length != 1) throw new InvalidOperationException("Native checked cast unavailable");
                roomTryCast = casts[0].MakeGenericMethod(localRoomType);
                roomHook = "bound";
            }
            catch (Exception ex) { roomTryCast = null; roomHook = "unavailable: " + ex.Message; ctx.Log.LogWarning("Optional local room proof: " + ex.Message); }
            // Optional, read-only capture of the actual request handler's registry.
            // Never rely on a disabled developer facade or an unregistered asset.
            try
            {
                var handler = logic.GetType("Jade.GameLogic.RequestHandlerGiveCard", true);
                var stateType = gameModel.GetType("Jade.GameModel.GameState", true);
                var registryType = Assembly.Load(new AssemblyName("GameDefinitions"))
                    .GetType("Jade.GameDefinitions.CardRegistry", true);
                ConstructorInfo constructor = null;
                foreach (var candidate in handler.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var parameters = candidate.GetParameters();
                    if (parameters.Length != 3) continue;
                    bool hasState = false, hasRegistry = false;
                    foreach (var parameter in parameters)
                    {
                        hasState |= parameter.ParameterType == stateType;
                        hasRegistry |= parameter.ParameterType == registryType;
                    }
                    if (!hasState || !hasRegistry) continue;
                    if (constructor != null) throw new InvalidOperationException("Ambiguous card registry constructor.");
                    constructor = candidate;
                }
                if (constructor == null) throw new InvalidOperationException("Card registry constructor unavailable.");
                ctx.Patches.Patch(constructor, postfix: Hook(nameof(CaptureDebugCardRegistry)));
            }
            catch (Exception ex) { ctx.Log.LogWarning("Optional debug card registry capture: " + ex.Message); }
            // Capture the authoritative spawn handler without altering its behavior.
            try
            {
                var handler = logic.GetType("Jade.GameLogic.RequestHandlerSpawnPiece", true);
                var spawner = logic.GetType("Jade.GameLogic.PieceSpawner", true);
                var constructor = handler.GetConstructor(new[] { spawner });
                if (constructor == null) throw new InvalidOperationException("Spawn handler constructor unavailable.");
                ctx.Patches.Patch(constructor, postfix: Hook(nameof(CaptureDebugSpawnContext)));
            }
            catch (Exception ex) { ctx.Log.LogWarning("Optional level editor spawn capture: " + ex.Message); }
            // The supplied interop report has separate native start and join
            // entry points. Keep this independent of the working AP hooks.
            try
            {
                if (health == null || !health.Available || roomTryCast == null)
                    throw new InvalidOperationException("Health or native local room metadata unavailable");
                var app = Assembly.Load(new AssemblyName("Jade.Application"));
                var state = app.GetType("Jade.Application.AppStatePlaying", true);
                var fresh = Exact(state, "StartNewCampaign", "200212814C11831908");
                var existing = Exact(state, "JoinExistingCampaign", "200412814C122D1184541512837D0112351281C5");
                playingRoom = Exact(state, "get_NetworkRoom", "20001281B1");
                healthRoomPointer = localRoomType.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                if (fresh.IsStatic || existing.IsStatic || playingRoom.IsStatic ||
                    !fresh.GetParameters()[0].ParameterType.IsEnum || fresh.GetParameters()[1].ParameterType != typeof(int) ||
                    playingRoom.ReturnType != networkRoom.ReturnType ||
                    healthRoomPointer?.PropertyType != typeof(IntPtr) || !healthRoomPointer.CanRead)
                    throw new InvalidOperationException("Unexpected fresh campaign or room metadata");
                ctx.Patches.Patch(existing, prefix: Hook(nameof(BeforeExistingCampaign)));
                ctx.Patches.Patch(fresh, postfix: Hook(nameof(AfterNewCampaign)));
                healthStartBound = true;
                health.SetFreshStartBinding("new and existing campaign hooks attached");
            }
            catch (Exception ex)
            {
                healthStartBound = false;
                health?.SetFreshStartBinding("unavailable: " + ex.Message);
                ctx.Log.LogWarning("Health fresh start detection: " + ex.Message);
            }
            // Exact metadata from the supplied interop report. Capture a live model
            // from the in-game HUD at turn start if the selection model was missed.
            try
            {
                var presenter = view.GetType("Jade.GameView.UI.HUD.InGameMenuPresenter", true);
                hudModel = Exact(presenter, "get_heroSelectModel", "2000129080");
                var turnStart = Exact(presenter, "OnTurnStart", "200001");
                if (hudModel.ReturnType != hero || hudModel.IsStatic || turnStart.IsStatic)
                    throw new InvalidOperationException("Unexpected HUD model signature");
                ctx.Patches.Patch(turnStart, postfix: Hook(nameof(CaptureFromHud)));
                hudHook = "turn start attached";
                try
                {
                    ctx.Patches.Patch(hudModel, postfix: Hook(nameof(CaptureHudGetter)));
                    hudHook = "turn start and HUD getter attached";
                }
                catch (Exception ex) { ctx.Log.LogWarning("Optional HUD getter capture: " + ex.Message); }
            }
            catch (Exception ex) { hudHook = "unavailable: " + ex.Message; ctx.Log.LogWarning("Optional HUD capture: " + ex.Message); }
            ctx.Patches.Patch(teardown, prefix: Hook(nameof(ClearModel)), postfix: Hook(nameof(ClearModel)));
            ctx.Patches.Patch(refill, prefix: Hook(nameof(BeforeRefill)), postfix: Hook(nameof(AfterRefill)));
            ctx.Patches.Patch(spend, prefix: Hook(nameof(BeforeSpend)), postfix: Hook(nameof(AfterSpend)));
            partialRefills = ctx.Config.Bind("HouseRules", "PartialAPRefills052", true,
                "Reset refund budget on a verified same-cap partial-to-full native refill. Full-to-full remains guarded. Restart to apply.").Value;
            RefreshApStatus();
            ctx.Log.LogInfo("BMF house rules: " + status);
        }
        catch (Exception ex)
        {
            running = null; model = null; faulted = true; status = "Skipped: " + ex.GetType().Name + ": " + ex.Message;
            try { ctx.Patches.UnpatchSelf(); } catch (Exception cleanup) { ctx.Log.LogWarning("House rules cleanup: " + cleanup.Message); }
            ctx.Log.LogWarning(status);
        }
        Write();
    }
    private void ConsumeUnverifiedHealthTransition()
    {
        if (Interlocked.Exchange(ref unverifiedHealthTransition, 0) != 0)
            health?.CancelForTransition("Campaign transition occurred off the main thread; queue again");
    }
    private static void BeforeExistingCampaign()
    {
        var r = running;
        if (r == null || !r.healthStartBound) return;
        if (Environment.CurrentManagedThreadId != r.mainThread)
        { Interlocked.Exchange(ref r.unverifiedHealthTransition, 1); return; }
        r.ConsumeUnverifiedHealthTransition();
        r.health.ExistingCampaignStarted();
    }
    private static void AfterNewCampaign(object __instance, int __1)
    {
        var r = running;
        if (r == null || !r.healthStartBound) return;
        if (Environment.CurrentManagedThreadId != r.mainThread)
        { Interlocked.Exchange(ref r.unverifiedHealthTransition, 1); return; }
        r.ConsumeUnverifiedHealthTransition();
        IntPtr key = IntPtr.Zero;
        // Verify native room type by IL2CPP checked cast, never by constructing
        // a wrapper from an arbitrary pointer. The game may still be loading;
        // the full gameplay/hero/room checks run again before health writes.
        if (r.health.Queued && __1 == 1)
        {
            try
            {
                key = r.HealthRoomIdentity(r.playingRoom.Invoke(__instance, null), false, out string detail);
                if (key == IntPtr.Zero) r.health.RejectRoom("new campaign: " + detail);
            }
            catch (Exception ex) { r.health.RejectRoom("new room read failed: " + ex.GetType().Name); }
        }
        r.health.NewCampaignStarted(__1, key);
    }
    private IntPtr HealthRoomIdentity(object room, bool duringPlay, out string detail)
    {
        detail = "missing native local room";
        if (room == null || roomTryCast == null || healthRoomPointer == null) return IntPtr.Zero;
        var nativeLocal = roomTryCast.Invoke(room, null);
        if (nativeLocal == null || !localRoomType.IsInstanceOfType(nativeLocal)) return IntPtr.Zero;
        var key = (IntPtr)healthRoomPointer.GetValue(nativeLocal);
        if (key == IntPtr.Zero) { detail = "zero room identity"; return IntPtr.Zero; }
        if (duringPlay)
        {
            int? count = ReadCount(roomPlayers, nativeLocal), locals = ReadCount(localPlayerCount, model);
            bool? ownsRoom = ReadFlag(roomHost, nativeLocal);
            if (count != 1 || locals != 1 || ownsRoom != true)
            {
                detail = $"roomPlayers={count?.ToString() ?? "?"} localPlayers={locals?.ToString() ?? "?"} roomHost={ownsRoom?.ToString() ?? "?"}";
                return IntPtr.Zero;
            }
        }
        detail = "verified local room";
        return key;
    }
    private void ObserveHealth(object piece, bool allowWrite)
    {
        ConsumeUnverifiedHealthTransition();
        if (health == null || !health.Available || !health.NeedsRoom || !healthStartBound) return;
        // Caller already verified local solo scope, including active/local hero.
        // Independently require the same native local room as the new campaign.
        checkingScope++;
        try
        {
            var m = model;
            if (m == null) { health.RejectRoom("no current model"); return; }
            var key = HealthRoomIdentity(networkRoom.Invoke(m, null), true, out string detail);
            if (key == IntPtr.Zero) { health.RejectRoom(detail); return; }
            health.Observe(piece, key, allowWrite);
        }
        catch (Exception ex) { health.RejectRoom("room read failed: " + ex.GetType().Name); }
        finally { checkingScope--; }
    }
    private IntPtr ModelKey(object candidate) => candidate == null ? IntPtr.Zero : (IntPtr)modelPointer.GetValue(candidate);
    private static void Capture(object __instance)
    {
        var r = running;
        if (r != null && Environment.CurrentManagedThreadId == r.mainThread && r.checkingScope == 0)
            r.CaptureModel(__instance, false);
    }
    private static void CaptureFromHud(object __instance)
    {
        var r = running;
        if (r == null || Environment.CurrentManagedThreadId != r.mainThread || r.checkingScope != 0) return;
        r.checkingScope++;
        try { r.CaptureModel(r.hudModel.Invoke(__instance, null), true); }
        catch (Exception ex) { r.CaptureError(ex); }
        finally { r.checkingScope--; }
    }
    private static void CaptureHudGetter(object __result)
    {
        var r = running;
        if (r != null && Environment.CurrentManagedThreadId == r.mainThread && r.checkingScope == 0)
            r.CaptureModel(__result, true);
    }
    private void CaptureModel(object candidate, bool fromHud)
    {
        if (candidate == null) return;
        try
        {
            IntPtr key = ModelKey(candidate);
            if (key == IntPtr.Zero) return;
            int handle = SceneManager.GetActiveScene().handle;
            modelCaptures++;
            if (fromHud) hudCaptures++;
            dirty = true;
            if (SessionModelGuard.PreferExistingHud(modelFromHud || (scopeModelKey != IntPtr.Zero && scopeModelKey == modelKey), capturedSceneHandle, handle, fromHud, modelKey, key))
            {
                ignoredGetterCaptures++;
                return;
            }
            // Keep the HUD or already verified scope preferred over unrelated UI models.
            // A getter on the same native object may refresh its wrapper without
            // losing HUD provenance. A new scene or identity must earn it again.
            bool sameCapture = key == modelKey && capturedSceneHandle == handle;
            if (scopeModelKey != IntPtr.Zero && scopeModelKey != key)
            {
                LoseScope("session model replaced");
                scopeModelKey = IntPtr.Zero;
            }
            modelFromHud = fromHud || (sameCapture && modelFromHud);
            model = candidate; modelKey = key; capturedSceneHandle = handle;
        }
        catch (Exception ex) { CaptureError(ex); }
    }
    private void CaptureError(Exception ex)
    {
        captureErrors++; dirty = true;
        if (captureErrors == 1) ctx.Log.LogWarning("Model capture skipped: " + ex.Message);
    }
    private void ClearCandidate()
    {
        model = null; modelKey = IntPtr.Zero; capturedSceneHandle = null; modelFromHud = false;
    }
    private static void ClearModel(object __instance)
    {
        var r = running;
        if (r == null || Environment.CurrentManagedThreadId != r.mainThread) return;
        try
        {
            IntPtr retired = r.ModelKey(__instance);
            bool matchCandidate = SessionModelGuard.MatchesTeardown(retired, r.modelKey);
            bool matchScope = SessionModelGuard.MatchesTeardown(retired, r.scopeModelKey);
            if (retired != IntPtr.Zero && !matchCandidate && !matchScope)
            { r.ignoredTeardowns++; r.dirty = true; return; }
            if (matchScope || r.scopeModelKey == IntPtr.Zero || retired == IntPtr.Zero)
            {
                r.LoseScope("session teardown");
                r.scopeModelKey = IntPtr.Zero;
                r.teardownSinceLocalScope = true;
            }
            if (matchCandidate || retired == IntPtr.Zero) r.ClearCandidate();
        }
        catch (Exception ex)
        {
            r.CaptureError(ex);
            r.LoseScope("unverified teardown");
            r.scopeModelKey = IntPtr.Zero;
            r.ClearCandidate();
        }
    }
    private void SyncScene(int handle, string name)
    {
        if (SessionModelGuard.IsSameScene(sceneHandle, scene, handle, name)) return;
        // Native callbacks can capture the new model before Plugin.Tick sees the
        // scene change. Retain only a capture tagged with the actual new scene.
        bool retain = model != null && SessionModelGuard.RetainCapture(capturedSceneHandle, handle);
        LoseScope("scene changed");
        scopeModelKey = IntPtr.Zero;
        if (retain) retainedCaptures++; else ClearCandidate();
        sceneHandle = handle; scene = name; dirty = true;
    }
    private void RecordScope(string reason)
    {
        lastScope = reason; dirty = true;
        if (reason == "local solo hero") return;
        lastRejectedFlags = lastFlags;
        scopeRejections.TryGetValue(reason, out int previous);
        scopeRejections[reason] = previous + 1;
    }
    private void LoseScope(string reason)
    {
        health?.ClearScope();
        int pending = heroes.Values.Count(h => h.PendingRestore);
        unresolved += pending;
        if (pending > 0) Sample($"Scope ended: {pending} outstanding cap(s) not restored: {reason}; discard test adventure");
        foreach (var h in heroes.Values) Sample($"Scope summary stat#{h.Id}: grants={h.Grants} restores={h.Restores} spent={h.Spent} bonusCycles={h.BonusCycles} refunds={h.Refunds} refundBudgetUsed={h.RefundsThisCycle}");
        heroes.Clear();
        dirty = true;
    }
    private bool Flag(MethodInfo method, object target, params object[] args) => method.Invoke(target, args) is bool b && b;
    private static bool? ReadFlag(MethodInfo method, object target)
    {
        try { return method.Invoke(target, null) is bool b ? b : null; }
        catch { return null; }
    }
    private static int? ReadCount(MethodInfo method, object target)
    {
        try { return method.Invoke(target, null) is int n ? n : null; }
        catch { return null; }
    }
    private bool CheckLocalRoom(object m, out string proof) => TryGetLocalRoom(m, out _, out proof);
    private bool TryGetLocalRoom(object m, out object verifiedRoom, out string proof)
    {
        verifiedRoom = null;
        proof = "not verified";
        if (!modelFromHud) { proof = "not HUD"; return false; }
        if (roomTryCast == null) { proof = "binding unavailable"; return false; }
        try
        {
            var room = networkRoom.Invoke(m, null);
            var nativeLocal = room == null ? null : roomTryCast.Invoke(room, null);
            if (nativeLocal == null || !localRoomType.IsInstanceOfType(nativeLocal))
            { proof = "not native local room"; return false; }
            bool? connected = ReadFlag(roomConnected, nativeLocal), isHost = ReadFlag(roomHost, nativeLocal);
            int? count = ReadCount(roomPlayers, nativeLocal), locals = ReadCount(localPlayerCount, m);
            proof = $"local room connected={connected?.ToString() ?? "?"} host={isHost?.ToString() ?? "?"} roomPlayers={count?.ToString() ?? "?"} localPlayers={locals?.ToString() ?? "?"}";
            if (!SessionModelGuard.LocalRoomProof(modelFromHud, true, connected, isHost, count, locals))
                return false;
            verifiedRoom = nativeLocal;
            return true;
        }
        catch (Exception ex) { proof = "room read failed: " + ex.GetType().Name; return false; }
    }
    private static void CaptureDebugCardRegistry(object __instance)
    {
        var r = running;
        if (r == null || r.faulted || Plugin.Instance?.DeveloperModeEnabled != true ||
            Environment.CurrentManagedThreadId != r.mainThread) return;
        try
        {
            var state = DebugWorkspace.ReadMember(__instance, "gameState") as Il2CppObjectBase;
            var registry = DebugWorkspace.ReadMember(__instance, "cardRegistry");
            if (state == null || state.Pointer == IntPtr.Zero || registry == null) return;
            if (r.debugCardRegistries.Count >= 8 && !r.debugCardRegistries.ContainsKey(state.Pointer))
                r.debugCardRegistries.Clear();
            r.debugCardRegistries[state.Pointer] = (state, registry);
        }
        catch (Exception ex)
        {
            if (!r.debugRegistryCaptureError)
                r.ctx.Log.LogWarning("Debug card registry capture failed: " + ex.GetBaseException().Message);
            r.debugRegistryCaptureError = true;
        }
    }

    private static void CaptureDebugSpawnContext(object __instance)
    {
        var r = running;
        if (r == null || r.faulted || Plugin.Instance?.DeveloperModeEnabled != true ||
            Environment.CurrentManagedThreadId != r.mainThread) return;
        try
        {
            var spawner = DebugWorkspace.ReadMember(__instance, "pieceSpawner");
            var state = DebugWorkspace.ReadMember(spawner, "gameState") as Il2CppObjectBase;
            if (state == null || state.Pointer == IntPtr.Zero) return;
            if (r.debugSpawnContexts.Count >= 8 && !r.debugSpawnContexts.ContainsKey(state.Pointer))
                r.debugSpawnContexts.Clear();
            r.debugSpawnContexts[state.Pointer] = (state, spawner);
        }
        catch (Exception ex)
        {
            if (!r.debugSpawnCaptureError)
                r.ctx.Log.LogWarning("Level editor spawn capture failed: " + ex.GetBaseException().Message);
            r.debugSpawnCaptureError = true;
        }
    }

    internal static bool TryGetLevelEditorContext(out LevelEditor.Context context, out string reason)
    {
        context = null;
        reason = "Start a local solo campaign with House rules and Developer mode enabled.";
        var r = running;
        if (r == null || r.faulted || Plugin.Instance?.DeveloperModeEnabled != true ||
            Plugin.Instance?.VerifiedGame != true || Environment.CurrentManagedThreadId != r.mainThread ||
            !r.modelFromHud || HouseRules.model == null) return false;
        try
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || !SessionModelGuard.RetainCapture(r.capturedSceneHandle, scene.handle))
                return false;
            var capturedModel = HouseRules.model;
            var key = r.modelKey;
            bool? s = ReadFlag(r.solo, capturedModel), l = ReadFlag(r.local, capturedModel), mp = ReadFlag(r.multi, capturedModel);
            bool? g = ReadFlag(r.inGame, capturedModel), p = ReadFlag(r.playing, capturedModel), h = ReadFlag(r.host, capturedModel);
            if (!r.TryGetLocalRoom(capturedModel, out var room, out reason)) return false;
            reason = SessionModelGuard.SessionRejection(s, l, mp, g, p, h, true);
            if (reason != null) return false;
            var state = DebugWorkspace.ReadMember(capturedModel, "gameState") as Il2CppObjectBase;
            var board = DebugWorkspace.ReadMember(state, "board") as Il2CppObjectBase;
            if (state == null || state.Pointer == IntPtr.Zero || board == null || board.Pointer == IntPtr.Zero ||
                board.GetType().FullName != "Jade.GameModel.Board" ||
                board.GetType().GetMethod("get_IsLoaded", Type.EmptyTypes)?.Invoke(board, null) is not bool loaded || !loaded)
            { reason = "Board not loaded."; return false; }
            var tileSet = DebugWorkspace.ReadMember(board, "tileSet") as Il2CppObjectBase;
            if (tileSet == null || tileSet.Pointer == IntPtr.Zero || room is not Il2CppObjectBase nativeRoom || nativeRoom.Pointer == IntPtr.Zero)
            { reason = "Tile set or native room identity unavailable."; return false; }
            // The current HUD model has its own live definition registry.
            // Do not require a constructor capture from a possibly distinct
            // simulation GameState to inspect or validate registered enemies.
            Il2CppObjectBase registry = null;
            try
            {
                var candidate = DebugWorkspace.ReadMember(capturedModel, "pieceRegistry") as Il2CppObjectBase;
                if (candidate != null && candidate.Pointer != IntPtr.Zero &&
                    candidate.GetType().FullName == "Jade.GameDefinitions.PieceRegistry") registry = candidate;
            }
            catch (Exception ex) { r.ctx.Log.LogWarning("Level editor HUD registry unavailable: " + ex.GetBaseException().Message); }
            object spawner = null;
            if (r.debugSpawnContexts.TryGetValue(state.Pointer, out var capture)) spawner = capture.Spawner;
            if (!ReferenceEquals(HouseRules.model, capturedModel) || !ReferenceEquals(running, r) ||
                key == IntPtr.Zero || r.modelKey != key || SceneManager.GetActiveScene().handle != scene.handle)
            { reason = "Session changed."; return false; }
            context = new LevelEditor.Context(scene.handle, key, state, board, tileSet, room, registry, spawner);
            reason = "Verified local solo board.";
            return true;
        }
        catch (Exception ex) { context = null; reason = ex.GetBaseException().Message; return false; }
    }

    // A debug command must resolve the current authoritative piece, not trust
    // a view object, a cached snapshot, or a session flag alone.
    internal static bool TryGetDebugRoomForPieceId(object pieceId, object cardId, UnityEngine.Object definition, out object room, out string reason)
    {
        room = null;
        reason = "Verified local session unavailable (House rules must be running).";
        var r = running;
        if (r == null || r.faulted || Plugin.Instance?.DeveloperModeEnabled != true || pieceId == null || cardId == null || definition == null ||
            Environment.CurrentManagedThreadId != r.mainThread || !r.modelFromHud || HouseRules.model == null ||
            !SessionModelGuard.RetainCapture(r.capturedSceneHandle, SceneManager.GetActiveScene().handle))
            return false;
        try
        {
            var capturedModel = HouseRules.model;
            var state = DebugWorkspace.ReadMember(capturedModel, "gameState") as Il2CppObjectBase;
            if (state == null || state.Pointer == IntPtr.Zero ||
                !r.debugCardRegistries.TryGetValue(state.Pointer, out var capture))
            { reason = "No matching game request registry captured yet."; return false; }
            var board = DebugWorkspace.ReadMember(state, "board");
            var gameModel = Assembly.Load(new AssemblyName("Jade.GameModel"));
            var boardType = gameModel.GetType("Jade.GameModel.Board", true);
            var idType = gameModel.GetType("Jade.GameModel.PieceId", true);
            var pieceType = gameModel.GetType("Jade.GameModel.Piece", true);
            if (!boardType.IsInstanceOfType(board) || !idType.IsInstanceOfType(pieceId))
                throw new InvalidOperationException("Board or piece ID type mismatch.");
            var helper = gameModel.GetType("Jade.GameModel.BoardHelpers", true);
            var getPiece = helper.GetMethod("GetPiece", BindingFlags.Public | BindingFlags.Static,
                null, new[] { boardType, idType }, null);
            if (getPiece == null || getPiece.ReturnType != pieceType)
                throw new InvalidOperationException("Authoritative piece lookup unavailable.");
            var piece = getPiece.Invoke(null, new[] { board, pieceId });
            if (piece == null) { reason = "Selected piece no longer exists."; return false; }
            reason = r.Scope(piece);
            if (reason != "local solo hero") return false;
            var lookup = capture.Registry.GetType().GetMethod("GetCardDefinition", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { cardId.GetType() }, null);
            if (lookup == null || lookup.ReturnType.FullName != "Jade.GameDefinitions.CardDefinition" ||
                lookup.Invoke(capture.Registry, new[] { cardId }) is not UnityEngine.Object registered ||
                registered == null || registered != definition)
            { reason = "Selected card is not registered in the current game."; return false; }
            if (!r.TryGetLocalRoom(capturedModel, out room, out reason)) return false;
            if (!ReferenceEquals(HouseRules.model, capturedModel) || !ReferenceEquals(running, r))
            { room = null; reason = "Session model changed."; return false; }
            return true;
        }
        catch (Exception ex) { room = null; reason = ex.GetBaseException().Message; return false; }
    }
    private int Allowance(object piece) => (int)allowance.Invoke(piece, null);
    private int Remaining(object piece) => (int)remaining.Invoke(null, new[] { piece });
    private string Scope(object piece)
    {
        var m = model;
        lastFlags = "source=" + (modelFromHud ? "HUD" : "getter") + "; not evaluated";
        if (m == null) return "no session model";
        try
        {
            if (!SessionModelGuard.RetainCapture(capturedSceneHandle, SceneManager.GetActiveScene().handle))
                return "model from different scene";
        }
        catch { return "scene not verified"; }
        checkingScope++;
        try
        {
            bool? s = ReadFlag(solo, m), l = ReadFlag(local, m), mp = ReadFlag(multi, m);
            bool? g = ReadFlag(inGame, m), p = ReadFlag(playing, m), h = ReadFlag(host, m);
            string room = "not needed";
            bool roomProof = false;
            if (s == false && l == true && mp == false && g == true && p == true && h == true)
                roomProof = CheckLocalRoom(m, out room);
            lastFlags = $"source={(modelFromHud ? "HUD" : "getter")} solo={s?.ToString() ?? "?"} local={l?.ToString() ?? "?"} multi={mp?.ToString() ?? "?"} inGame={g?.ToString() ?? "?"} playing={p?.ToString() ?? "?"} host={h?.ToString() ?? "?"} room={room} roomProof={roomProof}";
            string rejection = SessionModelGuard.SessionRejection(s, l, mp, g, p, h, roomProof);
            if (rejection != null) return rejection;
            var id = sheet.Invoke(piece, null);
            if (id == null) return "missing character sheet";
            if (!Flag(activeHero, m, id)) return "not active hero";
            if (!Flag(localHero, m, id)) return "not local hero";
            return "local solo hero";
        }
        finally { checkingScope--; }
    }
    private string Snapshot(object piece)
    {
        var facade = stats.Invoke(piece, null);
        if (facade == null) throw new InvalidOperationException("No stats facade");
        var args = new[] { apType };
        var obj = getObject.Invoke(facade, args);
        if (obj == null) throw new InvalidOperationException("No AP stat object");
        return $"allowance={Allowance(piece)} remaining={Remaining(piece)} stat={getStat.Invoke(facade, args)} statMax={getMax.Invoke(facade, args)} objectValue={getValue.Invoke(obj, null)} objectMax={getObjectMax.Invoke(obj, null)} permanentMax={getPermanentMax.Invoke(obj, null)}";
    }
    private HeroState Identify(object piece)
    {
        if (pointer == null || statPointer == null || (IntPtr)pointer.GetValue(piece) == IntPtr.Zero) return null;
        var f = stats.Invoke(piece, null);
        var obj = getObject.Invoke(f, new[] { apType });
        if (obj == null) return null;
        var key = (IntPtr)statPointer.GetValue(obj);
        if (key == IntPtr.Zero) return null;
        if (heroes.TryGetValue(key, out var h)) return h;
        if (heroes.Count >= 128) return null;
        h = new HeroState { Id = ++nextId };
        heroes.Add(key, h);
        return h;
    }
    private sealed class TraceState
    {
        internal HouseRules Owner;
        internal object Piece;
        internal HeroState Hero;
        internal string Before, Operation;
        internal int SpendAmount, BeforeRemaining, BeforeMax;
        internal bool VerifiedBefore;
        internal int SceneHandle;
        internal IntPtr ModelKey;
    }
    private static TraceState Begin(object piece, string operation, bool isSpend)
    {
        var r = running;
        if (r == null || r.faulted || piece == null) return null;
        if (Environment.CurrentManagedThreadId != r.mainThread) { Interlocked.Increment(ref r.otherThread); return null; }
        // Native stat setters may invoke refill again; never grant or restore from such reentry.
        if (r.writing != 0) { r.nestedSkipped++; r.dirty = true; return null; }
        if (isSpend) r.spendCalls++; else r.refillCalls++;
        r.dirty = true;
        try
        {
            // Synchronize before processing AP, not a frame later in the scene poll.
            var currentScene = SceneManager.GetActiveScene();
            r.SyncScene(currentScene.handle, currentScene.name ?? "");
            string scope = r.Scope(piece);
            r.RecordScope(scope);
            if (scope != "local solo hero") { r.skipped++; return null; }
            r.scopeModelKey = r.modelKey;
            r.localScopeSeen = true;
            r.teardownSinceLocalScope = false;
            // Health has its own fault handling and never changes the AP budget.
            if (r.health != null && r.health.Available)
            {
                r.writing++;
                try { r.ObserveHealth(piece, allowWrite: false); } finally { r.writing--; }
            }
            var h = r.Identify(piece);
            if (!isSpend && h != null && h.PendingRestore && r.Remaining(piece) <= 2)
                r.RestoreMaximum(piece, h, r.Remaining(piece));
            int beforeRemaining = r.Remaining(piece);
            int beforeMax = r.StatMax(piece);
            return new TraceState { Owner = r, Piece = piece, Hero = h, Operation = operation, Before = r.Snapshot(piece), BeforeRemaining = beforeRemaining, BeforeMax = beforeMax, SceneHandle = currentScene.handle, ModelKey = r.modelKey,
                VerifiedBefore = r.refundMode && ApRefundBudget.CanObserveCycle(beforeMax, beforeRemaining) && r.ValuesMatch(piece, beforeRemaining, beforeMax) };
        }
        catch (Exception ex) { r.Fail("before " + operation, ex); return null; }
    }
    private static bool End(TraceState state, string result)
    {
        if (state == null) return false;
        var r = state.Owner;
        if (r.faulted || !ReferenceEquals(running, r)) return false;
        try
        {
            if (state.ModelKey != r.modelKey || state.SceneHandle != SceneManager.GetActiveScene().handle)
            {
                r.lastFlags = "not evaluated: model or scene changed during AP operation";
                r.RecordScope("model or scene changed during AP operation");
                r.skipped++;
                return false;
            }
            r.Sample($"scope=local solo hero | stat#{state.Hero?.Id ?? 0} | {state.Operation} | {result} | before {state.Before} | after {r.Snapshot(state.Piece)}");
            r.samplesTaken++;
        }
        catch (Exception ex) { r.Fail("after " + state.Operation, ex); }
        // Apply after a successful original AP operation: the game may clear modifiers
        // during the original call. Health failure remains independent of AP.
        if (!r.faulted && r.health != null && r.health.Available && result != "result=False")
        {
            try
            {
                if (r.Scope(state.Piece) == "local solo hero")
                {
                    r.writing++;
                    try { r.ObserveHealth(state.Piece, allowWrite: true); } finally { r.writing--; }
                }
            }
            catch (Exception ex) { r.ctx.Log.LogWarning("Health observation skipped: " + ex.Message); }
        }
        return !r.faulted;
    }
    private static void BeforeRefill(object __0, out TraceState __state) => __state = Begin(__0, "refill", false);
    private static void AfterRefill(TraceState __state)
    {
        if (!End(__state, "returned")) return;
        var r = __state.Owner;
        if (r.faulted || __state.Hero == null) return;
        try
        {
            var h = __state.Hero;
            if (r.refundMode)
            {
                if (r.Scope(__state.Piece) != "local solo hero" || !ReferenceEquals(r.Identify(__state.Piece), h)) return;
                // Reset on a verified actual same-cap increase to full, never a full-to-full call.
                // A refund itself is never a reset; nested hooks are suppressed.
                int refillCap = __state.BeforeMax;
                if (ApRefundBudget.CanReset(__state.VerifiedBefore, __state.BeforeRemaining, refillCap, r.ValuesMatch(__state.Piece, refillCap, refillCap), r.partialRefills))
                {
                    h.RefundsThisCycle = 0; h.RefundCycleCap = refillCap; r.refundResets++;
                    if (__state.BeforeRemaining > 0) r.partialRefundResets++;
                    r.Sample($"Refund budget reset stat#{h.Id} before={__state.BeforeRemaining} cap={refillCap} budget={ApRefundBudget.Limit(r.bonus, refillCap, r.adjustExisting)}");
                }
                return;
            }
            if (r.bonus == 0) return;
            if (r.Remaining(__state.Piece) > __state.BeforeRemaining) h.SpentSinceGrant = 0;
            if (h.PendingRestore || (h.Grants > 0 && __state.BeforeRemaining >= 2)) { r.duplicateRefills++; return; }
            int cap = r.StatMax(__state.Piece);
            if (cap == 2) r.TryGrant(__state.Piece, h);
            else if (cap >= 3 && cap <= 5 && r.ValuesMatch(__state.Piece, cap, cap))
            {
                if (r.adjustExisting) r.TryGrant(__state.Piece, h, cap, false, cap);
                else { r.elevatedSkipped++; r.Sample($"Skip stat#{h.Id}: existing AP cap={cap}; enable Adjust existing caps only for disposable test saves"); }
            }
        }
        catch (Exception ex) { r.Fail("AP grant", ex); }
    }
    private static void BeforeSpend(int __0, object __1, out TraceState __state)
    { __state = Begin(__1, "spend amount=" + __0, true); if (__state != null) __state.SpendAmount = __0; }
    private static void AfterSpend(bool __result, TraceState __state)
    {
        if (!End(__state, "result=" + __result)) return;
        var r = __state.Owner;
        var h = __state.Hero;
        if (r.faulted || h == null || !__result || __state.SpendAmount <= 0) return;
        try
        {
            if (r.Scope(__state.Piece) != "local solo hero" || !ReferenceEquals(r.Identify(__state.Piece), h)) return;
            int current = r.Remaining(__state.Piece);
            // Count only a real matching deduction, not merely a true return value.
            if (__state.BeforeRemaining - current != __state.SpendAmount) return;
            int cap = r.StatMax(__state.Piece);
            if (r.refundMode)
            {
                h.Spent += __state.SpendAmount;
                if (r.bonus == 0) return;
                if (!ApRefundBudget.EligibleCap(cap, r.adjustExisting)) { r.refundElevatedSkipped++; return; }
                if (__state.BeforeMax != cap || (h.RefundCycleCap != 0 && h.RefundCycleCap != cap)) return;
                if (ApRefundBudget.CanRefund(r.bonus, h.RefundsThisCycle, cap, r.adjustExisting, __state.VerifiedBefore, __state.BeforeRemaining, __state.SpendAmount, current, r.ValuesMatch(__state.Piece, cap - 1, cap)))
                    r.TryRefund(__state.Piece, h, cap);
                return;
            }
            bool opening = r.bonus > 0 && h.Spent == 0 && h.Grants == 0 && !h.PendingRestore &&
                __state.SpendAmount == 1 && __state.BeforeRemaining == cap && current == cap - 1 &&
                (cap == 2 || (r.adjustExisting && cap >= 3 && cap <= 5));
            h.Spent += __state.SpendAmount;
            if (opening && r.ValuesMatch(__state.Piece, current, cap))
            {
                r.TryGrant(__state.Piece, h, current, true, cap);
                return;
            }
            if (h.Grants > 0)
            {
                h.SpentSinceGrant += __state.SpendAmount;
                if (current == 0 && h.SpentSinceGrant == 2 + r.bonus) h.BonusCycles++;
            }
            if (h.PendingRestore && current <= 2) r.RestoreMaximum(__state.Piece, h, current);
        }
        catch (Exception ex) { r.Fail("AP spend observation", ex); }
    }
    private void TryRefund(object piece, HeroState h, int cap)
    {
        if (!CanConfigure || !refundMode || bonus == 0 || Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h) || !ApRefundBudget.EligibleCap(cap, adjustExisting) || !ValuesMatch(piece, cap - 1, cap)) return;
        int limit = ApRefundBudget.Limit(bonus, cap, adjustExisting);
        if (h.RefundsThisCycle >= limit || (h.RefundCycleCap != 0 && h.RefundCycleCap != cap)) return;
        h.RefundCycleCap = cap;
        // Reserve the refund before writing; a failed or ambiguous write never retries.
        h.RefundsThisCycle++;
        writing++;
        try
        {
            var facade = stats.Invoke(piece, null);
            bool applied = Set(setStat, facade, (float)cap);
            bool verified = ValuesMatch(piece, cap, cap);
            Sample($"Refund stat#{h.Id} cap={cap} targetActions={2 + bonus} used={h.RefundsThisCycle}/{limit} applied={applied} verified={verified} | {Snapshot(piece)}");
            if (!applied || !verified) throw new InvalidOperationException("AP refund not verified; no further writes");
            h.Refunds++; refunds++;
        }
        finally { writing--; }
    }
    private int StatMax(object piece) => (int)getMax.Invoke(stats.Invoke(piece, null), new[] { apType });
    private bool ValuesMatch(object piece, int value, int max)
    {
        var f = stats.Invoke(piece, null); var args = new[] { apType }; var o = getObject.Invoke(f, args);
        return Allowance(piece) == 2 && Remaining(piece) == value && (int)getStat.Invoke(f, args) == value && (int)getMax.Invoke(f, args) == max &&
            (float)getValue.Invoke(o, null) == value && (float)getObjectMax.Invoke(o, null) == max && (float)getPermanentMax.Invoke(o, null) == max;
    }
    private bool Set(MethodInfo method, object facade, float value) => method.Invoke(facade, new object[] { apType, value }) is bool b && b;
    private void TryGrant(object piece, HeroState h, int baseline = 2, bool opening = false, int baselineMax = 2)
    {
        if (!CanConfigure || bonus == 0 || Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h) || !ValuesMatch(piece, baseline, baselineMax)) return;
        bool existing = baselineMax != 2;
        if (existing && (!adjustExisting || baselineMax < 3 || baselineMax > 5)) return;
        if (baseline != baselineMax - (opening ? 1 : 0)) return;
        var facade = stats.Invoke(piece, null);
        // Absolute target from the normal allowance, not the possibly persisted cap.
        int target = 2 + bonus - (opening ? 1 : 0);
        int targetMax = Math.Max(2, target);
        string source = (opening ? "opening-spend" : "refill") + (existing ? "-existing-cap" : "");
        Sample($"Grant stat#{h.Id} source={source} target={target}/{targetMax} baseline | {Snapshot(piece)}");
        h.PendingRestore = true;
        writing++;
        try
        {
            // Raise max before raising value; lower value before lowering max.
            bool applied = ApplyValues(piece, facade, target, targetMax);
            Sample($"Grant stat#{h.Id} applied={applied} | {Snapshot(piece)}");
            if (!applied || !ValuesMatch(piece, target, targetMax)) throw new InvalidOperationException("Bonus AP not retained");
            h.PendingRestore = targetMax > 2;
            h.Grants++; h.SpentSinceGrant = opening ? 1 : 0; grants++;
            if (existing) adjustedCaps++;
            if (opening) openingGrants++;
            dirty = true;
        }
        catch (Exception ex)
        {
            Sample("Grant failed: " + ex.GetType().Name + ": " + ex.Message);
            try
            {
                // On failure restore exactly the pre-write state, not an assumed normal cap.
                if (Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h))
                    throw new InvalidOperationException("Cannot verify AP object for rollback");
                bool rollback = ApplyValues(piece, facade, baseline, baselineMax) && ValuesMatch(piece, baseline, baselineMax);
                Sample($"Grant rollback stat#{h.Id} verified={rollback} | {Snapshot(piece)}");
                if (rollback) h.PendingRestore = false;
            }
            finally { faulted = true; errors++; status = "Bonus stopped: grant failed; discard test adventure"; dirty = true; }
        }
        finally { writing--; }
    }
    private bool ApplyValues(object piece, object facade, int targetValue, int targetMax)
    {
        int currentMax = StatMax(piece);
        if (targetMax > currentMax && !Set(setMax, facade, targetMax)) return false;
        if (Remaining(piece) != targetValue && !Set(setStat, facade, targetValue)) return false;
        if (targetMax < currentMax && !Set(setMax, facade, targetMax)) return false;
        return ValuesMatch(piece, targetValue, targetMax);
    }
    private void RestoreMaximum(object piece, HeroState h, int targetValue)
    {
        if (!h.PendingRestore) return;
        if (Scope(piece) != "local solo hero" || !ReferenceEquals(Identify(piece), h)) throw new InvalidOperationException("Cannot verify AP stat for restoration");
        if (targetValue < 0 || targetValue > 2) throw new InvalidOperationException("Unexpected restoration value");
        var facade = stats.Invoke(piece, null);
        bool currentOk = true, maxOk = false;
        writing++;
        try
        {
            if (Remaining(piece) != targetValue) { try { currentOk = Set(setStat, facade, targetValue); } catch (Exception ex) { currentOk = false; Sample("Current rollback exception: " + ex.GetType().Name); } }
            try { maxOk = Set(setMax, facade, 2f); } catch (Exception ex) { Sample("Max rollback exception: " + ex.GetType().Name); }
            bool restored = ValuesMatch(piece, targetValue, 2);
            Sample($"Restore stat#{h.Id} current={currentOk} max={maxOk} verified={restored} | {Snapshot(piece)}");
            if (!restored) throw new InvalidOperationException("Rollback not verified; discard test adventure");
            h.PendingRestore = false; h.Restores++; restores++; dirty = true;
        }
        finally { writing--; }
    }
    private void Sample(string line) { if (samples.Count < 250) samples.Add(line); dirty = true; }
    private void Fail(string stage, Exception ex)
    { faulted = true; errors++; status = "Tracing stopped: " + stage; Sample(status + " | " + ex.GetType().Name); ctx.Log.LogWarning(status + ": " + ex.Message); }
    public override void OnSceneChanged(SceneInfo info) => SyncScene(info.Handle, info.Name);
    public override void OnUpdate()
    {
        if (Plugin.Instance?.DeveloperModeEnabled != true) { debugCardRegistries.Clear(); debugSpawnContexts.Clear(); }
        ConsumeUnverifiedHealthTransition();
        if (requestedRefundMode != refundMode && (DateTime.UtcNow - lastModeCheck).TotalSeconds >= 1)
            TryApplyModeChange();
        if (dirty && (DateTime.UtcNow - lastWrite).TotalSeconds >= 5) Write();
        health?.Tick();
    }
    private void Write()
    {
        if (ctx == null) return;
        try
        {
            var lines = new List<string> { "BMF AP bonus probe (live settings; no new bonuses when bonus=0)", "Framework=" + Framework.Version, "GameVersion=" + ctx.GameVersion, "GameAssemblySHA256=" + ctx.GameAssemblySha256, "Scene=" + scene, "UTC=" + DateTime.UtcNow.ToString("O"), "Status=" + status, $"ConfiguredBonus={bonus} NextBonus={nextBonus?.Value ?? 0} RefundMode={refundMode} NextRefundMode={requestedRefundMode} ModePending={requestedRefundMode != refundMode} Refunds={refunds} RefundResets={refundResets} PartialRefundResets={partialRefundResets} PartialRefills={partialRefills} RefundElevatedSkipped={refundElevatedSkipped} Grants={grants} OpeningGrants={openingGrants} AdjustExisting={adjustExisting} NextAdjustExisting={nextAdjustExisting?.Value ?? false} AdjustedCaps={adjustedCaps} ElevatedSkipped={elevatedSkipped} Restores={restores} Pending={heroes.Values.Count(h => h.PendingRestore)} Unresolved={unresolved} DuplicateRefills={duplicateRefills} NestedSkipped={nestedSkipped}", $"Refills={refillCalls} Spends={spendCalls} Samples={samplesTaken} Skipped={skipped} Errors={errors} OtherThread={Interlocked.Read(ref otherThread)}", "Current scope anonymous native AP stat objects (not necessarily distinct heroes):" };
            lines.Add($"SessionCapture: ModelCaptures={modelCaptures} HudCaptures={hudCaptures} RetainedCaptures={retainedCaptures} IgnoredTeardowns={ignoredTeardowns} CaptureErrors={captureErrors} LastScope={lastScope}");
            lines.Add("HUD capture hook: " + hudHook);
            lines.Add($"HUD priority: selected={modelFromHud} ignoredGetterCaptures={ignoredGetterCaptures}");
            lines.Add("Local room proof binding: " + roomHook);
            lines.Add("Latest session flags: " + lastFlags);
            lines.Add("Last rejected session flags: " + lastRejectedFlags);
            foreach (var reason in scopeRejections.OrderBy(x => x.Key)) lines.Add($"Scope rejected: {reason.Key} count={reason.Value}");
            foreach (var h in heroes.Values) lines.Add($"stat#{h.Id} grants={h.Grants} restores={h.Restores} spent={h.Spent} bonusCycles={h.BonusCycles} refunds={h.Refunds} refundBudgetUsed={h.RefundsThisCycle} refundCycleCap={h.RefundCycleCap} pending={h.PendingRestore}");
            lines.Add("Samples (max 250; no player identifiers or raw pointers):");
            lines.AddRange(samples); File.WriteAllLines(Path.Combine(ctx.DataDirectory, "house-rules-probe.txt"), lines); dirty = false; lastWrite = DateTime.UtcNow;
        }
        catch (Exception ex) { ctx.Log.LogWarning("House rules report: " + ex.Message); }
    }
    public override void OnStop() { debugCardRegistries.Clear(); debugSpawnContexts.Clear(); health?.CancelForTransition("Module stopped"); LoseScope("module stopped"); if (ReferenceEquals(running, this)) { running = null; model = null; } Write(); health?.Write(); }
}
