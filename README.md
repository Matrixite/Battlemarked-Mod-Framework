# Battlemarked Mod Framework (BMF) — v0.4.6-alpha

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
- Double-click `Install-BMF.cmd` after extracting the whole ZIP and closing the game. The launcher uses your known D: game location. It builds locally and installs only BMF and the example, backing up existing DLLs. Alternatively use the PowerShell steps below.
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

Close the game and remove only `BepInEx/plugins/BattlemarkedModFramework/Battlemarked.ModFramework.dll` and `Battlemarked.Example.dll` if present, as well as any other plugins that require BMF. Backups are `.bak` files and do not load. To remove BepInEx itself, follow the loader's own uninstall guide; never delete original game files. Settings and reports may be kept or removed separately. Omitting `-IncludeExample` on a later build does not remove a previously installed example; delete its DLL or disable its module config manually.

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
