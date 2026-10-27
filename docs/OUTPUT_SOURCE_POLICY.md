# Source policy B: sidecar, degraded и unknown

**Статус (2026-10-27): DESIGN ONLY.** Source files, каталог,
production-код, `entities_table.csv`, frozen-манифесты и Phase C не менялись.
Решение B принято как направление; реализация — после design review, без
silent exclusion и без изменения source-файлов.

Связанные документы:
[`OUTPUT_SOURCE_AUDIT.md`](OUTPUT_SOURCE_AUDIT.md) — detector, batch 35,
267 tail; [`OUTPUT_TRUST_LEVELS.md`](OUTPUT_TRUST_LEVELS.md) — source_quality
в trust-модели; [`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md) —
измерение incremental-вклада B.

## 0. Решение

**B — sidecar, не silent exclusion.**

1. **Batch 35** (`degraded_lexical`) — 35 файлов с систематической
   лексической подстановкой (`Dee/Kent/irem/Ohio/Mu/thran`), проверенные
   35/35. Строки batch 35 не попадают в trusted/default view, но остаются в
   raw/review и в явном sidecar. Это не удаление файлов и не удаление строк
   из основного CSV.
2. **267 хвостовых кандидатов** — `unknown`, **не** `clean`: file-level
   detector их не выбрал, но они не проверялись вручную; среди 11 ранее
   проверенных файлов вне batch был один с настоящей missing-space OCR-порчей.
   `unknown` виден в default view с badge/`needs_review`, но не считается
   `degraded` и не исключается молча.
3. **Остальные файлы** — `usable` только в смысле «детектор не нашёл
   сигнатуру batch 35 и нет известной OCR-порчи». Это не гарантия
   читаемости; `usable` не равно «проверено вручную».
4. **302 файла / 10 055 flagged** — очередь кандидатов, не exclusion list.
   35 файлов — единственный проверенный file-level список этого дефекта; 267
   — unknown tail.
5. Никакого auto-delete, переписывания источников, `--include-degraded` как
   единственного способа доступа и скрытого фильтра. Sidecar должен быть
   документирован и доступен так же, как будущий `entities_trust.csv`.

## 1. Классы source_quality

| Значение | Что означает | Default/trusted view | Raw/review |
|---|---|---|---|
| `usable` | Детектор batch 35 отрицательный; известной OCR-сигнатуры нет. Не ручная гарантия. | видима | видима |
| `degraded_lexical` | Файл прошёл все шесть маркеров и density ≥1%; 35/35 проверены author-side. | **не показывается** в trusted/default; отдельный degraded view | видима полностью |
| `degraded_ocr` | Известная OCR/PDF-порча без словаря подстановок (например, missing-space); file-level detector её не ловит. | только review/row-level, не file-level exclusion | видима |
| `unknown` | 267 хвостовых кандидатов: row-level score ≥0.10, но file-level detector отрицательный; вручную не читались. | **видима с badge** `source_quality=unknown` + `needs_review`; не strong | видима |

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

### 3.2. Trusted/default

- `usable` — обычный вид;
- `degraded_lexical` — не показывается в trusted/default, но есть явная
  ссылка «degraded sources»/sidecar и счётчик строк; не `--include-degraded`
  как единственный вход;
- `unknown` — **показывается** с badge `unknown`/`needs_review`; не
  считается clean, но и не исключается; пользователь видит, что это
  кандидат на проверку;
- `degraded_ocr` — review/row-level; не file-level quarantine.

Это компромисс B: default не забит заведомо бесполезными 29,9% строк batch 35,
но 267 tail не объявлен чистым и не потерян.

### 3.3. Default-view decision

Сам default view (какие badge включены по умолчанию) остаётся продуктовым
решением после probe 2. B задаёт sidecar и правило исключения **только**
`degraded_lexical`; он не выбирает Strict и не превращает `needs_review` в
тихий фильтр. `unknown` должен быть видим в default; иначе B нарушает
уточнение про 267.

## 4. Counts и границы

Текущий full-output:

- всего: **10 531** файл, **37 406** строк, **2 615** файлов с упоминаниями;
- batch 35: **11 189** строк (29,9%), **11 063** top-4 строки (40,1%),
  **8 381** flagged (83,4% всех flagged), **8 287** top-4 flagged;
- 267 tail: **7 054** строки, **1 674** flagged; это `unknown`, не clean;
- 302 кандидата: **18 243** строки, **10 055** flagged; 302 — очередь, не
  exclusion;
- 11 проверенных файлов вне batch: **10/11** читаемы, **1/11** настоящая
  missing-space OCR-порча; file-level detector её не ловит.

Ограничения:

- 267 не читались вручную; `unknown` — честный статус, а не оценка precision;
- 35/35 проверены author-side, но это не blind-разметка;
- detector привязан к одной сигнатуре; другая порча потребует другого
  detector’а;
- counts по строкам — из текущего вывода, не end-to-end recall.

## 5. Реализация

1. **Freeze detector**: шесть маркеров + density ≥1%; batch 35 list hash
   `C3F5E7FBD7DE653756E817E79CA9F6F1F9AAF869E1F696B457B5B1598F868516`.
2. **Build source sidecar**: пройти по всем 2 615 файлам с упоминаниями;
   выставить `degraded_lexical` ровно 35, `unknown` 267, `degraded_ocr` для
   подтверждённого OCR-случая, остальным `usable` по detector-negative.
3. **Join rows**: `source_quality` попадёт в `entities_from_degraded_sources.csv`
   и будущий `entities_trust.csv`; основной CSV не меняется.
4. **View layer**: raw/review показывает всё; trusted/default применяет
   только правило B и badge; `unknown` остаётся видимым.
5. **Tests**:
   - file-level detector даёт 35/35 и 0/267;
   - 267 не попадают в `degraded_lexical` и не получают `usable`;
   - batch 35 не удаляется из raw/review;
   - `entities_from_degraded_sources.csv` содержит все 11 189 строк batch 35;
   - отсутствие sidecar не меняет старый CSV;
   - counts в отчёте совпадают с counts фильтра.
6. **Report**: команда, scope, detector version, фильтры, counts, сколько
   строк/файлов в каждом классе; никаких «очищено» без evidence.

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

## 7. Чего этот документ не делает

- Не удаляет и не переписывает source-файлы.
- Не делает auto-delete строк.
- Не меняет `entities_table.csv`.
- Не объявляет 267 clean или degraded.
- Не смешивает source exclusion с catalog correction/generator policy.
- Не выбирает Strict-view по умолчанию и не отменяет raw/review.
- Не открывает Phase C, entity graph, owner-таблицы или ONNX.
