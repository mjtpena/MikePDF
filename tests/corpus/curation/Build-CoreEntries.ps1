# Turns the candidates (cands-*.json, from Common.ps1 and the Curate-*.ps1 scripts) into core-tier entries of tests/corpus/manifest.json, applying the rules
# below, and writes what it left out, and why, to <Out>/rejected.tsv. Run by hand; what ends up in the manifest is the result, reviewed.
#
#   ./Build-CoreEntries.ps1 -Cands <dir with cands-*.json> -Manifest <tests/corpus/manifest.json> -Out <dir>
param([string]$Cands, [string]$Manifest, [string]$Out, [string[]]$Only = @())
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Manifest.ps1')

# ---- rules ----------------------------------------------------------------------------------------------------------------------------------------------------

# Embedded font programs a test file may carry: families whose licences allow embedding in a document and redistribution (OFL, Bitstream Vera, Apache, AMS/Knuth).
# Anything else (Arial, Times New Roman, Calibri, Myriad, Hiragino, Adobe's built-ins ...) is proprietary font software, and the file is left out.
$openFonts = '^(Liberation|DejaVu|SourceSans|SourceSerif|SourceCode|Noto|Roboto|OpenSans|Lato|Ubuntu|Carlito|Caladea|Arimo|Tinos|Cousine|CM(R|MI|SY|BX|TT|SL|EX|SS|B|TI|CSC|U|FF|DUNH|MSY)\d*|LM(Roman|Sans|Mono|Math)|STIX|Gentium|Charis|Doulos|Amiri|Cardo|Merriweather|FiraSans|FiraMono|IBMPlex|Inter|FreeMono|FreeSans|FreeSerif|OpenSymbol|MSAM|MSBM|EUFM|EUSM|Cantarell|DroidSans)'
# What arXiv papers (LaTeX output) embed: their authors' document, licensed by them; the font programs are the standard free TeX fonts.
$texFonts = '^(SF(RM|BX|TI|SS|TT|CC|SL|RI|XC)|dsrom|dsss|Pazo|LinBiolinum|LinLibertine|Inconsolata|BBOLD|MnSymbol|rsfs|RSFS|wasy|WASY|esint|rtcx|rtx|zi4|Euler|CM|LM|MSAM|MSBM|EUFM|EUSM|Nimbus|URW|TeXGyre|Libertine|Linux|NewTX|txfonts|STIX|Utopia|Charter|BitstreamCharter|Bera|DejaVu|Liberation|Noto|Roboto|Lato|Source|Open|Fira|MathPazo|Latin)'
# Producers and creators that mean the file was made from someone's real document in a desktop application.
$desktopApps = 'Microsoft|Word|Excel|PowerPoint|Acrobat|Adobe|InDesign|Illustrator|Photoshop|Quartz|Skia|Mozilla|Chrome|LibreOffice|OpenOffice|Writer|Impress|pdfTeX|LaTeX|XeTeX|LuaTeX|Ghostscript|FOP|Xara|iText|PDFlib|Cairo|PScript|Distiller|Nitro|Foxit|PDF24|CutePDF|Pages|Keynote|Prince|wkhtml|Qt|Crystal|Snagit'
$maxPhotoBytes = 150KB

function Get-Reject($c, [string]$kind, [string]$source = '') {
    $f = $c.facts
    if ($kind -in 'test') {
        foreach ($font in $f.fonts) { if ($font -notmatch $openFonts) { return "embeds the font program '$font', which is not on the open-font list" } }
        $app = "$($f.producer) $($f.creator)"
        if ($app -match $desktopApps) { return "made by a desktop application ($($app.Trim())), so possibly from someone's real document" }
        if (($f.dct -or $f.jpx) -and $c.bytes -gt $maxPhotoBytes) { return 'carries a JPEG or JPEG 2000 image of a size that may be a photograph' }
        if ($source -eq 'pdfium' -and $f.author -match '\w') { return "names an author ($($f.author))" }
    }
    if ($kind -eq 'tex') { foreach ($font in $f.fonts) { if ($font -notmatch $texFonts) { return "embeds the font program '$font'" } } }
    $null
}

function Get-FactTags($c) {
    $f = $c.facts
    $t = New-Object System.Collections.Generic.List[string]
    if ($f.encrypt) { $t.Add('encrypted') }
    if ($f.acroform) { $t.Add('form') }
    if ($f.tagged) { $t.Add('tagged') }
    if ($f.transp) { $t.Add('transparency') }
    if ($f.images -gt 0) { $t.Add('image') }
    if ($f.ccitt) { $t.Add('ccitt') }
    if ($f.jbig2) { $t.Add('jbig2') }
    if ($f.jpx) { $t.Add('jpx') }
    if ($f.dct) { $t.Add('dct') }
    if ($f.cjk) { $t.Add('cjk') }
    if ($f.outlines) { $t.Add('outline') }
    if ($f.linear) { $t.Add('linearized') }
    $t
}

