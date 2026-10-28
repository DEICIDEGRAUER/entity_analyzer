# Source policy B: sidecar, degraded и unknown

**Статус (2026-10-29): FILE-LEVEL DETECTOR + SOURCE=USABLE GATE IMPLEMENTED;
ROW-LEVEL SIDECAR PARTIAL.** Source files, frozen `entities_table.csv`,
frozen-манифесты и Phase C не менялись. Решение B остаётся sidecar-only, без
silent exclusion. 12 approved catalog corrections применены отдельно;
post-catalog diagnostic run снят (33 430 строк), counts §4 обновлены по нему.
`SourceQuality` реализует ровно batch-35 сигнатуру (35/35, 0/267); trusted и
strict теперь требуют `source=usable`, поэтому `unknown`/`degraded_*` не
проходят в них, но остаются в raw/review. 267-tail row-level `unknown` пока
не построен как отдельный sidecar: detector возвращает им `usable`, значит
исходный список ещё не применяется и data gap честно не закрыт.

Связанные документы:
[`OUTPUT_SOURCE_AUDIT.md`](OUTPUT_SOURCE_AUDIT.md) — detector, batch 35,
267 tail; [`OUTPUT_TRUST_LEVELS.md`](OUTPUT_TRUST_LEVELS.md) — source_quality
в trust-модели; [`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md) —
измерение incremental-вклада B.

## 0. Решение

**B — sidecar, не silent exclusion.**

1. **Batch 35** (`degraded_lexical`) — 35 файлов с систематической
   лексической подстановкой (`Dee/Kent/irem/Ohio/Mu/thran`), проверенные
   35/35. Строки batch 35 не попадают в trusted/strict view, но остаются в
   raw/review и в явном sidecar. Это не удаление файлов и не удаление строк
   из основного CSV.
2. **267 хвостовых кандидатов** — design-target `unknown`, **не** `clean`:
   file-level detector их не выбрал, но они не проверялись вручную; среди 11
   ранее проверенных файлов вне batch был один с настоящей missing-space
   OCR-порчей. В текущем runtime row-level `unknown` ещё не построен (см.
   статус выше), поэтому 267 не получают `usable` по ручной проверке; они
   видны в default view и не исключаются молча.
3. **Остальные файлы** — `usable` только в смысле «детектор не нашёл
   сигнатуру batch 35 и нет известной OCR-порчи». Это не гарантия
   читаемости; `usable` не равно «проверено вручную».
4. **302 файла / 7 249 flagged** в post-catalog выводе (pre-change: 10 055)
   — очередь кандидатов, не exclusion list. 35 файлов — единственный
   проверенный file-level список этого дефекта; 267 — unknown tail.
5. Никакого auto-delete, переписывания источников, `--include-degraded` как
   единственного способа доступа и скрытого фильтра. Sidecar должен быть
   документирован и доступен так же, как additive `entities_trust.csv`.

## 1. Классы source_quality

| Значение | Что означает | Trusted/strict | Raw/review/default |
|---|---|---|---|
| `usable` | Детектор batch 35 отрицательный; известной OCR-сигнатуры нет. Не ручная гарантия. | видима | видима |
| `degraded_lexical` | Файл прошёл все шесть маркеров и density ≥1%; 35/35 проверены author-side. | **не показывается** в trusted/strict; в default/raw видима | видима полностью |
| `degraded_ocr` | Известная OCR/PDF-порча без словаря подстановок (например, missing-space); file-level detector её не ловит. | только review/row-level, не file-level exclusion | видима |
| `unknown` | 267 хвостовых кандидатов: row-level score ≥0.10, но file-level detector отрицательный; вручную не читались. | **не проходит** в trusted/strict; в default/raw видима с badge `unknown` + `needs_review` | видима |

Важно:

- `unknown` — не `degraded` и не `usable`. Нельзя писать «0 из 267 = clean»;
  detector отрицательный только для конкретной сигнатуры.
- `usable` не является обещанием, что контекст читаем: это лишь отсутствие
  известной сигнатуры. Для 267 это не применяется.
- `degraded_lexical` — file-level, а не row-level: 2 808 строк ниже порога
  0,10 внутри batch 35 всё равно испорчены (15/15 выборочных проверок).
- `degraded_ocr` не получает file-level quarantine на этом проходе: размер и
  границы такого файла не подтверждены; нужен row-level review.

## 2. Sidecar-контракт

Основной `entities_table.csv` остаётся 8-колоночным frozen-контрактом.
Source metadata выводится отдельно:

```text
entities_source_quality.csv
key: file
columns:
  source_quality      usable | degraded_lexical | degraded_ocr | unknown
  source_reason       lexical_substitution | ocr_missing_space | candidate_tail | detector_negative
  detector_version    lexical-v1 (six markers, density >= 1%)
  evidence_level      author_checked | detector_only | not_assessed
  batch_id            batch-35 | unknown-267 | none
  source_sha256       optional; только локально, не в публичном отчёте
```

Для явного доступа к строкам degraded-источников:

```text
entities_from_degraded_sources.csv
key: file;num;entity;variant;line
columns: source_quality;source_reason;evidence_level
```

Это не второй основной CSV и не замена `entities_table.csv`; это sidecar,
построенный join’ом по `file`/`line`. Если позже появится
`entities_trust.csv`, он получает `source_quality` через тот же join, а не
дублирует file-level логику.

Требования:

- sidecar детерминирован и воспроизводим из detector version + source list;
- в публичном Git нет имён файлов, сырых контекстов и source hash;
- absence of sidecar не меняет поведение старого клиента;
- sidecar не содержит «очищенных» или переписанных source-текстов;
- counts и фильтры указываются в отчёте: сколько файлов/строк в каждом
  классе, сколько строк исключено из trusted view, сколько осталось в raw.

## 3. Что показывать пользователю

### 3.1. Raw/review

Все строки, включая batch 35 и unknown, с `source_quality` badge и
`trust_reason`. Ничего не удаляется. Это источник истины и база для проверки.

### 3.2. Trusted/strict

- `usable` — единственное состояние, которое проходит в trusted/strict;
- `degraded_lexical` — не показывается в trusted/strict, но остаётся в
  raw/review; есть явная ссылка «degraded sources»/sidecar и счётчик строк;
  не `--include-degraded` как единственный вход;
- `unknown` — **не проходит** в trusted/strict; в default/raw показывается с
  badge `unknown`/`needs_review`; не считается clean;
- `degraded_ocr` — review/row-level; не file-level quarantine.

Это компромисс B: default/raw не забит заведомо бесполезными 22,7% строк
batch 35 (post-catalog; pre-change 29,9%), но 267 tail не объявлен чистым и
не потерян. Default остаётся raw/review, а не trusted.

### 3.3. Default-view decision

Сам default view (какие badge включены по умолчанию) остаётся продуктовым
решением после probe 2. B задаёт sidecar и правило trusted/strict:
`source=usable`, не только исключение `degraded_lexical`. Default/raw
показывает все строки, включая `unknown`; `needs_review` не становится тихим
фильтром. `unknown` должен быть видим в default/raw; иначе B нарушает
уточнение про 267.

## 4. Counts и границы

Post-catalog full-output (2026-10-27; SHA-256
`6553A62C7174FCA26B70D9BB45262EF74E5B51856340429975D036269578B27F`):

- всего: **10 531** файл, **33 430** строк, **2 597** файлов с упоминаниями;
- batch 35: **7 601** строка (22,7%), **7 475** top-4 строк (31,9% top-4),
  **5 761** flagged (74,1% всех flagged), **5 667** top-4 flagged;
- 267 tail: **6 363** строки, **1 488** flagged; это `unknown`, не clean;
- 302 кандидата: **13 964** строки, **7 249** flagged; 302 — очередь, не
  exclusion;
- 11 проверенных файлов вне batch: **10/11** читаемы, **1/11** настоящая
  missing-space OCR-порча; file-level detector её не ловит.

Pre-change snapshot (для сравнения): **37 406 строк**, batch 35 — **11 189**
строк (29,9%), **8 381** flagged; 267 tail — **7 054** строки, **1 674**
flagged; 302 — **18 243** строки, **10 055** flagged. 302/267 — тот же
file-level список, построенный на pre-change выводе; post-catalog строки
пересчитаны по нему. Изменение counts объясняется catalog corrections, а не
новым detector’ом.

Ограничения:

- 267 не читались вручную; `unknown` — честный статус, а не оценка precision;
- 35/35 проверены author-side, но это не blind-разметка;
- detector привязан к одной сигнатуре; другая порча потребует другого
  detector’а;
- counts по строкам — из текущего вывода, не end-to-end recall.

## 5. Реализация

1. **Freeze detector**: шесть маркеров + density ≥1%; batch 35 list hash
   `C3F5E7FBD7DE653756E817E79CA9F6F1F9AAF869E1F696B457B5B1598F868516`.
2. **Partial source sidecar**: `SourceQuality` проходит по тексту и выставляет
   `degraded_lexical` ровно для batch-35 сигнатуры, иначе `usable`; 267-tail
   row-level `unknown` пока **не** построен. `degraded_ocr` не добавлялся:
   известный OCR-случай вне batch не является проверенным file-level списком.
3. **Join rows**: `source_quality`/`source_reason` добавлены в additive
   `entities_trust.csv`; `entities_from_degraded_sources.csv` (7 601 строка
   post-catalog) остаётся отдельным pending-артефактом. Основной CSV не
   меняется.
4. **View layer**: raw/review показывает всё; trusted/strict проходят только
   при `SourceQuality.IsUsable`; `unknown`/`degraded_*` остаются в
   raw/review с badge.
5. **Tests**:
   - file-level detector synthetic тест: шесть маркеров + density;
   - detector-negative → `usable`, empty → `unknown`;
   - batch 35 не удаляется из raw/review;
   - canonical + `unknown` не получает `trust=trusted`;
   - canonical + `usable` остаётся trusted;
   - `entities_from_degraded_sources.csv` не реализован;
   - отсутствие sidecar не меняет старый CSV.
6. **Report**: команда, scope, detector version, фильтры, counts, сколько
   строк/файлов в каждом классе; никаких «очищено» без evidence.

### 5.1. `source=usable` gate: scope + design (2026-10-29)

**Scope.** Гейт применяется только к additive trusted/strict и к
`trust=trusted` в `entities_trust.csv`. Raw/default/export все строки
сохраняют; `needs_review` не удаляется. Гейт не меняет основной
8-колоночный CSV и не является source-exclusion.

**Предикат.** Разрешено ровно `source_quality == usable`. `unknown`,
`degraded_lexical`, `degraded_ocr` и любое неизвестное значение дают
`review`, даже если classifier-trusted. Для `unknown` причина в sidecar —
`trust blocked by non-usable source (unknown)`, для degraded — сохранённый
`lexical trust blocked by degraded source`.

**Текущее состояние.** Проверка реализована в `ResultsViewModel` и
`ReportExporter.WriteTrustCsv`; старые cache-записи без source-поля имеют
`unknown` и поэтому больше не считаются clean. Остаётся data gap: detector
даёт `usable` 267 хвостовым кандидатам. Пока отдельный sidecar не загружен,
гейт не может отличить их от detector-negative файлов; это видимое
ограничение, не разрешение считать их clean.

**Sidecar (следующий слой, ещё не реализован).** Входной file-level
`entities_source_quality.csv` с ключом `file` и полями из §2; для 267
`source_quality=unknown`, `source_reason=candidate_tail`,
`evidence_level=not_assessed`, `batch_id=unknown-267`. Sidecar должен
покрывать все файлы с упоминаниями; отсутствующая запись = `unknown`, не
`usable`. Приоритет: sidecar > detector; batch-35 detector сохраняется как
fallback для запусков без sidecar. Hash/версия sidecar входят в cache
signature. Приватные имена файлов и source hash остаются вне публичного
Git; публичный тест использует синтетические имена.

**Acceptance.** (1) 14/14 trusted probe-2 не меняются при полном `usable`
sidecar; (2) 267 unknown и 35 degraded дают `review`; (3) raw/review row
count не меняется; (4) основной CSV остаётся 8-колоночным; (5) badge
`unknown` виден в default/raw. До выполнения этих пунктов generator
activation, replacement/default и entity graph не двигаются.

**Local diagnostic (2026-10-29, not a product run).** Post-catalog CSV
(33 430 строк; 2 596 уникальных basename с rows) + classifier:
classifier-only trusted = **6 462**; после gate с полным sidecar
(`usable` только вне 302 кандидатов) = **3 640**; blocked = **2 822**, из них
**193** degraded и **2 629** unknown. То есть без sidecar текущий trusted
включает **2 629 строк из 267 unknown** — это и есть незакрытый data gap;
нельзя объявлять trusted clean. Raw/review по-прежнему содержит все
33 430 строк.

## 6. Проверка пользы B

Порог из решения:

- **P растёт ≥0,15 при R≥0,95** — source exclusion должен терять мало;
- если R падает ниже 0,95, B не проходит, даже если P высокий;
- `unknown` не входит в precision trusted-view как strong; его доля и причины
  reportятся отдельно.

Оговорка для probe 2: если sample набран только из `usable`, B не может
изменить ни одну строку, и его incremental P/R будет нулевым **по
построению**. Для честного измерения B нужен либо отдельный source-cell
(строки batch 35/unknown), либо source-only отчёт на полном выводе. Нельзя
приписывать B улучшение, которого не было в sample. Это открытый пункт
probe 2 design, а не разрешение считать B доказанным.

Post-catalog source pool: batch 35 — **7 601** строка, 267 tail — **6 363**
строки; file-level 35/267 не изменился. Source cell должен быть набран из
этого post-catalog pool, иначе counts не совпадут с output, на котором
считаются конфигурации.

## 7. Чего этот документ не делает

- Не удаляет и не переписывает source-файлы.
- Не делает auto-delete строк.
- Не меняет `entities_table.csv`.
- Не объявляет 267 clean или degraded.
- Не смешивает source exclusion с catalog correction/generator policy.
- Не выбирает Strict-view по умолчанию и не отменяет raw/review.
- Не открывает Phase C, entity graph, owner-таблицы или ONNX.

## 8. Probe 2 source cell (2026-10-28)

Source cell: 12 строк (6 batch 35 + 6 unknown). Raw: 3 good / 3 ambiguous /
6 misleading, P=0,250. Source B скрывает 6 batch35 (все misleading),
оставляет 6 unknown (3 good / 3 ambiguous), P=**0,500**, R=**1,000**;
ΔP=+0,250 при R≥0,95. Это отдельная малая ячейка, не primary gate, и она
подтверждает направление B, но не является population estimate.

Primary 50 source-usable, поэтому C1 на нём равен C0 по построению; source B
нельзя приписывать primary-улучшение. `unknown` не скрывается и не получает
`clean`; batch35 остаётся в raw/review. Additive `entities_trust.csv`
реализован 2026-10-28 и содержит source columns; production default не
изменён, 267-tail row-level sidecar pending. Combined C2 P=0,523<0,85 →
trust-level exploratory view, не replacement
([`OUTPUT_QUALITY_PROBE_RESULTS.md`](OUTPUT_QUALITY_PROBE_RESULTS.md) §12).
