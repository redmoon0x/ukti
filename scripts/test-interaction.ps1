param([switch]$Speech)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$project = Join-Path $root 'tests\Ukti.Tests.csproj'
& $dotnet build $project -c Debug -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Interaction-test build failed.' }
& $dotnet run --project $project -c Debug --no-build -- --interaction
if ($LASTEXITCODE -ne 0) { throw 'Menu/save/paste interaction tests failed.' }
if ($Speech) {
    # Existing runtime/model only. Synthetic speech stays in memory; no mic,
    # download, user-preference changes, or edits to the running Ukti binary.
    $env:PATH = (Join-Path $root 'app') + ';' + $env:PATH
    & $dotnet run --project $project -c Debug --no-build -- --interaction speech
    if ($LASTEXITCODE -ne 0) { throw 'Real ASR-to-editor integration test failed.' }
}
