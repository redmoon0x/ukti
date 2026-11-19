# Verifies the native DLL dependency closure shipped beside Ukti.exe.
# Static PE import-table scan: checks which bundled DLLs are reachable from
# the P/Invoke entry point (nemo_speech_asr_c.dll).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root 'app'
$rootDll = Join-Path $dir 'nemo_speech_asr_c.dll'
if (-not (Test-Path $rootDll)) { throw "Missing $rootDll - run scripts/fetch-runtime.ps1 first." }

$all = Get-ChildItem $dir -Filter *.dll | Where-Object { $_.Name -notlike 'System.*' }
$content = @{}
foreach ($file in $all) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $content[$file.Name] = [Text.Encoding]::ASCII.GetString($bytes)
}

$reachable = [System.Collections.Generic.HashSet[string]]::new()
$queue = [System.Collections.Generic.Queue[string]]::new()
$queue.Enqueue('nemo_speech_asr_c.dll')
while ($queue.Count -gt 0) {
    $name = $queue.Dequeue()
    if (-not $reachable.Add($name)) { continue }
    if (-not $content.ContainsKey($name)) { throw "Missing dependency: $name" }
    foreach ($candidate in $all) {
        if ($content[$name].Contains($candidate.Name)) { $queue.Enqueue($candidate.Name) }
    }
}

Write-Output 'Reachable from nemo_speech_asr_c.dll:'
$reachable | Sort-Object | ForEach-Object { Write-Output "  $_" }
$unused = $all | Where-Object { -not $reachable.Contains($_.Name) }
Write-Output 'Not in ASR closure (candidate removals):'
$unused | ForEach-Object { Write-Output ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
