<#
.SYNOPSIS
  S6b: what a LOW-integrity handler host (prevhost runs stream-initialised preview handlers at low IL by default) can and cannot do against ShellHost.
  Starts the test host with a low-integrity copy of its own token (a token DuplicateTokenEx + SetTokenInformation; no system setting changes).
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'out'
function Section($t) { "`n===== $t" }
$log = Join-Path $out 'lowil.txt'
$all = & {
    . (Join-Path $here 'env.ps1') -Mode fresh
    $env:S6B_BUDGET_MS = '15000'
    function Reset { & $TH --shutdown | Out-Null; Remove-Item $env:S6B_LOG, "$out\shellhost.log" -ErrorAction SilentlyContinue }

    Section '1. ShellHost running at medium IL with the low-integrity pipe label (the proposed design); client at LOW'
    Reset
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --cx 256 --runs 1 *> $null          # starts ShellHost from a medium client
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --cx 256 --runs 3 --low-il
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --preview --runs 3 --low-il

    Section '2. Same, but ShellHost started with --no-low-il (no mandatory label on the pipe, default medium label); client at LOW'
    Reset
    . (Join-Path $here 'env.ps1') -Mode fresh -Extra '--no-low-il'
    $env:S6B_BUDGET_MS = '4000'
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --cx 256 --runs 1 *> $null
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --cx 256 --runs 2 --low-il
    "handler.log tail:"; Get-Content $env:S6B_LOG -ErrorAction SilentlyContinue | Select-Object -Last 4 | ForEach-Object { $_.Substring(0, [Math]::Min(250, $_.Length)) }

    Section '3. ShellHost NOT running; the LOW-integrity client has to launch it on demand'
    Reset
    . (Join-Path $here 'env.ps1') -Mode fresh
    $env:S6B_BUDGET_MS = '15000'
    & $TH --dll $Dll --pdf "$Pdfs\text1.pdf" --cx 256 --runs 1 --low-il
    "shellhost.log (note the integrity of the process that was started):"
    Get-Content "$out\shellhost.log" -ErrorAction SilentlyContinue | Select-Object -First 6 | ForEach-Object { $_.Substring(0, [Math]::Min(250, $_.Length)) }
    $p = Get-Process S6bShellHost -ErrorAction SilentlyContinue
    if ($p) { "S6bShellHost still running: pid $($p.Id)" } else { 'no S6bShellHost process is running' }
    Reset
}
$all | Tee-Object -FilePath $log
