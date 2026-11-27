# v0.6.28-alpha validation

## v0.6.28 enemy relocation

Four new source regressions fail on v0.6.27 and pass with the direct two-stage map tool, live board and source identity revalidation, native request/coroutine binding, and cancellation lifecycle. Verified the IL2CPP metadata for ForceMovePiece and its ValueTuple<IEnumerator, IProgressMarker> return; the native ForceMoveSinglePieceActionExecutor consumes Item1 as its own return coroutine. Verified native direct teleport and no-correction modes and the GameLogicRunner request handler table. All 46 Dev Tools and release Python checks pass locally. There is no dotnet or Windows game SDK/runtime here: neither compilation nor a successful gameplay move is claimed.

Windows acceptance: enable Developer mode in a disposable local solo campaign, Refresh, Move on map, select a living 1×1 hostile creature and a distant free tile, Confirm move, then verify the same creature appears at that exact tile with the same identity/health and normal AI/turn-order behavior. Try another move, a blocked/occupied tile, a changed or dead source, a large enemy, a hero, a busy action, cancelling/F10, level change, and disabling Developer mode. Confirm no unconsumed request executes after cancellation, and multiplayer cannot dispatch. On failure inspect toolbar status and BepInEx LogOutput.log for the native binding/request reason.

## First stable release automation

The 12 Python release-gate and simulated publication tests pass locally. These cover the current alpha skip, exact stable-version eligibility, inconsistent version rejection, wrong branch refusal, mismatched tag refusal, versioned source archive and matching SHA-256 checksum, and repeated-run protection. The publication tests use a temporary local Git remote and mock GitHub CLI; they do not create a real release. The hosted workflow is configured to run the .NET 8 core tests before publication; dotnet and the Windows game environment are unavailable here. Real GitHub Actions execution and Windows/gameplay acceptance remain unverified. No framework version, gameplay behavior or updater code changes are included in this automation.

## v0.6.27 independent Hero Forms switch

The changed tab/dispatch regressions and three additional source tests fail against v0.6.26 and pass with the independent feature authorization. Tests cover the menu visibility, tick and toolbar path, developer-independent hero context/picker/request, retained read-only capture, disabled-request cancellation and continued developer-only spawn/removal guards. CoreTests matrix now expects Hero Forms visibility to follow only its switch for both Developer mode states. All 42 Python Dev Tools and release checks pass. The Windows game SDK and dotnet are unavailable in this runner; live verification remains pending: with Developer mode off and House rules running, toggle Hero Forms on/off in Mods and check visibility, refresh forms, select/confirm a hero in a disposable solo campaign; verify Level Editor/Dev Tools stay hidden and multiplayer cannot dispatch.

## v0.6.26 combined House Rules tab

Two new source checks fail against v0.6.25 and pass with the combined top-level House Rules tab, preserved other tab IDs and both sets of unchanged rule-control calls. Layout positions within each subpage fit the minimum supported height. CoreTests checks the combined tab follows the House rules module preference and the reserved old tab slot stays hidden. All 39 Python Dev Tools and release checks pass. The runner lacks dotnet and installed Windows interop assemblies; verify the new tab renders, its AP/Health selectors switch correctly, the existing controls work and the tab hides when House rules is disabled. No gameplay rule code changes.

## v0.6.25 independent Hero Forms visibility and request gate

Three added source contract tests fail against v0.6.24 and pass with the independent saved live config switch, separate searchable Hero Forms tab, menu/target lifecycle and native request gate. CoreTests includes a full developer/house-rules/hero-forms visibility matrix. On Windows, verify that enabling/disabling Hero Forms in Mods immediately shows/hides the tab with Developer mode enabled, the preference survives restart, the Level Editor still offers only ordinary spawning/removal, and hiding or leaving Hero Forms cancels unconsumed work. The existing transform acceptance test remains pending; disabling does not revert a completed transformation. No Windows game SDK or dotnet in this environment, so compilation and in-game menu/runtime behavior remain unverified.

