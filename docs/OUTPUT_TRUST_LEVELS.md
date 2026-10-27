# Trust levels: `strong` / `weak` / `needs_review`

**Статус (2026-10-26; decisions 2026-10-27): DESIGN ONLY.** Это документ о
том, как объяснять пользователю доверие к строке. Production-код, CSV-контракт,
Phase C и frozen-манифесты не меняются. 12 approved catalog corrections
применены отдельно (§10.5 catalog audit); source-файлы не менялись.
`needs_review` — видимая metadata, не фильтр и не удаление.

## 0. Зачем это нужно

Первый probe показал, что одного `alias_type` недостаточно:

- canonical — 10/10 good;
- strong alias — 5/10 good;
- weak/composite — 0/10 good;
- ambiguous — 2 good, 1 ambiguous, 7 misleading;
- OCR/quality — 0/10 good (9/10 unusable context).

Причины ортогональны:

1. **alias class** — имя, generic-слово, title, часть пары, омоним;
2. **marker** — есть ли в контексте независимый якорь той же сущности;
3. **source quality** — читаем ли контекст;
4. **owner** — уникален ли владелец алиаса;
5. **match** — полное имя или частичное упоминание.

Trust level — это производная от этих измерений, а не ещё один alias-list.
Он не заменяет catalog/source audit и не «исправляет» их.

## 1. Определения

### 1.1. Alias class

| Класс | Что это | Примеры |
|---|---|---|
| `canonical` | имя/канон карточки | `Book of Eibon`, `Книга Эйбона`, `Nug and Yeb`, `Plateau of Leng`, `Yog-Sothoth` |
| `strong_alias` | уникальное собственное имя/устойчивый перевод, не generic | `Ленга`, `Йог-Сотот`, полные формы названий |
| `weak_composite` | generic-слово, титул, эпитет, часть/издание, член пары | `Книгу`, `The Gate`, `The Key`, `Наг`, `Nug`, `Yeb`, `The Lurker…` |
| `title` | название произведения/части, не имя сущности | `The Life of Eibon`, `Papyrus…` |
| `homonym` | совпадает с другим словом/именем | `Лао` (Laozi), `Наг` (прилагательное) |
| `shared` | алиас принадлежит ≥2 карточкам | `Cxaxukluth`, `Plateau of Tsang`, `Not-to-be-Named One` |
| `generated` | surface-форма, сгенерированная движком, а не каталогом | `Книге`, `Книгах` из `Книга Эйбона`; `Нага`, `Нагов` |

`strong_alias` — не то же, что `strong` trust level. Это класс имени; trust
ниже решает, можно ли показывать строку как доказательство.

### 1.2. Marker

**Marker** — независимый якорь той же сущности в контексте:

- canonical/полное имя;
- другой сильный алиас (не сам проверяемый вариант);
- для пары — второй член пары (`Nug` + `Yeb`, `Наг` + `Йеб`);
- для generic-алиаса — явное имя рядом (`The Gate` + `Yog-Sothoth`,
  `Книгу` + `Эйбон`).

Сам проверяемый вариант **не** считается marker’ом. Иначе `The Gate`
всегда был бы «подтверждён» самим собой.

### 1.3. Source quality

| Значение | Смысл |
|---|---|
| `usable` | контекст читаем; источник не в lexical batch 35 и не имеет явной OCR-деградации |
| `degraded_lexical` | source batch 35: `Dee/Kent/irem/Ohio/thran`, потеря пробелов |
| `degraded_ocr` | OCR/PDF-порча без лексического словаря: склейки, пропуски |
| `unknown` | не проверено |

`degraded_*` — **не** automatically excluded; это причина `needs_review`.
`unknown` (267 хвостовых кандидатов) — **не** `clean`: он виден в default view
с badge/`needs_review`, но не считается degraded. Source-policy design:
[`OUTPUT_SOURCE_POLICY.md`](OUTPUT_SOURCE_POLICY.md).

### 1.4. Owner

- `unique` — алиас принадлежит одной карточке;
- `shared` — ≥2 карточки;
- `conflict` — владелец заведомо неверен (например, `Zhar` в `Nug and Yeb`).

Owner-таблицы и graph-label set в этом дизайне не создаются. Для trust
достаточно статуса строки; разрешение владельца — отдельный будущий трек.

## 2. Правила вывода trust

