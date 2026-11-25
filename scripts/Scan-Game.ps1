[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [string]$Filter = 'card|hero|character|enemy|monster|dice|turn|action|campaign|quest|encounter|board|tile|inventory|loot|damage|health|ability|spell|session|network',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$interop = Join-Path $GameDir 'BepInEx/interop'
if (!(Test-Path -LiteralPath $interop -PathType Container)) { throw 'Run Battlemarked once with BepInEx to generate interop assemblies.' }
if (!$OutputPath) { $OutputPath = Join-Path $root 'interop-report.txt' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$project = Join-Path $root 'tools/InteropScanner/InteropScanner.csproj'
& dotnet run --project $project -c Release -- $interop $OutputPath $Filter
if ($LASTEXITCODE -ne 0) { throw "Scanner returned $LASTEXITCODE. Check the report for details." }