## v0.6.24 experimental hero forms

Static inspection of the supplied verified IL2CPP metadata identifies SwapPieceHelper.TransformIntoNewPiece(Piece, PieceTypeId, PlayerId, Team, bool, CharacterSheetId, bool, bool, Action), the hero spawn processor's swap helper and the RequestHandlerDebugCommandFactory/Create/HandleRequest entry points. Disassembly confirms that the native transformation builds SpawnData, gets the existing owner, carries selected traits, invokes PieceSpawner.SpawnPieces, makes a reversible transform record, and uses PieceDespawner; the spawner grants registered cards. The native debug dispatcher returns null for an unregistered command, so a BMF nonce without a matching active hook cannot run the transformation. BMF does not edit piece fields, assets, saves or directly run Unity coroutines. Native simulation handler and helper captures must share GameState identity, and the simulation and HUD session/owner IDs must agree. Request consumption requires the same verified local session, currently active owned hero and registered target definition.

The three new source contract tests were red before implementation and pass afterward; existing map tests remain green. No installed Windows game/interop/SDK in this environment, so neither C# compilation against the user's interop set nor in-game transformation, move, attack, card hand, turn queue, death/revert or persistence is verified. Test in a fresh disposable solo campaign: enable Dev mode before starting, refresh the editor, toggle Hero forms, select a simple 1x1 creature such as GoblinFighter, target the current hero, confirm once; observe whether it appears and can actually move, spend AP, play cards and end turn. Check invalid enemy target, other hero's turn, stacked transform, cancel, scene change and multiplayer cannot dispatch. If the request is not observed, share the precise toolbar status and BepInEx log. Existing v0.6.23 user logs confirm the direct-map spawn/removal UI is working.

## v0.6.23 captured direct-map failures

User-provided BepInEx log from v0.6.22 shows successful loading, 162 eligible enemies / 463 scanned, followed by removal failing at StatsFacade.GetStat(PhysicalArmor) because the target has no such stat, and placement failing at the local request type check. The latter is after verified local room and tile validation; the native metadata interface table confirms both RequestSpawnPiece and RequestTryDealDamageToPiece implement native IRequest (type index 39925). Replace CLR IsAssignableFrom with a native TryCast and require the expected wrapper and equal native pointer; distinguish missing local room from request cast failures. The exact native TryGetStat(Type, out int, int) overload allows the optional armor stat to be absent with a zero default; reject unrelated read errors. The two regression checks fail against 0.6.22 and pass after the changes; all 31 Python source/release tests pass. Windows build and actual native request delivery remain unverified from this environment. Acceptance: update, enter disposable solo, select free tile and spawn, check actual piece appears; select eligible unarmored and armored enemies/props, confirm removal and observe result. Check BepInEx logs for the next precise failure if any.

## v0.6.22 direct map tools

Verified supplied metadata and native code: `Jade.Input.NonVR.BoardInteractionHandlerNonVR.Tick` and `TryGetHoveredTile(Ray, out TileSetHelpers.TileHoverData)` are present in GameView.dll; the handler holds current gameState, networkRoom, boardPresenter and cameraControlModel. The camera model's ActiveCamera exposes the Unity camera. The hit test performs a native physics raycast with game layer masks. The board presenter's audio/visual manager holds the native damage type and piece registries. The native DamageTypeId constant is Physical; DamageTypeWithProperties constructor substitutes an empty collection when the additionalTags reference is null. RequestTryDealDamageToPiece accepts a single element and PieceId. The source uses live HUD/board/room identity, bounded stats and explicit confirmation; removal is damage/destruction, not arbitrary object deletion. No Windows generated interop SDK available and user's Desktop Commander device remains offline. All 29 Python Dev Tools and Release tests pass; no live compile or gameplay verification.

