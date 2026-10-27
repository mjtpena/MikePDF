# Builds and publishes the S3 binaries (Release). Output goes to spikes/S3/out (gitignored).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force $out | Out-Null
Push-Location $root
try {
    # framework-dependent engine (uses the shared .NET runtime in C:\Program Files\dotnet)
    dotnet publish engine -c Release -o (Join-Path $out 'engine-fdd') --self-contained false
    # self-contained engine (runtime copied next to the exe, so the only directory the container must read is inside the repo)
    dotnet publish engine -c Release -r win-x64 --self-contained true -o (Join-Path $out 'engine-sc')
    # identical copy for the Low-IL variant. An inheritable AppContainer ACE on the directory makes the Low-IL apphost fail (0x80008085),
    # so this directory never receives that ACE.
    $low = Join-Path $out 'engine-sc-low'
    if (Test-Path $low) { Get-ChildItem $low -Recurse | ForEach-Object { $_.Attributes = 'Normal' }; [System.IO.Directory]::Delete($low, $true) }
    Copy-Item -Recurse (Join-Path $out 'engine-sc') $low
    # broker / driver
    dotnet publish launcher -c Release -o (Join-Path $out 'launcher') --self-contained false
}
finally { Pop-Location }
# start every run from clean ACLs; the launcher re-grants the container ACE on engine-fdd and engine-sc only
foreach ($d in 'engine-fdd', 'engine-sc', 'engine-sc-low') { icacls (Join-Path $out $d) /reset /T /Q | Out-Null }
