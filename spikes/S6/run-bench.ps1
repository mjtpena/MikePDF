param(
    [int]$Warm = 30,
    [string]$Tag = 'r1',
    [string[]]$Modes = @('jit-x64', 'r2r-x64', 'aot-x64', 'msix-jit-x64', 'msix-aot-x64')
)
# S6.2 startup benchmark driver. Run LAST, after every other S6 step, because other agents share this machine.
# Records system CPU load before the batch; the harness itself logs system CPU% per run.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bench = "$root\Bench\bin\Release\net10.0\Bench.exe"
$outDir = "$root\out\bench"; New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$alias = "$env:LOCALAPPDATA\Microsoft\WindowsApps\quires6.exe"
$jitMsixLayout = "$root\HelloQuire\bin\x64\Release\net10.0-windows10.0.26100.0"
$aotMsixLayout = "$root\out\publish\aot-msix-x64"

function Unregister { Get-Process HelloQuire -ErrorAction SilentlyContinue | Stop-Process -Force; Get-AppxPackage Quire.S6Spike -ErrorAction SilentlyContinue | Remove-AppxPackage }

foreach ($m in $Modes) {
    $load = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 5).CounterSamples.CookedValue
    "[$Tag] $m : system CPU before batch (5 x 1 s samples) = " + (($load | % { [math]::Round($_, 0) }) -join ', ') | Tee-Object -FilePath "$outDir\$Tag-load.txt" -Append
    switch ($m) {
        'jit-x64'      { & $bench "$root\out\publish\jit-x64\HelloQuire.exe" "$Tag-$m" $Warm "$outDir\$Tag-$m.csv" --copy-fresh }
        'r2r-x64'      { & $bench "$root\out\publish\r2r-x64\HelloQuire.exe" "$Tag-$m" $Warm "$outDir\$Tag-$m.csv" --copy-fresh }
        'aot-x64'      { & $bench "$root\out\publish\aot-x64\HelloQuire.exe" "$Tag-$m" $Warm "$outDir\$Tag-$m.csv" --copy-fresh }
        'msix-jit-x64' { Unregister; Add-AppxPackage -Register "$jitMsixLayout\AppxManifest.xml"; & $bench $alias "$Tag-$m" $Warm "$outDir\$Tag-$m.csv"; Unregister }
        'msix-aot-x64' { Unregister; Add-AppxPackage -Register "$aotMsixLayout\AppxManifest.xml"; & $bench $alias "$Tag-$m" $Warm "$outDir\$Tag-$m.csv"; Unregister }
    }
}
Unregister
Get-ChildItem $env:TEMP -Directory -Filter 's6bench-*' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
