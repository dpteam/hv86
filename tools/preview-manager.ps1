<#
.SYNOPSIS
    Builds .preview\manager.html from web\* so the interface can be looked at
    in a normal browser (no exe, no service).

.DESCRIPTION
    The page detects that chrome.webview is missing and switches to demo data
    (see preview() in manager.js), so the layout, the table, the action pane
    and the event log can be reviewed without building anything.

    This is a development helper, not part of the shipped application.
#>
[CmdletBinding()]
param(
    [string]$Open = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$web  = Join-Path $root 'web'
$out  = Join-Path $root '.preview'

if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out -Force | Out-Null }

$html = [System.IO.File]::ReadAllText((Join-Path $web 'manager.html'))
$css  = [System.IO.File]::ReadAllText((Join-Path $web 'theme.css'))
$js   = [System.IO.File]::ReadAllText((Join-Path $web 'manager.js'))
$ru   = [System.IO.File]::ReadAllText((Join-Path $web 'i18n\ru.json'))
$en   = [System.IO.File]::ReadAllText((Join-Path $web 'i18n\en.json'))

$html = $html.Replace('/*{{THEME}}*/', $css)
$html = $html.Replace('//{{JS}}', $js)
$html = $html.Replace('//{{I18N}}', 'window.I18N = { "ru": ' + $ru + ', "en": ' + $en + ' };')
$html = $html.Replace('{{VERSION}}', '0.1.0')

$target = Join-Path $out 'manager.html'
[System.IO.File]::WriteAllText($target, $html, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "preview: $target"

if ($Open -ne '' -and $Open -ne 'no') {
    Start-Process $target
}

exit 0