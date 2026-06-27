param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "src\CozyIslandReadAloud\CozyIslandReadAloud.csproj"
$PluginOut = Join-Path $Root "src\CozyIslandReadAloud\bin\$Configuration\CozyIslandReadAloud.dll"
$Dist = Join-Path $Root "dist\CozyIslandReadAloud"
$DistAudio = Join-Path $Dist "audio\zh-CN"

dotnet build $Project -c $Configuration -v:minimal

New-Item -ItemType Directory -Force -Path $DistAudio | Out-Null
Copy-Item -LiteralPath $PluginOut -Destination (Join-Path $Dist "CozyIslandReadAloud.dll") -Force
Copy-Item -Path (Join-Path $Root "audio\zh-CN\*.wav") -Destination $DistAudio -Force

Write-Host "Package ready:" $Dist
