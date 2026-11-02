# Finds PDFs on Wikimedia Commons whose licence is one the policy allows, per file, using the licence Commons records for each (the file's own description page),
# downloads them, and writes candidates (with the licence evidence) as JSON. Run by hand; the manifest holds the result.
#
#   ./Curate-Commons.ps1 -Cache <dir> -Out <commons.json>
param([string]$Cache, [string]$Out, [long]$MaxBytes = 6MB, [switch]$Extra)
. (Join-Path $PSScriptRoot 'Common.ps1')

# Query, tags that the query itself justifies, and how many files to take. Only the query's own claim is tagged here; page counts and content facts are added from the file.
$queries = @(
    @{ q = 'intitle:incategory:"Arabic books" filetype:pdf'; tags = @('rtl', 'arabic', 'real-world'); take = 14 }
    @{ q = 'intitle:arabic filetype:pdf'; tags = @('rtl', 'arabic', 'real-world'); take = 14 }
    @{ q = 'intitle:hebrew filetype:pdf'; tags = @('rtl', 'hebrew', 'real-world'); take = 12 }
    @{ q = 'intitle:persian filetype:pdf'; tags = @('rtl', 'persian', 'real-world'); take = 10 }
    @{ q = 'intitle:urdu filetype:pdf'; tags = @('rtl', 'urdu', 'real-world'); take = 6 }
    @{ q = 'intitle:chinese filetype:pdf'; tags = @('cjk', 'chinese', 'real-world'); take = 14 }
    @{ q = 'intitle:中文 filetype:pdf'; tags = @('cjk', 'chinese', 'real-world'); take = 10 }
    @{ q = 'intitle:japanese filetype:pdf'; tags = @('cjk', 'japanese', 'real-world'); take = 14 }
    @{ q = 'intitle:日本語 filetype:pdf'; tags = @('cjk', 'japanese', 'real-world'); take = 10 }
    @{ q = 'intitle:korean filetype:pdf'; tags = @('cjk', 'korean', 'real-world'); take = 10 }
    @{ q = 'filetype:pdf scanned'; tags = @('scanned', 'real-world'); take = 22 }
    @{ q = 'filetype:pdf digitized book'; tags = @('scanned', 'real-world'); take = 18 }
    @{ q = 'filetype:pdf "scan of"'; tags = @('scanned', 'real-world'); take = 14 }
    @{ q = 'filetype:pdf map'; tags = @('maps', 'real-world'); take = 10 }
    @{ q = 'filetype:pdf newspaper'; tags = @('scanned', 'newspaper', 'real-world'); take = 8 }
    @{ q = 'filetype:pdf sheet music score'; tags = @('vector', 'music', 'real-world'); take = 10 }
    @{ q = 'filetype:pdf poster'; tags = @('transparency', 'poster', 'real-world'); take = 8 }
    @{ q = 'filetype:pdf diagram infographic'; tags = @('vector', 'real-world'); take = 8 }
)

# A second pass (-Extra): the first one found few right-to-left and Korean files whose titles are in their own script, which is what Confirm-CommonsTags in
# Build-CoreEntries.ps1 needs to see before it keeps a script tag. These queries are words in the scripts themselves.
$secondPass = @(
    @{ q = 'intitle:كتاب filetype:pdf'; tags = @('rtl', 'arabic', 'real-world'); take = 14 }
    @{ q = 'intitle:مجلة filetype:pdf'; tags = @('rtl', 'arabic', 'real-world'); take = 8 }
    @{ q = 'intitle:تاريخ filetype:pdf'; tags = @('rtl', 'arabic', 'real-world'); take = 8 }
    @{ q = 'intitle:ספר filetype:pdf'; tags = @('rtl', 'hebrew', 'real-world'); take = 12 }
    @{ q = 'intitle:תורה filetype:pdf'; tags = @('rtl', 'hebrew', 'real-world'); take = 6 }
    @{ q = 'intitle:کتاب filetype:pdf'; tags = @('rtl', 'persian', 'real-world'); take = 10 }
    @{ q = 'intitle:دیوان filetype:pdf'; tags = @('rtl', 'persian', 'real-world'); take = 6 }
    @{ q = 'intitle:한국 filetype:pdf'; tags = @('cjk', 'korean', 'real-world'); take = 10 }
    @{ q = 'intitle:조선 filetype:pdf'; tags = @('cjk', 'korean', 'real-world'); take = 6 }
    @{ q = 'intitle:中國 filetype:pdf'; tags = @('cjk', 'chinese', 'real-world'); take = 8 }
    @{ q = 'intitle:日本 filetype:pdf'; tags = @('cjk', 'japanese', 'real-world'); take = 8 }
    @{ q = 'intitle:رسالة filetype:pdf'; tags = @('rtl', 'arabic', 'real-world'); take = 6 }
    @{ q = 'intitle:שיר filetype:pdf'; tags = @('rtl', 'hebrew', 'real-world'); take = 5 }
    @{ q = 'intitle:한국어 filetype:pdf'; tags = @('cjk', 'korean', 'real-world'); take = 4 }
    @{ q = 'intitle:中文 filetype:pdf'; tags = @('cjk', 'chinese', 'real-world'); take = 6 }
    @{ q = 'intitle:日本語 filetype:pdf'; tags = @('cjk', 'japanese', 'real-world'); take = 6 }
)
if ($Extra) { $queries = $secondPass }

