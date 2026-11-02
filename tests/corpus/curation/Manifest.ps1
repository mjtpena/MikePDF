# Helpers for editing tests/corpus/manifest.json with the same formatting the tool writes (2-space indent, camelCase, nulls omitted, CRLF as the file has).
function Read-Manifest([string]$Path) {
    $raw = Get-Content $Path -Raw
    [pscustomobject]@{ Node = [System.Text.Json.Nodes.JsonNode]::Parse($raw); Crlf = $raw.Contains("`r`n"); Path = $Path }
}
function Write-Manifest($M) {
    $opt = [System.Text.Json.JsonSerializerOptions]::new(); $opt.WriteIndented = $true
    $opt.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    $opt.TypeInfoResolver = [System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver]::new()
    $json = $M.Node.ToJsonString($opt).Replace("`r`n", "`n")
    $eol = if ($M.Crlf) { "`r`n" } else { "`n" }
    if ($M.Crlf) { $json = $json.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($M.Path, $json.TrimEnd() + $eol, [Text.UTF8Encoding]::new($false))
}
function ConvertTo-Node($Object) { [System.Text.Json.Nodes.JsonNode]::Parse(($Object | ConvertTo-Json -Depth 8 -Compress)) }
