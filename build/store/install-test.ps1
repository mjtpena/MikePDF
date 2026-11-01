<#
.SYNOPSIS
  CI ONLY. Signs a package with an ephemeral self-signed certificate, trusts it on the throwaway runner, installs it with its Windows App SDK
  framework, and either runs the self-test of a -TestBuild package or the Windows App Certification Kit.
.DESCRIPTION
  Refuses to run anywhere but a GitHub-hosted runner: it adds a certificate to the machine trust store, which must never happen on a developer
  machine. The certificate's subject is the package's Publisher, its private key stays on the runner, and the signed copy is never uploaded: the
  Store signs the real package. Windows PowerShell 5.1 compatible (the Appx cmdlets live there).
#>
param(
    [Parameter(Mandatory)] [string] $Msix,
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $Arch,
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [ValidateSet('SelfTest', 'Wack')] [string] $Mode,
    [string] $Pdf
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This script trusts a self-signed certificate. It runs only on GitHub-hosted runners.'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

# The publisher and the framework version come from the package itself.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($Msix)
try {
    $reader = New-Object System.IO.StreamReader($zip.GetEntry('AppxManifest.xml').Open())
    $manifest = [xml]$reader.ReadToEnd()
    $reader.Dispose()
} finally { $zip.Dispose() }
$publisher = $manifest.Package.Identity.Publisher
$packageName = $manifest.Package.Identity.Name

# Sign a copy, so the unsigned package the Store needs is never replaced by a test-signed one.
$signed = Join-Path $env:RUNNER_TEMP ('signed-' + (Split-Path $Msix -Leaf))
Copy-Item $Msix $signed -Force
$cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'Quire CI (ephemeral)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$cer = Join-Path $env:RUNNER_TEMP 'quire-ci.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\$hostArch\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe for $hostArch not found" }
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $signed
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

# The framework package the Store would install as a dependency, from the pinned NuGet package.
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$pin = [regex]::Match((Get-Content (Join-Path $root 'Directory.Packages.props') -Raw), 'Include="Microsoft.WindowsAppSDK" Version="([^"]+)"').Groups[1].Value
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$framework = Join-Path $packages "microsoft.windowsappsdk.runtime\$pin\tools\MSIX\win10-$Arch\Microsoft.WindowsAppRuntime.2.msix"
if (-not (Test-Path $framework)) { throw "Windows App SDK framework package not found: $framework" }

if ($Mode -eq 'Wack') {
    # The kit installs and removes the package itself; it needs the framework and the trust, and no prior install.
    Add-AppxPackage -Path $framework -ErrorAction SilentlyContinue
    $appcert = "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe"
    if (-not (Test-Path $appcert)) { throw 'The Windows App Certification Kit is not installed on this runner.' }
    $report = Join-Path $OutDir 'wack-report.xml'
    & $appcert reset
    & $appcert test -appxpackagepath $signed -reportoutputpath $report
    $exit = $LASTEXITCODE
    Write-Host "appcert exit code: $exit"
    if (-not (Test-Path $report)) { throw 'appcert wrote no report.' }
    $xml = [xml](Get-Content $report -Raw)
    Write-Host "Overall result: $($xml.REPORT.OVERALL_RESULT)"
    $failed = @($xml.SelectNodes('//TEST[RESULT="FAIL"]'))
    foreach ($t in $failed) { Write-Host "FAIL: $($t.NAME)" }
    if ($failed.Count -gt 0) { exit 1 }
    exit $exit
}

Add-AppxPackage -Path $signed -DependencyPath $framework -ErrorAction Stop
$installed = Get-AppxPackage -Name $packageName
Write-Host ("installed: {0} at {1}, signature {2}" -f $installed.PackageFullName, $installed.InstallLocation, $installed.SignatureKind)
if ($installed.InstallLocation -notlike '*\WindowsApps\*') { throw 'The package is not installed under WindowsApps.' }

$result = Join-Path $OutDir "selftest-$Arch.json"
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\quire.exe'
$process = Start-Process -FilePath $alias -ArgumentList '--selftest', "`"$Pdf`"", "`"$result`"" -Wait -PassThru
Write-Host "self-test exit code: $($process.ExitCode)"
if (Test-Path $result) { Get-Content $result | Write-Host } else { throw 'The self-test wrote no result.' }
if ($process.ExitCode -ne 0) { exit $process.ExitCode }

# Removing the package must leave nothing of ours behind, including the protected directories under LocalState.
$dataDir = Join-Path $env:LOCALAPPDATA "Packages\$($installed.PackageFamilyName)"
Remove-AppxPackage -Package $installed.PackageFullName
Write-Host "app data after uninstall: exists=$(Test-Path $dataDir)"
if (Test-Path $dataDir) { throw "Uninstall left $dataDir behind." }
