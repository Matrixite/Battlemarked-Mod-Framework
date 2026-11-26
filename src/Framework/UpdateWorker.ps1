# Embedded in the installed framework. The worker runs separately from Unity,
# waits for the game to close, then invokes the existing transactional installer.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [Parameter(Mandatory=$true)][string]$SourceDir,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Commit,
    [Parameter(Mandatory=$true)][int]$GamePid
)
$ErrorActionPreference = 'Stop'
$versionPattern = '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$'
if ($Version -cnotmatch $versionPattern -or $Commit -cnotmatch '^[0-9a-f]{40}$' -or $GamePid -le 0) { throw 'Invalid update request.' }
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$updates = Join-Path $GameDir 'BepInEx\BMF-Updates'
$stage = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$expectedSource = Join-Path $stage "Battlemarked-Mod-Framework-$Commit"
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
if ($SourceDir -ne $expectedSource -or (Split-Path $stage -Parent) -ne $updates) { throw 'Update staging path mismatch.' }
$manifest = Get-Content -LiteralPath (Join-Path $SourceDir 'bmf-update.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.version -cne $Version) { throw 'Update manifest mismatch.' }
$installer = Join-Path $SourceDir 'scripts\Build-Install.ps1'
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer missing from staged source.' }
$cancel = Join-Path $stage 'cancel.request'
$result = Join-Path $updates 'last-update.txt'
$log = Join-Path $updates 'update-install.log'
$gate = [System.IO.File]::Open((Join-Path $updates 'install.lock'), [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
$transcript = $false
$exitCode = 0
function Set-Result([string]$message) {
    Set-Content -LiteralPath $result -Value $message -Encoding UTF8
    Write-Host $message
}
try {
    Start-Transcript -Path $log -Force | Out-Null
    $transcript = $true
    Write-Host "BMF v$Version | GitHub commit $Commit"
    Set-Result "v$Version queued: waiting for Battlemarked to close."
    Set-Content -LiteralPath (Join-Path $stage 'worker.ready') -Value 'ready' -Encoding ASCII
    # Never terminate the game or race a second Battlemarked process.
    while ($true) {
        if (Test-Path -LiteralPath $cancel -PathType Leaf) {
            Set-Result "Update v$Version cancelled."
            return
        }
        $parent = Get-Process -Id $GamePid -ErrorAction SilentlyContinue
        $games = @(Get-Process -Name battlemarked -ErrorAction SilentlyContinue)
        if ((!$parent -or $parent.ProcessName -ne 'battlemarked') -and $games.Count -eq 0) { break }
        Start-Sleep -Seconds 1
    }
    if (Test-Path -LiteralPath $cancel -PathType Leaf) {
        Set-Result "Update v$Version cancelled."
        return
    }
    Set-Result "Installing v$Version. Do not launch Battlemarked yet."
    & $installer -GameDir $GameDir -ExpectedVersion $Version -Install -Confirm:$false
    Set-Result "Installed v$Version successfully. You can launch Battlemarked."
}
catch {
    $exitCode = 1
    Write-Host $_ -ForegroundColor Red
    try { Set-Result "FAILED v$Version. See update-install.log. Previous DLLs preserved or restored if possible." } catch {}
    # Keep the error visible briefly; the report is retained locally.
    Start-Sleep -Seconds 4
}
finally {
    if ($transcript) { try { Stop-Transcript | Out-Null } catch {} }
    $gate.Dispose()
}
exit $exitCode
