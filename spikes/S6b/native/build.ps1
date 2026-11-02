<#
.SYNOPSIS
  Builds the S6b handler DLL and the test host with MSVC (x64 or arm64). Throwaway spike code.
.DESCRIPTION
  Finds a Visual Studio with the C++ tools via vswhere (any instance), imports its environment for the requested architecture, and compiles with the same
  hardening flags as the engine (/guard:cf /sdl /GS /CETCOMPAT on x64). Nothing is registered.
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $OutDir
)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "../out/native-$Arch" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$install = & $vswhere -all -products * -prerelease -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $install) { throw 'No Visual Studio with the C++ x64 tools found.' }
$vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
$target = if ($Arch -eq 'arm64') { if ($hostArch -eq 'arm64') { 'arm64' } else { 'x64_arm64' } } else { if ($hostArch -eq 'arm64') { 'arm64_x64' } else { 'x64' } }

$src = $PSScriptRoot
$cetFlag = if ($Arch -eq 'x64') { '/CETCOMPAT' } else { '' }
$cl = @"
call "$vcvars" $target >nul || exit /b 1
cd /d "$OutDir"
cl /nologo /std:c++20 /EHsc /O2 /W4 /GS /guard:cf /sdl /DUNICODE /D_UNICODE /LD "$src\s6b_handler.cpp" /Fe:s6b_handler.dll /link /DEF:"$src\s6b_handler.def" /guard:cf $cetFlag ole32.lib shlwapi.lib shell32.lib gdi32.lib user32.lib advapi32.lib uuid.lib || exit /b 1
cl /nologo /std:c++20 /EHsc /O2 /W4 /GS /guard:cf /sdl /DUNICODE /D_UNICODE "$src\s6b_testhost.cpp" /Fe:s6b_testhost.exe /link /guard:cf $cetFlag ole32.lib shlwapi.lib shell32.lib gdi32.lib user32.lib advapi32.lib uuid.lib psapi.lib || exit /b 1
"@
$bat = Join-Path $OutDir 'build.cmd'
Set-Content -Path $bat -Value $cl -Encoding ASCII
& cmd.exe /c $bat
if ($LASTEXITCODE -ne 0) { throw "native build failed ($LASTEXITCODE)" }
Get-ChildItem $OutDir | Where-Object { $_.Extension -in '.dll', '.exe' } | Select-Object Name, Length | Format-Table | Out-Host
