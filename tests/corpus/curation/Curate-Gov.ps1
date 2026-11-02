# US federal government documents, which are works of the US Government and so in the public domain (17 U.S.C. 105; https://www.usa.gov/government-works).
# Chosen so that the URL names an immutable document: an enrolled bill, a Federal Register document of a given day, an IRS form of a past tax year.
#
#   ./Curate-Gov.ps1 -Cache <dir> -Out <gov.json>
param([string]$Cache, [string]$Out)
. (Join-Path $PSScriptRoot 'Common.ps1')

$evidence105 = 'US Government work: not eligible for copyright under 17 U.S.C. 105 (https://www.usa.gov/government-works); GovInfo public domain notice https://www.govinfo.gov/about/policies'
$result = New-Object System.Collections.Generic.List[object]
function Add-Gov([string]$Url, [string]$Name, [string[]]$Tags, [string]$Source, [string]$Evidence) {
    $dest = Join-Path $Cache ('gov\' + $Name)
    if (-not (Save-Url $Url $dest)) { Write-Warning "not downloaded: $Url"; return }
    $bytes = [IO.File]::ReadAllBytes($dest)
    if ($bytes.Length -lt 1000 -or [Text.Encoding]::Latin1.GetString($bytes, 0, 5) -ne '%PDF-') { Write-Warning "not a PDF: $Url"; return }
    $script:result.Add([pscustomobject]@{
        path = "gov/$Name"; bytes = $bytes.Length; sha256 = (Get-FileSha256 $dest); fetchUrl = $Url; source = $Source
        licence = 'LicenseRef-Public-Domain'; evidence = $Evidence; tags = $Tags; local = $dest; facts = (Get-PdfFacts $bytes)
    })
}

# Enrolled bills: some of the longest documents there are (thousands of pages in a few MB).
$bills = 'BILLS-117hr3684enr', 'BILLS-117hr2617enr', 'BILLS-116hr133enr', 'BILLS-116hr748enr', 'BILLS-117hr5376enr', 'BILLS-117s2938enr', 'BILLS-117hr1319enr', 'BILLS-118hr2670enr', 'BILLS-117hr4346enr', 'BILLS-115hr1892enr'
foreach ($b in $bills) {
    $url = "https://www.govinfo.gov/content/pkg/$b/pdf/$b.pdf"
    Add-Gov $url "$b.pdf" @('gov', 'real-world', 'text', 'long-document') "https://www.govinfo.gov/app/details/$b" $evidence105
}

# IRS forms and instructions of past tax years (a prior-year file is not replaced).
$irs = 'f1040--2022', 'f1040s1--2022', 'f1040sc--2022', 'f1040sb--2022', 'f1040sd--2022', 'fw9--2018', 'fw4--2023', 'fw4--2021', 'f941--2022', 'f940--2022', 'f8949--2022', 'f1040nr--2022', 'f1065--2022', 'f1120--2022', 'f1040x--2022', 'f4868--2022', 'f2441--2022', 'f8812--2022', 'fss4--2022', 'f1099msc--2022', 'f1099nec--2022', 'fw2--2022', 'f8829--2022', 'f4562--2022', 'i1040gi--2022', 'p17--2022', 'iw4--2023', 'i941--2022'
foreach ($f in $irs) {
    Add-Gov "https://www.irs.gov/pub/irs-prior/$f.pdf" "irs-$f.pdf" @('gov', 'real-world', 'irs') "https://www.irs.gov/pub/irs-prior/$f.pdf" $evidence105
}

# Federal Register documents of one week, by length, so that the same query names the same files for ever.
$days = '2024-01-02', '2024-03-05', '2024-06-04', '2024-09-03'
$picked = 0
$buckets = @{ short = 0; medium = 0; long = 0 }
foreach ($day in $days) {
    $u = "https://www.federalregister.gov/api/v1/documents.json?per_page=200&order=oldest&conditions[publication_date][is]=$day&fields[]=pdf_url&fields[]=page_length&fields[]=document_number&fields[]=type"
    $r = Invoke-RestMethod $u -UserAgent 'MikePDF-corpus/1.0 (+https://github.com/mjtpena/MikePDF)'
    foreach ($d in $r.results) {
        $bucket = if ($d.page_length -le 3) { 'short' } elseif ($d.page_length -le 12) { 'medium' } else { 'long' }
        $limit = @{ short = 6; medium = 8; long = 8 }[$bucket]
        if ($buckets[$bucket] -ge $limit -or -not $d.pdf_url) { continue }
        $buckets[$bucket]++
        Add-Gov $d.pdf_url ("fr-$($day)-$($d.document_number).pdf") @('gov', 'real-world', 'text', 'federal-register') "https://www.federalregister.gov/d/$($d.document_number)" $evidence105
    }
}
$result | ConvertTo-Json -Depth 6 | Set-Content $Out
"gov: $($result.Count) files, $(($result | Measure-Object bytes -Sum).Sum) bytes"
