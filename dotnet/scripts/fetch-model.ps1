<#
.SYNOPSIS
    Explicitly downloads and verifies the ONNX model files for Entity Analyzer.

.DESCRIPTION
    The model is never committed and is never downloaded automatically by the
    application. This script fetches the pinned revision into a local models/
    directory, verifies each file's size and SHA-256, and only then renames the
    temporary .part file into place.

    Use -RecordHashes only for the first controlled download, when tokenizer
    hashes are not pinned yet. After that, -Verify is strict and works offline.

.PARAMETER Model
    Model profile from models/manifest.json. Default: e5-small.

.PARAMETER Precision
    Model precision. Default: int8.

.PARAMETER Destination
    Target directory. Relative paths are resolved against the repository root.
    Default: the destination from the manifest (models/e5-small-int8).

.PARAMETER RecordHashes
    Record actual sizes and SHA-256 values for files whose hashes are not yet
    pinned, then update models/README.md and models/manifest.json.

.PARAMETER Verify
    Verify already downloaded files against the manifest. No network access.

.PARAMETER Force
    Re-download every file even when a valid local copy exists.
#>
[CmdletBinding()]
param(
    [string]$Model = 'e5-small',
    [string]$Precision = 'int8',
    [string]$Destination,
    [switch]$RecordHashes,
    [switch]$Verify,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($Verify -and ($RecordHashes -or $Force)) {
    throw '-Verify cannot be combined with -RecordHashes or -Force.'
}

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptDir
$manifestPath = Join-Path $repoRoot 'models/manifest.json'
$readmePath = Join-Path $repoRoot 'models/README.md'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Manifest not found: $manifestPath"
}
if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
    throw "README not found: $readmePath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1) {
    throw "Unsupported manifest schemaVersion: $($manifest.schemaVersion)"
}

$modelProperty = $manifest.models.PSObject.Properties[$Model]
if ($null -eq $modelProperty) {
    throw "Unknown model profile '$Model'. See models/manifest.json."
}
$modelInfo = $modelProperty.Value

if ([string]$modelInfo.precision -ne $Precision) {
    throw "Profile '$Model' has precision '$($modelInfo.precision)', not '$Precision'."
}

$source = [string]$modelInfo.source
$revision = [string]$modelInfo.revision
$license = [string]$modelInfo.license
if ([string]::IsNullOrWhiteSpace($source) -or
    [string]::IsNullOrWhiteSpace($revision) -or
    [string]::IsNullOrWhiteSpace($license)) {
    throw "Profile '$Model' is missing source, revision, or license."
}

$defaultDestination = if ($modelInfo.destination) {
    [string]$modelInfo.destination
} else {
    "models/$Model-$Precision"
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = $defaultDestination
}
if (-not [IO.Path]::IsPathRooted($Destination)) {
    $Destination = Join-Path $repoRoot $Destination
}
$Destination = [IO.Path]::GetFullPath($Destination)

$files = @($modelInfo.files)
if ($files.Count -eq 0) {
    throw "Profile '$Model' contains no files."
}

$downloadBase = "https://huggingface.co/$source/resolve/$revision/"

function Get-Sha256 {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -lt 1MB) {
        return "$Bytes B"
    }
    $mib = [double]$Bytes / 1MB
    [string]::Format(
        [Globalization.CultureInfo]::InvariantCulture,
        '{0} B ({1:0.0} MiB)',
        $Bytes,
        $mib)
}

