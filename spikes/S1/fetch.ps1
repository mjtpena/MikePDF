<#
.SYNOPSIS
  S1.1 (NOT EXECUTED: BLOCKED on this machine): bootstrap depot_tools and a PDFium checkout.
.DESCRIPTION
  Written from, and not yet validated against, these sources:
    - PDFium README + Chromium Windows instructions (toolchain requirements, DEPOT_TOOLS_WIN_TOOLCHAIN=0)
      https://pdfium.googlesource.com/pdfium/+/refs/heads/main/README.md
      https://chromium.googlesource.com/chromium/src/+/main/docs/windows_build_instructions.md
    - bblanchon/pdfium-binaries steps/01-install.sh, 02-checkout.sh, 04-install-extras.sh
      https://github.com/bblanchon/pdfium-binaries/tree/master/steps
  -CheckOnly runs only the prerequisite probe (this part WAS run; its output is in docs/spikes/S1.md).
  Everything lands under out/work (gitignored).
#>
param(
    [string] $Branch = 'chromium/8125',        # PDFium branch (PDFium 158.0.8125.0 prebuilt pin). Release builds: a stable branch, see ADR-0002
    [string] $Work = (Join-Path $PSScriptRoot 'out/work'),
    [switch] $CheckOnly
)
$ErrorActionPreference = 'Stop'

function Test-Prerequisites {
    $missing = @()
    # Visual Studio with the C++ workload, ARM64 tools and ATL/MFC (Chromium doc, VS 2026 18.0+).
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = $null
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -property installationPath -requires `
            Microsoft.VisualStudio.Workload.NativeDesktop `
            Microsoft.VisualStudio.Component.VC.ATLMFC `
            Microsoft.VisualStudio.Component.VC.Tools.ARM64 `
            Microsoft.VisualStudio.Component.VC.MFC.ARM64
    }
    if (-not $vs) { $missing += 'Visual Studio 2026 (18.x) with Desktop development with C++, VC.ATLMFC, VC.Tools.ARM64, VC.MFC.ARM64' }
    # Windows SDK >= 10.0.28000.2270 (ADR-0001 pins 10.0.28000.2957), plus Debugging Tools.
    $sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include" -Directory -ErrorAction SilentlyContinue |
        Where-Object { [version]$_.Name -ge [version]'10.0.28000.2270' }
    if (-not $sdk) { $missing += 'Windows SDK >= 10.0.28000.2270 (with Debugging Tools for Windows)' }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $missing += 'git' }
    $dt = Join-Path $Work 'depot_tools'
    if (-not (Test-Path $dt) -and -not (Get-Command gclient -ErrorAction SilentlyContinue)) {
        $missing += 'depot_tools (this script clones it; needs network + git)'
    }
    [pscustomobject]@{ VisualStudio = $vs; WindowsSdk = ($sdk | Select-Object -Last 1).Name; Missing = $missing }
}

$pre = Test-Prerequisites
$pre | Format-List | Out-String | Write-Host
if ($CheckOnly) { exit ([int]($pre.Missing.Count -gt 0)) }
if ($pre.Missing.Count -gt 0 -and ($pre.Missing -ne 'depot_tools (this script clones it; needs network + git)')) {
    throw "Prerequisites missing: $($pre.Missing -join '; ')"
}

New-Item -ItemType Directory -Force $Work | Out-Null
Push-Location $Work
try {
    if (-not (Test-Path depot_tools)) {
        git clone https://chromium.googlesource.com/chromium/tools/depot_tools.git depot_tools
    }
    $env:PATH = "$(Join-Path $Work 'depot_tools');$env:PATH"      # must precede Python/git (Chromium doc)
    $env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'                          # use the local VS, not Google's internal toolchain
    $env:vs2026_install = $pre.VisualStudio                       # variable name per Chromium doc; verify for the pinned branch
    git config --global core.longpaths true

    # Minimal checkout config (no V8/test corpus) like pdfium-binaries does for non-V8 builds.
    # V8/XFA builds need the full checkout: re-run with the custom-var removed (see build.ps1 -Config v8/xfa).
    gclient config --unmanaged https://pdfium.googlesource.com/pdfium.git --custom-var checkout_configuration=minimal
    Add-Content .gclient "target_os = [ 'win' ]"
    gclient sync -r "origin/$Branch" --no-history --shallow
    if ($LASTEXITCODE) { throw 'gclient sync failed' }
    Write-Host "Checked out PDFium $Branch -> $(Join-Path $Work 'pdfium'). Record 'git -C pdfium rev-parse HEAD' in build/pdfium.lock."
} finally { Pop-Location }
