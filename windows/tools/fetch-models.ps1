<#
.SYNOPSIS
    Downloads the local-Whisper runtime pinned in models.lock.json and verifies every file by SHA-256.

.DESCRIPTION
    Default: whisper-server.exe with its DLLs (whisper.cpp release zip for Windows x64) into windows\whisper\,
    plus ggml-large-v3-turbo.bin (~1.6 GB) and ggml-silero-v5.1.2.bin into windows\models\whisper\.
    A file that is already present with the pinned hash is not downloaded again. A download whose hash does
    not match is deleted and the script fails; nothing unverified is ever moved into place. Only the files
    listed in the lock are taken from the zip (no other executables, no paths from the archive).

    whisper-server.exe needs the Microsoft Visual C++ 2015-2022 x64 runtime (MSVCP140, VCRUNTIME140,
    VCRUNTIME140_1, VCOMP140).

.PARAMETER TestModel
    Fetch only ggml-tiny.bin (~78 MB) for the gated LocalWhisperLiveTests.

.PARAMETER ServerOnly
    Fetch only whisper-server.exe and its DLLs.

.PARAMETER Diarization
    Fetch only the speaker-diarization models (section "diarization" of models.lock.json, ~33 MB) into
    windows\models\diarization\: the pyannote segmentation-3.0 ONNX model (from a .tar.bz2, unpacked with the
    Windows tar.exe; only the pinned members are extracted and verified) and the 3D-Speaker ERes2Net embedding
    model. They are also fetched by a run without switches.

.PARAMETER Force
    Download and verify again even if a verified file is present.

.EXAMPLE
    .\windows\tools\fetch-models.ps1 -ServerOnly; .\windows\tools\fetch-models.ps1 -TestModel
#>
[CmdletBinding()]
param([switch]$TestModel, [switch]$ServerOnly, [switch]$Force, [switch]$Diarization)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($TestModel -and $ServerOnly) { throw 'Use either -TestModel or -ServerOnly, not both.' }
if ($Diarization -and ($TestModel -or $ServerOnly)) { throw 'Use -Diarization alone.' }

$windowsRoot = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'models.lock.json') | ConvertFrom-Json

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-Verified([string]$Path, [string]$Sha256) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-Sha256 $Path) -eq $Sha256.ToLowerInvariant())
}

function Save-Verified([string]$Url, [string]$Destination, [string]$Sha256, [long]$Size) {
    if (-not $Force -and (Test-Verified $Destination $Sha256)) {
        Write-Host "present      $Destination"
        return
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    $partial = "$Destination.partial"
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    Write-Host ('download     {0} ({1:N1} MB)' -f $Url, ($Size / 1MB))
    $curl = Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($curl) {
        & $curl.Source --fail --location --silent --show-error --retry 3 --output $partial $Url
        if ($LASTEXITCODE -ne 0) {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
            throw "Download failed: $Url"
        }
    } else {
        Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing
    }
    $actual = Get-Sha256 $partial
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $partial -Force
        throw "SHA-256 mismatch for $Url`n  expected $Sha256`n  actual   $actual"
    }
    Move-Item -LiteralPath $partial -Destination $Destination -Force
    Write-Host ('verified     {0} ({1:N1} MB, sha256 {2})' -f $Destination, ((Get-Item -LiteralPath $Destination).Length / 1MB), $actual)
}

function Install-WhisperServer($server) {
    $destination = Join-Path $windowsRoot $server.destination
    $files = @($server.files.PSObject.Properties)
    $missing = @($files | Where-Object { -not (Test-Verified (Join-Path $destination $_.Name) $_.Value) })
    if (-not $Force -and $missing.Count -eq 0) {
        Write-Host "present      $destination\whisper-server.exe ($($server.version), $($files.Count) files)"
        return
    }
    $work = Join-Path ([IO.Path]::GetTempPath()) ('wallfly-whisper-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $zip = Join-Path $work $server.archive
        Save-Verified $server.url $zip $server.sha256 $server.size
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            foreach ($file in $files) {
                $entry = $archive.GetEntry($server.archiveFolder + '/' + $file.Name)
                if ($null -eq $entry) { throw "$($file.Name) is missing from $($server.archive)." }
                $target = Join-Path $destination $file.Name
                $partial = "$target.partial"
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $partial, $true)
                $actual = Get-Sha256 $partial
                if ($actual -ne $file.Value.ToLowerInvariant()) {
                    Remove-Item -LiteralPath $partial -Force
                    throw "SHA-256 mismatch for $($file.Name)`n  expected $($file.Value)`n  actual   $actual"
                }
                Move-Item -LiteralPath $partial -Destination $target -Force
            }
        } finally { $archive.Dispose() }
        Write-Host "verified     $destination\whisper-server.exe ($($server.version) / $($server.build), $($files.Count) files)"
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Install-Diarization($section) {
    # Speaker diarization models (Wallfly.Diarization). Plain files are verified like every other download; an
    # archive is verified as a whole, then only the pinned members are extracted and verified one by one.
    $destination = Join-Path $windowsRoot $section.destination
    foreach ($file in @($section.files)) {
        if ($null -eq $file.archive) {
            Save-Verified $file.url (Join-Path $destination $file.name) $file.sha256 $file.size
            continue
        }
        $members = @($file.extract.PSObject.Properties)
        $missing = @($members | Where-Object { -not (Test-Verified (Join-Path $destination $_.Name) $_.Value) })
        if (-not $Force -and $missing.Count -eq 0) {
            Write-Host "present      $destination\$($file.name) ($($members.Count) files)"
            continue
        }
        $work = Join-Path ([IO.Path]::GetTempPath()) ('wallfly-diarization-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $work | Out-Null
        try {
            $archive = Join-Path $work $file.archive
            Save-Verified $file.url $archive $file.sha256 $file.size
            $unpacked = Join-Path $work 'unpacked'
            New-Item -ItemType Directory -Path $unpacked | Out-Null
            $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
            if (-not (Test-Path -LiteralPath $tar)) { $tar = (Get-Command tar.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
            & $tar -xjf $archive -C $unpacked @($members | ForEach-Object { $_.Name })
            if ($LASTEXITCODE -ne 0) { throw "Extracting $($file.archive) failed." }
            foreach ($member in $members) {
                $source = Join-Path $unpacked $member.Name
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "$($member.Name) is missing from $($file.archive)." }
                $actual = Get-Sha256 $source
                if ($actual -ne $member.Value.ToLowerInvariant()) {
                    throw "SHA-256 mismatch for $($member.Name)`n  expected $($member.Value)`n  actual   $actual"
                }
                $target = Join-Path $destination $member.Name
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                Move-Item -LiteralPath $source -Destination $target -Force
            }
            Write-Host "verified     $destination\$($file.name) ($($members.Count) files)"
        } finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($Diarization) { Install-Diarization $lock.diarization; return }
if (-not $TestModel) { Install-WhisperServer $lock.whisperServer }
if (-not $ServerOnly) {
    $selected = @($lock.models | Where-Object { [bool]$_.testOnly -eq [bool]$TestModel })
    foreach ($model in $selected) {
        Save-Verified $model.url (Join-Path (Join-Path $windowsRoot $model.destination) $model.name) $model.sha256 $model.size
    }
}
if (-not $TestModel -and -not $ServerOnly -and $null -ne $lock.diarization) { Install-Diarization $lock.diarization }
