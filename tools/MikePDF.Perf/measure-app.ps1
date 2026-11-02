#requires -Version 7
<#
.SYNOPSIS
  Measures the packaged app end to end from its ETW markers: cold start to an interactive window, cold open to first pixel, warm open to first
  pixel, scroll frame times, and the UI process's memory (master prompt 11).

.DESCRIPTION
  Two modes.

  Capture (default): needs an elevated PowerShell (wpr starts an ETW session), the packaged app installed, and no MikePDF running. For each run it
  starts a trace of the provider "MikePDF-App" with wpr, launches the app on a document through the app execution alias, optionally flings the page
  with the mouse wheel, samples the UI process's working set, stops the trace, reads the events out of the .etl with Get-WinEvent, and closes the app
  window. It never installs, registers or unregisters a package, and changes no setting. -Fling drives the real mouse wheel over the app window.

  Analysis only (-EventsCsv): reads event files of the form "TimeUtc,EventId,ProcessId" (ISO 8601 times) and computes the figures. This is the part
  that is tested, on synthetic traces (tests/MikePDF.Perf.Tests/TraceAnalysisTests.cs). Use it on events exported from a trace made some other way
  (PerfView, WPA).

  HONESTY: the capture mode, the .etl conversion and the markers inside the running app were written without being able to launch the packaged app
  on the machine they were written on. They are unverified until someone runs this script on a machine with the app installed.

.EXAMPLE
  # Capture three cold launches of the generated 1-page PDF, from an elevated pwsh:
  ./tools/MikePDF.Perf/measure-app.ps1 -Runs 3

.EXAMPLE
  # Analyse event files without capturing:
  ./tools/MikePDF.Perf/measure-app.ps1 -EventsCsv run1.csv,run2.csv -ProcessStart 2026-11-02T08:00:00.1234567Z,2026-11-02T08:01:00.2345678Z
#>
[CmdletBinding(DefaultParameterSetName = 'Capture')]
param(
    # Capture: the PDF to open. Default: the generated 1-page document.
    [Parameter(ParameterSetName = 'Capture')] [string] $Document,
    # Capture: how to start the app. The execution alias installed by the package.
    [Parameter(ParameterSetName = 'Capture')] [string] $AppCommand = 'mikepdf.exe',
    [Parameter(ParameterSetName = 'Capture')] [ValidateRange(1, 50)] [int] $Runs = 3,
    # Capture: seconds to wait after the window appears before sampling memory and stopping.
    [Parameter(ParameterSetName = 'Capture')] [ValidateRange(2, 120)] [int] $SettleSeconds = 8,
    # Capture: fling the page with the mouse wheel for -FlingSeconds (drives the REAL mouse over the app window).
    [Parameter(ParameterSetName = 'Capture')] [switch] $Fling,
    [Parameter(ParameterSetName = 'Capture')] [ValidateRange(2, 60)] [int] $FlingSeconds = 10,

    # Analysis only: one event file per launch.
    [Parameter(ParameterSetName = 'Events', Mandatory)] [string[]] $EventsCsv,
    # Analysis only: the process creation time of each launch (ISO 8601, UTC), in the same order. Without it the app's Main event is the start.
    [Parameter(ParameterSetName = 'Events')] [string[]] $ProcessStart,

    # Path of MikePDF.Perf.exe. Default: the Release build under tools/MikePDF.Perf/bin.
    [string] $PerfTool,
    [string] $OutDir = 'perf-results/app',
    [string] $RunnerId = 'local-app',
    # Only on that real reference machine.
    [ValidateSet('R1', 'R2', 'R3')] [string] $ReferenceTier
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$providerGuid = '6f1b4a44-3d0e-4c6b-9b64-0d5a1d1f7a21' # MikePDF.Perf.Trace.AppEventIds.ProviderGuid; a test keeps it equal to the app's
$providerName = 'MikePDF-App'

function Find-PerfTool {
    if ($PerfTool) {
        if (-not (Test-Path $PerfTool)) { throw "MikePDF.Perf.exe not found at $PerfTool" }
        return (Resolve-Path $PerfTool).Path
    }
    $found = Get-ChildItem (Join-Path $PSScriptRoot 'bin/Release') -Recurse -Filter MikePDF.Perf.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw 'MikePDF.Perf.exe not found. Build it: dotnet build tools/MikePDF.Perf -c Release' }
    return $found.FullName
}

function Invoke-Analysis([string[]] $Csv, [string[]] $Starts, [hashtable[]] $Memory) {
    $tool = Find-PerfTool
    $arguments = @('trace-metrics', '--out', $OutDir, '--runner-id', $RunnerId)
    if ($ReferenceTier) { $arguments += @('--reference-tier', $ReferenceTier) }
    for ($i = 0; $i -lt $Csv.Count; $i++) {
        $spec = (Resolve-Path $Csv[$i]).Path
        if ($Starts -and $i -lt $Starts.Count -and $Starts[$i]) { $spec += ";processStart=$($Starts[$i])" }
        if ($Memory -and $i -lt $Memory.Count) {
            $spec += ";uiWorkingSetMb=$($Memory[$i].WorkingSetMb.ToString('0.###', [cultureinfo]::InvariantCulture))"
            $spec += ";uiPrivateMb=$($Memory[$i].PrivateMb.ToString('0.###', [cultureinfo]::InvariantCulture))"
        }
        $arguments += @('--run', $spec)
    }
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw "MikePDF.Perf trace-metrics failed with exit code $LASTEXITCODE" }
    Write-Host "Wrote $(Join-Path $OutDir 'app-results.json') and app-results.md"
}

