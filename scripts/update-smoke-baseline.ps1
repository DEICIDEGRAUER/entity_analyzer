<#
.SYNOPSIS
    Verifies or deliberately re-baselines the synthetic smoke-corpus pipeline anchor.

.DESCRIPTION
    The smoke corpus is a committed end-to-end anchor: publish-cli runs on a
    small, documented set of edge-case files and the resulting
    entities_table.csv is pinned by SHA-256.

    -Check runs the CLI twice (to catch non-determinism), recomputes counts and
    the Merkle root, and compares everything with smoke-corpus/manifest.json.

    Update mode requires a clean Git working tree and a mandatory -Reason. It
    runs the CLI once, updates the snapshot in manifest.json, and appends a
    history entry with the previous hash, the new hash, and the reason.

.PARAMETER Corpus
    Smoke-corpus directory, or its analyzed corpus/ subdirectory. Relative
    paths are resolved from the current directory. Default:
    <repo>/test-fixtures/smoke-corpus.

.PARAMETER Manifest
    Manifest path. Default: manifest.json next to the corpus/ directory.

.PARAMETER CliPath
    Published CLI executable. Default: <repo>/publish-cli/EntityAnalyzer.Cli.exe
    (or the extension-less executable on non-Windows hosts).

.PARAMETER Catalog
    Optional JSON catalog used for the smoke run. Default: catalog.json next to
    the analyzed corpus/ directory when it exists. The catalog is an input of
    the anchor and its SHA-256 is pinned in the manifest.

.PARAMETER Reason
    Required in update mode. Recorded verbatim in the manifest history.

.PARAMETER Check
    Read-only verification. Runs the CLI twice and fails on a hash mismatch or
    any drift from the pinned snapshot.
#>
[CmdletBinding(DefaultParameterSetName = 'Update')]
param(
    [Parameter(Position = 0)]
    [string]$Corpus,
    [string]$Manifest,
    [string]$CliPath,
    [string]$Catalog,
    [Parameter(ParameterSetName = 'Update', Mandatory = $true)]
    [string]$Reason,
    [Parameter(ParameterSetName = 'Check', Mandatory = $true)]
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($Corpus)) {
    $Corpus = Join-Path $repoRoot 'test-fixtures\smoke-corpus'
}

$inputPath = (Resolve-Path -LiteralPath $Corpus).Path
if (Test-Path -LiteralPath (Join-Path $inputPath 'corpus') -PathType Container) {
    $smokeRoot = $inputPath
    $corpusPath = Join-Path $inputPath 'corpus'
} else {
    $corpusPath = $inputPath
    $smokeRoot = Split-Path -Parent $inputPath
}

if ([string]::IsNullOrWhiteSpace($Manifest)) {
    $Manifest = Join-Path $smokeRoot 'manifest.json'
}
$Manifest = (Resolve-Path -LiteralPath $Manifest).Path

if (-not (Test-Path -LiteralPath $corpusPath -PathType Container)) {
    throw "Corpus directory not found: $corpusPath"
}
if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    throw "Manifest not found: $Manifest"
}

if ([string]::IsNullOrWhiteSpace($Catalog)) {
    $defaultCatalog = Join-Path $smokeRoot 'catalog.json'
    if (Test-Path -LiteralPath $defaultCatalog -PathType Leaf) {
        $Catalog = $defaultCatalog
    }
}
if (-not [string]::IsNullOrWhiteSpace($Catalog)) {
    if (-not (Test-Path -LiteralPath $Catalog -PathType Leaf)) {
        throw "Catalog not found: $Catalog"
    }
    $Catalog = (Resolve-Path -LiteralPath $Catalog).Path
}

$catalogRelative = $null
$catalogHash = $null
if (-not [string]::IsNullOrWhiteSpace($Catalog)) {
    $catalogRelative = $Catalog.Substring($smokeRoot.Length).TrimStart('\', '/').Replace('\', '/')
    $catalogHash = (Get-FileHash -LiteralPath $Catalog -Algorithm SHA256).Hash.ToUpperInvariant()
}

if (-not $PSBoundParameters.ContainsKey('CliPath') -or [string]::IsNullOrWhiteSpace($CliPath)) {
    $candidates = @(
        (Join-Path $repoRoot 'publish-cli\EntityAnalyzer.Cli.exe'),
        (Join-Path $repoRoot 'publish-cli\EntityAnalyzer.Cli')
    )
    $CliPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($CliPath) -or -not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    throw "Published CLI not found. Run publish-cli first or pass -CliPath. Checked: $CliPath"
}
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path

$manifestData = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$manifestData.schema_version -ne 1) {
    throw "Unsupported manifest schema_version: $($manifestData.schema_version)"
}
$snapshot = $manifestData.snapshots.smoke_current
if ($null -eq $snapshot) {
    throw "Manifest has no snapshots.smoke_current entry: $Manifest"
}

$extensions = @($manifestData.extensions | ForEach-Object { ([string]$_).ToLowerInvariant() })
$extensionSet = @{}
foreach ($extension in $extensions) {
    if (-not $extension.StartsWith('.')) { $extension = '.' + $extension }
    $extensionSet[$extension] = $true
}

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '')
    } finally {
        $sha.Dispose()
    }
}

