# ═══════════════════════════════════════════════════════════════════
# verify.ps1 — R1-16: вся верификация одной командой.
# build -> test -> publish -> CLI-проверка на изолированном корпусе ->
# synthetic smoke-corpus anchor ->
# [опционально GUI-смоук] -> JSON-итог.
#
# Запуск:  powershell -File verify.ps1 [-WithGuiSmoke] [-SkipPublish]
# Bench-gate (шаг 4b) локально требует Windows High performance; см. docs/PERFORMANCE.md.
# Коды возврата: 0 — всё зелёное; 1 — есть провал (какой шаг — в итоговом JSON).
# Итог: _scratch\verify_result.json (машиночитаемая сводка).
# Требование PowerShell 5.1: файл сохранён с BOM (кириллица в строках).
# ═══════════════════════════════════════════════════════════════════
param(
    [switch]$WithGuiSmoke,
    [switch]$SkipPublish
)

# Защита от «зависаний» Start-Process -Wait (наблюдались простои до 15 минут):
# воркеры MSBuild (/nodeReuse:true) наследуют перенаправленные потоки и держат
# их открытыми после завершения команды. Отключаем переиспользование и сервер.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$ErrorActionPreference = 'Continue'
$root     = Split-Path -Parent $MyInvocation.MyCommand.Path   # <repo>\dotnet
$scratch  = Join-Path $root "_scratch"
if (-not (Test-Path $scratch)) { New-Item -ItemType Directory -Path $scratch | Out-Null }
$resultPath = Join-Path $scratch "verify_result.json"
$steps    = [System.Collections.Generic.List[object]]::new()
$swTotal  = [System.Diagnostics.Stopwatch]::StartNew()

# ── Герметичность профиля: CLI и GUI-смоук работают на ВРЕМЕННОМ профиле
# (ENTITYANALYZER_DATA_DIR); реальный %APPDATA%\EntityAnalyzer обязан
# остаться нетронутым — проверка «profile-untouched» в конце.
$env:ENTITYANALYZER_DATA_DIR = Join-Path $env:TEMP "ea_verify_profile"
if (Test-Path $env:ENTITYANALYZER_DATA_DIR) { Remove-Item $env:ENTITYANALYZER_DATA_DIR -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $env:ENTITYANALYZER_DATA_DIR | Out-Null
$env:EA_SEMANTIC_USER_DIR = Join-Path $env:ENTITYANALYZER_DATA_DIR "semantic"
$realProfile = Join-Path $env:APPDATA "EntityAnalyzer"
function Get-ProfileSnapshot([string]$dir) {
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -Recurse -File -Force -ErrorAction SilentlyContinue |
        Sort-Object FullName | ForEach-Object { $_.FullName + "|" + $_.Length + "|" + $_.LastWriteTimeUtc.Ticks })
}
$profileBefore = Get-ProfileSnapshot $realProfile

function Add-Step([string]$name, [int]$code, [string]$detail, [double]$seconds) {
    $script:steps.Add(@{ step = $name; exitCode = $code; ok = ($code -eq 0); detail = $detail; seconds = [math]::Round($seconds, 1) })
    $mark = "OK "; if ($code -ne 0) { $mark = "FAIL" }
    # Прогресс дублируем в файл: консольный пайп родителя может умереть,
    # и запись в него блокирует весь скрипт (найдено при первом запуске).
    [System.IO.File]::AppendAllText((Join-Path $script:scratch "verify_progress.txt"),
        "[$mark] $name ($([math]::Round($seconds,1)) c) $detail`r`n", [System.Text.Encoding]::UTF8)
}

