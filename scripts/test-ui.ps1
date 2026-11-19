param(
    [switch]$Screenshots,
    [string]$ScreenshotDirectory = 'docs'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$testProject = Join-Path $root 'tests\Ukti.Tests.csproj'
# The test project compiles the real sources. Do not overwrite a running app,
# and let dotnet restore the test project on a fresh checkout.
if ($Screenshots) {
    $output = if ([IO.Path]::IsPathRooted($ScreenshotDirectory)) { $ScreenshotDirectory } else { Join-Path $root $ScreenshotDirectory }
    & $dotnet run --project $testProject -c Debug -- --ui $output
} else {
    & $dotnet run --project $testProject -c Debug -- --ui
}
if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
