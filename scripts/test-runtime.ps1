param([ValidateSet('nemotron-en', 'nemotron-3.5', 'parakeet-tdt', 'parakeet-ctc')][string]$Model = 'nemotron-en')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$testProject = Join-Path $root 'tests\Ukti.Tests.csproj'
$env:PATH = (Join-Path $root 'app') + ';' + $env:PATH
# Restore/build the test host itself; do not overwrite the running application.
& $dotnet run --project $testProject -c Debug -- $Model
if ($LASTEXITCODE -ne 0) { throw 'Runtime smoke test failed.' }
