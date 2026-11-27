# Battlemarked Mod Framework (BMF) — v0.6.26-alpha

## First stable release automation

Development updates continue through the existing Mod Manager; no alpha release is created by this workflow. Once v1.0.0 is ready for stable publication, set `bmf-update.json` to version `1.0.0` and channel `stable`, and synchronize the version in `Directory.Build.props`, `src/Framework/Api.cs` and `src/Example/ExamplePlugin.cs`. Complete the Windows installation, gameplay and save-safety acceptance checks before making that commit on `main`.

The **Publish BMF v1.0.0** workflow checks the exact pushed commit, validates these declarations, and runs the release automation tests and .NET 8 core tests. Only a passing eligible commit can create the `v1.0.0` tag and first stable [GitHub Release](https://github.com/Matrixite/Battlemarked-Mod-Framework/releases). The release contains `Battlemarked-Mod-Framework-v1.0.0.zip` (the versioned source installer, including `Install-BMF.cmd`) and its `.sha256` checksum. The usual local BepInEx and SDK prerequisites still apply; no game assemblies or precompiled DLLs are included.

The workflow never moves an existing tag or replaces an existing release. If publication fails, inspect the Actions logs and retry the failed run for the same commit; an existing draft or conflicting tag requires review. GitHub Actions must remain enabled with permission for the publication job to create repository tags and releases. Future stable versions beyond 1.0.0 are not covered by this first-release workflow.

## v0.6.26 — combined House Rules tab

Open **F10 > House Rules** and choose **AP Rules** or **Health** within that tab. The two former top-level tabs have been combined to leave more room for other mods. All original controls, values, live AP behavior and explicit fresh-campaign health queueing remain unchanged. House Rules and Presets are still hidden when the House rules module is disabled; all other tab IDs and visibility rules are unchanged.

## v0.6.25 — independent Hero Forms tab and live toggle

Open **F10 > Mods** and use the **Hero forms (experimental)** Enable/Disable switch at the top of the module list. This preference is saved and applies immediately, unlike the restart-only module switches. It defaults to enabled to preserve v0.6.24 behavior. **Hero Forms** is now a separate tab (called **Forms** on smaller displays); it is visible only when both Hero Forms and Developer mode are enabled. House rules must still be running in a verified local solo campaign.

The Hero Forms page has its own refresh, search, paged creature list and **Transform hero** map tool. Level Editor returns to enemy placement, removal and tile inspection only. Disabling Hero Forms hides the tab, cancels pending selection and unconsumed requests and blocks native form dispatch; it does not revert a character already transformed or interrupt a native coroutine already accepted by the game. Read-only native session capture is retained when the feature is disabled, so re-enabling can use the current valid captured game session. No other module preferences change.

## v0.6.24 — experimental playable creature forms

The user confirmed v0.6.23 direct map placement/removal works; their runtime log records a GoblinFighter removal and BarBrawlLoggerMelee and BlackPuddingSmall spawn requests. Those tools remain available.

**F10 > Level Editor > Refresh > Hero forms.** Select a registered living 1x1 creature (including enemies and eligible ordinary heroes), then **Transform hero**. During the hero's turn middle-click that hero on the 3D board and press **Confirm hero form**. Developer mode and House rules must be enabled before starting a new disposable local solo campaign. The game must be idle.

BMF submits a one-time native debug request and uses the game's actual simulation-side reversible transformation routine, retaining the real hero sheet, player ownership and Players team. A request accepted by the handler is not proof of a completed or playable transformation. The native transformation rules may change cards, stats and available actions. This is a first experimental implementation, not an unrestricted hero-select class unlock: multi-tile creatures, NPCs, props, nested transformations and a separate manual revert button are not supported yet. It does not promise persistence across encounters or saves. Back up saves and test only in a disposable campaign. No AI execution or enemy ownership changes are made outside the transformation request. Native request/processor capture and local session identity must match; otherwise the tool declines the request and shows a reason.

## v0.6.23 — direct map request and missing armour fixes

The user's v0.6.22 runtime log confirms 162 registered eligible enemies, successful map targeting, and two pre-dispatch failures: `Native local spawn request unavailable` and `Stat of type PhysicalArmor couldn't be found`. Both spawn and removal now use a native checked `TryCast<IRequest>` on the constructed request rather than relying on CLR wrapper interface assignability. The verified native metadata lists IRequest on both concrete request types. The cast must return the same nonzero native pointer with the expected interface wrapper type. For removal, native StatsFacade.TryGetStat(Type, out int, int defaultValue) reads optional PhysicalArmor with zero as the missing-stat default; required Health and bounded damage validation remain intact. Scope and target protections are unchanged. Both fixes have failing-before/passing-after source regression checks; native dispatch and results still require Windows gameplay confirmation.

## v0.6.22 — direct 3D board placement and removal

In the Level Editor, refresh the board, select a registered enemy and press **Place on map**. The full manager is replaced by a compact toolbar at the top of the screen. Middle-click a visible free tile on the actual 3D game board; the game camera and native desktop board picker resolve the tile, including elevation. Check the selection and press **Confirm placement**. After a submitted request, select another tile to place another copy of the same enemy. The original grid placement is still available.

For removal, press **Remove on map**, middle-click an occupied tile and press **Confirm removal (damage)**. This sends the game's single-target native physical damage request for the selected hostile creature or attackable prop, with observed HP and physical armour used to calculate bounded damage. The game decides the actual result; ordinary death/destruction, loot and encounter effects may apply, and immunity or scripts may prevent removal. This is not a generic scene-object deletion or guaranteed despawn. Heroes, player-controlled/downable pieces, NPCs, POIs and non-destructible scenery are excluded. Press **Back to editor / cancel** or F10 to leave targeting; pending requests/selection are cleared. No automatic repeat, direct object destruction or multiplayer writes. Use disposable solo campaigns.

The desktop picker is captured on its live Tick, not a constructor; exact HUD state, board, room and freshness are checked before selection/dispatch. Game-action processing, active grab and room-queue checks reject busy states. Source regressions pass; Windows compilation, native targeting, enemy spawn and damage/removal behavior require in-game acceptance.

## v0.6.21 — level editor enemy list correction

The user confirmed the v0.6.20 update loads and the board/tile inspector works, but the enemy pane was empty with the "Tiles ready" registry-capture message. The editor now obtains the piece registry from the verified current HUD model, without requiring the matching spawn-handler constructor capture. Native metadata confirms `AffectedPiecesConfig` is a ScriptableObject: use the matching captured config when available, otherwise require an unambiguous live loaded config. Metadata also confirms `PieceAIBehaviourDefinition` is a native reference object, not a Unity asset; the old Unity-object test would reject every AI definition. The editor checks the correct native type and nonzero pointer instead.

Press **Refresh board & enemies** in a local solo campaign. The status reports eligible/scanned/unreadable counts or the specific registry/config problem. Registration, hostile creature classification, 1×1 footprint, tile availability and native local-room checks are still required before a single normal spawn request. No direct writes or multiplayer scope changes. The two source regressions failed against v0.6.20 and pass with this correction; Windows compilation and actual enemy list/spawn acceptance are still pending.

## v0.6.20 — experimental runtime Level Editor

Open **F10 > Mods**, ensure House rules is enabled and running, and turn **Developer mode ON before starting a new disposable local solo campaign**. In a loaded level, open **Level Editor** (labelled **Editor** on a smaller display) and click **Refresh board & enemies**. The map is a paged 8×8 window of the live board model: `.` free, `#` blocked, `P` occupied, `-` missing, `*` selected. Use X-/X+/Y-/Y+ to move the window; select a tile to inspect its coordinates, height, flags and occupancy.

The enemy list contains eligible loaded, registered, non-player-controlled 1×1 hostile creatures with AI, classified by the current game spawner's registry and native piece qualities. Search by name or ID, select a free tile and enemy, then **Place selected enemy > Confirm enemy spawn**. The request runs once on Update, rechecks the current local solo HUD/room, tile and definition, and uses the game's normal RequestSpawnPiece pipeline; a submitted request is not proof of a successful spawn. Check the game and refresh again. No action runs automatically, and multiplayer/unverified scope is rejected.

This is a runtime inspection and placement prototype, not the original Unity level authoring tool. No terrain editing, deleting pieces, entrance/exit editing, map saving/export/import, or custom-level creation is implemented. Do not use on valued campaigns; back up saves. The v0.6.20 constructor capture requirement is superseded by the v0.6.21 HUD registry correction above. Native binding errors are reported in the editor/BepInEx log. Windows installation and in-game acceptance remain pending.

## v0.6.19 — fix debug guard build error

The captured v0.6.18 build report identifies three CS0176 errors in HouseRules.cs: the new card-grant guard accessed the shared static session model through an instance. All three references now use `HouseRules.model` explicitly, with a separately named captured-model snapshot and the same final identity check. The native read/write guards and other Dev Tools behavior are unchanged. The installer retains detailed build logging. The user confirmed v0.6.19 installs successfully; card-grant gameplay acceptance is still required.

## v0.6.18 — installer build diagnostics

The v0.6.17 installer transcript confirms that the source build exited unsuccessfully after detecting the correct game fingerprint and .NET SDKs, but included no compiler/restore output. The installer now explicitly redirects native stdout and stderr to per-project files and copies both streams and the exit code into `BepInEx/BMF-Updates/build-details.log` before handling failure. The transcript also receives the captured lines. Existing transactional copying and backup behavior are unchanged. If this update fails, share build-details.log; the underlying build failure is not yet identified. Windows execution remains to be verified.

## v0.6.17 — debug workspace compiler compatibility

If an update queues successfully but the next launch remains on v0.6.14, the download/queue did not necessarily fail: the installer builds from source after shutdown and retains the previous DLLs when compilation fails. v0.6.16 accidentally reintroduced two class records, a known source of CS0656 with the game's generated interop reference set (see v0.2.1). v0.6.17 uses ordinary classes and explicit native-method/constructor matching instead of the new capturing reflection lambdas. Dev Tools behavior and existing update/install safety checks are unchanged. The user’s actual v0.6.16 installer error remains unconfirmed without the Windows transcript.

Check for updates again, queue v0.6.17, quit and wait for Steam to show Play **and** the installer to finish. If the old version persists, inspect `BepInEx/BMF-Updates/last-update.txt` and share `update-install.log` from that folder. Do not disable antivirus or remove the installed framework.

## v0.6.16 — BMF-owned debug workspace

F10 > Mods > Developer mode ON > Dev Tools opens a new independent inspector, without using the disabled native menu. Choose a page, then **Refresh** for a bounded, searchable snapshot of loaded objects:

- **Scene:** active-scene Unity objects, activation state, child count and position.
- **Board:** current-scene piece views and IDs/positions. Select a hero for the Cards page.
- **Pieces:** loaded piece definitions, type IDs and base HP/AP/attack.
- **Cards:** loaded card definitions and IDs/GUIDs. Select a card after selecting a hero on Board; press Grant, then Confirm grant to submit one experimental request.
- **Encounters:** loaded encounter assets and GUIDs.
- **Events:** observed core/gameplay method-call totals for running diagnostic modules.
- **Native:** the original guarded launcher, retained for compatibility.

Browsing is read-only and not a complete asset catalogue. Snapshots are capped at 4,096 inspected objects, can contain inactive/preview objects, and are cleared on scene change or leaving Dev Tools. Card grants require the verified game build, House rules running, Developer mode enabled **before a local campaign is created** (for registry capture), an active local hero, a current HUD-backed native room with exactly one connected local player/host, matching game-state registry, unchanged selection IDs and an idle request queue. The framework sends the normal native card request, not a direct inventory write; submission does not guarantee delivery. Use only disposable local solo sessions with save backups. Unavailable bindings or unproven scope fail closed. At v0.6.16 no spawning or level editing was available (see v0.6.20 above). This alpha has not been compiled against the installed Windows interop files or tested in a live game.

## v0.6.15 — native debug menu retail-build detection

The verified retail build initializes the native menu with `NopFidelityDebugMenuFacadeImpl` instead of the actual developer interface. The separate Game Design UI's enable getter returns false and its setter does nothing. BMF now checks the native facade and DebugTools instance before opening, so an empty container no longer causes the mod manager to close. The Dev Tools status reports the restriction or missing initialization, with readiness details in the BepInEx log. This is detection, not a restoration of developer features that the retail build disables.

## v0.6.14 — experimental Dev Tools tab

Open F10 > Mods and turn Developer mode ON, then select Dev Tools > Show game debug menu. The framework calls the existing native `Jade.DebugMenu.DebugMenu.ShowDebugMenu()` method on the verified game build, if exactly one loaded scene component is available. F10 returns to the mod manager; Hide game debug menu closes it. Turning Developer mode OFF hides the tab and closes a native menu opened by BMF. No feature flags, debug commands, game saves or module settings are changed by the launcher. The game debug menu itself can provide commands that change game state: use a disposable local solo game and back up saves first.

This experimental launcher is session-only and reports unavailable if the menu, compatible interop type or method is absent. It does not recreate an editor or promise that every internal developer tool is included in the retail game.

## v0.6.13 — prominent update restart warning

The Updates tab displays a large bold yellow warning: after the update is queued, quit Battlemarked and wait until Steam shows **Play**, not **Stop** or **Stopping**, before launching again. The updater, install worker and module lifecycle are unchanged.

## v0.6.12 — rollback to v0.6.10 behavior

Reverts the v0.6.11 experimental main-menu module switching changes in their entirety. Module enable/disable switches again take effect on the next game launch. The developer-mode visibility, provider-neutral update screen and House rules tab visibility are retained from v0.6.10. The version is raised to 0.6.12 so the existing updater can install this rollback; no downgrade or configuration/save reset is needed.

## v0.6.10 — hide Presets with House rules

Presets now follows the same saved module enable preference as AP rules and Health. Disable House rules in F10 > Mods to hide all three tabs immediately; re-enable to show them again. Overview, Mods and Updates remain accessible. Reports continues to follow Developer mode independently. Module lifecycle and safety guards are unchanged.

## v0.6.9 — house-rule tab visibility

Disabling House rules in F10 > Mods hides both AP rules and Health immediately, regardless of Developer mode. Re-enabling the module shows them again. Tab visibility follows the saved next-launch enable setting; starting or stopping the module itself still requires a game restart, and the existing AP and health safety guards are unchanged. Overview, Presets, Mods and Updates stay accessible; Reports remains developer-only.

## v0.6.8 — cleaner normal-player interface

When Developer mode is OFF, the Reports tab and the three diagnostic module entries are hidden. Enable Developer mode from F10 > Mods to bring them back immediately. Other tabs remain accessible and the tab bar fills the available width in either mode. The Updates screen uses provider-neutral labels/status messages, while the existing update source, safety checks, installer and technical diagnostics are retained. For support exports, turn Developer mode ON first, then use F10 > Reports > Export support ZIP.

## v0.6.7 — developer-only module visibility

The Mods tab now hides Core gameplay diagnostics, Gameplay event logger and the optional Scene logger by default. Toggle **Developer mode: OFF/ON** at the bottom of F10 > Mods to show or hide those entries immediately; the preference is saved under Menu > DeveloperMode in the framework config. This changes display only: existing diagnostic module enable/disable settings, reports, house rules and other mods are unaffected. Module enable/disable switches still apply on the next launch. Developer mode is an opt-in display preference, not an access-control mechanism.

## v0.6.6 — one-click diagnostic export

Use **F10 > Reports > Export support ZIP** to capture the available framework reports in one local ZIP. The export refreshes compatibility, AP and health reports, includes existing startup/gameplay/core reports and the last update result if present, and writes a manifest listing report versions and timestamps, missing or oversized files. Each report is capped at 1 MiB. Find the ZIP in `BepInEx/config/BattlemarkedModFramework/support/`. It intentionally excludes configuration, saves, the health baseline ledger, raw BepInEx log and installer transcript. Review the included reports before sharing; no automatic uploads. For updater failures, share `BMF-Updates/update-install.log` separately if needed. AP, health behavior, updater, and installer are unchanged.

The user confirmed the automatic upgrade to v0.6.3 and the v0.6.5 Steam stopping correction. Remaining release gate: verify repeatable health on two genuinely fresh disposable campaigns without restarting, and validate the export on Windows.

## v0.6.5 — updater shutdown cleanup

The installer now disables persistent .NET build servers, Roslyn shared compilation and MSBuild node reuse during framework builds only. This addresses a plausible reason Steam can continue showing STOPPING after a game-launched update finishes: reusable compiler/build processes might still belong to its process tree. Steam may still show STOPPING while the legitimate post-exit installation is running; wait for the installer to finish. The change does not terminate other processes, change antivirus settings, or modify the game's shutdown code. If Steam remains stuck after a normal exit without an update, capture the Battlemarked process status and game log; this is a separate possible shutdown issue. The exact cause of the user's report remains unverified.

## v0.6.4 — moveable mod manager

Open F10 and hold the left mouse button on the title bar (marked "Drag to move"). Drag the entire manager to the desired location, then release. The position is saved in the Menu configuration and restored after reopening or restarting. The manager remains within the current screen bounds when the resolution changes. Set Menu PositionX and PositionY to -1 in the closed game's framework config to reset to center. Existing buttons, AP, health, presets and the updater are unchanged. Install using F10 > Updates; the user confirmed the v0.6.2 to v0.6.3 automatic update worked.

## v0.6.3 — automatic update test

This is an intentional version-only test release following the user's confirmation that v0.6.2 works. It changes no gameplay rules, updater implementation, worker or installer logic. Its purpose is to test the complete update from v0.6.2 through **F10 > Updates**, including source download, staging, post-exit build/install, and relaunch. No manual ZIP install is required for the intended test. Wait for the worker to signal that the update is queued before closing the game, and do not restart until the installer finishes.

## v0.6.2 — updater compiler compatibility

The Windows v0.6.1 build reported CS0656 (missing `System.Runtime.CompilerServices.NullableAttribute..ctor`) specifically at the captured download lambda in FrameworkUpdater.cs. This patch replaces that inferred compiler-generated closure with a named, explicitly typed request/delegate, while retaining the same asynchronous staging, safety checks, updater worker and installer. It does not require editing the SDK, BepInEx or game assemblies. The working named-method update-check pattern is unchanged. Install v0.6.2 manually; v0.6.1 failed to build, so its updater cannot install this correction. The user subsequently confirmed v0.6.2 works.

## v0.6.1 — automatic GitHub updates

Install this version once using `Install-BMF.cmd`. From then on, open **F10 > Updates**.

- **Automatic update checks** are on by default: a nonblocking check at startup and every six hours, or click Check for updates now.
- **Download and install when game closes** downloads a pinned snapshot from the public `Matrixite/Battlemarked-Mod-Framework` repository, checks the manifest/version and safe archive paths, and schedules a separate Windows worker. No plugin DLL is replaced while the game is running. Keep the game open until the status says queued, exit normally, then wait for the installer to finish before relaunching.
- **Automatic installation** is off by default. Enable it to download and queue newer versions without another click. Cancel a queued update before exiting if you change your mind. Disabling automatic checks does not cancel a worker already queued. A cancelled/failed scheduled worker for the same pinned commit is not automatically repeated; use a manual check and install to retry.
- The updater builds the source locally with the existing .NET SDK and BepInEx/interop assemblies. It uses the backup-and-rollback installer, checks the expected version and refuses an automatic downgrade before copying, and keeps existing config, health baselines, third-party plugins and game saves. It also updates an already-installed example DLL. It does not launch or forcibly close the game.
- See `BepInEx/BMF-Updates/last-update.txt` and `update-install.log` for the last worker outcome. Backups remain in `BepInEx/BMF-Backups/`; staged source is retained for troubleshooting. If necessary, run `Restore-BMF.cmd` from a manually extracted source archive with the game closed (DLL rollback only).
- Security model: the fixed public repository is the trusted source of executable code. HTTPS, pinned commit IDs, strict version comparison, no redirects, bounded downloads and archive path validation are used. No GitHub password, token or separate binary signing key is required. This is not a signed binary release mechanism: protect the GitHub account and keep main releasable. No credentials or gameplay data are sent by the updater.
- **Publishing future updates:** bump the numeric version in `bmf-update.json`, `Directory.Build.props`, `src/Framework/Api.cs` and `src/Example/ExamplePlugin.cs` together; keep the installer and worker contract compatible. The updater checks main's pinned commit and only offers strictly newer versions. Changes without a version bump do not update clients already on that version.

v0.6.1 adds only the updater; the AP and native-health rule code is unchanged from v0.6.0. Its Windows worker still requires a real update/restart test.

## v0.6.0 — Stability and quality of life

This source alpha builds on the user's verified v0.5.13 session: 12 AP refunds, four resets, four native health increases (16→20, 25→32 with raw 22→29 and the original +3 modifier, 20→25, 16→20), all four healed to target, 46/46 retained observations, zero framework errors. The user subsequently compiled and played v0.6.0: the Relaxed preset produced four verified AP refunds and four resets, with zero framework errors. Health was not queued in that session.

- **Repeatable health trials:** leave the current campaign, queue from F10 > Health, then start a new disposable solo campaign. A successful exact fresh-start callback with one verified local room opens a new bounded four-hero trial; loading an existing campaign never authorizes a write. The health preference persists; explicit authorization does not. Any fault or loss of verification still blocks further writes for the launch. Native setter, modifier preservation and AP refund math are unchanged.
- **Baseline safety:** before a native health write the framework records the original raw/effective maximum and modifier count in `health-baselines-v060.txt` under the house-rule config directory. A known character-sheet identity (SHA-256 hash only) or same-process native stat pointer with changed baseline is rejected. Missing/corrupt/unwritable known journal data stops new health writes; never delete it to bypass the guard. A different identity, previously modified save from an older release, or deleted journal across launches cannot be proven unmodified. Use fresh disposable games, retain the journal and back up saves separately. This is not a universal save validator or an automatic repair tool.
- **Manager:** F10 opens Overview, AP rules, Health, Presets, Mods and Reports. AP preferences still apply live; refund/cap mode switches still wait for verified idle scope. Module toggles are next-launch only.
- **Presets:** Vanilla (+0 AP / 100% HP), Relaxed (+1 / 125%), Heroic (+2 / 150%), Tested party (+3 / 125%), Epic (+3 / 200%). All prefer AP refunds rather than cap overwrites; non-vanilla presets include existing consistent caps in the refund calculation. Selecting a preset changes preferences and cancels health authorization; queue separately. Vanilla does not undo previous native changes.
- **Diagnostics:** the local `compatibility-report.txt` combines build/hash match, active/next-launch module states, scope flags, hook status, AP counts, health status, baseline ledger and prior-trial totals. It refreshes every 15 seconds when startup diagnostics are enabled or on demand in Reports. A verified fingerprint alone does not certify runtime hook behavior or save safety.

### Install or update

1. Back up your game saves independently, exit Battlemarked, and extract the entire source ZIP outside the game directory.
2. Double-click `Check-BMF.cmd` for prerequisites and the game fingerprint. Steam libraries are detected automatically; enter the game directory if no unique installation is found. BepInEx 6 IL2CPP x64 (initialized once), .NET 8 SDK and .NET 6 targeting pack are still required.
3. Double-click `Install-BMF.cmd`. It builds both projects before copying only the BMF DLLs; it snapshots the previous DLLs with checksums into `BepInEx/BMF-Backups/`. Existing settings, ledger, third-party mods and saves remain untouched. The script checks that the game is closed and attempts rollback if copying fails.
4. If necessary, close the game and run `Restore-BMF.cmd` to restore the latest complete DLL snapshot after confirmation. It backs up the currently installed pair too. It cannot undo game-save changes. Do not delete older backups if you need them.
5. Launch, press F10, and use fresh disposable solo adventures only. See VALIDATION.md for the two-campaign regression test.

For custom paths, pass `-GameDir "D:\SteamLibrary\steamapps\common\Battlemarked"` to any of the three launchers. The command-line installer performs no downloads; v0.6.1 adds the separate opt-in updater described above.

## Historical release notes (older versions)

## New in v0.5.13 — preserve verified additive permanent health modifiers

v0.5.12 diagnostics confirm the fourth hero was skipped six times by the existing-modifier gate: current/max/permanent=25, raw max=22, modifier count=1. Three other heroes retained all 32 subsequent observations (two healed above their original maximum), and AP recorded 12 refunds/four resets/zero errors. The runtime probe does not identify the modifier's type or permanency, so only the observed +3 difference is known.

The supplied Jade.GameModel.dll identifies StatModifier.get_StatType (20001185C0), get_Modifier (20000C), get_ModifierType (20001185AC), get_ModifierPermanency (20001185B0), Stat.SetMaxValue (2001010C), and set_maxValue (2001010C). Enum metadata gives AdditionAndSubtraction=0 and Permanent=1. Optional exact bindings read each modifier's native identity, type, magnitude and permanence. Only positive or zero integral permanent additive health modifiers (max 16), whose sum exactly explains the difference between raw and effective/permanent max, are eligible. Unknown, negative, temporary, multiplier and cap modifiers remain read-only. Unmodified heroes keep the original facade setter path.

For the observed 22 raw + 3 modifier = 25 effective, if the modifier is eligible, 125% targets ceil(25 * 1.25) = 32 effective. The framework writes 29 raw via Stat.SetMaxValue, preserving every original modifier and current HP=25. It checks original and resulting raw/permanent/effective values and original modifier identities and magnitudes before/after the write and on subsequent observations. It does not change or recreate any modifiers. If verification fails, it attempts to restore the exact original raw backing value via set_maxValue only on the same stat with unchanged modifier stamps, verifies the original maximum, then restores current HP if needed. Any failure stops the party trial; discard the test adventure. Original room authorization, bounded party trial and AP behavior remain unchanged.

Test 125% in a fresh disposable local solo campaign, using the same four-hero party. Expect an additional hero with rawBaseline=22 rawTarget=29 target=32 preservedModifiers=1 if its modifier is supported. Check additive modifier binding, applied/verified/lost/rollback fields and any candidate rejection reasons in the health report. Exact runtime modifier interpretation, SetMaxValue effects and rollback are unverified. Do not use on an existing save or multiplayer.

## New in v0.5.12 — health eligibility for injured heroes

The v0.5.11 fresh-campaign queue worked at runtime: one new campaign and one successful claim, no room or arm rejections, three verified native max increases (16→20, 20→25, 16→20), 26 retained observations, and one hero reached 20/20 HP after beginning at 16/16. The AP probe confirms 12 verified refunds across four heroes and zero framework errors. Six health candidates were skipped by the baseline check. The old report does not show which baseline check failed, so that cause is not proven.

The native health trial no longer requires full health. In the already verified fresh local solo room, an eligible living hero with consistent positive current HP at or below its unmodified native max can receive the max increase while retaining exactly its original current HP. For example, 14/16 at 125% becomes 14/20, not 20/20. Downed or zero-HP heroes, invalid/mismatched readings and any existing or unknown modifiers still cannot be changed. The runtime write, same-object verification and rollback, one-room authorization, one-attempt-per-identity/four-hero limit and AP calculations are unchanged. Health reports now include a count by baseline rejection reason and one anonymous set of raw readings for each reason.

Test in a new disposable solo campaign: queue 125% health before creating it, play each hero and inspect the four maximums. If any hero remains unchanged, upload the hero-health probe, especially the new Health candidate rejected lines. Discard the adventure. Source changes and added assertions have not been compiled or runtime-tested here.

## New in v0.5.11 — queue hero health for a fresh campaign

The v0.5.10 runtime report verified 18 AP refunds across four tracked hero AP objects, six budget resets and zero framework errors. All four used five paid one-AP actions in the first round. The HUD model provided true solo/local/in-game/playing/host flags and false multiplayer; the alternative room proof was not needed. Eleven non-character-sheet callbacks were skipped. Health did not run: eight arm rejections, zero attempts. The old lifetime AP activity guard prevents arming once any AP callback has occurred, even if the user is returning to the menu; the exact timing of the eight clicks is unknown.

The health button now queues an explicit in-memory request for the **next new solo game**. It can be clicked at the main menu or during gameplay; clicking it does not change the active game. Exact metadata in the supplied interop report identifies Jade.Application.AppStatePlaying.StartNewCampaign (200212814C11831908), JoinExistingCampaign (200412814C122D1184541512837D0112351281C5) and get_NetworkRoom (20001281B1). The queue can be claimed only on the new-campaign callback with one local player and a nonzero checked native LocalNetworkRoom identity. Joining an existing game, an off-main-thread transition, or an invalid start cancels the request. Before any health write, the original verified build/local solo/host/active local hero checks must pass, the native room must still match the fresh claim, and its player count, local player count and host status must be verified. A different verified room cancels the claim. If required metadata or runtime verification fails, health remains read-only; AP is independent.

The selected percentage persists as before; authorization is **not** persisted. Disarming or changing the target cancels queued or armed writes. There is still at most one attempted party trial per process, up to four unique sheet/stat identities, with the same full baseline, max-only write, verification and rollback protections. Once gameplay is observed, departure ends that authorization; after any write attempt the trial cannot be rearmed in the same process. This avoids stacking native max changes, which can persist in saves. The AP algorithms and controls are unchanged.

**Test:** launch v0.5.11, set health to 125%, click **Queue health for next new solo game**. Confirm the status is Queued. Start a completely new disposable local solo game, without restarting or loading a save. Play paid actions on all heroes, try healing above original max HP, and send the AP/health probes plus LogOutput.log before restarting. Check the health probe for Fresh start binding, NewCampaigns, ExistingCampaigns, Claims, RoomRejections, Armed, Attempts, Applied and per-hero retention. The new hook/room timing and party-wide writes are not yet runtime verified. Discard the test adventure.

## New in v0.5.10 — verify first-adventure local solo scope

The v0.5.9 report showed +3 AP, refund mode enabled, two spend callbacks but no samples/refunds, 165 model capture observations (not unique model instances), two HUD captures and two scope rejections because IsSinglePlayer was false. The health trial was not armed. The reports confirm the AP settings were loaded, not that any of the other session flags were true.

Prefer the current-scene HUD model or already verified model over unrelated getter captures; capture from the HUD model getter as well as turn start. A new scene, matching teardown, or new HUD identity can replace the selection. A changed selected model ends the old tracked scope rather than transferring refund budgets between sessions.

The supplied metadata exposes HeroSelectModel.get_NetworkRoom (2000128191), get_LocalPlayerCount (200008), and LocalNetworkRoom.get_NumPlayers (200008), IsHost (200002), IsConnected (200002). If the UI single-player flag is explicitly false, the framework can accept independent solo proof **only** from the current HUD model with a successful IL2CPP checked cast to LocalNetworkRoom, one room player, one local player, connected/host room, and all other UI flags (local, not multiplayer, in game, playing, host). Unknown or explicit multiplayer remains blocked; the exact fingerprint and active/local hero checks remain required. If reflection binding or room verification fails, the fallback is unavailable. No game flag is modified.

The report records HUD preference/ignored getter captures, optional checked-room binding, all six UI flags and the last rejected flags, plus the room proof when evaluated. Settings and AP/health algorithms are unchanged. The new path is not runtime-verified.

Test: install v0.5.10, at the main menu enable +3 AP/refund mode, set health percentage and explicitly arm current launch if testing health. Start a new disposable local solo adventure and use paid actions; do not restart. Include the house-rules and hero-health probes and LogOutput.log before restarting. For AP, look for Refunds > 0 or Grants > 0 with local solo hero scope, and for health, Armed/Applied. Discard test adventure.

## New in v0.5.9 — new-adventure session capture

User reported needing to create a fresh game and restart before rules took effect. The exact failing runtime condition is not yet confirmed; no v0.5.8 failure probe was supplied. The prior implementation cleared a captured HeroSelectModel on every polled Unity scene change, even if native callbacks had already supplied a model belonging to the new scene. This could discard the model or end a newly started health trial. It also cleared all state from any model teardown, including unrelated instances.

This version tags each main-thread capture with the actual scene handle and native model identity, synchronizes scene state before AP operations, and retains only a capture made in the new scene. Duplicate scene notifications do not clear it again. Matched teardowns clear the relevant scope/candidate rather than discarding a newer unrelated capture. AP operations whose model or scene changed during the operation are rejected. Additional optional capture hooks on the already verified HeroSelectModel getters and active-hero checks, and the supplied assembly report's InGameMenuPresenter.OnTurnStart (200001) and get_heroSelectModel (2000129080), supply the model without changing the game. Missing optional hooks are logged. Existing exact build, main-thread, local solo/host/active hero checks are retained; no fallback authorizes unknown scope. Health and AP calculations unchanged.

Start a new disposable solo adventure without restarting the game. Set AP and arm health at the main menu, move all heroes, end a round, and upload house-rules-probe.txt, hero-health-probe.txt and LogOutput.log **before restarting**. The AP report now records model/HUD captures, retained captures, ignored teardowns, and separate scope-rejection reasons. If rules do not activate this will distinguish failed model acquisition from a rejected gameplay/hero flag. Party health remains experimental; native changes may persist. Discard test adventure.

## New in v0.5.8 — live AP settings, no restart

The House rules AP menu now changes the active extra AP bonus (0–3) and existing-cap adjustment immediately, including during a verified local solo adventure. Settings are also saved for the next launch if the config write succeeds. The menu displays **Active** AP rather than Next launch. No direct stat write occurs when you click a setting; it takes effect on the next eligible AP event. Refunds already used in a cycle are not reset on settings changes. Disabling prevents new bonuses but does not undo actions already taken, or instantly lower an existing cap; previously pending restorations still run. Native same-cap refills continue resetting the refund ledger even while the bonus is disabled or existing-cap adjustment is off.

Switching AP refund mode is immediate at an idle main menu before any tracked adventure, or after a verified idle scope/teardown with no tracked AP objects or in-progress writes. A change requested during an adventure is explicitly queued until the next safe idle scope, not until a game restart. Return to the main menu and wait for the queued indication to disappear before starting a new adventure. Clicking the queued mode button again cancels the request. Scene changes alone do not prove an ended adventure. This prevents switching algorithms mid-cycle or abandoning live cap restorations. The partial-refill policy remains a startup-only advanced config setting.

**Test in a fresh disposable local solo adventure.** Enable refund mode and select +1 AP at the main menu; start the adventure without restarting. Change to +3 during play and use up the current cycle's remaining refund allowance; decrease/disable/re-enable during the same cycle and verify used refunds are not repeated. Complete a verified refill and check the new allowance. Request a mode switch mid-adventure, return to the main menu, confirm it takes effect, and upload the AP probe and log. The current-launch health arm and party trial are unchanged. Prior AP and health runtime observations apply to earlier versions; live settings require verification.

## New in v0.5.7 — no-restart health activation

A v0.5.6 receipt confirmed that its request had been claimed on a launch, but the subsequent report was unarmed and had no AP activity or health attempts. The exact sequence of launches is unknown; no party-wide native write has yet been tested. The one-shot next-launch mechanism is replaced with an explicit in-memory current-launch arm. Legacy pending and claimed files and the old config flag cannot arm this release.

Launch the game to its main menu, open F10 > House rules > Hero health, select 125%, and click **Arm health trial (current launch)**. The status must show **Armed: 125% | 0/4 heroes applied**. Close the menu and start a completely new disposable local solo adventure **without restarting**. Move each hero, try healing above the original maximum and finish a round. Upload the health and AP probes and main log.

Arming is denied if any AP hook has been observed (including off-thread activity), a captured model indicates an adventure is active, or the party trial already made an attempt/ended. A model may not be captured at the main menu; the framework cannot prove save freshness. Exact fingerprint, local-solo/active-hero checks, four unique identities maximum, no repeat writes, baseline checks and rollback remain unchanged. The arm is never persisted, and changing percentage or clicking Disarm stops further writes without reversing a modified maximum. Previous receipts are inert in v0.5.7. If an arm is rejected, check Activation and ArmRejections in the probe. Native maximum can persist in saves; discard the test adventure. AP rules remain unchanged.

## New in v0.5.6 — bounded party-wide native health trial (historical)

The v0.5.5 test successfully claimed the activation request and verified the native maximum: 22 -> 28 HP at 125%, with current HP preserved at 22. Eight subsequent observations retained the raw/permanent/effective max; zero losses and zero errors. Healing above 22 HP and party-wide behavior were not tested. AP continued to work (four refunds, one reset, zero errors).

This version extends the successful native trial to up to four distinct eligible heroes in a single local solo scope. The supplied generated Jade.GameModel assembly verifies Piece.get_characterSheetId signature 2000128490 and CharacterSheetId.get_Id signature 20000E, returning a string. Both the character-sheet ID and native health stat identity are reserved before a write. Neither ID nor pointer is printed. No second attempt for either identity, including replacement stat copies; no reapplication, even after loss. Initial full/unmodified baseline, exact-build/main-thread/local-solo scope, native verification, current HP preservation and same-object rollback remain required. A failed write stops health independently of AP. A lost maximum stops further writes. Once an attempt has occurred, scope departure permanently seals the party trial for that process. The framework cannot prove that a save is fresh; persistent changes may compound across separately armed launches.

**Fresh disposable local solo adventures only.** Install v0.5.6, F10 > House rules > Hero health, select 125%, click Next launch request until SAVED and restart once. The request is version-specific; previous v0.5.5 markers are not reused. Start a new adventure, move each hero, heal above the original maximum if possible, take damage, complete rounds, then upload the three reports. Inspect Applied, Lost, Errors, each hero's baseline/target/lastMax/highestCurrent/aboveBaseline, and replacedStat. No direct healing or save restoration is provided. Discard the test adventure. The separate verified request mechanism and AP behavior are retained. Party-wide identity and gameplay behavior require runtime verification.

## New in v0.5.5 — verified one-shot health request (historical)

The v0.5.4 logs showed a request queued (`NextArmed=True`) at the end of one session, but the subsequent game launch reported `Armed=False`, `Consumed=False` and no health write. The exact reason the old config boolean did not survive startup is unknown. No evidence yet confirms or rejects the native health setter. AP remains unchanged (12 refunds, four resets, zero errors in the latest session).

This version retires `ArmNativeHealth054` and uses a separate, version-specific request file in the House rules module data folder. F10 > House rules > Hero health: select 125%, click **Next launch request** until **SAVED** appears, close the game completely, and relaunch once. The pending request is written and read back before SAVED is reported. Changing the selected percentage cancels a pending request. Clicking the button again cancels it. At startup the framework claims the pending file by moving it to `native-health-trial-055.claimed` before enabling any write, rejects an invalid or changed percentage, and logs the exact request outcome. The claimed file is an inert receipt, not an authorization for a subsequent launch. The button is OFF after claim by design; check the separate status **Armed: 125% (one hero)**. Do not click the button again during the test.

**Fresh disposable local solo adventure only.** The native maximum may persist in saves; do not load a prior or valuable save. Move a full-health unmodified hero, take damage, heal above its previous max if possible, and complete a round. Only one native health stat object can be attempted per process; no auto-reapplication, no party-wide health changes, no restoration on teardown. Upload `hero-health-probe.txt`, `house-rules-probe.txt` and `LogOutput.log`. The health report includes `ArmRequest=`, `Armed=`, `Consumed=`, `Applied=` and later verification counts. If the request is not found on startup, share the startup line and whether the pending/claimed file exists in the same module data directory. To cancel a pending request when the game is closed, remove only `native-health-trial-055.pending`. Do not edit the old arm boolean.

## New in v0.5.4 — one-shot native maximum health trial (historical)

The v0.5.3 computed getter returned 20 while the native stats facade remained 16. The rule correctly stopped. AP remained operational (49 refunds, 23 resets, zero errors). Both previous experimental health paths (temporary modifier churn and computed getter detour) are disabled in this version; their source remains in Git history.

This version tests the same verified native StatsFacade.TrySetStatMax/ TrySetStat methods used in earlier AP experiments, but with Health=2. It is **read-only unless explicitly armed**. F10 > House rules > Hero health: set 125%, then click Native trial next launch so it says ARMED. Restart and start a completely new disposable local solo adventure. The arm is consumed at launch. Move a full-health unmodified hero first. At most one native health object is changed during the entire process; injured heroes and existing modifier baselines are skipped. Current HP is preserved (the value setter is used only if needed to preserve original HP or roll back). The report verifies raw, effective and permanent maximum plus current HP and modifier count. A failed write attempts same-object rollback; failure can leave modified state. Subsequent observations are read-only, including after refills. No automatic reapplication, no teardown restoration and no changes to other heroes. This is a diagnostic trial, not a party-wide release.

**Native maximum may persist in saved adventures. Do not arm on any valuable or previously modified save. Discard the test adventure afterwards.** Re-arming and loading a modified adventure may compound its health. The framework cannot prove the save is fresh. Test whether the selected hero can actually heal above the original max and retain it through a round; upload hero-health-probe.txt, house-rules-probe.txt and LogOutput.log. If the trial verifies and is retained, a later revision can address party-wide identity and baseline handling. 100% does not undo previous native writes. AP code remains unchanged.


## New in v0.5.3 — computed health trial

The v0.5.2 logs verified the partial AP reset (one remaining AP refilled to three and five paid actions followed). AP code is unchanged. Health recalculation repeatedly removed the independent temporary modifier: 61 losses, 58 refreshes, ten throttles. The supplied assembly metadata shows StatsFacade.UpdateModifiers receives a status-effect list, so adding a standalone modifier after each recalculation is not a durable approach.

New **Computed health** option (on by default, 100% health remains disabled/read-only). When enabled, no native health modifiers or HP setters are used. A scoped postfix of Stat.get_Max scales the original integral native maximum by the chosen percentage, rounded up, for a health object already observed on an active local solo hero. The live object and session are checked per intercepted getter, with recursion suppression. The native result is measured with the hook suppressed, and both the facade and object maximum must report the target or the rule stops. Registration is session-scoped and cleared at teardown. The legacy temporary mode is retained behind the toggle; it is not used simultaneously.

This is a runtime experiment, not a confirmed fix for gameplay. Native IL2CPP internal calls may bypass a detour, and current HP may be clamped by other paths. Computed max may be observed only by some consumers. Use a fresh disposable solo adventure, 125% health, Computed health ON, restart, move each hero, heal a hero above its original maximum, take damage and complete rounds. Verify actual current HP, not just UI maximum. Upload hero-health-probe.txt, house-rules-probe.txt and LogOutput.log. Look for ComputedHook=True, ComputedVerified, ComputedHits, ComputedRejected=0 and Attempts=0. If it fails, 100% disables the rule; legacy mode remains experimental. Do not use valuable saves.

## New in v0.5.2 — longer health sessions and earlier AP refill recovery

The 0.5.1 user test verified all 24 health writes (five initial applications and 19 refreshes), with all five tracked objects at target on their last observation. The game still removed the temporary modifier 19 times. This version adds an exact, optional `StatsFacade.UpdateModifiers` postfix to refresh after native stat recalculation for already-observed active local heroes. It matches a live facade against a bounded session-local map, rechecks local solo scope, and retains AP observations as fallback if the hook is unavailable. No cached piece wrappers are kept across scope teardown. The temporary bonus is not made permanent and no direct max/current setter is used. Existing baseline/no-stacking/current-HP verification remains unchanged. Replace the 32 lifetime attempts per stat object with a rolling eight writes per five seconds per object. Throttling recovers at a later observation. This is not a guarantee of uninterrupted bonus health: native recalculation may still occur on untracked objects or through other paths; the hook's real execution and effect need testing. The 128 tracked-object limit is retained.

AP refund mode resets on verified same-cap partial-to-full native refills by default. Set `PartialAPRefills052 = false` in the house-rules configuration before restarting to restore the old empty-to-full-only policy. Full-to-full calls are deliberately not reset: the hook alone cannot distinguish a new turn from a duplicate. A native refill is not proven to be a turn boundary. No changes to the bonus grant amount or existing-cap accounting.

Test in a fresh disposable solo adventure, with 125% hero health and +3 AP refund mode. Take damage, complete multiple rounds, end a turn with one AP left and check whether the next round has five one-point actions. Upload hero-health-probe.txt, house-rules-probe.txt and LogOutput.log. Look for RecalcHook=True, RecalcMatched, source=modifier-update, Throttled and PartialRefundResets. Do not use valuable saves.

## New in v0.5.1 — bounded health refresh

The v0.5.0 125% test verified four applications, but three temporary modifiers disappeared. One object already had a native modifier (22 raw / 25 permanent and effective HP), so it was skipped. This version samples before an AP operation and applies/refreshes after successful spends or refills. Initial application requires full health and a consistent permanent baseline; existing native modifiers may be retained if raw/permanent/effective maximum and modifier count match the guard. The target is calculated from that permanent maximum, e.g. 25 becomes 32 at 125%. If a previously verified bonus disappears, it can be reapplied at the same baseline even while injured, without directly healing or changing permanent HP. Exact original raw/permanent max and modifier count must match, with at most 32 total attempts per native object per process. If the native result changes current HP or fails any check, further health writes stop. Do not use valuable saves. Temporary modifiers may still disappear between AP events, and changes in unrelated modifiers may prevent refresh. The observed count and aggregate values do not prove modifier identity; native copying, additive interactions and UI remain unverified. AP calculation unchanged.

Test 125% in a fresh disposable solo adventure. Move each hero, take damage, complete a round and move again. Upload hero-health-probe.txt and house-rules-probe.txt. Look for Refreshed and verified=True, and unchanged current health across each write. 100% remains read-only and does not remove a previously applied modifier.

## New in v0.5.0 — experimental hero maximum health

The v0.4.8 +3 AP refund mode was confirmed by the user: 17 refunds, eight budget resets, five paid one-point actions on multiple tracked objects, and no reported framework errors. Its calculation is retained.

F10 > House rules > **Hero health** offers 100% (default), 125%, 150%, 175% and 200%. Changes require restarting. The first health alpha attempts a native temporary additive health modifier on the first verified AP observation of a full-health, non-downed local solo hero with a consistent unmodified health stat. It rounds the target upward (10 HP at 150% becomes 15 max HP). It does not directly set current health or permanent maximum; the game decides how its native modifier affects current HP. One attempt per native health stat object per process, with no retry if the modifier disappears. Previously modified, injured or unverified states are skipped. If the native effect is not verified, further health writes stop independently of AP.

Use a fresh disposable solo adventure, start at 150%, move each hero once and inspect the displayed HP. Take damage, advance a round, and upload hero-health-probe.txt from the same module data folder as house-rules-probe.txt, plus LogOutput.log. This version is not verified for save persistence, modifier refresh or actual health behavior. 100% is read-only and does not remove a previously applied modifier; no blanket modifier removal is attempted. Do not use a valuable save.

The health metadata signatures and enum members were checked against the supplied generated Jade.GameModel assembly. Standalone health and AP test cases are included but require local .NET compilation.

## New in v0.4.8 — existing AP caps in refund mode

The v0.4.7 report showed +3 selected, four AP objects at permanent max 3, zero refunds and 32 skipped elevated-cap spends. Refund mode now honors **Adjust existing AP caps** (opt-in, disposable solo only). A verified cap of 3 with +3 selected receives at most two one-point refunds per tracked refill cycle, targeting five paid one-point actions. A normal cap of 2 receives three. Neither maximum nor permanent maximum is written by this path. The visible counter stays at its existing maximum. Caps above the selected target are not lowered; disable/repair of previously modified saves is not provided by this mode. Refills must be verified empty-to-full at the same cap; a change mid-cycle suspends refunds until such a refill. Early turn endings, native copies, and UI behavior still need runtime testing. Restart after changing settings. For this reported test, enable both AP refund mode and Adjust existing AP caps and select +3. A fresh disposable adventure is recommended; do not use a valued save.

## New in v0.4.7 — optional capped AP refunds

The cap-changing method remains available and unchanged by default. In a fresh disposable solo adventure, enable **AP refund mode** in F10 > House rules, select +1 to +3 and restart. It refunds a verified single-point spend from 2 to 1 back to 2, up to the selected budget per tracked native AP object. It never writes the maximum or permanent maximum. The normal counter remains at most 2, but the target is 3–5 one-point actions. It resets its budget only after a verified 0-to-2 refill; ending a turn with unused AP may therefore withhold later bonuses. Two-point actions and previously elevated AP caps are deliberately not refunded. Existing cap adjustment is ignored in this mode. Bonus 0 is read-only. The opening and subsequent turns, native state copies and game UI remain unverified; this is not a save-repair feature. Do not use a valuable save. The legacy +3 fix is also still awaiting in-game testing.

The standalone guard tests can be run with `dotnet run --project tests/CoreTests/CoreTests.csproj` using the .NET 8 SDK.

Unofficial, source-only developer preview for the Windows/Steam edition of Demeo x Dungeons & Dragons: Battlemarked. This is not endorsed by Resolution Games or Wizards of the Coast. No game assets or loader binaries are included.

## New in v0.4.6 — absolute AP target and opt-in existing cap adjustment

The v0.4.5 +3 report had four tracked objects already at max=permanentMax=3 with allowance=2, and zero new grants. The old guard only accepted max=2. This release keeps that conservative default and adds an explicit `Adjust existing AP caps` option (off by default). Only in verified local solo gameplay with allowance=2 and consistent full 3-5 AP stats (or a verified first paid action from full), the option treats these as old bonuses. It sets the absolute target 2 + configured bonus, minus any first action already spent, rather than stacking onto the existing cap. This can overwrite legitimate modifiers and is for disposable test adventures only. A successful adjustment is tracked for restoration to 2 when the remaining AP falls to 2 or below. Failed writes attempt to restore the exact pre-write cap and value; restoration is not guaranteed. Existing AP caps outside this narrow shape are not modified. A bonus of 0 remains read-only, even with adjustment enabled. Existing config key ExtraHeroAP044 is retained.

1. Back up saves, close the game, install using Install-BMF.cmd.
2. For a fresh disposable solo adventure set +3 AP in F10 > House rules, leave adjustment OFF, restart. Expected total 5 AP on eligible refills, or 4 remaining after the first paid action if the opening fallback fires.
3. To specifically test a disposable adventure with a previously elevated AP cap, turn `Adjust existing AP caps` ON in the House rules tab and restart. The option is also available as `[HouseRules] AdjustExistingAP046 = true` in the House Rules settings.cfg while the game is closed. Do not use it on a normal save.
4. Spend five one-point actions per hero, complete another round, and upload LogOutput.log and house-rules-probe.txt. Inspect AdjustedCaps, ElevatedSkipped and the grant target/observed values. Anonymous native stat objects need not correspond one-to-one to heroes.
5. To disable, choose +0 and turn adjustment OFF, then restart. This stops future changes but does not repair existing saves. Discard test adventures. Pending caps can remain on teardown and cloned objects may retain changes. The opening fallback is once per tracked object; round detection is not verified. No multiplayer or important saves.

## What is implemented

- BepInEx 6 IL2CPP entry point, module registration and duplicate ID rejection.
- Unity main-thread module callbacks: start, active scene changed (polled), update and stop.
- Separate configuration and log source for each module; enable/disable on next launch.
- Module-owned Harmony patch instance and cleanup on failure or shutdown; exact method resolution helper and opt-in native game build fingerprint check.
- Startup diagnostics, game version, SHA-256 of GameAssembly.dll and loaded assembly inventory.
- Metadata-only interop scanner to discover candidate game types and methods without executing them.
- Example BepInEx plugin that logs scenes without changing game state.
- PowerShell build/install scripts, and standalone lifecycle test program.

## New in v0.3.0

- Retains the six original presentation probes. The user's v0.2.1 logs show five invoked, one attached but not invoked, and successful startup. Reports now distinguish observed calls from attached hooks awaiting calls.
- Experimental desktop mod manager: F10 toggles the Unity IMGUI window. Installed mods tab lists registered BMF modules, current running state and next-launch enable state. Buttons save settings for the next launch only. Diagnostics tab lists core method call counters. This is not a VR UI and does not discover arbitrary BepInEx plugins. The menu does not pause or block all game input; test it at the main menu. The game may not deliver IMGUI events or hotkeys; this has not been tested.
- Seven additional core method entry probes: AP refill/subtract, card execution, damage, healing, turn advance, dice advantage. Default on for the exact native game fingerprint from the user's tested v0.2.1 build; otherwise skipped. Every method is independently matched to its metadata signature before patching. Counts are not evidence of authoritative state changes, successful actions, or correct network authority. No inputs/results are changed or collected. Test solo only.
- New module callback `OnCoreSignal(GameplaySignal signal)` reports coalesced core entries separately from presentation signals. Existing module API retained.

## Upgrade from v0.3.0 (existing installer procedure)

1. Close Battlemarked. Back up saves. Extract this ZIP to a separate folder, not into the game folder.
2. Double-click `Install-BMF.cmd`. It uses `D:\SteamLibrary\steamapps\common\Battlemarked`, builds framework and example before copying, and backs up existing DLLs. Existing configuration is retained.
3. Launch the game. At the main menu press F10, inspect Installed mods and Diagnostics, then close the menu. Test solo combat: move, play cards, end turns, attack, heal if possible. Exit normally.
4. Share the following files (check timestamp/version and review for private data):
   - `BepInEx/LogOutput.log`
   - `BepInEx/config/BattlemarkedModFramework/org.battlemarked.diagnostics.corelogic/core-probe.txt`
   - `BepInEx/config/BattlemarkedModFramework/org.battlemarked.diagnostics.gameplay/gameplay-probe.txt`
5. If F10 fails, close the game and set `[Menu] StartOpen = true` in `BepInEx/config/org.battlemarked.modframework.cfg`, then relaunch. If there is still no menu, check for `BMF desktop menu OnGUI callback observed` or warnings. To disable the menu set `[Menu] Enabled = false`.
6. If core hooks cause problems, close the game and set `[General] Enabled = false` in `BepInEx/config/BattlemarkedModFramework/org.battlemarked.diagnostics.corelogic/settings.cfg`. Alternatively reinstall the known-working v0.2.1 archive. If the game fails before config is generated, restore v0.2.1 by reinstalling it or remove both BMF DLLs from the BMF plugin folder. No game binaries are overwritten. Reports from disabled modules can be stale; compare timestamps and version.

## Requirements and limitations

- Windows Steam 64-bit, BepInEx 6 IL2CPP, .NET 8 SDK and .NET 6 targeting pack. This release additionally references the locally generated `UnityEngine.IMGUIModule.dll`. The inventory confirms that assembly exists in the scanned installation, but the GUI API and runtime callbacks are untested.
- v0.4.3 has been compiled and played by the user. v0.4.6 has received static checks, not compilation or in-game testing. Native crashes and incomplete restoration cannot be ruled out. The AP probe is read-only by default; no other house rules, multiplayer synchronisation, custom content injection or hot-unloading. Use solo tests and trusted modules only.

## Build fix in v0.2.1

The local v0.2.0 build reported nine CS0656 errors involving NullableAttribute and NullableContextAttribute at C# record declarations. All four records are now ordinary classes, with explicit value equality and deconstruction for the public SceneInfo and GameplaySignal types. Record-specific `with` syntax and generated record formatting are no longer available. Nullable analysis remains disabled for game projects. No changes to BepInEx or interop files are needed. The user subsequently confirmed successful compilation and in-game startup for v0.2.1.

## New in v0.2.0

- Built-in presentation diagnostics, based on the supplied interop scan, with exact raw metadata signature matching. Missing or ambiguous matches and patch exceptions are reported individually. Matching metadata is not proof of runtime compatibility.
- Six observed handler entry points: turn start/end, card execution, piece damage, dice spawn and encounter dice resolution. No game state is written and no arguments or results are collected. These counts may include replay or duplicate events; dice spawn is not a dice result.
- Modules can override `OnGameplaySignal(GameplaySignal signal)` to receive coalesced handler calls on the main thread. `Kind`, `CallsSinceLastUpdate`, and `TotalCalls` are available. No authoritative gameplay state is exposed yet.
- Double-click `Install-BMF.cmd` after extracting the whole ZIP and closing the game. The current launcher detects Steam libraries or asks for the game folder. It builds locally and installs only BMF and the example, backing up existing DLLs. Alternatively use the PowerShell steps below.
- After a solo combat test and normal exit, share `BepInEx/config/BattlemarkedModFramework/org.battlemarked.diagnostics.gameplay/gameplay-probe.txt` and `BepInEx/LogOutput.log`. Check for private details first. If startup fails, remove the framework and example DLLs as documented below. To disable probes before another run, set `EnablePresentationHooks = false` in the module settings.cfg (if generated), or remove the framework and example DLLs. No game binaries are changed.

## Installation

1. In Steam, right-click Battlemarked > Manage > Browse local files. Copy the actual directory path. The folder should contain `battlemarked.exe` and `GameAssembly.dll`.
2. Download the current Windows x64 Unity IL2CPP BepInEx build from https://builds.bepinex.dev/projects/bepinex_be (or see installation guide below). Extract it into the game folder, not into the game's `_Data` folder. Do not layer multiple mod loaders.
3. Run the game once normally and close it. Confirm `BepInEx/core/BepInEx.Unity.IL2CPP.dll` and `BepInEx/interop/UnityEngine.CoreModule.dll` exist. First start can take longer while interop is generated. If these are missing, troubleshoot the loader first.
4. Install the .NET SDK(s) from https://dotnet.microsoft.com/download. Extract this project somewhere separate from the game. Open PowerShell inside the project directory.
5. Run the following (replace the path with your actual game path). `-Install` copies the DLLs, and `-IncludeExample` opts into the harmless example. Without `-Install`, it builds only. The script backs up overwritten DLLs and preserves settings. If PowerShell blocks a script, review the source and use `Unblock-File -LiteralPath .\scripts\Build-Install.ps1` on the trusted extracted file; do not change global execution policy.

```powershell
.\scripts\Build-Install.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Battlemarked' -Install -IncludeExample
```

6. Start the game in solo mode. Search `BepInEx/LogOutput.log` (or the actual BepInEx log file) for `BMF ... loaded`, `BMF ready`, and `Example module ready`. Visit a different scene and check for `Active scene:`. Exit normally. See `BepInEx/config/BattlemarkedModFramework/startup-report.txt` for the game fingerprint. If no messages appear, check for load errors or missing dependencies. Do not assume successful installation merely because files were copied.

### Uninstall

Close the game and remove only `BepInEx/plugins/BattlemarkedModFramework/Battlemarked.ModFramework.dll` and `Battlemarked.Example.dll` if present, as well as any other plugins that require BMF. Backups are `.bak` files and do not load. To remove BepInEx itself, follow the loader's own uninstall guide; never delete original game files. Settings and reports may be kept or removed separately. An existing example DLL is rebuilt with the framework during installation. Uninstall it separately if unwanted.

## Discover game-specific methods

After the loader's first run, run:

```powershell
.\scripts\Scan-Game.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Battlemarked'
```

It writes `interop-report.txt` to the project folder (or use `-OutputPath`). To narrow the scan, add `-Filter 'dice|roll|card|damage'`. The report lists matching types, methods, parameter names and raw metadata signature blobs, and fields for matching types. Raw signature blobs aren't decoded C# signatures and do not prove a method's semantics; inspect the local wrapper in a .NET decompiler and validate the exact overload and runtime behaviour. Generic or inherited methods may require specialised resolution. Reports are local and are not automatically uploaded. Share the relevant report sections and `startup-report.txt` if requesting help; review for private details first. Do not redistribute proprietary game assemblies.

## Create your own mod

Copy `src/Example` to a new folder under `src`, rename the `.csproj` and classes, and change both the BepInEx plugin GUID and module ID. The relative reference to `../Framework/Battlemarked.ModFramework.csproj` remains valid. Build it with:

```powershell
dotnet build .\src\YourMod\YourMod.csproj -c Release '-p:GameDir=D:\SteamLibrary\steamapps\common\Battlemarked'
```

Copy only your mod's DLL into `BepInEx/plugins/YourMod/`. Keep one installed copy of the framework DLL. The BepInEx dependency ensures the framework loads first. Register from your `BasePlugin.Load()` using `Framework.Register(new YourModule())`. Registration can be queued from any thread, but module callbacks and patch setup run on the main thread. `OnStart` may run after the first scene becomes active, and active-scene polling does not detect every additive scene or transition within a single frame. `OnStop` may be called after a partial start, so make cleanup null-safe and idempotent. A failed or disabled module is not restarted until the next launch. Callback exceptions are logged and the failing module is stopped; this cannot contain native crashes or undo arbitrary game state changes.

The generated settings are at `BepInEx/config/BattlemarkedModFramework/<module-id>/settings.cfg`. Set `[General] Enabled = false` while the game is closed to disable that module on the next launch. Modules are trusted C# code and can bypass this lifecycle; the switch only controls BMF callbacks. Each module can use `context.Patches.Patch(...)` and `context.ResolveMethod(...)` once real targets have been validated. Guard gameplay patches with `context.IsVerifiedBuild(verifiedSha256)` and an independently verified offline-session check. A build hash check is opt-in, not automatically enforced. BMF installs six observational view-handler prefixes by default; it does not alter original arguments or results. Do not reuse another Harmony owner ID. Use `OnStop` for any non-Harmony cleanup; patches installed outside the module-owned Harmony instance are not automatically removed.

## Development roadmap (not implemented)

1. Confirm framework load and scene callback on the installed build; inspect the interop report.
2. Build version-specific adapters for cards, characters, enemies, dice, actions and turns, starting with read-only observation.
3. Add opt-in solo house rules (action count, health/difficulty, card limits) after validating authority and lifecycle.
4. Add a mod settings UI, content registration (cards, heroes, enemies) and custom encounter/campaign support only where the actual game data and engine permit it.
5. Consider private co-op compatibility only after confirming game terms, host authority, peer negotiation and deterministic synchronisation. No public matchmaking modifications.

## Verification commands

```powershell
dotnet run --project .\tests\CoreTests\CoreTests.csproj -c Release
dotnet build .\tools\InteropScanner\InteropScanner.csproj -c Release
```

These two projects do not need game assemblies. See `VALIDATION.md` for current status.

## References

- SteamDB technology listing: https://steamdb.info/app/3124340/info/
- BepInEx IL2CPP setup: https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html
- BepInEx BasePlugin API source: https://github.com/BepInEx/BepInEx/blob/master/Runtimes/Unity/BepInEx.Unity.IL2CPP/BasePlugin.cs
- Community Battlemarked loader precedent: https://github.com/McFredward/demeo-mixed-reality
- Original Demeo HouseRules (conceptual inspiration only; not ported): https://github.com/orendain/DemeoMods
