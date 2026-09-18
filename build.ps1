# Builds the AltSetter tool into dist/AltSetter/.
#
# Produces a self-contained win-x64 executable, so nothing needs to be installed
# on the machine that runs it.
param(
    [string]$Configuration = "Release",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\AltSetter\AltSetter.csproj"
$dist = Join-Path $root "dist\AltSetter"

Write-Host "==> gzip the CMU dictionary"
$dict = Join-Path $root "src\AltSetter\data\cmudict.txt"
$dictGz = "$dict.gz"
if (-not (Test-Path $dict)) {
    throw "Missing $dict. Run: python tools\extract_cmudict.py"
}
$in = [System.IO.File]::OpenRead($dict)
$out = [System.IO.File]::Create($dictGz)
$gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
try { $in.CopyTo($gz) } finally { $gz.Dispose(); $in.Dispose(); $out.Dispose() }
Write-Host ("    {0:N0} -> {1:N0} bytes" -f (Get-Item $dict).Length, (Get-Item $dictGz).Length)

Write-Host "==> dotnet publish"
$publish = Join-Path $root "build\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$args = @(
    "publish", $project,
    "-c", $Configuration,
    "-o", $publish,
    "-p:DebugType=none"
)
if (-not $FrameworkDependent) {
    $args += @("-r", "win-x64", "--self-contained", "true",
               "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true")
}
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> assemble $dist"
# Keep voice_path.txt if it is already configured; it records which bank the
# user wants as the fallback.
$existingVoicePath = if (Test-Path (Join-Path $dist "voice_path.txt")) {
    Get-Content (Join-Path $dist "voice_path.txt") -Raw
} else { $null }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null
Copy-Item (Join-Path $publish "AltSetter.exe") $dist
if ($existingVoicePath) {
    Set-Content -Path (Join-Path $dist "voice_path.txt") -Value $existingVoicePath -NoNewline
} else {
    @"
# The voicebank AltSetter reads recorded context from.
# Delete the line below to fall back to the project's singer.
# D:\UTAU\voice\Lem_V4Bi_Civet
"@ | Set-Content -Path (Join-Path $dist "voice_path.txt") -Encoding UTF8
}

Write-Host "==> smoke test"
$smoke = & (Join-Path $dist "AltSetter.exe") --mode inspect --bank "D:\UTAU\voice\Lem_V4Bi_Civet" 2>&1
$smoke | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { throw "smoke test failed" }

Write-Host ""
Write-Host "Built: $dist"
Write-Host "Run .\run_tests.ps1 to check it against the real voicebanks,"
Write-Host "or .\install.ps1 to put it in a convenient place."
