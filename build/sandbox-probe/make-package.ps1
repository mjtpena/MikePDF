<#
.SYNOPSIS
  Builds the TEST-ONLY sandbox-probe MSIX (unsigned): the engine, the sandbox probe and a self-test executable with an execution alias.
.DESCRIPTION
  Used by .github/workflows/package-sandbox-probe.yml and for local harness debugging. Needs the native build with tests on
  (./build.ps1 -SkipManaged -Test), because the sandbox probe is a test-only target. Never part of a release package.
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $Publisher = 'CN=QuireProbeCI',
    [switch] $UnsignedNamespace
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$bin = Join-Path $root "build/out/cmake-$Arch/bin/Release"
$stage = Join-Path $root 'build/out/probe-pkg'
$outDir = Join-Path $root 'build/out/probe'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage, $outDir | Out-Null

foreach ($f in 'Quire.Engine.exe', 'pdfium.dll', 'Quire.Engine.SandboxProbe.exe', 'rogue-copy.dll') {
    if (-not (Test-Path (Join-Path $bin $f))) { throw "$f missing in $bin. Run: ./build.ps1 -SkipManaged -Test -Arch $Arch" }
}

# Engine directory (the layout the product package will use) + the test-only probe beside it.
$engine = Join-Path $stage 'Engine'
New-Item -ItemType Directory $engine | Out-Null
foreach ($f in 'Quire.Engine.exe', 'pdfium.dll', 'Quire.Engine.SandboxProbe.exe', 'rogue-copy.dll') { Copy-Item (Join-Path $bin $f) $engine }

# Self-test executable (self-contained, so the runner needs no .NET).
dotnet publish (Join-Path $root 'tests/Quire.PackageProbe/Quire.PackageProbe.csproj') -c Release -r "win-$Arch" --self-contained -p:QuireArch=$Arch -o (Join-Path $stage 'PackageProbe') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'publish of Quire.PackageProbe failed' }

$fixtures = Join-Path $stage 'Fixtures'
New-Item -ItemType Directory $fixtures | Out-Null
Copy-Item (Join-Path $root 'tests/Quire.Integration.Tests/Fixtures/*.pdf') $fixtures

$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory $assets | Out-Null
$src = Join-Path $root 'build/store/assets'
Copy-Item (Join-Path $src 'Square150x150Logo.scale-100.png') (Join-Path $assets 'Square150x150Logo.png')
Copy-Item (Join-Path $src 'Square44x44Logo.scale-100.png') (Join-Path $assets 'Square44x44Logo.png')
Copy-Item (Join-Path $src 'StoreLogo.scale-100.png') (Join-Path $assets 'StoreLogo.png')

if ($UnsignedNamespace) { $Publisher = 'CN=QuireProbeCI, OID.2.25.311729368913984317654407730594956997722=1' }
$manifestArch = if ($Arch -eq 'arm64') { 'arm64' } else { 'x64' }
@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap3 desktop rescap">
  <Identity Name="Quire.SandboxProbe" Publisher="$Publisher" Version="1.0.0.0" ProcessorArchitecture="$manifestArch" />
  <Properties>
    <DisplayName>Quire sandbox probe (test only)</DisplayName>
    <PublisherDisplayName>Quire CI</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.26300.0" />
  </Dependencies>
  <Resources><Resource Language="en-US" /></Resources>
  <Applications>
    <Application Id="Probe" Executable="PackageProbe\Quire.PackageProbe.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Quire sandbox probe" Description="Test only" BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
      <Extensions>
        <uap3:Extension Category="windows.appExecutionAlias" Executable="PackageProbe\Quire.PackageProbe.exe" EntryPoint="Windows.FullTrustApplication">
          <uap3:AppExecutionAlias>
            <desktop:ExecutionAlias Alias="quirepkgprobe.exe" />
          </uap3:AppExecutionAlias>
        </uap3:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@ | Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Encoding UTF8

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found (Windows SDK)' }
$msix = Join-Path $outDir 'Quire.SandboxProbe.msix'
& $makeappx.FullName pack /d $stage /p $msix /nv /o | Select-Object -Last 3 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
Write-Output $msix
