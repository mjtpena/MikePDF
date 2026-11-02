<#
.SYNOPSIS
  S6b fault behaviour: a page with a 14000 x 14000 pixel image (a 2.3 MB file; PDFium decodes it in bands, peak about 24 MB) with different engine memory limits (the job's per-process commit limit),
  and a handler that gives up at a short deadline while the engine is still busy: is the engine killed at once, and does the next request work?
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'out'
$log = Join-Path $out 'faults.txt'
$all = & {
    function Fresh($mode, $extra) { . (Join-Path $here 'env.ps1') -Mode $mode -Extra $extra; & $TH --shutdown | Out-Null; Remove-Item $env:S6B_LOG, "$out\shellhost.log" -ErrorAction SilentlyContinue; $env:S6B_PEAK = '1' }
    foreach ($case in @(@('heavy14k.pdf', 256), @('heavy14k.pdf', 16))) {
        $pdf = $case[0]; $mem = $case[1]
        "`n===== $pdf with the engine memory limit at $mem MB (handler budget 30 s so that only the limit decides)"
        Fresh 'fresh' ''
        . (Join-Path $here 'env.ps1') -Mode fresh -MemMb $mem
        $env:S6B_BUDGET_MS = '30000'; $env:S6B_PEAK = '1'
        & $TH --dll $Dll --pdf "$Pdfs\$pdf" --cx 256 --runs 3
        Get-Content "$out\shellhost.log" | Select-String 'failed|gone|killed' | Select-Object -First 3 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) }
    }

    "`n===== handler gives up after 120 ms per call while the engine renders heavy14k.pdf (about 200 ms); is the engine killed, does the next request work?"
    Fresh 'fresh' ''
    $env:S6B_BUDGET_MS = '30000'
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --cx 256 --runs 1 | Out-Null          # ShellHost up
    $env:S6B_BUDGET_MS = '120'
    $t = [Diagnostics.Stopwatch]::StartNew()
    & $TH --dll $Dll --pdf "$Pdfs\heavy14k.pdf" --cx 256 --runs 1
    "handler call returned after $($t.ElapsedMilliseconds) ms (includes test host start)"
    $pidLine = Get-Content "$out\shellhost.log" | Select-String 'session open|client gone' | Select-Object -Last 3
    $pidLine | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) }
    $enginePid = [regex]::Matches((($pidLine | ForEach-Object { $_.Line }) -join "`n"), 'engine (?:pid )?(\d+)')[-1].Groups[1].Value
    "engine pid from the ShellHost log: $enginePid"
    $gone = $false
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-Process -Id $enginePid -ErrorAction SilentlyContinue)) { $gone = $true; "engine $enginePid gone after about $($i * 50) ms of polling (50 ms steps, after the handler returned)"; break }
        Start-Sleep -Milliseconds 50
    }
    if (-not $gone) { "engine $enginePid STILL RUNNING after 2 s" }
    $env:S6B_BUDGET_MS = '30000'
    "next request:"
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --cx 256 --runs 2
    & $TH --shutdown | Out-Null
}
$all | Tee-Object -FilePath $log
