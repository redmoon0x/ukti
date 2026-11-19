# Tests verification/extraction and rejection without downloading or executing
# a real runtime. Fixtures are private temporary ZIPs, not speech/model data.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$helper = Join-Path $root 'packaging\Install-Runtime.ps1'
$temp = Join-Path $env:TEMP ('ukti-bootstrap-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $source = Join-Path $temp 'source'
    $member = Join-Path $source 'nemo-speech-0.3.0-windows-x86_64-cuda\bin'
    $cache = Join-Path $temp 'cache'
    New-Item -ItemType Directory -Path $member,$cache -Force | Out-Null
    $dll = Join-Path $member 'fixture.dll'
    [IO.File]::WriteAllText($dll, 'Non-executable runtime verification fixture.')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $archive = Join-Path $cache 'nemo-speech-0.3.0-windows-x86_64-cuda.zip'
    # Explicit ZIP-standard separators: legacy PowerShell hosts otherwise
    # create backslash member names unlike NVIDIA's release archive.
    $zip = [IO.Compression.ZipArchive]::new([IO.File]::Create($archive), [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $entry = $zip.CreateEntry('nemo-speech-0.3.0-windows-x86_64-cuda/bin/fixture.dll')
        $stream = $entry.Open(); $bytes = [IO.File]::ReadAllBytes($dll)
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
    $spec = [ordered]@{
        Archive=[ordered]@{
            Url='https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0/nemo-speech-0.3.0-windows-x86_64-cuda.zip'
            Bytes=(Get-Item $archive).Length; SHA256=(Get-FileHash $archive -Algorithm SHA256).Hash
        }
        Files=@([ordered]@{ Name='fixture.dll'; Bytes=(Get-Item $dll).Length; SHA256=(Get-FileHash $dll -Algorithm SHA256).Hash })
    }
    $manifest = Join-Path $temp 'manifest.json'
    $spec | ConvertTo-Json -Depth 6 | Set-Content $manifest -Encoding UTF8
    $destination = Join-Path $temp 'verified'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Manifest $manifest -Destination $destination -CacheDirectory $cache
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash (Join-Path $destination 'fixture.dll')).Hash -ne $spec.Files[0].SHA256) { throw 'Valid cached runtime was not verified/extracted.' }
    Write-Output 'PASS: cached archive hash, allowlisted member, and extracted-file checksum verification.'

    # A complete but incorrectly pinned partial file must be rejected without
    # another network request or copying any DLL into the destination.
    $badCache = Join-Path $temp 'bad-cache'
    New-Item -ItemType Directory -Path $badCache | Out-Null
    Copy-Item $archive (Join-Path $badCache 'nemo-speech-0.3.0-windows-x86_64-cuda.zip.partial')
    $spec.Archive.SHA256 = '0' * 64
    $spec | ConvertTo-Json -Depth 6 | Set-Content $manifest -Encoding UTF8
    $badDestination = Join-Path $temp 'rejected'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Manifest $manifest -Destination $badDestination -CacheDirectory $badCache
    if ($LASTEXITCODE -eq 0 -or (Test-Path (Join-Path $badDestination 'fixture.dll'))) { throw 'Bad archive hash was accepted.' }
    Write-Output 'PASS: archive checksum mismatch fails closed before installation.'

    $spec.Files[0].Name = '../escape.dll'
    $spec | ConvertTo-Json -Depth 6 | Set-Content $manifest -Encoding UTF8
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Manifest $manifest -Destination $badDestination -CacheDirectory $badCache
    if ($LASTEXITCODE -eq 0 -or (Test-Path (Join-Path $temp 'escape.dll'))) { throw 'Unsafe runtime path was accepted.' }
    Write-Output 'PASS: unsafe archive/member names are rejected.'
    $global:LASTEXITCODE = 0
} finally { if (Test-Path $temp) { Remove-Item $temp -Recurse -Force } }