Acceptance on disposable local solo: enable Developer mode, refresh editor, select an enemy and Place on map. Toolbar exposes the board; middle-click a visible free tile, confirm, verify exactly one intended spawn and repeat on another tile. Click invalid/outside-board, occupied/blocked, toolbar, then cancel; confirm no request. Remove on map, middle-click an enemy or destructible prop, confirm, observe actual result and game effects. Try hero, NPC, POI, scenery, moved target, busy game, scene transition, disabled developer mode, closed editor and multiplayer: no removal dispatch. Check F10 cancels before queued map command processing. If unavailable, consult toolbar status or BepInEx LogOutput.log for exact native binding reason.

## v0.6.21 level editor empty enemy list

User screenshot shows BMF 0.6.20 in a loaded verified board, 8×8 tile grid, selected tile (0,7) and zero enemies. The exact status is the branch for no matching spawner capture. The precise reason the constructor capture was absent cannot be determined from the screenshot alone. Metadata proves HeroSelectModel has a live pieceRegistry field; AffectedPiecesConfig derives UnityEngine.ScriptableObject; PieceAIBehaviourDefinition derives System.Object. The previous AI check would independently reject valid non-Unity native reference objects. Use the HUD registry in the already-verified context and an exact matching or unambiguous loaded config, then revalidate registration, native classification, room and registry identity for dispatch. New source contract regressions fail before the fix and pass afterward; all 27 Python source/release tests pass. No Windows SDK/interop assemblies or live game available here. Acceptance: update to 0.6.21, enter disposable local solo, refresh, confirm a nonzero eligible count; select one free tile and eligible enemy, confirm once, observe actual result. If count remains zero, collect status text and BepInEx LogOutput.log to identify the next native binding problem. Preserve false/unknown scope refusal.

## v0.6.20 runtime Level Editor

Static inspection of the supplied verified GameAssembly and matching IL2CPP metadata identifies the retail `Jade.LevelEditor.LevelEditor` component, board and tile helpers, the native enemy classification/registry and the normal spawn request. The new framework-owned editor uses exact method signatures from that metadata. Its source regression tests cover developer-only visibility, current HUD-backed local-room proof, bounded tile reads, explicit confirmation, scope/tile/definition/queue revalidation before one request, and no direct spawn or level-asset mutation. All 25 Python Dev Tools and release tests pass. The .NET core test matrix was extended for tab visibility, but this runner lacks the SDK and installed Windows-generated interop assemblies. Neither compile nor native runtime behavior is verified here.

Acceptance on Windows: update from the user-confirmed working v0.6.19, check v0.6.20 in F10, enable Developer mode before starting a disposable local solo campaign with House rules running, then open Level Editor. Refresh the board, pan across boundaries, select different occupied/free/missing tiles and compare coordinates/occupancy against the game. Search for a registered enemy, confirm once on a free tile, verify the game actually spawns the intended creature and refresh. Check blocked/occupied tiles, busy room, unknown build, multiplayer, closed tab, disabled developer mode and changed level/session cannot dispatch. Check AP/health/Dev Tools remain functional. No actual spawn is claimed until this test succeeds.

## v0.6.19 static model access

The user's v0.6.18 Windows build-details.log reports restore success, zero warnings and exactly three CS0176 errors at HouseRules.cs lines 679, 684 and 712. The static field `HouseRules.model` was incorrectly accessed through `r.model` in the new card-grant guard. The correction explicitly accesses the static field and retains the captured-model identity test. The source regression test fails against the old three references and passes after the fix. The user subsequently confirmed v0.6.19 works; existing Dev Tools runtime acceptance checks remain pending.

## v0.6.18 build diagnostics

