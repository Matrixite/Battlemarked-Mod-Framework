using System.Reflection;
using BepInEx.Configuration;

namespace Battlemarked.ModFramework;

// Explicitly armed, bounded party health trial. Caller verifies build, main thread and local solo hero.
// No repeated writes, no temporary modifiers, no getter patches. May persist in a disposable save.
internal sealed class HeroHealthRule
{
    private readonly ModuleContext ctx;
    private readonly ConfigEntry<int> nextPercent;
    private string activationStatus = "Queue for next new solo game";
    private string freshStartBinding = "not attempted", lastRoomRejection = "none";
    private int percent;
    private bool writeHalted, ready, faulted, dirty = true;
    private readonly FreshHealthAuthorization authorization = new();
    private MethodInfo usesHealth, isDowned, sheet, sheetId, stats, getObject, getStat, getMax, value, max, permanent, raw, modifiers, setMax, setValue;
    private PropertyInfo pointer, count, modifierItem, modifierPointer;
    private MethodInfo modifierStatType, modifierKind, modifierPermanency, modifierAmount, statSetMax, restoreRaw;
    private object healthType, additiveKind, permanentKind;
    private bool additiveBinding;
    private string additiveStatus = "not attempted";
    private NativeHealthPartyTrial party = new();
    private readonly HealthSafetyLedger ledger;
    private int previousTrials, previousApplied, previousVerified, previousLost;
    private int errors, skipped, missingIdentity, rejectedReservations, armRejections, freshStarts, existingStarts, claims, roomRejections;
    private DateTime lastWrite;
    private readonly List<string> samples = new();
    private readonly Dictionary<string, int> candidateRejections = new(StringComparer.Ordinal);
    internal int NextPercent => HealthBudget.Normalize(nextPercent.Value);
    internal bool Armed => authorization.Armed;
    internal bool Queued => authorization.Queued;
    internal bool NeedsRoom => authorization.HasRoom;
    internal bool Available => ready;
    private int Applied => party.Entries.Count(e => e.Applied);
    private int Verified => party.Entries.Sum(e => e.Verified);
    private int Lost => party.Entries.Sum(e => e.Lost);
    internal string Status => !ready ? "Native health metadata unavailable" : faulted ? "Health trial stopped; see report" :
        !ledger.Ready ? "Health baseline ledger blocked; see report" :
        writeHalted ? $"Further writes stopped: {Applied} applied" :
        Armed ? $"Armed: {percent}% | {Applied}/{NativeHealthPartyTrial.Maximum} heroes applied" :
        Queued ? $"Queued: {percent}% for next new solo game" :
        party.Ended ? $"Trial ended: {Applied} applied | {activationStatus}" :
        $"Read-only; {activationStatus}";
    internal string SafetySummary => ledger.Summary + $" | prior trials={previousTrials}, applied={previousApplied}, verified={previousVerified}, lost={previousLost}";
    internal string Counts => $"Verified: {Verified} | Lost: {Lost} | Errors: {errors}";
    internal void ChangePercent(int delta) => SetPercent(Math.Clamp(NextPercent + delta, 100, 200));
    internal void SetPercent(int selected)
    {
        if (!Available) return;
        selected = HealthBudget.Normalize(selected);
        if (selected == NextPercent) return;
        // A changed percentage cannot silently change an already authorized target.
        if (Armed || Queued) Disarm("Target changed; queue again");
        try
        {
            nextPercent.Value = selected;
            ctx.Config.Save();
        }
        catch (Exception ex)
        {
            errors++;
            SetActivation("Could not save health preference");
            ctx.Log.LogWarning("BMF health preference: " + ex.Message);
        }
        Write();
    }
    internal void SetFreshStartBinding(string detail)
    {
        freshStartBinding = detail;
        dirty = true;
        Write();
    }

    internal void QueueForFreshCampaign()
    {
        if (!Available || faulted || writeHalted || !ledger.Ready)
        { RejectArm("Health trial unavailable"); return; }
        if (!party.CanQueueNext(NextPercent, authorization.HasRoom))
        {
            RejectArm(NextPercent == 100 ? "Select more than 100%" :
                "Leave the current trial before queueing another new campaign");
            return;
        }
        percent = NextPercent;
        authorization.Queue();
        SetActivation($"Queued {percent}% for next new solo game");
        Write();
    }

