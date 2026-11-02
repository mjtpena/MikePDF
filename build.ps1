<#
.SYNOPSIS
  Build (and optionally test) MikePDF: native engine via CMake, managed code via dotnet.
.DESCRIPTION
  Same entry point locally and in CI (.github/workflows/ci.yml). Native tests run only
  when the target architecture matches the host.
.EXAMPLE
  ./build.ps1 -Test
  ./build.ps1 -Arch arm64 -Configuration Debug
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [switch] $Test,
    [switch] $SkipNative,
    [switch] $SkipManaged,
    [switch] $Release  # release-grade checks: Spectre libraries required
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot

function Invoke-Step([string] $Name, [scriptblock] $Block) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

function Find-CMake {
    $onPath = Get-Command cmake -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        # -all: the newest VS instance is not necessarily the one with the C++ tools.
        $found = & $vswhere -all -products * -prerelease -sort -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' | Select-Object -First 1
        if ($found) { return $found }
    }
    throw 'cmake not found (install CMake or the VS C++ CMake tools)'
}

$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }

# The checked-in protocol code (C# and C++) must match the schema; the native build below compiles it too.
if (-not ($SkipNative -and $SkipManaged)) {
    Invoke-Step 'Protocol generator check' { dotnet run --project (Join-Path $root 'tools/MikePDF.ProtocolGen') -c $Configuration -- check }
}

if (-not $SkipNative) {
    $pdfium = & (Join-Path $root 'build/fetch-pdfium.ps1') -Arch $Arch | Select-Object -Last 1
    $cmake = Find-CMake
    $buildDir = Join-Path $root "build/out/cmake-$Arch"
    $platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    Invoke-Step 'CMake configure' { & $cmake -S $root -B $buildDir -A $platform "-DMIKEPDF_PDFIUM_DIR=$pdfium" "-DMIKEPDF_BUILD_TESTS=$(if ($Test) { 'ON' } else { 'OFF' })" "-DMIKEPDF_REQUIRE_SPECTRE=$(if ($Release -or $env:CI -eq 'true') { 'ON' } else { 'OFF' })" }
    Invoke-Step 'CMake build' { & $cmake --build $buildDir --config $Configuration --parallel }
    if ($Test -and $Arch -eq $hostArch) {
        $ctest = Join-Path (Split-Path $cmake) 'ctest.exe'
        Invoke-Step 'Native tests' { & $ctest --test-dir $buildDir -C $Configuration --output-on-failure }
    } elseif ($Test) {
        Write-Warning "Native tests skipped: cannot run $Arch binaries on a $hostArch host"
    }
}

if (-not $SkipManaged) {
    $sln = Join-Path $root 'MikePDF.slnx'
    Invoke-Step 'dotnet restore' { dotnet restore $sln -p:MikePDFArch=$Arch }
    Invoke-Step 'dotnet build' { dotnet build $sln -c $Configuration -p:MikePDFArch=$Arch --no-restore }
    if ($Test -and $Arch -eq $hostArch) {
        # The integration tests launch the real sandboxed engine built above.
        $env:MIKEPDF_ENGINE_DIR = Join-Path $root "build/out/cmake-$Arch/bin/$Configuration"
        Invoke-Step 'dotnet test' { dotnet test --solution $sln -c $Configuration -p:MikePDFArch=$Arch --no-build }
    } elseif ($Test) {
        Write-Warning "Managed tests skipped: cannot run $Arch binaries on a $hostArch host"
    }
}