User-provided v0.6.17 transcript confirms the update worker reached installation and the framework build returned failure; .NET SDKs 10.0.404 and 11.0.100 were detected and the game fingerprint matched. The transcript contains no compiler/restore diagnostics, so the precise cause of the unsuccessful build is still unknown. Source regression checks verify explicit native stdout/stderr redirection, reporting before exit-code handling, and failure before DLL replacement. Windows acceptance: trigger a build failure in a disposable test installation (e.g. an invalid source file), confirm build-details.log contains native error text and exit code and no installed DLL was replaced; then build valid source and confirm installation. PowerShell and the Windows interop set are unavailable in the development environment.

## v0.6.17 interop compiler compatibility

User reported v0.6.16 queues but v0.6.14 remains installed after relaunch. The existing worker builds before replacing any DLL; the exact failing step is unknown without the installer transcript. v0.6.16 had two class records, reintroducing the compiler compatibility issue previously reported with the user’s interop set. The new source-level check fails on those two records in v0.6.16 and passes after replacing them with ordinary classes. The two updated reflection binding searches also avoid captured lambdas, preserving exact, unambiguous matching. This is not a Windows compilation or runtime test. Confirm the next queued installation reports success, the menu shows v0.6.17, and Dev Tools opens with the browser pages. If not, collect `BepInEx/BMF-Updates/update-install.log` and `last-update.txt`.

## v0.6.16 debug workspace

Source regression checks cover the independent developer workspace, search and explicit refresh, scene/visibility cleanup, one-shot confirmation and Update dispatch, verified native room and authoritative piece lookup, registry verification, selection identity guards, and the retained native-menu readiness guard. The supplied verified IL2CPP metadata confirms the browser types/getters, RequestGiveCard constructor, LocalNetworkRoom.Send and queue getter, BoardHelpers.GetPiece, and the request handler's gameState/cardRegistry fields. Native disassembly confirms the handler resolves card definitions through its CardRegistry. The card command fails closed if its optional constructor capture is unavailable or the HUD model does not share the captured game-state identity.

Windows compilation and live game acceptance are pending. In a disposable local solo session with backups: enable Developer mode before starting a campaign (House rules enabled); open Dev Tools, Refresh each browser, test filtering and pagination, select a board hero then a card, confirm one grant, check the actual hand and the BepInEx log. Verify a busy room, nonhero, stale selection, different scene, unverified build and online/multiplayer session cannot dispatch a grant. Check disabling Developer mode cancels selections/captures, verify the rest of F10 remains usable, and confirm the original Native page still reports the retail restriction. Events require the corresponding diagnostic modules to be running. Source tests do not substitute for a Windows build or runtime test.

## v0.6.15 native menu retail restriction

Static inspection of the supplied verified GameAssembly shows Jade.DebugMenu.DebugMenu.Initialize allocates NopFidelityDebugMenuFacadeImpl, hides the root, and does not initialize DebugTools. The compiled GameDesignDebugUIMenu.get_IsEnabled returns false and set_IsEnabled is a return-only stub. The new runtime readiness guard must reject an uninitialized/no-op facade before calling ShowDebugMenu. Source regression tests cover the ordering and diagnostic message. Windows compilation and runtime verification are still pending. Acceptance: press Show game debug menu in Dev Tools; the retail limitation (or a precise missing-object/binding reason) should be shown without closing BMF. Confirm no new exception in BepInEx log.

## v0.6.14 Dev Tools (experimental)

Confirmed the uploaded GameAssembly SHA-256 matches the existing verified-build guard. Metadata and native code inspection identify a Unity component, `Jade.DebugMenu.DebugMenu`, with compiled no-argument ShowDebugMenu and HideDebugMenu methods. BMF only invokes these methods on an existing, unambiguous loaded scene instance. Source regression tests pass; the CoreTests visibility matrix includes Dev Tools. Runtime activation, the existence of a scene instance in the installed player, and Windows compilation remain unverified.

