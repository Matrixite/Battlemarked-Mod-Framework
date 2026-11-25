[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [switch]$Install,
    [switch]$IncludeExample
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (!(Test-Path -LiteralPath (Join-Path $GameDir 'battlemarked.exe') -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $GameDir 'GameAssembly.dll') -PathType Leaf)) {
    throw 'Select the Windows Battlemarked game directory containing battlemarked.exe and GameAssembly.dll.'
}
foreach ($relative in @('BepInEx/core/BepInEx.Unity.IL2CPP.dll','BepInEx/core/0Harmony.dll','BepInEx/interop/UnityEngine.CoreModule.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $GameDir $relative) -PathType Leaf)) { throw "Missing $relative. Install current BepInEx 6 IL2CPP x64 and launch the game once." }
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 8 SDK and .NET 6 targeting pack/SDK.' }
$projects = @(@{Project='src/Framework/Battlemarked.ModFramework.csproj'; Dll='Battlemarked.ModFramework.dll'})
if ($IncludeExample) { $projects += @{Project='src/Example/Battlemarked.Example.csproj'; Dll='Battlemarked.Example.dll'} }
foreach ($p in $projects) {
    $project = Join-Path $root $p.Project
    & dotnet build $project -c Release "-p:GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
if ($Install) {
    if (Get-Process -Name battlemarked -ErrorAction SilentlyContinue) { throw 'Close Battlemarked before installation.' }
    $dest = Join-Path $GameDir 'BepInEx/plugins/BattlemarkedModFramework'
    if ($PSCmdlet.ShouldProcess($dest, 'Copy selected framework DLLs')) {
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($p in $projects) {
            $dir = Split-Path (Join-Path $root $p.Project) -Parent
            $dll = Join-Path $dir "bin/Release/net6.0/$($p.Dll)"
            $target = Join-Path $dest $p.Dll
            if (Test-Path -LiteralPath $target) {
                $backup = "$target.bak"
                if (Test-Path -LiteralPath $backup) { $backup = "$target.$([Guid]::NewGuid().ToString('N')).bak" }
                Copy-Item -LiteralPath $target -Destination $backup
            }
            Copy-Item -LiteralPath $dll -Destination $target -Force
        }
        Write-Host "Installed to $dest. Existing config was not changed."
    }
} else { Write-Host 'Build only. Add -Install to copy DLLs to your game.' }
