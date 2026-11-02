# Dot-source: sets up the environment the handler DLL reads (S6B_*). Local runs only; nothing is registered or changed outside this process.
param([string] $Mode = 'fresh', [string] $Pipe = 'mikepdf-s6b-local', [int] $MaxEngines = 3, [int] $MemMb = 256, [string] $Extra = '')
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$out = Join-Path $PSScriptRoot 'out'
$sh = Get-ChildItem (Join-Path $PSScriptRoot 'ShellHost/bin/Release') -Recurse -Filter S6bShellHost.exe | Select-Object -First 1
$env:S6B_PIPE = $Pipe
$env:S6B_SHELLHOST = $sh.FullName
$env:S6B_SHELLHOST_ARGS = "--pipe $Pipe --engine-dir `"$root\build\out\cmake-x64\bin\Release`" --mode $Mode --max-engines $MaxEngines --mem-mb $MemMb --log `"$out\shellhost.log`" $Extra"
$env:S6B_LOG = "$out\handler.log"
$script:TH = Join-Path $out 'native-x64\s6b_testhost.exe'
$script:Dll = Join-Path $out 'native-x64\s6b_handler.dll'
$script:Pdfs = Join-Path $out 'pdfs'
