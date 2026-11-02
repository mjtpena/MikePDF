# arXiv papers whose authors chose a Creative Commons licence that allows redistribution. arXiv's default licence does NOT allow others to redistribute, so
# only papers whose OAI-PMH record names CC BY or CC0 are taken (not NC, ND or SA). Each paper's licence is read from its own record (arXiv metadata format
# `arXiv`, element `license`, which describes the current version), so the current version is fetched (/pdf/<id>v<N>, N from the arXiv API) and pinned by hash.
# arXiv asks automated clients to wait 3 seconds between requests (https://info.arxiv.org/help/api/tou.html); the corpus fetcher does too.
#
#   ./Curate-Arxiv.ps1 -Cache <dir> -Out <arxiv.json>
param([string]$Cache, [string]$Out, [long]$MaxBytes = 5MB)
. (Join-Path $PSScriptRoot 'Common.ps1')

# set, what the choice is for, how many
$sets = @(
    @{ set = 'math'; tags = @('math', 'type1-fonts'); take = 6 }
    @{ set = 'cs:cs:CV'; tags = @('figures', 'images'); take = 7 }
    @{ set = 'cs:cs:GR'; tags = @('figures', 'vector', 'transparency'); take = 5 }
    @{ set = 'cs:cs:CL'; tags = @('tables'); take = 4 }
    @{ set = 'physics:astro-ph'; tags = @('figures', 'images'); take = 5 }
    @{ set = 'physics:physics'; tags = @('figures', 'vector'); take = 4 }
    @{ set = 'eess'; tags = @('figures', 'images'); take = 4 }
    @{ set = 'q-bio'; tags = @('figures'); take = 4 }
    @{ set = 'stat'; tags = @('math', 'figures'); take = 4 }
    @{ set = 'econ'; tags = @('tables'); take = 3 }
)
$window = @{ from = '2024-03-04'; until = '2024-03-06' }
$agent = 'MikePDF-corpus/1.0 (+https://github.com/mjtpena/MikePDF)'
$result = New-Object System.Collections.Generic.List[object]
foreach ($s in $sets) {
    Start-Sleep -Seconds 3
    $u = "https://export.arxiv.org/oai2?verb=ListRecords&metadataPrefix=arXiv&set=$($s.set)&from=$($window.from)&until=$($window.until)"
    $x = [xml](Invoke-WebRequest $u -UserAgent $agent -TimeoutSec 120).Content
    $recs = @($x.'OAI-PMH'.ListRecords.record)
    $taken = 0
    foreach ($rec in $recs) {
        if ($taken -ge $s.take) { break }
        $m = $rec.metadata.arXiv
        if (-not $m) { continue }
        $licence = switch ($m.license) {
            'http://creativecommons.org/licenses/by/4.0/' { 'CC-BY-4.0' }
            'http://creativecommons.org/publicdomain/zero/1.0/' { 'CC0-1.0' }
            default { $null }
        }
        if (-not $licence -or $m.id -notlike '2403.*' -or $m.id -notmatch '^\d{4}\.\d{4,5}$' -or ($result | Where-Object { $_.arxivId -eq $m.id })) { continue }
        Start-Sleep -Seconds 3
        $api = [xml](Invoke-WebRequest "https://export.arxiv.org/api/query?id_list=$($m.id)" -UserAgent $agent -TimeoutSec 120).Content
        $version = if ($api.feed.entry.id -match 'v(\d+)$') { $Matches[1] } else { $null }
        if (-not $version) { continue }
        Start-Sleep -Seconds 3
        $name = "$($m.id)v$version"
        $url = "https://arxiv.org/pdf/$name"
        $dest = Join-Path $Cache "arxiv\$name.pdf"
        if (-not (Save-Url $url $dest 2)) { Write-Warning "not downloaded: $url"; continue }
        $bytes = [IO.File]::ReadAllBytes($dest)
        if ($bytes.Length -gt $MaxBytes -or $bytes.Length -lt 3000 -or [Text.Encoding]::Latin1.GetString($bytes, 0, 5) -ne '%PDF-') { Remove-Item $dest; continue }
        $result.Add([pscustomobject]@{
            path = "arxiv/$name.pdf"; arxivId = $m.id; bytes = $bytes.Length; sha256 = (Get-FileSha256 $dest)
            fetchUrl = $url; source = "https://arxiv.org/abs/$name"
            licence = $licence
            evidence = "arXiv record $($m.id): license element $($m.license) (OAI-PMH metadataPrefix=arXiv, harvested 2026-11-03); version $version is the current one"
            tags = @('arxiv', 'real-world', 'scientific') + $s.tags; local = $dest; facts = (Get-PdfFacts $bytes)
        })
        $taken++
    }
    "{0,-20} {1} of {2} records" -f $s.set, $taken, $recs.Count
}
$result | ConvertTo-Json -Depth 6 | Set-Content $Out
"arxiv: $($result.Count) files, $(($result | Measure-Object bytes -Sum).Sum) bytes"
