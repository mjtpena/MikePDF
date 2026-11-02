<#
.SYNOPSIS
  S6b flood: many handler hosts at once (Explorer asks for many thumbnails in parallel). ShellHost must keep live engines at its cap, finish or fail every request, and not grow.
  N test-host processes, each making R thumbnail calls, all against one ShellHost with --max-engines M.
#>
param([int] $Hosts = 16, [int] $Runs = 10, [int] $MaxEngines = 3, [string] $Mode = 'fresh')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'out'
. (Join-Path $here 'env.ps1') -Mode $Mode -MaxEngines $MaxEngines
$env:S6B_BUDGET_MS = '30000'
& $TH --shutdown | Out-Null
Remove-Item "$out\shellhost.log" -ErrorAction SilentlyContinue
$work = Join-Path $out "flood-$Hosts-x-$Runs-max$MaxEngines-$Mode"
New-Item -ItemType Directory -Force $work | Out-Null
Get-ChildItem $work | Remove-Item -Force
$sw = [Diagnostics.Stopwatch]::StartNew()
$procs = 1..$Hosts | ForEach-Object {
    Start-Process -FilePath $TH -ArgumentList '--dll', "`"$Dll`"", '--pdf', "`"$Pdfs\text1.pdf`"", '--cx', '256', '--runs', $Runs, '--label', "h$_" `
        -RedirectStandardOutput (Join-Path $work "h$_.csv") -NoNewWindow -PassThru
}
$procs | Wait-Process
"all $Hosts hosts finished after $($sw.ElapsedMilliseconds) ms (wall clock, includes test-host start and the one cold ShellHost start)"
$sh = Get-Process S6bShellHost -ErrorAction SilentlyContinue
if ($sh) { "ShellHost after the flood: pid $($sh.Id), working set $([int]($sh.WorkingSet64 / 1MB)) MB, private $([int]($sh.PrivateMemorySize64 / 1MB)) MB, threads $($sh.Threads.Count)" }
$all = Join-Path $work 'all.csv'
$first = $true
Get-ChildItem $work -Filter 'h*.csv' | ForEach-Object {
    $lines = Get-Content $_.FullName | Where-Object { $_ -notmatch '^# (self|label)' }
    if ($first) { $lines | Set-Content $all; $first = $false } else { $lines | Where-Object { $_ -notmatch '^run,' } | Add-Content $all }
}
$rows = Get-Content $all | Where-Object { $_ -notmatch '^#' } | ConvertFrom-Csv
"calls: $($rows.Count), ok: $(@($rows | Where-Object ok -eq '1').Count), failed: $(@($rows | Where-Object ok -ne '1').Count)"
$tot = $rows | Where-Object ok -eq '1' | ForEach-Object { [double]$_.total_ms } | Sort-Object
if ($tot.Count) { "total_ms (to bitmap): p50 {0:N1}, p95 {1:N1}, max {2:N1}" -f $tot[[int](($tot.Count - 1) * 0.5)], $tot[[int](($tot.Count - 1) * 0.95)], $tot[-1] }
& $TH --shutdown | Out-Null
Start-Sleep -Milliseconds 500
Get-Content "$out\shellhost.log" | Select-String 'peakLiveEngines|exit' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(160, $_.Line.Length)) }
"engine processes left running (MikePDF.Engine.exe on this machine, including other agents' own): $(@(Get-Process MikePDF.Engine -ErrorAction SilentlyContinue).Count)"
