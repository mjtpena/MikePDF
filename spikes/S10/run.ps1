<#
.SYNOPSIS
  S10: file replace semantics. Builds the probe and runs the local-disk parts. Raw output goes to spikes/S10/out/raw.
.PARAMETER Work
  Scratch root for the test files (default: a fresh folder under %TEMP%). Everything created there is deleted by the probe.
.PARAMETER ExtraRoot
  Optional second root (for example a folder inside a OneDrive-synced directory, or \\localhost\share\folder) to run the matrix and metadata parts on too.
.PARAMETER Label
  Name for the extra root in the output.
#>
param(
    [string] $Work = (Join-Path $env:TEMP 'mikepdf-s10'),
    [string] $ExtraRoot,
    [string] $Label = 'extra'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build Probe\S10.Probe.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$exe = Join-Path $PSScriptRoot 'out\build\S10.Probe\bin\Release\net10.0-windows\S10.Probe.exe'
$raw = Join-Path $PSScriptRoot 'out\raw'
New-Item -ItemType Directory -Force $raw, $Work | Out-Null

function Run([string] $name, [string[]] $probeArgs) {
    Remove-Item (Join-Path $raw "$name.jsonl") -ErrorAction SilentlyContinue
    & $exe @probeArgs *> (Join-Path $raw "$name.txt")
}

Run 'matrix-local' @('matrix', (Join-Path $Work 'matrix'), 'local', (Join-Path $raw 'matrix-local.jsonl'))
Run 'attack-local' @('attack', (Join-Path $Work 'attack'), (Join-Path $raw 'attack-local.jsonl'))
Run 'meta-local' @('meta', (Join-Path $Work 'meta'), 'local', (Join-Path $raw 'meta-local.jsonl'))
Run 'repeat-local' @('repeat', (Join-Path $Work 'repeat'), '30', (Join-Path $raw 'repeat-local.jsonl'))
Run 'netshare-local' @('netshare', (Join-Path $Work 'netshare'))
Run 'crash-local' @('crash', (Join-Path $Work 'crash'), (Join-Path $raw 'crash-local.jsonl'))
Run 'stress-local' @('stress', (Join-Path $Work 'stress'), '600', (Join-Path $raw 'stress-local.jsonl'))
if ($ExtraRoot) {
    Run "matrix-$Label" @('matrix', (Join-Path $ExtraRoot 'matrix'), $Label, (Join-Path $raw "matrix-$Label.jsonl"))
    Run "meta-$Label" @('meta', (Join-Path $ExtraRoot 'meta'), $Label, (Join-Path $raw "meta-$Label.jsonl"))
}
Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
