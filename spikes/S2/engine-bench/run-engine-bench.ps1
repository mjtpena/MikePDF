# S2.1 driver: full matrix (default scheduling), process-cold TTFT x30, and the E-core pinned repeat.
# Machine must be otherwise idle. Output: spikes/S2/out/results/engine-*
param([switch]$SkipCold, [switch]$SkipEcore)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bench = "$root\out\engine-bench\bench.exe"; $fx = "$root\out\fixtures"
function Cpu { (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 5).CounterSamples.CookedValue | % { [math]::Round($_, 1) } }
$d = "$root\out\results\engine-default"; New-Item -ItemType Directory -Force $d | Out-Null
"cpu before: " + ((Cpu) -join ',') | Tee-Object "$d\load.txt"
& $bench all $fx $d | Out-File "$d\stdout.txt"
"cpu after: " + ((Cpu) -join ',') | Tee-Object "$d\load.txt" -Append
if (-not $SkipCold) {
    $out = "$root\out\results\engine-cold.csv"
    "fixture,tile,state,init_ms,open_ms,page_ms,firstTile_ms,ttft_excl_init_ms" | Out-File $out -Encoding ascii
    foreach ($f in Get-ChildItem "$fx\*.pdf") { foreach ($t in 256, 512) { 1..30 | % { & $bench cold $f.FullName $t | % { $_ -replace [regex]::Escape($fx + ''), '' } | Out-File $out -Append -Encoding ascii } } }
}
if (-not $SkipEcore) {
    $e = "$root\out\results\engine-ecore"; New-Item -ItemType Directory -Force $e | Out-Null
    "cpu before: " + ((Cpu) -join ',') | Tee-Object "$e\load.txt"
    & $bench all $fx $e 0x10 | Out-File "$e\stdout.txt"     # logical CPU 4 = efficiency class 0 (E-core) on this 275HX, see bench.exe cpuinfo
    "cpu after: " + ((Cpu) -join ',') | Tee-Object "$e\load.txt" -Append
}