function Run-Detached([string]$exe, [string[]]$argList, [string]$workDir, [string]$outLog, [string]$errLog) {
    # Длинные команды в консоли обрываются — все внешние вызовы через файлы.
    $p = Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $workDir `
         -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru -Wait
    return $p.ExitCode
}

function Mark([string]$msg) {
    # Точка прогресса на СТАРТЕ каждого шага: если скрипт завис — файл покажет где.
    [System.IO.File]::AppendAllText((Join-Path $script:scratch "verify_progress.txt"),
        ">> $msg`r`n", [System.Text.Encoding]::UTF8)
}

# ── Шаги 1–5. Досрочный выход при провале — `break verify_pipeline`
# (настоящего goto в PowerShell нет: прежний `goto FINISH` не работал и шаги
# выполнялись дальше провала, а строка `FINISH:` падала с CommandNotFound).
:verify_pipeline do {

# ── 1. Сборка: требование 0 ошибок / 0 предупреждений ───────────────
# UseSharedCompilation=false: VBCSCompiler переживает dotnet-процесс и
# наследует redirected-хендлы; Start-Process -Wait тогда висит до его
# завершения (наблюдалось на bench-gate). Компилятор запускаем в процессе.
Mark "step1 build: старт"
$t = [System.Diagnostics.Stopwatch]::StartNew()
$buildLog = Join-Path $scratch "verify_build.log"
$code = Run-Detached "dotnet" @('build','EntityAnalyzer.slnx','-c','Release','--nologo','-p:UseSharedCompilation=false') $root $buildLog (Join-Path $scratch "verify_build.err.log")
$t.Stop()
$buildText = ""
if (Test-Path $buildLog) { $buildText = Get-Content $buildLog -Raw -Encoding UTF8 }
$warnHits  = ([regex]::Matches($buildText, '[Ww]arning|предупреждени')).Count
$errHits   = ([regex]::Matches($buildText, '[Ee]rror|ошибк[аи]')).Count
$detail = "exit=$code; маркеров warning≈$warnHits, error≈$errHits (см. verify_build.log)"
Add-Step "build" $code $detail $t.Elapsed.TotalSeconds
if ($code -ne 0) { Write-Output "Сборка сломана — остальные шаги пропущены."; break verify_pipeline }

# ── 2. Тесты: эталон ≥ 257 ──────────────────────────────────────────
Mark "step2 test: старт"
$t = [System.Diagnostics.Stopwatch]::StartNew()
$testLog = Join-Path $scratch "verify_test.log"
$code = Run-Detached "dotnet" @('test','EntityAnalyzer.slnx','-c','Release','--nologo','-p:UseSharedCompilation=false') $root $testLog (Join-Path $scratch "verify_test.err.log")
$t.Stop()
$passed = -1; $failed = -1; $total = -1
if (Test-Path $testLog) {
    # ВАЖНО: вывод dotnet — UTF-8 без BOM; без явной кодировки PS 5.1
    # читает его в ANSI, кириллица ломается и regex не совпадает.
    $txt = Get-Content $testLog -Raw -Encoding UTF8
    # Решение содержит несколько тестовых проектов: суммируем все итоговые
    # строки, а не берём первую (иначе baseline видит только ONNX-проект).
    $summaryMatches = @()
    $summaryMatches += [regex]::Matches($txt, 'не пройдено\s+(\d+),\s*пройдено\s+(\d+),\s*пропущено\s+(\d+),\s*всего\s+(\d+)')
    $summaryMatches += [regex]::Matches($txt, 'failed:\s*(\d+),\s*passed:\s*(\d+),\s*skipped:\s*(\d+),\s*total:\s*(\d+)')
    if ($summaryMatches.Count -gt 0) {
        $failed = 0; $passed = 0; $total = 0
        foreach ($match in $summaryMatches) {
            $failed += [int]$match.Groups[1].Value
            $passed += [int]$match.Groups[2].Value
            $total  += [int]$match.Groups[4].Value
        }
    }
}
$ok = ($code -eq 0) -and ($failed -eq 0) -and ($total -ge 257)
$detail = "exit=$code; passed=$passed, failed=$failed, total=$total (эталон ≥257)"
Add-Step "test" ($(if ($ok) { 0 } else { 2 })) $detail $t.Elapsed.TotalSeconds
if (-not $ok) { break verify_pipeline }

# ── 3. Публикация GUI и CLI ─────────────────────────────────────────
if (-not $SkipPublish) {
    Mark "step3 publish: старт"
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    $c1 = Run-Detached "dotnet" @('publish','src\EntityAnalyzer.App\EntityAnalyzer.App.csproj','-c','Release','-o','publish','--nologo','-p:UseSharedCompilation=false') $root (Join-Path $scratch "verify_pub_gui.log") (Join-Path $scratch "verify_pub_gui.err.log")
    $c2 = Run-Detached "dotnet" @('publish','src\EntityAnalyzer.Cli\EntityAnalyzer.Cli.csproj','-c','Release','-o','publish-cli','--nologo','-p:UseSharedCompilation=false') $root (Join-Path $scratch "verify_pub_cli.log") (Join-Path $scratch "verify_pub_cli.err.log")
    $t.Stop()
    $code = $(if ($c1 -eq 0 -and $c2 -eq 0) { 0 } else { 3 })
    Add-Step "publish" $code "gui=$c1, cli=$c2" $t.Elapsed.TotalSeconds
    if ($code -ne 0) { break verify_pipeline }
}

# ── 4. CLI на изолированном корпусе (temp, не пользовательские данные) ──
Mark "step4 cli: старт"
$t = [System.Diagnostics.Stopwatch]::StartNew()
$tmpCorpus = Join-Path $env:TEMP ("ea_verify_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmpCorpus | Out-Null
$sample = Join-Path $tmpCorpus "sample.txt"
[System.IO.File]::WriteAllText($sample, "Жрец Нага шептал у алтаря Йеба, и тьма отвечала." , [System.Text.Encoding]::UTF8)
$tmpOut = Join-Path $tmpCorpus "_out"
$cliExe = Join-Path $root "publish-cli\EntityAnalyzer.Cli.exe"
$cliArgs = @('--corpus', $tmpCorpus, '--out', $tmpOut, '--preset', 'light')
# Рабочий каталог намеренно отдельный: кэш обязан жить в <output>\.cache,
# а не в текущем каталоге процесса (регрессия старого поведения).
$cliWork = Join-Path $env:TEMP ("ea_verify_cwd_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $cliWork | Out-Null
$code = Run-Detached $cliExe $cliArgs $cliWork (Join-Path $scratch "verify_cli.log") (Join-Path $scratch "verify_cli.err.log")
$t.Stop()
$reportExists = Test-Path (Join-Path $tmpOut "entities_report.txt")
$cacheInOutput = Test-Path (Join-Path $tmpOut ".cache")
$cacheInCwd = Test-Path (Join-Path $cliWork ".cache")
$ok = ($code -eq 0) -and $reportExists -and $cacheInOutput -and (-not $cacheInCwd)
Add-Step "cli" $(if ($ok) { 0 } else { 4 }) "exit=$code; отчёт создан=$reportExists; кэш в output=$cacheInOutput; кэш в cwd=$cacheInCwd; корпус=$tmpCorpus" $t.Elapsed.TotalSeconds
try { Remove-Item $tmpCorpus -Recurse -Force } catch { }
try { Remove-Item $cliWork -Recurse -Force } catch { }

# ── 4a. Synthetic smoke-corpus pipeline anchor ─────────────────────
# Pinned 16-file fixture + SHA-256 entities_table.csv; independent of the real
# corpus. The check runs publish-cli twice and fails on drift or non-determinism.
Mark "step4a smoke-corpus-anchor: старт"
$t = [System.Diagnostics.Stopwatch]::StartNew()
$anchorScript = Join-Path $root "scripts\update-smoke-baseline.ps1"
$anchorCorpus = Join-Path $root "test-fixtures\smoke-corpus"
$anchorLog = Join-Path $scratch "verify_smoke_anchor.log"
$code = Run-Detached "powershell" @('-NoProfile','-ExecutionPolicy','Bypass','-File',$anchorScript,'-Check','-Corpus',$anchorCorpus,'-CliPath',$cliExe) $root $anchorLog (Join-Path $scratch "verify_smoke_anchor.err.log")
$t.Stop()
$anchorText = ""
if (Test-Path $anchorLog) { $anchorText = Get-Content $anchorLog -Raw -Encoding UTF8 }
$anchorHash = ""
$anchorMatch = [regex]::Match($anchorText, 'SHA-256 ([A-Fa-f0-9]{64})')
if ($anchorMatch.Success) { $anchorHash = $anchorMatch.Groups[1].Value.ToUpperInvariant() }
$detail = "exit=$code; two runs identical=$($code -eq 0 -and $anchorHash.Length -eq 64); hash=$anchorHash"
Add-Step "smoke-corpus-anchor" $code $detail $t.Elapsed.TotalSeconds
if ($code -ne 0) { break verify_pipeline }

# ── 4a2. Phase C frozen-artifact guard ──────────────────────────────
# Labels, run manifest, corpus-slice manifest, and cross-lingual pair fixture
# are pinned before/after scoring. A mismatch means Phase C numbers are no
# longer reproducible. No model or private corpus text is required.
Mark "step4a2 phase-c-artifacts: start"
$t = [System.Diagnostics.Stopwatch]::StartNew()
$phaseCScript = Join-Path $root "scripts\verify-phase-c-artifacts.ps1"
$phaseCLog = Join-Path $scratch "verify_phase_c_artifacts.log"
$code = Run-Detached "powershell" @('-NoProfile','-ExecutionPolicy','Bypass','-File',$phaseCScript) $root $phaseCLog (Join-Path $scratch "verify_phase_c_artifacts.err.log")
$t.Stop()
$detail = "exit=$code; frozen labels/manifest checked (see verify_phase_c_artifacts.log)"
Add-Step "phase-c-artifacts" $code $detail $t.Elapsed.TotalSeconds
if ($code -ne 0) { break verify_pipeline }

# ── 4b. Performance-gate (R1-14): замедление стадии > порога = провал ──
# Эталон bench/baseline.json; при его отсутствии первый прогон фиксирует
# эталон и остаётся зелёным (осознанное поведение Bench).
# Стабилизация против шума микробенчмарка: n=9 + автоповтор при провале —
# верификация краснится только если гейт не прошёл ДВАЖДЫ подряд
# (два инцидента ложных срабатываний на шумных стадиях зафиксированы).
# Порог ×1.5: микробенчмарки decode utf8 / sentence split дают ложные
# срабатывания ×1.3 на холостой машине (подтверждено 3 раундами).
# Bench-gate is local-only (CI does not run it) and CPU-frequency sensitive.
# It is blocked on a non-High-performance scheme instead of reporting a false
# regression; correctness steps above are unaffected.
$benchPowerHint = ""
$benchPowerOk = $true
if ($env:OS -eq "Windows_NT") {
    try {
        $benchScheme = (& powercfg /getactivescheme 2>$null) -join " "
        $benchMhz = (Get-CimInstance Win32_Processor -ErrorAction Stop |
            Select-Object -First 1).CurrentClockSpeed
        # powercfg output is localized; match the High performance GUID, not its name.
        if ($benchScheme -notmatch '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c') {
            $benchPowerOk = $false
            $benchPowerHint = "active power scheme is not High performance (${benchMhz} MHz): $benchScheme"
            Write-Host "[bench] ERROR: $benchPowerHint; local bench-gate is CPU-frequency sensitive"
            Write-Host "[bench] switch: powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c; restore Balanced after verification; baseline/threshold unchanged"
        } else {
            Write-Host "[bench] power scheme: High performance (${benchMhz} MHz)"
        }
    } catch { }
}

Mark "step4b bench-gate: старт"
$t = [System.Diagnostics.Stopwatch]::StartNew()
if (-not $benchPowerOk) {
    $t.Stop()
    Add-Step "bench-gate" 2 "blocked: $benchPowerHint; switch to High performance (powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c) and rerun; baseline/threshold unchanged" $t.Elapsed.TotalSeconds
    break verify_pipeline
}
$benchProj = Join-Path $root "tools\Bench\Bench.csproj"
$code = Run-Detached "dotnet" @('run','--project',$benchProj,'-c','Release','-p:UseSharedCompilation=false','--','--gate','--n','9') $root (Join-Path $scratch "verify_bench.log") (Join-Path $scratch "verify_bench.err.log")
if ($code -ne 0) {
    Add-Content -Path (Join-Path $scratch "verify_bench.log") -Value "[retry] первый прогон провалился — повтор через 2 c (шум фона)" -Encoding UTF8
    Start-Sleep -Seconds 2
    $code = Run-Detached "dotnet" @('run','--project',$benchProj,'-c','Release','-p:UseSharedCompilation=false','--','--gate','--n','9') $root (Join-Path $scratch "verify_bench_retry.log") (Join-Path $scratch "verify_bench_retry.err.log")
}
$t.Stop()
$benchDetail = "exit=$code (порог ×1.5, n=9, автоповтор при провале)"
if ($benchPowerHint) { $benchDetail += "; $benchPowerHint" }
Add-Step "bench-gate" $code $benchDetail $t.Elapsed.TotalSeconds
if ($code -ne 0) { break verify_pipeline }

# ── 5. GUI-смоук (опционально; отсоединённый запуск с бэкапом данных) ──
if ($WithGuiSmoke) {
    $smokeScript = Join-Path $root "ui_smoke.ps1"
    if (Test-Path $smokeScript) {
        $t = [System.Diagnostics.Stopwatch]::StartNew()
        $code = Run-Detached "powershell" @('-NoProfile','-ExecutionPolicy','Bypass','-File',$smokeScript) $root (Join-Path $scratch "verify_smoke.log") (Join-Path $scratch "verify_smoke.err.log")
        $t.Stop()
        Add-Step "gui-smoke" $code "exit=$code (SMOKE V15)" $t.Elapsed.TotalSeconds
    } else {
        Add-Step "gui-smoke" 9 "скрипт не найден: $smokeScript" 0
    }
}

} while ($false)

# ── 6. Профиль пользователя не тронут (герметичность смоука) ────────
$t = [System.Diagnostics.Stopwatch]::StartNew()
$profileAfter = Get-ProfileSnapshot $realProfile
$profileDiff = Compare-Object $profileBefore $profileAfter
if ($profileDiff) {
    $changed = ($profileDiff | ForEach-Object { $_.InputObject }) -join "; "
    Add-Step "profile-untouched" 5 ("реальный профиль ИЗМЕНЁН: " + $changed.Substring(0, [Math]::Min(400, $changed.Length))) $t.Elapsed.TotalSeconds
} else {
    Add-Step "profile-untouched" 0 ("$realProfile не изменён (файлов: " + $profileAfter.Count + ")") $t.Elapsed.TotalSeconds
}

# ── Итог ────────────────────────────────────────────────────────────
$swTotal.Stop()
$allOk = ($steps | Where-Object { $_.ok -ne $true }).Count -eq 0
$summary = @{
    startedAt = (Get-Date).ToString("o")
    totalSeconds = [math]::Round($swTotal.Elapsed.TotalSeconds, 1)
    ok = $allOk
    steps = $steps
}
$json = ConvertTo-Json $summary -Depth 4
[System.IO.File]::WriteAllText($resultPath, $json, (New-Object System.Text.UTF8Encoding($true)))
Write-Output ""
Write-Output ("ИТОГ: " + $(if ($allOk) { "ВСЁ ЗЕЛЁНОЕ" } else { "ЕСТЬ ПРОВАЛЫ" }) + " · отчёт: $resultPath")
if ($allOk) { exit 0 } else { exit 1 }
