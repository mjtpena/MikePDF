# S2 viewer matrix driver. Usage: run-matrix.ps1 -Gpu default|igpu|dgpu -Tag <name> [-Only fling,upload,ttft,zoom,extras]
# GPU selection: Windows per-app graphics preference (HKCU\Software\Microsoft\DirectX\UserGpuPreferences\<exe>), removed again at the end.
param([ValidateSet('default', 'igpu', 'dgpu')][string]$Gpu = 'default', [string]$Tag = 'run', [string]$Only = 'fling,upload,ttft,zoom,extras,hidpi', [int]$Repeat = 1)
$ErrorActionPreference = 'Stop'
$OnlyList = $Only -split ','
$root = Split-Path $PSScriptRoot -Parent
$exe = (Get-ChildItem "$root\out\bin\Quire.S2.Viewer" -Recurse -Filter S2Viewer.exe | Select-Object -First 1).FullName
$fx = "$root\out\fixtures"
$res = "$root\out\results\viewer\$Tag-$Gpu"; New-Item -ItemType Directory -Force $res | Out-Null
$regPath = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
$hadValue = $null -ne (Get-ItemProperty $regPath -Name $exe -ErrorAction SilentlyContinue)
if ($hadValue) { throw "preference for $exe already exists; refusing to overwrite" }
switch ($Gpu) {
    'igpu' { New-ItemProperty -Path $regPath -Name $exe -Value 'GpuPreference=1;' -PropertyType String -Force | Out-Null }
    'dgpu' { New-ItemProperty -Path $regPath -Name $exe -Value 'GpuPreference=2;' -PropertyType String -Force | Out-Null }
}
function Cpu { try { (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 3 -ErrorAction Stop).CounterSamples.CookedValue | ForEach-Object { [math]::Round($_, 0) } } catch { 'n/a' } }
function Run([string]$label, [string[]]$a, [int]$timeout = 150) {
    $load = (Cpu) -join ','
    $p = Start-Process -FilePath $exe -ArgumentList (@('--label', $label, '--out', $res) + $a) -PassThru
    if (-not $p.WaitForExit($timeout * 1000)) {
        $p.Kill($true); Get-Process engine -ErrorAction SilentlyContinue | Stop-Process -Force
        "$label : TIMEOUT" | Tee-Object "$res\driver.log" -Append
        return
    }
    "$label : exit $($p.ExitCode) (system CPU before: $load)" | Tee-Object "$res\driver.log" -Append
}
try {
    $docs = @{ text2000 = "$fx\text-2000.pdf"; book = "$fx\real-gpo911-book.pdf"; vec60 = "$fx\stress-vector-60.pdf"; tr60 = "$fx\stress-transp-60.pdf" }
    for ($rep = 1; $rep -le $Repeat; $rep++) {
        if ($OnlyList -contains 'fling') {
            foreach ($tile in 512, 256) { foreach ($d in 'text2000', 'book') { foreach ($p in 'win2d', 'comp') {
                Run "fling-$d-$p-t$tile-r$rep" @('--mode', 'fling', '--presenter', $p, '--doc', $docs[$d], '--tile', $tile) } } }
            foreach ($d in 'vec60', 'tr60') { foreach ($p in 'win2d', 'comp') { Run "fling-$d-$p-t512-r$rep" @('--mode', 'fling', '--presenter', $p, '--doc', $docs[$d], '--tile', 512) } }
        }
        if ($OnlyList -contains 'upload') {
            foreach ($tile in 256, 512) { foreach ($p in 'win2d', 'comp') { Run "upload-$p-t$tile" @('--mode', 'upload', '--presenter', $p, '--doc', "$fx\text-book.pdf", '--tile', $tile, '--n', 200) } }
        }
        if ($OnlyList -contains 'ttft') {
            foreach ($t in 256, 512) { Run "ttft-warm-text20-t$t" @('--mode', 'ttft', '--presenter', 'win2d', '--doc', "$fx\text-20.pdf", '--tile', $t, '--startpage', 5, '--iters', 30) }
            Run "ttft-warm-text20-comp-t512" @('--mode', 'ttft', '--presenter', 'comp', '--doc', "$fx\text-20.pdf", '--tile', 512, '--startpage', 5, '--iters', 30)
            Run "ttft-warm-text2000-p0-t512" @('--mode', 'ttft', '--presenter', 'win2d', '--doc', $docs.text2000, '--tile', 512, '--startpage', 0, '--iters', 30)
            Run "ttft-warm-book-t512" @('--mode', 'ttft', '--presenter', 'win2d', '--doc', $docs.book, '--tile', 512, '--startpage', 100, '--iters', 30)
            Run "ttft-warm-topo-t512" @('--mode', 'ttft', '--presenter', 'win2d', '--doc', "$fx\real-usgs-topo.pdf", '--tile', 512, '--startpage', 0, '--zoom', 100, '--iters', 20)
            foreach ($i in 1..12) { Run "ttft-cold-text20-t512-$i" @('--mode', 'ttft', '--presenter', 'win2d', '--doc', "$fx\text-20.pdf", '--tile', 512, '--startpage', 5, '--iters', 1, '--cold') }
            foreach ($i in 1..12) { Run "ttft-cold-text2000-t512-$i" @('--mode', 'ttft', '--presenter', 'win2d', '--doc', $docs.text2000, '--tile', 512, '--startpage', 0, '--iters', 1, '--cold') }
        }
        if ($OnlyList -contains 'zoom') {
            foreach ($p in 'win2d', 'comp') { Run "zoom-$p-book" @('--mode', 'zoom', '--presenter', $p, '--doc', $docs.book, '--tile', 512, '--cycles', 6) }
        }
        if ($OnlyList -contains 'hidpi') {
            # zoom 200% / 400% flings approximate R3's 4K at 200% / 400%-class pixel and tile counts (this display is 100% scale)
            foreach ($z in 200, 400) { foreach ($p in 'win2d', 'comp') { Run "hidpi-z$z-text2000-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.text2000, '--zoom', $z) } }
            foreach ($p in 'win2d', 'comp') { Run "hidpi-z200-vec60-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.vec60, '--zoom', 200) }
            foreach ($p in 'win2d', 'comp') { Run "hidpi-z200-book-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.book, '--zoom', 200) }
        }
        if ($OnlyList -contains 'r1like') {
            # R1 stand-in: Intel iGPU + the process (and the engine child) pinned to 4 E-cores (logical CPUs 2-5, mask 60). Clock/IPC/memory of a real 4-core R1 are NOT reproduced.
            foreach ($z in 100, 200, 400) { foreach ($p in 'win2d', 'comp') { Run "r1like-z$z-text2000-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.text2000, '--zoom', $z, '--affinity', 60) } }
            foreach ($p in 'win2d', 'comp') { Run "r1like-z200-book-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.book, '--zoom', 200, '--affinity', 60) }
            foreach ($p in 'win2d', 'comp') { Run "r1like-z100-tr60-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.tr60, '--affinity', 60) }
            foreach ($p in 'win2d', 'comp') { Run "r1like-z100-vec60-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.vec60, '--affinity', 60) }
        }
        if ($OnlyList -contains 'r1ttft') {
            foreach ($p in 'win2d', 'comp') { Run "r1like-ttft-text20-$p" @('--mode', 'ttft', '--presenter', $p, '--doc', "$fx\text-20.pdf", '--tile', 512, '--startpage', 5, '--iters', 30, '--affinity', 60) }
        }
        if ($OnlyList -contains 'extras') {
            # transport: sandbox on/off; upload thread vs UI thread; low-res off; progressive off; inflight depth; slower fling; 4 E-cores
            Run "x-nosandbox-text2000-win2d" @('--mode', 'fling', '--presenter', 'win2d', '--doc', $docs.text2000, '--sandbox', 0)
            foreach ($p in 'win2d', 'comp') { Run "x-uiupload-text2000-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.text2000, '--upload', 'ui') }
            foreach ($p in 'win2d', 'comp') { Run "x-nolowres-vec60-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.vec60, '--lowres', 0) }
            Run "x-noprogressive-vec60-comp" @('--mode', 'fling', '--presenter', 'comp', '--doc', $docs.vec60, '--progressive', 0)
            foreach ($k in 1, 6) { Run "x-inflight$k-vec60-comp" @('--mode', 'fling', '--presenter', 'comp', '--doc', $docs.vec60, '--inflight', $k) }
            foreach ($p in 'win2d', 'comp') { Run "x-v6000-text2000-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.text2000, '--v0', 6000) }
            foreach ($p in 'win2d', 'comp') { Run "x-4ecores-text2000-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.text2000, '--affinity', 60) }
            foreach ($p in 'win2d', 'comp') { Run "x-4ecores-vec60-$p" @('--mode', 'fling', '--presenter', $p, '--doc', $docs.vec60, '--affinity', 60) }
            Run "x-prefetch0-vec60-comp" @('--mode', 'fling', '--presenter', 'comp', '--doc', $docs.vec60, '--prefetch', 0)
            Run "x-prefetch2-vec60-comp" @('--mode', 'fling', '--presenter', 'comp', '--doc', $docs.vec60, '--prefetch', 2)
            Run "x-idle-win2d" @('--mode', 'idle', '--presenter', 'win2d', '--doc', "$fx\text-20.pdf")
            Run "x-idle-comp" @('--mode', 'idle', '--presenter', 'comp', '--doc', "$fx\text-20.pdf")
        }
    }
}
finally {
    if ($Gpu -ne 'default') { Remove-ItemProperty -Path $regPath -Name $exe -ErrorAction SilentlyContinue }
    Get-Process engine -ErrorAction SilentlyContinue | Stop-Process -Force
}
