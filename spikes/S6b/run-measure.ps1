<#
.SYNOPSIS
  S6b local measurements: the handler DLL loaded by the test host (no registration, no windows shown), ShellHost started on demand, the real sandboxed engine.
  Every command line and every raw CSV goes to spikes/S6b/out/.
#>
param([string[]] $Only = @(), [switch] $Quick)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'out'
$cmds = Join-Path $out 'commands.txt'
. (Join-Path $here 'env.ps1')   # sets $TH, $Dll, $Pdfs and the S6B_* variables
$n = if ($Quick) { 0.1 } else { 1.0 }
function N([int] $x) { [Math]::Max(3, [int]($x * $n)) }

function Stop-Shell { & $TH --shutdown | Out-Null; Start-Sleep -Milliseconds 300 }
function Scenario([string] $name, [string] $mode, [string] $thArgs, [hashtable] $env = @{}, [string] $extra = '') {
    if ($Only.Count -and $Only -notcontains $name) { return }
    . (Join-Path $here 'env.ps1') -Mode $mode -Extra $extra
    $env:S6B_BUDGET_MS = '30000'
    foreach ($k in $env.Keys) { Set-Item "Env:$k" $env[$k] }
    Stop-Shell
    $file = Join-Path $out "m-$name.csv"
    $line = "$TH --dll $Dll $thArgs --label $name   (S6B_SHELLHOST_ARGS=$env:S6B_SHELLHOST_ARGS; extra env: $(($env.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join ' '))"
    Add-Content $cmds ("[{0}] {1}" -f (Get-Date -Format s), $line)
    Write-Host "==> $name"
    $argList = $thArgs -split ' ' | Where-Object { $_ }   # no value in this script contains a space
    & $TH --dll $Dll @argList --label $name *> $file
    & $TH --shutdown | Out-Null
    foreach ($k in $env.Keys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
}

# Background load, because this machine is shared (the figures are "under whatever load the machine had", not a clean R1/R2 number).
$cpu = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 5 | ForEach-Object { $_.CounterSamples.CookedValue } | Measure-Object -Average).Average
$info = @("date: $(Get-Date -Format s)", "machine: $env:COMPUTERNAME, $((Get-CimInstance Win32_Processor).Name), logical cores $([Environment]::ProcessorCount)", "OS: $([Environment]::OSVersion.VersionString)", "background CPU before the run (5 s average): $([int]$cpu) %")
$info | Set-Content (Join-Path $out 'environment.txt')
Write-Host ($info -join "`n")

$t1 = "$Pdfs\text1.pdf"
# 1-4: the main latency figures, 256 px, the three engine modes, warm and cold.
Scenario 'warm-fresh-256'  'fresh' "--pdf $t1 --cx 256 --runs $(N 200) --save $out\thumb-text1-256.bin --mem"
Scenario 'cold-fresh-256'  'fresh' "--pdf $t1 --cx 256 --runs $(N 30) --cold"
Scenario 'warm-spare-256'  'spare' "--pdf $t1 --cx 256 --runs $(N 200)"
Scenario 'cold-spare-256'  'spare' "--pdf $t1 --cx 256 --runs $(N 30) --cold"
Scenario 'warm-reuse-256'  'reuse' "--pdf $t1 --cx 256 --runs $(N 200)"
# thumbnail sizes
foreach ($cx in 32, 96, 1024) { Scenario "warm-fresh-$cx" 'fresh' "--pdf $t1 --cx $cx --runs $(N 100)" }
# source size: stream copy and engine open cost
Scenario 'size-text20-65KB'  'fresh' "--pdf $Pdfs\text20.pdf --cx 256 --runs $(N 100)"
Scenario 'size-pad5MB'       'fresh' "--pdf $Pdfs\pad5mb.pdf --cx 256 --runs $(N 50)"
Scenario 'size-pad30MB'      'fresh' "--pdf $Pdfs\pad30mb.pdf --cx 256 --runs $(N 30)"
Scenario 'size-pad40MB-over-cap' 'fresh' "--pdf $Pdfs\pad40mb.pdf --cx 256 --runs 3"
# pixel return path: section vs inline over the pipe
foreach ($cx in 256, 1024) {
    Scenario "inline-$cx" 'fresh' "--pdf $t1 --cx $cx --runs $(N 100)" @{ S6B_INLINE = '1' }
}
# IStream read chunk size (what the shell's stream hands back per Read)
foreach ($kb in 4, 64) { Scenario "chunk-${kb}KB-pad30MB" 'fresh' "--pdf $Pdfs\pad30mb.pdf --cx 256 --runs $(N 20)" @{ S6B_CHUNK_KB = "$kb" } }
# preview
Scenario 'preview-warm' 'fresh' "--pdf $t1 --preview --runs $(N 30)"
Scenario 'preview-cold' 'fresh' "--pdf $t1 --preview --runs $(N 15) --cold"
# memory: engine peak working set and ShellHost footprint
Scenario 'mem-fresh-256' 'fresh' "--pdf $Pdfs\text20.pdf --cx 256 --runs $(N 100) --mem" @{ S6B_PEAK = '1' }
Scenario 'mem-fresh-1024' 'fresh' "--pdf $Pdfs\text20.pdf --cx 1024 --runs $(N 50) --mem" @{ S6B_PEAK = '1' }
# hostile and odd inputs: must fail cleanly and fast
foreach ($f in 'encrypted.pdf', 'js.pdf') { Scenario "input-$f" 'fresh' "--pdf $Pdfs\$f --cx 256 --runs 3" }
Set-Content "$Pdfs\zero.pdf" -Value ([byte[]]@()) -AsByteStream
Set-Content "$Pdfs\notpdf.pdf" -Value ([byte[]](1..200)) -AsByteStream
$full = [System.IO.File]::ReadAllBytes("$Pdfs\text20.pdf"); [System.IO.File]::WriteAllBytes("$Pdfs\truncated.pdf", $full[0..($full.Length / 2)])
foreach ($f in 'zero.pdf', 'notpdf.pdf', 'truncated.pdf') { Scenario "input-$f" 'fresh' "--pdf $Pdfs\$f --cx 256 --runs 3" }
Stop-Shell
Write-Host 'done'
