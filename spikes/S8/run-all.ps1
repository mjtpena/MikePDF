<#
.SYNOPSIS
  Re-creates every S8 artefact: build, PDFium enumeration, fixtures, Quire + pyHanko validation, classification table, probes.
.NOTES
  Test certificates are generated fresh on every run (in memory, never imported into a certificate store), so ALL outputs under out/ must be
  produced by one run. Needs: .NET SDK 10, spikes/_shared/pdfium (P0), spikes/_shared/corpus (fetch.ps1), and
  `python -m venv .venv; .venv\Scripts\pip install pyhanko pyhanko-certvalidator` in this directory.
  Network: none after the package installs. The revocation probe talks to 127.0.0.1 only.
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$py = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
$b = Join-Path $PSScriptRoot 'out\build'
$fix = 'out\fixtures'

foreach ($p in 'enum\S8.Enum.csproj', 'validate\S8.Validate.csproj', 'fixtures\S8.Fixtures.csproj') {
    dotnet build $p -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
}
$F = "$b\S8.Fixtures\bin\Release\net10.0\S8.Fixtures.exe"
$E = "$b\S8.Enum\bin\Release\net10.0\S8.Enum.exe"

& $E ..\_shared\corpus\pdfium-resources --out out\enum | Tee-Object out\enum-corpus.txt | Select-Object -First 1
& $F gen $fix
& $py -I crossvalidate\pyhanko_sign.py $fix
& $F post-pyhanko $fix
& $F validate $fix | Tee-Object out\quire-validate.txt | Select-Object -Last 2
& $F audit $fix
& $E $fix --out out\enum-fixtures | Select-Object -First 1
& $py -I crossvalidate\pyhanko_validate.py $fix 2>$null | Out-Null
& $py -I crossvalidate\classify.py $fix | Out-Null
& $py -I crossvalidate\enum_compare.py $fix out\enum-fixtures
& $F bench $fix | Out-Null
& $F cng $fix
& $F revocation $fix
& $F corpus-sign $fix
& $E out\corpus-signed --out out\enum-corpus-signed | Select-Object -First 1
& $py -I crossvalidate\pyhanko_validate_dir.py out\corpus-signed 2>$null
Get-Content "$fix\classification-stats.json"
# S8.2 structure-parser coverage over the PDFium corpus (343 files, many malformed by design) and over the PDFs shipped with a local Acrobat install (read-only, nothing copied)
$V = "$b\S8.Validate\bin\Release\net10.0\S8.Validate.exe"
& $V ..\_shared\corpus\pdfium-resources --json out\corpus\validate-corpus.json | Out-Null
if (Test-Path 'C:\Program Files\Adobe\Acrobat DC') { & $V 'C:\Program Files\Adobe\Acrobat DC' --json out\corpus\adobe-install-pdfs.json | Out-Null }
