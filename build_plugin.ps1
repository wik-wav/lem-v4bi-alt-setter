# Builds the AltSetter batch edit plugin for OpenUtau, and optionally installs it.
#
#   .\build_plugin.ps1                        # uses D:\OpenUtau
#   .\build_plugin.ps1 -Install
#   .\build_plugin.ps1 -OpenUtauDir D:\other\OpenUtau
param(
    [string]$Configuration = "Release",
    [string]$OpenUtauDir = "D:\OpenUtau",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\AltSetterPlugin\AltSetterPlugin.csproj"
$dist = Join-Path $root "dist"

if (-not (Test-Path (Join-Path $OpenUtauDir "OpenUtau.Core.dll"))) {
    throw "OpenUtau not found at $OpenUtauDir. Pass -OpenUtauDir."
}
if (-not (Test-Path (Join-Path $root "src\AltSetter\data\cmudict.txt.gz"))) {
    throw "Missing the gzipped CMU dictionary. Run .\build.ps1 once, or tools\extract_cmudict.py."
}

Write-Host "==> build the plugin"
& dotnet build $project -c $Configuration -o (Join-Path $root "build\plugin") `
    -p:OpenUtauDir=$OpenUtauDir
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item (Join-Path $root "build\plugin\AltSetterPlugin.dll") $dist -Force

Write-Host ""
Write-Host "Built: $(Join-Path $dist 'AltSetterPlugin.dll')"

if ($Install) {
    # OpenUtau keeps the plugin loaded, so a running instance holds the file and
    # the copy fails. Say so instead of leaving an old plugin installed, which is
    # silent and looks exactly like a fix that did not work.
    $running = Get-Process -Name OpenUtau -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host ""
        Write-Warning ("OpenUtau is running (pid {0}); it holds Plugins\AltSetterPlugin.dll." -f ($running.Id -join ", "))
        Write-Warning "Close OpenUtau and run this again, or the old plugin stays installed."
        throw "OpenUtau is running"
    }
    $plugins = Join-Path $OpenUtauDir "Plugins"
    $target = Join-Path $plugins "AltSetterPlugin.dll"
    Copy-Item (Join-Path $dist "AltSetterPlugin.dll") $plugins -Force
    $installed = Get-Item $target
    Write-Host "Installed to $plugins"
    Write-Host ("  {0}  {1:N0} bytes  {2}" -f $installed.Name, $installed.Length,
        $installed.LastWriteTime.ToString("HH:mm:ss"))
    Write-Host ""
    Write-Host "Restart OpenUtau. It appears as:"
    Write-Host "  Piano Roll -> Notes -> External -> Alt Setter: set alts from voicebank context"
    Write-Host ""
    Write-Host "The voicebank comes from the track's own singer, so each track can use a"
    Write-Host "different bank. The change is one undo step (Ctrl+Z)."
} else {
    Write-Host "Copy it into $(Join-Path $OpenUtauDir 'Plugins') and restart OpenUtau."
}