| Alias class | Marker | Source | Owner | Match | Trust |
|---|---|---|---|---|---|
| `canonical` | не нужен (сам имя) | `usable` | `unique` | full | **strong** |
| `strong_alias` | не нужен, если имя уникально | `usable` | `unique` | full | **strong** |
| `strong_alias` | — | `degraded_*` | `unique` | any | **needs_review** |
| `weak_composite`/`title`/`generated` | явный marker | `usable` | `unique` | full/partial | **weak** |
| `weak_composite`/`title`/`generated` | нет marker | `usable` | `unique` | any | **needs_review** |
| `homonym` | явный disambiguating marker | `usable` | `unique` | full | **weak** |
| `homonym` | нет marker | any | any | any | **needs_review** |
| pair member (`Nug`, `Yeb`, `Наг`) | второй член пары | `usable` | `unique` | partial | **weak** |
| pair member | нет второго члена | `usable` | `unique` | partial | **needs_review** |
| любой non-canonical | any | any | `shared`/`conflict` | any | **needs_review** |
| любой | any | `degraded_*` | any | any | **needs_review** |

Пояснения:

- `strong` не означает «алиас из каталога». Это имя, которое само является
  якорем: canonical или уникальный strong alias. Generic `The Gate` не может
  быть strong, даже если он формально в каталоге.
- `weak` — не «плохая строка» и не удаление. Это «сущность вероятна, но
  строка сама по себе не доказательство». В UI она видна с бейджем.
- `needs_review` — не silent filter. Строка остаётся в raw/review с причиной.
- `partial` — отдельный match-статус; он не покрывается trust-уровнем. Строка
  `Nug` без `Yeb` — partial + needs_review, а не full/strong.

## 3. Причины (`trust_reason`)

Рекомендуемый enum, без свободного текста в основном CSV:

- `canonical_name`
- `strong_unique_alias`
- `marker_present`
- `partial_pair_with_marker`
- `generic_without_marker`
- `partial_pair_without_marker`
- `title_or_epithet`
- `homonym_unresolved`
- `owner_shared`
- `owner_conflict`
- `source_lexical_corruption`
- `source_ocr_degraded`
- `source_degraded_unknown`
- `unknown_alias_class`

Одна строка может иметь несколько причин; sidecar хранит основную и
дополнительные, если нужно. Не смешивать `trust_reason` с `theme` или
`density`.

## 4. Куда выводить metadata

Не менять текущий `entities_table.csv` (8 колонок, frozen contract).
Предлагается sidecar:

```
entities_trust.csv
key: file;num;entity;variant;line
columns: trust;trust_reason;alias_class;source_quality;owner_status;match
```

- Sidecar строится рядом с выводом; отсутствие sidecar не меняет поведение
  старого клиента.
- UI показывает badge `strong` / `weak` / `needs_review`.
- Raw/review всегда доступен; `needs_review` не исчезает.
- Default view (strict / trusted / current) **не выбирается этим документом**;
  это продуктовое решение после второго probe. До него нельзя silently
  фильтровать.
- Если позже понадобится колонка в отчёте, это новое поле/версия, а не
  тихая замена существующего CSV.

## 5. Связь с уже измеренными baseline’ами

Первый probe:

| Baseline | P | R | Что означает |
|---|---:|---:|---|
| Current | 0,347 | 1,000 | всё видно, доверия мало |
| Strict canonical-only | 1,000 | 0,588 | высокое доверие, теряет 41% good |
| Intermediate | 0,750 | 0,882 | не проходит P≥0,85 |
| TitleGate | 0,436 | 1,000 | недостаточно |
| Intermediate+Q (post-hoc) | 0,882 | 0,882 | не gate; нужен targeted probe |

Выводы:

- Strict — возможный консервативный режим, но не автоматическая замена:
  потеря 41% good-строк существенна.
- Intermediate как есть не проходит. `needs_review` не должен использоваться
  как скрытый способ поднять precision: если строки просто исчезают, это
  другой продукт.
- Trust-уровни объясняют, **почему** строка в weak/review; они не являются
  третьим baseline’ом вместо strict.

## 6. Targeted probe 2 (предложение; не запущен)

Запускать только после решений по source batch 35 и catalog correction.
Иначе состав `weak`/`strong` изменится между freeze и разметкой.

