# ─── prepare: mini corpus + ИЗОЛИРОВАННЫЙ профиль ───
# Смоук НИКОГДА не трогает реальный %APPDATA%\EntityAnalyzer: он обязан
# работать через ENTITYANALYZER_DATA_DIR (изолированный каталог профиля).
# Без переменной — отказ: цена ошибки — потеря данных пользователя.
if ([string]::IsNullOrWhiteSpace($env:ENTITYANALYZER_DATA_DIR)) {
    Write-Output "FAIL: ENTITYANALYZER_DATA_DIR не задан — смоук отказывается работать на реальном профиле"
    exit 1
}
$profileDir = [System.IO.Path]::GetFullPath($env:ENTITYANALYZER_DATA_DIR)
$realProfile = [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA "EntityAnalyzer"))
if ($profileDir.TrimEnd('\') -ieq $realProfile.TrimEnd('\')) {
    Write-Output "FAIL: ENTITYANALYZER_DATA_DIR указывает на реальный профиль: $profileDir"
    exit 1
}
# Semantic-слой тоже не должен читать/писать данные машины: даже если
# снаружи задан EA_SEMANTIC_USER_DIR, смоук уводит его в изолированный профиль.
$env:EA_SEMANTIC_USER_DIR = Join-Path $profileDir "semantic"

$corpus = Join-Path $env:TEMP "ea_smoke_corpus"
New-Item -ItemType Directory -Force -Path $corpus | Out-Null
Set-Content -Path "$corpus\a.txt" -Encoding UTF8 -Value "Жрец Наг и Йеб шептал в подземелье. Йеб отвечал из тьмы."
Set-Content -Path "$corpus\b.txt" -Encoding UTF8 -Value "Культ Нага процветал. Никто не смел произнести имя Йеб вслух."
Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
$settingsPath = Join-Path $profileDir "settings.json"
@{ corpusDir = $corpus; outputDir = (Join-Path $env:TEMP "ea_smoke_reports"); preset = "light"; fuzzy = $false; deepCorrupt = $false; contextWords = 30; mentionsPerFile = 5; formats = @("txt","json","md") } | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8
Remove-Item (Join-Path $env:TEMP "ea_smoke_reports") -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "prepared (изолированный профиль: $profileDir)"

Set-Location $PSScriptRoot
$p = Start-Process -FilePath ".\publish\EntityAnalyzer.exe" -PassThru
Start-Sleep -Seconds 7
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-MainWindow([int]$procId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
function Find-Palette([int]$procId) {
    $procCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $titleCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Поиск и команды")
    $andCond = New-Object System.Windows.Automation.AndCondition($procCond, $titleCond)
    # Owned modal windows are not always direct children of the UIA root.
    foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $andCond)) {
        if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $w }
    }
    return $null
}
function Get-AllOf($win, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    return @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | ForEach-Object { $_ })
}
function Invoke-One($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    return $false
}
function Select-Radio($win, [int]$idx) {
    $radios = Get-AllOf $win ([System.Windows.Automation.ControlType]::RadioButton)
    $sp = $null
    if ($radios[$idx].TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select() }
    Start-Sleep -Milliseconds 1400
}
function Select-Tab($win, [string]$name) {
    foreach ($t in (Get-AllOf $win ([System.Windows.Automation.ControlType]::TabItem))) {
        if ($t.Current.Name -eq $name) {
            $sp = $null
            if ($t.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) {
                $sp.Select()
                Start-Sleep -Milliseconds 700
                return $true
            }
        }
    }
    return $false
}
function Find-Button($win, $substr) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    for ($try = 0; $try -lt 8; $try++) {
        foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($b.Current.IsEnabled -and $b.Current.Name -like ("*" + $substr + "*")) { return $b }
        }
        Start-Sleep -Milliseconds 700
    }
    return $null
}
function Set-Text($el, [string]$value) {
    $vp = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue($value); return $true }
    return $false
}
function Select-ComboItem($combo, [string]$itemName) {
    $exp = $null
    if (-not $combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$exp)) { return $false }
    $exp.Expand(); Start-Sleep -Milliseconds 500
    $itemCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($it in $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)) {
        if ($it.Current.Name -eq $itemName) {
            $isp = $null
            if ($it.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$isp)) { $isp.Select() }
            Start-Sleep -Milliseconds 400
            $exp.Collapse(); Start-Sleep -Milliseconds 300
            return $true
        }
    }
    $exp.Collapse(); Start-Sleep -Milliseconds 300
    return $false
}
function Find-Text($win, $substr) {
    foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
        if ($tx.Current.Name -like ("*" + $substr + "*")) { return $true }
    }
    return $false
}
function Fail($msg) { Write-Output ("FAIL: " + $msg); Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; exit 1 }

