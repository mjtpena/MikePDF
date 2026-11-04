<#
.SYNOPSIS
  Builds and runs the MikePDF fuzz targets (docs/fuzzing.md).
.DESCRIPTION
  One entry point for the local runs, the CI smoke job and the scheduled workflow.

  Targets:
    protocol-cpp      C++ IPC decoders (libFuzzer, MSVC /fsanitize=fuzzer,address)             tests/fuzz/protocol_fuzz.cpp
    engine-document   engine document open + render through the PDFium wrapper (libFuzzer+ASan) tests/fuzz/engine_document_fuzz.cpp
    protocol-cs       C# IPC decoders                                                            tests/fuzz/MikePDF.Protocol.Fuzz
    pdfwriter-parse   MikePDF.PdfWriter parser and validator                                     tests/fuzz/MikePDF.PdfWriter.Fuzz
    pdfwriter-append  MikePDF.PdfWriter incremental writer round trip
    shell-reply-cpp   the Explorer handler's reply decoder and checks (SHELL-18, libFuzzer+ASan)   tests/fuzz/shell_reply_fuzz.cpp
    shell-request-cs  ShellHost's request decoder and rules (SHELL-18)                             tests/fuzz/MikePDF.ShellHost.Fuzz
    pdfwriter-edit    MikePDF.PdfWriter stage 4 (annotation edits) and the copies on arbitrary files   tests/fuzz/MikePDF.PdfWriter.Fuzz
    pdfwriter-assemble  MikePDF.PdfWriter page assembler (extract, merge, duplicate) on arbitrary files
    journal-cs        the edit journal's reader and the recovery code behind it (save-pipeline 3.4)
    editblob-cs       the edit blob's reader and the appender on what it decodes (save-pipeline 11.3)

  The C# targets run in one of two modes (-CSharpMode, ADR-0017):
    coverage    (default) SharpFuzz-instrumented MikePDF.Protocol.dll / MikePDF.PdfWriter.dll, driven by MSVC's libFuzzer through the
                vendored libfuzzer-dotnet driver (tests/fuzz/libfuzzer-dotnet). Coverage-guided.
    mutational  the dependency-free fallback (tests/fuzz/Shared/MutationalFuzzer.cs): no coverage feedback, outcome-signature novelty only.
                Needs no C++ toolchain.

  Exit code: 0 no finding, 1 at least one finding (reproducers are in -FindingsRoot), 2 the run could not be set up.
  Writes <OutRoot>/summary.json and <OutRoot>/summary.md.
.EXAMPLE
  ./tests/fuzz/run-fuzz.ps1 -Target all -Seconds 60            # smoke: what CI does on every push
  ./tests/fuzz/run-fuzz.ps1 -Target protocol-cpp -Seconds 900  # the local 15-minute run
#>
[CmdletBinding()]
param(
    [ValidateSet('protocol-cpp', 'engine-document', 'protocol-cs', 'pdfwriter-parse', 'pdfwriter-append', 'shell-reply-cpp', 'shell-request-cs', 'pdfwriter-edit', 'pdfwriter-assemble', 'journal-cs', 'editblob-cs', 'all')]
    [string[]] $Target = @('all'),
    [int] $Seconds = 60,
    [string] $OutRoot,
    [string] $CorpusRoot,
    [string] $FindingsRoot,
    [switch] $SkipBuild,
    [int] $Rng = 0,
    # Parallel processes per target, sharing one corpus directory. libFuzzer's -fork mode does not exist on Windows; -jobs/-workers does.
    [ValidateRange(1, 64)] [int] $Jobs = 1,
    # How the C# targets are driven. `coverage` fails with exit code 2 when the driver or the instrumenter cannot be set up (it never falls back silently).
    [ValidateSet('coverage', 'mutational')] [string] $CSharpMode = 'coverage',
    # Instead of fuzzing: prove that the coverage-guided pipeline (driver, instrumented harness, libFuzzer) finds a planted exception, hang and
    # stack overflow and keeps a reproducer for each. Exit code 0 when all three are found.
    [switch] $SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutRoot) { $OutRoot = Join-Path $root 'build/out/fuzz' }
