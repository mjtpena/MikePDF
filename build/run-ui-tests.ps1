<#
.SYNOPSIS
  Runs the FlaUI UI journeys (tests/MikePDF.UI.Tests) against a loosely registered MikePDF package. Needs an unlocked, interactive desktop.
.DESCRIPTION
  ADR-0009. Not part of ./build.ps1 -Test: the tests drive real windows, so they cannot run headless.
  Steps: build the native engine (unless -SkipBuild) and the app, register the build output with Add-AppxPackage -Register (Developer Mode,
  no certificate and no elevation), run the tests, take screenshots into build/out/shots, then unregister (unless -KeepInstalled).
  Never adds a certificate to any trust store and never asks for administrator rights.
.EXAMPLE
  ./build/run-ui-tests.ps1
  ./build/run-ui-tests.ps1 -SkipBuild -Filter-method "*Password*"
  ./build/run-ui-tests.ps1 -SkipBuild -PackageLayout build/out/package/stage-x64
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
    [switch] $SkipBuild,
    [switch] $SkipNativeBuild,
    [switch] $KeepInstalled,
    # A package layout made by build/package.ps1 (its stage-<arch> folder) to test instead of the build output: the shipped, published app.
    [string] $PackageLayout,
    # For the GitHub-hosted runner (.github/workflows/ui-journeys.yml): turns Developer Mode on (an HKLM write, so it refuses to run anywhere else), and
    # writes a TRX report to build/out/ui-results. The registered manifest and the shell-handler check are the same as locally.
    [switch] $Ci,
    [string] ${Filter-method}
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# Ends the MikePDF apps and the engines they started (their descendants), never a MikePDF.Engine that belongs to anything else: another
# agent's integration test, the render tool, a person's own reader.
function Stop-MikePDF {
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name
    foreach ($app in (Get-Process MikePDF -ErrorAction SilentlyContinue)) {
        $queue = [System.Collections.Generic.Queue[int]]::new(); $queue.Enqueue($app.Id); $descendants = @()
        while ($queue.Count -gt 0) {
            $at = $queue.Dequeue()
            foreach ($c in ($all | Where-Object { $_.ParentProcessId -eq $at -and $_.ProcessId -ne $at })) { $descendants += $c; $queue.Enqueue([int]$c.ProcessId) }
        }
        foreach ($d in ($descendants | Where-Object { $_.Name -eq 'MikePDF.Engine.exe' })) { Stop-Process -Id $d.ProcessId -Force -ErrorAction SilentlyContinue }
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    }
}
Set-Location $root

if ($Ci) {
    # Developer Mode is what lets Add-AppxPackage -Register take an unsigned layout. It is a machine setting (HKLM), so only a disposable hosted runner may have it set by a script.
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw '-Ci changes a machine setting and runs only on a GitHub-hosted runner.' }
    $unlock = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    New-Item -Path $unlock -Force | Out-Null
    Set-ItemProperty -Path $unlock -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord
}

if (-not $SkipBuild -and -not $SkipNativeBuild) {
    & (Join-Path $root 'build.ps1') -Arch $Arch -SkipManaged
    if ($LASTEXITCODE -ne 0) { throw 'native build failed' }
}

Stop-MikePDF
if (-not $SkipBuild) {
    dotnet build (Join-Path $root 'src/MikePDF.App') -c Release "-p:MikePDFArch=$Arch" -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'app build failed' }
    dotnet build (Join-Path $root 'tests/MikePDF.UI.Tests') -c Release "-p:MikePDFArch=$Arch" -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'UI test build failed' }
}

$sourceLayout = if ($PackageLayout) { (Resolve-Path $PackageLayout).Path } else { Join-Path $root "src/MikePDF.App/bin/Release/net10.0-windows10.0.26100.0/win-$Arch" }
if (-not (Test-Path (Join-Path $sourceLayout 'AppxManifest.xml'))) { throw "No package layout at $sourceLayout. Build src/MikePDF.App first." }

# The Explorer thumbnail provider, the preview handler and their COM servers are shell handlers, and this harness must never register those on the machine it runs on. It registers a COPY of
# the layout whose manifest has them removed (everything else stays, the .pdf file type association included), says so, and fails if the manifest that ends up registered still has them.
$shellEntries = '<desktop2:ThumbnailHandler', '<desktop2:DesktopPreviewHandler', '<com:SurrogateServer', 'Category="windows.comServer"'
function Test-ShellEntries([string] $manifestPath) {
    $text = Get-Content $manifestPath -Raw
    # comments may name the elements (the manifest explains them); only real elements count
    $text = [regex]::Replace($text, '(?s)<!--.*?-->', '')
    return @($shellEntries | Where-Object { $text.Contains($_) })
}
$layout = Join-Path $root "build/out/ui-layout-$Arch"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
Copy-Item -Path $sourceLayout -Destination $layout -Recurse -Force
$manifest = Join-Path $layout 'AppxManifest.xml'
$text = Get-Content $manifest -Raw
$text = [regex]::Replace($text, '\s*<desktop2:ThumbnailHandler[^>]*/>', '')
$text = [regex]::Replace($text, '\s*<desktop2:DesktopPreviewHandler[^>]*/>', '')
$text = [regex]::Replace($text, '(?s)\s*<com:Extension Category="windows.comServer">.*?</com:Extension>', '')
Set-Content $manifest $text -Encoding UTF8
$left = Test-ShellEntries $manifest
if ($left.Count -gt 0) { throw "The manifest copy still has shell handler entries ($($left -join ', ')); refusing to register it." }
Write-Host "Shell handlers (thumbnail provider, preview handler, their COM servers) were REMOVED from the manifest this run registers; the rest, including the .pdf association, is unchanged. Layout: $layout" -ForegroundColor Yellow
$identity = ([xml](Get-Content $manifest -Raw)).Package.Identity.Name
$previous = Get-AppxPackage -Name $identity
if ($previous) { Remove-AppxPackage $previous.PackageFullName }
Add-AppxPackage -Register $manifest
$registered = Get-AppxPackage -Name $identity
$registeredLeft = Test-ShellEntries (Join-Path $registered.InstallLocation 'AppxManifest.xml')
if ($registeredLeft.Count -gt 0) {
    Remove-AppxPackage $registered.PackageFullName
    throw "The registered package still declares shell handler entries ($($registeredLeft -join ', ')); it was removed again and nothing was run."
}
$shots = Join-Path $root 'build/out/shots'
New-Item -ItemType Directory -Force $shots | Out-Null
try {
    $env:MIKEPDF_UI_TESTS = '1'
    $env:MIKEPDF_SHOTS_DIR = $shots
    $arguments = @('test', '--project', (Join-Path $root 'tests/MikePDF.UI.Tests'), '-c', 'Release', '--no-build')
    if ($Ci) {
        $results = Join-Path $root 'build/out/ui-results'
        New-Item -ItemType Directory -Force $results | Out-Null
        $arguments += @('--results-directory', $results, '--report-xunit-trx', '--report-xunit-trx-filename', "ui-journeys-$Arch.trx")
    }
    if (${Filter-method}) { $arguments += @('--filter-method', ${Filter-method}) }
    dotnet @arguments
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:MIKEPDF_UI_TESTS, Env:MIKEPDF_SHOTS_DIR -ErrorAction SilentlyContinue
    Stop-MikePDF
    if (-not $KeepInstalled) { Get-AppxPackage -Name $identity | Remove-AppxPackage }
}
Write-Host "Screenshots: $shots"
exit $code
