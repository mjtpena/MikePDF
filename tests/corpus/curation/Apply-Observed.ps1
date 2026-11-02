# Writes what a first run of new core-tier files showed into their manifest entries (expect.status and pages, the multi-page and huge-page-count tags, which pages
# of a long document to render), the way the smoke tier's expectations were first set: they are regression expectations, reviewed by a person, not claims about
# what the files "should" do. A real-world file that does not open is marked knownIssue so that it counts against the open rate instead of hiding in the expected
# failures; a file broken on purpose (a test suite's parser tests) is just expected to fail. Engine crashes and hangs are not written down: they are findings.
#
#   ./Apply-Observed.ps1 -Report <render-out/report.json> -Manifest <tests/corpus/manifest.json>
param([string]$Report, [string]$Manifest)
. (Join-Path $PSScriptRoot 'Manifest.ps1')
$run = Get-Content $Report -Raw | ConvertFrom-Json
$m = Read-Manifest $Manifest
$byId = @{}; foreach ($f in $run.files) { $byId[$f.id] = $f }
$realWorld = 'gov/', 'commons/', 'arxiv/'
$changed = 0; $findings = New-Object System.Collections.Generic.List[string]
foreach ($e in $m.Node['files'].AsArray()) {
    $id = $e['id'].GetValue[string]()
    $tiers = @($e['tiers'].AsArray() | ForEach-Object { $_.GetValue[string]() })
    if ($tiers -contains 'smoke' -or $tiers -notcontains 'core' -or -not $byId.ContainsKey($id)) { continue }
    $r = $byId[$id]
    $status = $r.open.status
    if ($r.engineEvents | Where-Object { $_.kind -eq 'crashed' }) { $findings.Add("$id : engine event(s): " + (($r.engineEvents | Where-Object { $_.kind -eq 'crashed' } | ForEach-Object { $_.reason }) -join ',')) }
    $expect = $e['expect'].AsObject()
    $expect['status'] = $status
    $expect['opens'] = ($status -eq 'Ok')
    if ($status -eq 'Ok') {
        $expect['pages'] = [int]$r.pageCount
        if ($r.pages | Where-Object { $_.status -eq 'Damaged' }) { $expect['allowsDamagedPages'] = $true }
        $tags = New-Object System.Collections.Generic.List[string]; foreach ($t in $e['tags'].AsArray()) { $tags.Add($t.GetValue[string]()) }
        if ($r.pageCount -gt 1 -and -not $tags.Contains('multi-page')) { $tags.Add('multi-page') }
        if ($r.pageCount -ge 500 -and -not $tags.Contains('huge-page-count')) { $tags.Add('huge-page-count') }
        $tagJson = if ($tags.Count -gt 0) { @($tags | Sort-Object -Unique) | ConvertTo-Json -Compress -AsArray } else { '[]' }
        $e['tags'] = [System.Text.Json.Nodes.JsonNode]::Parse($tagJson)
        if ($r.pageCount -gt 40) {
            $n = [int]$r.pageCount
            $render = [System.Text.Json.Nodes.JsonNode]::Parse((@{ pages = "1-3,$([int][Math]::Floor($n / 2)),$n" } | ConvertTo-Json -Compress))
            $e['render'] = $render
        }
    } elseif ($status -ne 'PasswordRequired' -and ($realWorld | Where-Object { $id.StartsWith($_) })) {
        $expect['knownIssue'] = "Opening ended with $status when the file was added (PDFium $($run.machine.pdfiumVersion)): $($r.open.detail)"
    }
    $changed++
}
# Tags the content scan cannot give: a file the engine could not open is a broken file; test-suite names that say what the file is about.
foreach ($e in $m.Node['files'].AsArray()) {
    $tiers = @($e['tiers'].AsArray() | ForEach-Object { $_.GetValue[string]() })
    if ($tiers -notcontains 'core') { continue }
    $id = $e['id'].GetValue[string]()
    $tags = New-Object System.Collections.Generic.HashSet[string]; foreach ($t in $e['tags'].AsArray()) { [void]$tags.Add($t.GetValue[string]()) }
    $before = $tags.Count
    if ($e['expect']['status'].GetValue[string]() -eq 'Damaged') {
        [void]$tags.Add('broken-structure')
        if ($id -match 'xref|trailer|startxref|offset') { [void]$tags.Add('broken-xref') }
    }
    if ($id -match 'annot') { [void]$tags.Add('annotation') }
    if ($id -match '(^|[/_-])(form|field|fields|button|checkbox|combobox|listbox|radio)([._-]|$)') { [void]$tags.Add('form') }
    if ($tags.Count -ne $before) {
        $e['tags'] = [System.Text.Json.Nodes.JsonNode]::Parse((@($tags | Sort-Object) | ConvertTo-Json -Compress -AsArray))
    }
}
Write-Manifest $m
"updated $changed entries"
$findings
