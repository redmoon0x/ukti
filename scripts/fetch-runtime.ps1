# Places the NeMo-Speech.cpp runtime beside the built executable.
#
# Ukti links against the published C ABI in nemo_speech_asr_c.dll. Those binaries
# are ~700 MB together, far past GitHub's file limits, so they are fetched from
# NVIDIA's official release rather than committed. The model itself is downloaded
# by the app on first launch.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = '0.3.0'
$asset = "nemo-speech-$version-windows-x86_64-cuda.zip"
$url = "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v$version/$asset"

$root = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $root 'app'
$zip = Join-Path $env:TEMP $asset

if (-not (Test-Path $appDir)) {
    Write-Host "Building first..."
    & (Join-Path $root 'build.cmd')
}

Write-Host "Downloading NeMo-Speech.cpp $version (CUDA, Windows x64)..."
Invoke-WebRequest -Uri $url -OutFile $zip

Write-Host "Extracting..."
$tmp = Join-Path $env:TEMP "nemo-speech-$version"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $tmp -Force

$bin = Get-ChildItem $tmp -Directory | Select-Object -First 1
$binPath = Join-Path $bin.FullName 'bin'
if (-not (Test-Path $binPath)) { throw "Unexpected archive layout: $binPath not found" }

Write-Host "Copying runtime DLLs to $appDir"
Copy-Item (Join-Path $binPath '*.dll') $appDir -Force

Remove-Item $zip -Force
Remove-Item $tmp -Recurse -Force

Write-Host ""
Write-Host "Done. Start the app with:"
Write-Host "  $appDir\Ukti.exe"
Write-Host ""
Write-Host "The speech model (~700 MB) downloads on first launch."
