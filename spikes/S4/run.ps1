# S4 save-fidelity spike: end-to-end run.
#   1. gen    : build fixtures, apply every operation through PDFium, save incrementally and as a full copy -> out/files, out/manifest.json
#   2. pdfjs  : render every output headlessly with pdf.js (Firefox's engine) and diff against the unmodified input -> out/render/pdfjs, out/pdfjs-results.json
#   3. check  : qpdf --check, object-level inspection, fresh-PDFium reopen, PDFium render, signature checks, corpus probe
#               -> out/results.json, out/matrix.md, out/matrix-compact.md, out/probe.md, out/manual-acrobat-checklist.md
#   4. montage: contact sheets for eyeballing -> out/sheets
# Prerequisites: .NET SDK 10, Node, spikes/_shared/pdfium/pdfium-win-x64 (fetch.ps1), spikes/_shared/tools/qpdf (see tools SOURCE.txt),
#                `npm install` in spikes/S4/pdfjs.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'out'
Remove-Item -Recurse -Force (Join-Path $out 'files'), (Join-Path $out 'render'), (Join-Path $out 'sheets'), (Join-Path $out 'probe') -ErrorAction SilentlyContinue
dotnet build (Join-Path $here 'gen\Gen.csproj') -c Release --nologo -v q
dotnet build (Join-Path $here 'check\Check.csproj') -c Release --nologo -v q
dotnet (Join-Path $here 'gen\bin\Release\net10.0\S4.Gen.dll') $out
Push-Location (Join-Path $here 'pdfjs')
node render.mjs $out
Pop-Location
dotnet (Join-Path $here 'check\bin\Release\net10.0\S4.Check.dll') $out
Push-Location (Join-Path $here 'pdfjs')
node montage.mjs $out
Pop-Location