if (-not $CorpusRoot) { $CorpusRoot = Join-Path $OutRoot 'corpus' }
if (-not $FindingsRoot) { $FindingsRoot = Join-Path $OutRoot 'findings' }
$all = 'protocol-cpp', 'engine-document', 'protocol-cs', 'pdfwriter-parse', 'pdfwriter-append', 'shell-reply-cpp', 'shell-request-cs', 'pdfwriter-edit', 'pdfwriter-assemble', 'journal-cs', 'editblob-cs'
# One harness (MikePDF.PdfWriter.Fuzz) serves these; the first word it is started with picks the target.
$pdfwriterGroup = 'pdfwriter-parse', 'pdfwriter-append', 'pdfwriter-edit', 'pdfwriter-assemble', 'journal-cs', 'editblob-cs'
if ($Target -contains 'all') { $Target = $all }
if ($SelfTest) { $Target = @('protocol-cs'); $CSharpMode = 'coverage' }
New-Item -ItemType Directory -Force $OutRoot, $CorpusRoot, $FindingsRoot, (Join-Path $OutRoot 'logs') | Out-Null
# The fuzzers run with another working directory (one per target), so a relative root would point somewhere else for them.
$OutRoot = (Resolve-Path $OutRoot).Path; $CorpusRoot = (Resolve-Path $CorpusRoot).Path; $FindingsRoot = (Resolve-Path $FindingsRoot).Path

function Find-CMake {
    $onPath = Get-Command cmake -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -all -products * -prerelease -sort -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' | Select-Object -First 1
        if ($found) { return $found }
    }
    throw 'cmake not found'
}

# ---- build ---------------------------------------------------------------------------------------------------------------
$nativeBin = Join-Path $root 'build/out/fuzz-x64/fuzz/Release'
$csBin = @{
    'protocol-cs'      = Join-Path $root 'build/out/fuzz-bin/protocol'
    'pdfwriter-parse'  = Join-Path $root 'build/out/fuzz-bin/pdfwriter'
    'pdfwriter-append' = Join-Path $root 'build/out/fuzz-bin/pdfwriter'
    'pdfwriter-edit'   = Join-Path $root 'build/out/fuzz-bin/pdfwriter'
    'pdfwriter-assemble' = Join-Path $root 'build/out/fuzz-bin/pdfwriter'
    'journal-cs'       = Join-Path $root 'build/out/fuzz-bin/pdfwriter'
    'editblob-cs'      = Join-Path $root 'build/out/fuzz-bin/pdfwriter'
    'shell-request-cs' = Join-Path $root 'build/out/fuzz-bin/shellhost'
}

$driverExe = Join-Path $root 'build/out/fuzz-driver/driver/Release/libfuzzer_dotnet.exe'
$useCoverage = ($CSharpMode -eq 'coverage') -and [bool] ($Target | Where-Object { $_ -in 'protocol-cs', 'pdfwriter-parse', 'pdfwriter-append', 'pdfwriter-edit', 'pdfwriter-assemble', 'journal-cs', 'editblob-cs', 'shell-request-cs' })
# SharpFuzz.CommandLine 2.3.0 (MIT, tests/fuzz/.config/dotnet-tools.json): a tool-manifest version pins the version but not the bytes, so the
# package's SHA-512 is pinned here and checked after the restore.
$sharpFuzzCliSha512 = 'G/7VoIh0eqKYdGM9GZJqM+twCotc9T3WFl6QMaulYhzKNGwS90zdXx43JC3jYrwz/BTGVDv4Nu1VK5F+f12kzg=='

