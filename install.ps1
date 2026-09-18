# Puts the AltSetter tool somewhere convenient, with the two extra files it can
# use. Nothing here touches OpenUtau itself: this tool is run on a .ustx file.
#
#   .\install.ps1
#   .\install.ps1 -Target "D:\Tools\AltSetter" -Bank "D:\UTAU\voice\Lem_V4Bi_Quoll"
param(
    [string]$Target = "$env:USERPROFILE\AltSetter",
    [string]$Bank = "D:\UTAU\voice\Lem_V4Bi_Civet"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist\AltSetter"

if (-not (Test-Path (Join-Path $dist "AltSetter.exe"))) {
    throw "dist\AltSetter\AltSetter.exe is missing. Run .\build.ps1 first."
}

New-Item -ItemType Directory -Path $Target -Force | Out-Null
Copy-Item (Join-Path $dist "AltSetter.exe") $Target -Force

# Which bank to read when --bank is not given. The project's singer is tried
# first, so this is only a fallback.
if ($Bank) {
    @"
# The voicebank AltSetter reads recorded context from.
# Delete the line below to fall back to the project's singer.
$Bank
"@ | Set-Content -Path (Join-Path $Target "voice_path.txt") -Encoding UTF8
}

Write-Host "Installed to $Target"
if ($Bank) { Write-Host "Default voicebank: $Bank" }
Write-Host ""
Write-Host "Run it on a project with the project closed in OpenUtau:"
Write-Host "  `"$Target\AltSetter.exe`" `"D:\path\to\song.ustx`""
Write-Host ""
Write-Host "Add -n first to see what it would change without writing."
