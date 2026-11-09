# Generator-policy frozen trace guard.
#
# Criterion v2 is not only documentation. This guard pins the 198 output-level
# trace events (164 approved additions, 29 same-context re-attributions, and 5
# nested-span improvements) and their classification vocabulary. It fails fast
# if the accepted trace is edited silently after the criteria met.
#
# The CSV may be checked out with LF or CRLF; the hash accepts either pure
# line-ending normalization. A real content edit still fails.
#
# This deliberately does not rerun the corpus: the corpus run is a separate
# selfcheck that writes OUTPUT_GENERATOR_TRACE.csv. This guard runs in seconds
# and is meaningful both before and after the corpus run.

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

function Assert-TraceHash([string]$RelativePath, [string]$Expected) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing frozen generator trace: $RelativePath"
    }

    $actual = Get-Sha256 $path
    if ($actual -ne $Expected.ToUpperInvariant()) {
        $variants = Get-EolHashVariants $path
        if ($variants -notcontains $Expected.ToUpperInvariant()) {
            throw "Generator trace changed silently: $RelativePath expected $Expected, got $actual (LF/CRLF variants: $($variants -join ', '))"
        }
    }

    $script:checks++
    Write-Output "OK   frozen trace csv $actual"
}

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label mismatch: expected $Expected, got $Actual"
    }
    $script:checks++
    Write-Output "OK   $Label $Actual"
}

function Get-Count([hashtable]$Map, [string]$Key) {
    if (-not $Map.ContainsKey($Key)) { return 0 }
    return $Map[$Key]
}

$tracePath = 'docs/OUTPUT_GENERATOR_TRACE.csv'
Assert-TraceHash `
    $tracePath `
    '1B72B940802750B2E0C3861BCEA2366767E721B534BDD2DC6FC2B763FD358816'

# Parse and classify. This is the actual criterion-v2 assertion list applied to
# the frozen trace; not just a document claim.
$trace = Join-Path $root $tracePath
$rows = Import-Csv -LiteralPath $trace
Assert-Equal $rows.Count 198 'frozen trace row count'

$byKind = @{}
$byClassification = @{}
foreach ($row in $rows) {
    if (-not $byKind.ContainsKey($row.kind)) {
        $byKind[$row.kind] = 0
    }
    $byKind[$row.kind]++
    if (-not $byClassification.ContainsKey($row.classification)) {
        $byClassification[$row.classification] = 0
    }
    $byClassification[$row.classification]++

    if ($row.contextHash -notmatch '^[0-9A-F]{64}$') {
        throw "Frozen trace contains a malformed contextHash: '$($row.contextHash)'"
    }
    if ([string]::IsNullOrWhiteSpace($row.entity) -or [string]::IsNullOrWhiteSpace($row.plusVariant)) {
        throw "Frozen trace contains an incomplete row: $(ConvertTo-Json -InputObject $row -Compress)"
    }
    if ([string]::IsNullOrWhiteSpace($row.file)) {
        throw "Frozen trace is missing the source file: $(ConvertTo-Json -InputObject $row -Compress)"
    }
    if ([int]$row.line -lt 1 -or [long]$row.offset -lt 0) {
        throw "Frozen trace has a non-source line/offset: $(ConvertTo-Json -InputObject $row -Compress)"
    }
}
Assert-Equal $byKind['added'] 193 'added rows'
Assert-Equal $byKind['removed'] 5 'removed rows'

Assert-Equal (Get-Count $byClassification 'approved_addition') 164 'approved additions'
Assert-Equal (Get-Count $byClassification 'same_context_re_attribution') 29 'same-context re-attributions'
Assert-Equal (Get-Count $byClassification 'nested_span_improvement') 5 'nested-span improvements'

$forbidden = @('standalone_suppression', 'orphan_removal', 'orphan_addition', 'unclassified')
Assert-Equal (Get-Count $byClassification 'standalone_suppression') 0 'standalone suppression rows'
foreach ($item in $forbidden) {
    if ($byClassification.ContainsKey($item)) {
        throw "Frozen trace contains forbidden classification: $item ($($byClassification[$item]))"
    }
}

# Default is `FullPhrasePlusExceptions` after the v18.2 RC activation commit.
# This guards against a doc edit obscuring an accidental default regression.
$policyPath = Join-Path $root 'src/EntityAnalyzer.Core/Variants/CompositeVariantPolicy.cs'
$policy = Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8
if ($policy -notmatch 'public\s+const\s+CompositeVariantMode\s+DefaultMode\s*=\s*CompositeVariantMode\.FullPhrasePlusExceptions') {
    throw 'CompositeGeneratorPolicy.DefaultMode is not FullPhrasePlusExceptions; the activation commit has not been applied.'
}
$script:checks++
Write-Output 'OK   default mode is FullPhrasePlusExceptions'

# Doc evidence. Keeps the generated/accepted trace file from being orphaned by
# a later narrative rewrite.
$docPath = Join-Path $root 'docs/OUTPUT_GENERATOR_POLICY.md'
if (-not (Test-Path -LiteralPath $docPath -PathType Leaf)) {
    throw 'Missing generator-policy document: docs/OUTPUT_GENERATOR_POLICY.md'
}
$doc = Get-Content -LiteralPath $docPath -Raw -Encoding UTF8
$requiredDocMarkers = @(
    ' criterion v2'
    'GENERATOR COMPARISON SELFCHECK GREEN UNDER'
    '7 assertions'
    'No orphan removals'
    'No orphan additions'
    'nested span improvement'
    'standalone suppression'
    'HOLDs'
)
foreach ($marker in $requiredDocMarkers) {
    if (-not $doc.Contains($marker)) {
        throw "OUTPUT_GENERATOR_POLICY.md is missing a required marker: '$marker'"
    }
}
$script:checks++
Write-Output 'OK   criterion v2 markers in OUTPUT_GENERATOR_POLICY.md'

Write-Output "Generator policy trace guard OK ($checks checks)"
exit 0
