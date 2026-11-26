<#
.SYNOPSIS
  Downloads a portable C/C++ toolchain (llvm-mingw, CMake, Ninja) into windows/.tools without admin rights.
  Every archive is SHA-256-verified against the pinned value below before extraction.
.NOTES
  Re-run is idempotent: existing, verified installs are skipped. Use -Force to re-download.
#>
[CmdletBinding()]
param(
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$toolsDir = Join-Path $PSScriptRoot '..\.tools'
$toolsDir = (New-Item -ItemType Directory -Force $toolsDir).FullName
$downloads = (New-Item -ItemType Directory -Force (Join-Path $toolsDir 'downloads')).FullName

$packages = @(
    @{
        Name   = 'llvm-mingw'
        Url    = 'https://github.com/mstorsjo/llvm-mingw/releases/download/20261020/llvm-mingw-20261020-ucrt-x86_64.zip'
        Sha256 = 'fd5462b7f5d93b2ff981fcd3f9c7e427e47f3203c64ea91c27bcc444eebd74cd'
        Size   = 190612575
        # archive contains a single top-level folder; it is renamed to Target
        Target = 'llvm-mingw'
        Probe  = 'bin\clang++.exe'
    },
    @{
        Name   = 'cmake'
        Url    = 'https://github.com/Kitware/CMake/releases/download/v4.4.5/cmake-4.4.5-windows-x86_64.zip'
        Sha256 = '326c826486d46a23cf9d5a1c9ee3a567d427bdd02c4a34071937a2a675cff004'
        Size   = 54412232
        Target = 'cmake'
        Probe  = 'bin\cmake.exe'
    },
    @{
        Name   = 'ninja'
        Url    = 'https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip'
        Sha256 = '07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65'
        Size   = 291570
        Target = 'ninja'
        Probe  = 'ninja.exe'
        Flat   = $true   # archive has no top-level folder
    }
)

foreach ($p in $packages) {
    $targetDir = Join-Path $toolsDir $p.Target
    $probe = Join-Path $targetDir $p.Probe
    if ((Test-Path $probe) -and -not $Force) {
        Write-Host "[skip] $($p.Name) already present at $targetDir"
        continue
    }
    $zip = Join-Path $downloads ([System.IO.Path]::GetFileName($p.Url))
    if (-not (Test-Path $zip) -or $Force -or (Get-Item $zip).Length -ne $p.Size) {
        Write-Host "[download] $($p.Name) <- $($p.Url) ($([math]::Round($p.Size/1MB,1)) MB)"
        & "$env:SystemRoot\System32\curl.exe" -L --fail --silent --show-error --retry 3 --output $zip $p.Url
        if ($LASTEXITCODE -ne 0) { throw "curl failed for $($p.Url)" }
    }
    $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($hash -ne $p.Sha256) {
        Remove-Item $zip -Force
        throw "SHA-256 mismatch for $($p.Name): expected $($p.Sha256), got $hash. Archive deleted."
    }
    Write-Host "[verified] $($p.Name) sha256=$hash"
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
    if ($p.Flat) {
        New-Item -ItemType Directory -Force $targetDir | Out-Null
        & "$env:SystemRoot\System32\tar.exe" -xf $zip -C $targetDir
    } else {
        $staging = Join-Path $toolsDir ("_extract_" + $p.Name)
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        New-Item -ItemType Directory -Force $staging | Out-Null
        & "$env:SystemRoot\System32\tar.exe" -xf $zip -C $staging
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $zip" }
        $inner = Get-ChildItem $staging | Select-Object -First 1
        Move-Item $inner.FullName $targetDir
        Remove-Item $staging -Recurse -Force
    }
    if (-not (Test-Path $probe)) { throw "Extraction of $($p.Name) did not produce $probe" }
    Write-Host "[installed] $($p.Name) -> $targetDir"
}

Write-Host ""
Write-Host "Toolchain ready:"
& (Join-Path $toolsDir 'llvm-mingw\bin\clang++.exe') --version | Select-Object -First 1
& (Join-Path $toolsDir 'cmake\bin\cmake.exe') --version | Select-Object -First 1
Write-Host ("ninja " + (& (Join-Path $toolsDir 'ninja\ninja.exe') --version))
