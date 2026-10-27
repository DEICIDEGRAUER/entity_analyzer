# Targeted probe 2: один sample, incremental-конфигурации

**Статус (2026-10-27): DESIGN ONLY.** Sample не набран, labels не сделаны.
12 approved catalog corrections применены к private catalog; source-файлы,
production-код и frozen CSV не менялись, но новый output после catalog
corrections ещё не снят. Это pre-registration дизайна; counts, seed и hashes
фиксируются в отдельном manifest **до** разметки.

Связанные документы:
[`OUTPUT_TRUST_LEVELS.md`](OUTPUT_TRUST_LEVELS.md),
[`OUTPUT_SOURCE_POLICY.md`](OUTPUT_SOURCE_POLICY.md),
[`OUTPUT_GENERATOR_POLICY.md`](OUTPUT_GENERATOR_POLICY.md),
[`OUTPUT_QUALITY_PROBE_RESULTS.md`](OUTPUT_QUALITY_PROBE_RESULTS.md).

## 0. Цель

Измерить **incremental-вклад** трёх фиксов на одной разметке, а не делать
четыре независимых probe с разными labels:

1. source B — degraded/unknown sidecar;
2. catalog corrections — 12 approved операций;
3. generator policy — full phrase only для composite names.

Плюс дешёвый non-ML baseline `Strict canonical-only`. Все конфигурации
считаются на одном 50-строчном sample; labels один раз.

## 1. Главное ограничение: source B и `source_usable`

Если sample набран только из `source_quality=usable`, source B **по
построению** не меняет ни одну строку: degraded-файлов в sample нет. Тогда
нельзя получить ΔP для B на этом sample и нельзя приписывать B улучшение,
которого не было.

Поэтому дизайн разделяет два измерения:

- **primary sample 50 строк** — source-usable, owner-unique; измеряет
  catalog и generator incremental;
- **source cell** — отдельная небольшая выборка из batch 35 и `unknown` (не
  входит в primary 50 и не меняет пороги 25/25), только для проверки
  читаемости/эффекта B. Либо B оценивается на полном выводе по counts и
  source-only labels; это должно быть решено до генерации sample.

Это открытый пункт: если требуется измерить B **на тех же 50 строках**, нужно
разрешить в primary sample долю degraded/unknown и изменить стратификацию.
Молча оставить sample source-usable и потом объявить B полезным нельзя.

## 2. Primary sample

### 2.1. Состав

- **50 строк**: **25 weak/composite + 25 strong**.
- Только **non-canonical**: canonical уже 10/10 good; его не перепроверяем.
- Только `source_quality=usable`: batch 35, `degraded_ocr` и `unknown` в
  primary не входят; source B измеряется отдельно (§4).
- Только `owner=unique`: shared/ambiguous уже дали 7/10 misleading; они
  остаются `needs_review`, не входят в primary.
- В каждой страте — **13 с marker / 12 без marker**; число фиксируется до
  выборки, не подгоняется после.
- Один файл — не более одной строки.
- Исключить 20 pilot + 50 probe-1 строк; исключить строки, использованные в
  source cell.
- Seed, дата, output hash, catalog hash, source-policy hash и список
  исключённых строк фиксируются в manifest до разметки. Предлагаемый seed:
  `20261027`; он не является выбором после просмотра rows.
- Если в какой-то ячейке не хватает строк, расширяется только candidate pool,
  а не меняются 25/25 и 13/12; недостача и замена reportятся.

### 2.2. Какие aliases считаются weak/composite

Для стратификации используется текущая trust-модель, а не новая эвристика:

- `weak_composite`: generic-слово/титул/часть пары/часть composite-имени;
- `generated`: форма, сгенерированная из composite name;
- `title`: название произведения/издания;
- `shared`/`homonym` — **не** primary; они остаются `needs_review`;
- `strong_alias`: уникальное собственное имя, не generic, не shared.

`needs_review` — видимая metadata; даже если строка не попала в sample, она не
исчезает из raw/review. Sample — не population estimate.

## 3. Конфигурации

Все конфигурации применяются к одному и тому же labeled sample. Для каждой
строки решается, видна ли она в конфигурации; labels не меняются.

| ID | Конфигурация | Что добавляет | Что проверяет |
|---|---|---|---|
| C0 | **Current** | текущий вывод | reference |
| C1 | **+ source B** | sidecar: `degraded_lexical` вне trusted/default; `unknown` видим с badge | вклад source policy |
| C2 | **+ source B + catalog** | 12 approved corrections: 6 owner_reassign, 5 misattribution_removal, 1 homonym_removal | вклад catalog |
| C3 | **+ source B + catalog + generator** | full phrase only по [`OUTPUT_GENERATOR_POLICY.md`](OUTPUT_GENERATOR_POLICY.md); без catalog aliases | вклад generator |
| C4 | **Strict canonical-only** | только canonical; дешёвый non-ML baseline | не замена, а нижняя/верхняя граница |
| C5 | **No-trust-filter** | все строки, включая `needs_review` | reference видимого объёма |

Примечание: `C3` на текущем этапе — **симуляция** generator policy на
row-level: из вывода удаляются generator-only standalone forms; aliases не
меняются. После появления кода конфигурация должна быть пересчитана реальным
`FullPhraseOnly`, а не фильтром по CSV. До этого C3 нельзя называть
production-измерением.

