<#
.SYNOPSIS
  Builds the Quire MSIX for one architecture, or bundles two into the .msixbundle and .msixupload for the Microsoft Store.
.DESCRIPTION
  Single architecture (default):
    1. builds the sandboxed engine with CMake (./build.ps1 -SkipManaged), unless -SkipNativeBuild
    2. publishes Quire.App: self-contained .NET, ReadyToRun, framework-dependent Windows App SDK (not Native AOT yet: see Quire.App.csproj)
    3. lays out the package: app, Engine\ (Quire.Engine.exe + pdfium.dll; the engine runs in place from the package), Licenses\, Assets
    4. writes AppxManifest.xml from the build's manifest with the identity from build/store/identity.json (parameters override), the version,
       the architecture and MaxVersionTested 10.0.26300.0
    5. packs with makeappx into Quire_<version>_<arch>.msix, and the PDBs into Quire_<version>_<arch>.appxsym
  -Bundle: takes Quire_<version>_x64.msix and _arm64.msix from -OutDir and writes Quire_<version>.msixbundle and Quire_<version>.msixupload
  (the bundle plus the .appxsym files), after checking that both packages share identity and version.

  Nothing here needs administrator rights, adds a certificate, or touches the system. The package is unsigned: the Store signs it. To run a
  package locally, -RegisterLocal registers the staged layout in Developer Mode (Add-AppxPackage -Register); -Unregister removes it.

  -TestBuild adds QUIRE_SELFTEST (Quire.exe --selftest) and the sandbox probe under Probe\, for installed-package tests. A release package
  contains neither, and this script fails if it finds either in one.
