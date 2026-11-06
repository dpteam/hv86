<#
.SYNOPSIS
    One-shot verification of an hv86 build.

.DESCRIPTION
    Runs, in order:
      1. dotnet build
      2. --unit      micro tests (state format, disk blocks, json, paths)
      3. --selftest  embedded resources, WebView2 runtime, data dir, presets
      4. --smoke     real boot of a guest + save_state + restore  (needs WebView2)
      5. --itest     engine + vmhost + snapshots + watchdog restart (needs WebView2)

    Smoke needs a small bootable image. If -Image is not given, the FreeDOS
    720K floppy is downloaded once into the temp folder and its SHA-256 is
    verified against the value in assets\presets.json.

    Exit code 0 means everything passed.

.PARAMETER Image
    Path to a floppy/hard-disk image for the smoke test.

.PARAMETER SkipSmoke
    Skip step 4.

.PARAMETER SkipItest
    Skip step 5.

NOTE: keep this file ASCII-only (see fetch-webview2.ps1).
#>
[CmdletBinding()]
param(
    [string]$Image = '',
    [switch]$SkipSmoke,
    [switch]$SkipItest
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$dll = Join-Path $root 'bin\hv86.dll'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'hv86-selftest'
$env:HV86_ROOT = $tempRoot

$script:failed = 0

function Step([string]$name) {
    Write-Host ''
    Write-Host ("=== {0} " -f $name) -NoNewline
    Write-Host ('=' * [Math]::Max(0, (60 - $name.Length)))
}

function Record([string]$name, [int]$code) {
    if ($code -eq 0) {
        Write-Host ("[{0}] ok" -f $name) -ForegroundColor Green
    }
    else {
        Write-Host ("[{0}] FAILED (exit {1})" -f $name, $code) -ForegroundColor Red
        $script:failed++
    }
}

# ---------------------------------------------------------------- 1. build

Step 'build'
& dotnet build hv86.csproj -c Debug -v minimal
Record 'build' $LASTEXITCODE

if (-not (Test-Path $dll)) {
    Write-Host '[FATAL] bin\hv86.dll not found' -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- 2. unit

Step 'unit'
& dotnet $dll --unit
Record 'unit' $LASTEXITCODE

# ---------------------------------------------------------------- 3. selftest

Step 'selftest'
& dotnet $dll --selftest
Record 'selftest' $LASTEXITCODE

# ---------------------------------------------------------------- 4. smoke

if (-not $SkipSmoke) {
    Step 'smoke'

    if ($Image -eq '') {
        $Image = Join-Path ([System.IO.Path]::GetTempPath()) 'hv86-smoke-freedos.img'
    }

    if (-not (Test-Path $Image)) {
        Write-Host "downloading FreeDOS image ..."
        $url = 'https://i.copy.sh/freedos722.img'
        $web = New-Object Net.WebClient
        $web.Headers.Add('User-Agent', 'hv86-selftest')
        $web.DownloadFile($url, $Image)
    }

    $hash = (Get-FileHash -Path $Image -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = '8ecc7604d4c17c16e136d219a92e64747196d9ae044690e90be9ca0468b1ff12'

    if ($hash -ne $expected) {
        Write-Host ("[FAIL] image hash mismatch: {0}" -f $hash) -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host ("image ok: {0}" -f $Image)
        & dotnet $dll --smoke $Image
        Record 'smoke' $LASTEXITCODE
    }
}

# ---------------------------------------------------------------- 5. itest

if (-not $SkipItest) {
    Step 'itest'
    & dotnet $dll --itest
    Record 'itest' $LASTEXITCODE
}

# ---------------------------------------------------------------- summary

Write-Host ''
if ($script:failed -eq 0) {
    Write-Host 'SELFTEST: OK' -ForegroundColor Green
    exit 0
}

Write-Host ("SELFTEST: FAILED ({0})" -f $script:failed) -ForegroundColor Red
exit 1