# Runs one launcher verb with a hard timeout and tees output to out/<name>.txt.
# usage: ./run.ps1 <name> <timeoutSec> <verb> [args...]
param([string]$Name, [int]$TimeoutSec, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
$root = $PSScriptRoot
$exe = Join-Path $root 'out\launcher\quire-s3.exe'
$p = Start-Process -FilePath $exe -ArgumentList $Rest -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $root "out\$Name.txt") -RedirectStandardError (Join-Path $root "out\$Name.err")
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    $p.Kill($true)
    Get-Process quire-engine-stub -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Output "TIMEOUT after $TimeoutSec s"
}
Get-Content (Join-Path $root "out\$Name.txt")
$err = Get-Content (Join-Path $root "out\$Name.err")
if ($err) { Write-Output '--- stderr ---'; $err }
