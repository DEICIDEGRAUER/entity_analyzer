# R2-16 «Матричный ночной CI» — локальная матрица культур.
# Прогон полного набора тестов под тремя локалями; ловушка турецкого I
# проверяется на машине разработчика, не только в облаке.
# Использование: powershell -File dotnet\ci-matrix.ps1
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sln = Join-Path $root 'EntityAnalyzer.slnx'
$cultures = @('ru-RU', 'en-US', 'tr-TR')
$results = @()

foreach ($culture in $cultures) {
    Write-Host "=== Матрица: культура $culture ===" -ForegroundColor Cyan
    $env:EA_TEST_CULTURE = $culture
    $output = & dotnet test $sln -c Release --nologo 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $passed = if ($output -match 'пройдено\s+(\d+)') { $Matches[1] } elseif ($output -match 'passed!\s*:?\s*.*?(\d+)') { $Matches[1] } else { '?' }
    $failed = if ($output -match [regex]::Escape('не пройдено')) {
        if ($output -match 'не пройдено\s+(\d+)') { $Matches[1] } else { '1' }
    } else { '0' }
    $results += [pscustomobject]@{ Culture = $culture; Exit = $exit; Passed = $passed; Failed = $failed }
    Write-Host "  exit=$exit passed=$passed failed=$failed"
}

Remove-Item Env:\EA_TEST_CULTURE -ErrorAction SilentlyContinue

$bad = $results | Where-Object { $_.Exit -ne 0 -or $_.Failed -ne '0' }
if ($bad) {
    Write-Host "CI MATRIX: FAIL — культуры с проблемами: $($bad.Culture -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "CI MATRIX: PASSED — все $($cultures.Count) культур зелёные" -ForegroundColor Green
exit 0
