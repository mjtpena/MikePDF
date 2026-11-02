<#
.SYNOPSIS
  Captures the Store listing screenshots (build/out/shots/store-*.png) by running one FlaUI journey. Needs an unlocked, interactive desktop.
.DESCRIPTION
  Runs ReaderJourneyTests.ScreenshotsForTheStoreListing (tests/MikePDF.UI.Tests) through build/run-ui-tests.ps1: it registers the build output (or a
  package layout) with Add-AppxPackage -Register, opens a generated sample document with real text, outline and links (tests/MikePDF.UI.Tests/ArticlePdf.cs),
  drives the app like a user and captures the window: reading, thumbnails, bookmarks, a text selection, find highlights, a contents page with links.
  The journey also leaves its other pictures (named without the store- prefix) there; only the store-* files are meant for a listing, and
  this script copies those, trimmed, to docs/store/screenshots.
  Every picture is the window's visible frame at the size the journey sets (at least 1366x768; the files are not named after a size, so
  nothing here changes when the product is renamed or the window size is changed). Look at each one before using it: the journey asserts that
  the content is there, not that it looks right.
  Never adds a certificate to any trust store and never asks for administrator rights.
.EXAMPLE
  ./build/capture-screenshots.ps1                                   # the development layout (build output of src/MikePDF.App)
  ./build/capture-screenshots.ps1 -PackageLayout build/out/package/stage-x64   # the shipped, published app
  ./build/capture-screenshots.ps1 -SkipBuild                        # reuse the last build
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
    [switch] $SkipBuild,
    [string] $PackageLayout
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$shots = Join-Path $root 'build/out/shots'
New-Item -ItemType Directory -Force $shots | Out-Null
# Old pictures must not pass for new ones.
Get-ChildItem $shots -Filter 'store-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force

$arguments = @{ Arch = $Arch; 'Filter-method' = '*ScreenshotsForTheStoreListing' }
if ($SkipBuild) { $arguments.SkipBuild = $true }
if ($PackageLayout) { $arguments.PackageLayout = $PackageLayout }
& (Join-Path $PSScriptRoot 'run-ui-tests.ps1') @arguments
$code = $LASTEXITCODE

$pictures = @(Get-ChildItem $shots -Filter 'store-*.png' -ErrorAction SilentlyContinue | Sort-Object Name)
Add-Type -AssemblyName System.Drawing
if ($code -eq 0 -and $pictures.Count -lt 4) { throw "Expected at least four store pictures, found $($pictures.Count)." }

# The listing's copies, in docs/store/screenshots: 8 pixels off each edge, so the rounded window corners don't show what is behind the window.
$listing = Join-Path $root 'docs/store/screenshots'
New-Item -ItemType Directory -Force $listing | Out-Null
foreach ($p in $pictures) {
    $image = [System.Drawing.Bitmap]::new($p.FullName)
    try {
        $trimmed = $image.Clone([System.Drawing.Rectangle]::new(8, 8, $image.Width - 16, $image.Height - 16), $image.PixelFormat)
        try {
            if ($trimmed.Width -lt 1366 -or $trimmed.Height -lt 768) { throw "$($p.Name) is $($trimmed.Width)x$($trimmed.Height), under the Store's 1366x768." }
            $target = Join-Path $listing ($p.Name -replace '^store-', '')
            $trimmed.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host ("{0}  {1}x{2}" -f $target, $trimmed.Width, $trimmed.Height)
        } finally { $trimmed.Dispose() }
    } finally { $image.Dispose() }
}
exit $code