$win = Get-MainWindow $p.Id
if (-not $win) { Write-Output "FAIL: window"; exit 1 }

# 1 ── кодекс самозагрузился (журнал на дашборде)
$logFound = $false
foreach ($t in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    if ($t.Current.Name -match "Справочник сущностей готов") { $logFound = $true }
}
if (-not $logFound) { Fail "no codex log" }
Write-Output "OK codex auto-loaded"

# 2 ── страница Кодекс: карточка Наг и Йеб в галерее
Select-Radio $win 2
if (-not (Find-Text $win "Наг и Йеб")) { Fail "Наг и Йеб not in gallery" }
Write-Output "OK Наг и Йеб in gallery"

# 2b ── Результаты: доступный фильтр по вердиктам
Select-Radio $win 1
$modeBox = $null
foreach ($cb in (Get-AllOf $win ([System.Windows.Automation.ControlType]::ComboBox))) {
    if ($cb.Current.Name -eq "Фильтр цитат") { $modeBox = $cb; break }
}
if (-not $modeBox) { Fail "verdict filter combo missing" }
$exp = $null
if (-not $modeBox.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$exp)) {
    Fail "verdict filter combo cannot expand"
}
$exp.Expand()
Start-Sleep -Milliseconds 500
$modes = @{}
foreach ($it in (Get-AllOf $modeBox ([System.Windows.Automation.ControlType]::ListItem))) {
    $modes[$it.Current.Name] = $true
}
$exp.Collapse()
foreach ($mode in @("Непроверенные", "Подтверждённые", "Ложные", "Спорные")) {
    if (-not $modes.ContainsKey($mode)) { Fail ("verdict filter missing: " + $mode) }
}
Write-Output "OK review verdict filters"

Select-Radio $win 2
# 3 ── создать карточку и применить
$addBtn = Find-Button $win "Новая карточка"
if (-not $addBtn) { Fail "no add button" }
[void](Invoke-One $addBtn)
Start-Sleep -Milliseconds 2000

# найти редактор имени (Edit со значением «Новая карточка …»)
$nameBox = $null
$edits = Get-AllOf $win ([System.Windows.Automation.ControlType]::Edit)
foreach ($e in $edits) {
    $vp = $null; $val = ""
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $val = $vp.Current.Value }
    if ($val -like "Новая карточка*") { $nameBox = $e; break }
}
if (-not $nameBox) { Fail "name box not found" }
[void](Set-Text $nameBox "Тестовая Карточка")
Start-Sleep -Milliseconds 500

$applyBtn = Find-Button $win "Применить"
if (-not $applyBtn) { Fail "apply button missing" }
[void](Invoke-One $applyBtn)
Start-Sleep -Seconds 3

if (-not (Find-Text $win "Тестовая Карточка")) { Fail "card not created" }
Write-Output "OK card created & applied"

# 4 ── вкладка Связи: связать с Наг и Йебом
$tabs = Get-AllOf $win ([System.Windows.Automation.ControlType]::TabItem)
$linksTab = $null
foreach ($tb in $tabs) { if ($tb.Current.Name -eq "Связи") { $linksTab = $tb } }
if (-not $linksTab) { Fail "links tab missing" }
$sp = $null
[void]($linksTab.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp))
$sp.Select()
Start-Sleep -Milliseconds 800

$tabCombos = @($linksTab.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox))))
if ($tabCombos.Count -lt 2) { Fail ("link combos=" + $tabCombos.Count) }
$r1 = Select-ComboItem $tabCombos[0] "Наг и Йеб"
$r2 = Select-ComboItem $tabCombos[1] "владеет"
$linkBtn = Find-Button $win "Связать"
if (-not $linkBtn) { Fail "link button missing" }
[void](Invoke-One $linkBtn)
Start-Sleep -Milliseconds 1200

$linkOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    $nm = $tx.Current.Name
    if ($nm -like "*владеет*" -and ($nm -like "*→ Наг и Йеб*" -or $nm -like "*← Наг и Йеб*")) { $linkOk = $true }
}
if (-not $linkOk) { Fail "link row not shown" }
Write-Output "OK typed link created"

# 5 ── Схемы: категории на месте
Select-Radio $win 4
$schemaOk = $false
foreach ($li in (Get-AllOf $win ([System.Windows.Automation.ControlType]::ListItem))) {
    if ($li.Current.Name -like "*Божество*" -or $li.Current.Name -like "*Артефакт*") { $schemaOk = $true }
}
if (-not $schemaOk) { Fail "schema categories missing" }
Write-Output "OK schema categories"

# 6 ── История: страница открывается без ошибок (ранее падала на EmptyToVis)
Select-Radio $win 6
Start-Sleep -Milliseconds 800
$historyOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    if ($tx.Current.Name -like "*ИСТОРИЯ ПРОГОНОВ*") { $historyOk = $true }
}
if (-not $historyOk) { Fail "history page not rendered" }
Write-Output "OK history page renders"

# 7 ── Паспорт файла: раздел открывается, пустое состояние с подсказкой (Группа 3, item 24)
Select-Radio $win 9
$passportOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    if ($tx.Current.Name -like "*Анализ ещё не запускался*") { $passportOk = $true }
}
if (-not $passportOk) { Fail "passport page not rendered" }
Write-Output "OK passport page renders"

# 8 ── Словари: раздел открывается, вкладка «Эпитеты» с пустым состоянием (Группа 3, item 23)
Select-Radio $win 10
$dictOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    if ($tx.Current.Name -like "*словарю эпитетов*") { $dictOk = $true }
}
if (-not $dictOk) { Fail "dictionaries page not rendered" }
Write-Output "OK dictionaries page renders"

# 9 ── Корпус: раздел открывается, пустой поиск с подсказкой (Группа 3, item 31)
Select-Radio $win 11
$corpusOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    if ($tx.Current.Name -like "*вернитесь к поиску*") { $corpusOk = $true }
}
if (-not $corpusOk) { Fail "corpus page not rendered" }
Write-Output "OK corpus page renders"

# 10 ── Мир: страница Группы 4 открывается, вкладки на месте (items 5/6/19)
Select-Radio $win 3
$worldTabs = @{}
foreach ($tb in (Get-AllOf $win ([System.Windows.Automation.ControlType]::TabItem))) { $worldTabs[$tb.Current.Name] = $true }
if (-not ($worldTabs.ContainsKey("Древо") -and $worldTabs.ContainsKey("Иерархия") -and $worldTabs.ContainsKey("Хронология") -and $worldTabs.ContainsKey("География"))) { Fail "world tabs missing" }
Write-Output "OK world page renders"

# 11 ── Командная палитра: доступная кнопка, поиск карточки кодекса, UIA-имена строк
$paletteButton = Find-Button $win "Поиск и команды"
if (-not $paletteButton) { Fail "palette button missing" }
Write-Output ("palette button: " + $paletteButton.Current.Name)
$paletteButton.SetFocus()
Start-Sleep -Milliseconds 200
if (-not (Invoke-One $paletteButton)) { Fail "palette button has no invoke pattern" }
Start-Sleep -Seconds 3

$palette = Find-Palette $p.Id
if (-not $palette) {
    Fail "command palette did not open"
}

$queryBox = $null
foreach ($e in (Get-AllOf $palette ([System.Windows.Automation.ControlType]::Edit))) {
    if ($e.Current.Name -eq "Поиск по командам и данным") { $queryBox = $e; break }
}
if (-not $queryBox) { Fail "palette search box missing" }
if (-not (Set-Text $queryBox "zzzzqqq")) { Fail "palette search box is not editable" }
Start-Sleep -Milliseconds 700

$emptyItems = Get-AllOf $palette ([System.Windows.Automation.ControlType]::ListItem)
if ($emptyItems.Count -ne 0) { Fail ("palette should be empty; items=" + $emptyItems.Count) }
if (-not (Find-Text $palette "Ничего не найдено")) { Fail "palette empty state missing" }
if (-not (Find-Text $palette "Найдено: 0")) { Fail "palette empty status missing" }
Write-Output "OK palette empty state"

if (-not (Set-Text $queryBox "наг")) { Fail "palette search box is not editable" }
Start-Sleep -Milliseconds 700

