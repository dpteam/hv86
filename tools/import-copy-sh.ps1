<#
.SYNOPSIS
    Builds assets\presets-copy.json from the copy.sh/v86 image list.

.DESCRIPTION
    The image list lives in the minified site bundle. Extract it first:
        node tools\copy-sh-list.mjs <v86_all.js> list.json
    Then:
        powershell -File tools\import-copy-sh.ps1 -List list.json

    We keep only entries we can actually run: exactly one media (fda/hda/cdrom),
    no initial_state, no 9p root, no bzImage/multiboot kernels. Big proprietary
    images (Windows 9x/2000/XP, Android) are included as URLs only - nothing is
    downloaded or stored here, and no hashes are baked in (they are pinned on
    first download, see PresetCatalog.EnsureLocalFile).

    Sizes come from HEAD requests, so the UI can show them before downloading.

.PARAMETER List
    JSON produced by tools\copy-sh-list.mjs.

.PARAMETER Out
    Target file (default assets\presets-copy.json).

NOTE: keep this file ASCII-only (see fetch-webview2.ps1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$List,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ($Out -eq '') { $Out = Join-Path $root 'assets\presets-copy.json' }

$items = Get-Content $List -Raw -Encoding UTF8 | ConvertFrom-Json

$boot = @{
    fda   = 'FLOPPY_CD_HARDDISK'
    cdrom = 'CD_HARDDISK_FLOPPY'
    hda   = 'HARDDISK_CD_FLOPPY'
}

$presets = @()
$seen = @{}

foreach ($item in $items) {
    if ($item.state -or $item.has_filesystem -or $item.bzimage -or $item.initrd -or $item.multiboot -or $item.from_filesystem) { continue }

    $kinds = @($item.media.PSObject.Properties.Name)
    if ($kinds.Count -ne 1) { continue }

    $kind = $kinds[0]
    $url = $item.media.$kind

    if ($url.StartsWith('//')) { $url = 'https:' + $url }

    $id = 'v86-' + ($item.id -replace '[^a-z0-9\-]', '')
    if ($seen.ContainsKey($id)) { continue }
    $seen[$id] = $true

    # K=0 в каталоге сайта означает "по умолчанию": для флоппи берём 32 МБ
    # (DOS-гостям больше не нужно), для дисков и CD - 128
    $memory = [int]$item.memory_mb
    if ($memory -le 0) { $memory = if ($kind -eq 'fda') { 32 } else { 128 } }
    if ($memory -gt 1024) { $memory = 1024 }

    $vga = [int]$item.vga_mb
    if ($vga -le 0) { $vga = 8 }
    if ($vga -gt 32) { $vga = 32 }

    $size = 0

    try {
        $head = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 30
        if ($head.Headers['Content-Length']) { $size = [long]$head.Headers['Content-Length'] }
    }
    catch {
        Write-Host ("[warn] HEAD failed: {0}" -f $url) -ForegroundColor Yellow
    }

    $presets += [pscustomobject]@{
        id             = $id
        name           = $item.name
        kind           = $kind
        url            = $url
        size           = $size
        sha256         = ''
        memory_mb      = $memory
        vga_memory_mb  = $vga
        disk_mb        = 0
        boot           = $boot[$kind]
        net            = $item.net
        description    = ("copy.sh/v86 demo image, " + $kind + ".")
    }

    Write-Host ("  {0,-22} {1,-6} {2,10} bytes" -f $id, $kind, $size)
}

$doc = [pscustomobject]@{
    version = 1
    source  = 'https://copy.sh/v86/'
    note    = 'Images from the official v86 demo stand. Nothing is bundled: downloaded on demand. sha256 is empty until the first download pins it (see PresetCatalog).'
    presets = $presets
}

$json = $doc | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host ("presets: {0} -> {1}" -f $presets.Count, $Out)