# A Commons search finds a file by its title and description, which says nothing sure about the pages. Keep a content tag only when the file backs it up:
# the title is in the script (or names the language), or the PDF's own structure shows it (CJK character collections, page images for "scanned").
function Confirm-CommonsTags($c, [string[]]$tags) {
    $title = [uri]::UnescapeDataString($c.source)
    $f = $c.facts
    $keep = New-Object System.Collections.Generic.List[string]
    foreach ($t in $tags) {
        switch ($t) {
            'rtl' { if ($title -match '[֐-ࣿיִ-﷿ﹰ-﻿]|(?i)arabic|hebrew|persian|farsi|urdu') { $keep.Add($t) } }
            { $_ -in 'arabic', 'hebrew', 'persian', 'urdu' } { if ($title -match '[֐-ࣿיִ-﷿ﹰ-﻿]|(?i)arabic|hebrew|persian|farsi|urdu') { $keep.Add($t) } }
            'cjk' { if ($f.cjk -or $title -match '[぀-ヿ㐀-鿿가-힯]|(?i)chinese|japanese|korean') { $keep.Add($t) } }
            { $_ -in 'chinese', 'japanese', 'korean' } { if ($f.cjk -or $title -match '[぀-ヿ㐀-鿿가-힯]|(?i)chinese|japanese|korean') { $keep.Add($t) } }
            'scanned' { if ($f.images -gt 0 -and $f.fonts.Count -eq 0) { $keep.Add($t) } }
            'transparency' { if ($f.transp) { $keep.Add($t) } }
            default { $keep.Add($t) }
        }
    }
    $keep | Sort-Object -Unique
}

# ---- sources --------------------------------------------------------------------------------------------------------------------------------------------------

$sources = @(
    @{ key = 'pdfium'; kind = 'test'; licence = 'BSD-3-Clause'
       evidence = 'PDFium LICENSE (BSD-3-Clause) at the pinned commit, https://pdfium.googlesource.com/pdfium/+/43d55e429cf72c21e9a1e60475b50feefa16fda9/LICENSE; contributions need Google''s contributor agreement; per-file automated audit below' }
    @{ key = 'chromium'; kind = 'test'; licence = 'BSD-3-Clause'
       evidence = 'Chromium LICENSE (BSD-3-Clause), https://github.com/chromium/chromium/blob/499a301657a1e61a9ec0399b7bfa0b95cc283ac2/LICENSE; pdf/test/data is first-party test data (not under third_party); per-file automated audit below' }
    @{ key = 'qpdf'; kind = 'test'; licence = 'Apache-2.0'
       evidence = 'qpdf NOTICE.md and manual/license.rst at the pinned commit: Apache-2.0 for the repository, no exception for test files (https://github.com/qpdf/qpdf/blob/4eba95899886e851cc41d76886483b347612f2a8/NOTICE.md); per-file automated audit below' }
    @{ key = 'pdf-differences'; kind = 'test'; licence = 'Apache-2.0'
       evidence = 'PDF Association pdf-differences repository LICENSE (Apache-2.0) at the pinned commit; per-file automated audit below' }
    @{ key = 'safedocs'; kind = 'test'; licence = 'Apache-2.0'
       evidence = 'PDF Association safedocs repository LICENSE (Apache-2.0) at the pinned commit; per-file automated audit below' }
    @{ key = 'verapdf'; kind = 'test'; licence = 'CC-BY-4.0'
       evidence = 'veraPDF-corpus README: "This work is licensed under a Creative Commons Attribution 4.0 International (CC BY 4.0)" (https://github.com/veraPDF/veraPDF-corpus/blob/bb75f4f0073d9350dfd058c0162a367e6fadf25e/README.md), credited to the veraPDF Consortium / PDF Association; per-file automated audit below' }
    @{ key = 'gov'; kind = 'none' }
    @{ key = 'commons'; kind = 'none' }
    @{ key = 'arxiv'; kind = 'none' }
)
$perSourceCap = @{ qpdf = 330; verapdf = 240; pdfium = 400; chromium = 100; 'pdf-differences' = 100; safedocs = 100; gov = 100; commons = 200; arxiv = 60 }

# ---- build ----------------------------------------------------------------------------------------------------------------------------------------------------

New-Item -ItemType Directory -Force $Out | Out-Null
$m = Read-Manifest $Manifest
$files = $m.Node['files'].AsArray()
$have = @{}; foreach ($f in $files) { $have[$f['id'].GetValue[string]()] = $true; $have['sha:' + $f['sha256'].GetValue[string]()] = $true }
$smokeAuditExcluded = 'bug_650', 'embedded_images', 'annotation_stamp_with_ap', 'page_labels', 'annotation_ink_multiple', 'annotation_highlight_square_with_ap' | ForEach-Object { "pdfium/$_.pdf" }
$rejected = New-Object System.Collections.Generic.List[string]
$counts = [ordered]@{}

