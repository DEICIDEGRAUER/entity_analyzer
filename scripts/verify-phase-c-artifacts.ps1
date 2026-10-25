# Phase C frozen-artifact guard.
#
# Pins the committed labels, run manifest, corpus-slice manifest, and cross-
# lingual pair fixture, plus the CLOSED/NO-GO/EXPLORATORY scope markers in
# docs/PHASE_C.md section 5.6 and the paraphrase-feasibility/lesson markers in
# sections 5.8-5.9. This is intentionally separate from the model-gated
# evaluator: it runs in seconds, needs no corpus text or ONNX weights, and fails
# if a frozen input or the recorded decision was edited.
#
# Optional checks (not committed, so skipped when absent):
#   $env:EA_PHASE_C_ENTITIES_TABLE  path to the pinned phase-c-1000 entities_table.csv
#   $env:EA_PHASE_C_OUTPUT_DIR      directory containing semantic_neighbors.csv
#
# Git may check out the same blob with LF or CRLF depending on platform and
# autocrlf. Hashes therefore match the raw bytes or either pure line-ending
# normalization; a real content edit still fails.

[CmdletBinding()]
param(
    [string]$EntitiesTable,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $scriptDir
$checks = 0

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-Sha256Bytes([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Bytes)
        return ([System.BitConverter]::ToString($hash)).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-EolHashVariants([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $lfText = $text.Replace("`r`n", "`n")
    $lfHash = Get-Sha256Bytes ([System.Text.Encoding]::UTF8.GetBytes($lfText))
    $crlfText = $lfText.Replace("`n", "`r`n")
    $crlfHash = Get-Sha256Bytes ([System.Text.Encoding]::UTF8.GetBytes($crlfText))
    return @($lfHash, $crlfHash)
}

function Test-ExpectedHash([string]$Path, [string]$Expected) {
    $actual = Get-Sha256 $Path
    if ($actual -eq $Expected.ToUpperInvariant()) {
        return $actual
    }

    $variants = Get-EolHashVariants $Path
    if ($variants -contains $Expected.ToUpperInvariant()) {
        return $actual
    }

    throw "Phase C frozen artifact changed: expected $Expected, got $actual (LF/CRLF variants: $($variants -join ', '))"
}

function Assert-FileHash([string]$RelativePath, [string]$Expected, [string]$Label) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing frozen Phase C artifact: $RelativePath"
    }

    $actual = Get-Sha256 $path
    if ($actual -ne $Expected.ToUpperInvariant()) {
        $variants = Get-EolHashVariants $path
        if ($variants -notcontains $Expected.ToUpperInvariant()) {
            throw "Phase C frozen artifact changed: $Label ($RelativePath) expected $Expected, got $actual (LF/CRLF variants: $($variants -join ', '))"
        }
        $note = " (line-ending normalized; expected $Expected)"
    } else {
        $note = ''
    }

    $script:checks++
    Write-Output "OK   $Label $actual$note"
}

function Test-OptionalFileHash(
    [string]$Path,
    [string]$Expected,
    [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Output "SKIP $Label (not present)"
        return
    }

    $actual = Test-ExpectedHash $Path $Expected
    $script:checks++
    Write-Output "OK   $Label $actual"
}

Assert-FileHash `
    'test-fixtures/phase-c-corpus-1000/labels/labels-primary.jsonl' `
    'E81CBC59C14BE84A6F65A2DB9B231E358FD6F829A474CA76C2A75BFD5ADE1280' `
    'labels-primary.jsonl'

Assert-FileHash `
    'test-fixtures/phase-c-corpus-1000/labels/labels-track-a.jsonl' `
    'F820DE0915EE563F5E1801839E664C05E8470BC061FA5DB95DCFD6CD3D0A3E85' `
    'labels-track-a.jsonl'

Assert-FileHash `
    'test-fixtures/phase-c-corpus-1000/labels/labels.meta.json' `
    '47ACF677715FA4BB05769029F20C09F9B5D18B0FC798B7DCDEAEC129B1132BFC' `
    'labels.meta.json'

Assert-FileHash `
    'test-fixtures/phase-c-corpus-1000/phase-c-manifest.json' `
    'E76DDB9F20E74D6BB1AD8B7CB879CD94C96321A733E6F6A2354278ED62703CDF' `
    'phase-c-manifest.json'

Assert-FileHash `
    'test-fixtures/phase-c-corpus-1000/manifest.json' `
    '77D348AB5CFB13E5031EB1DBAD809DA0B7067C778E2F12BAA44725CD76BEDF6B' `
    'phase-c-1000 manifest.json'

Assert-FileHash `
    'test-fixtures/phase-c-corpus-1000/cross-lingual-pairs.json' `
    '865D98328E071B760522E8F1B39264B484DFF953C9F9E883B27168E73380C289' `
    'cross-lingual-pairs.json'

# Pin the operational scope markers, so an accidental docs edit cannot silently
# turn CLOSED back into an active GO or hide the alias/feasibility caveats.
$phaseCPath = Join-Path $root 'docs/PHASE_C.md'
if (-not (Test-Path -LiteralPath $phaseCPath -PathType Leaf)) {
    throw 'Missing Phase C decision document: docs/PHASE_C.md'
}
$phaseCText = Get-Content -LiteralPath $phaseCPath -Raw -Encoding UTF8

function Get-Section([string]$Text, [string]$StartPattern, [string]$EndPattern, [string]$Label) {
    $match = [regex]::Match($Text, "(?ms)^$StartPattern\b.*?(?=^(?:$EndPattern))")
    if (-not $match.Success) {
        throw "PHASE_C.md section $Label not found"
    }
    return [regex]::Replace($match.Value, '\s+', ' ')
}

$scopeForMarkers = Get-Section $phaseCText '### 5\.6' '### 5\.7\b|## 6\.' '5.6'
$requiredScopeMarkers = @(
    'CLOSED',
    'Phase C delivered the infrastructure',
    'current corpus does not justify the feature',
    'delivered, not currently needed',
    'alias coverage, not semantic retrieval',
    'ONNX semantic capability remains untested on this corpus',
    'Historical matrix verdict was **GO (full)**',
    'not an active ONNX approval',
    'Exact alias lookup (catalog-aware)',
    'fallback, not a Phase C feature',
    'Entity graph (Track A)',
    'Full-context retrieval',
    'semantic_neighbors.csv',
    'EXPLORATORY',
    'Paraphrase benchmark',
    'FAIL',
    'feasibility',
    'no longer a Phase C precondition',
    'entity-graph backlog',
    'do not auto-delete'
)
foreach ($marker in $requiredScopeMarkers) {
    if (-not $scopeForMarkers.Contains($marker)) {
        throw "PHASE_C.md section 5.6 scope decision changed: missing '$marker'"
    }
}
$noGoCount = ([regex]::Matches($scopeForMarkers, 'NO-GO')).Count
if ($noGoCount -lt 3) {
    throw "PHASE_C.md section 5.6 must contain at least 3 NO-GO markers, found $noGoCount"
}
$goCount = ([regex]::Matches($scopeForMarkers, '(?<!NO-)GO')).Count
if ($goCount -lt 1) {
    throw "PHASE_C.md section 5.6 must preserve the historical GO (full) marker"
}
$closedCount = ([regex]::Matches($scopeForMarkers, 'CLOSED')).Count
if ($closedCount -lt 2) {
    throw "PHASE_C.md section 5.6 must contain CLOSED markers, found $closedCount"
}
$script:checks++
Write-Output "OK   PHASE_C.md section 5.6 scope markers ($goCount historical GO, $noGoCount NO-GO, $closedCount CLOSED)"

$feasibilityForMarkers = Get-Section $phaseCText '### 5\.8' '### 5\.9\b|## 6\.' '5.8'
$requiredFeasibilityMarkers = @(
    'author-side feasibility probe',
    '24/30',
    '16/30',
    '10/30',
    'FAIL',
    'paraphrase benchmark',
    'No labels were created',
    'no scoring',
    'exact matching',
    'similarity'
)
foreach ($marker in $requiredFeasibilityMarkers) {
    if (-not $feasibilityForMarkers.Contains($marker)) {
        throw "PHASE_C.md section 5.8 feasibility decision changed: missing '$marker'"
    }
}
$script:checks++
Write-Output "OK   PHASE_C.md section 5.8 paraphrase-feasibility markers (24/30 natural, 16/30 unambiguous, 10/30 clean)"

$lessonForMarkers = Get-Section $phaseCText '### 5\.9' '## 6\.' '5.9'
$requiredLessonMarkers = @(
    'Pre-registration protected against motivated reasoning.',
    'It did not protect against an unmodeled cheap baseline.',
    'Future pre-registrations must include at least one cheap non-ML baseline (exact match, alias normalization, BM25) before declaring a gate.',
    'ONNX justified',
    'marginal',
    'not justified',
    'no such run'
)
foreach ($marker in $requiredLessonMarkers) {
    if (-not $lessonForMarkers.Contains($marker)) {
        throw "PHASE_C.md section 5.9 lesson/threshold decision changed: missing '$marker'"
    }
}
$script:checks++
Write-Output "OK   PHASE_C.md section 5.9 pre-registration lesson and Delta thresholds"

# The slice extraction input is private and not committed. Its hash is still
# pinned in phase-c-manifest.json; check that embedded value, and check the
# actual file only when a local caller supplies it.
$manifestPath = Join-Path $root 'test-fixtures/phase-c-corpus-1000/phase-c-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedSliceHash = '1561C5949C32B3F49C32B49D66A4BCECCAE61118AB604C4EC4926F0E55337FB5'
if ($manifest.extraction.entities_table_sha256 -ne $expectedSliceHash) {
    throw "phase-c-manifest.json pins the wrong entities_table hash: $($manifest.extraction.entities_table_sha256)"
}
Write-Output "OK   embedded entities_table slice hash $expectedSliceHash"

if ([string]::IsNullOrWhiteSpace($EntitiesTable)) {
    $EntitiesTable = $env:EA_PHASE_C_ENTITIES_TABLE
}
Test-OptionalFileHash $EntitiesTable $expectedSliceHash 'entities_table.csv (slice, optional)'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = $env:EA_PHASE_C_OUTPUT_DIR
}
$csvPath = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $null
} else {
    Join-Path $OutputDirectory 'semantic_neighbors.csv'
}
Test-OptionalFileHash `
    $csvPath `
    '96C3F1BBFD9A1EB2C1898E13059A2BC83DCDFDC1E862A306D8E8BCE6A3AC2D59' `
    'semantic_neighbors.csv (optional)'

Write-Output "Phase C frozen artifacts OK ($checks committed checks + embedded manifest check)"
exit 0
