# Targeted probe 2: один sample, incremental-конфигурации

**Статус (2026-10-27): DESIGN ONLY.** Sample не набран, labels не сделаны.
12 approved catalog corrections применены к private catalog; post-catalog
diagnostic output снят: **33 430 строк**, SHA-256
`6553A62C7174FCA26B70D9BB45262EF74E5B51856340429975D036269578B27F`.
Source-файлы, production-код и frozen CSV не менялись. Sample всё ещё не
заморожен: не решён baseline для incremental catalog (C0/C2) и source cell.
Это pre-registration дизайна; counts, seed и hashes фиксируются в отдельном
manifest **до** разметки.

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

**Решение 2026-10-27:** выбран отдельный source cell (вариант 1 ниже); primary
50 остаётся source-usable. Source cell не входит в primary P/R gate, но его
labels и counts reportятся отдельно. Молча оставить sample source-usable и
потом объявить B полезным нельзя.

### 1.1. Catalog timing: C0 не может быть post-catalog

12 approved corrections уже применены. Поэтому post-catalog output
(33 430 строк) — это уже **C2**, а не C0:
если взять его как `Current`, шаг `+ catalog` станет no-op и его ΔP будет
нулевым по построению. Для incremental C0→C2 нужен один из вариантов:

1. **Pre-change baseline + post-catalog view, один label set** (рекомендуется
   для incremental): C0 — frozen pre-change output
   `3954D596EED2828859113F16C1EBB9EDED45FC1DFC8DAD0DF471FCC66B253685`
   (37 406 строк), C2 — post-catalog output
   `6553A62C7174FCA26B70D9BB45262EF74E5B51856340429975D036269578B27F`
   (33 430 строк). Sample строится по union **source-row** ключей
   `file/line` (context), а не по `entity`; labels содержат expected owner;
   C0/C2 вычисляются по одному label set. Это не «sample на старом CSV»: оба
   снимка явно в manifest, а разница — измеряемый catalog-эффект.
2. **Control run с backup-каталогом**: снять current pre-change baseline
   отдельным прогоном с backup-каталогом и использовать его как C0;
   post-catalog — C2. Дороже, но устраняет сомнение в сопоставимости
   старого output.
3. **Отказаться от catalog incremental в P/R**: C0 = post-catalog; измерить
   только source/generator, а catalog-эффект reportить отдельными counts
   (до/после). Тогда порог catalog нельзя объявлять пройденным по P/R.

**Решение 2026-10-27:** выбран вариант 1 — pre-change C0 и post-catalog C2,
один label set на source-row ключах. Контрольный прогон с backup-каталогом не
делается; вариант 3 (только counts) отклонён. Для owner_reassign labels должны
включать expected owner: одна и та же строка в C0 показывается под неверным
владельцем, в C2 — под верным; одного `good/misleading` без owner недостаточно.

## 2. Primary sample

### 2.1. Состав

- **50 строк**: **25 weak/composite + 25 strong**.
- Только **non-canonical**: canonical уже 10/10 good; его не перепроверяем.
- Только `source_quality=usable`: batch 35, `degraded_ocr` и `unknown` в
  primary не входят; source B измеряется отдельно (§4).
- Только `owner=unique`: shared/ambiguous уже дали 7/10 misleading; они
  остаются `needs_review`, не входят в primary.
- **Catalog coverage:** sample обязан покрыть все 12 approved операций
  (6 owner_reassign, 5 removal, 1 homonym) или явно reportить, какие не
  покрыты. Иначе C2 ΔP не оценивается, и нельзя объявлять catalog-порог
  пройденным. Если owner-unique исключает такие строки, нужна отдельная
  catalog cell или изменение стратификации до manifest.
- В каждой страте — **13 с marker / 12 без marker**; число фиксируется до
  выборки, не подгоняется после.
- Один файл — не более одной строки.
- Исключить 20 pilot + 50 probe-1 строк; исключить строки, использованные в
  source cell.
- Seed, дата, **pre-change и post-catalog output hashes**, catalog/source-policy
  hash и список исключённых строк фиксируются в manifest до разметки.
  Предлагаемый seed: `20261027`; он не является выбором после просмотра rows.
- Если в какой-то ячейке не хватает строк, расширяется только candidate pool,
  а не меняются 25/25 и 13/12; недостача и замена reportятся.

### 2.2. Какие aliases считаются weak/composite

Для стратификации используется текущая trust-модель, а не новая эвристика:

