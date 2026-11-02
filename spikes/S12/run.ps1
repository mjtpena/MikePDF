<#
.SYNOPSIS
  Spike S12 (printing): build and run every measurement. Throwaway.
.DESCRIPTION
  Prints only to the inbox "Microsoft Print to PDF" queue, only to files under spikes/S12/out, and deletes them afterwards. It never changes the default printer,
  adds a printer or driver, or touches system settings. No window is opened (the print path is GDI; the one hidden window in `info` is created and destroyed without being shown).
  The sandbox probe starts in the EXISTING per-user AppContainer profile "mikepdf.render" (it creates none). It needs one ACL grant on spikes/S12/out/probe for that profile's
  SID so the container can run the probe and write its report there; the folder is deleted at the end, which removes the grant.
  Prerequisites: spikes/_shared/pdfium/fetch.ps1 -Variants pdfium-win-x64; VS Build Tools 2022 (for the native probe).
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build -c Release | Out-Host
$env:S12_OUT = "$PSScriptRoot\out"
$exe = "$PSScriptRoot\bin\Release\net10.0-windows10.0.26100.0\win-x64\S12.exe"

& $exe info              # printers, device capabilities, PrintTicket capabilities, PrintManager.IsSupported, hidden-HWND PrintManagerInterop.GetForWindow
& $exe layout            # range grammar and PrintLayout tables
& $exe xps               # IPrintDocumentPackageTarget created for the printer, then cancelled
& $exe jobs              # jobs A..E: 3-page jobs with range, subset, scaling, duplex, copies, image mode; SSIM against engine renders
& $exe peek              # what the printer writes into the PDF (producer, filters)
& $exe vector            # FPDF_RenderPage into the printer DC (unsandboxed comparison)
& $exe cancel            # AbortDoc after 3 pages
foreach ($v in 'b1','w10','b10','c10','v10','b100','i100','v100') { & $exe perfone $v }   # one fresh process per variant: clean memory baselines (b100 takes about 6 minutes)

# Candidate (c): printing from inside the engine's sandbox
dotnet build Probe\Probe.csproj -c Release -o out\probe | Out-Host
cmd /c "$PSScriptRoot\Probe\build-probe.cmd" | Out-Host
# Grant modify on out\probe only, to the SID of the existing profile
$map = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings'
$sid = (Get-ChildItem $map | Where-Object { (Get-ItemProperty $_.PSPath).Moniker -eq 'mikepdf.render' }).PSChildName
icacls "$PSScriptRoot\out\probe" /grant "*${sid}:(OI)(CI)(M)" | Out-Host
& $exe ac "$PSScriptRoot\out" mikepdf.render
& $exe acbisect "$PSScriptRoot\out"
Remove-Item -Recurse -Force "$PSScriptRoot\out\probe", "$PSScriptRoot\out\denied", "$PSScriptRoot\out\obj" -ErrorAction SilentlyContinue

# Native AOT check of the GDI path (needs vswhere.exe on PATH, see S6)
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish S12.csproj -c Release -r win-x64 -p:PublishAot=true -o out\aot | Out-Host
$env:S12_OUT = "$PSScriptRoot\out\aot-run"; & .\out\aot\S12.exe jobs   # the final JSON write fails under AOT (reflection serialiser, spike only)
