# Phase C frozen-artifact guard.
#
# Pins the committed labels, run manifest, corpus-slice manifest, and cross-
# lingual pair fixture. This is intentionally separate from the model-gated
# evaluator: it runs in seconds, needs no corpus text or ONNX weights, and
# fails if a frozen input was edited after the labels were scored.
#
# Optional checks (not committed, so skipped when absent):
#   $env:EA_PHASE_C_ENTITIES_TABLE  path to the pinned phase-c-1000 entities_table.csv
#   $env:EA_PHASE_C_OUTPUT_DIR      directory containing semantic_neighbors.csv

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

function Assert-FileHash([string]$RelativePath, [string]$Expected, [string]$Label) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing frozen Phase C artifact: $RelativePath"
    }

    $actual = Get-Sha256 $path
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "Phase C frozen artifact changed: $Label ($RelativePath) expected $Expected, got $actual"
    }

    $script:checks++
    Write-Output "OK   $Label $actual"
}

function Test-OptionalFileHash(
    [string]$Path,
    [string]$Expected,
    [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Output "SKIP $Label (not present)"
        return
    }

    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "Phase C frozen artifact changed: $Label expected $Expected, got $actual"
    }

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
