<#
.SYNOPSIS
    Boot-tests old-dos.ru presets in the headless engine and records what the
    guest actually printed on the VGA screen.

.DESCRIPTION
    "One image inside the archive" (tools\old-dos-scan.mjs) does not mean it
    boots: a Windows 1.0 setup diskette is a single image too. This script
    creates a VM from the preset, starts it in the headless engine, reads the
    text screen and stops the VM. A preset that shows nothing but BIOS output
    is not "suitable in every sense" and must not go into the catalog.

    Needs Node 20+ and engine\node_modules (tools\fetch-engine.ps1).

.PARAMETER Presets
    Comma-separated preset ids (olddos-*).

.PARAMETER Seconds
    How long to let the guest boot before reading the screen (default 20).

NOTE: keep this file ASCII-only (see fetch-webview2.ps1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Presets,
    [int]$Seconds = 20,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

if ($Out -eq '') { $Out = Join-Path ([IO.Path]::GetTempPath()) 'hv86-boot-test.txt' }

$dll = Join-Path $root 'bin\hv86.dll'
$report = @()

function RunCli([string[]]$arguments) {
    # stderr команды не должен валить скрипт: "ВМ не найдена" при уборке - норма
    $ErrorActionPreference = 'Continue'

    $output = & dotnet $dll --cli @arguments 2>&1 | Out-String
    return @{ text = $output; code = $LASTEXITCODE }
}

# CLI ходит по каналу \\.\pipe\hv86, поэтому нужен движок: если его нет -
# поднимаем свой (--engine) и гасим в конце
$engine = $null

$probe = RunCli @('status', '--json')
if ($probe.text -notmatch '"product"') {
    Write-Host 'starting engine ...'
    $engine = Start-Process -FilePath 'dotnet' -ArgumentList $dll, '--engine' -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
}

try {

foreach ($id in ($Presets -split ',')) {
    $preset = $id.Trim()
    if ($preset -eq '') { continue }

    $name = 'bt-' + $preset
    Write-Host ("=== {0}" -f $preset)

    $null = RunCli @('delete', $name)      # на случай прошлого прогона

    $created = RunCli @('create', '--name', $name, '--preset', $preset, '--engine', 'headless')
    if ($created.code -ne 0) {
        $report += ("{0}`tcreate failed`t{1}" -f $preset, ($created.text.Trim()))
        continue
    }

    $started = RunCli @('start', $name)
    if ($started.code -ne 0) {
        $report += ("{0}`tstart failed`t{1}" -f $preset, ($started.text.Trim()))
        $null = RunCli @('delete', $name)
        continue
    }

    Start-Sleep -Seconds $Seconds

    $screen = RunCli @('screen', $name)
    $lines = ($screen.text -split "`r?`n") | Where-Object { $_.Trim().Length -gt 0 }
    $tail = ($lines | Select-Object -Last 6) -join ' | '

    $null = RunCli @('stop', $name)
    $null = RunCli @('delete', $name)

    $report += ("{0}`t{1}" -f $preset, $tail)
    Write-Host ("    {0}" -f $tail)
}

}
finally {
    if ($engine -ne $null) {
        Stop-Process -Id $engine.Id -Force -ErrorAction SilentlyContinue
        Write-Host 'engine stopped'
    }
}

[IO.File]::WriteAllLines($Out, $report, (New-Object Text.UTF8Encoding($false)))
Write-Host ''
Write-Host ("report: {0}" -f $Out)