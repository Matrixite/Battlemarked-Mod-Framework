# v0.6.6-alpha validation

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
