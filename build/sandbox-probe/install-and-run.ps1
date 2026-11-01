<#
.SYNOPSIS
  CI ONLY. Signs the test package with an ephemeral self-signed certificate, trusts it on the (throwaway) runner, installs it, runs the probe
  (as the runner's token and again at medium integrity), then uninstalls it and checks what is left of the package's data.
.DESCRIPTION
  Refuses to run anywhere but GitHub-hosted runners: it adds a certificate to the machine trust store, which must never happen on a developer machine.
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
Log "OS: $([System.Environment]::OSVersion.Version)  processor: $env:PROCESSOR_ARCHITECTURE"

# A file in some other installed application package, to check that the sandbox cannot read other apps' files.
$other = ''
foreach ($p in (Get-AppxPackage | Where-Object { $_.InstallLocation -like 'C:\Program Files\WindowsApps\*' -and $_.Name -ne 'Quire.SandboxProbe' -and -not $_.IsFramework })) {
    $candidate = Join-Path $p.InstallLocation 'AppxManifest.xml'
    try { [void][System.IO.File]::ReadAllBytes($candidate); $other = $candidate; break } catch { }
}
Log "other-package file: $other"

$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\quirepkgprobe.exe'
Log "running $alias (the runner's own token)"
& $alias --out $OutDir --workspace $Workspace --other-package-file $other 2>&1 | ForEach-Object { Log "$_" }
Log "probe exit code: $LASTEXITCODE"

# ---- the same at medium integrity, without creating a user: a filtered token through runas /trustlevel ------------------------------------
$mediumOut = Join-Path $OutDir 'medium'
New-Item -ItemType Directory -Force $mediumOut | Out-Null
$inner = '\"' + $alias + '\" --out \"' + $mediumOut + '\" --workspace \"' + $Workspace + '\" --other-package-file \"' + $other + '\" --modes a,k'
foreach ($attempt in 1, 2) {
    $line = if ($attempt -eq 1) { "/trustlevel:0x20000 `"$inner`"" } else { "/c runas /trustlevel:0x20000 `"$inner`"" }
    $exe = if ($attempt -eq 1) { 'runas.exe' } else { 'cmd.exe' }
    Log "medium-integrity attempt ${attempt}: $exe $line"
    try {
        $proc = Start-Process -FilePath $exe -ArgumentList $line -PassThru -WindowStyle Hidden
        for ($i = 0; $i -lt 90 -and -not (Test-Path (Join-Path $mediumOut 'results.md')); $i++) { Start-Sleep -Seconds 2 }
        # The probe flushes results.md after every mode; wait for the summary.
        for ($i = 0; $i -lt 60; $i++) {
            if ((Test-Path (Join-Path $mediumOut 'results.md')) -and (Select-String -Path (Join-Path $mediumOut 'results.md') -Pattern '^## Summary' -Quiet)) { break }
            Start-Sleep -Seconds 2
        }
    } catch { Log "medium-integrity attempt ${attempt} failed to start: $($_.Exception.Message)" }
    if (Test-Path (Join-Path $mediumOut 'results.md')) { break }
}
if (-not (Test-Path (Join-Path $mediumOut 'results.md'))) { Log 'MEDIUM INTEGRITY: no results after two attempts (runas /trustlevel did not start the packaged probe).' }

# Code Integrity and application events from the run, in case a probe process was killed.
foreach ($logName in 'Microsoft-Windows-CodeIntegrity/Operational', 'Application') {
    try {
        Get-WinEvent -LogName $logName -MaxEvents 80 -ErrorAction Stop |
            Select-Object TimeCreated, Id, ProviderName, Message | Format-List | Out-String -Width 220 |
            Set-Content -Path (Join-Path $OutDir ('events-' + ($logName -replace '[/\\]', '_') + '.txt'))
    } catch { Log "could not read $logName : $($_.Exception.Message)" }
}

# ---- uninstall: is the package's data, including the protected directory, removed? -------------------------------------------------------
$data = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)"
Log "package data folder: $data exists=$(Test-Path $data)"
if (Test-Path $data) {
    Get-ChildItem $data -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 60 FullName | ForEach-Object { Log "  before uninstall: $($_.FullName)" }
    $priv = Join-Path $data 'LocalState\private-probe'
    if (Test-Path $priv) { (icacls $priv) -join "`n" | ForEach-Object { Log "  ACL of the protected directory:`n$_" } }
}
Remove-AppxPackage -Package $pkg.PackageFullName
Start-Sleep -Seconds 5
$left = Test-Path $data
Log "AFTER UNINSTALL: package data folder exists=$left"
if ($left) { Get-ChildItem $data -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 60 FullName | ForEach-Object { Log "  left behind: $($_.FullName)" } }
Log "AFTER UNINSTALL: package still registered=$([bool](Get-AppxPackage -Name 'Quire.SandboxProbe'))"

$results = Join-Path $OutDir 'results.md'
if (Test-Path $results) {
    if ($env:GITHUB_STEP_SUMMARY) {
        Get-Content $results -Raw | Add-Content -Path $env:GITHUB_STEP_SUMMARY
        if (Test-Path (Join-Path $mediumOut 'results.md')) { "`n# Medium integrity run`n" | Add-Content -Path $env:GITHUB_STEP_SUMMARY; Get-Content (Join-Path $mediumOut 'results.md') -Raw | Add-Content -Path $env:GITHUB_STEP_SUMMARY }
    }
} else {
    Log 'NO results.md was written: the self-test did not run (see install.log)'
    exit 2
}