    // Only the exact new-campaign callback can claim a queue. No AP activity,
    // UI click, or loaded adventure can independently grant health authority.
    internal void NewCampaignStarted(int localPlayers, IntPtr verifiedRoom)
    {
        freshStarts++;
        bool queued = Queued;
        if (!queued)
        {
            if (authorization.HasRoom) CancelForTransition("New game without a queued trial");
            dirty = true;
            return;
        }
        if (!Available || faulted || writeHalted || !ledger.Ready || !party.CanQueueNext(percent, authorization.HasRoom))
        {
            CancelForTransition("Queued trial no longer available");
            return;
        }
        if (!authorization.ClaimFreshStart(localPlayers, verifiedRoom))
        {
            RejectArm(localPlayers != 1 ? "New game was not one local player" :
                "New game room could not be verified; queue again before another new game");
            return;
        }
        // Reset reservations only after a new, verified local room claims the request.
        // Persistent baseline checks remain in force across all campaigns and launches.
        if (party.Entries.Count != 0)
        {
            previousTrials++;
            previousApplied += Applied;
            previousVerified += Verified;
            previousLost += Lost;
            samples.Clear();
            candidateRejections.Clear();
        }
        party = new NativeHealthPartyTrial();
        claims++;
        SetActivation($"Fresh solo room claimed at {percent}%");
        Write();
    }

    internal void ExistingCampaignStarted()
    {
        existingStarts++;
        CancelForTransition("Existing campaign; health trial not authorized");
    }

    internal void CancelForTransition(string reason)
    {
        bool hadRequest = Queued || authorization.HasRoom;
        authorization.Cancel();
        bool ended = party.EndScope();
        if (hadRequest || ended)
        {
            SetActivation(reason);
            Write();
        }
        dirty = true;
    }

    internal void RejectRoom(string reason)
    {
        roomRejections++;
        if (lastRoomRejection != reason) Sample("Health room rejected: " + reason);
        lastRoomRejection = reason;
        dirty = true;
    }

    private void RejectCandidate(string reason, string readings)
    {
        skipped++;
        candidateRejections.TryGetValue(reason, out int previous);
        candidateRejections[reason] = previous + 1;
        if (previous == 0) Sample($"Health candidate skipped: {reason} | {readings}");
        dirty = true;
    }

