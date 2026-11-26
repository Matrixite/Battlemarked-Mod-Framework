# v0.6.1-alpha validation

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
