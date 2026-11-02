<#
.SYNOPSIS
  Builds the TEST-ONLY S6b package (unsigned MSIX): packaged shell handlers (thumbnail and preview through COM surrogates), a com:ExeServer, ShellHost, the real engine.
.DESCRIPTION
  CI only (the workflow s6b-shell-transport.yml). Never installed on a developer machine: installing needs a trusted certificate, which only the disposable runner gets.
  Expects: the native engine built (./build.ps1 -SkipManaged), the S6b native build with -Ci, and the ShellHost published self-contained.
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $Publisher = 'CN=MikePDFS6bCI'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$engineBin = Join-Path $root "build/out/cmake-$Arch/bin/Release"
$native = Join-Path $root "spikes/S6b/out/native-$Arch"
$stage = Join-Path $root 'spikes/S6b/out/pkg-stage'
$outDir = Join-Path $root 'spikes/S6b/out/pkg'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage, $outDir | Out-Null

foreach ($f in 'MikePDF.Engine.exe', 'pdfium.dll') { if (-not (Test-Path (Join-Path $engineBin $f))) { throw "$f missing in $engineBin" } }
foreach ($f in 's6b_handler.dll', 'S6bDriver.exe', 'S6bComServer.exe') { if (-not (Test-Path (Join-Path $native $f))) { throw "$f missing in $native" } }

New-Item -ItemType Directory "$stage/Engine", "$stage/ShellHost", "$stage/Fixtures", "$stage/Assets" | Out-Null
foreach ($f in 'MikePDF.Engine.exe', 'pdfium.dll') { Copy-Item (Join-Path $engineBin $f) "$stage/Engine" }
foreach ($f in 's6b_handler.dll', 'S6bDriver.exe', 'S6bComServer.exe') { Copy-Item (Join-Path $native $f) $stage }
Copy-Item (Join-Path $root "spikes/S6b/out/shellhost-publish-$Arch/*") "$stage/ShellHost" -Recurse
$text1 = Join-Path $root 'spikes/S6b/out/pdfs/text1.pdf'
if (-not (Test-Path $text1)) { $text1 = Join-Path $root 'tests/MikePDF.Integration.Tests/Fixtures/hello_world.pdf' } # python not available: any PDF will do for the shell paths
Copy-Item $text1 (Join-Path $stage 'Fixtures/text1.pdf')
$src = Join-Path $root 'build/store/assets'
Copy-Item (Join-Path $src 'Square150x150Logo.scale-100.png') "$stage/Assets/Square150x150Logo.png"
Copy-Item (Join-Path $src 'Square44x44Logo.scale-100.png') "$stage/Assets/Square44x44Logo.png"
Copy-Item (Join-Path $src 'StoreLogo.scale-100.png') "$stage/Assets/StoreLogo.png"

# CLSIDs: ...0001 thumbnail (DLL in the COM surrogate), ...0002 preview (DLL in PreviewHost), ...0003 ExeServer class.
# Measured with makeappx: desktop2:ThumbnailHandler Clsid must resolve to a com:SurrogateServer class (keyref ComServer_SurrogateServer_Class_IdKey),
# so an ExeServer class cannot itself be the thumbnail handler. The ExeServer here is only an activation and marshaling probe.
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:desktop2="http://schemas.microsoft.com/appx/manifest/desktop/windows10/2"
  xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap3 desktop desktop2 com rescap">
  <Identity Name="MikePDF.S6bProbe" Publisher="$Publisher" Version="1.0.0.0" ProcessorArchitecture="$Arch" />
  <Properties>
    <DisplayName>MikePDF S6b shell transport probe (test only)</DisplayName>
    <PublisherDisplayName>MikePDF CI</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.26300.0" />
  </Dependencies>
  <Resources><Resource Language="en-US" /></Resources>
  <Applications>
    <Application Id="Driver" Executable="S6bDriver.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="S6b driver" Description="Test only" BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
      <Extensions>
        <uap3:Extension Category="windows.appExecutionAlias" Executable="S6bDriver.exe" EntryPoint="Windows.FullTrustApplication">
          <uap3:AppExecutionAlias>
            <desktop:ExecutionAlias Alias="s6bdriver.exe" />
          </uap3:AppExecutionAlias>
        </uap3:Extension>
        <uap:Extension Category="windows.fileTypeAssociation">
          <uap:FileTypeAssociation Name="s6bpdf">
            <uap:SupportedFileTypes><uap:FileType>.s6bpdf</uap:FileType></uap:SupportedFileTypes>
            <desktop2:ThumbnailHandler Clsid="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0001" />
            <desktop2:DesktopPreviewHandler Clsid="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0002" />
          </uap:FileTypeAssociation>
        </uap:Extension>
        <uap:Extension Category="windows.fileTypeAssociation">
          <uap:FileTypeAssociation Name="pdf">
            <uap:SupportedFileTypes><uap:FileType>.pdf</uap:FileType></uap:SupportedFileTypes>
            <desktop2:ThumbnailHandler Clsid="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0001" />
            <desktop2:DesktopPreviewHandler Clsid="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0002" />
          </uap:FileTypeAssociation>
        </uap:Extension>
        <com:Extension Category="windows.comServer">
          <com:ComServer>
            <com:ExeServer Executable="S6bComServer.exe" DisplayName="S6b exe server">
              <com:Class Id="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0003" DisplayName="S6b exe thumbnail" />
            </com:ExeServer>
            <com:SurrogateServer DisplayName="S6b thumbnail surrogate" AppId="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0010">
              <com:Class Id="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0001" Path="s6b_handler.dll" ThreadingModel="Both" />
            </com:SurrogateServer>
            <com:SurrogateServer DisplayName="S6b preview surrogate" AppId="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0011" SystemSurrogate="PreviewHost">
              <com:Class Id="6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0002" Path="s6b_handler.dll" ThreadingModel="STA" />
            </com:SurrogateServer>
          </com:ComServer>
        </com:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding UTF8

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found (Windows SDK)' }
$msix = Join-Path $outDir "MikePDF.S6bProbe-$Arch.msix"
& $makeappx.FullName pack /d $stage /p $msix /nv /o | Select-Object -Last 3 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
Write-Output $msix
