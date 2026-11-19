# Installs into a temporary directory, checks the payload, and uninstalls it.
# Refuses to overwrite an existing installed Ukti registration/Start-menu folder.
param(
    [string]$Installer = 'dist\Ukti-Setup-1.0.0-win-x64.exe',
    [switch]$RuntimeTest,
    [string]$ModelPath = '',
    [string]$RuntimeDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installerPath = if ([IO.Path]::IsPathRooted($Installer)) { $Installer } else { Join-Path $root $Installer }
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Ukti'
$programKey = 'HKCU:\Software\Ukti'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$menu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Ukti'
if ((Test-Path $registration) -or (Test-Path $programKey) -or (Test-Path $menu)) {
    throw 'An installed Ukti registration/shortcut already exists. Use a clean test account; nothing was changed.'
}
$existingRun = (Get-ItemProperty $runKey -Name Ukti -ErrorAction SilentlyContinue).Ukti
$settings = Join-Path $env:LOCALAPPDATA 'Ukti\settings.txt'
$settingsHash = if (Test-Path $settings) { (Get-FileHash $settings -Algorithm SHA256).Hash } else { $null }
$models = Join-Path $env:LOCALAPPDATA 'Ukti\models'
function ModelInventory {
    return ((@(Get-ChildItem $models -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ Name=$_.Name; Bytes=$_.Length; Modified=$_.LastWriteTimeUtc.Ticks }
    })) | ConvertTo-Json -Compress)
}
$beforeModels = ModelInventory
$temp = Join-Path $env:TEMP ('Ukti installer smoke ' + [Guid]::NewGuid().ToString('N'))
$target = Join-Path $temp 'app'
$uninstalled = $false
function RunAndWait([string]$File, [string]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($File, $Arguments)
    $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Installer process timed out.' }
        if ($process.ExitCode -ne 0) { throw "Installer process returned $($process.ExitCode). Quit all Ukti copies first." }
    } finally { $process.Dispose() }
}
function CheckRuntime([string]$Directory, [string]$Model) {
    $pipeName = 'Ukti.InstallerTest.' + [Guid]::NewGuid().ToString('N')
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
    $worker = $null
    try {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Model))
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $Directory 'Ukti.exe'), "--asr-worker $pipeName nemotron-en $encoded")
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        # No repo/runtime directory on PATH: installed DLLs must stand alone.
        $start.EnvironmentVariables['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
        $worker = [Diagnostics.Process]::Start($start)
        $worker.BeginOutputReadLine(); $worker.BeginErrorReadLine()
        $connection = $pipe.BeginWaitForConnection($null, $null)
        try {
            if (-not $connection.AsyncWaitHandle.WaitOne(30000)) { throw 'Installed speech worker did not connect.' }
            $pipe.EndWaitForConnection($connection)
        } finally { $connection.AsyncWaitHandle.Dispose() }
        $reader = [IO.BinaryReader]::new($pipe, [Text.Encoding]::UTF8, $true)
        $writer = [IO.BinaryWriter]::new($pipe, [Text.Encoding]::UTF8, $true)
        $status = $reader.ReadByte(); $version = $reader.ReadString()
        if ($status -ne 0) { throw "Installed runtime failed to load: $version" }
        $writer.Write([byte]1); $writer.Write([int]16000); $writer.Write([int]8000)
        $writer.Write([byte[]]::new(32000)); $writer.Flush()
        $status = $reader.ReadByte(); $text = $reader.ReadString()
        if ($status -ne 0) { throw "Installed runtime failed inference: $text" }
        Write-Output "PASS: installed runtime loaded/warmed on CUDA with a clean PATH ($version)."
    } finally {
        $pipe.Dispose()
        if ($worker) {
            try { if (-not $worker.WaitForExit(5000)) { $worker.Kill(); $worker.WaitForExit(5000) | Out-Null } }
            finally { $worker.Dispose() }
        }
    }
}
try {
    $reuseArgument = if ($RuntimeDirectory) { ' /RUNTIME_DIR="' + (Resolve-Path $RuntimeDirectory).Path + '"' } else { '' }
    RunAndWait $installerPath ('/S' + $reuseArgument + ' /D=' + $target)
    $manifest = Get-Content (Join-Path $target 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.IncludesModels) { throw 'Installer unexpectedly declares bundled models.' }
    foreach ($file in $manifest.Files) {
        $path = Join-Path $target $file.Path
        if (-not (Test-Path $path)) { throw "Installed file missing: $($file.Path)" }
        if ((Get-Item $path).Length -ne $file.Bytes -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $file.SHA256) {
            throw "Installed file differs from build payload: $($file.Path)"
        }
    }
    $runtime = Get-Content (Join-Path $target 'runtime-manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $runtime.Files) {
        $path = Join-Path $target $file.Name
        if (-not (Test-Path $path) -or (Get-Item $path).Length -ne $file.Bytes -or
            (Get-FileHash $path -Algorithm SHA256).Hash -ne $file.SHA256) { throw "Installed runtime differs: $($file.Name)" }
    }
    if (-not $manifest.IncludesRuntime -and (Get-Item $installerPath).Length -gt 2MB) { throw 'Web installer unexpectedly exceeds 2 MiB.' }
    if (Get-ChildItem $target -Recurse -Include '*.gguf','settings.txt','*.wav','*.log','*.dmp') { throw 'Installer included model/personal-state files.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $menu 'Ukti.lnk'))
    if (-not ('UktiInstallerPath' -as [type])) {
        Add-Type 'using System.Text; using System.Runtime.InteropServices; public static class UktiInstallerPath { [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern uint GetLongPathName(string input, StringBuilder output, uint size); }'
    }
    $resolvedShortcut = [Text.StringBuilder]::new(32768)
    $expectedShortcut = [Text.StringBuilder]::new(32768)
    $resolvedExpected = [UktiInstallerPath]::GetLongPathName((Join-Path $target 'Ukti.exe'), $expectedShortcut, 32768)
    if ([UktiInstallerPath]::GetLongPathName($shortcut.TargetPath, $resolvedShortcut, 32768) -eq 0 -or
        $resolvedExpected -eq 0 -or $resolvedShortcut.ToString() -ne $expectedShortcut.ToString()) {
        throw "Start-menu shortcut target is wrong: actual='$($shortcut.TargetPath)' expected='$(Join-Path $target 'Ukti.exe')'."
    }
    if ((Get-ItemProperty $registration).DisplayVersion -ne $manifest.Version) { throw 'Windows Apps registration is missing/wrong.' }
    Write-Output ('PASS: installer copied {0} app/notice files plus {1} verified runtime DLLs, registered uninstall, and created the Start-menu shortcut; no model/state bundled.' -f $manifest.Files.Count, $runtime.Files.Count)
    if ($RuntimeTest) {
        if (-not $ModelPath) {
            $ModelPath = @('nemotron-en.q8_0.gguf','nemotron-speech-streaming-en-0.6b.q8_0.gguf') |
                ForEach-Object { Join-Path $models $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $ModelPath -or -not (Test-Path $ModelPath)) { throw 'RuntimeTest needs an already-downloaded English model. No model is downloaded by this test.' }
        CheckRuntime $target $ModelPath
    }
    $sentinel = Join-Path $target 'unrelated-user-file.txt'
    [IO.File]::WriteAllText($sentinel, 'This file must survive uninstall.')
    RunAndWait (Join-Path $target 'Uninstall.exe') '/S'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (((Test-Path (Join-Path $target 'Ukti.exe')) -or (Test-Path $registration)) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if ((Test-Path (Join-Path $target 'Ukti.exe')) -or (Test-Path $registration) -or (Test-Path $menu)) { throw 'Uninstall left application files, registration, or shortcuts behind.' }
    if (-not (Test-Path $sentinel)) { throw 'Uninstaller deleted an unrelated file.' }
    if ($settingsHash -and ((Get-FileHash $settings -Algorithm SHA256).Hash -ne $settingsHash)) { throw 'Install/uninstall changed saved preferences.' }
    if ((ModelInventory) -ne $beforeModels) { throw 'Install/uninstall changed the model cache.' }
    $uninstalled = $true
    Write-Output 'PASS: uninstall removed the app/shortcuts/registration, preserved unrelated files, preferences, and downloaded models.'
} finally {
    # On a failure, attempt the package's own uninstall before cleaning only its
    # temporary test directory. Never remove user app-data/model directories.
    if (-not $uninstalled -and (Test-Path (Join-Path $target 'Uninstall.exe'))) {
        try { RunAndWait (Join-Path $target 'Uninstall.exe') '/S'; Start-Sleep -Seconds 1 } catch { Write-Warning $_ }
    }
    if ($existingRun) { Set-ItemProperty $runKey -Name Ukti -Value $existingRun }
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
