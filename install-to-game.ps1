param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = Join-Path (Split-Path -Parent $Root) "CozyIsland"
}

$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$GameExe = Join-Path $GameDir "CozyIsland.exe"
if (!(Test-Path -LiteralPath $GameExe)) {
    throw "CozyIsland.exe was not found in: $GameDir"
}

$BepInExRoot = Join-Path $Root "deps\BepInEx_win_x64_5.4.23.2"
$PackageDir = Join-Path $Root "dist\CozyIslandReadAloud"
if (!(Test-Path -LiteralPath (Join-Path $PackageDir "CozyIslandReadAloud.dll"))) {
    & (Join-Path $Root "build-package.ps1")
}

$InstalledBepInEx = Join-Path $GameDir "BepInEx\core\BepInEx.dll"
if (!(Test-Path -LiteralPath $InstalledBepInEx)) {
    if (!(Test-Path -LiteralPath $BepInExRoot)) {
        throw "BepInEx was not found in the game directory or local deps. Install BepInEx 5 x64 into the game first, or extract BepInEx_win_x64_5.4.23.2 to: $BepInExRoot"
    }

    Copy-Item -Path (Join-Path $BepInExRoot "*") -Destination $GameDir -Recurse -Force
}
else {
    Write-Host "BepInEx already installed, skipping core copy."
}

$PluginTarget = Join-Path $GameDir "BepInEx\plugins\CozyIslandReadAloud"
New-Item -ItemType Directory -Force -Path $PluginTarget | Out-Null
Copy-Item -Path (Join-Path $PackageDir "*") -Destination $PluginTarget -Recurse -Force
Set-Content -LiteralPath (Join-Path $PluginTarget "workspace-root.txt") -Value $Root -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $Root "audio_cache\windows\zh-CN") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Root "audio_cache\mimo\zh-CN") | Out-Null

Write-Host "Installed CozyIsland Read Aloud to:" $PluginTarget
Write-Host "Start the game once, then check BepInEx\LogOutput.log if the button does not appear."
