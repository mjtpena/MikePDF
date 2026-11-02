# Rewrites the manifest's "excluded" list: the sources that were looked at for the core tier (2026-11-03) and left out, each with the reason and what was checked.
# The six PDFium files left out by the smoke-tier audit stay (they were added by Apply-Audit earlier); this script keeps entries whose source starts with
# https://pdfium.googlesource.com/pdfium/+/43d55e4 and replaces the rest.
param([string]$Manifest)
. (Join-Path $PSScriptRoot 'Manifest.ps1')
$m = Read-Manifest $Manifest
$old = $m.Node['excluded'].AsArray()
# A JsonObject unrolls into key/value pairs in a PowerShell pipeline, so the entries are walked by index.
$keepNodes = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $old.Count; $i++) {
    $source = $old[$i]['source']
    if ($source -and $source.GetValue[string]() -like 'https://pdfium.googlesource.com/pdfium/+/43d55e4*') { $keepNodes.Add([System.Text.Json.Nodes.JsonNode]::Parse($old[$i].ToJsonString())) }
}
$list = @(
    @{ source = 'https://github.com/mozilla/pdf.js/tree/f70fabbe791d151b720451c239564237855814a5/test/pdfs'; licence = 'Apache-2.0 (repository only)'
       reason = 'Looked at 2026-11-03: 1,077 PDFs (156 MB) and 462 `.link` files that point at external downloads. The folder has no licence or provenance file at all; the files came from bug reports and web pages. The repository''s Apache-2.0 licence is not evidence that each file may be redistributed, so none is included. Taking only the files whose origin can be shown (for example files pdf.js developers generated) would mean reading each commit; not done.' }
    @{ source = 'https://github.com/veraPDF/veraPDF-corpus/tree/bb75f4f0073d9350dfd058c0162a367e6fadf25e'; licence = 'CC-BY-4.0 (README, whole repository)'
       reason = 'Included in the core tier as a sample (up to one pass and one fail file per clause directory, filtered by the font and producer rules in tests/corpus/curation). Left out: the `Isartor test files` folder (204 files, a separate suite whose licence the repository does not state), `TWG test files` (85) and `Undefined` (10) (no provenance stated per file), files over 1 MB, and files that embed proprietary font programs. About 2,350 other files were not sampled: most are near copies of the ones that were.' }
    @{ source = 'https://github.com/py-pdf/sample-files'; licence = 'CC-BY-SA-4.0'
       reason = 'All 38 PDFs are CC BY-SA 4.0. ShareAlike is not on the list ADR-0010''s amendment allows (BSD, Apache, MIT, CC0, public domain, CC BY). Nothing is adapted or redistributed here (the files are only fetched), so allowing ShareAlike later would cost nothing but the decision.' }
    @{ source = 'https://github.com/pdf-association/pdf20examples'; licence = 'CC-BY-SA-4.0'
       reason = 'The PDF Association publishes these PDF 2.0 examples under CC BY-SA 4.0 (LICENSE.md). ShareAlike is not on the allowed list. (The same association''s pdf-differences and safedocs repositories are Apache-2.0 and are included.)' }
    @{ source = 'https://github.com/pdfminer/pdfminer.six/tree/a18de2a9c479b4c847538500017b449ddaec177e/samples'; licence = 'none stated'
       reason = 'samples/README says the files "can be distributed freely but does not come with explicit licensing terms or source files", and credits third parties (an IRS form, the US Copyright Office, a Japanese gazette, two research papers, iText). No licence, so none is included; only samples/contrib/matplotlib.pdf has one ("released under the terms of the LICENSE file") and a single file was not worth a source.' }
    @{ source = 'https://github.com/pdfcpu/pdfcpu/tree/a21a372de6864eccd9e554df63c7950dfd1a4b47/pkg'; licence = 'Apache-2.0 (repository only)'
       reason = '507 PDFs (260 MB) under pkg/testdata and pkg/samples. The samples are made by pdfcpu from its test inputs, some of which are third-party documents (the folder has a licence file only for its fonts). No per-file provenance, so none is included.' }
    @{ source = 'https://github.com/ArtifexSoftware/ghostpdl (Ghostscript) and https://github.com/qpdf/qpdf'; licence = 'AGPL-3.0 (Ghostscript)'
       reason = 'Ghostscript is AGPL-3.0, which ADR-0003 forbids; its public repository has no separate test-file licence, and its larger PDF test set is private. qpdf (Apache-2.0) is included: 330 of its 637 qtest files after the font, producer and duplicate rules.' }
    @{ source = 'https://github.com/apache/pdfbox/tree/trunk/pdfbox/src/test/resources'; licence = 'Apache-2.0 (repository only)'
       reason = 'Not examined. Apache PDFBox is Apache-2.0, but many of its test PDFs are attachments from JIRA issues with no per-file record of who granted what. Left for a later pass.' }
    @{ source = 'https://chroniclingamerica.loc.gov/ (Library of Congress)'; licence = 'public domain for pre-1931 US newspapers (not verified per page)'
       reason = 'Tried: the site answers automated requests with a Cloudflare bot challenge (HTTP 403, "Just a moment..."), so the pages cannot be fetched by a job. Not worked around. Wikimedia Commons supplies scanned newspapers and books instead.' }
    @{ source = 'https://www.ato.gov.au/ (Australian Taxation Office forms)'; licence = 'CC-BY-3.0-AU / CC-BY-4.0 (not verified)'
       reason = 'ADR-0010 names them. Not done: the form URLs are replaced in place when a form is updated, so a hash pin would break, and the licence of each form was not read. IRS prior-year forms (public domain, frozen once issued) cover the business-form category instead.' }
    @{ source = 'https://digitalcorpora.org/corpora/file-corpora/govdocs1/ and SafeDocs / Common Crawl PDF datasets'; licence = 'not stated per file'
       reason = 'Still not examined. Bulk web crawls: no per-file licence, and documents may hold personal data (ADR-0010 decision 5). They belong to the full tier, which needs a per-document licence decision the project has not made.' }
    @{ source = 'Hostile files from oss-fuzz and bug trackers'; licence = 'not stated'
       reason = 'Third-party crash and fuzzing files have no licence. The two files in the smoke tier that take the engine down (a memory-exhausting form fan-out, a tiling pattern that never finishes) are generated by this repository instead.' }
    @{ source = 'https://arxiv.org/ (papers under the default arXiv licence)'; licence = 'arXiv non-exclusive distribution licence'
       reason = 'Most arXiv papers (101 of 182 sampled in one day''s harvest) carry the default licence, which lets arXiv distribute but not others. Only papers whose records name CC BY or CC0 are included; CC BY-NC, -ND and -SA papers are left out.' }
    @{ source = 'Wikimedia Commons PDFs over 6 MB, under CC BY-SA, or with a recorded restriction'; licence = 'various'
       reason = 'Not taken: the size cap keeps the nightly download small (Korean-titled books were all larger), ShareAlike is not allowed, and any file with a Restrictions entry on its page is skipped.' }
)
$m.Node['excluded'] = [System.Text.Json.Nodes.JsonArray]::new()
foreach ($e in $list) { $m.Node['excluded'].Add([System.Text.Json.Nodes.JsonNode]::Parse(($e | ConvertTo-Json -Compress))) }
foreach ($k in $keepNodes) { $m.Node['excluded'].Add($k) }
Write-Manifest $m
"excluded: $($m.Node['excluded'].AsArray().Count)"
