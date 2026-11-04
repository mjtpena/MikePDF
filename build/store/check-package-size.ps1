<#
.SYNOPSIS
  Install-size gate: the unpacked size of a single-architecture MSIX against the master prompt 11 budget (60 MB), with a breakdown by component.
.DESCRIPTION
  Unpacked size = the sum of the lengths of the entries in the .msix (a zip), in MiB (1 MB = 1,048,576 bytes). It is the same definition as
  tools/MikePDF.Perf/Scenarios/PackageScenario.cs (the package.install_size metric) and docs/perf/budgets.json (install.size); the limit is read from budgets.json
  so the three cannot drift apart. Nothing is installed, registered or launched: the file is only opened as a zip.

  Writes a Markdown breakdown (the largest files, then totals per component) to the job summary when GITHUB_STEP_SUMMARY is set, or to -SummaryFile, and exits 1
  with a message that names the overshoot and the largest files when the package is above the limit.
.EXAMPLE
  ./build/store/check-package-size.ps1 -Msix build/out/package/MikePDF_1.0.0.0_x64.msix
  ./build/store/check-package-size.ps1 -Msix build/out/package/MikePDF_1.0.0.0_x64.msix -LimitMB 70 -NoFail
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Msix,
    # Defaults to the install.size target in docs/perf/budgets.json.
    [double] $LimitMB,
    [string] $Budgets,
    [string] $SummaryFile,
    [int] $Top = 15,
    # Report only (exit 0 above the limit).
    [switch] $NoFail
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Budgets) { $Budgets = Join-Path $root 'docs/perf/budgets.json' }
if (-not $LimitMB) {
    $metric = (Get-Content $Budgets -Raw | ConvertFrom-Json).metrics | Where-Object { $_.budgetId -eq 'install.size' }
    if (-not $metric) { throw "install.size is not in $Budgets" }
    $LimitMB = [double]$metric.targets.R1
}
$mib = 1048576.0

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $Msix).Path)
try {
    $entries = $zip.Entries | ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Bytes = [long]$_.Length } }
} finally { $zip.Dispose() }

# Ordered rules: the first pattern that matches the path (forward slashes, relative to the package root) names the component.
$componentRules = @(
    @('^Engine/', 'Engine and PDFium (Engine/)'),
    @('^ShellHost/', 'ShellHost (Native AOT)'),
    @('^ShellExt/', 'Explorer handlers (ShellExt/)'),
    @('^WriterHost/', 'Writer host (WriterHost/)'),
    @('^Licenses/', 'Licences'),
    @('^(Assets/|[^/]*\.pri$)', 'Assets and resources'),
    @('^(AppxManifest\.xml|AppxBlockMap\.xml|\[Content_Types\]\.xml|AppxMetadata/)', 'Package metadata'),
    @('^(MikePDF\.[^/]*|MessagePack[^/]*\.dll)$', 'MikePDF managed code (and MessagePack)'),
    @('^(WinRT\.Runtime|Microsoft\.Windows\.SDK\.NET)\.dll$', 'C#/WinRT runtime and Windows SDK projection'),
    @('^Microsoft\.Graphics\.', 'Win2D'),
    @('^(coreclr|clrjit|hostfxr|hostpolicy|mscorrc|clretwrc|clrgc|clrgcexp|mscordaccore[^/]*|mscordbi|createdump|System\.Private\.CoreLib|Microsoft\.DiaSymReader\.Native[^/]*)\.(dll|exe)$', '.NET runtime (CLR, JIT, host, CoreLib)'),
    @('^(System\.|Microsoft\.(CSharp|VisualBasic|Win32)|mscorlib|netstandard|WindowsBase|api-ms|ucrtbase|msquic|clrcompression|System\.IO\.Compression\.Native)', '.NET libraries'),
    @('^(Microsoft\.|WebView2Loader)', 'Windows App SDK and WinUI (projections, bootstrap, WebView2)')
)
function Get-Component([string] $Path) {
    foreach ($rule in $componentRules) { if ($Path -match $rule[0]) { return $rule[1] } }
    return 'Other'
}

$files = $entries | Where-Object { $_.Path -notlike '*/' }
$total = ($files | Measure-Object Bytes -Sum).Sum
$grouped = $files | Group-Object { Get-Component $_.Path } | ForEach-Object {
    [pscustomobject]@{ Component = $_.Name; Files = $_.Count; MiB = [math]::Round(($_.Group | Measure-Object Bytes -Sum).Sum / $mib, 1) }
} | Sort-Object MiB -Descending
$largest = $files | Sort-Object Bytes -Descending | Select-Object -First $Top
$totalMiB = $total / $mib
$over = $totalMiB - $LimitMB
$ok = $totalMiB -le $LimitMB
$fileMiB = (Get-Item $Msix).Length / $mib

$lines = @()
$lines += "### Install size: $(Split-Path $Msix -Leaf)"
$lines += ''
$status = if ($ok) { 'within budget' } else { 'OVER BUDGET' }
$lines += "**$([math]::Round($totalMiB, 1)) MB unpacked** against a limit of $LimitMB MB: **$status** ($([math]::Round($fileMiB, 1)) MB as a file, $(@($files).Count) files; 1 MB = 1,048,576 bytes; master prompt 11, docs/perf/budgets.json install.size)."
$lines += ''
$lines += '| Component | Files | MB |'
$lines += '|---|---:|---:|'
foreach ($g in $grouped) { $lines += "| $($g.Component) | $($g.Files) | $($g.MiB) |" }
$lines += ''
$lines += "| Largest $Top files | MB |"
$lines += '|---|---:|'
foreach ($f in $largest) { $lines += "| ``$($f.Path)`` | $([math]::Round($f.Bytes / $mib, 2)) |" }
$text = $lines -join "`n"

Write-Host $text
if ($SummaryFile) { Set-Content $SummaryFile $text -Encoding utf8NoBOM }
if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY ($text + "`n") }

if (-not $ok) {
    $message = "Install size is $([math]::Round($totalMiB, 1)) MB, $([math]::Round($over, 1)) MB over the $LimitMB MB budget (master prompt 11, docs/perf/budgets.json install.size). Largest: " +
        (($largest | Select-Object -First 5 | ForEach-Object { "$($_.Path) $([math]::Round($_.Bytes / $mib, 1)) MB" }) -join '; ')
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error title=Install size over budget::$message" } else { Write-Error $message -ErrorAction Continue }
    if (-not $NoFail) { exit 1 }
}
exit 0
