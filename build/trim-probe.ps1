<#
.SYNOPSIS
  Publishes tools/MikePDF.TrimProbe the way build/package.ps1 publishes the app (self-contained, trimmed, ReadyToRun) and runs it: MikePDF's non-UI code, in its shipped form.
.DESCRIPTION
  A trimmed build can fail at run time where an unpublished one does not (reflection, serialisation, COM and P/Invoke marshalling that the trimmer cannot see). The probe runs the
  settings and store JSON, the session file, edit commands, the window-less WinRT calls the app makes (through C#/WinRT and the trimmed Windows SDK projection) and a sandboxed
  engine session through the Broker and the MessagePack protocol, and exits non-zero on any failure.
  It needs the native engine (./build.ps1 -SkipManaged) for the engine check. It starts no window, installs nothing and registers nothing; the engine session creates the same
  per-user AppContainer profile the integration tests do.
  -Untrimmed publishes the same probe without trimming, for the startup comparison in docs/perf/install-size.md (-Runs times each, the median reported).
.EXAMPLE
  ./build/trim-probe.ps1
  ./build/trim-probe.ps1 -Compare -Runs 15
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
    [switch] $Untrimmed,
    [switch] $NoReadyToRun,
    [switch] $Compare,
    [int] $Runs = 10,
    [string] $OutDir
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'build/out/trim-probe' }
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
if ($Arch -ne $hostArch) { throw "The probe runs on the host architecture ($hostArch); $Arch binaries cannot run here." }
$engineDir = Join-Path $root "build/out/cmake-$Arch/bin/Release"
$pdf = Join-Path $root 'tests/MikePDF.Integration.Tests/Fixtures/rectangles.pdf'
$haveEngine = (Test-Path (Join-Path $engineDir 'MikePDF.Engine.exe')) -and (Test-Path (Join-Path $engineDir 'pdfium.dll'))
if (-not $haveEngine) { Write-Warning "No engine in $engineDir (run ./build.ps1 -SkipManaged -Arch $Arch): the engine check will be skipped." }

function Publish-Probe([string] $Name, [bool] $Trim) {
    $out = Join-Path $OutDir $Name
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    $publishArgs = @('publish', (Join-Path $root 'tools/MikePDF.TrimProbe/MikePDF.TrimProbe.csproj'), '-c', 'Release', '-r', "win-$Arch", '--self-contained', 'true',
        "-p:MikePDFArch=$Arch", "-p:PublishTrimmed=$($Trim.ToString().ToLowerInvariant())", "-p:PublishReadyToRun=$((-not $NoReadyToRun).ToString().ToLowerInvariant())", '-o', $out, '-nologo', '-v', 'q')
    dotnet @publishArgs 2>&1 | Tee-Object -Variable publishLog | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the probe ($Name) failed" }
    # The same rule as build/package.ps1: the two known library summaries (see MikePDF.App.csproj) are warnings, any other assembly's is a failure.
    $new = @($publishLog | ForEach-Object { "$_" } | Select-String -Pattern "IL2104: Assembly '([^']+)'" | ForEach-Object { $_.Matches[0].Groups[1].Value } | Where-Object { $_ -notin 'WinRT.Runtime', 'Microsoft.Windows.SDK.NET' } | Select-Object -Unique)
    if ($new.Count -gt 0) { throw "The trimmer reports warnings for assemblies that were clean before: $($new -join ', ')." }
    $size = (Get-ChildItem $out -Recurse -File | Where-Object { $_.Extension -notin '.pdb', '.xml' } | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Published {0}: {1:N1} MiB" -f $Name, $size)
    return [string] (Join-Path $out 'mikepdf-trimprobe.exe')
}

function Invoke-Probe([string] $Exe, [bool] $Trimmed) {
    $probeArgs = @(if ($Trimmed) { "--expect-trimmed"; "1" }) + @("--pdf", $pdf) + $(if ($haveEngine) { @("--engine-dir", $engineDir) } else { @() })
    & $Exe @probeArgs | Out-Host
    return [int] $LASTEXITCODE
}

$trimmedExe = Publish-Probe 'trimmed' (-not $Untrimmed)
Write-Host '==> Probe (trimmed)' -ForegroundColor Cyan
$code = Invoke-Probe $trimmedExe (-not $Untrimmed)
if ($code -ne 0) { throw "The trimmed probe failed ($code check(s))." }

if ($Compare) {
    $plainExe = Publish-Probe 'untrimmed' $false
    Write-Host '==> Probe (untrimmed, for the startup comparison)' -ForegroundColor Cyan
    if ((Invoke-Probe $plainExe $false) -ne 0) { throw 'The untrimmed probe failed.' }
    function Measure-Wall([string] $Exe, [string[]] $ExeArgs) {
        $times = foreach ($i in 1..$Runs) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            & $Exe @ExeArgs | Out-Null
            $sw.Stop()
            $sw.Elapsed.TotalMilliseconds
        }
        $sorted = @($times | Sort-Object)
        [pscustomobject]@{ MedianMs = [math]::Round($sorted[[int][math]::Floor($sorted.Count / 2)]); MinMs = [math]::Round($sorted[0]); MaxMs = [math]::Round($sorted[-1]) }
    }
    # Both builds, alternating, so that a busy minute hits both. The no-argument run is runtime start plus the JSON checks; the engine run adds the sandbox and one tile.
    foreach ($mode in @(@{ Name = 'process start + JSON checks'; Args = @() }, @{ Name = 'with a sandboxed engine session'; Args = $(if ($haveEngine) { @('--engine-dir', $engineDir, '--pdf', $pdf) } else { $null }) })) {
        if ($null -eq $mode.Args) { continue }
        $t = Measure-Wall $trimmedExe $mode.Args
        $p = Measure-Wall $plainExe $mode.Args
        Write-Host ("{0}: trimmed median {1} ms (min {2}, max {3}); untrimmed median {4} ms (min {5}, max {6})" -f $mode.Name, $t.MedianMs, $t.MinMs, $t.MaxMs, $p.MedianMs, $p.MinMs, $p.MaxMs)
    }
}