- `weak_composite`: generic-слово/титул/часть пары/часть composite-имени;
- `generated`: форма, сгенерированная из composite name;
- `title`: название произведения/издания;
- `shared`/`homonym` — **не** primary; они остаются `needs_review`;
- `strong_alias`: уникальное собственное имя, не generic, не shared.

Для owner_reassign строк label дополнительно фиксирует `expected_owner`;
без этого нельзя отличить C0 (неверный владелец) от C2 (верный) при том же
surface.

**Решение 2026-10-27:** generator exception list не одобрен — approved
exception set пустой. `Leng`, `Nug`/`Yeb`, `Tsang`, `Йиг`, `Эйбон` остаются
HOLD/owner review; C3 design baseline — full phrase only без исключений.

`needs_review` — видимая metadata; даже если строка не попала в sample, она не
исчезает из raw/review. Sample — не population estimate.

## 3. Конфигурации

Все конфигурации применяются к одному и тому же labeled sample. Для каждой
строки решается, видна ли она в конфигурации; labels не меняются.

| ID | Конфигурация | Что добавляет | Что проверяет |
|---|---|---|---|
| C0 | **Current / pre-change baseline** | pre-change output (37 406) до фиксов | reference |
| C1 | **+ source B** | sidecar: `degraded_lexical` вне trusted/default; `unknown` видим с badge | вклад source policy |
| C2 | **+ source B + catalog** | 12 approved corrections: 6 owner_reassign, 5 misattribution_removal, 1 homonym_removal | вклад catalog |
| C3 | **+ source B + catalog + generator** | full phrase only по [`OUTPUT_GENERATOR_POLICY.md`](OUTPUT_GENERATOR_POLICY.md); source B и catalog остаются; aliases не меняются | вклад generator |
| C4 | **Strict canonical-only** | только canonical; дешёвый non-ML baseline | не замена, а нижняя/верхняя граница |
| C5 | **No-trust-filter** | все строки, включая `needs_review` | reference видимого объёма |

Примечание о baseline: C0 — **pre-change** снимок (37 406), C2 —
post-catalog (33 430), если выбран вариант 1/2 из §1.1. Если C0 сделан
post-catalog, C2 no-op и каталог нельзя оценивать incremental. C0/C1/C2
считаются на одном label set с `expected_owner`; C3 остаётся row-level
симуляцией generator policy.

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
2. **Source-only full-output check:** counts post-catalog batch 35 (7 601
   строка), 267 unknown (6 363 строки), 35/35 verified; pre-change было
   11 189 / 7 054. P/R только на labeled source cell.
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
  Для owner_reassign additional condition: shown `entity` должен совпадать с
  `expected_owner`; иначе строка misleading для этой конфигурации, даже если
  surface сам по себе читаем.
- **P** = good visible / all visible.
- **R** = good visible / all good в labeled sample (знаменатель один и тот же
  для всех конфигураций).
- `ambiguous` reportится отдельно; не переклассифицируется в good/misleading
  после начала разметки.
- `needs_review` не удаляется молча; его доля и причины reportятся.

### 5.1.1. Rubric до labels

- **good**: surface встречается в контексте как имя ожидаемой сущности
  (`expected_owner` для owner_reassign), контекст читаем и не приписывает
  surface другой сущности. Для generic/partial/composite формы нужен
  независимый marker, не совпадающий с проверяемым surface.
- **ambiguous**: surface может относиться к ожидаемой сущности, но контекст
  слишком короткий/дейктический, marker слабый или отсутствует, либо форма
  generic/title и допускает другое прочтение. Строка остаётся видимой, но не
  является доказательством.
- **misleading**: контекст приписывает surface не той сущности, это common
  word/место/титул/омоним, либо source-порча делает имя недостоверным; для
  approved removal target ожидаемого owner нет.
- **source cell**: оценивается читаемость и поддержка показанной сущности;
  `good` — читаемо и поддерживает, `ambiguous` — порча/частичность, но
  восстановимо, `misleading` — подстановка/склейка разрушает смысл или
  приписывает не ту сущность.
- Один label на строку; `expected_owner` заполняется для owner_reassign.
  C0/C2 не меняют label: конфигурация лишь решает, видна ли строка и под
  каким owner; при несовпадении shown owner с `expected_owner` строка
  считается misleading **для этой конфигурации**.

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

