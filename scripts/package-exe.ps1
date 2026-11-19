# Builds a per-user Windows EXE installer. Models and user preferences are NEVER
# copied from app/ or LOCALAPPDATA into the package.
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$OutputDirectory = 'dist',
    [string]$Makensis = '',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'obj\installer-stage'
$payload = Join-Path $stage 'payload'
$build = Join-Path $root 'obj\installer-build'
$tools = Join-Path $root 'obj\installer-tools'
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
New-Item -ItemType Directory -Force -Path $tools,$output | Out-Null
foreach ($part in $Version.Split('.')) { if ([int]$part -gt 65535) { throw 'Version parts must fit Windows version resources.' } }

if (-not $Makensis) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($command) { $Makensis = $command.Source }
    else {
        $Makensis = Join-Path $tools 'nsis-3.11\makensis.exe'
        $zip = Join-Path $tools 'nsis-3.11.zip'
        $expected = 'C7D27F780DDB6CFFB4730138CD1591E841F4B7EDB155856901CDF5F214394FA1'
        if (-not (Test-Path $zip)) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri 'https://prdownloads.sourceforge.net/nsis/nsis-3.11.zip' -OutFile $zip
        }
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected) { throw 'NSIS compiler archive checksum mismatch.' }
        # Always extract from the verified archive, not a stale local compiler.
        Expand-Archive $zip $tools -Force
    }
}
if (-not (Test-Path $Makensis)) { throw "NSIS compiler not found: $Makensis" }
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet build (Join-Path $root 'Ukti.csproj') -c Release -o $build "-p:Version=$Version" -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $payload | Out-Null
Copy-Item (Join-Path $build 'Ukti.exe'),(Join-Path $build 'Ukti.exe.config') $payload
Get-ChildItem $build -Filter 'System.*.dll' | Copy-Item -Destination $payload

# ASR dependency set plus the app-local Microsoft C/C++ redistributables from
# NVIDIA's official Windows x64 CUDA archive. No CLI, TTS/NMT tools or models.
$native = @(
    'nemo_speech_asr_c.dll', 'nemo_speech_asr.dll', 'nemo_speech_speechlm.dll',
    'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll', 'ggml-cuda.dll',
    'llama.dll', 'llama-common.dll', 'cublas64_13.dll', 'cublasLt64_13.dll',
    'concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
    'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vcomp140.dll',
    'vcruntime140.dll', 'vcruntime140_1.dll'
)
$runtimeFiles = @()
foreach ($name in $native) {
    $source = Join-Path $root "app\$name"
    if (-not (Test-Path $source)) { throw "Missing runtime dependency: $source. Run scripts/fetch-runtime.ps1 first." }
    $stream = [IO.File]::OpenRead($source)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3C; $pe = $reader.ReadInt32()
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "Not a Windows x64 DLL: $name" }
    } finally { $reader.Dispose() }
    $runtimeFiles += [ordered]@{ Name=$name; Bytes=(Get-Item $source).Length; SHA256=(Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant() }
    if ($Offline) { Copy-Item $source $payload }
}
$runtime = [ordered]@{
    Version='0.3.0'; Architecture='windows-x86_64-cuda'
    Archive=[ordered]@{
        Url='https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0/nemo-speech-0.3.0-windows-x86_64-cuda.zip'
        Bytes=508625181
        SHA256='d0d32ae1bf6ee0534767a47b2aa0e9e2ff6fe39643465c0baae9301d04f6ed42'
    }
    Files=@($runtimeFiles)
}
$runtime | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $payload 'runtime-manifest.json') -Encoding UTF8
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $payload 'LICENSE.txt')
Copy-Item (Join-Path $root 'packaging\INSTALL.txt') (Join-Path $payload 'INSTALL.txt')
Copy-Item (Join-Path $root 'packaging\licenses') $payload -Recurse
if (Get-ChildItem $payload -Recurse -Include '*.gguf','settings.txt','*.wav','*.log','*.dmp') {
    throw 'A model or personal-state file unexpectedly entered the installer payload.'
}

$hashes = Get-ChildItem $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = $_.FullName.Substring($payload.Length + 1).Replace('\','/'); Bytes = $_.Length; SHA256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{ Product='Ukti'; Version=$Version; Architecture='x64'; IncludesModels=$false; IncludesRuntime=[bool]$Offline; Runtime='NeMo-Speech.cpp 0.3.0 CUDA'; Files=@($hashes) }
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $payload 'manifest.json') -Encoding UTF8

# Generate exact-file uninstall rules. Never RMDir /r an installation directory
# chosen by a user, and never remove their separately downloaded models.
$rules = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem $payload -Recurse -File) {
    $relative = $file.FullName.Substring($payload.Length + 1).Replace('$','$$').Replace('"','$\"')
    $rules.Add('Delete "$INSTDIR\' + $relative + '"')
}
if (-not $Offline) {
    foreach ($name in $native) { $rules.Add('Delete "$INSTDIR\' + $name + '"') }
}
Get-ChildItem $payload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1).Replace('$','$$').Replace('"','$\"')
    $rules.Add('RMDir "$INSTDIR\' + $relative + '"')
}
[IO.File]::WriteAllLines((Join-Path $stage 'uninstall-files.nsh'), $rules, [Text.UTF8Encoding]::new($true))
$suffix = if ($Offline) { '-offline' } else { '' }
$installer = Join-Path $output "Ukti-Setup-$Version-win-x64$suffix.exe"
$kb = [int][math]::Ceiling((Get-ChildItem $payload -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$runtimeKb = [int][math]::Ceiling(($runtimeFiles | ForEach-Object { $_.Bytes } | Measure-Object -Sum).Sum / 1KB)
if (-not $Offline) { $kb += $runtimeKb }
$mode = if ($Offline) { @('/DOFFLINE') } else { @() }
& $Makensis /V2 @mode "/DVERSION=$Version" "/DPROJECT_ROOT=$root" "/DSTAGE=$stage" "/DOUTPUT_FILE=$installer" "/DPAYLOAD_KB=$kb" "/DRUNTIME_KB=$runtimeKb" (Join-Path $root 'packaging\Ukti.nsi')
if ($LASTEXITCODE -ne 0) { throw 'EXE installer compilation failed.' }
$checksum = Get-FileHash $installer -Algorithm SHA256
[IO.File]::WriteAllText(($installer + '.sha256'), $checksum.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($installer) + "`r`n", [Text.UTF8Encoding]::new($false))
Copy-Item (Join-Path $root 'packaging\INSTALL.txt') (Join-Path $output 'INSTALL.txt') -Force
Write-Output "Installer: $installer"
Write-Output ("Size: {0:N2} MiB (no speech model bundled)" -f ((Get-Item $installer).Length / 1MB))
if (-not $Offline) { Write-Output 'Web installer: downloads/verifies the NVIDIA runtime during setup; the app downloads a model on first launch.' }
Write-Output 'Unsigned build: Windows SmartScreen may show an unknown-publisher warning.'
