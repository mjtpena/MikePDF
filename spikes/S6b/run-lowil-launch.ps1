<#
.SYNOPSIS
  S6b option (c): can a LOW-integrity process (prevhost's default for stream-initialised preview handlers) launch the AppContainer engine itself?
  Runs the ShellHost binary in --launch-test mode, once at medium integrity (control) and once with a low-integrity token. The engine deployment, the AppContainer
  profile and the PDF already exist (created by the medium run), so this isolates CreateProcess-into-AppContainer from a low-integrity caller.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'out'
. (Join-Path $here 'env.ps1') -Mode fresh
$low = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\s6b'
New-Item -ItemType Directory -Force $low | Out-Null
$sh = $env:S6B_SHELLHOST
$engine = (Resolve-Path (Join-Path $here '../../build/out/cmake-x64/bin/Release')).Path
$pdf = Join-Path $Pdfs 'text1.pdf'
$persist = (Get-ChildItem $env:TEMP -Directory -Filter 's6b-engine-persist-*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
# A copy of the engine inside LocalLow (the folder Windows labels Low), readable by the profile's AppContainer SID, so the low-integrity process can see it.
$lowEngine = Join-Path $low 'engine'
New-Item -ItemType Directory -Force $lowEngine | Out-Null
Copy-Item (Join-Path $persist '*') $lowEngine -Force
$sid = ((Get-Acl $persist).Access | Where-Object { $_.IdentityReference.Value -like 'S-1-15-2-*' } | Select-Object -First 1).IdentityReference.Value
icacls $lowEngine /grant "*${sid}:(OI)(CI)RX" /T | Out-Null
$persist = $lowEngine
$lines = @()
$lines += '== control: the same launch test at medium integrity'
$res = Join-Path $out 'launch-medium.txt'
Remove-Item $res -ErrorAction SilentlyContinue
& $sh --launch-test $pdf $res --engine-dir $engine --tmp $env:TEMP --log "$out\launch-medium.log" | Out-Null
Start-Sleep -Seconds 2
$lines += (Get-Content $res -ErrorAction SilentlyContinue)
$lines += '== the same at LOW integrity (token with the Low mandatory label)'
$res2 = Join-Path $low 'launch-low.txt'
Remove-Item $res2 -ErrorAction SilentlyContinue
& $TH --low-exec "`"$sh`" --launch-test `"$pdf`" `"$res2`" --engine-in-place `"$persist`" --tmp `"$low`"" | ForEach-Object { $lines += $_ }
$lines += (Get-Content $res2 -ErrorAction SilentlyContinue)
$lines += '== low integrity again, with the profile SID derived from the existing profile (CreateOrOpen is refused at low integrity) so the next step is reached'
Remove-Item $res2 -ErrorAction SilentlyContinue
$env:S6B_DERIVE_PROFILE = '1'
& $TH --low-exec "`"$sh`" --launch-test `"$pdf`" `"$res2`" --engine-in-place `"$persist`" --tmp `"$low`"" | ForEach-Object { $lines += $_ }
$lines += (Get-Content $res2 -ErrorAction SilentlyContinue)
if (-not (Test-Path $res2)) { $lines += 'no result file was written by the low-integrity process (it failed before reaching the test)' }
$lines | Tee-Object -FilePath (Join-Path $out 'lowil-launch.txt')