function Install-CoverageTools {
    Write-Host '==> libFuzzer driver for .NET (vendored libfuzzer-dotnet, MSVC libFuzzer) and the SharpFuzz instrumenter' -ForegroundColor Cyan
    $cmake = Find-CMake
    $driverBuild = Join-Path $root 'build/out/fuzz-driver'
    & $cmake -S (Join-Path $PSScriptRoot 'libfuzzer-dotnet') -B $driverBuild -A x64
    if ($LASTEXITCODE -ne 0) { Write-Host 'CMake configure of the libFuzzer driver failed' -ForegroundColor Red; exit 2 }
    & $cmake --build $driverBuild --config Release
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $driverExe)) { Write-Host 'building the libFuzzer driver failed (needs the "C++ AddressSanitizer" Visual Studio component, which carries MSVC libFuzzer)' -ForegroundColor Red; exit 2 }

    $manifest = Join-Path $PSScriptRoot '.config/dotnet-tools.json'
    dotnet tool restore --tool-manifest $manifest
    if ($LASTEXITCODE -ne 0) { Write-Host 'dotnet tool restore failed' -ForegroundColor Red; exit 2 }
    $packages = ((dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', '').Trim()
    $sha = (Get-Content (Join-Path $packages 'sharpfuzz.commandline/2.3.0/sharpfuzz.commandline.2.3.0.nupkg.sha512') -Raw).Trim()
    if ($sha -ne $sharpFuzzCliSha512) { Write-Host "SharpFuzz.CommandLine 2.3.0 does not match the pinned SHA-512 ($sha)" -ForegroundColor Red; exit 2 }
}

# Copies a built harness folder to <folder>-cov and instruments the named MikePDF assemblies there (never the shipped ones, never the harness).
function Install-Instrumented([string] $plain, [string[]] $assemblies) {
    $cov = "$plain-cov"
    if (Test-Path $cov) { Remove-Item $cov -Recurse -Force }
    Copy-Item $plain $cov -Recurse
    Push-Location $PSScriptRoot # the tool manifest is found from here
    try {
        foreach ($name in $assemblies) {
            dotnet sharpfuzz (Join-Path $cov $name)
            if ($LASTEXITCODE -ne 0) { Write-Host "instrumenting $name failed" -ForegroundColor Red; exit 2 }
        }
    } finally { Pop-Location }
}

if (-not $SkipBuild) {
    if ($Target | Where-Object { $_ -in 'protocol-cpp', 'engine-document', 'shell-reply-cpp' }) {
        $pdfium = & (Join-Path $root 'build/fetch-pdfium.ps1') -Arch x64 | Select-Object -Last 1
        $cmake = Find-CMake
        $buildDir = Join-Path $root 'build/out/fuzz-x64'
        Write-Host '==> native fuzz targets (MSVC libFuzzer + AddressSanitizer)' -ForegroundColor Cyan
        & $cmake -S $root -B $buildDir -A x64 "-DMIKEPDF_PDFIUM_DIR=$pdfium" -DMIKEPDF_BUILD_TESTS=OFF -DMIKEPDF_BUILD_FUZZERS=ON -DMIKEPDF_REQUIRE_SPECTRE=OFF
        if ($LASTEXITCODE -ne 0) { Write-Host 'CMake configure failed' -ForegroundColor Red; exit 2 }
        & $cmake --build $buildDir --config Release --target fuzz_protocol fuzz_engine_document fuzz_shell_reply --parallel
        if ($LASTEXITCODE -ne 0) { Write-Host 'native fuzz build failed' -ForegroundColor Red; exit 2 }
    }
    if ($Target | Where-Object { $_ -eq 'protocol-cs' }) {
        Write-Host '==> C# protocol fuzz harness' -ForegroundColor Cyan
        dotnet build (Join-Path $root 'tests/fuzz/MikePDF.Protocol.Fuzz') -c Release -o $csBin['protocol-cs']
        if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit 2 }
    }
    if ($Target | Where-Object { $_ -eq 'shell-request-cs' }) {
        Write-Host '==> C# ShellHost fuzz harness' -ForegroundColor Cyan
        dotnet build (Join-Path $root 'tests/fuzz/MikePDF.ShellHost.Fuzz') -c Release -o $csBin['shell-request-cs']
        if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit 2 }
    }
    if ($Target | Where-Object { $_ -in $pdfwriterGroup }) {
        Write-Host '==> C# PdfWriter fuzz harness' -ForegroundColor Cyan
        dotnet build (Join-Path $root 'tests/fuzz/MikePDF.PdfWriter.Fuzz') -c Release -o $csBin['pdfwriter-parse']
        if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit 2 }
    }
    if ($useCoverage) {
        Install-CoverageTools
        if ($Target -contains 'protocol-cs') { Install-Instrumented $csBin['protocol-cs'] @('MikePDF.Protocol.dll') }
        if ($Target -contains 'shell-request-cs') { Install-Instrumented $csBin['shell-request-cs'] @('MikePDF.Protocol.dll', 'MikePDF.ShellHost.dll') }
        if ($Target | Where-Object { $_ -in $pdfwriterGroup }) { Install-Instrumented $csBin['pdfwriter-parse'] @('MikePDF.PdfWriter.dll', 'MikePDF.Core.dll', 'MikePDF.Protocol.dll') }
    }
}