    internal void RejectArm(string reason)
    {
        armRejections++;
        SetActivation(reason);
        Write();
    }
    internal void Disarm(string reason = "Disarmed")
    {
        authorization.DisableWrites();
        SetActivation(reason);
        Write();
    }
    private void SetActivation(string detail)
    {
        activationStatus = detail;
        Sample(detail);
        ctx.Log.LogInfo("BMF native health activation: " + detail);
    }
    internal void ClearScope()
    {
        // A pending queue or freshly claimed room may cross the initial scene
        // transition. Once matching gameplay was observed, never carry it over.
        if (party.EndScope() || authorization.Observed)
        {
            authorization.Cancel();
            SetActivation("Scope ended; queue another fresh game");
        }
        dirty = true;
    }
    private static MethodInfo Exact(Type type, string name, string signature)
    {
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters &&
                Convert.ToHexString(m.Module.ResolveSignature(m.MetadataToken)) == signature).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Exact health method unavailable: " + type.FullName + "." + name);
        return matches[0];
    }
    internal HeroHealthRule(ModuleContext context, Type piece, Type facade, Type stat, Type enumType)
    {
        ctx = context;
        ledger = new HealthSafetyLedger(Path.Combine(ctx.DataDirectory, "health-baselines-v060.txt"));
        nextPercent = ctx.Config.Bind("HouseRules", "HeroMaxHealth050", 100,
            "Experimental max health target 100/125/150/175/200%. Native maximum trial requires an explicit queue before starting a new solo campaign. Fresh disposable solo only.");
        // Retire the old boolean. It is not an authorization in this build.
        var legacyArm = ctx.Config.Bind("HouseRules", "ArmNativeHealth054", false,
            "Retired v0.5.4 setting; ignored. Queue using the House rules menu.");
        legacyArm.Value = false;
        ctx.Config.Save();
        percent = NextPercent;
        // Never consume any previous launch's flag or saved marker.
        authorization.Cancel();
        SetActivation("Queue for next new solo game");
        try
        {
            if (!enumType.IsEnum || enumType.FullName != "Jade.GameModel.Stats+Type" || !Enum.IsDefined(enumType, "Health"))
                throw new InvalidOperationException("Missing health enum");
            healthType = Enum.Parse(enumType, "Health");
            if (Convert.ToInt32(healthType) != 2) throw new InvalidOperationException("Unexpected Health enum value");
            usesHealth = Exact(piece, "get_usesHealth", "200002");
            isDowned = Exact(piece, "get_IsDowned", "200002");
            sheet = Exact(piece, "get_characterSheetId", "2000128490");
            if (sheet.ReturnType.Name != "CharacterSheetId" || sheet.ReturnType.Assembly != piece.Assembly)
                throw new InvalidOperationException("Unexpected character sheet identity type");
            sheetId = Exact(sheet.ReturnType, "get_Id", "20000E");
            if (sheetId.IsStatic || sheetId.ReturnType != typeof(string))
                throw new InvalidOperationException("Unexpected character sheet ID getter");
            stats = Exact(piece, "get_stats", "2000128310");
            getObject = Exact(facade, "GetStatObject", "20011283041185C0");
            getStat = Exact(facade, "GetStat", "2001081185C0");
            getMax = Exact(facade, "GetStatMax", "2001081185C0");
            value = Exact(stat, "get_Value", "20000C");
            max = Exact(stat, "get_Max", "20000C");
            permanent = Exact(stat, "get_PermanentMax", "20000C");
            raw = Exact(stat, "get_maxValue", "20000C");
            modifiers = Exact(stat, "get_modifiers", "2000151221011282F8");
            setMax = Exact(facade, "TrySetStatMax", "2002021185C00C");
            setValue = Exact(facade, "TrySetStat", "2002021185C00C");
            foreach (var method in new[] { setMax, setValue })
            {
                var args = method.GetParameters();
                if (method.IsStatic || method.ReturnType != typeof(bool) || args.Length != 2 ||
                    args[0].ParameterType != enumType || args[1].ParameterType != typeof(float))
                    throw new InvalidOperationException("Unexpected health setter signature");
            }
            pointer = stat.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            count = modifiers.ReturnType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (pointer == null || pointer.PropertyType != typeof(IntPtr) || !pointer.CanRead ||
                count == null || count.PropertyType != typeof(int) || !count.CanRead)
                throw new InvalidOperationException("Native health identity or modifier count unavailable");
            // Optional, exact bindings from the supplied Jade.GameModel.dll.
            // Unmodified heroes retain the original verified facade setter path.
            try
            {
                var args = modifiers.ReturnType.GetGenericArguments();
                if (args.Length != 1 || args[0].FullName != "Jade.GameModel.StatModifier" ||
                    args[0].Assembly != stat.Assembly)
                    throw new InvalidOperationException("Unexpected health modifier element type");
                var modifier = args[0];
                modifierStatType = Exact(modifier, "get_StatType", "20001185C0");
                modifierAmount = Exact(modifier, "get_Modifier", "20000C");
                modifierKind = Exact(modifier, "get_ModifierType", "20001185AC");
                modifierPermanency = Exact(modifier, "get_ModifierPermanency", "20001185B0");
                if (modifierStatType.ReturnType != enumType || modifierAmount.ReturnType != typeof(float) ||
                    !modifierKind.ReturnType.IsEnum || modifierKind.ReturnType.Name != "StatModifierType" ||
                    !modifierPermanency.ReturnType.IsEnum || modifierPermanency.ReturnType.Name != "StatModifierPermanency")
                    throw new InvalidOperationException("Unexpected modifier enum/amount type");
                additiveKind = Enum.Parse(modifierKind.ReturnType, "AdditionAndSubtraction");
                permanentKind = Enum.Parse(modifierPermanency.ReturnType, "Permanent");
                if (Convert.ToInt32(additiveKind) != 0 || Convert.ToInt32(permanentKind) != 1)
                    throw new InvalidOperationException("Unexpected additive or permanent enum value");
                modifierItem = modifiers.ReturnType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .SingleOrDefault(p => p.Name == "Item" && p.CanRead && p.PropertyType == modifier &&
                        p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(int));
                modifierPointer = modifier.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                statSetMax = Exact(stat, "SetMaxValue", "2001010C");
                restoreRaw = Exact(stat, "set_maxValue", "2001010C");
                if (modifierItem == null || modifierPointer?.PropertyType != typeof(IntPtr) || !modifierPointer.CanRead ||
                    statSetMax.IsStatic || restoreRaw.IsStatic || statSetMax.ReturnType != typeof(void) || restoreRaw.ReturnType != typeof(void))
                    throw new InvalidOperationException("Modifier index/identity or raw maximum setter unavailable");
                additiveBinding = true;
                additiveStatus = "verified permanent additive metadata";
            }
            catch (Exception ex)
            {
                additiveStatus = "unavailable: " + ex.Message;
                ctx.Log.LogWarning("BMF optional additive health path: " + ex.Message);
            }
            ready = true;
            ctx.Log.LogInfo("BMF native health trial metadata verified; armed=" + Armed);
        }
        catch (Exception ex) { Stop("metadata", ex); }
        Write();
    }
    private float F(MethodInfo method, object obj) => (float)method.Invoke(obj, null);
    private int I(MethodInfo method, object facade) => (int)method.Invoke(facade, new[] { healthType });
    private int ModifierCount(object stat)
    {
        var list = modifiers.Invoke(stat, null);
        return list == null ? -1 : (int)count.GetValue(list);
    }
    private string Snapshot(object facade, object stat) =>
        $"current={I(getStat, facade)} max={I(getMax, facade)} objectValue={F(value, stat)} objectMax={F(max, stat)} permanentMax={F(permanent, stat)} rawMax={F(raw, stat)} modifiers={ModifierCount(stat)}";
    private bool SameStat(object facade, IntPtr key)
    {
        var live = getObject.Invoke(facade, new[] { healthType });
        return live != null && (IntPtr)pointer.GetValue(live) == key;
    }
    private bool TryReadModifierStamps(object stat, out HealthModifierStamp[] stamps, out string rejection)
    {
        stamps = null;
        rejection = "modifier count unavailable";
        try
        {
            var list = modifiers.Invoke(stat, null);
            if (list == null) return false;
            int n = (int)count.GetValue(list);
            if (n < 0) return false;
            if (n == 0) { stamps = Array.Empty<HealthModifierStamp>(); rejection = null; return true; }
            if (n > 16) { rejection = "too many health modifiers"; return false; }
            if (!additiveBinding) { rejection = "additive metadata unavailable"; return false; }
            var result = new HealthModifierStamp[n];
            for (int i = 0; i < n; i++)
            {
                var entry = modifierItem.GetValue(list, new object[] { i });
                if (entry == null) { rejection = "missing modifier object"; return false; }
                if (!object.Equals(modifierStatType.Invoke(entry, null), healthType))
                { rejection = "modifier is not health"; return false; }
                if (!object.Equals(modifierKind.Invoke(entry, null), additiveKind))
                { rejection = "non-additive health modifier"; return false; }
                if (!object.Equals(modifierPermanency.Invoke(entry, null), permanentKind))
                { rejection = "non-permanent health modifier"; return false; }
                float amount = F(modifierAmount, entry);
                if (!float.IsFinite(amount) || amount < 0 || amount > 10000 || amount != MathF.Truncate(amount))
                { rejection = "non-integral or negative additive health modifier"; return false; }
                result[i] = new HealthModifierStamp((IntPtr)modifierPointer.GetValue(entry), (int)amount);
            }
            if (!HealthBudget.TryAdditiveBonus(result, out _) || (int)count.GetValue(list) != n)
            { rejection = "modifier identity or total not verified"; return false; }
            stamps = result;
            rejection = null;
            return true;
        }
        catch (Exception ex) { rejection = "modifier read failed: " + ex.GetType().Name; return false; }
    }

    private bool SameModifiers(object stat, NativeHealthPartyTrial.Entry trial)
        => trial.Modifiers.Length == 0 ? ModifierCount(stat) == 0 :
            TryReadModifierStamps(stat, out var live, out _) && HealthBudget.SameModifierStamps(trial.Modifiers, live);
    private bool Matches(object facade, object stat, int current, int expectedMax, int expectedRaw, NativeHealthPartyTrial.Entry trial)
        => I(getStat, facade) == current && F(value, stat) == current && MaxMatches(facade, stat, expectedMax, expectedRaw, trial);
    private bool MaxMatches(object facade, object stat, int expectedMax, int expectedRaw, NativeHealthPartyTrial.Entry trial)
        => I(getMax, facade) == expectedMax && F(max, stat) == expectedMax &&
           F(permanent, stat) == expectedMax && F(raw, stat) == expectedRaw && SameModifiers(stat, trial);

    private bool WriteMaximum(object facade, object stat, NativeHealthPartyTrial.Entry trial)
    {
        if (trial.Modifiers.Length == 0)
            return setMax.Invoke(facade, new object[] { healthType, (float)trial.Target }) is true;
        // Use the native Stat.SetMaxValue method with the raw target, never
        // write the computed/effective maximum into the underlying raw field.
        statSetMax.Invoke(stat, new object[] { (float)trial.RawTarget });
        return true; // immediately verify all native getters and modifier stamps
    }

    private bool RestoreOriginalMaximum(object facade, object stat, NativeHealthPartyTrial.Entry trial)
    {
        if (MaxMatches(facade, stat, trial.Baseline, trial.RawBaseline, trial)) return true;
        if (trial.Modifiers.Length == 0)
            return setMax.Invoke(facade, new object[] { healthType, (float)trial.Baseline }) is true;
        // Only called after the same object and original modifier stamps were
        // reverified. The exact generated raw-field setter restores the captured
        // backing value, not an effective value with the modifier counted twice.
        restoreRaw.Invoke(stat, new object[] { (float)trial.RawBaseline });
        return MaxMatches(facade, stat, trial.Baseline, trial.RawBaseline, trial);
    }
    // The game model exposes a string-valued CharacterSheetId.Id. Do not retain
    // the wrapper or write the ID/pointer to logs. Reject both duplicate identities.
    private string ReadSheetId(object piece)
    {
        var id = sheet.Invoke(piece, null);
        return id == null ? null : sheetId.Invoke(id, null) as string;
    }
    private bool SameIdentity(object piece, object facade, string id, IntPtr key)
    {
        if (!string.Equals(ReadSheetId(piece), id, StringComparison.Ordinal) || !SameStat(facade, key)) return false;
        var liveFacade = stats.Invoke(piece, null);
        return liveFacade != null && SameStat(liveFacade, key);
    }
    internal void Observe(object piece, IntPtr verifiedRoom, bool allowWrite = true, string source = "AP")
    {
        if (!ready || faulted || party.Ended || (!Armed && party.Entries.Count == 0)) return;
        if (!authorization.MarkObserved(verifiedRoom))
        {
            RejectRoom("not the claimed fresh local room");
            if (verifiedRoom != IntPtr.Zero)
                CancelForTransition("Different gameplay room; health authorization cancelled");
            return;
        }
        try
        {
            if (usesHealth.Invoke(piece, null) is not true || isDowned.Invoke(piece, null) is not false) return;
            string id = ReadSheetId(piece);
            if (string.IsNullOrWhiteSpace(id)) { missingIdentity++; dirty = true; return; }
            var facade = stats.Invoke(piece, null);
            if (facade == null) return;
            var stat = getObject.Invoke(facade, new[] { healthType });
            if (stat == null) return;
            var key = (IntPtr)pointer.GetValue(stat);
            if (key == IntPtr.Zero) return;
            var existing = party.Find(id);
            if (existing != null)
            {
                // Multiple native copies may represent the same logical hero.
                // Do not scale or restore any replacement object.
                if (existing.StatKey != key)
                {
                    existing.ReplacedStat++;
                    if (existing.ReplacedStat == 1) Sample($"hero#{existing.Id}: different native health object; no additional write");
                    dirty = true;
                    return;
                }
                if (!existing.Applied) return;
                existing.Observations++;
                int current = I(getStat, facade), currentMax = I(getMax, facade);
                existing.LastCurrent = current; existing.LastMax = currentMax;
                bool retained = SameIdentity(piece, facade, id, key) && MaxMatches(facade, stat, existing.Target, existing.RawTarget, existing)
                    && current >= 0 && current <= existing.Target && F(value, stat) == current;
                if (retained)
                {
                    existing.Verified++;
                    existing.HighestCurrent = Math.Max(existing.HighestCurrent, current);
                    if (current > existing.Baseline) existing.AboveBaseline++;
                }
                else
                {
                    existing.Lost++;
                    writeHalted = true; // keep observing, but do not expand an unstable trial
                }
                Sample($"hero#{existing.Id} observed source={source} retained={retained} | {Snapshot(facade, stat)}");
                return;
            }
            if (!authorization.Allows(verifiedRoom) || writeHalted || !allowWrite || percent == 100) return;
            int initial = I(getStat, facade), nativeMax = I(getMax, facade);
            float objectCurrent = F(value, stat), objectMax = F(max, stat), permanentMax = F(permanent, stat), rawMax = F(raw, stat);
            int modifierCount = ModifierCount(stat);
            string readings = $"current={initial} max={nativeMax} objectValue={objectCurrent} objectMax={objectMax} permanentMax={permanentMax} rawMax={rawMax} modifiers={modifierCount}";
            var stamps = Array.Empty<HealthModifierStamp>();
            int bonus = 0;
            if (modifierCount > 0)
            {
                if (!TryReadModifierStamps(stat, out stamps, out string modifierRejection))
                { RejectCandidate(modifierRejection, readings); return; }
                if (stamps.Length != modifierCount || !HealthBudget.TryAdditiveBonus(stamps, out bonus))
                { RejectCandidate("modifier count or identity changed", readings); return; }
                readings += $" verifiedAdditive={bonus}";
            }
            string rejection = HealthBudget.NativeTrialRejection(percent, initial, nativeMax,
                objectCurrent, objectMax, permanentMax, rawMax, modifierCount, bonus);
            if (rejection != null)
            { RejectCandidate(rejection, readings); return; }
            int target = HealthBudget.Target(nativeMax, percent);
            if (!party.CanReserveIdentity(id, key)) { rejectedReservations++; dirty = true; return; }
            if (!ledger.TryAuthorize(id, key, (int)rawMax, nativeMax, modifierCount, out string ledgerRejection))
            { RejectCandidate("baseline safety: " + ledgerRejection, readings); return; }
            var trial = party.Reserve(id, key, nativeMax, target, initial, (int)rawMax, stamps);
            if (trial == null) { rejectedReservations++; dirty = true; return; }
            int baseline = trial.Baseline;
            Sample($"hero#{trial.Id} native trial target={target} rawTarget={trial.RawTarget} preservedModifiers={trial.Modifiers.Length} | before {Snapshot(facade, stat)}");
            bool writeAttempted = false;
            try
            {
                if (!SameIdentity(piece, facade, id, key) || !Matches(facade, stat, initial, baseline, trial.RawBaseline, trial))
                    throw new InvalidOperationException("Native health identity or baseline changed");
                writeAttempted = true;
                bool wrote = WriteMaximum(facade, stat, trial);
                if (!wrote || !SameIdentity(piece, facade, id, key) || !MaxMatches(facade, stat, target, trial.RawTarget, trial))
                    throw new InvalidOperationException("Native maximum not verified");
                // Preserve pre-write HP if the native maximum setter changed it.
                if (I(getStat, facade) != initial && setValue.Invoke(facade, new object[] { healthType, (float)initial }) is not true)
                    throw new InvalidOperationException("Current HP preservation failed");
                if (!SameIdentity(piece, facade, id, key) || !Matches(facade, stat, initial, target, trial.RawTarget, trial))
                    throw new InvalidOperationException("Native health trial verification failed");
                trial.Applied = true; trial.LastMax = target;
                Sample($"hero#{trial.Id} native trial verified=True | after {Snapshot(facade, stat)}");
            }
            catch (Exception ex)
            {
                if (writeAttempted)
                {
                    try
                    {
                        // Never rewrite or reconstruct the existing modifier list.
                        // Restore only the same native stat with the original stamps.
                        if (!SameIdentity(piece, facade, id, key) || !SameModifiers(stat, trial))
                            throw new InvalidOperationException("Rollback identity/modifiers unavailable");
                        bool maxOk = RestoreOriginalMaximum(facade, stat, trial);
                        if (!maxOk || !SameIdentity(piece, facade, id, key) ||
                            !MaxMatches(facade, stat, baseline, trial.RawBaseline, trial))
                            throw new InvalidOperationException("Rollback maximum, identity or modifiers not verified");
                        bool hpOk = I(getStat, facade) == initial ||
                            setValue.Invoke(facade, new object[] { healthType, (float)initial }) is true;
                        trial.RolledBack = maxOk && hpOk && SameIdentity(piece, facade, id, key) && Matches(facade, stat, initial, baseline, trial.RawBaseline, trial);
                        trial.RollbackFailed = !trial.RolledBack;
                        Sample($"hero#{trial.Id} native trial rollback verified={trial.RolledBack} | {Snapshot(facade, stat)}");
                    }
                    catch (Exception rollback) { trial.RollbackFailed = true; Sample("Rollback unverified: " + rollback.Message); }
                }
                Stop("native trial", ex);
            }
        }
        catch (Exception ex) { Stop("observation", ex); }
    }
    private void Stop(string stage, Exception ex)
    {
        faulted = true; errors++;
        Sample($"Stopped: {stage}: {ex.GetType().Name}: {ex.Message}");
        ctx.Log.LogWarning("BMF health rule stopped: " + stage + ": " + ex.Message);
    }
    private void Sample(string line)
    {
        if (samples.Count < 200) samples.Add(line);
        dirty = true;
    }
    internal void Tick() { if (dirty && (DateTime.UtcNow - lastWrite).TotalSeconds >= 5) Write(); }
    internal void Write()
    {
        try
        {
            var lines = new List<string> { "BMF hero maximum health probe (bounded party native maximum trial)",
                "Framework=" + Framework.Version, "GameVersion=" + ctx.GameVersion, "GameAssemblySHA256=" + ctx.GameAssemblySha256,
                "UTC=" + DateTime.UtcNow.ToString("O"), "Status=" + Status,
                $"ConfiguredPercent={percent} NextPercent={NextPercent} Armed={Armed} Queued={Queued} ScopeEnded={party.Ended} WritesHalted={writeHalted} Attempts={party.Entries.Count} Applied={Applied} Observations={party.Entries.Sum(e => e.Observations)} VerifiedObservations={Verified} Lost={Lost} Skipped={skipped} MissingIdentity={missingIdentity} RejectedReservations={rejectedReservations} ArmRejections={armRejections} Errors={errors}",
                "Activation=" + activationStatus,
                SafetySummary,
                "Additive modifier binding: " + additiveStatus,
                $"Fresh start binding: {freshStartBinding} NewCampaigns={freshStarts} ExistingCampaigns={existingStarts} Claims={claims} RoomRejections={roomRejections} LastRoomRejection={lastRoomRejection}",
                "Up to four unique character-sheet IDs and native health objects per verified fresh campaign; IDs/pointers not logged. Native maximum may persist. Discard test adventure." };
            foreach (var e in party.Entries.OrderBy(e => e.Id))
                lines.Add($"hero#{e.Id} applied={e.Applied} baseline={e.Baseline} target={e.Target} rawBaseline={e.RawBaseline} rawTarget={e.RawTarget} preservedModifiers={e.Modifiers.Length} originalCurrent={e.OriginalCurrent} lastCurrent={e.LastCurrent} lastMax={e.LastMax} highestCurrent={e.HighestCurrent} observations={e.Observations} verified={e.Verified} lost={e.Lost} aboveBaseline={e.AboveBaseline} replacedStat={e.ReplacedStat} rolledBack={e.RolledBack} rollbackFailed={e.RollbackFailed}");
            foreach (var reason in candidateRejections.OrderBy(e => e.Key))
                lines.Add($"Health candidate rejected: {reason.Key} count={reason.Value}");
            lines.AddRange(samples);
            File.WriteAllLines(Path.Combine(ctx.DataDirectory, "hero-health-probe.txt"), lines);
            lastWrite = DateTime.UtcNow; dirty = false;
        }
        catch (Exception ex) { ctx.Log.LogWarning("Health report: " + ex.Message); }
    }
}
