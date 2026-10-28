# Trust levels: `strong` / `weak` / `needs_review`

**Статус (2026-10-28): DESIGN ONLY; strong alias требует marker.** Это документ о
том, как объяснять пользователю доверие к строке. Production-код, CSV-контракт,
Phase C и frozen-манифесты не меняются. 12 approved catalog corrections
применены отдельно (§10.5 catalog audit); source-файлы не менялись.
`needs_review` — видимая metadata, не фильтр и не удаление. Probe 2 показал:
strong alias без marker — **4/11 good**, с marker — **11/11 good**; поэтому
старое правило «strong alias = trusted без marker» отменено (§2.1).

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
| `strong_alias` | **явный независимый marker** | `usable` | `unique` | full | **strong** |
| `strong_alias` | **нет marker** | `usable` | `unique` | any | **needs_review** |
| `strong_alias` | — | `degraded_*` | `unique` | any | **needs_review** |
| `weak_composite`/`title`/`generated` | явный marker | `usable` | `unique` | full/partial | **weak** |
| `weak_composite`/`title`/`generated` | нет marker | `usable` | `unique` | any | **needs_review** |
| `homonym` | явный disambiguating marker | `usable` | `unique` | full | **weak** |
| `homonym` | нет marker | any | any | any | **needs_review** |
| pair member (`Nug`, `Yeb`, `Наг`) | второй член пары | `usable` | `unique` | partial | **weak** |
| pair member | нет второго члена | `usable` | `unique` | partial | **needs_review** |
| любой non-canonical | any | any | `shared`/`conflict` | any | **needs_review** |
| любой | any | `degraded_*` | any | any | **needs_review** |

### 2.1. Gate из probe 2

Probe 2, frozen labels (n=50; source-usable; owner unique; C2 view):

| Bucket | n | good | P | Вывод |
|---|---:|---:|---:|---|
| canonical | 3 | 3 | 1,000 | strong |
| strong alias + marker | 11 | 11 | 1,000 | **strong** |
| strong alias без marker | 11 | 4 | 0,364 | needs_review |
| weak + marker | 13 | 5 | 0,385 | **weak, не trusted** |
| weak без marker | 12 | 0 | 0,000 | needs_review |

`trusted`-вкладка = canonical **или** strong alias + marker, только
`source=usable` и `owner=unique`: 14/14 good, P 1,000 (Wilson lower 0,785),
**R=14/23=0,609**. Weak с marker остаётся weak-бейджем, но **не** входит в
trusted: 5/13 good — слишком мало. Это **фильтр для отдельной trusted-вкладки**,
а не metadata и не удаление: raw/review всегда содержит все строки. Старый
критерий R≥0,80 marker-gated strong **не выполняет** (0,609); поэтому это
precision-first режим, а не замена default. Strict canonical-only (3/3,
R=0,130) остаётся явным conservative view. Для trusted P≥0,85 с CI нужен
probe 3, а не перенос точки из 14/14.

Пояснения:

- `strong` не означает «алиас из каталога» и не означает «уникальный alias
  без проверки контекста». Canonical — сам якорь; strong alias требует
  независимого marker. Generic `The Gate` не может быть strong, даже если он
  формально в каталоге.
- `weak` — не «плохая строка» и не удаление. Это «сущность вероятна, но
  строка сама по себе не доказательство». В UI она видна с бейджем; weak +
  marker **не** повышается до trusted (probe 2: 5/13 good).
- `needs_review` — не silent filter. Строка остаётся в raw/review с причиной.
  Strong alias без marker — именно `needs_review`, а не «почти strong».
- `partial` — отдельный match-статус; он не покрывается trust-уровнем. Строка
  `Nug` без `Yeb` — partial + needs_review, а не full/strong.
- **trusted view** — отдельный фильтр (canonical + strong alias marker), не
  default и не замена raw. `needs_review` в нём не исчезает из продукта; он
  остаётся в review-вкладке.

## 3. Причины (`trust_reason`)

Рекомендуемый enum, без свободного текста в основном CSV:

- `canonical_name`
- `strong_unique_alias`
- `strong_alias_marker`
- `strong_alias_without_marker`
- `weak_marker_not_trusted`
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
- **Trusted view** — явный фильтр `canonical OR strong_alias+marker`; это не
  default и не metadata. Он показывает 14/50 sample, P=1,000, R=0,609
  (n=14; CI lower 0,785). Пользователь видит меньше строк, но не получает
  weak/misleading под видом trusted.
- **Conservative view** — отдельный strict canonical-only режим (3/3,
  R=0,130); не заменяет trusted и не является default.
- Default view (strict / trusted / current) **не выбирается этим документом**;
  это продуктовое решение после probe 3/ревью. До него нельзя silently
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

Probe 2 (frozen labels; per-bucket §2.1):

| Bucket | n | P | R к 23 good |
|---|---:|---:|---:|
| C0 current | 47 | 0,362 | 0,739 |
| C2 catalog | 44 | 0,523 | 1,000 |
| trusted (canonical + strong alias marker) | 14 | **1,000** | **0,609** |
| strict canonical-only | 3 | 1,000 | 0,130 |

Главный вывод: strong без marker — 72% good (18/25), поэтому strong-bucket в
старом design не является trusted. Marker-gated strong — 14/14 good, но
R=0,609; это precision-first, не выполнение старого R≥0,80.

