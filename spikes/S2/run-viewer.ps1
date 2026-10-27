# usage: ./run-viewer.ps1 <timeoutSec> <args...>   (runs S2Viewer.exe, waits, prints summary head or ERROR.txt)
param([int]$TimeoutSec, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
$root = $PSScriptRoot
$exe = Get-ChildItem "$root\out\bin\Quire.S2.Viewer" -Recurse -Filter S2Viewer.exe | Select-Object -First 1
$p = Start-Process -FilePath $exe.FullName -ArgumentList $Rest -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill($true); Get-Process engine -ErrorAction SilentlyContinue | Stop-Process -Force; Write-Output "TIMEOUT after $TimeoutSec s" }
else { Write-Output "exit code $($p.ExitCode)" }