# ---- seeds ---------------------------------------------------------------------------------------------------------------
# Protocol: every golden frame, plus its body (the decoders take both). PDFs: every fixture the repository already has.
$seedRoot = Join-Path $OutRoot 'seeds'
$protocolSeeds = Join-Path $seedRoot 'protocol'
$pdfSeeds = Join-Path $seedRoot 'pdf'
New-Item -ItemType Directory -Force $protocolSeeds, $pdfSeeds | Out-Null
foreach ($file in Get-ChildItem (Join-Path $root 'tests/MikePDF.Protocol.Tests/golden') -Filter *.bin) {
    Copy-Item $file.FullName (Join-Path $protocolSeeds $file.Name) -Force
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -gt 4) { [IO.File]::WriteAllBytes((Join-Path $protocolSeeds ('body-' + $file.Name)), $bytes[4..($bytes.Length - 1)]) }
}
foreach ($dir in 'tests/MikePDF.PdfWriter.Tests/Fixtures/pdfium', 'tests/MikePDF.PdfWriter.Tests/Fixtures/assembly', 'tests/MikePDF.Integration.Tests/Fixtures') {
    foreach ($file in Get-ChildItem (Join-Path $root $dir) -Filter *.pdf) {
        Copy-Item $file.FullName (Join-Path $pdfSeeds $file.Name) -Force
    }
}
# The shell targets (SHELL-18): seeds made by seeds/make_shell_seeds.py. The request seeds are frames; the decoder also takes a body, so those are seeded too.
$shellReplySeeds = Join-Path $seedRoot 'shell-reply'
$shellRequestSeeds = Join-Path $seedRoot 'shell-request'
New-Item -ItemType Directory -Force $shellReplySeeds, $shellRequestSeeds | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'seeds/shell-reply/*.bin') $shellReplySeeds -Force
foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'seeds/shell-request') -Filter *.bin) {
    Copy-Item $file.FullName (Join-Path $shellRequestSeeds $file.Name) -Force
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -gt 4) { [IO.File]::WriteAllBytes((Join-Path $shellRequestSeeds ('body-' + $file.Name)), $bytes[4..($bytes.Length - 1)]) }
}
# The save targets' seeds were made by `MikePDF.PdfWriter.Fuzz <journal|editblob> --seeds-out <dir>` and are committed.
$journalSeeds = Join-Path $seedRoot 'journal'
$editblobSeeds = Join-Path $seedRoot 'editblob'
New-Item -ItemType Directory -Force $journalSeeds, $editblobSeeds | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'seeds/journal/*.jsonl') $journalSeeds -Force
Copy-Item (Join-Path $PSScriptRoot 'seeds/editblob/*.bin') $editblobSeeds -Force
$dictionaries = Join-Path $PSScriptRoot 'dictionaries'

# ---- run -----------------------------------------------------------------------------------------------------------------
function Get-CorpusFiles([string] $dir) { if (Test-Path $dir) { @(Get-ChildItem $dir -File -Recurse).Count } else { 0 } }

