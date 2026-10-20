# R2-16 «Матрица культур CI» — проверка тестов в разных культурах.
# Культура влияет на форматирование чисел, дат и сортировку; ошибки I
# обнаруживаются только при прогоне, а не при сборке.
# Запуск: powershell -File dotnet\ci-matrix.ps1
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sln = Join-Path $root 'EntityAnalyzer.slnx'
$cultures = @('ru-RU', 'en-US', 'tr-TR')
$results = @()

# Вывод dotnet CLI принудительно на английском: русские/турецкие сообщения
# ломают разбор чисел, а культура тестов задаётся отдельно через EA_TEST_CULTURE.
$oldLang = $env:DOTNET_CLI_UI_LANGUAGE
$oldCulture = $env:EA_TEST_CULTURE
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

try {
    foreach ($culture in $cultures) {
        Write-Host "=== Матрица: культура $culture ===" -ForegroundColor Cyan
        $env:EA_TEST_CULTURE = $culture
        $output = & dotnet test $sln -c Release --nologo 2>&1 | Out-String
        $exit = $LASTEXITCODE

        $passed = '?'
        $failed = '?'
        $total = '?'
        if ($output -match 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+\d+,\s+Total:\s+(\d+)') {
            $failed = $Matches[1]
            $passed = $Matches[2]
            $total = $Matches[3]
        } elseif ($exit -ne 0) {
            $passed = '0'
            $failed = '1'
        }

        $results += [pscustomobject]@{
            Culture = $culture
            Exit = $exit
            Passed = $passed
            Failed = $failed
            Total = $total
        }
        Write-Host "  exit=$exit passed=$passed failed=$failed total=$total"
    }
}
finally {
    if ($null -eq $oldCulture) { Remove-Item Env:\EA_TEST_CULTURE -ErrorAction SilentlyContinue }
    else { $env:EA_TEST_CULTURE = $oldCulture }

    if ($null -eq $oldLang) { Remove-Item Env:\DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue }
    else { $env:DOTNET_CLI_UI_LANGUAGE = $oldLang }
}

$bad = $results | Where-Object { $_.Exit -ne 0 -or $_.Failed -ne '0' -or $_.Total -eq '0' }
if ($bad) {
    Write-Host "CI MATRIX: FAIL — проблемы с культурами: $($bad.Culture -join ', ')" -ForegroundColor Red
    $results | Format-Table -AutoSize
    exit 1
}
Write-Host "CI MATRIX: PASSED — все $($cultures.Count) культур пройдены" -ForegroundColor Green
$results | Format-Table -AutoSize
exit 0
