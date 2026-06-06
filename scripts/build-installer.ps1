param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $root "release\TwitchStudioNative-$Runtime"
$zipPath = Join-Path $root "release\TwitchStudioNative-$Runtime.zip"
$issPath = Join-Path $root "installer\TwitchStudioNative.iss"

New-Item -ItemType Directory -Force (Join-Path $root "release") | Out-Null

dotnet publish (Join-Path $root "TwitchStudioNative.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=false `
    /p:DebugType=none `
    /p:DebugSymbols=false

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
Write-Host "Portable archive: $zipPath"

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if ($iscc) {
    $env:APP_VERSION = $Version
    & $iscc.Source $issPath
    Write-Host "Installer output: $(Join-Path $root "release")"
} else {
    Write-Warning "Inno Setup compiler 'iscc' was not found. Portable zip was built; install Inno Setup to build the installer exe."
}