Acceptance: with Developer mode OFF, Dev Tools is absent; turn it ON and verify Dev Tools appears regardless of House rules settings. In a disposable local solo game, use Show game debug menu; the native menu should appear and BMF should close, or an unavailable reason should be displayed. Use F10 to reopen BMF, hide the debug menu, and verify the native UI closes. Repeat across a scene change and turn Developer mode OFF to verify the framework-owned native menu closes and Dev Tools disappears. Check that Mods, Reports, Updates and existing rules still work. No automatic release is made for this alpha.

## v0.6.13 update warning

Source checks verify the warning is present on Updates, uses enlarged bold yellow labels without altering the shared skin style, and fits within the minimum manager height. If style construction fails, plain uppercase labels remain. Windows compilation and visual verification are pending because dotnet is unavailable here. In-game acceptance: open Updates at normal and minimum supported window sizes, verify the warning and all buttons are readable, and confirm Steam shows Play before relaunching after a queued update.

## v0.6.12 rollback

Restores the complete v0.6.10 source snapshot except for synchronized version declarations and rollback documentation. The v0.6.11 main-menu lifecycle hooks and factory API are removed. Source comparison verifies the rollback; Windows compilation and runtime verification remain pending. Acceptance: install from Updates, restart and verify 0.6.12, then confirm module switches indicate next launch and the AP/Health/Presets and Reports tab visibility behavior is unchanged.

## v0.6.10 Presets visibility

Extended the existing four-combination CoreTests tab visibility matrix to cover Presets with House rules disabled/enabled and Developer mode disabled/enabled. Static checks pass; executable tests and Windows compilation are pending because dotnet is unavailable here. In-game acceptance: disable House rules and confirm AP rules, Health and Presets disappear, then re-enable and confirm all return, with both developer modes. Mods, Overview and Updates stay accessible; Reports is still developer-only.

## v0.6.9 house-rule tab visibility

Static checks verify the menu uses the current House rules module enable preference, preserves tab IDs and keeps Reports independent. CoreTests covers all four combinations of the two preferences. The .NET SDK is unavailable in the development workspace, so executable tests and Windows build remain pending. In-game acceptance: disable House rules in Mods and verify AP rules and Health disappear immediately, with Developer mode both off and on; re-enable and verify they reappear. Restart with the module disabled and confirm the same visibility, then re-enable and restart to restore gameplay controls. Confirm Presets, Mods and Updates still open correctly.

## v0.6.8 player-facing tabs and update wording

Source-level checks cover filtering tabs by developer mode, stable tab identities, full-width layout, and provider-neutral status/result display. Added CoreTests assertions for both modes and provider strings, including uppercase and provider hostnames. The .NET SDK is unavailable here; executable tests and Windows compilation are pending. In-game acceptance: toggle Developer mode repeatedly under Mods and confirm Reports appears/disappears immediately, Updates opens the correct page in both modes, and report export still works when enabled. Check for updates and confirm no provider name/repository appears in the update UI. Verify previous update status, automatic check/install toggles, and existing update workflow.

## v0.6.7 developer module visibility

Source-level checks verify the default-off preference, exact diagnostic IDs and filter-before-pagination wiring. CoreTests includes coverage of all three diagnostic IDs and ordinary/third-party IDs in both modes. The .NET SDK is unavailable in the development workspace, so compilation and executable tests remain pending. Windows acceptance: use F10 > Mods with Developer mode OFF (only normal modules appear), toggle ON (all installed diagnostics appear), toggle OFF again (hidden immediately, normal modules remain), restart once with each preference to confirm persistence. Leave module settings unchanged during the visibility test; confirm diagnostic reporting still works. An absent optional Scene logger should not appear.

## v0.6.6 diagnostics

Added a local support archive with a fixed allowlist, per-file length cap, report-version/timestamp manifest, missing-file reporting and atomic publication. Tests cover absent and oversized reports, older version identification and exclusion of settings/ledger. Source review only: dotnet is unavailable in the workspace. No save or gameplay writes occur during export. In-game acceptance: export from F10 > Reports, verify the archive contains the expected diagnostic text and no settings, ledger or saves. Inspect the contents before sharing.

