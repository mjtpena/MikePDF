<#
.SYNOPSIS
  Fetch the PDFium build pinned in build/pdfium.lock for one architecture.
.DESCRIPTION
  Downloads (cached), verifies SHA-256, and extracts to build/out/pdfium/<arch>/.
  Prints the extraction directory. Fails closed on any hash mismatch.
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64'
)
$ErrorActionPreference = 'Stop'

$lock = Get-Content (Join-Path $PSScriptRoot 'pdfium.lock') -Raw | ConvertFrom-Json
$asset = $lock.assets.$Arch
if (-not $asset) { throw "pdfium.lock has no asset for $Arch" }

$out = Join-Path $PSScriptRoot "out/pdfium"
$cache = Join-Path $out '.cache'
$dest = Join-Path $out $Arch
New-Item -ItemType Directory -Force $cache | Out-Null

$stamp = Join-Path $dest '.sha256'
if ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $asset.sha256)) {
    Write-Output $dest
    return
}

$tgz = Join-Path $cache $asset.file
if (-not (Test-Path $tgz)) {
    Invoke-WebRequest -Uri "$($lock.baseUrl)/$($asset.file)" -OutFile $tgz -UseBasicParsing
}
$hash = (Get-FileHash -Algorithm SHA256 $tgz).Hash.ToLowerInvariant()
if ($hash -ne $asset.sha256) {
    Remove-Item $tgz -Force
    throw "SHA-256 mismatch for $($asset.file): expected $($asset.sha256), got $hash"
}

if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force $dest | Out-Null
tar -xzf $tgz -C $dest
if ($LASTEXITCODE -ne 0) { throw "tar failed for $($asset.file)" }
Set-Content -Path $stamp -Value $asset.sha256 -NoNewline
Write-Output $dest