if ($PSCmdlet.ParameterSetName -eq 'Events') {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    Invoke-Analysis -Csv $EventsCsv -Starts $ProcessStart -Memory @()
    return
}

# ---- capture ------------------------------------------------------------------------------------------------------------------------

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Capturing an ETW trace with wpr needs an elevated PowerShell. (Use -EventsCsv to analyse events captured elsewhere.)'
}
if (-not (Get-Command wpr.exe -ErrorAction SilentlyContinue)) { throw 'wpr.exe (Windows Performance Recorder) was not found.' }
if (Get-Process -Name MikePDF -ErrorAction SilentlyContinue) { throw 'MikePDF is already running. Close it first: a cold start cannot be measured with the app already open.' }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

if (-not $Document) {
    $tool = Find-PerfTool
    $docs = Join-Path $OutDir 'documents'
    & $tool generate --out $docs --only text1 | Out-Null
    $Document = Join-Path $docs 'text1.pdf'
}
$Document = (Resolve-Path $Document).Path

$profilePath = Join-Path $OutDir 'MikePDF-App.wprp'
@"
<?xml version="1.0" encoding="utf-8"?>
<WindowsPerformanceRecorder Version="1.0" Author="MikePDF.Perf" Team="MikePDF">
  <Profiles>
    <EventCollector Id="EventCollector_MikePDF" Name="MikePDF markers">
      <BufferSize Value="256"/>
      <Buffers Value="32"/>
    </EventCollector>
    <EventProvider Id="EventProvider_MikePDF" Name="$providerGuid" Level="5"/>
    <Profile Id="MikePDF.Verbose.File" Name="MikePDF" Description="$providerName markers" LoggingMode="File" DetailLevel="Verbose">
      <Collectors>
        <EventCollectorId Value="EventCollector_MikePDF">
          <EventProviders>
            <EventProviderId Value="EventProvider_MikePDF"/>
          </EventProviders>
        </EventCollectorId>
      </Collectors>
    </Profile>
  </Profiles>
</WindowsPerformanceRecorder>
"@ | Set-Content -Path $profilePath -Encoding utf8

if ($Fling) {
    Add-Type -Namespace MikePdf -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
}

function Invoke-Fling([System.Diagnostics.Process] $Process, [int] $Seconds) {
    $rect = New-Object MikePdf.Native+RECT
    [void][MikePdf.Native]::GetWindowRect($Process.MainWindowHandle, [ref]$rect)
    [void][MikePdf.Native]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2))
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        [MikePdf.Native]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero) # MOUSEEVENTF_WHEEL, one notch down
        Start-Sleep -Milliseconds 8
    }
}

$csvFiles = @()
$starts = @()
$memory = @()
for ($run = 1; $run -le $Runs; $run++) {
    Write-Host "Run $run of $Runs"
    $etl = Join-Path $OutDir "run$run.etl"
    $csv = Join-Path $OutDir "run$run.csv"
    Remove-Item $etl, $csv -ErrorAction SilentlyContinue
    & wpr.exe -cancel 2>$null | Out-Null
    & wpr.exe -start "$profilePath!MikePDF.Verbose.File" -filemode
    if ($LASTEXITCODE -ne 0) { throw "wpr -start failed with exit code $LASTEXITCODE" }
    $process = $null
    try {
        Start-Process -FilePath $AppCommand -ArgumentList "`"$Document`""
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not $process -and [DateTime]::UtcNow -lt $deadline) {
            $process = Get-Process -Name MikePDF -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $process) { Start-Sleep -Milliseconds 20 }
        }
        if (-not $process) { throw "$AppCommand did not start a MikePDF process within 30 s." }
        $startTime = $process.StartTime.ToUniversalTime().ToString('o')
        Start-Sleep -Seconds $SettleSeconds
        if ($Fling) { Invoke-Fling -Process $process -Seconds $FlingSeconds; Start-Sleep -Seconds 1 }
        $process.Refresh()
        $workingSet = $process.WorkingSet64 / 1MB
        $private = $process.PrivateMemorySize64 / 1MB
    }
    finally {
        & wpr.exe -stop $etl 2>&1 | Out-Null
        if ($process -and -not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) { Write-Warning "MikePDF (pid $($process.Id)) did not close; close it by hand." }
        }
    }

    # The provider is not registered on this machine, so only the event id, process id and time are read; the payloads are not needed.
    $events = Get-WinEvent -Path $etl -Oldest | Where-Object { $_.ProviderId -eq [guid]$providerGuid }
    if (-not $events) { throw "No events from $providerName in $etl. Is the installed build one with the markers?" }
    $events | ForEach-Object { '{0},{1},{2}' -f $_.TimeCreated.ToUniversalTime().ToString('o'), $_.Id, $_.ProcessId } |
        ForEach-Object -Begin { 'TimeUtc,EventId,ProcessId' } -Process { $_ } | Set-Content -Path $csv -Encoding utf8
    $csvFiles += $csv
    $starts += $startTime
    $memory += @{ WorkingSetMb = $workingSet; PrivateMb = $private }
}

Invoke-Analysis -Csv $csvFiles -Starts $starts -Memory $memory
