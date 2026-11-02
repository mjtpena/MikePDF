param([Parameter(Mandatory)][string]$Dir, [string]$Filter = '*.dll,*.exe')
# Prints PE machine type for every native/managed binary in a directory tree.
$map = @{ 0x8664 = 'x64'; 0xAA64 = 'ARM64'; 0x014C = 'x86'; 0x0 = 'unknown' }
Get-ChildItem $Dir -Recurse -Include ($Filter -split ',') -File | ForEach-Object {
    try {
        $fs = [IO.File]::OpenRead($_.FullName)
        $br = New-Object IO.BinaryReader $fs
        if ($br.ReadUInt16() -ne 0x5A4D) { [pscustomobject]@{ File = $_.FullName.Substring($Dir.Length); Machine = 'not-PE'; Managed = '' }; return }
        $fs.Position = 0x3C; $pe = $br.ReadInt32(); $fs.Position = $pe + 4; $m = $br.ReadUInt16()
        $fs.Position = $pe + 24; $magic = $br.ReadUInt16()
        $dirOff = if ($magic -eq 0x20B) { $pe + 24 + 112 + 14 * 8 } else { $pe + 24 + 96 + 14 * 8 }
        $fs.Position = $dirOff; $clr = $br.ReadUInt32()
        [pscustomobject]@{ File = $_.FullName.Substring($Dir.Length); Machine = $(if ($map.ContainsKey([int]$m)) { $map[[int]$m] } else { '0x{0:X}' -f $m }); Managed = $(if ($clr) { 'CLR' } else { '' }) }
    } finally { if ($fs) { $fs.Dispose() } }
}