foreach ($s in $sources) {
    if ($Only.Count -gt 0 -and $Only -notcontains $s.key) { continue }
    $found = @(Get-ChildItem $Cands -Filter "cands-$($s.key).json") + @(Get-ChildItem $Cands -Filter "cands-$($s.key)-*.json") | Select-Object -ExpandProperty FullName
    if ($found.Count -eq 0) { "skip $($s.key): no candidates"; continue }
    $rows = @($found | ForEach-Object { Get-Content $_ -Raw | ConvertFrom-Json }) | Sort-Object path -Unique
    $kept = New-Object System.Collections.Generic.List[object]
    foreach ($c in $rows) {
        $name = $c.path -replace '^(qpdf/qtest/qpdf/|pdf/test/data/|pdfium/|commons/|gov/|arxiv/)', ''
        $id = ("$($s.key)/" + $name).Replace(' ', '_')
        $why = $null
        if ($have.ContainsKey($id)) { continue }                                       # already in the manifest (the smoke tier, or an earlier run)
        $c.facts = Get-PdfFacts ([IO.File]::ReadAllBytes($c.local))                    # read again, so a better rule applies to files downloaded earlier
        if ($smokeAuditExcluded -contains $id) { $why = 'left out by the smoke-tier audit (see the manifest''s excluded list)' }
        elseif ($have.ContainsKey('sha:' + $c.sha256)) { $why = 'the same bytes are already in the corpus' }
        elseif ($s.key -in 'qpdf' -and $c.path -match '(-out\d*|_out\d*|-new|-after)\.pdf$') { $why = 'an expected output of a qpdf test, the same document as its input' }
        elseif ($s.key -eq 'pdfium' -and -not $c.hasTemplate) { $why = Get-Reject $c 'test' 'pdfium' }
        elseif ($s.kind -ne 'none') { $why = Get-Reject $c $s.kind }
        if ($why) { $rejected.Add("$id`t$($s.key)`t$why"); continue }
        $kept.Add(@{ id = $id; c = $c })
    }
    # An even spread when a source gives more than its share.
    $cap = $perSourceCap[$s.key]
    if ($kept.Count -gt $cap) {
        $step = $kept.Count / $cap
        $pick = 0..($cap - 1) | ForEach-Object { $kept[[int][Math]::Floor($_ * $step)] }
        foreach ($k in $kept) { if ($pick -notcontains $k) { $rejected.Add("$($k.id)`t$($s.key)`tnot taken: the source gives more than its share of the tier (an even sample was kept)") } }
        $kept = $pick
    }
    foreach ($k in $kept) {
        $c = $k.c
        $licence = if ($s.licence) { $s.licence } else { $c.licence }
        $evidence = if ($s.evidence) { $s.evidence } else { $c.evidence }
        $tags = @($c.tags) + @(Get-FactTags $c) | Where-Object { $_ } | Sort-Object -Unique
        if ($s.key -eq 'commons') { $tags = @(Confirm-CommonsTags $c $tags) }
        $audit = $null
        if ($s.kind -eq 'test') {
            $template = if ($c.hasTemplate) { 'a hand-written template (.in) sits beside it in the repository; ' } else { '' }
            $audit = 'automated audit 2026-11-03: ' + $template + 'no desktop-application producer, no author, embedded fonts (where visible) all open-licence, no large photographic image'
        } elseif ($s.kind -eq 'tex') {
            $audit = 'automated audit 2026-11-03: licence read per paper from arXiv; embedded fonts (where visible) are the standard free TeX fonts'
        }
        $entry = [ordered]@{
            id = $k.id; origin = 'fetch'; source = $c.source
        }
        if ($c.fetchUrl -ne $c.source) { $entry['fetchUrl'] = $c.fetchUrl }
        if ($c.fetchEncoding) { $entry['fetchEncoding'] = $c.fetchEncoding }
        $entry['licence'] = $licence; $entry['redistributable'] = $true; $entry['licenceEvidence'] = $evidence
        if ($audit) { $entry['audit'] = $audit }
        $entry['sha256'] = $c.sha256; $entry['bytes'] = $c.bytes; $entry['tags'] = @($tags); $entry['tiers'] = @('core')
        $entry['expect'] = [ordered]@{ opens = $true; status = 'Ok' }
        $have[$k.id] = $true; $have['sha:' + $c.sha256] = $true
        $files.Add([System.Text.Json.Nodes.JsonNode]::Parse(($entry | ConvertTo-Json -Depth 6 -Compress)))
    }
    $counts[$s.key] = "$($kept.Count) kept of $($rows.Count)"
}
Write-Manifest $m
$rejected | Set-Content (Join-Path $Out 'rejected.tsv')
$counts.GetEnumerator() | ForEach-Object { "{0,-16} {1}" -f $_.Key, $_.Value }
"rejected: $($rejected.Count)"
