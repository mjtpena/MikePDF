<#
.SYNOPSIS
  Fetch the spike fixture corpus: PDFium's own testing/resources at the pinned branch.
.DESCRIPTION
  PDFium test resources are covered by PDFium's BSD-3-Clause licence (see
  spikes/_shared/pdfium/pdfium-win-x64/licenses/pdfium.txt). Extracted to
  spikes/_shared/corpus/pdfium-resources/ (gitignored). Spikes only; the real
  corpus manifest is ADR-0010 / M1 work.
#>
$ErrorActionPreference = 'Stop'
$Branch = 'chromium/8125'
$root = $PSScriptRoot
$tools = Join-Path $root '..\tools\dl'
New-Item -ItemType Directory -Force $tools | Out-Null
$tgz = Join-Path $tools 'pdfium-testing-resources.tgz'
if (-not (Test-Path $tgz)) {
    $url = "https://pdfium.googlesource.com/pdfium/+archive/refs/heads/$Branch/testing/resources.tar.gz"
    Invoke-WebRequest -Uri $url -OutFile $tgz -UseBasicParsing
}
$dest = Join-Path $root 'pdfium-resources'
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force $dest | Out-Null
tar -xzf $tgz -C $dest
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
$n = (Get-ChildItem -Recurse -Filter *.pdf $dest).Count
Write-Host "OK: $n PDF files in $dest (sha256 of archive: $((Get-FileHash -Algorithm SHA256 $tgz).Hash.ToLowerInvariant()))"
