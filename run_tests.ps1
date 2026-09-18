# Runs the full offline test suite against the real voicebanks and the real
# project file. Read-only with respect to the voicebanks and the original .ustx;
# everything is written under build/.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
# Local paths to the real projects some checks use. Kept out of the repository:
# see fixtures.local.ps1.example.
$localFixtures = Join-Path $root "fixtures.local.ps1"
if (Test-Path $localFixtures) { . $localFixtures }
$exe = Join-Path $root "dist\AltSetter\AltSetter.exe"
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root "build\dbg\AltSetter.exe"
}
if (-not (Test-Path $exe)) { throw "No AltSetter.exe. Run build.ps1 first." }

$failed = 0

function Run-Check($title, $scriptBlock) {
    Write-Host ""
    Write-Host "================================================================"
    Write-Host "== $title"
    Write-Host "================================================================"
    & $scriptBlock
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $title"
        $scriptGlobal:failed++
    }
}

$banks = @("Lem_V4Bi_Civet", "Lem_V4Bi_Phascogale", "Lem_V4Bi_Quoll")
foreach ($bank in $banks) {
    $path = "D:\UTAU\voice\$bank"
    if (-not (Test-Path $path)) { Write-Host "skipping missing bank $path"; continue }
    Run-Check "bank reading and scoring against $bank" {
        python (Join-Path $root "tools\test_plugin.py") --exe $exe --bank $path
    }
    Run-Check "writing alts into a .ustx with $bank" {
        python (Join-Path $root "tools\test_unit.py") --exe $exe --bank $path
    }
}

Write-Host ""
if ($failed -gt 0) {
    Write-Host "$failed check(s) failed."
    exit 1
}
Write-Host "All checks passed."
