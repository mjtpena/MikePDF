<#
.SYNOPSIS
  S10 with the real broker and the real sandboxed engine. Needs ./build.ps1 to have built the native engine (build/out/cmake-x64/bin/Release).
.DESCRIPTION
  Patches ONE line of a copy of src/MikePDF.Broker/EngineSupervisor.cs (the FileShare passed when the broker opens a document) into out/gen, compiles the broker
  sources plus the probe, creates a throw-away AppContainer profile (MikePDF.S10Spike, deleted at the end), and tries each replace mechanism while the real engine has the
  document mapped. No window is opened and nothing under src/ is changed.
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$src = Get-Content (Join-Path $repo 'src\MikePDF.Broker\EngineSupervisor.cs') -Raw
$needle = 'file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);'
if (-not $src.Contains($needle)) { throw 'the broker no longer opens documents the way this spike expects; re-read EngineSupervisor.OpenDocumentAsync' }
New-Item -ItemType Directory -Force out\gen, out\raw | Out-Null
$patched = $src.Replace($needle, 'file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, S10.Patch.Share);')
Set-Content -Path out\gen\EngineSupervisor.cs -Value $patched -NoNewline
dotnet build RealEngine\S10.RealEngine.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$exe = Join-Path $PSScriptRoot 'out\build\S10.RealEngine\bin\Release\net10.0-windows10.0.26100.0\S10.RealEngine.exe'
& $exe (Join-Path $PSScriptRoot 'out\raw\real-engine.txt')
