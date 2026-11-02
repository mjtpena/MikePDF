# The rest of PDFium's testing/resources at the pinned commit (the smoke tier has the first ones). Gitiles serves a file base64-encoded with ?format=TEXT.
# `-Names` is the list of repository paths (testing/resources/...), from `git ls-tree -r --name-only <commit> -- testing/resources` in a blobless clone.
#
#   ./Curate-Pdfium.ps1 -Names <file> -Cache <dir> -Out <pdfium.json>
param([string]$Names, [string]$InNames, [string]$Cache, [string]$Out, [string]$Commit = '43d55e429cf72c21e9a1e60475b50feefa16fda9')
. (Join-Path $PSScriptRoot 'Common.ps1')
$paths = Get-Content $Names | ForEach-Object { $_.Replace('\', '/') }
$templates = @{}; foreach ($p in (Get-Content $InNames)) { $templates[$p.Replace('\', '/')] = $true }
$base = "https://pdfium.googlesource.com/pdfium/+/$Commit/"
$paths | ForEach-Object -ThrottleLimit 6 -Parallel {
    . (Join-Path $using:CurationRoot 'Common.ps1')
    $dest = Join-Path $using:Cache ('pdfium\' + $_.Replace('/', '\'))
    if (-not (Test-Path $dest)) {
        $raw = $dest + '.b64'
        if (Save-Url ($using:base + ($_ -split '/' | ForEach-Object { [uri]::EscapeDataString($_) } | Join-String -Separator '/') + '?format=TEXT') $raw) {
            [IO.File]::WriteAllBytes($dest, [Convert]::FromBase64String(([IO.File]::ReadAllText($raw)).Trim()))
            Remove-Item $raw
        }
    }
}
$result = foreach ($p in $paths) {
    $dest = Join-Path $Cache ('pdfium\' + $p.Replace('/', '\'))
    if (-not (Test-Path $dest)) { Write-Warning "not downloaded: $p"; continue }
    $bytes = [IO.File]::ReadAllBytes($dest)
    $rel = $p.Substring('testing/resources/'.Length)
    [pscustomobject]@{
        path = "pdfium/$rel"; bytes = $bytes.Length; sha256 = (Get-FileSha256 $dest)
        fetchUrl = $base + (($p -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/') + '?format=TEXT'; fetchEncoding = 'base64'
        source = $base + (($p -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
        hasTemplate = $templates.ContainsKey(($p -replace '\.pdf$', '.in')); local = $dest; facts = (Get-PdfFacts $bytes)
    }
}
$result | ConvertTo-Json -Depth 6 | Set-Content $Out
"pdfium: $(@($result).Count) files, $((@($result) | Measure-Object bytes -Sum).Sum) bytes"
