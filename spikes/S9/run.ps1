<#
.SYNOPSIS
  Spike S9 (annotation display strategy): build and run every measurement. Throwaway.
.DESCRIPTION
  Offscreen only: Win2D CanvasDevice + CanvasRenderTarget on a chosen adapter (or WARP), PDFium via P/Invoke. No window is created,
  no input is sent, no registry or system setting is changed. Raw results go to spikes/S9/out (gitignored; the text results are force-added).
  Prerequisite: spikes/_shared/pdfium/fetch.ps1 -Variants pdfium-win-x64 and spikes/_shared/corpus/fetch.ps1 (for the PDFium resource PDFs).
#>
param([switch]$SkipOverlay)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build -c Release -nowarn:CS0649,CA2014 | Out-Host
$env:S9_OUT = "$PSScriptRoot\out"
$exe = "$PSScriptRoot\bin\Release\net10.0-windows10.0.26100.0\win-x64\S9.exe"
New-Item -ItemType Directory -Force $env:S9_OUT | Out-Null
if (-not $SkipOverlay) {
  # Q1. Overlay cost: three devices, N = 10/100/1000/5000, zoom 1/4/16, 7 ink strategies + 3 highlight strategies (about 40 minutes)
  & $exe overlay dgpu igpu warp *> "$env:S9_OUT\s9-overlay-run.log"
  python -I summarize.py "$env:S9_OUT\s9-overlay.csv" > "$env:S9_OUT\s9-overlay-pivots.md"
}
# Q2. Hidden flag: correctness against RemoveAnnot, persistence, save leak, cost, sprite of one annotation
& $exe hidden *> "$env:S9_OUT\s9-hidden-run.log"
& $exe hiddencost *> "$env:S9_OUT\s9-hidden-cost-run.log"
# Q3. Rebase: overlay vs a fresh PDFium render of the saved file, per kind/scale/rotation; double drawing; reopen cost
& $exe rebase *> "$env:S9_OUT\s9-rebase-run.log"
# Q4. Highlight: Multiply over the tile vs PDFium's appearance stream, plus the SourceOver fallback and PDFium's generated AP
& $exe highlight *> "$env:S9_OUT\s9-highlight-run.log"
# Pessimistic CPU proxy: the quick overlay matrix pinned to four E-cores (affinity mask 0xF00)
$env:S9_OUT = "$PSScriptRoot\out\pinned"; New-Item -ItemType Directory -Force $env:S9_OUT | Out-Null
cmd /c "start /wait /affinity F00 /b `"`" `"$exe`" overlay dgpu igpu --quick > `"$PSScriptRoot\out\s9-overlay-pinned-run.log`" 2>&1"
