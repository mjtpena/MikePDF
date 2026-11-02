<#
.SYNOPSIS
  Task P0: fetch the pinned prebuilt PDFium (bblanchon/pdfium-binaries) for spikes.
.DESCRIPTION
  Downloads each pinned asset, verifies its SHA-256 against the pin below, and
  extracts it to spikes/_shared/pdfium/<variant>/. Extracted files are gitignored.
  Pins are recorded in docs/adr/0001-platform-baseline.md. Never used by production.
#>
param(
    [string[]] $Variants = @('pdfium-win-x64', 'pdfium-win-arm64', 'pdfium-v8-win-x64')
)
$ErrorActionPreference = 'Stop'

$Release = 'chromium/8125'   # PDFium 158.0.8125.0, published 2026-10-26
$Pins = @{
    'pdfium-win-x64'      = '9a964261760cff52570aabf038ab346d3a5034dd8b30d0d5fe8861235d356b8b'
    'pdfium-win-arm64'    = 'd394ba61991fb53f4d87f70988035f337d9e73f2bd2c3c1b966df8c43fcabb05'
    'pdfium-v8-win-x64'   = '476b34fd2c1aca811a02960ecb04fff9019c75fd308d70f1b9faa2b33e777557'
    'pdfium-v8-win-arm64' = '9d310dda25c14424c4308b89643c4273e87dd3bb37d205cc18f053c55637d5a0'
}

$root = $PSScriptRoot
$cache = Join-Path $root '.cache'
New-Item -ItemType Directory -Force $cache | Out-Null

foreach ($v in $Variants) {
    if (-not $Pins.ContainsKey($v)) { throw "No pin for variant '$v'" }
    $tgz = Join-Path $cache "$v.tgz"
    if (-not (Test-Path $tgz)) {
        $url = "https://github.com/bblanchon/pdfium-binaries/releases/download/$Release/$v.tgz"
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $tgz -UseBasicParsing
    }
    $hash = (Get-FileHash -Algorithm SHA256 $tgz).Hash.ToLowerInvariant()
    if ($hash -ne $Pins[$v]) {
        Remove-Item $tgz -Force
        throw "SHA-256 mismatch for ${v}: expected $($Pins[$v]), got $hash"
    }
    $dest = Join-Path $root $v
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Force $dest | Out-Null
    tar -xzf $tgz -C $dest
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $v" }
    Write-Host "OK $v -> $dest (sha256 $hash)"
}
