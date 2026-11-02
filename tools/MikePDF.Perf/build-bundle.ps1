<#
.SYNOPSIS
  Build the native engine and the perf harness of one checkout and gather exactly what a measurement needs into one folder.
.DESCRIPTION
  The same-runner A/B gate (.github/workflows/perf.yml, docs/perf/README.md) measures two builds in one job: the base and the head. This script
  builds either of them, and it is always the head's copy of the script that does so, because the base may be older than the script.
  Only what the harness needs is built: the engine (build.ps1 -SkipManaged) and tools/MikePDF.Perf with the projects it references. No app, no tests.

  The bundle is relocatable, which is what makes it cacheable by commit:
    <Out>/engine/MikePDF.Engine.exe, pdfium.dll
    <Out>/harness/MikePDF.Perf.exe and its dependencies
    <Out>/bundle.json                      commit, architecture, and when it was built
.PARAMETER Source
  The checkout to build (a git worktree of the commit).
.PARAMETER Out
  The folder to fill. It is emptied first.
.PARAMETER Arch
  x64 or arm64 (the host's architecture: the harness runs the engine).
.PARAMETER SeedPdfiumFrom
  Another checkout whose downloaded PDFium archive is copied in first, so that the second build does not download it again. build/fetch-pdfium.ps1 still verifies the SHA-256.
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Out,
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $SeedPdfiumFrom
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Source = (Resolve-Path $Source).Path
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force (Join-Path $Out 'engine'), (Join-Path $Out 'harness') | Out-Null
$Out = (Resolve-Path $Out).Path

if ($SeedPdfiumFrom) {
    $cache = Join-Path $SeedPdfiumFrom 'build/out/pdfium/.cache'
    if (Test-Path $cache) {
        $target = Join-Path $Source 'build/out/pdfium/.cache'
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item -Path (Join-Path $cache '*') -Destination $target -Recurse -Force
    }
}

Push-Location $Source
try {
    $commit = (git rev-parse HEAD).Trim()
    Write-Host "==> Engine of $commit ($Arch)" -ForegroundColor Cyan
    & (Join-Path $Source 'build.ps1') -Configuration Release -Arch $Arch -SkipManaged
    if (-not $?) { throw 'the engine build failed' }

    Write-Host "==> Harness of $commit ($Arch)" -ForegroundColor Cyan
    dotnet build tools/MikePDF.Perf/MikePDF.Perf.csproj -c Release "-p:MikePDFArch=$Arch"
    if ($LASTEXITCODE -ne 0) { throw 'the harness build failed' }

    $engineBin = Join-Path $Source "build/out/cmake-$Arch/bin/Release"
    foreach ($file in 'MikePDF.Engine.exe', 'pdfium.dll') {
        Copy-Item (Join-Path $engineBin $file) (Join-Path $Out 'engine') -ErrorAction Stop
    }

    $exe = Get-ChildItem (Join-Path $Source 'tools/MikePDF.Perf/bin/Release') -Recurse -Filter MikePDF.Perf.exe | Select-Object -First 1
    if (-not $exe) { throw 'MikePDF.Perf.exe was not built' }
    Copy-Item -Path (Join-Path $exe.DirectoryName '*') -Destination (Join-Path $Out 'harness') -Recurse -Force

    [pscustomobject]@{ commit = $commit; arch = $Arch; builtUtc = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $Out 'bundle.json')
}
finally {
    Pop-Location
}

# The compiler servers are still winding down after a build; the harness would be timing them.
dotnet build-server shutdown | Out-Null
Write-Host "Bundle ready: $Out"