# `driverArgs` come first: for the .NET targets that is the driver's --target_path (the instrumented harness it starts as a child process).
# The harness path is quoted for the driver's CreateProcess, hence the \" around it (the C runtime strips the outer quotes and keeps the inner ones).
function Invoke-Native([string] $name, [string] $exe, [string] $seeds, [string] $dict, [int] $maxLen,
                       [string] $kind = 'libFuzzer + AddressSanitizer (coverage-guided)', [string[]] $driverArgs = @(), [int] $timeout = 30) {
    $corpus = Join-Path $CorpusRoot $name
    $findings = Join-Path $FindingsRoot $name
    $jobDir = Join-Path $OutRoot "logs/$name-jobs"
    New-Item -ItemType Directory -Force $corpus, $findings, $jobDir | Out-Null
    $log = Join-Path $OutRoot "logs/$name.log"
    $argv = $driverArgs + @("-max_len=$maxLen", "-max_total_time=$Seconds", "-timeout=$timeout", '-rss_limit_mb=4096', "-dict=$dict",
              ('-artifact_prefix=' + $findings.Replace('\', '/') + '/'), '-print_final_stats=1')
    if ($driverArgs.Count -eq 0) { $argv += '-use_value_profile=1' } # value profile needs the compare tracing that only the compiled targets have
    if ($Rng -ne 0) { $argv += "-seed=$Rng" }
    if ($Jobs -gt 1) { $argv += "-jobs=$Jobs", "-workers=$Jobs" }
    $argv += $corpus, $seeds
    Write-Host "==> $name for ${Seconds}s ($Jobs process$(if ($Jobs -gt 1) { 'es' }))" -ForegroundColor Cyan
    $p = Start-Process -FilePath $exe -ArgumentList $argv -NoNewWindow -PassThru -WorkingDirectory $jobDir -RedirectStandardError $log -RedirectStandardOutput (Join-Path $OutRoot "logs/$name.out.log")
    $null = $p.Handle # keeps the exit code readable after the process ends
    # -max_total_time ends a healthy run. If a fuzzer is still alive 10 minutes after that (for example -jobs workers that die at start and are restarted
    # for ever), kill the tree and fail the run instead of hanging the job until the CI limit.
    $exitCode = $null
    if (-not $p.WaitForExit(($Seconds + 600) * 1000)) {
        Write-Host "$name did not stop $($Seconds + 600) s after it started; killing it" -ForegroundColor Red
        & taskkill.exe /T /F /PID $p.Id | Out-Null
        $exitCode = 124
    } else { $exitCode = $p.ExitCode }
    # One log per process: this one when there is a single process, fuzz-<n>.log files under -jobs.
    $logs = if ($Jobs -gt 1) { @(Get-ChildItem $jobDir -Filter 'fuzz-*.log' | ForEach-Object FullName) } else { @($log) }
    $ft = 0; $execs = [int64] 0; $counters = $null
    foreach ($one in $logs) {
        $text = Get-Content $one -Raw
        # MSVC's libFuzzer prints `ft:` (features: instrumented edges x hit-count buckets, plus value-profile features) but no `cov:` line,
        # because MSVC emits no PC table. So there is no covered-edge percentage for the native targets, only the feature count.
        $stat = [regex]::Matches($text, '#(\d+)\s+(?:DONE|REDUCE|NEW|pulse|INITED|RELOAD)\s+(?:cov: (\d+) )?ft: (\d+) corp: (\d+)/') | Select-Object -Last 1
        if ($stat) { $ft = [Math]::Max($ft, [int] $stat.Groups[3].Value) }
        $units = [regex]::Match($text, 'stat::number_of_executed_units: (\d+)')
        if ($units.Success) { $execs += [int64] $units.Groups[1].Value }
        if (-not $counters -and $driverArgs.Count -eq 0) { $counters = [regex]::Match($text, '(\d+) inline 8-bit counters') } # the .NET driver has 64 counters of its own, which say nothing
    }
    $found = @(Get-ChildItem $findings -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(crash|timeout|oom|leak|slow-unit)-' })
    [pscustomobject]@{
        target = $name; kind = $kind; seconds = $Seconds; processes = $Jobs; exit = $exitCode
        executions = $execs; features = $ft; instrumentedEdges = if ($counters -and $counters.Success) { [int] $counters.Groups[1].Value } else { $null }
        corpusFiles = Get-CorpusFiles $corpus; findings = $found.Count; findingFiles = @($found | ForEach-Object Name)
    }
}

function Invoke-Managed([string] $name, [string] $dll, [string[]] $leading, [string] $seeds, [string] $dict, [int] $maxLen) {
    $corpus = Join-Path $CorpusRoot $name
    $findings = Join-Path $FindingsRoot $name
    New-Item -ItemType Directory -Force $corpus, $findings | Out-Null
    Write-Host "==> $name for ${Seconds}s ($Jobs process$(if ($Jobs -gt 1) { 'es' }))" -ForegroundColor Cyan
    $running = @()
    for ($i = 0; $i -lt $Jobs; $i++) {
        $argv = @($dll) + $leading + @('--mutate', $Seconds, '--corpus', $corpus, '--seeds', $seeds, '--dict', $dict, '--findings', $findings, '--max-len', $maxLen, '--timeout-ms', 20000)
        $argv += @('--rng', $(if ($Rng -ne 0) { $Rng + $i } else { [Environment]::TickCount + 7919 * $i }))
        $out = Join-Path $OutRoot "logs/$name.$i.log"
        $running += [pscustomobject]@{
            Out = $out
            Process = Start-Process -FilePath dotnet -ArgumentList $argv -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError (Join-Path $OutRoot "logs/$name.$i.err.log")
        }
    }
    $running | ForEach-Object { $_.Process.WaitForExit() }
    $execs = [int64] 0
    foreach ($job in $running) {
        foreach ($m in [regex]::Matches((Get-Content $job.Out -Raw), 'worker done: (\d+) execs')) { $execs += [int64] $m.Groups[1].Value }
    }
    $found = @(Get-ChildItem $findings -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(crash|timeout)-[0-9a-f]+$' })
    [pscustomobject]@{
        target = $name; kind = 'mutational fallback, NO coverage feedback (outcome-signature novelty only)'; seconds = $Seconds; processes = $Jobs
        exit = ($running | ForEach-Object { $_.Process.ExitCode } | Measure-Object -Maximum).Maximum
        executions = $execs; features = $null; instrumentedEdges = $null
        corpusFiles = Get-CorpusFiles $corpus; findings = $found.Count; findingFiles = @($found | ForEach-Object Name)
    }
}

# The coverage-guided C# targets: libFuzzer (through the vendored libfuzzer-dotnet driver) runs the SharpFuzz-instrumented harness as a child process.
function Invoke-Guided([string] $name, [string] $harnessExe, [string[]] $leading, [string] $seeds, [string] $dict, [int] $maxLen) {
    if (-not (Test-Path $driverExe)) { Write-Host "the libFuzzer driver is not built ($driverExe); run without -SkipBuild" -ForegroundColor Red; exit 2 }
    if (-not (Test-Path $harnessExe)) { Write-Host "the instrumented harness is not built ($harnessExe); run without -SkipBuild" -ForegroundColor Red; exit 2 }
    # libFuzzer's -jobs mode starts its workers by joining its own arguments back into a command line WITHOUT re-quoting them, so every argument
    # here must survive that: no spaces, no quotes. Hence --target_path (the harness executable) and --target_arg (one word: `parse`, `append`, or none;
    # a harness started with exactly that and nothing else runs in --libfuzzer mode). A path with spaces works for one process only.
    if ($harnessExe -match '\s') {
        if ($Jobs -gt 1) { Write-Host "-Jobs $Jobs needs a repository path without spaces for the coverage-guided C# targets ($harnessExe)" -ForegroundColor Red; exit 2 }
        $child = @('"--target_path=\"' + $harnessExe + '\"' + $(if ($leading.Count) { ' ' + $leading[0] }) + '"')
    } else {
        $child = @("--target_path=$harnessExe") + $(if ($leading.Count) { "--target_arg=$($leading[0])" })
    }
    # A managed out-of-memory (a finding) must end in an exception, not in the machine's memory: cap the child's GC heap at 2 GiB (the value is hex).
    $previous = $env:DOTNET_GCHeapHardLimit
    $env:DOTNET_GCHeapHardLimit = '80000000'
    try {
        Invoke-Native $name $driverExe $seeds $dict $maxLen 'libFuzzer + SharpFuzz edge coverage (coverage-guided)' $child 20
    } finally { $env:DOTNET_GCHeapHardLimit = $previous }
}

function Invoke-CSharp([string] $name, [string] $harness, [string[]] $leading, [string] $seeds, [string] $dict, [int] $maxLen) {
    if ($CSharpMode -eq 'coverage') {
        Invoke-Guided $name ((Join-Path ($csBin[$name] + '-cov') $harness) + '.exe') $leading $seeds $dict $maxLen
    } else {
        Invoke-Managed $name ((Join-Path $csBin[$name] $harness) + '.dll') $leading $seeds $dict $maxLen
    }
}

if ($SelfTest) {
    if (-not (Test-Path $driverExe)) { Write-Host "the libFuzzer driver is not built ($driverExe); run without -SkipBuild" -ForegroundColor Red; exit 2 }
    $harness = (Join-Path ($csBin['protocol-cs'] + '-cov') 'MikePDF.Protocol.Fuzz') + '.exe'
    $expected = [ordered]@{ throw = 'crash-'; hang = 'timeout-'; overflow = 'crash-' }
    $failed = 0
    foreach ($kind in $expected.Keys) {
        $dir = Join-Path $OutRoot "selftest/$kind"
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        $seed = Join-Path $dir 'seed'; $found = Join-Path $dir 'findings'
        New-Item -ItemType Directory -Force $seed, $found | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $seed 'fzz'), [byte[]] (70, 90, 90)) # "FZZ": the planted failure fires on an input that starts with it
        $argv = @(('"--target_path=\"' + $harness + '\" --libfuzzer --selftest ' + $kind + '"'), '-max_total_time=30', '-timeout=3', '-max_len=64',
                  ('-artifact_prefix=' + $found.Replace('\', '/') + '/'), $seed)
        $log = Join-Path $dir 'run.log'
        $p = Start-Process -FilePath $driverExe -ArgumentList $argv -NoNewWindow -Wait -PassThru -RedirectStandardError $log -RedirectStandardOutput (Join-Path $dir 'run.out')
        $artifact = @(Get-ChildItem $found -File | Where-Object { $_.Name -like "$($expected[$kind])*" })
        $ok = ($p.ExitCode -ne 0) -and ($artifact.Count -eq 1) -and ($artifact[0].Length -ge 3)
        if ($ok -and $kind -eq 'throw') { $ok = (Get-Content $log -Raw) -match 'the planted failure was reached' }
        Write-Host ("selftest {0,-8} exit {1,-3} reproducer {2}: {3}" -f $kind, $p.ExitCode, $(if ($artifact) { $artifact[0].Name } else { 'none' }), $(if ($ok) { 'found' } else { 'NOT FOUND' }))
        if (-not $ok) { $failed++ }
    }
    if ($failed) { Write-Host "$failed planted failure(s) were not found: the coverage-guided pipeline cannot be trusted" -ForegroundColor Red; exit 1 }
    exit 0
}

$results = @()
foreach ($t in $Target) {
    switch ($t) {
        'protocol-cpp'     { $results += Invoke-Native $t (Join-Path $nativeBin 'fuzz_protocol.exe') $protocolSeeds (Join-Path $dictionaries 'protocol.dict') 65540 }
        'engine-document'  { $results += Invoke-Native $t (Join-Path $nativeBin 'fuzz_engine_document.exe') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
        'protocol-cs'      { $results += Invoke-CSharp $t 'MikePDF.Protocol.Fuzz' @() $protocolSeeds (Join-Path $dictionaries 'protocol.dict') 65540 }
        'pdfwriter-parse'  { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('parse') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
        'pdfwriter-append' { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('append') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
        'pdfwriter-edit'   { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('edit') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
        'pdfwriter-assemble' { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('assemble') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
        'journal-cs'       { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('journal') $journalSeeds (Join-Path $dictionaries 'journal.dict') 65536 }
        'editblob-cs'      { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('editblob') $editblobSeeds (Join-Path $dictionaries 'protocol.dict') 65536 }
        'shell-reply-cpp'  { $results += Invoke-Native $t (Join-Path $nativeBin 'fuzz_shell_reply.exe') $shellReplySeeds (Join-Path $dictionaries 'protocol.dict') 65548 }
        'shell-request-cs' { $results += Invoke-CSharp $t 'MikePDF.ShellHost.Fuzz' @() $shellRequestSeeds (Join-Path $dictionaries 'protocol.dict') 65540 }
    }
}

$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutRoot 'summary.json')
$md = @('| Target | Kind | Seconds x processes | Executions | libFuzzer features (instrumented edges) | Corpus files | Findings |', '|---|---|---|---|---|---|---|')
foreach ($r in $results) {
    $edges = if ($null -eq $r.features) { 'n/a' } elseif ($null -eq $r.instrumentedEdges) { "$($r.features)" } else { "$($r.features) ($($r.instrumentedEdges))" }
    $md += "| $($r.target) | $($r.kind) | $($r.seconds) x $($r.processes) | $($r.executions) | $edges | $($r.corpusFiles) | $($r.findings) |"
}
$md | Set-Content (Join-Path $OutRoot 'summary.md')
$md | ForEach-Object { Write-Host $_ }

$bad = @($results | Where-Object { $_.findings -gt 0 -or ($_.exit -ne 0 -and $_.exit -ne $null) })
if ($bad.Count -gt 0) {
    Write-Host "FINDINGS in: $(($bad | ForEach-Object target) -join ', '). Reproducers are under $FindingsRoot" -ForegroundColor Red
    exit 1
}
exit 0
