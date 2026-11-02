<#
.SYNOPSIS
  Collects the licence texts that ship inside the package (Licenses\): MikePDF's own, the third-party summary, and one file per PDFium bundled library.
.DESCRIPTION
  - LICENSE, NOTICE and THIRD-PARTY-NOTICES.md from the repository root
  - every file of the pinned PDFium build's licenses/ folder (build/out/pdfium/<arch>/licenses, fetched and hash-checked by build/fetch-pdfium.ps1)
  - the Windows App SDK licence terms and the .NET runtime licence and third-party notices, when found
  Fails if a required file is missing, so a package never ships without its notices (ADR-0003, master prompt 16).
.EXAMPLE
  ./build/generate-notices.ps1 -OutDir build/out/package/stage-x64/Licenses -Arch x64
#>
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
New-Item -ItemType Directory -Force $OutDir | Out-Null

foreach ($name in 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md') {
    $source = Join-Path $root $name
    if (-not (Test-Path $source)) { throw "$name is missing from the repository root" }
    $target = if ($name -eq 'THIRD-PARTY-NOTICES.md') { $name } else { "$name.txt" }
    Copy-Item $source (Join-Path $OutDir $target) -Force
}

# PDFium and the libraries bundled inside it: the pinned build ships one licence file per library.
$pdfium = & (Join-Path $PSScriptRoot 'fetch-pdfium.ps1') -Arch $Arch | Select-Object -Last 1
$licenses = Join-Path $pdfium 'licenses'
if (-not (Test-Path $licenses)) { throw "PDFium licenses folder not found: $licenses" }
$pdfiumOut = Join-Path $OutDir 'PDFium'
New-Item -ItemType Directory -Force $pdfiumOut | Out-Null
$files = Get-ChildItem $licenses -File
if ($files.Count -eq 0) { throw "No licence files in $licenses" }
foreach ($file in $files) { Copy-Item $file.FullName (Join-Path $pdfiumOut $file.Name) -Force }
if (Test-Path (Join-Path $pdfium 'LICENSE')) { Copy-Item (Join-Path $pdfium 'LICENSE') (Join-Path $pdfiumOut 'pdfium-binaries-LICENSE.txt') -Force }

# Windows App SDK (Microsoft Software License Terms): from the NuGet package the build used.
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$props = Get-Content (Join-Path $root 'Directory.Packages.props') -Raw
$wasdkVersion = [regex]::Match($props, 'Include="Microsoft.WindowsAppSDK" Version="([^"]+)"').Groups[1].Value
$wasdk = Join-Path $packages "microsoft.windowsappsdk/$wasdkVersion/license.txt"
if (Test-Path $wasdk) { Copy-Item $wasdk (Join-Path $OutDir 'WindowsAppSDK-license.txt') -Force } else { Write-Warning "Windows App SDK licence not found at $wasdk" }

# The .NET runtime is bundled (self-contained): its licence and third-party notices live beside the dotnet host.
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path (Resolve-Path $dotnet).Path
foreach ($pair in @(@('LICENSE.txt', 'DotNet-LICENSE.txt'), @('ThirdPartyNotices.txt', 'DotNet-ThirdPartyNotices.txt'))) {
    $source = Join-Path $dotnetRoot $pair[0]
    if (Test-Path $source) { Copy-Item $source (Join-Path $OutDir $pair[1]) -Force } else { Write-Warning "$($pair[0]) not found beside the dotnet host" }
}

Write-Host "Licences written to $OutDir ($((Get-ChildItem $OutDir -Recurse -File).Count) files)"