function Get-CanonicalCsvSha256([string]$Path) {
    # The CSV writer uses Environment.NewLine. Canonicalize CRLF/CR to LF before
    # hashing so the same semantic output has the same anchor on Windows/Linux.
    $text = [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false)))
    $text = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($text)
    return Get-Sha256Hex $bytes
}

function Get-MerkleRoot([hashtable]$Hashes) {
    $paths = [string[]]@($Hashes.Keys)
    [Array]::Sort($paths, [System.StringComparer]::Ordinal)

    $leaves = New-Object 'System.Collections.Generic.List[byte[]]'
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($path in $paths) {
            $pathBytes = [System.Text.Encoding]::UTF8.GetBytes($path)
            $hashBytes = [System.Text.Encoding]::ASCII.GetBytes([string]$Hashes[$path])
            $payload = New-Object byte[] ($pathBytes.Length + 1 + $hashBytes.Length)
            [Array]::Copy($pathBytes, 0, $payload, 0, $pathBytes.Length)
            $payload[$pathBytes.Length] = 0
            [Array]::Copy($hashBytes, 0, $payload, $pathBytes.Length + 1, $hashBytes.Length)
            $leaves.Add($sha.ComputeHash($payload))
        }

        if ($leaves.Count -eq 0) {
            return [System.BitConverter]::ToString($sha.ComputeHash([byte[]]@())).Replace('-', '')
        }

        while ($leaves.Count -gt 1) {
            $next = New-Object 'System.Collections.Generic.List[byte[]]'
            for ($i = 0; $i -lt $leaves.Count; $i += 2) {
                if ($i + 1 -lt $leaves.Count) {
                    $left = $leaves[$i]
                    $right = $leaves[$i + 1]
                    $combined = New-Object byte[] ($left.Length + $right.Length)
                    [Array]::Copy($left, 0, $combined, 0, $left.Length)
                    [Array]::Copy($right, 0, $combined, $left.Length, $right.Length)
                    $next.Add($sha.ComputeHash($combined))
                } else {
                    $next.Add($leaves[$i])
                }
            }
            $leaves = $next
        }

        return [System.BitConverter]::ToString($leaves[0]).Replace('-', '')
    } finally {
        $sha.Dispose()
    }
}

function Get-SmokeStats([string]$Root, [string]$CsvPath, [hashtable]$ExtensionSet) {
    $allFiles = @(Get-ChildItem -LiteralPath $Root -File -Recurse)
    $supported = @($allFiles | Where-Object { $ExtensionSet.ContainsKey($_.Extension.ToLowerInvariant()) })
    $emptyFiles = @($supported | Where-Object { $_.Length -eq 0 }).Count
    $totalBytes = 0
    foreach ($file in $supported) { $totalBytes += $file.Length }

    $hashes = @{}
    $groups = @{}
    foreach ($file in $supported) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashes[$relative] = $hash
        if (-not $groups.ContainsKey($hash)) {
            $groups[$hash] = New-Object 'System.Collections.Generic.List[string]'
        }
        $groups[$hash].Add($relative)
    }

    $duplicateGroups = 0
    $duplicateFilesSkipped = 0
    foreach ($hash in $groups.Keys) {
        $count = $groups[$hash].Count
        if ($count -gt 1) {
            $duplicateGroups++
            $duplicateFilesSkipped += ($count - 1)
        }
    }

    $rows = @(Import-Csv -LiteralPath $CsvPath -Delimiter ';' -Encoding UTF8)
    $filesWithMentions = 0
    if ($rows.Count -gt 0) {
        $filesWithMentions = @($rows | ForEach-Object { [string]$_.file } | Sort-Object -Unique).Count
    }

    return [pscustomobject]@{
        scanned_files            = $allFiles.Count
        supported_files          = $supported.Count
        analyzed_files           = $supported.Count - $emptyFiles - $duplicateFilesSkipped
        empty_files              = $emptyFiles
        total_bytes              = $totalBytes
        merkle_root              = (Get-MerkleRoot $hashes)
        duplicate_groups         = $duplicateGroups
        duplicate_files_skipped  = $duplicateFilesSkipped
        entities_table_sha256    = (Get-CanonicalCsvSha256 $CsvPath)
        mentions                 = $rows.Count
        files_with_mentions      = $filesWithMentions
    }
}

function Invoke-SmokeCli([string]$OutputDir, [string]$LogPath) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $cliArgs = @(
        '--corpus', $corpusPath,
        '--out', $OutputDir,
        '--preset', 'extended',
        '--no-incremental'
    )
    if (-not [string]::IsNullOrWhiteSpace($Catalog)) {
        $cliArgs += @('--catalog', $Catalog)
    }
    & $CliPath @cliArgs *> $LogPath
    if ($LASTEXITCODE -ne 0) {
        throw "publish-cli failed with exit code $LASTEXITCODE. See $LogPath"
    }
    $csvPath = Join-Path $OutputDir 'entities_table.csv'
    if (-not (Test-Path -LiteralPath $csvPath -PathType Leaf)) {
        throw "publish-cli did not create entities_table.csv in $OutputDir"
    }
    return $csvPath
}