1. Manifest заморожен 2026-10-28 (см. §9): seed, sample IDs, counts 25/25,
   13/12, **pre-change и post-catalog output hashes**, catalog/source-policy
   hashes, baseline choice (§1.1), excluded probe-1/pilot rows, source cell
   definition. После labels manifest не меняется.
2. Author-side labels: `good` / `ambiguous` / `misleading` + reason
   (`source_lexical`, `source_ocr`, `catalog_owner`, `generic`, `partial`,
   `generator`, `other`) + `expected_owner` для owner_reassign. Один label на
   строку; не менять после начала blind-подмножества.
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
- Catalog corrections уже применены; manifest использует pre-change C0 и
  post-catalog C2. Post-catalog output — C2, не C0; иначе catalog incremental
  равен нулю по построению.
- Один label set должен содержать expected owner; иначе owner_reassign
  нельзя честно измерить.
- Manifest покрывает 12 approved operations; 12 строк coverage (по одной на
  operation). `Ptmak` в pre-change выводе не встречается, поэтому его
  coverage-строка — post-only и имеет `variant_source=canonical_postonly`;
  это осознанное исключение из «non-canonical», иначе C2 recall для нового
  владельца не измерить.
- `needs_review` не является фильтром; если конфигурация просто удаляет
  review-строки, это отдельный продукт, а не precision-фикс.
- Phase C, entity graph, owner-таблицы, ONNX и default-view не открываются
  этим документом.

## 8. Следующий шаг

1. ~~Утвердить source cell или изменить source-usable стратификацию.~~
   **Сделано 2026-10-27:** выбран отдельный source cell.
2. ~~Утвердить baseline для catalog incremental.~~ **Сделано 2026-10-27:**
   выбран pre-change C0 + post-catalog C2 (§1.1). Post-catalog output уже
   снят: **33 430 строк**, hash
   `6553A62C7174FCA26B70D9BB45262EF74E5B51856340429975D036269578B27F`;
   он не является C0.
3. ~~Утвердить generator design/exception list.~~ **Сделано 2026-10-27:**
   exception set пуст; C3 остаётся симуляцией до реализации.
4. ~~Сгенерировать manifest 50 строк + source cell.~~ **Сделано 2026-10-28:**
   manifest `becb1a83…`; см. §9. Labels ещё не сделаны.
5. После отчёта решить default view; не пушить до probe 2, локальные
   коммиты допустимы.

## 9. Frozen manifest (2026-10-28)

Manifest заморожен до labels. Локальный ignored-файл:
`output/quality-probe2-2026-10-27/probe2-manifest.csv` (контексты не
публикуются); metadata — `probe2-manifest.json`.

- SHA-256 manifest: **`becb1a83f487e231c626ca5b83d0daffe6fe8720d7358d876caf735e172608d0`**.
- Frame: union source-row ключей pre-change + post-catalog:
  **37 983** ключа; **32 853** есть в обоих, **4 553** pre-only, **577**
  post-only. Pre-hash `3954D596…`, post-hash `6553A62C…`, catalog hash
  `95B54F6D…` (полные значения — §1.1/metadata).
- Primary: **50** строк, **25 weak_composite + 25 strong**, в каждой страте
  **13 marker / 12 без marker**; один файл — не более одной строки.
- Pre/post состав primary: **41** строк есть в обоих, **6** pre-only,
  **3** post-only. Шесть pre-only — approved removal/homonym targets; три
  post-only — approved owner_reassign coverage, включая `Ptmak`, которого нет
  в pre-change выводе.
- Owners: **44** строки с ровно одним owner, **6** — approved removal/homonym
  targets с нулём owners; shared (2–3 owners) в primary не входят.
- Catalog coverage: **12/12 approved operations** по одной строке; `Ptmak`
  покрыт post-only строкой с `variant_source=canonical_postonly`. Это
  единственное исключение из «non-canonical»; оно не является canonical C0.
- Source cell: **12** строк, **6 batch 35 + 6 unknown**, 12 разных файлов;
  не входит в primary и не меняет 25/25.
- Исключены **70** строк: 20 pilot + 50 probe-1; пересечение пустое.
- Sample IDs: `P2-01…P2-50` (primary), `S2-01…S2-12` (source cell).
- Labels: **не начаты**. После начала разметки manifest не меняется; любая
  правка sample требует нового manifest hash и нового probe.

Manifest — targeted sample, не population estimate. Он не меняет
`entities_table.csv`, production-код, source-файлы, frozen CSV, Phase C или
default view.
