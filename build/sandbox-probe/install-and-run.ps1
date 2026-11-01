<#
.SYNOPSIS
  CI ONLY. Signs the test package with an ephemeral self-signed certificate, trusts it on the (throwaway) runner, installs it and runs the probe.
.DESCRIPTION
  Refuses to run anywhere but GitHub Actions: it adds a certificate to the machine trust store, which must never happen on a developer machine.
  Windows PowerShell 5.1 compatible (the Appx cmdlets live there).
#>
param(
    [Parameter(Mandatory)] [string] $Msix,
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [string] $Workspace
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This script trusts a self-signed certificate. It runs only on GitHub-hosted runners.'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'install.log'
function Log([string] $m) { $m | Tee-Object -FilePath $log -Append | Out-Host }

$subject = 'CN=QuireProbeCI'
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'QuireProbeCI (ephemeral)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$cer = Join-Path $env:RUNNER_TEMP 'QuireProbeCI.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Log "ephemeral certificate $($cert.Thumbprint) trusted on this runner only (the private key never leaves it)"

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $Msix 2>&1 | ForEach-Object { Log "$_" }
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

try {
    Add-AppxPackage -Path $Msix -ErrorAction Stop
} catch {
    Log "Add-AppxPackage FAILED: $($_.Exception.Message)"
    Get-AppPackageLog -All -ErrorAction SilentlyContinue | Select-Object -First 30 | Out-String | ForEach-Object { Log $_ }
    throw
}
$pkg = Get-AppxPackage -Name 'Quire.SandboxProbe'
Log ("installed: {0}  InstallLocation={1}  SignatureKind={2}  IsDevelopmentMode={3}" -f $pkg.PackageFullName, $pkg.InstallLocation, $pkg.SignatureKind, $pkg.IsDevelopmentMode)

# A file in some other installed application package, to check that the sandbox cannot read other apps' files.
$other = ''
foreach ($p in (Get-AppxPackage | Where-Object { $_.InstallLocation -like 'C:\Program Files\WindowsApps\*' -and $_.Name -ne 'Quire.SandboxProbe' -and -not $_.IsFramework })) {
    $candidate = Join-Path $p.InstallLocation 'AppxManifest.xml'
    try { [void][System.IO.File]::ReadAllBytes($candidate); $other = $candidate; break } catch { }
}
Log "other-package file: $other"

$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\quirepkgprobe.exe'
Log "running $alias"
& $alias --out $OutDir --workspace $Workspace --other-package-file $other 2>&1 | ForEach-Object { Log "$_" }
Log "probe exit code: $LASTEXITCODE"

$results = Join-Path $OutDir 'results.md'
if (Test-Path $results) {
    if ($env:GITHUB_STEP_SUMMARY) { Get-Content $results -Raw | Add-Content -Path $env:GITHUB_STEP_SUMMARY }
} else {
    Log 'NO results.md was written: the self-test did not run (see install.log)'
    exit 2
}