**Обновление 2026-10-27:** source B и 12 catalog corrections решены; generator
policy — design only. Актуальный дизайн одного sample с incremental-
конфигурациями и порогами — [`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md).
Важно: primary sample source-usable не измеряет source B; для B нужен
отдельный source cell/full source check. Это ограничение нельзя обходить
постфактум.

### 6.1. Состав

- 50 строк: **25 weak/composite + 25 strong**.
- Только **non-canonical** строки; canonical уже 10/10, его не перепроверяем.
- Только `source_quality=usable`: source batch 35 и явная OCR-порча
  исключены; source-дефект измеряется отдельно, а не маскируется trust-правилом.
- Только `owner=unique`: shared/ambiguous уже дали 7/10 misleading; они
  остаются `needs_review`, их не перепроверяем.
- Внутри каждой страты — фиксированная стратификация по marker:
  **13 с marker / 12 без marker** (или наоборот; число фиксируется до
  выборки). Без marker weak-строка должна уходить в `needs_review`.
- Один файл — не более одной строки; исключить 20 pilot + 50 probe-1 строк;
  seed, дата, catalog hash и source-policy hash фиксируются до разметки.
- Это targeted probe, не population estimate. Доли 25/25 не являются долями
  корпуса.

### 6.2. Baselines на тех же строках

1. **Current** — reference.
2. **Strict canonical-only** — дешёвый non-ML baseline; P≥0,95,
   canonical loss≤5% (порог из первого probe).
3. **Strong-only** — canonical + unique strong alias, source usable, unique
   owner; P/R report only, без отдельного gate-порога.
4. **Trusted = strong + weak-with-marker** — кандидат; P≥0,85, R≥0,80
   (порог Intermediate, но теперь без source/shared-шума).
5. **No-trust-filter** — все строки, включая `needs_review`; reference для
   того, сколько пользователь видит.

`needs_review` не входит в precision trusted-view, но его доля и причины
reportятся отдельно. Нельзя объявлять gate, если `needs_review` стал тихим
удалением.

### 6.3. Обязательный cheap baseline

Pre-registration должна включать минимум один дешёвый non-ML baseline до
разметки: strict canonical-only, детерминированный marker/title-gate и/или
file-level детектор лексической подстановки (`Dee/Kent/irem/Ohio/Mu/thran`).
Это правило из Phase C §5.9 и
[`OUTPUT_QUALITY_AUDIT.md`](OUTPUT_QUALITY_AUDIT.md) §8. Отчёт обязан указать
команду, scope, фильтры и counts: сколько строк/файлов исключено, сколько
осталось в каждой страте. Source-детектор — фильтр источника, не замена
row-level readability.

### 6.4. Что считается успехом

- Trusted проходит P≥0,85 и R≥0,80; weak-строки с marker не хуже strong по
  misleading-доле (доверие к ним объяснимо).
- Strict остаётся альтернативой, если P≥0,95 и canonical loss≤5%.
- Если ни один вариант не проходит, не включать default-view; сохранить
  текущий вывод + `needs_review` metadata и выбрать другой продукт-компромисс.
- Никаких выводов по 25/25 без confidence interval: 50 строк дают широкий
  интервал; решение — по заранее зафиксированным counts, не по точечной
  оценке.

## 7. Чего этот дизайн не делает

- Не меняет каталог, owner-таблицы, graph-label set.
- Не удаляет и не скрывает строки автоматически.
- Не вводит HomonymClassifier. Он остаётся возможным только для остаточных
  конкретных случаев после probe, не как абстрактное улучшение.
- Не переоткрывает Phase C и не трогает ONNX/BoW/hybrid.
- Не выбирает strict-view по умолчанию. Это решение после probe 2.
- Не заменяет source/catalog audit: `needs_review` лишь объясняет, почему
  строка не стала strong/weak.

## 8. Следующий шаг

1. Source batch 35 проверен 35/35; file-level детектор даёт ровно 35 файлов
   и 0 из 267. Выбран **B**: sidecar, 267 — `unknown`, не clean; дизайн —
   [`OUTPUT_SOURCE_POLICY.md`](OUTPUT_SOURCE_POLICY.md). 302 не использовать
   как список exclusion.
2. Catalog correction: **12 операций одобрены и применены** (13 alias
   strings; 6 owner_reassign + 5 misattribution_removal + 1 homonym_removal),
   3 HOLD и `Leng` no-op; новый hash и residual surfaces —
   [`OUTPUT_CATALOG_AUDIT.md`](OUTPUT_CATALOG_AUDIT.md) §10.5. Post-catalog
   output снят (**33 430 строк**, hash §10.6); для probe 2 он является C2,
   не C0, поэтому sample нельзя заморозить без выбора pre-change baseline (§1.1
   probe 2).
3. Generator policy — design only: 14 composite names, full phrase only,
   exception list; [`OUTPUT_GENERATOR_POLICY.md`](OUTPUT_GENERATOR_POLICY.md).
4. Зафиксировать targeted probe 2: seed, manifest, catalog/source hashes,
   25+25, 13/12 marker cells, incremental C0–C5, baselines и пороги; дизайн —
   [`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md).
5. Разметить author-side, затем blind-подмножество; только после этого —
   решение о trusted-view/sidecar.

До шага 5 production-код и source-файлы не меняются; каталог изменён только
12 approved операциями. Primary sample source-usable не измеряет source B;
нужен отдельный source cell/full source check, иначе B нельзя объявлять
полезным. Post-catalog output уже снят, но incremental catalog требует
pre-change C0/control run; без этого sample не фиксируется.