function Assert-KnownFile {
    param(
        [string]$Path,
        [string]$ExpectedHash,
        [long]$ExpectedSize
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing file: $Path"
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedHash)) {
        throw "SHA-256 is not pinned for $Path. Run -RecordHashes in a controlled environment."
    }

    $actualSize = (Get-Item -LiteralPath $Path).Length
    if ($ExpectedSize -gt 0 -and $actualSize -ne $ExpectedSize) {
        throw "Size mismatch for $Path`: expected $ExpectedSize bytes, got $actualSize bytes."
    }

    $actualHash = Get-Sha256 -Path $Path
    if ($actualHash -ne $ExpectedHash.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path`: expected $($ExpectedHash.ToLowerInvariant()), got $actualHash. Run -Force to re-download."
    }
}

function Update-ReadmeTable {
    param($ModelInfo, [string]$ReadmePath)

    $rows = foreach ($file in @($ModelInfo.files)) {
        $hashLabel = if ($file.sha256) {
            '`' + ([string]$file.sha256).ToLowerInvariant() + '`'
        } else {
            'pending'
        }
        $sourceLabel = '`' + ([string]$ModelInfo.source) + ' @ ' + ([string]$ModelInfo.revision) + '`'
        "| ``$($file.path)`` | $(Format-Size ([long]$file.size)) | $hashLabel | $([string]$ModelInfo.license) | $sourceLabel |"
    }

    $block = @(
        '<!-- BEGIN MODEL HASHES -->'
        '| Файл | Размер | SHA-256 | Лицензия | Источник |'
        '|------|-------:|---------|----------|----------|'
    ) + @($rows) + @(
        '<!-- END MODEL HASHES -->'
    )
    $blockText = $block -join "`r`n"

    $readme = [IO.File]::ReadAllText($ReadmePath)
    $pattern = '(?s)<!-- BEGIN MODEL HASHES -->.*?<!-- END MODEL HASHES -->'
    if ($readme -notmatch $pattern) {
        throw "Hash table markers are missing in $ReadmePath"
    }

    $readme = [regex]::Replace(
        $readme,
        $pattern,
        [System.Text.RegularExpressions.MatchEvaluator]{
            param($match)
            $blockText
        })
    [IO.File]::WriteAllText($ReadmePath, $readme, [Text.UTF8Encoding]::new($false))
}

Write-Host "Model:       $Model ($Precision)"
Write-Host "Revision:    $revision"
Write-Host "Source:      $source"
Write-Host "Destination: $Destination"
Write-Host "Mode:        $(if ($Verify) { 'verify' } elseif ($RecordHashes) { 'record-hashes' } else { 'fetch' })"
Write-Host ''

$verified = 0
foreach ($file in $files) {
    $relativePath = [string]$file.path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Unsafe file path in manifest: '$relativePath'"
    }

    $expectedHash = if ($file.sha256) { ([string]$file.sha256).ToLowerInvariant() } else { $null }
    $expectedSize = if ($null -ne $file.size) { [long]$file.size } else { [long]0 }
    $target = Join-Path $Destination $relativePath
    $targetDirectory = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $targetDirectory)) {
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    }

    $exists = Test-Path -LiteralPath $target -PathType Leaf
    if ($Verify) {
        Assert-KnownFile -Path $target -ExpectedHash $expectedHash -ExpectedSize $expectedSize
        Write-Host "OK          $relativePath"
        $verified++
        continue
    }

    if (-not $RecordHashes -and [string]::IsNullOrWhiteSpace($expectedHash)) {
        throw "SHA-256 is not pinned for $relativePath. Run -RecordHashes in a controlled environment."
    }

    if ($exists -and -not $Force) {
        $actualSize = (Get-Item -LiteralPath $target).Length
        $actualHash = Get-Sha256 -Path $target
        if (-not [string]::IsNullOrWhiteSpace($expectedHash)) {
            if ($expectedSize -gt 0 -and $actualSize -ne $expectedSize) {
                throw "Size mismatch for $relativePath`: expected $expectedSize bytes, got $actualSize bytes. Run -Force to re-download."
            }
            if ($actualHash -ne $expectedHash) {
                throw "SHA-256 mismatch for $relativePath`: expected $expectedHash, got $actualHash. Run -Force to re-download."
            }
        } elseif (-not $RecordHashes) {
            throw "SHA-256 is not pinned for $relativePath. Run -RecordHashes in a controlled environment."
        } else {
            if ($expectedSize -gt 0 -and $actualSize -ne $expectedSize) {
                throw "Size mismatch for $relativePath`: expected $expectedSize bytes, got $actualSize bytes. Run -Force to re-download."
            }
        }

        if ($RecordHashes) {
            $file.size = $actualSize
            $file.sha256 = $actualHash
        }
        Write-Host "OK          $relativePath"
        $verified++
        continue
    }

    $url = $downloadBase + $relativePath.Replace('\', '/')
    $part = "$target.part"
    if (Test-Path -LiteralPath $part) {
        Remove-Item -LiteralPath $part -Force
    }

    try {
        Write-Host "Downloading $relativePath"
        Invoke-WebRequest -Uri $url -OutFile $part -UseBasicParsing -Headers @{
            'User-Agent' = 'EntityAnalyzer-fetch-model/1.0'
        }

        $actualSize = (Get-Item -LiteralPath $part).Length
        $actualHash = Get-Sha256 -Path $part
        if ($expectedSize -gt 0 -and $actualSize -ne $expectedSize) {
            throw "Size mismatch for $relativePath`: expected $expectedSize bytes, got $actualSize bytes."
        }
        if (-not [string]::IsNullOrWhiteSpace($expectedHash) -and $actualHash -ne $expectedHash) {
            throw "SHA-256 mismatch for $relativePath`: expected $expectedHash, got $actualHash."
        }
        if ([string]::IsNullOrWhiteSpace($expectedHash) -and -not $RecordHashes) {
            throw "SHA-256 is not pinned for $relativePath. Run -RecordHashes in a controlled environment."
        }

        Move-Item -LiteralPath $part -Destination $target -Force
        if ($RecordHashes) {
            $file.size = $actualSize
            $file.sha256 = $actualHash
        }
        Write-Host "OK          $relativePath"
        $verified++
    } catch {
        if (Test-Path -LiteralPath $part) {
            Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

if ($RecordHashes) {
    foreach ($file in $files) {
        if ([string]::IsNullOrWhiteSpace([string]$file.sha256)) {
            throw "Cannot record manifest: SHA-256 is still missing for $($file.path)."
        }
    }

    $json = $manifest | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($manifestPath, $json + "`n", [Text.UTF8Encoding]::new($false))
    Update-ReadmeTable -ModelInfo $modelInfo -ReadmePath $readmePath
    Write-Host ''
    Write-Host "Recorded hashes and updated $manifestPath and $readmePath"
} elseif ($Verify) {
    Write-Host ''
    Write-Host "Verified $verified file(s) in $Destination"
} else {
    Write-Host ''
    Write-Host "Fetched $verified file(s) into $Destination"
}
