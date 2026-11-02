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

  The three C# targets run in one of two modes (-CSharpMode, ADR-0017):
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
    [ValidateSet('protocol-cpp', 'engine-document', 'protocol-cs', 'pdfwriter-parse', 'pdfwriter-append', 'all')]
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
    [ValidateSet('coverage', 'mutational')] [string] $CSharpMode = 'coverage'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutRoot) { $OutRoot = Join-Path $root 'build/out/fuzz' }
if (-not $CorpusRoot) { $CorpusRoot = Join-Path $OutRoot 'corpus' }
if (-not $FindingsRoot) { $FindingsRoot = Join-Path $OutRoot 'findings' }
$all = 'protocol-cpp', 'engine-document', 'protocol-cs', 'pdfwriter-parse', 'pdfwriter-append'
if ($Target -contains 'all') { $Target = $all }
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
}

$driverExe = Join-Path $root 'build/out/fuzz-driver/driver/Release/libfuzzer_dotnet.exe'
$useCoverage = ($CSharpMode -eq 'coverage') -and [bool] ($Target | Where-Object { $_ -in 'protocol-cs', 'pdfwriter-parse', 'pdfwriter-append' })
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
    if ($Target | Where-Object { $_ -in 'protocol-cpp', 'engine-document' }) {
        $pdfium = & (Join-Path $root 'build/fetch-pdfium.ps1') -Arch x64 | Select-Object -Last 1
        $cmake = Find-CMake
        $buildDir = Join-Path $root 'build/out/fuzz-x64'
        Write-Host '==> native fuzz targets (MSVC libFuzzer + AddressSanitizer)' -ForegroundColor Cyan
        & $cmake -S $root -B $buildDir -A x64 "-DMIKEPDF_PDFIUM_DIR=$pdfium" -DMIKEPDF_BUILD_TESTS=OFF -DMIKEPDF_BUILD_FUZZERS=ON -DMIKEPDF_REQUIRE_SPECTRE=OFF
        if ($LASTEXITCODE -ne 0) { Write-Host 'CMake configure failed' -ForegroundColor Red; exit 2 }
        & $cmake --build $buildDir --config Release --target fuzz_protocol fuzz_engine_document --parallel
        if ($LASTEXITCODE -ne 0) { Write-Host 'native fuzz build failed' -ForegroundColor Red; exit 2 }
    }
    if ($Target | Where-Object { $_ -eq 'protocol-cs' }) {
        Write-Host '==> C# protocol fuzz harness' -ForegroundColor Cyan
        dotnet build (Join-Path $root 'tests/fuzz/MikePDF.Protocol.Fuzz') -c Release -o $csBin['protocol-cs']
        if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit 2 }
    }
    if ($Target | Where-Object { $_ -like 'pdfwriter-*' }) {
        Write-Host '==> C# PdfWriter fuzz harness' -ForegroundColor Cyan
        dotnet build (Join-Path $root 'tests/fuzz/MikePDF.PdfWriter.Fuzz') -c Release -o $csBin['pdfwriter-parse']
        if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit 2 }
    }
    if ($useCoverage) {
        Install-CoverageTools
        if ($Target -contains 'protocol-cs') { Install-Instrumented $csBin['protocol-cs'] @('MikePDF.Protocol.dll') }
        if ($Target | Where-Object { $_ -like 'pdfwriter-*' }) { Install-Instrumented $csBin['pdfwriter-parse'] @('MikePDF.PdfWriter.dll') }
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
foreach ($dir in 'tests/MikePDF.PdfWriter.Tests/Fixtures/pdfium', 'tests/MikePDF.Integration.Tests/Fixtures') {
    foreach ($file in Get-ChildItem (Join-Path $root $dir) -Filter *.pdf) {
        Copy-Item $file.FullName (Join-Path $pdfSeeds $file.Name) -Force
    }
}
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
    $p = Start-Process -FilePath $exe -ArgumentList $argv -NoNewWindow -Wait -PassThru -WorkingDirectory $jobDir -RedirectStandardError $log -RedirectStandardOutput (Join-Path $OutRoot "logs/$name.out.log")
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
        target = $name; kind = $kind; seconds = $Seconds; processes = $Jobs; exit = $p.ExitCode
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
    $child = '"--target_path=\"' + $harnessExe + '\"' + (($leading + '--libfuzzer') | ForEach-Object { " $_" }) + '"'
    # A managed out-of-memory (a finding) must end in an exception, not in the machine's memory: cap the child's GC heap at 2 GiB (the value is hex).
    $previous = $env:DOTNET_GCHeapHardLimit
    $env:DOTNET_GCHeapHardLimit = '80000000'
    try {
        Invoke-Native $name $driverExe $seeds $dict $maxLen 'libFuzzer + SharpFuzz edge coverage (coverage-guided)' @($child) 20
    } finally { $env:DOTNET_GCHeapHardLimit = $previous }
}

function Invoke-CSharp([string] $name, [string] $harness, [string[]] $leading, [string] $seeds, [string] $dict, [int] $maxLen) {
    if ($CSharpMode -eq 'coverage') {
        Invoke-Guided $name ((Join-Path ($csBin[$name] + '-cov') $harness) + '.exe') $leading $seeds $dict $maxLen
    } else {
        Invoke-Managed $name (Join-Path $csBin[$name] $harness) $leading $seeds $dict $maxLen
    }
}

$results = @()
foreach ($t in $Target) {
    switch ($t) {
        'protocol-cpp'     { $results += Invoke-Native $t (Join-Path $nativeBin 'fuzz_protocol.exe') $protocolSeeds (Join-Path $dictionaries 'protocol.dict') 65540 }
        'engine-document'  { $results += Invoke-Native $t (Join-Path $nativeBin 'fuzz_engine_document.exe') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
        'protocol-cs'      { $results += Invoke-CSharp $t 'MikePDF.Protocol.Fuzz' @() $protocolSeeds (Join-Path $dictionaries 'protocol.dict') 65540 }
        'pdfwriter-parse'  { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('parse') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
        'pdfwriter-append' { $results += Invoke-CSharp $t 'MikePDF.PdfWriter.Fuzz' @('append') $pdfSeeds (Join-Path $dictionaries 'pdf.dict') 131072 }
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
