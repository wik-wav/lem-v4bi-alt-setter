# Runs every check for the AltSetter batch edit plugin.
#
# Read-only with respect to the voicebanks; builds into build/ only.
param(
    [string]$OpenUtauDir = "D:\OpenUtau"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
# Local paths to the real projects some checks use. Kept out of the repository:
# see fixtures.local.ps1.example.
$localFixtures = Join-Path $root "fixtures.local.ps1"
if (Test-Path $localFixtures) { . $localFixtures }
$dll = Join-Path $root "dist\AltSetterPlugin.dll"
if (-not (Test-Path $dll)) {
    $dll = Join-Path $root "build\plugin\AltSetterPlugin.dll"
}
if (-not (Test-Path $dll)) { throw "No AltSetterPlugin.dll. Run .\build_plugin.ps1 first." }

$failed = 0
function Run-Check($title, $scriptBlock) {
    Write-Host ""
    Write-Host "================================================================"
    Write-Host "== $title"
    Write-Host "================================================================"
    & $scriptBlock
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $title"
        $script:failed++
    }
}

$banks = @("Lem_V4Bi_Civet", "Lem_V4Bi_Phascogale", "Lem_V4Bi_Quoll") |
    ForEach-Object { "D:\UTAU\voice\$_" } |
    Where-Object { Test-Path $_ }

Run-Check "plugin decisions, per bank and per track" {
    & dotnet build (Join-Path $root "src\AltSetterPluginTest\AltSetterPluginTest.csproj") `
        -c Release -o (Join-Path $root "build\plugintest") -p:OpenUtauDir=$OpenUtauDir | Out-Null
    if ($LASTEXITCODE -ne 0) { return }
    & (Join-Path $root "build\plugintest\AltSetterPluginTest.exe") @banks
}

Write-Host ""
if ($failed -gt 0) {
    Write-Host "$failed check(s) failed."
    exit 1
}
Write-Host "All checks passed."
