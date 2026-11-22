# Renders docs/banner.html to docs/banner.png (1920x1080) and docs/social-preview.png (1920x960,
# for the repository's Settings > Social preview) with headless Chrome or Edge.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$html = Join-Path $root 'docs\banner.html'
$png = Join-Path $root 'docs\banner.png'
$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Install Chrome or Edge to render the banner.' }
function Render([string]$url, [string]$out, [int]$height) {
    $profile = Join-Path ([IO.Path]::GetTempPath()) "clearcut-banner-$PID"
    & $browser --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
        --window-size=1920,$height --virtual-time-budget=10000 "--user-data-dir=$profile" `
        "--screenshot=$out" $url | Out-Null
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Rendered $out"
}
$uri = ([Uri]$html).AbsoluteUri
Render $uri $png 1080
Render "$uri`?social" (Join-Path $root 'docs\social-preview.png') 960