`Strict` — cheap baseline, не кандидат на автоматическую замену. Если
Strict P≥0,95 и canonical loss≤5%, он остаётся альтернативой; если нет —
не используется как gate.

## 4. Source B: отдельное измерение

Primary sample source-usable, поэтому C1 на нём не даст ΔP. Варианты:

1. **Source cell (рекомендуется):** 10–15 строк из batch 35 + `unknown`,
   размеченные отдельно; оценивается readability и misleading-доля B, не
   смешивая с 25/25. Source cell не входит в primary P/R gate; counts
   reportятся отдельно.
2. **Source-only full-output check:** counts batch 35 (11 189 строк), 267
   unknown (7 054 строки), 35/35 verified; P/R только на labeled source cell.
3. Если B должен быть в primary 50, изменить стратификацию: добавить
   source-degraded ячейку и зафиксировать новые 25/25 + source counts **до**
   sample. Это требует явного решения и нового manifest.

Порог B: **P +≥0,15 при R≥0,95**. Для file-level exclusion это означает, что
B должен убирать в основном плохие строки, почти не теряя good. `unknown`
не считается degraded; он не должен исчезать из default-view.

## 5. Метрики и пороги

### 5.1. Определения

- **Visible** — строки, которые конфигурация показывает в оцениваемом view.
- **Good** — author-side label `good`; `ambiguous` и `misleading` не good.
- **P** = good visible / all visible.
- **R** = good visible / all good в labeled sample (знаменатель один и тот же
  для всех конфигураций).
- `ambiguous` reportится отдельно; не переклассифицируется в good/misleading
  после начала разметки.
- `needs_review` не удаляется молча; его доля и причины reportятся.

### 5.2. Пороги (до labels)

| Проверка | Порог | Смысл |
|---|---|---|
| Source B vs Current | **ΔP ≥0,15, R≥0,95** | exclusion должен терять мало |
| Catalog vs source B | **ΔP ≥0,05, R без потерь** | точечные owner-фиксы |
| Generator vs source B+catalog | **ΔP ≥0,10, R≥0,90** | structural change должен окупаться |
| Суммарно C3 | **P≥0,85** | иначе трёх фиксов недостаточно |
| Strict canonical-only | P≥0,95, canonical loss≤5% | cheap альтернатива, не gate |

Пороги сравниваются **incremental**, а не только с Current: C2 сравнивается с
C1, C3 — с C2. Если C1 неотличим от C0 из-за source-usable sample, это
reportится как ограничение, а не как «B не работает».

### 5.3. Что считается успехом

- C3 проходит P≥0,85, R≥0,90, и каждый шаг даёт ожидаемый incremental ΔP.
- C2 не теряет recall: owner_reassign не должен удалять good-строки;
  misattribution/homonym removal может убрать misleading.
- C1 проходит свой порог **на source cell/full source check**, а не
  приписывается primary sample.
- Strict остаётся альтернативой, если P≥0,95 и canonical loss≤5%.
- Если C3 <0,85 — не включать generator policy по умолчанию; переходить к
  trust-level/product decision, а не к четвёртому probe.

## 6. Разметка

1. Freeze manifest: seed, sample IDs, counts 25/25, 13/12, hashes, excluded
   probe-1/pilot rows, source cell definition.
2. Author-side labels: `good` / `ambiguous` / `misleading` + reason
   (`source_lexical`, `source_ocr`, `catalog_owner`, `generic`, `partial`,
   `generator`, `other`). Один label на строку; не менять после начала
   blind-подмножества.
3. Blind subset (минимум 20 строк, включая обе страты и обе marker-ячейки);
   сравнить с author-side; расхождения reportить, не «исправлять» labels.
4. Считать C0–C5 на одном label set; отчёт обязан указать команду, scope,
   фильтры и counts: сколько строк/файлов исключено, сколько visible,
   сколько `needs_review`.
5. Не объявлять default view до отчёта; sidecar `entities_trust.csv` — после
   probe 2, не до.

## 7. Риски и ограничения

- 50 строк дают широкий confidence interval; это targeted probe, не
  population estimate. Решение — по заранее зафиксированным counts, не по
  точечной P.
- 25/25 и 13/12 — фиксированные доли sample, не доли корпуса.
- Source-usable primary не измеряет B; без source cell/full source check
  вывод о B будет ложным.
- C3 — row-level симуляция до реализации generator policy; после реализации
  её нужно пересчитать.
- Catalog corrections могут изменить strata; sample нельзя freeze до
  применения 12 approved операций в прогоне.
- `needs_review` не является фильтром; если конфигурация просто удаляет
  review-строки, это отдельный продукт, а не precision-фикс.
- Phase C, entity graph, owner-таблицы, ONNX и default-view не открываются
  этим документом.

## 8. Следующий шаг

1. Утвердить source cell или изменить source-usable стратификацию.
2. 12 approved catalog corrections уже применены к каталогу; **переснять
   вывод** и зафиксировать новый catalog/output hash. Старый CSV для freeze
   не годится.
3. Утвердить generator design/exception list; C3 считать как симуляцию до
   реализации.
4. Сгенерировать manifest 50 строк + source cell, затем labels.
5. После отчёта решить default view; не пушить до probe 2, локальные
   коммиты допустимы.
