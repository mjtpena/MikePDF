<#
.SYNOPSIS
  S1.2 (NOT EXECUTED: BLOCKED on this machine): build pdfium.dll from source for one config + arch.
.DESCRIPTION
  Sources: pdfium-binaries steps/03-patch.sh, 05-configure.sh, 06-build.sh and patches/
  (shared_library.patch, public_headers.patch, clang_rt.patch, win/build.patch, win/resources.rc):
  https://github.com/bblanchon/pdfium-binaries
  Upstream PDFium declares `component("pdfium")`, so is_component_build=false yields a static library and
  FPDF_EXPORT expands to nothing. pdfium-binaries therefore patches BUILD.gn to shared_library("pdfium") and
  fpdfview.h to always export; Quire needs the same (or its own equivalent patch).
  Run fetch.ps1 first. ARM64 is a cross-compile from an x64 host (target_cpu="arm64").
  Writes out/work/metrics-<config>-<arch>.json (build seconds, dll size) for the S1 report.
  Own DLLs are then analysed with:  python symdiff.py --extra own-x64=<dll>
#>
param(
    [Parameter(Mandatory)][ValidateSet('nov8', 'v8', 'xfa')] [string] $Config,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')] [string] $Arch,
    [string] $Work = (Join-Path $PSScriptRoot 'out/work'),
    # MUST be pinned to a commit before real use; 'master' is only a placeholder.
    [string] $PatchesRef = 'master'
)
$ErrorActionPreference = 'Stop'
$env:PATH = "$(Join-Path $Work 'depot_tools');$env:PATH"
$env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'

$src = Join-Path $Work 'pdfium'
if (-not (Test-Path $src)) { throw "No checkout at $src. Run fetch.ps1 first." }
$patches = Join-Path $Work "patches-$PatchesRef"
if (-not (Test-Path $patches)) {
    New-Item -ItemType Directory -Force $patches, "$patches/win" | Out-Null
    $base = "https://raw.githubusercontent.com/bblanchon/pdfium-binaries/$PatchesRef/patches"
    foreach ($f in 'shared_library.patch', 'public_headers.patch', 'clang_rt.patch', 'win/build.patch', 'win/resources.rc') {
        Invoke-WebRequest "$base/$f" -OutFile (Join-Path $patches $f) -UseBasicParsing
    }
    if ($Config -ne 'nov8') {
        New-Item -ItemType Directory -Force "$patches/v8" | Out-Null
        Invoke-WebRequest "$base/v8/pdfium.patch" -OutFile "$patches/v8/pdfium.patch" -UseBasicParsing
    }
}

Push-Location $src
try {
    # Patches (03-patch.sh). Idempotence: reset first.
    git reset --hard; git -C build reset --hard
    git apply "$patches/shared_library.patch"
    git apply "$patches/public_headers.patch"
    git -C build apply "$patches/clang_rt.patch"
    git -C build apply "$patches/win/build.patch"
    if ($Config -ne 'nov8') { git apply "$patches/v8/pdfium.patch" }
    # resources.rc is an envsubst template upstream (VERSION, VERSION_CSV, YEAR); copy as-is for the spike.
    Copy-Item "$patches/win/resources.rc" resources.rc -Force

    # Configure (05-configure.sh): args sorted into out/<name>/args.gn, then gn gen.
    $out = "out/$Config-$Arch"
    New-Item -ItemType Directory -Force $out | Out-Null
    $args = @(Get-Content (Join-Path $PSScriptRoot 'args/common.gn')) +
            @(Get-Content (Join-Path $PSScriptRoot "args/$Config.gn")) +
            "target_cpu = `"$Arch`""
    $args | Where-Object { $_ -and $_ -notmatch '^\s*#' } | Sort-Object | Set-Content "$out/args.gn"
    gn gen $out
    if ($LASTEXITCODE) { throw 'gn gen failed' }

    # Build (06-build.sh) and time it. Single run: report as n=1, cold/warm unknown.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    ninja -C $out pdfium
    if ($LASTEXITCODE) { throw 'ninja failed' }
    $sw.Stop()

    $dll = Join-Path $out 'pdfium.dll'
    $m = [ordered]@{
        config = $Config; arch = $Arch; patches_ref = $PatchesRef
        pdfium_commit = (git rev-parse HEAD); build_seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        dll_bytes = (Get-Item $dll).Length; dll = (Resolve-Path $dll).Path
    }
    $m | ConvertTo-Json | Set-Content (Join-Path $Work "metrics-$Config-$Arch.json")
    $m | Format-Table | Out-String | Write-Host
} finally { Pop-Location }