$dirty = & git -C $repoRoot status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "Not a Git work tree: $repoRoot"
}
if (-not $Check -and @($dirty).Count -gt 0) {
    throw "Working tree is dirty. Commit or stash changes before re-baselining the smoke anchor."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ea-smoke-anchor-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

$oldDataDir = $env:ENTITYANALYZER_DATA_DIR
$oldSemanticDir = $env:EA_SEMANTIC_USER_DIR
$oldInvariant = $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT

try {
    $env:ENTITYANALYZER_DATA_DIR = Join-Path $tempRoot 'profile'
    $env:EA_SEMANTIC_USER_DIR = Join-Path $tempRoot 'semantic'
    $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'
    New-Item -ItemType Directory -Force -Path $env:ENTITYANALYZER_DATA_DIR, $env:EA_SEMANTIC_USER_DIR | Out-Null

    $run1Out = Join-Path $tempRoot 'run1'
    $run1Csv = Invoke-SmokeCli $run1Out (Join-Path $tempRoot 'run1.log')
    $stats = Get-SmokeStats $corpusPath $run1Csv $extensionSet

    if ($Check) {
        $run2Out = Join-Path $tempRoot 'run2'
        $run2Csv = Invoke-SmokeCli $run2Out (Join-Path $tempRoot 'run2.log')
        $run2Hash = Get-CanonicalCsvSha256 $run2Csv
        if ($run2Hash -ne $stats.entities_table_sha256) {
            throw "Smoke anchor is non-deterministic: run1=$($stats.entities_table_sha256), run2=$run2Hash"
        }

        $fields = @(
            'scanned_files', 'supported_files', 'analyzed_files', 'empty_files',
            'total_bytes', 'merkle_root', 'duplicate_groups',
            'duplicate_files_skipped', 'entities_table_sha256', 'mentions',
            'files_with_mentions', 'catalog', 'catalog_sha256'
        )
        $mismatches = @()
        foreach ($field in $fields) {
            $expected = $snapshot.$field
            $actual = if ($field -eq 'catalog') { $catalogRelative }
                      elseif ($field -eq 'catalog_sha256') { $catalogHash }
                      else { $stats.$field }
            if ([string]$expected -ne [string]$actual) {
                $mismatches += "$field expected=$expected actual=$actual"
            }
        }
        if ($mismatches.Count -gt 0) {
            throw ("Smoke anchor drift:`n  " + ($mismatches -join "`n  "))
        }

        Write-Output ("Smoke anchor OK: {0} mentions in {1} files; canonical entities_table.csv SHA-256 {2}; two runs identical." -f $stats.mentions, $stats.files_with_mentions, $stats.entities_table_sha256)
        return
    }

    $date = Get-Date -Format 'yyyy-MM-dd'
    $previousHash = [string]$snapshot.entities_table_sha256
    $snapshot.date = $date
    $snapshot.scanned_files = $stats.scanned_files
    $snapshot.supported_files = $stats.supported_files
    $snapshot.analyzed_files = $stats.analyzed_files
    $snapshot.empty_files = $stats.empty_files
    $snapshot.total_bytes = $stats.total_bytes
    $snapshot.merkle_root = $stats.merkle_root
    $snapshot.duplicate_groups = $stats.duplicate_groups
    $snapshot.duplicate_files_skipped = $stats.duplicate_files_skipped
    $snapshot.entities_table_sha256 = $stats.entities_table_sha256
    $snapshot.mentions = $stats.mentions
    $snapshot.files_with_mentions = $stats.files_with_mentions
    $snapshot.catalog = $catalogRelative
    $snapshot.catalog_sha256 = $catalogHash

    $history = @($manifestData.history)
    $history += [pscustomobject][ordered]@{
        date = $date
        previous_sha256 = $previousHash
        new_sha256 = $stats.entities_table_sha256
        catalog = $catalogRelative
        catalog_sha256 = $catalogHash
        reason = $Reason
    }
    $manifestData.generated = $date
    $manifestData.history = @($history)

    $json = ($manifestData | ConvertTo-Json -Depth 20).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText($Manifest, $json, (New-Object System.Text.UTF8Encoding($false)))

    Write-Output ("Smoke anchor updated: {0} -> {1}; {2} mentions in {3} files; reason: {4}" -f $previousHash, $stats.entities_table_sha256, $stats.mentions, $stats.files_with_mentions, $Reason)
} finally {
    if ($null -eq $oldDataDir) { Remove-Item Env:ENTITYANALYZER_DATA_DIR -ErrorAction SilentlyContinue } else { $env:ENTITYANALYZER_DATA_DIR = $oldDataDir }
    if ($null -eq $oldSemanticDir) { Remove-Item Env:EA_SEMANTIC_USER_DIR -ErrorAction SilentlyContinue } else { $env:EA_SEMANTIC_USER_DIR = $oldSemanticDir }
    if ($null -eq $oldInvariant) { Remove-Item Env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT -ErrorAction SilentlyContinue } else { $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = $oldInvariant }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