function Get-LicenceFor([string]$short, [string]$restrictions) {
    if ($restrictions) { return $null }
    switch -Regex ($short) {
        '^(Public domain|PD[-\w ]*)$' { return @{ spdx = 'LicenseRef-Public-Domain'; text = $short } }
        '^CC0'                        { return @{ spdx = 'CC0-1.0'; text = $short } }
        '^CC BY (\d\.\d)$'            { return @{ spdx = "CC-BY-$($Matches[1])"; text = $short } }
        default                       { return $null }
    }
}

$seen = @{}
$result = New-Object System.Collections.Generic.List[object]
foreach ($q in $queries) {
    $taken = 0; $offset = 0; $tries = 0
    while ($taken -lt $q.take -and $tries -lt 8) {
        $tries++
        $u = 'https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=50&gsroffset=' + $offset + '&gsrsort=none&gsrsearch=' + [uri]::EscapeDataString($q.q) + '&prop=imageinfo&iiprop=url|size|sha1|mime|extmetadata&iiextmetadatafilter=LicenseShortName|Artist|Credit|Restrictions|UsageTerms|AttributionRequired'
        $r = Invoke-RestMethod $u -UserAgent 'MikePDF-corpus/1.0 (+https://github.com/mjtpena/MikePDF)'
        if (-not $r.query) { break }
        foreach ($p in ($r.query.pages.PSObject.Properties.Value | Sort-Object index)) {
            if ($taken -ge $q.take) { break }
            $i = $p.imageinfo[0]
            if ($i.mime -ne 'application/pdf' -or $i.size -gt $MaxBytes -or $i.size -lt 1500 -or $seen.ContainsKey($p.title)) { continue }
            $lic = Get-LicenceFor $i.extmetadata.LicenseShortName.value $i.extmetadata.Restrictions.value
            if (-not $lic) { continue }
            $seen[$p.title] = $true
            $name = $p.title.Substring(5)
            $dest = Join-Path $Cache ('commons\' + ($p.pageid) + '.pdf')
            if (-not (Save-Url $i.url $dest)) { Write-Warning "not downloaded: $($p.title)"; continue }
            $bytes = [IO.File]::ReadAllBytes($dest)
            if ($bytes.Length -ne $i.size) { Write-Warning "size differs: $($p.title)"; continue }
            $result.Add([pscustomobject]@{
                path = "commons/$($p.pageid)-" + (($name -replace '[^\w\.\-]', '_') -replace '_+', '_')
                bytes = $bytes.Length; sha256 = (Get-FileSha256 $dest)
                fetchUrl = $i.url; source = $i.descriptionurl
                licence = $lic.spdx; licenceText = $lic.text
                evidence = "Wikimedia Commons file page $($i.descriptionurl): licence '$($lic.text)', no restrictions recorded (API extmetadata, 2026-11-03)"
                artist = ($i.extmetadata.Artist.value -replace '<[^>]+>', '').Trim()
                commonsSha1 = $i.sha1
                tags = $q.tags; local = $dest; facts = (Get-PdfFacts $bytes)
            })
            $taken++
        }
        if ($r.continue.gsroffset) { $offset = $r.continue.gsroffset } else { break }
    }
    "{0,-45} {1}" -f $q.q, $taken
}
$result | ConvertTo-Json -Depth 6 | Set-Content $Out
"total $($result.Count), bytes $(($result | Measure-Object bytes -Sum).Sum)"