User confirmations: v0.6.2 built and worked, automatic update to v0.6.3 worked, and the v0.6.5 Steam STOPPING correction worked. The antivirus warning during the v0.6.4 update is not diagnosed; v0.6.4 loaded. Native-health repeatable trial remains to be tested. Do not mark the framework stable until remaining safety checks are verified.

## v0.6.5 shutdown / build-server isolation

User reports Steam continues showing STOPPING after leaving Battlemarked. Source review identified that the post-exit updater builds through dotnet with reusable servers enabled. The installer now passes --disable-build-servers, -p:UseSharedCompilation=false and -nr:false on every project build; these are scoped to the build command and do not stop anyone's unrelated processes. The user observed v0.6.4 loaded; no process listing or updater transcript was available to confirm the cause of STOPPING. Static checks only: Windows PowerShell and dotnet are not installed in the development environment.

Acceptance: update from v0.6.4, allow the post-exit installer to complete, then verify Steam returns to Play. Relaunch v0.6.5, exit normally without requesting an update, and confirm Play returns promptly as well. If it remains STOPPING, record whether battlemarked.exe is still running and retain the update transcript and BepInEx log for investigation.

## v0.6.4 window movement

Added standalone position tests for first-open centering, pointer offset, bounded dragging on every edge, persistence, resolution changes and click-without-drag. The local environment has no .NET SDK; C# test execution and in-game IMGUI behavior are pending. User confirmed the previous automatic update from v0.6.2 to v0.6.3 worked.

Windows acceptance: update via F10 > Updates, then drag only the title bar. Verify the whole manager moves with the pointer, cannot be dragged off-screen, and that buttons/tabs/F10 still work. Close and reopen F10, restart the game, and confirm the position is retained. Resize the game if possible; the manager should stay visible. AP/health and updater code remain unchanged.

## v0.6.3 end-to-end update test

The user confirmed v0.6.2 works. This release changes only the four version declarations and documentation, leaving the updater, AP and health implementation and Windows installer untouched. End-to-end update verification is pending at publication, not claimed successful.

1. In the installed v0.6.2 game, press F10 > Updates > Check for updates now. The available version should be 0.6.3.
2. Click Download and install when game closes. Wait for the status to confirm the worker has queued the update, then exit Battlemarked normally.
3. Wait for the installer to finish. Relaunch only after the worker reports success.
4. Confirm the installed version is 0.6.3 and that GitHub reports no newer version. Check that settings are retained.
5. If anything fails, retain BepInEx/BMF-Updates/last-update.txt, update-install.log and BepInEx/LogOutput.log for diagnosis.

## v0.6.2 build correction

User v0.6.1 build: restore succeeded using installed SDKs 10.0.404 and 11.0.100; compilation failed at FrameworkUpdater.cs:169, CS0656, missing NullableAttribute constructor. The failing statement used a captured lambda to call DownloadStage. The fix uses an explicit named StageRequest with a readonly offer and an explicitly typed Func<Task<Stage>> delegate. No other updater logic, gameplay logic or installer code changes. The local source check confirms removal of the captured lambda; the exact Windows compiler/IL2CPP environment is not available here. Re-run Install-BMF.cmd on v0.6.2 and confirm compilation before claiming the fix works.

## Updater validation

The user's v0.6.0 logs confirm successful startup, F10 callback, Relaxed preset (+1 AP), four verified refunds and four resets with zero framework errors. Native health was not queued in that run, so repeatable health trial validation is still pending.

v0.6.1 deliberately leaves house-rule logic unchanged. Standalone tests cover strict numeric version ordering, downgrade rejection, pinned commit URLs, manifest parsing and unsafe archive path rejection. Static source review and diff checks performed; the development workspace does not have dotnet or Windows PowerShell. No compile, actual GitHub download, or Windows install is certified.