$paletteItems = Get-AllOf $palette ([System.Windows.Automation.ControlType]::ListItem)
if ($paletteItems.Count -eq 0) {
    $value = $null
    $vp = $null
    if ($queryBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $value = $vp.Current.Value }
    Fail ("palette returned no matches; value=" + $value)
}
if (Find-Text $palette "Ничего не найдено") { Fail "palette empty state stuck after matches" }
if ($paletteItems[0].Current.Name -notlike "Кодекс · Наг*") {
    Fail ("palette top item=" + $paletteItems[0].Current.Name)
}
Write-Output "OK palette ranks codex first"
$executeButton = Find-Button $palette "Выполнить выбранную команду"
if (-not $executeButton -or -not $executeButton.Current.IsEnabled) { Fail "palette execute button missing or disabled" }
Write-Output "OK palette execute button"

$palettePattern = $null
if ($palette.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$palettePattern)) {
    $palettePattern.Close()
}
Start-Sleep -Milliseconds 500
$paletteButtonAfter = Find-Button $win "Поиск и команды"
if (-not $paletteButtonAfter -or -not $paletteButtonAfter.Current.HasKeyboardFocus) {
    Fail "palette did not restore focus to opener"
}
Write-Output "OK palette restores focus"

# 12 ── Протокол проверки: вердикт сразу обновляет подсказку на «Выгрузке»
Select-Radio $win 0
$runBtn = Find-Button $win "Запустить анализ"
if (-not $runBtn) { Fail "analysis button missing" }
[void](Invoke-One $runBtn)
$reports = Join-Path $env:TEMP "ea_smoke_reports"
$analysisDone = $false
for ($i = 0; $i -lt 90; $i++) {
    if (Test-Path (Join-Path $reports "entities_data.json")) { $analysisDone = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $analysisDone) { Fail "analysis did not finish" }
Start-Sleep -Seconds 1
if (Test-Path (Join-Path $reports "review_report.md")) { Fail "review_report.md written without verdicts" }
if (Test-Path (Join-Path $reports "review_report.csv")) { Fail "review_report.csv written without verdicts" }
Write-Output "OK no review protocol without verdicts"
Select-Radio $win 1
Start-Sleep -Seconds 1
$confirm = Find-Button $win "подтверждено"
if (-not $confirm) { Fail "confirm button missing" }
[void](Invoke-One $confirm)
Start-Sleep -Milliseconds 800
if (-not (Test-Path (Join-Path $profileDir "verdicts.jsonl"))) { Fail "verdict not saved" }
Write-Output "OK verdict saved"

Select-Radio $win 5
if (-not (Select-Tab $win "ВЫГРУЗКА")) { Fail "export tab missing" }
$hintOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    if ($tx.Current.Name -like "*review_report.md и review_report.csv*") { $hintOk = $true; break }
}
if (-not $hintOk) { Fail "review protocol hint missing or stale after verdict" }
Write-Output "OK review protocol hint after verdict"

$exportBtn = Find-Button $win "Экспортировать"
if (-not $exportBtn) { Fail "export button missing" }
if (-not (Invoke-One $exportBtn)) { Fail "export button has no invoke pattern" }
$mdPath = Join-Path $reports "review_report.md"
$csvPath = Join-Path $reports "review_report.csv"
$reviewExported = $false
for ($i = 0; $i -lt 60; $i++) {
    if ((Test-Path $mdPath) -and (Test-Path $csvPath)) { $reviewExported = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $reviewExported) { Fail "review protocol files were not written after export" }
$md = Get-Content $mdPath -Raw -Encoding UTF8
if ($md -notmatch '\*\*Подтверждено:\*\* 1') { Fail "review_report.md confirmed count mismatch" }
if ($md -notmatch '## Решения') { Fail "review_report.md decisions section missing" }
if ($md -notmatch '\| a\.txt \|') { Fail "review_report.md decision row missing" }
$csv = @(Get-Content $csvPath -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
if ($csv.Count -ne 2) { Fail ("review_report.csv row count=" + $csv.Count) }
if ($csv[0] -ne 'file;line;offset;entity;variant;verdict') { Fail "review_report.csv header mismatch" }
if ($csv[1] -notmatch '"confirmed"') { Fail "review_report.csv confirmed code missing" }
Write-Output "OK review protocol exported after verdict"

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Output "SMOKE V15 PASSED"