## 6. Probe 2 (completed) и probe 3 (proposed)

Probe 2 завершён; freeze и результаты —
[`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md) §9–§11. Source B и 12
catalog corrections решены; generator policy — design only. Для source B
выбран **отдельный source cell**; primary остаётся source-usable. Для catalog
incremental выбран **pre-change C0 + post-catalog C2**. Generator exception
set пуст: все кандидаты HOLD. Эти решения нельзя обходить постфактум.

Probe 3 (если R≥0,80 остаётся обязательным): отдельная marker-gated strong
cell для сужения CI 11/11 и generator-only cell для C3. Это новый sample, не
правка frozen probe 2.

### 6.1. Состав

- 50 строк: **25 weak/composite + 25 strong**.
- Только **non-canonical**, кроме трёх `canonical_postonly` coverage-строк
  (`Ptmak`, `Cxaxukluth`, `Naggoob`): без них C2 recall нового owner не
  измерить. Это зафиксированное исключение, а не общий canonical sample.
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
   canonical loss≤5% (порог из первого probe); отдельный conservative view.
3. **Strong-only** — canonical или unique strong alias + marker, source
   usable, owner unique; P/R report only; strong alias без marker исключён.
4. **Trusted** — тот же strong-only (canonical + strong alias + marker), **без
   weak**; precision-first кандидат. Weak+marker в trusted не входит: probe 2
   дал 5/13 good.
5. **No-trust-filter** — все строки, включая `needs_review`; reference для
   того, сколько пользователь видит; raw/review сохраняется.

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

- Marker-gated strong проходит P≥0,85 по точке (14/14), **но R=0,609 <0,80**.
  Значит, это precision-first вкладка, а не default-view по старому критерию.
  Нельзя одновременно получить P≥0,85 и R≥0,80 из этих бакетов: weak+marker
  добавляет 5 good и 8 не-good, strong без marker — 4 good и 7 misleading.
- Strict остаётся альтернативой, если P≥0,95 и canonical loss≤5%; в probe 2
  strict P=1,000, R=0,130.
- Если R≥0,80 остаётся жёстким требованием, ни один текущий bucket не
  проходит; тогда — targeted probe 3 (marker-gated strong + generator-only),
  а не перенос точки 14/14 в production.
- Никаких выводов по 50 строкам без confidence interval: 14/14 даёт Wilson
  lower 0,785; решение по counts и CI, не по точечной оценке.

## 7. Чего этот дизайн не делает

- Не меняет каталог, owner-таблицы, graph-label set.
- Не удаляет и не скрывает строки автоматически.
- Не вводит HomonymClassifier. Он остаётся возможным только для остаточных
  конкретных случаев после probe, не как абстрактное улучшение.
- Не переоткрывает Phase C и не трогает ONNX/BoW/hybrid.
- Не выбирает strict-view по умолчанию. Это отдельный conservative view;
  решение о default — после probe 3/ревью.
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
   а C0 — pre-change snapshot.
3. Generator policy — design only: 14 composite names, full phrase only,
   approved exception set пуст; [`OUTPUT_GENERATOR_POLICY.md`](OUTPUT_GENERATOR_POLICY.md).
4. Targeted probe 2 **заморожен 2026-10-28**: manifest
   `becb1a83…`, 25+25 primary, 13/12 markers в каждой страте, 12 source-cell,
   12/12 catalog operations; дизайн — [`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md) §9.
5. **Probe 2 завершён 2026-10-28.** Primary: 23 good / 4 ambiguous / 23
   misleading; blind 20 строк, 19/20 совпадений. C0 P=0,362/R=0,739;
   C2 P=0,523/R=1,000; catalog ΔP=+0,161. Source cell: B повышает P с 0,250
   до 0,500 при R=1,000 (n=12). Generator C3 **не evaluable** (0
   generator-only строк в primary). Combined P=0,523 <0,85 → по
   pre-registered rule нужен **trust-level design**, а не новые
   catalog/generator fixes. Результаты —
   [`OUTPUT_QUALITY_PROBE_RESULTS.md`](OUTPUT_QUALITY_PROBE_RESULTS.md) §12.
6. **Per-bucket P (2026-10-28):** canonical 3/3; strong alias + marker
   **11/11**; strong alias без marker 4/11; weak + marker 5/13; weak без marker
   0/12; trusted (canonical + strong alias marker) **14/14, P=1,000,
   R=0,609**. Решение: trusted-вкладка = marker-gated strong, weak не
   повышается; strict canonical-only — отдельный conservative view. Старый
   R≥0,80 не выполняется; если R обязателен — probe 3, не production.
7. **Probe 3 предложение:** отдельная marker-gated strong cell (валидация
   11/11, CI lower 0,741) + generator-only cell (C3); новый sample, не менять
   frozen probe 2. До него production-код не меняется.

По итогам шага 5 production-код и source-файлы не меняются; каталог изменён
только 12 approved операциями. Primary sample source-usable не измеряет source
B; source cell измеряет отдельно. Manifest включает pre-change C0 и
post-catalog C2; после labels он не меняется. Source B/generator defaults не
включены; catalog corrections применены, default view не менялся. Push
docs-результатов — после per-bucket, не production.
