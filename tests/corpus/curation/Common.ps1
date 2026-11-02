# Helpers for building core-tier manifest entries (ADR-0010). Not part of the gate: these scripts are how the entries in manifest.json were made, so a reviewer can
# repeat the work. Everything they download goes to a cache folder outside the repository; no corpus file is ever committed.
#
#   . ./Common.ps1
#   $c = Get-GitHubCandidates -Repo qpdf/qpdf -Sha <commit> -Under qpdf/qtest/qpdf -Cache <dir>
#
# A "candidate" is a file that has been downloaded, hashed and scanned, with the facts the tagging and the licence rules need. Nothing here decides a licence:
# the per-source decisions are in curate-*.ps1 and in the manifest's licenceEvidence.

$ErrorActionPreference = 'Stop'
$CurationRoot = $PSScriptRoot

function Get-GitHubHeaders {
    $headers = @{ 'User-Agent' = 'mikepdf-corpus-curation' }
    $token = $env:GITHUB_TOKEN
    if (-not $token) { try { $token = (gh auth token 2>$null) } catch { } }
    if ($token) { $headers['Authorization'] = "Bearer $token" }
    $headers
}

# Facts about a PDF's bytes, from the raw text (compressed object streams are not looked into: a tag found only there is missed, never invented).
function Get-PdfFacts([byte[]]$Bytes) {
    $t = [System.Text.Encoding]::Latin1.GetString($Bytes)
    $pick = { param($rx) $m = [regex]::Match($t, $rx); if ($m.Success) { $m.Groups[1].Value } else { '' } }
    [pscustomobject]@{
        producer  = (& $pick '/Producer\s*\(([^)]{0,80})')
        creator   = (& $pick '/Creator\s*\(([^)]{0,80})')
        author    = (& $pick '/Author\s*\(([^)]{0,80})')
        title     = (& $pick '/Title\s*\(([^)]{0,80})')
        xmp       = $t.Contains('<x:xmpmeta')
        encrypt   = [regex]::IsMatch($t, '/Encrypt\s')
        acroform  = $t.Contains('/AcroForm')
        tagged    = $t.Contains('/StructTreeRoot') -or [regex]::IsMatch($t, '/MarkInfo\s*<<\s*/Marked\s+true')
        transp    = [regex]::IsMatch($t, '/SMask|/BM\s*/(?!Normal)|/CA\s+0?\.\d|/ca\s+0?\.\d|/S\s*/Transparency')
        images    = ([regex]::Matches($t, '/Subtype\s*/Image')).Count
        ccitt     = $t.Contains('/CCITTFaxDecode')
        jbig2     = $t.Contains('/JBIG2Decode')
        jpx       = $t.Contains('/JPXDecode')
        dct       = $t.Contains('/DCTDecode')
        fontfiles = ([regex]::Matches($t, '/FontFile[23]?\b')).Count
        cid       = [regex]::IsMatch($t, '/CIDFontType[02]|/Registry\s*\((Adobe)\)')
        cjk       = [regex]::IsMatch($t, '/Ordering\s*\((Japan1|GB1|CNS1|Korea1)\)|/Encoding\s*/(UniJIS|UniGB|UniCNS|UniKS|90ms|90pv|GBK|GBpc|GB-EUC|B5pc|ETen|KSC|EUC|Ext-RKSJ|Add-RKSJ)')
        outlines  = $t.Contains('/Outlines')
        linear    = $t.Contains('/Linearized')
        pagesHint = ([regex]::Matches($t, '/Type\s*/Page\b')).Count
        # Names of embedded font programs (the subset prefix removed), as far as they are visible in uncompressed objects.
        fonts     = @([regex]::Matches($t, '/FontName\s*/(?:[A-Z]{6}\+)?([^\s/\[\]<>()]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        objstm    = $t.Contains('/ObjStm')
        copyright = [regex]::IsMatch($t, '(?i)copyright\s*(\(c\)|©|\d{4})|all rights reserved|\(c\)\s*\d{4}')
        header    = if ($t.Length -ge 8) { $t.Substring(0, 8) } else { $t }
    }
}

function Get-FileSha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

# Downloads one URL to a path with retries; returns $true when the file is there.
function Save-Url([string]$Url, [string]$Path, [int]$Attempts = 4) {
    if (Test-Path -LiteralPath $Path) { return $true }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            Invoke-WebRequest -Uri $Url -OutFile ($Path + '.part') -UserAgent 'MikePDF-corpus/1.0 (+https://github.com/mjtpena/MikePDF; hash-pinned test corpus)' -TimeoutSec 600
            Move-Item -LiteralPath ($Path + '.part') -Destination $Path -Force
            return $true
        } catch {
            Start-Sleep -Seconds (2 * $i)
        }
    }
    return $false
}

# Lists the PDFs under a path of a GitHub repository at a commit (no clone), downloads them from raw.githubusercontent.com at that commit, hashes and scans them.
function Get-GitHubCandidates {
    param([string]$Repo, [string]$Sha, [string]$Under = '', [string]$Cache, [long]$MaxBytes = 5MB, [string]$Match = '\.pdf$', [string]$Exclude = '', [string[]]$OnlyPaths = $null, [string]$TreePath = '')
    # -TreePath lists just one folder's tree (for repositories too large to list whole); paths are then given from the repository root as usual.
    $treeRef = if ($TreePath) { "${Sha}:$TreePath" } else { $Sha }
    $tree = Invoke-RestMethod "https://api.github.com/repos/$Repo/git/trees/${treeRef}?recursive=1" -Headers (Get-GitHubHeaders)
    if ($tree.truncated) { throw "The tree of $Repo is truncated; list a subfolder." }
    if ($TreePath) { foreach ($e in $tree.tree) { $e.path = "$TreePath/$($e.path)" } }
    $blobs = $tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -match $Match -and ($Under -eq '' -or $_.path.StartsWith($Under + '/')) -and $_.size -le $MaxBytes -and $_.size -gt 0 -and ($Exclude -eq '' -or $_.path -notmatch $Exclude) -and ($null -eq $OnlyPaths -or $OnlyPaths -contains $_.path) }
    $dir = Join-Path $Cache ($Repo.Replace('/', '_'))
    $blobs | ForEach-Object -ThrottleLimit 8 -Parallel {
        . (Join-Path $using:CurationRoot 'Common.ps1')
        $url = 'https://raw.githubusercontent.com/' + $using:Repo + '/' + $using:Sha + '/' + (($_.path -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
        $dest = Join-Path $using:dir ($_.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [void](Save-Url $url $dest)
    }
    foreach ($b in $blobs) {
        $dest = Join-Path $dir ($b.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $dest)) { Write-Warning "not downloaded: $($b.path)"; continue }
        $bytes = [IO.File]::ReadAllBytes($dest)
        if ($bytes.Length -ne $b.size) { Write-Warning "size differs: $($b.path)"; continue }
        $facts = Get-PdfFacts $bytes
        [pscustomobject]@{
            path = $b.path; bytes = $bytes.Length; sha256 = (Get-FileSha256 $dest)
            fetchUrl = 'https://raw.githubusercontent.com/' + $Repo + '/' + $Sha + '/' + (($b.path -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
            source = "https://github.com/$Repo/blob/$Sha/" + (($b.path -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
            local = $dest; facts = $facts
        }
    }
}
