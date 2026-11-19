# Embedded in the small web installer. Downloads data only; nothing from the
# archive is loaded/executed until the pinned SHA-256 and file hashes match.
param(
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$CacheDirectory,
    [string]$ExistingDirectory = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
function Matches($Path, $Bytes, $Hash) {
    return ((Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Item -LiteralPath $Path).Length -eq $Bytes -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Hash)
}
function DownloadArchive($Spec, $Path) {
    $partial = $Path + '.partial'
    if ((Test-Path $partial) -and (Get-Item $partial).Length -gt $Spec.Bytes) { Remove-Item $partial -Force }
    $url = [string]$Spec.Url
    $failures = 0
    while (-not (Test-Path $partial) -or (Get-Item $partial).Length -lt $Spec.Bytes) {
        $offset = if (Test-Path $partial) { (Get-Item $partial).Length } else { 0L }
        $last = [math]::Min($offset + 8MB - 1, $Spec.Bytes - 1)
        $response = $null; $inputStream = $null; $file = $null
        try {
            $request = [Net.HttpWebRequest]::Create($url)
            $request.UserAgent = 'Ukti-Installer/1.0'
            $request.AllowAutoRedirect = $true
            $request.Timeout = 30000; $request.ReadWriteTimeout = 30000
            if ($request.Proxy) { $request.Proxy.Credentials = [Net.CredentialCache]::DefaultNetworkCredentials }
            $request.AddRange([long]$offset, [long]$last)
            $response = $request.GetResponse()
            if ($response.ResponseUri.Scheme -ne 'https') { throw 'Runtime download redirected away from HTTPS.' }
            $url = $response.ResponseUri.AbsoluteUri
            if ([int]$response.StatusCode -eq 200) {
                # Some servers ignore Range. Restart, rather than appending a
                # complete response to an existing partial archive.
                $offset = 0L; $expected = [long]$Spec.Bytes
            } elseif ([int]$response.StatusCode -eq 206) {
                $expected = [long]($last - $offset + 1)
                if ($response.Headers['Content-Range'] -ne "bytes $offset-$last/$($Spec.Bytes)") { throw 'Unexpected runtime download byte range.' }
            } else { throw "Unexpected HTTP response: $($response.StatusCode)" }
            $file = [IO.File]::Open($partial, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $file.SetLength($offset); $file.Position = $offset
            $inputStream = $response.GetResponseStream()
            $buffer = [byte[]]::new(65536); $received = 0L
            while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $received += $count
                if ($received -gt $expected) { throw 'Runtime server sent more bytes than expected.' }
                $file.Write($buffer, 0, $count)
            }
            if ($received -ne $expected) { throw 'Runtime download ended before the requested range completed.' }
            $file.Flush()
            $percent = [math]::Min(100, [math]::Floor(100 * ($offset + $received) / $Spec.Bytes))
            Write-Output "Downloading speech runtime: $percent%"
            $failures = 0
        } catch {
            $failures++
            $url = [string]$Spec.Url # Get a fresh signed CDN URL after a failure.
            if ($failures -ge 3) { throw }
            Write-Output "Download interrupted; resuming (attempt $failures of 3)..."
            Start-Sleep -Seconds 2
        } finally {
            if ($inputStream) { $inputStream.Dispose() }
            if ($file) { $file.Dispose() }
            if ($response) { $response.Dispose() }
        }
    }
    Write-Output 'Verifying downloaded runtime SHA-256...'
    if (-not (Matches $partial $Spec.Bytes $Spec.SHA256)) {
        Remove-Item $partial -Force
        throw 'Runtime archive checksum mismatch. Nothing from it was installed. Please retry.'
    }
    Move-Item $partial $Path -Force
}
try {
    $spec = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    if ($spec.Archive.Url -notlike 'https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0/*' -or
        $spec.Archive.SHA256 -notmatch '^[a-fA-F0-9]{64}$' -or $spec.Files.Count -lt 1) { throw 'Invalid runtime manifest.' }
    foreach ($entry in $spec.Files) {
        if ($entry.Name -notmatch '^[a-zA-Z0-9_.-]+\.dll$' -or $entry.SHA256 -notmatch '^[a-fA-F0-9]{64}$' -or
            $entry.Bytes -le 0 -or $entry.Bytes -gt 1GB) { throw 'Invalid runtime file entry.' }
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $reuse = [bool]$ExistingDirectory
    if ($reuse) {
        foreach ($entry in $spec.Files) {
            if (-not (Matches (Join-Path $ExistingDirectory $entry.Name) $entry.Bytes $entry.SHA256)) { $reuse = $false; break }
        }
    }
    if ($reuse) {
        Write-Output 'Reusing the existing, verified speech runtime.'
        foreach ($entry in $spec.Files) { Copy-Item -LiteralPath (Join-Path $ExistingDirectory $entry.Name) -Destination (Join-Path $Destination $entry.Name) -Force }
    } else {
        New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
        $archive = Join-Path $CacheDirectory 'nemo-speech-0.3.0-windows-x86_64-cuda.zip'
        if (-not (Matches $archive $spec.Archive.Bytes $spec.Archive.SHA256)) {
            if (Test-Path $archive) { Remove-Item $archive -Force }
            Write-Output 'Downloading NVIDIA speech runtime (about 485 MiB). An interrupted download can resume on retry.'
            DownloadArchive $spec.Archive $archive
        } else { Write-Output 'Using the verified cached runtime download.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($entry in $spec.Files) {
                $member = $zip.GetEntry('nemo-speech-0.3.0-windows-x86_64-cuda/bin/' + $entry.Name)
                if (-not $member -or $member.Length -ne $entry.Bytes) { throw "Missing or invalid runtime archive member: $($entry.Name)" }
                $path = Join-Path $Destination $entry.Name
                $source = $member.Open(); $file = [IO.File]::Create($path)
                try { $source.CopyTo($file) } finally { $file.Dispose(); $source.Dispose() }
                if (-not (Matches $path $entry.Bytes $entry.SHA256)) { throw "Extracted runtime checksum mismatch: $($entry.Name)" }
            }
        } finally { $zip.Dispose() }
    }
    Write-Output 'Speech runtime verified. Ready to install Ukti.'
    exit 0
} catch {
    Write-Output ('Runtime setup failed: ' + $_.Exception.Message)
    Write-Output 'Check your connection/proxy and free disk space, then retry. No speech model is downloaded by this installer.'
    exit 1
}