Windows acceptance test:
1. Install v0.6.1 manually, launch, confirm F10 > Updates reports 0.6.1 installed and no newer version while the GitHub manifest is 0.6.1. Switch automatic checking off/on; verify settings persist.
2. When a newer tested version is published with matching manifest and project constants, check for it. Confirm correct newer version; click Download and install when game closes. Wait for queued status; verify current game continues to run.
3. Exit normally and wait for PowerShell to finish. Confirm last-update.txt reports success and backup manifest exists. Relaunch and confirm both installed framework and existing example report the new version. Verify configuration and health ledger were retained.
4. On a disposable installation, repeat a queued update with cancellation; verify no DLLs changed. Simulate network loss or a build failure; verify error reporting and previous DLLs remain intact. Re-enable opt-in automatic installation and test a subsequent version bump.
5. Verify a same-version or older manifest does not offer an update. Do not launch while the worker is installing. Do not use a valuable adventure for regression testing.

## Baseline evidence

In the user's v0.5.13 fresh local solo session, AP recorded 12 verified refunds and four resets with no framework errors. All four heroes received verified health increases (16→20, 25→32 with raw 22→29 and +3 permanent additive modifier preserved, 20→25, 16→20). All four healed to target; 46/46 subsequent observations retained the maximum. This is evidence for **v0.5.13**, not confirmation of v0.6 runtime behavior.

## v0.6 changes and checks

- Exact game fingerprint, room/host/local player and active-hero eligibility, native setters, additive modifier verification, rollback and AP accounting retained.
- A sealed party can be replaced only when a new explicit queue is claimed from the fresh campaign hook. The original scope remains sealed; four heroes maximum per trial.
- Native maxima are journaled before any write; known baseline mismatches and corrupt/unsavable ledger data fail closed. This does not independently prove that every unknown ID or old save is clean.
- Added standalone regression assertions for new-campaign eligibility, bound-room rejection, repeat baselines, cross-launch compounding rejection, changed pointer baseline, raw identity privacy, missing/corrupt journal, and preset matching/limits.
- Static diff/structure review performed. The .NET SDK and Windows PowerShell are unavailable in the development workspace; build, standalone C# tests, PowerShell execution, and in-game behavior are **not verified**.

## Required Windows validation before marking stable

1. Back up saves separately. Close the game; run Check-BMF.cmd and Install-BMF.cmd on the exact verified game build. Confirm no compile errors and a backup manifest in BepInEx/BMF-Backups. Run the standalone suite with:
   ```powershell
   dotnet run --project tests/CoreTests/CoreTests.csproj
   ```
2. Press F10. Check six tabs, profile labels and module next-launch state. Choose Tested party (+3 AP / 125% HP). Confirm health is not armed by this action and refund mode is active or pending idle.
3. Queue health manually. Start a new disposable local solo game, move all four heroes, take damage, heal, finish a turn. Expect verified targets and 12 refunds/four resets as in v0.5.13 for the same party.
4. Return to the main menu **without restarting**. Queue health again and start another genuinely new disposable local solo game. Check Claims=2 and prior-trial counts; each known hero must either match its baseline and receive the same target or be skipped with a baseline rejection. No compounding.
5. Queue then load an existing disposable campaign instead: no health authorization/writes. Select a different preset while queued: queue cancelled. Never test on a valuable save or in multiplayer.
6. Refresh Reports. Check compatibility-report.txt, hero-health-probe.txt, house-rules-probe.txt, BepInEx/LogOutput.log. Confirm no framework errors, no lost observations, no rollback failure. Share reports after checking for private data.
7. With the game closed, verify the DLL restore workflow on a test installation (both known DLLs, config preserved, checksums match). Restoration of DLLs is not restoration of saves.

Stop if verification fails; do not use affected adventures. Preserve the baseline ledger for investigation. The installer never edits or backs up game saves.
