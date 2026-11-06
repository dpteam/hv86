<#
.SYNOPSIS
    Installs the Node dependencies of the headless engine.

.DESCRIPTION
    The headless engine (engine\vmhost.mjs) runs v86 under Node, so it needs
    the "v86" npm package. The version is pinned in engine\package.json -
    state snapshots are not compatible between v86 versions, exactly like in
    the WebView2 engine.

    Requirements: Node.js 20+ and npm in PATH. The engine itself is optional:
    without it only the WebView2 engine works (VMs need a logged-in user).

    Result:
      engine\node_modules\v86\...        (v86 package, ~4 MB)
      engine\package-lock.json           (pinned dependency tree)

.PARAMETER Force
    Reinstall even if node_modules already exists.

NOTE: keep this file ASCII-only (see fetch-webview2.ps1).
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$engine = Join-Path $root 'engine'

if (-not (Test-Path $engine)) {
    Write-Host "[ERROR] engine directory not found: $engine" -ForegroundColor Red
    exit 1
}

$node = Get-Command node -ErrorAction SilentlyContinue
$npm  = Get-Command npm -ErrorAction SilentlyContinue

if (-not $node) {
    Write-Host "[ERROR] Node.js not found in PATH. Install Node 20+ or put node.exe into engine\node\" -ForegroundColor Red
    exit 1
}

if (-not $npm) {
    Write-Host "[ERROR] npm not found in PATH" -ForegroundColor Red
    exit 1
}

$version = (& node --version).Trim()
Write-Host "[1/2] node $version"

$v86 = Join-Path $engine 'node_modules\v86'

if (-not $Force -and (Test-Path $v86)) {
    Write-Host "[ok] v86 already installed - skipping (use -Force to reinstall)"
    exit 0
}

Write-Host "[2/2] npm install in engine ..."

Push-Location $engine
try {
    & npm install --silent
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] npm install failed with code $LASTEXITCODE" -ForegroundColor Red
        exit 1
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path (Join-Path $v86 'build\v86.wasm'))) {
    Write-Host "[ERROR] v86 package looks broken: build\v86.wasm is missing" -ForegroundColor Red
    exit 1
}

Write-Host "done: engine\node_modules\v86"
exit 0