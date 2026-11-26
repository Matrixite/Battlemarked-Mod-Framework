[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$GameDir,
    [string]$ExpectedVersion,
    [switch]$Install,
    [switch]$IncludeExample,
    [switch]$CheckOnly,
    [switch]$RestoreLatest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($ExpectedVersion) {
    if ($ExpectedVersion -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') { throw 'Invalid expected update version.' }
    [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    if ([string]$props.Project.PropertyGroup.Version -cne $ExpectedVersion) { throw 'Source version does not match the requested update.' }
}
$verifiedHash = 'C195E177AB5986C26D9E168517A2712DB451A35B79847768883550B05B829280'
$names = @('Battlemarked.ModFramework.dll', 'Battlemarked.Example.dll')
if (($CheckOnly -and ($Install -or $RestoreLatest)) -or ($Install -and $RestoreLatest)) { throw 'Choose one operation.' }
function Is-GameDirectory([string]$path) {
    return ($path -and (Test-Path -LiteralPath $path -PathType Container) -and (Test-Path -LiteralPath (Join-Path $path 'battlemarked.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $path 'GameAssembly.dll') -PathType Leaf))
}
if (!$GameDir) {
    $libraries = @()
    foreach ($key in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')) {
        $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($entry.SteamPath) { $libraries += $entry.SteamPath }
        if ($entry.InstallPath) { $libraries += $entry.InstallPath }
    }
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($programFiles) { $libraries += (Join-Path $programFiles 'Steam') }
    if (Test-Path -LiteralPath 'D:\SteamLibrary') { $libraries += 'D:\SteamLibrary' }
    foreach ($steam in @($libraries | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $steam -PathType Container)) { continue }
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf -PathType Leaf) {
            foreach ($match in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $library = $match.Groups[1].Value.Replace('\\','\')
                if (Test-Path -LiteralPath $library -PathType Container) { $libraries += $library }
            }
        }
    }
    $candidates = @($libraries | Select-Object -Unique | ForEach-Object {
        if (Test-Path -LiteralPath $_ -PathType Container) {
            $candidate = Join-Path $_ 'steamapps\common\Battlemarked'
            if (Is-GameDirectory $candidate) { (Resolve-Path -LiteralPath $candidate).Path }
        }
    } | Select-Object -Unique)
    if ($candidates.Count -eq 1) { $GameDir = $candidates[0] }
    else {
        if ($candidates.Count -gt 1) { Write-Host 'Multiple installations found:'; $candidates | ForEach-Object { Write-Host $_ } }
        $GameDir = (Read-Host 'Enter Battlemarked game folder (Steam > Manage > Browse local files)').Trim().Trim('"')
    }
}
if (!(Is-GameDirectory $GameDir)) { throw 'Select the game folder containing battlemarked.exe and GameAssembly.dll.' }
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Host "Game directory: $GameDir"
$hash = (Get-FileHash -LiteralPath (Join-Path $GameDir 'GameAssembly.dll') -Algorithm SHA256).Hash
Write-Host "GameAssembly SHA256: $hash"
if ($hash -ne $verifiedHash) { Write-Warning 'Unverified game build. Native house-rule writes remain disabled.' }
else { Write-Host 'Game fingerprint matches the verified v0.5.13 target.' }

$dest = Join-Path $GameDir 'BepInEx\plugins\BattlemarkedModFramework'
$backupRoot = Join-Path $GameDir 'BepInEx\BMF-Backups'
function Assert-GameClosed {
    if (Get-Process -Name battlemarked -ErrorAction SilentlyContinue) { throw 'Close Battlemarked before changing installed DLLs.' }
}
function Open-InstallGate {
    # Manual installs/restores and the automatic worker share this DLL-copy gate.
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    return [System.IO.File]::Open((Join-Path $backupRoot 'install.lock'), [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
}
function Assert-UpdateIsNewer {
    if (!$ExpectedVersion) { return }
    $installedDll = Join-Path $dest $names[0]
    if (!(Test-Path -LiteralPath $installedDll -PathType Leaf)) { throw 'Installed framework missing; use manual installation.' }
    $installedVersion = [Reflection.AssemblyName]::GetAssemblyName($installedDll).Version
    if ($installedVersion -ge [version]($ExpectedVersion + '.0')) { throw 'Installed version is already equal or newer; automatic update skipped.' }
}
function New-Snapshot {
    $folder = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $records = @()
    foreach ($name in $names) {
        $target = Join-Path $dest $name
        $exists = Test-Path -LiteralPath $target -PathType Leaf
        $sha = ''
        if ($exists) {
            $copy = Join-Path $folder $name
            Copy-Item -LiteralPath $target -Destination $copy
            $sha = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
            if ($sha -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw "Backup verification failed: $name" }
        }
        $records += [pscustomobject]@{ Name=$name; Existed=[bool]$exists; SHA256=$sha }
    }
    # Written last; incomplete snapshots are not advertised as restorable.
    @{ Format=1; Files=$records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'manifest.json') -Encoding UTF8
    Write-Host "Backup: $folder" -ForegroundColor Cyan
    return $folder
}
function Read-Snapshot([string]$folder) {
    $manifest = Get-Content -LiteralPath (Join-Path $folder 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Format -ne 1 -or @($manifest.Files).Count -ne $names.Count) { throw 'Invalid backup manifest.' }
    foreach ($name in $names) {
        $record = @($manifest.Files | Where-Object { $_.Name -ceq $name })
        if ($record.Count -ne 1 -or $record[0].Existed -isnot [bool]) { throw "Invalid backup record: $name" }
        if ($record[0].Existed) {
            $file = Join-Path $folder $name
            if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $record[0].SHA256) {
                throw "Backup checksum mismatch: $name"
            }
        }
    }
    return $manifest
}
function Restore-Snapshot([string]$folder) {
    $manifest = Read-Snapshot $folder
    foreach ($name in $names) {
        $record = @($manifest.Files | Where-Object { $_.Name -ceq $name })[0]
        $target = Join-Path $dest $name
        if ($record.Existed) {
            Copy-Item -LiteralPath (Join-Path $folder $name) -Destination $target -Force
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $record.SHA256) { throw "Restore verification failed: $name" }
        }
        elseif (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
    }
}
if ($RestoreLatest) {
    Assert-GameClosed
    $latest = Get-ChildItem -LiteralPath $backupRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json') -PathType Leaf } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (!$latest) { throw 'No complete v0.6 backup found.' }
    $null = Read-Snapshot $latest.FullName
    if ($PSCmdlet.ShouldProcess($dest, "Restore previous DLL snapshot $($latest.Name)")) {
        $installGate = Open-InstallGate
        try {
            Assert-GameClosed
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            $current = New-Snapshot
            try { Restore-Snapshot $latest.FullName }
            catch {
                $failure = $_
                try { Restore-Snapshot $current } catch { Write-Warning "Rollback failed. Keep snapshot $current for manual recovery." }
                throw $failure
            }
            Write-Host 'Previous DLL snapshot restored. Configuration and game saves untouched.'
        }
        finally { $installGate.Dispose() }
    }
    return
}
foreach ($relative in @('BepInEx/core/BepInEx.Unity.IL2CPP.dll','BepInEx/core/0Harmony.dll','BepInEx/core/BepInEx.Core.dll','BepInEx/core/Il2CppInterop.Runtime.dll','BepInEx/interop/UnityEngine.CoreModule.dll','BepInEx/interop/UnityEngine.IMGUIModule.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $GameDir $relative) -PathType Leaf)) { throw "Missing $relative. Install BepInEx 6 IL2CPP x64 and launch once to generate interop." }
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 8 SDK and .NET 6 targeting pack/SDK.' }
$sdks = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or !$sdks.Count) { throw 'No .NET SDK detected.' }
Write-Host "Detected .NET SDKs: $($sdks -join '; ')"
if ($CheckOnly) {
    Write-Host 'Basic prerequisites detected. A successful build and runtime test are still required.'
    return
}
if ($Install) { Assert-GameClosed; Assert-UpdateIsNewer }
$projects = @(@{Project='src/Framework/Battlemarked.ModFramework.csproj'; Dll=$names[0]})
# Keep an already-installed example in sync.
if ($IncludeExample -or ($Install -and (Test-Path -LiteralPath (Join-Path $dest $names[1]) -PathType Leaf))) {
    $projects += @{Project='src/Example/Battlemarked.Example.csproj'; Dll=$names[1]}
}
foreach ($p in $projects) {
    $project = Join-Path $root $p.Project
    & dotnet build $project -c Release "-p:GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    $dir = Split-Path $project -Parent
    $p.Output = Join-Path $dir "bin/Release/net6.0/$($p.Dll)"
    if (!(Test-Path -LiteralPath $p.Output -PathType Leaf)) { throw "Missing build output: $($p.Dll)" }
    if ($ExpectedVersion) {
        $built = [Reflection.AssemblyName]::GetAssemblyName($p.Output).Version
        if ($built.ToString(3) -cne $ExpectedVersion -or $built.Revision -ne 0) { throw "Built DLL version does not match $ExpectedVersion : $($p.Dll)" }
    }
}
if (!$Install) { Write-Host 'Build only. Add -Install to copy DLLs to your game.'; return }
Assert-GameClosed
if ($PSCmdlet.ShouldProcess($dest, 'Install build outputs with rollback snapshot')) {
    $installGate = Open-InstallGate
    try {
        Assert-GameClosed
        Assert-UpdateIsNewer
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        $snapshot = New-Snapshot
        try {
            Assert-GameClosed
            foreach ($p in $projects) {
                $target = Join-Path $dest $p.Dll
                Copy-Item -LiteralPath $p.Output -Destination $target -Force
                if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $p.Output -Algorithm SHA256).Hash) {
                    throw "Installed checksum mismatch: $($p.Dll)"
                }
            }
        }
        catch {
            $failure = $_
            try { Restore-Snapshot $snapshot } catch { Write-Warning "Rollback failed. Keep snapshot $snapshot for manual recovery." }
            throw $failure
        }
        Write-Host "Installed to $dest. Existing configuration, health ledger and game saves were not changed."
    }
    finally { $installGate.Dispose() }
}
