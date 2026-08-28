# ─── prepare: mini corpus + fresh codex ───
$corpus = Join-Path $env:TEMP "ea_smoke_corpus"
New-Item -ItemType Directory -Force -Path $corpus | Out-Null
Set-Content -Path "$corpus\a.txt" -Encoding UTF8 -Value "Жрец Наг и Йеб шептал в подземелье. Йеб отвечал из тьмы."
Set-Content -Path "$corpus\b.txt" -Encoding UTF8 -Value "Культ Нага процветал. Никто не смел произнести имя Йеб вслух."
$settingsPath = Join-Path $env:APPDATA "EntityAnalyzer\settings.json"
New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
@{ corpusDir = $corpus; outputDir = (Join-Path $env:TEMP "ea_smoke_reports"); preset = "light"; fuzzy = $false; deepCorrupt = $false; contextWords = 30; mentionsPerFile = 5; formats = @("txt","json","md") } | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8
Remove-Item (Join-Path $env:TEMP "ea_smoke_reports") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA "EntityAnalyzer\user_catalog.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA "EntityAnalyzer\codex.json") -ErrorAction SilentlyContinue
Write-Output "prepared"

Set-Location C:\Users\malin\Desktop\entity_analyzer\dotnet
$p = Start-Process -FilePath ".\publish\EntityAnalyzer.exe" -PassThru
Start-Sleep -Seconds 7
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-MainWindow([int]$procId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
function Get-AllOf($win, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    return @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | ForEach-Object { $_ })
}
function Invoke-One($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke() }
}
function Select-Radio($win, [int]$idx) {
    $radios = Get-AllOf $win ([System.Windows.Automation.ControlType]::RadioButton)
    $sp = $null
    if ($radios[$idx].TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select() }
    Start-Sleep -Milliseconds 1400
}
function Find-Button($win, $substr) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    for ($try = 0; $try -lt 5; $try++) {
        foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($b.Current.IsEnabled -and $b.Current.Name -like ("*" + $substr + "*")) { return $b }
        }
        Start-Sleep -Milliseconds 600
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

# 3 ── создать карточку и применить
$addBtn = Find-Button $win "Новая карточка"
if (-not $addBtn) { Fail "no add button" }
Invoke-One $addBtn
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
Invoke-One $applyBtn
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
Invoke-One $linkBtn
Start-Sleep -Milliseconds 1200

$linkOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    $nm = $tx.Current.Name
    if ($nm -like "*владеет*" -and ($nm -like "*→ Наг и Йеб*" -or $nm -like "*← Наг и Йеб*")) { $linkOk = $true }
}
if (-not $linkOk) { Fail "link row not shown" }
Write-Output "OK typed link created"

# 5 ── Схемы: категории на месте
Select-Radio $win 3
$schemaOk = $false
foreach ($li in (Get-AllOf $win ([System.Windows.Automation.ControlType]::ListItem))) {
    if ($li.Current.Name -like "*Божество*" -or $li.Current.Name -like "*Артефакт*") { $schemaOk = $true }
}
if (-not $schemaOk) { Fail "schema categories missing" }
Write-Output "OK schema categories"

# 6 ── История: страница открывается без ошибок (ранее падала на EmptyToVis)
Select-Radio $win 5
Start-Sleep -Milliseconds 800
$historyOk = $false
foreach ($tx in (Get-AllOf $win ([System.Windows.Automation.ControlType]::Text))) {
    if ($tx.Current.Name -like "*ИСТОРИЯ ПРОГОНОВ*") { $historyOk = $true }
}
if (-not $historyOk) { Fail "history page not rendered" }
Write-Output "OK history page renders"

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Output "SMOKE V10 PASSED"


