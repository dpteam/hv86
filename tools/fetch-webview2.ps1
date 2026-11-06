<#
.SYNOPSIS
    Downloads the WebView2 SDK (managed DLLs) into lib\webview2.

.DESCRIPTION
    The NuGet package Microsoft.Web.WebView2 is unpacked by hand because the
    project references the DLLs via HintPath (no PackageReference, AGENTS.md #2).

    Output:
      lib\webview2\Microsoft.Web.WebView2.Core.dll
      lib\webview2\Microsoft.Web.WebView2.WinForms.dll
      lib\webview2\WebView2Loader.dll      (x64, next to the exe)
      lib\webview2\version.txt

    NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 files in
    the OEM/ANSI code page when there is no BOM, and non-ASCII text breaks parsing.

.PARAMETER Version
    SDK version (pinned default).

.PARAMETER Force
    Re-download even if the files already exist.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.4314.35',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root        = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$libDir      = Join-Path $root 'lib\webview2'
$versionFile = Join-Path $libDir 'version.txt'

$coreDll = Join-Path $libDir 'Microsoft.Web.WebView2.Core.dll'
$wfDll   = Join-Path $libDir 'Microsoft.Web.WebView2.WinForms.dll'
$loader  = Join-Path $libDir 'WebView2Loader.dll'

if (-not $Force -and (Test-Path $coreDll) -and (Test-Path $wfDll) -and (Test-Path $loader)) {
    $have = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '?' }
    Write-Host "[ok] WebView2 SDK $have already present - skipping"
    exit 0
}

if (-not (Test-Path $libDir)) { New-Item -ItemType Directory -Path $libDir -Force | Out-Null }

$url   = "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$Version"
$temp  = Join-Path ([System.IO.Path]::GetTempPath()) ("hv86-wv2-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$nupkg = Join-Path $temp 'wv2.nupkg'

Write-Host "[1/3] downloading WebView2 SDK $Version ..."
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $wc = New-Object Net.WebClient
    $wc.Headers.Add('User-Agent', 'hv86-build')
    $wc.DownloadFile($url, $nupkg)
} catch {
    Write-Host "[ERROR] download failed: $url" -ForegroundColor Red
    Write-Host "        $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

if ((Get-Item $nupkg).Length -lt 100000) {
    Write-Host "[ERROR] package is suspiciously small - probably an error page" -ForegroundColor Red
    exit 1
}

Write-Host "[2/3] extracting ..."
$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
try {
    # .NET 9 (net*-windows) uses lib_manual/netcoreapp3.0 - built for .NET Core 3.0+,
    # compatible with .NET 5+. lib/net462 is Framework-only.
    function Find-Entry([string]$fileName) {
        $cands = @($zip.Entries | Where-Object {
            $p = $_.FullName.Replace('\', '/')
            $p.EndsWith('/' + $fileName) -and (
                $p -like "lib_manual/netcoreapp3.0/*" -or
                $p -like "lib/net*/*"
            )
        })
        if ($cands.Count -eq 0) { return $null }

        foreach ($pref in @('lib_manual/netcoreapp3.0/', 'lib/net462/', 'lib/net45/')) {
            $hit = $cands | Where-Object { $_.FullName.Replace('\','/').StartsWith($pref) } | Select-Object -First 1
            if ($hit) { return $hit }
        }
        return ($cands | Sort-Object FullName | Select-Object -First 1)
    }

    $plan = @(
        @{ name = 'Microsoft.Web.WebView2.Core.dll';     dest = $coreDll },
        @{ name = 'Microsoft.Web.WebView2.WinForms.dll'; dest = $wfDll   }
    )
    foreach ($item in $plan) {
        $entry = Find-Entry $item.name
        if (-not $entry) {
            Write-Host "[ERROR] package has no '$($item.name)'" -ForegroundColor Red
            exit 1
        }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $item.dest, $true)
        Write-Host ("       {0} <- {1}" -f $item.name, $entry.FullName)
    }

    $loaderEntry = $zip.Entries | Where-Object {
        $_.FullName.Replace('\','/') -eq 'runtimes/win-x64/native/WebView2Loader.dll'
    } | Select-Object -First 1
    if (-not $loaderEntry) {
        Write-Host "[ERROR] package has no 'runtimes/win-x64/native/WebView2Loader.dll'" -ForegroundColor Red
        exit 1
    }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($loaderEntry, $loader, $true)
    Write-Host ("       WebView2Loader.dll <- {0}" -f $loaderEntry.FullName)
} finally {
    $zip.Dispose()
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Set-Content -Path $versionFile -Value $Version -Encoding ASCII

Write-Host "[3/3] done: WebView2 SDK $Version in lib\webview2"
exit 0