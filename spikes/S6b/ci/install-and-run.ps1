<#
.SYNOPSIS
  CI ONLY. Signs the S6b test package with an ephemeral self-signed certificate, trusts it on the (throwaway) runner, installs it, dumps what the shell registered,
  runs the driver (which exercises the real shell paths), collects the logs, uninstalls and checks what is left.
.DESCRIPTION
  Refuses to run anywhere but GitHub-hosted runners: it adds a certificate to the machine trust store and writes registry-backed shell registrations through the
  package, which must never happen on a developer machine. Windows PowerShell 5.1 compatible (the Appx cmdlets live there).
#>
param(
    [Parameter(Mandatory)] [string] $Msix,
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [string] $NativeDir
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This script trusts a self-signed certificate. It runs only on GitHub-hosted runners.'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'install.log'
function Log([string] $m) { $m | Tee-Object -FilePath $log -Append | Out-Host }
function Dump([string] $name, [scriptblock] $block) {
    Log "---- $name"
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # native tools (reg.exe) write errors to stderr, and an absent key is an answer, not a failure
    try { $text = & $block 2>&1 | Out-String -Width 250; $text | Set-Content (Join-Path $OutDir "$name.txt"); Log $text } catch { Log "FAILED: $($_.Exception.Message)" }
    finally { $ErrorActionPreference = $old }
}

# A log folder every process can append to, whatever its integrity level or container (the handlers run in surrogates we do not control).
$logDir = 'C:\s6b-log'
New-Item -ItemType Directory -Force $logDir | Out-Null
icacls $logDir /grant '*S-1-1-0:(OI)(CI)F' '*S-1-15-2-1:(OI)(CI)F' | Out-Null
icacls $logDir /setintegritylevel '(OI)(CI)L' | Out-Null

$subject = 'CN=MikePDFS6bCI'
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'MikePDF S6b CI (ephemeral)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$cer = Join-Path $env:RUNNER_TEMP 'MikePDFS6bCI.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Log "ephemeral certificate $($cert.Thumbprint) trusted on this runner only (the private key never leaves it)"
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\$hostArch\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $Msix 2>&1 | ForEach-Object { Log "$_" }
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

Dump 'os' { [System.Environment]::OSVersion.VersionString; "processor: $env:PROCESSOR_ARCHITECTURE"; whoami /groups /fo list | Select-String 'Mandatory Label'; "default .pdf handler before install:"; reg query 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice' 2>&1; reg query 'HKCR\.pdf' /ve 2>&1 }

try { Add-AppxPackage -Path $Msix -ErrorAction Stop } catch {
    Log "Add-AppxPackage FAILED: $($_.Exception.Message)"
    Get-AppPackageLog -All -ErrorAction SilentlyContinue | Select-Object -First 40 | Out-String | ForEach-Object { Log $_ }
    throw
}
$pkg = Get-AppxPackage -Name 'MikePDF.S6bProbe'
Log ("installed: {0}  InstallLocation={1}  SignatureKind={2}" -f $pkg.PackageFullName, $pkg.InstallLocation, $pkg.SignatureKind)
$full = $pkg.PackageFullName
$family = $pkg.PackageFamilyName

# ---- what the shell registered (raw registry evidence, read only) --------------------------------------------------------------------------
Dump 'reg-extensions' { foreach ($e in '.s6bpdf', '.pdf') { "##### HKCR\$e"; reg query "HKCR\$e" /s 2>&1 } }
Dump 'reg-packagedcom' { "##### HKCR\PackagedCom\Package\$full"; reg query "HKCR\PackagedCom\Package\$full" /s 2>&1 }
Dump 'reg-clsid-appid' { foreach ($g in '0001', '0002', '0003', '0010', '0011') { $id = "{6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B$g}"; "##### CLSID $id"; reg query "HKCR\CLSID\$id" /s 2>&1; "##### AppID $id"; reg query "HKCR\AppID\$id" /s 2>&1 } }
Dump 'reg-progids' {
    "##### progids that mention the package family or our extension, under HKCR"
    reg query 'HKCR' /f 'S6bProbe' /k 2>&1 | Select-Object -First 40
    reg query 'HKCR' /f $family /k 2>&1 | Select-Object -First 40
    "##### HKCU Classes extension registrations"
    reg query 'HKCU\Software\Classes\Extensions\ContractId\Windows.File\PackageId' /f 'S6bProbe' /k 2>&1 | Select-Object -First 20
    reg query 'HKLM\SOFTWARE\Classes\Extensions\ContractId\Windows.File\PackageId' /f 'S6bProbe' /k 2>&1 | Select-Object -First 20
}
Dump 'reg-shellex' {
    "##### where the thumbnail (E357FCCD) and preview (8895b1c6) handler keys live for our file types"
    foreach ($iid in '{E357FCCD-A995-4576-B01F-234630154E96}', '{8895b1c6-b41f-4c1c-a562-0d564250836f}') {
        reg query 'HKCR' /f $iid /k /s 2>&1 | Select-String 'S6b|AppX|s6b|\.pdf' | Select-Object -First 30
    }
}

# ---- run the driver (inside the package, through its execution alias) ----------------------------------------------------------------------
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\s6bdriver.exe'
$fixtures = Join-Path $pkg.InstallLocation 'Fixtures'
$drvOut = Join-Path $OutDir 'driver'
New-Item -ItemType Directory -Force $drvOut | Out-Null
Log "running $alias"
$proc = Start-Process -FilePath $alias -ArgumentList '--selftest', "`"$drvOut`"", "`"$fixtures`"" -Wait -PassThru -RedirectStandardOutput (Join-Path $OutDir 'driver-stdout.txt') -RedirectStandardError (Join-Path $OutDir 'driver-stderr.txt')
Log "driver exit code: $($proc.ExitCode)"
Get-Content (Join-Path $OutDir 'driver-stdout.txt') -ErrorAction SilentlyContinue | ForEach-Object { Log $_ }

# ---- the same driver at MEDIUM and at LOW integrity, unpackaged (a copy outside the package, so no identity and no registry virtualization) ----------------------------------
# The runner's token is High integrity, so everything above ran at High. A real Explorer runs at Medium, and prevhost's classic default for stream handlers is Low.
# These runs lower the integrity of a duplicate of the runner's token (SetTokenInformation(TokenIntegrityLevel)); it is NOT a filtered user session.
$tools = Join-Path $logDir 'tools'
$fx2 = Join-Path $logDir 'fixtures'
New-Item -ItemType Directory -Force $tools, $fx2 | Out-Null
Copy-Item (Join-Path $NativeDir 'S6bDriver.exe'), (Join-Path $NativeDir 's6b_testhost.exe') $tools
Copy-Item (Join-Path $fixtures 'text1.pdf') $fx2
foreach ($il in 'medium', 'low') {
    $work = Join-Path $logDir "work-$il"
    $o = Join-Path $logDir "out-$il"
    New-Item -ItemType Directory -Force $work, $o | Out-Null
    Log "running the driver at $il integrity"
    & (Join-Path $tools 's6b_testhost.exe') --il $il --low-exec "`"$(Join-Path $tools 'S6bDriver.exe')`" --selftest `"$o`" `"$fx2`" `"$work`"" 2>&1 | ForEach-Object { Log "$_" }
    if (Test-Path (Join-Path $o 'driver.log')) { Copy-Item (Join-Path $o 'driver.log') (Join-Path $OutDir "driver-$il.log"); Get-Content (Join-Path $o 'driver.log') | ForEach-Object { Log $_ } } else { Log "no driver.log at $il integrity" }
}

# ---- who wins when another handler is registered for .pdf? (HKCU\Software\Classes writes: this disposable runner only) ----------------------------------------------------
$expOut = Join-Path $logDir 'out-experiments'
New-Item -ItemType Directory -Force $expOut, (Join-Path $logDir 'work-exp') | Out-Null
foreach ($name in 'none', 'ext-level', 'progid-level', 'both') {
    & (Join-Path $tools 'S6bDriver.exe') --experiment $name $expOut $fx2 (Join-Path $logDir 'work-exp') 2>&1 | ForEach-Object { Log "$_" }
}
if (Test-Path (Join-Path $expOut 'experiment.log')) { Copy-Item (Join-Path $expOut 'experiment.log') (Join-Path $OutDir 'experiment.log') }
Dump 'reg-after-experiments' { reg query 'HKCU\Software\Classes\.pdf' /s 2>&1 }

# ShellHost exits by itself 60 s after its last request (spec 3.2): is it gone?
Log 'waiting 75 s for the idle exit of ShellHost and the ExeServer ...'
Start-Sleep -Seconds 75
Dump 'processes-after-idle' { Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'S6b|MikePDF.Engine' } | Select-Object ProcessId, Name | Format-Table | Out-String }
Log "S6bShellHost running after the idle wait: $([bool](Get-Process S6bShellHost -ErrorAction SilentlyContinue)); S6bComServer: $([bool](Get-Process S6bComServer -ErrorAction SilentlyContinue)); engines: $(@(Get-Process MikePDF.Engine -ErrorAction SilentlyContinue).Count)"

# ---- the logs the handlers wrote, and what is still running -------------------------------------------------------------------------------
foreach ($f in 'handler.log', 'shellhost.log', 'comserver.log') {
    $p = Join-Path $logDir $f
    if (Test-Path $p) { Copy-Item $p (Join-Path $OutDir $f); Log "==== $f (first 60 lines)"; Get-Content $p | Select-Object -First 60 | ForEach-Object { Log $_ } } else { Log "==== $f was never written" }
}
Dump 'processes-after' { Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'dllhost|prevhost|S6b|MikePDF.Engine' } | Select-Object ProcessId, ParentProcessId, Name, CommandLine | Format-List }
foreach ($logName in 'Microsoft-Windows-AppModel-Runtime/Admin', 'Microsoft-Windows-CodeIntegrity/Operational', 'Application') {
    try {
        Get-WinEvent -LogName $logName -MaxEvents 60 -ErrorAction Stop | Select-Object TimeCreated, Id, ProviderName, Message | Format-List | Out-String -Width 220 |
            Set-Content (Join-Path $OutDir ('events-' + ($logName -replace '[/\\]', '_') + '.txt'))
    } catch { Log "could not read $logName : $($_.Exception.Message)" }
}

# ---- uninstall: does the registration go away? ---------------------------------------------------------------------------------------------
Remove-AppxPackage -Package $full
Start-Sleep -Seconds 3
Dump 'after-uninstall' {
    "package registered: $([bool](Get-AppxPackage -Name 'MikePDF.S6bProbe'))"
    foreach ($e in '.s6bpdf') { "##### HKCR\$e"; reg query "HKCR\$e" /s 2>&1 }
    "##### CLSID 0001"; reg query 'HKCR\CLSID\{6B7C2D10-5A1E-4F3B-9C11-0A5B6D6B0001}' /s 2>&1
    "##### PackagedCom"; reg query "HKCR\PackagedCom\Package\$full" /s 2>&1
}
if (-not (Test-Path (Join-Path $drvOut 'driver.log'))) { Log 'NO driver.log: the driver did not run (see install.log)'; exit 2 }
if ($env:GITHUB_STEP_SUMMARY) { '```' | Add-Content $env:GITHUB_STEP_SUMMARY; Get-Content (Join-Path $drvOut 'driver.log') | Add-Content $env:GITHUB_STEP_SUMMARY; '```' | Add-Content $env:GITHUB_STEP_SUMMARY }
