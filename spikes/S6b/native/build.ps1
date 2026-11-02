<#
.SYNOPSIS
  Builds the S6b handler DLL and the test host with MSVC (x64 or arm64); with -Ci also the CI driver and the COM ExeServer probe. Throwaway spike code.
.DESCRIPTION
  Finds a Visual Studio instance that has the C++ tools (any instance: the newest one is not necessarily the one with them), imports its environment for the
  requested architecture, and compiles with the engine's hardening flags (/guard:cf /sdl /GS, /CETCOMPAT on x64). Nothing is registered.
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $OutDir,
    [switch] $Ci
)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "../out/native-$Arch" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vcvars = $null
foreach ($install in (& $vswhere -all -products * -prerelease -property installationPath)) {
    $candidate = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
    if ((Test-Path $candidate) -and (Test-Path (Join-Path $install 'VC\Tools\MSVC'))) { $vcvars = $candidate; break }
}
if (-not $vcvars) { throw 'No Visual Studio with the C++ tools found.' }
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
$target = if ($Arch -eq 'arm64') { if ($hostArch -eq 'arm64') { 'arm64' } else { 'x64_arm64' } } else { if ($hostArch -eq 'arm64') { 'arm64_x64' } else { 'x64' } }

$src = $PSScriptRoot
$ciDir = Join-Path $PSScriptRoot '../ci'
$cetFlag = if ($Arch -eq 'x64') { '/CETCOMPAT' } else { '' }
$common = '/nologo /std:c++20 /EHsc /O2 /W3 /GS /guard:cf /sdl /DUNICODE /D_UNICODE'
$libs = 'ole32.lib shlwapi.lib shell32.lib gdi32.lib user32.lib advapi32.lib uuid.lib'
$lines = @(
    "call `"$vcvars`" $target >nul || exit /b 1",
    "cd /d `"$OutDir`"",
    "cl $common /LD `"$src\s6b_handler.cpp`" /Fe:s6b_handler.dll /link /DEF:`"$src\s6b_handler.def`" /guard:cf $cetFlag $libs || exit /b 1",
    "cl $common `"$src\s6b_testhost.cpp`" /Fe:s6b_testhost.exe /link /guard:cf $cetFlag $libs psapi.lib || exit /b 1"
)
if ($Ci) {
    $lines += "cl $common `"$ciDir\driver.cpp`" /Fe:S6bDriver.exe /link /guard:cf $cetFlag $libs || exit /b 1"
    $lines += "cl $common `"$ciDir\comserver.cpp`" /Fe:S6bComServer.exe /link /guard:cf $cetFlag $libs || exit /b 1"
}
$bat = Join-Path $OutDir 'build.cmd'
Set-Content -Path $bat -Value $lines -Encoding ASCII
& cmd.exe /c $bat
if ($LASTEXITCODE -ne 0) { throw "native build failed ($LASTEXITCODE)" }
Get-ChildItem $OutDir | Where-Object { $_.Extension -in '.dll', '.exe' } | Select-Object Name, Length | Format-Table | Out-Host
