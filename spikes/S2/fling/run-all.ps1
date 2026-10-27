# Full S2 viewer pass: iGPU then dGPU (all groups), then a repeat of the fling group on both. ~75 min. Machine must be idle.
# -SkipIgpuFull: the iGPU full pass (tag f1) already exists.
param([switch]$SkipIgpuFull)
$here = $PSScriptRoot
if (-not $SkipIgpuFull) { & "$here\run-matrix.ps1" -Gpu igpu -Tag f1 *> "$here\..\out\results\matrix-f1-igpu.log" }
& "$here\run-matrix.ps1" -Gpu dgpu -Tag f1 *> "$here\..\out\results\matrix-f1-dgpu.log"
& "$here\run-matrix.ps1" -Gpu igpu -Tag f2 -Only fling *> "$here\..\out\results\matrix-f2-igpu.log"
& "$here\run-matrix.ps1" -Gpu dgpu -Tag f2 -Only fling *> "$here\..\out\results\matrix-f2-dgpu.log"
"ALL DONE" | Out-File "$here\..\out\results\run-all.done"