.EXAMPLE
  ./build/package.ps1 -Arch x64 -Version 1.0.0.0
  ./build/package.ps1 -Bundle -Version 1.0.0.0
  ./build/package.ps1 -Arch x64 -TestBuild -RegisterLocal
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
    [string] $Version = '1.0.0.0',
    [switch] $TestBuild,
    [switch] $Bundle,
    [string] $IdentityFile,
    # Overrides for identity.json, so the real Partner Center values can be supplied without editing the file.
    [string] $IdentityName,
    [string] $Publisher,
    [string] $PublisherDisplayName,
    [string] $DisplayName,
    [string] $OutDir,
    [switch] $SkipNativeBuild,
    [switch] $SkipPublish,
    [switch] $RegisterLocal,
    [switch] $Unregister
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'build/out/package' }
if (-not $IdentityFile) { $IdentityFile = Join-Path $root 'build/store/identity.json' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Test-Version([string] $Value) {
    # Store rules: the fourth part is 0 and the first part is not 0. A bundle's packages share the version.
    if ($Value -notmatch '^(\d+)\.(\d+)\.(\d+)\.0$') { throw "Version '$Value' is not MAJOR.MINOR.BUILD.0 (the Store reserves the fourth part)." }
    if ([int]$Matches[1] -eq 0) { throw "Version '$Value': the first part must not be 0 (the Store rejects it)." }
}

function Find-WindowsSdkTool([string] $Name) {
    $props = Get-Content (Join-Path $root 'Directory.Packages.props') -Raw
    $version = [regex]::Match($props, 'Include="Microsoft.Windows.SDK.BuildTools" Version="([^"]+)"').Groups[1].Value
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $candidates = @()
    if ($version) { $candidates += Get-ChildItem (Join-Path $packages "microsoft.windows.sdk.buildtools/$version/bin/*/$hostArch/$Name") -ErrorAction SilentlyContinue }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    if (Test-Path $kits) { $candidates += Get-ChildItem (Join-Path $kits "*/$hostArch/$Name") -ErrorAction SilentlyContinue | Sort-Object FullName -Descending }
    $found = $candidates | Select-Object -First 1
    if (-not $found) { throw "$Name not found (restore Quire.App for Microsoft.Windows.SDK.BuildTools, or install the Windows SDK)" }
    return $found.FullName
}

Test-Version $Version
if ($TestBuild -and $Bundle) { throw 'A bundle is for the Store: build it from release packages, not -TestBuild.' }

# ---------------------------------------------------------------------------------------------------------------------------------------
# Identity
# ---------------------------------------------------------------------------------------------------------------------------------------
$identity = Get-Content $IdentityFile -Raw | ConvertFrom-Json
if ($IdentityName) { $identity.Name = $IdentityName }
if ($Publisher) { $identity.Publisher = $Publisher }
if ($PublisherDisplayName) { $identity.PublisherDisplayName = $PublisherDisplayName }
if ($DisplayName) { $identity.DisplayName = $DisplayName }
foreach ($key in 'Name', 'Publisher', 'PublisherDisplayName', 'DisplayName') { if (-not $identity.$key) { throw "identity: $key is empty" } }
if ($identity.Name -notmatch '^[A-Za-z0-9][A-Za-z0-9.\-]{2,49}$') { throw "identity: Name '$($identity.Name)' is not a valid package name" }
if ($identity.Publisher -notmatch '^CN=') { throw "identity: Publisher must be a distinguished name starting with CN=" }
$placeholder = $identity.Name -eq 'Quire.Placeholder' -or $identity.Publisher -like 'CN=00000000-*'
if ($placeholder) { Write-Warning 'identity.json still holds PLACEHOLDER values: this package cannot be uploaded to the Store (see docs/store/README.md).' }
function Xml([string] $Value) { [System.Security.SecurityElement]::Escape($Value) }

# ---------------------------------------------------------------------------------------------------------------------------------------
# Bundle mode
# ---------------------------------------------------------------------------------------------------------------------------------------
if ($Bundle) {
    Step 'Bundle'
    $makeappx = Find-WindowsSdkTool 'makeappx.exe'
    $packages = @('x64', 'arm64') | ForEach-Object { Join-Path $OutDir "Quire_${Version}_$_.msix" }
    foreach ($p in $packages) { if (-not (Test-Path $p)) { throw "Missing $p. Build both architectures first: ./build/package.ps1 -Arch x64 ; ./build/package.ps1 -Arch arm64" } }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $seen = @()
    foreach ($p in $packages) {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($p)
        try {
            $entry = $zip.GetEntry('AppxManifest.xml')
            $reader = New-Object System.IO.StreamReader($entry.Open())
            $manifest = [xml]$reader.ReadToEnd()
            $reader.Dispose()
        } finally { $zip.Dispose() }
        $id = $manifest.Package.Identity
        $seen += [pscustomobject]@{ File = Split-Path $p -Leaf; Name = $id.Name; Publisher = $id.Publisher; Version = $id.Version; Architecture = $id.ProcessorArchitecture }
    }
    $seen | Format-Table | Out-Host
    if (@($seen.Name | Select-Object -Unique).Count -ne 1 -or @($seen.Publisher | Select-Object -Unique).Count -ne 1 -or @($seen.Version | Select-Object -Unique).Count -ne 1) {
        throw 'The packages do not share identity and version; a bundle needs all three to match.'
    }
    $staging = Join-Path $OutDir 'bundle-staging'
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory $staging | Out-Null
    foreach ($p in $packages) { Copy-Item $p $staging }
    $bundlePath = Join-Path $OutDir "Quire_$Version.msixbundle"
    if (Test-Path $bundlePath) { Remove-Item $bundlePath }
    & $makeappx bundle /d $staging /p $bundlePath /bv $Version /o | Select-Object -Last 3 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed' }

    $upload = Join-Path $OutDir "Quire_$Version.msixupload"
    $zipPath = "$upload.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    if (Test-Path $upload) { Remove-Item $upload }
    $parts = @($bundlePath) + (@('x64', 'arm64') | ForEach-Object { Join-Path $OutDir "Quire_${Version}_$_.appxsym" } | Where-Object { Test-Path $_ })
    Compress-Archive -Path $parts -DestinationPath $zipPath
    Move-Item $zipPath $upload
    Remove-Item -Recurse -Force $staging
    Write-Host ''
    Write-Host "Bundle:  $bundlePath"
    Write-Host "Upload:  $upload"
    return
}

# ---------------------------------------------------------------------------------------------------------------------------------------
# Single architecture
# ---------------------------------------------------------------------------------------------------------------------------------------
$suffix = if ($TestBuild) { '_TEST' } else { '' }
$stage = Join-Path $OutDir "stage-$Arch$suffix"
$msixPath = Join-Path $OutDir "Quire_${Version}_${Arch}${suffix}.msix"
$symbolsPath = Join-Path $OutDir "Quire_${Version}_${Arch}${suffix}.appxsym"

if ($Unregister) {
    Get-AppxPackage -Name $identity.Name | Remove-AppxPackage
    Write-Host "Unregistered $($identity.Name)"
    return
}

$cmakeBin = Join-Path $root "build/out/cmake-$Arch/bin/Release"
if (-not $SkipNativeBuild) {
    Step "Native engine ($Arch)"
    $nativeArgs = @{ Arch = $Arch; Configuration = 'Release'; SkipManaged = $true }
    if ($TestBuild) { $nativeArgs.Test = $true }   # the sandbox probe is a test target
    & (Join-Path $root 'build.ps1') @nativeArgs
    if ($LASTEXITCODE -ne 0) { throw 'native build failed' }
}
foreach ($f in 'Quire.Engine.exe', 'pdfium.dll') { if (-not (Test-Path (Join-Path $cmakeBin $f))) { throw "$f not found in $cmakeBin" } }

$publish = Join-Path $OutDir "publish-$Arch$suffix"
if (-not $SkipPublish) {
    Step "Publish Quire.App ($Arch, self-contained, ReadyToRun)"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    $publishArgs = @(
        'publish', (Join-Path $root 'src/Quire.App/Quire.App.csproj'), '-c', 'Release', '-r', "win-$Arch", '--self-contained', 'true',
        '-p:PublishReadyToRun=true', "-p:QuireArch=$Arch", "-p:Version=$Version", '-o', $publish, '-nologo', '-v', 'q',
        # resources.pri must carry the package's name, not the development manifest's (the certification kit's "App resources" test).
        "-p:ProjectPriIndexName=$($identity.Name)")
    if ($TestBuild) { $publishArgs += '-p:QuireSelfTest=true' }
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
if (-not (Test-Path (Join-Path $publish 'Quire.exe'))) { throw "No Quire.exe in $publish" }

Step 'Lay out the package'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory $stage | Out-Null
# Everything from the publish folder except debug symbols (they go to the .appxsym), documentation XML and the development engine copy.
Get-ChildItem $publish -Force | Where-Object { $_.Name -notin 'Engine', 'AppxManifest.xml' } | ForEach-Object { Copy-Item $_.FullName $stage -Recurse -Force }
Get-ChildItem $stage -Recurse -File | Where-Object { ($_.Extension -in '.pdb', '.xml' -and $_.Name -notlike 'AppxManifest*') -or $_.Extension -eq '.appxrecipe' } | Remove-Item -Force

$engineDir = Join-Path $stage 'Engine'
New-Item -ItemType Directory $engineDir | Out-Null
foreach ($f in 'Quire.Engine.exe', 'pdfium.dll') { Copy-Item (Join-Path $cmakeBin $f) $engineDir }

if ($TestBuild) {
    $probeDir = Join-Path $stage 'Probe'
    New-Item -ItemType Directory $probeDir | Out-Null
    foreach ($f in 'Quire.Engine.SandboxProbe.exe', 'rogue-copy.dll', 'rogue-marker.dll') {
        $source = Join-Path $cmakeBin $f
        if (-not (Test-Path $source)) { throw "$f not found: -TestBuild needs the native tests built" }
        Copy-Item $source $probeDir
    }
}

Step 'Licences'
& (Join-Path $PSScriptRoot 'generate-notices.ps1') -OutDir (Join-Path $stage 'Licenses') -Arch $Arch

Step 'Manifest'
$built = Get-ChildItem (Join-Path $root "src/Quire.App/bin/Release/*/win-$Arch/AppxManifest.xml") | Select-Object -First 1
if (-not $built) { throw 'The build did not produce AppxManifest.xml (src/Quire.App/bin/Release/.../AppxManifest.xml)' }
$text = Get-Content $built.FullName -Raw
$text = [regex]::Replace($text, '<Identity [^>]*/>', "<Identity Name=`"$(Xml $identity.Name)`" Publisher=`"$(Xml $identity.Publisher)`" Version=`"$Version`" ProcessorArchitecture=`"$Arch`" />", 1)
$text = [regex]::Replace($text, '<DisplayName>[^<]*</DisplayName>', "<DisplayName>$(Xml $identity.DisplayName)</DisplayName>", 1)
$text = [regex]::Replace($text, '<PublisherDisplayName>[^<]*</PublisherDisplayName>', "<PublisherDisplayName>$(Xml $identity.PublisherDisplayName)</PublisherDisplayName>", 1)
$text = [regex]::Replace($text, 'DisplayName="[^"]*"( Description=)', "DisplayName=`"$(Xml $identity.DisplayName)`"`$1", 1)
# The MSIX tooling forces MaxVersionTested to the SDK of the target framework (S6); ADR-0001 declares 10.0.26300.0.
$text = $text -replace 'MaxVersionTested="[^"]*"', 'MaxVersionTested="10.0.26300.0"'
$text = $text -replace 'MinVersion="10\.0\.26100\.0"', 'MinVersion="10.0.26100.0"'
if ($text -notmatch 'Name="runFullTrust"') { throw 'The manifest lost runFullTrust.' }
Set-Content (Join-Path $stage 'AppxManifest.xml') $text -Encoding utf8NoBOM

# resources.pri (strings and scale-qualified assets) must be at the package root.
if (-not (Test-Path (Join-Path $stage 'resources.pri'))) {
    $pri = Get-ChildItem (Join-Path $root "src/Quire.App/bin/Release/*/win-$Arch/resources.pri") | Select-Object -First 1
    if (-not $pri) { throw 'resources.pri is missing' }
    Copy-Item $pri.FullName $stage
}

Step 'Release checks'
$offenders = @()
# Never in any package: the WriterHost (no save feature in this build) and the CI package probe.
foreach ($name in 'Quire.WriterHost.exe', 'Quire.PackageProbe.exe') { $offenders += Get-ChildItem $stage -Recurse -Filter $name -ErrorAction SilentlyContinue }
# Test builds only: the sandbox probe and the unsigned DLL it uses.
if (-not $TestBuild) {
    foreach ($name in 'Quire.Engine.SandboxProbe.exe', 'rogue-copy.dll', 'rogue-marker.dll') { $offenders += Get-ChildItem $stage -Recurse -Filter $name -ErrorAction SilentlyContinue }
    if (Test-Path (Join-Path $stage 'Probe')) { $offenders += Get-Item (Join-Path $stage 'Probe') }
}
# The self-test is compiled in only under QUIRE_SELFTEST: the type name must not appear in a release build.
$appDll = Join-Path $stage 'Quire.dll'
$bytes = [System.IO.File]::ReadAllBytes($appDll)
$hasSelfTest = [System.Text.Encoding]::ASCII.GetString($bytes).Contains('SelfTest')
if ($hasSelfTest -and -not $TestBuild) { $offenders += Get-Item $appDll }
if (@($offenders).Count -gt 0) { throw "A release package must not contain the sandbox probe, the self-test or the WriterHost. Found: $($offenders.FullName -join ', ')" }
if (-not $hasSelfTest -and $TestBuild) { throw '-TestBuild was asked for but the self-test is not in Quire.dll.' }
foreach ($required in 'Quire.exe', 'AppxManifest.xml', 'resources.pri', 'Engine/Quire.Engine.exe', 'Engine/pdfium.dll', 'Licenses/LICENSE.txt', 'Licenses/NOTICE.txt', 'Licenses/THIRD-PARTY-NOTICES.md', 'Licenses/PDFium/pdfium.txt') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "Package content is incomplete: $required" }
}
# Quire.App must not carry PDFium itself or its own copy of the engine: only Engine\ has them.
$strays = Get-ChildItem $stage -Recurse -Filter pdfium.dll | Where-Object { $_.DirectoryName -ne $engineDir }
if ($strays) { throw "pdfium.dll outside Engine\: $($strays.FullName -join ', ')" }

Step 'Pack'
$makeappx = Find-WindowsSdkTool 'makeappx.exe'
if (Test-Path $msixPath) { Remove-Item $msixPath }
& $makeappx pack /d $stage /p $msixPath /o | Select-Object -Last 4 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }

# Symbols: the app's PDBs and the engine's, for crash analysis of Store builds. Not shipped to users.
$symbolFiles = @(Get-ChildItem $publish -Recurse -Filter *.pdb) + @(Get-ChildItem $cmakeBin -Filter Quire.Engine.pdb)
if (Test-Path $symbolsPath) { Remove-Item $symbolsPath }
$symbolZip = "$symbolsPath.zip"
if ($symbolFiles.Count -gt 0) {
    Compress-Archive -Path $symbolFiles.FullName -DestinationPath $symbolZip -Force
    Move-Item $symbolZip $symbolsPath
}

if ($RegisterLocal) {
    Step 'Register the staged layout (Developer Mode; no certificate, no elevation)'
    $existing = Get-AppxPackage -Name $identity.Name
    if ($existing) { Remove-AppxPackage $existing.PackageFullName }
    Add-AppxPackage -Register (Join-Path $stage 'AppxManifest.xml')
    Write-Host "Registered. Remove with: ./build/package.ps1 -Arch $Arch -Unregister (or Remove-AppxPackage)"
}

$size = [math]::Round((Get-Item $msixPath).Length / 1MB, 1)
Write-Host ''
Write-Host "Package: $msixPath ($size MB, unsigned: the Store signs it)"
if (Test-Path $symbolsPath) { Write-Host "Symbols: $symbolsPath" }
Write-Host "Layout:  $stage"
