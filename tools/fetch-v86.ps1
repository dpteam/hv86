<#
.SYNOPSIS
    Downloads v86 (libv86.js + v86.wasm + BIOS) into assets\v86\<version>\.

.DESCRIPTION
    v86 is pinned by version: emulator state snapshots are not compatible across
    versions, so the version is stored in vm.json, in every snapshot meta.json
    and in manifest.json.

    Sources:
      * build/libv86.js, build/v86.wasm - from the npm tarball of package "v86";
      * bios/seabios.bin, bios/vgabios.bin - from the copy/v86 repository
        (not shipped in the npm package).

    Output:
      assets\v86\<version>\libv86.js
      assets\v86\<version>\v86.wasm
      assets\v86\<version>\seabios.bin
      assets\v86\<version>\vgabios.bin
      assets\v86\manifest.json      (version + sizes + SHA-256)

    NOTE: keep this file ASCII-only (see fetch-webview2.ps1).

.PARAMETER Version
    npm package version of v86.

.PARAMETER Force
    Re-download even if the files already exist.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.5.473',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root     = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$v86Root  = Join-Path $root 'assets\v86'
$outDir   = Join-Path $v86Root $Version
$manifest = Join-Path $v86Root 'manifest.json'

$files = @('libv86.js', 'v86.wasm', 'seabios.bin', 'vgabios.bin')
$have  = @($files | Where-Object { Test-Path (Join-Path $outDir $_) })

if (-not $Force -and $have.Count -eq $files.Count) {
    Write-Host "[ok] v86 $Version already present - skipping"
    exit 0
}

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("hv86-v86-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $tgz    = Join-Path $temp "v86-$Version.tgz"
    $tgzUrl = "https://registry.npmjs.org/v86/-/v86-$Version.tgz"

    Write-Host "[1/3] downloading v86 $Version (npm) ..."
    $wc = New-Object Net.WebClient
    $wc.Headers.Add('User-Agent', 'hv86-build')
    $wc.DownloadFile($tgzUrl, $tgz)

    # npm tarball is gzipped tar, not zip: ZipFile.OpenRead() cannot read it.
    # bsdtar (tar.exe) ships with Windows 10 1803+.
    Write-Host "[2/3] extracting ..."
    & tar -xzf $tgz -C $temp
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] tar failed with code $LASTEXITCODE" -ForegroundColor Red
        exit 1
    }

    foreach ($item in @(
        @{ entry = 'package/build/libv86.js'; dest = (Join-Path $outDir 'libv86.js') },
        @{ entry = 'package/build/v86.wasm';  dest = (Join-Path $outDir 'v86.wasm')  }
    )) {
        $src = Join-Path $temp ($item.entry.Replace('/', '\'))
        if (-not (Test-Path $src)) {
            Write-Host "[ERROR] npm package has no '$($item.entry)'" -ForegroundColor Red
            exit 1
        }
        Copy-Item -Path $src -Destination $item.dest -Force
        Write-Host ("       {0}" -f $item.entry)
    }

    foreach ($bios in @('seabios.bin', 'vgabios.bin')) {
        $url  = "https://raw.githubusercontent.com/copy/v86/master/bios/$bios"
        $dest = Join-Path $outDir $bios
        Write-Host ("       {0}" -f $url)
        try {
            $wc.DownloadFile($url, $dest)
        } catch {
            Write-Host "[ERROR] download failed: $url : $($_.Exception.Message)" -ForegroundColor Red
            exit 1
        }
    }
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "[3/3] computing SHA-256 ..."
$items = @()
foreach ($name in $files) {
    $path = Join-Path $outDir $name
    $item = Get-Item $path
    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $items += [pscustomobject]@{ file = $name; size = $item.Length; sha256 = $hash }
    Write-Host ("       {0,-14} {1,10} bytes  {2}" -f $name, $item.Length, $hash)
}

$doc = [pscustomobject]@{
    version = $Version
    source  = "https://github.com/copy/v86"
    license = "BSD-2-Clause"
    fetched = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    files   = $items
}

$json = $doc | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifest, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "done: assets\v86\$Version + assets\v86\manifest.json"
exit 0