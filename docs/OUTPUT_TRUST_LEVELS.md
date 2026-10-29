# Trust levels: `strong` / `weak` / `needs_review`

**Статус (2026-10-29): EXPLORATORY IMPLEMENTATION; SOURCE GATE VERIFIED;
default view не меняется.** Trusted и strict canonical-only добавлены как
**явные additive-фильтры**; raw/review остаётся default. Sidecar
`entities_trust.csv` пишется рядом с основным CSV, основной 8-колоночный
контракт не изменён. Классификатор `TrustViewClassifier` детерминированно
воспроизводит trusted-набор probe 2 (14/14; P=1,000, Wilson lower 0,785), но
это exploratory-эвристика, не production-SLO и не owner-таблица.
Source-гейт требует явный `source=usable`: `unknown` и `degraded_*` остаются
в raw/review. Полный 267-tail sidecar загружается из
`entities_source_quality.csv` (auto в `<Output.Dir>`, explicit
`--source-quality-sidecar`); 267 получают `unknown`, 35 — `degraded_lexical`.
Product run: classifier-only trusted **6 462** → **3 317**; blocked **3 145**
(193 degraded + 2 952 unknown); raw/review сохраняет **33 430** строк.
Duplicate identical content inherits worst source quality, поэтому ранний
diagnostic 3 640 заменён на 3 317 (см. source policy §5.1). Из 14 old probe-2
trusted-строк 13 остаются trusted; P2-29 блокируется, потому что её content
дублируется под 267-tail именем. Это source-gated product view, не
classifier-only probe 2: product trusted-good recall 13/23 (56,5%), тогда как
classifier-only остаётся 14/23 (60,9%); raw/review сохраняет P2-29.
12 approved catalog corrections применены отдельно (§10.5 catalog audit);
source-файлы не менялись.
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

`source=usable` — **единственное** состояние, которое может войти в trusted
или strict canonical-only. `degraded_*` и `unknown` не удаляются из
raw/review и видны там с badge/`needs_review`, но в trusted/strict не
проходят. `unknown` (267 хвостовых кандидатов) — **не** `clean`; отсутствие
sidecar не повышает его до usable. Source-policy design:
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

`trusted`-вкладка = canonical **или** strong alias + marker; в probe 2
дополнительно `source=usable` и `owner=unique`: 14/14 good, P 1,000 (Wilson
lower 0,785), **R=14/23=0,609**. Runtime-классификатор проверяет owner
uniqueness по каталогу, а view/export require `source=usable`: `degraded_*`
и `unknown` дают `review`. Полный 267-tail sidecar загружен в product run,
поэтому detector-negative `usable` больше не обходит гейт; `unknown` не
считается clean. Weak с marker
остаётся weak-бейджем, но **не** входит в trusted: 5/13 good — слишком мало.
Это **additive view**, а не metadata и не удаление: raw/review всегда
содержит все строки.

**Критерий R применён правильно:** R≥0,80 относится к **replacement
default-view** — фильтру, который убирает строки из единственного вывода.
Для additive trusted R отчётный, не gate: 39% good остаются в raw/review и
не теряются из продукта. Поэтому R=0,609 не является провалом дизайна; это
цена precision-first вкладки, а не потеря данных. Если позже trusted захочет
стать default, тогда R≥0,80 снова станет gate.

Trusted выпускается как **exploratory**: P=1,000 по точке, Wilson lower
0,785 — достаточно для явной exploratory-метки, но не для production-SLO.
Probe 3 для marker-gated strong **не нужен**: он подтвердил бы уже
подтверждённое; SLO при необходимости набирается отдельно. Strict
canonical-only (3/3, R=0,130) остаётся отдельным conservative view.

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
Пишется additive sidecar:

```
entities_trust.csv
key: file,num,entity,variant,line
columns: trust,alias_class,marker,source_quality,source_reason,trust_reason,exploratory
```

Фактический header: `file,num,entity,variant,line,trust,alias_class,marker,
source_quality,source_reason,trust_reason,exploratory`. Это additive CSV с
запятыми; основной 8-колоночный `entities_table.csv` не меняется.
`owner_status`/`match` пока не выводятся: owner-таблицы и полный match-статус
не открывались; это не скрытая замена полей.

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
- Default view остаётся **current raw/review**; trusted и strict — отдельные
  additive exploratory-вкладки. Нельзя silently фильтровать default.
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
старом design не является trusted. Marker-gated strong — 14/14 good,
P=1,000; R=0,609 **не gate для additive view** и не «провал». Это
precision-first exploratory-вкладка; raw/review сохраняет остальные good.
R≥0,80 применяется только если trusted когда-нибудь заменит default.

## 6. Probe 2 (completed); probe 3 не нужен

Probe 2 завершён; freeze и результаты —
[`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md) §9–§11. Source B и 12
catalog corrections решены; generator policy реализована opt-in, default —
`Legacy`. Для source B выбран **отдельный source cell**; primary остаётся
source-usable. Для catalog incremental выбран **pre-change C0 + post-catalog
C2**. Изначально exception set был пуст (все кандидаты HOLD); после отдельного
qualitative inspection одобрены только `Ленгом`, `Цанга`, `Цанге`. Это не
probe 3 и не меняет frozen labels; C3 остаётся n/e.

Probe 3 **не нужен** для marker-gated strong: 14/14 уже достаточно для
exploratory, а добор n ради CI lower≥0,85 не даёт продуктового решения.
Generator также не измеряется через probe 3: вместо него — qualitative
inspection 20–30 generator-affected строк и exception list. Это отдельный
technical-debt трек, не новый 50-строчный sample.

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

- Marker-gated strong проходит P≥0,85 по точке (14/14). R=0,609 **отчётный,
  не gate** для additive view: raw/review сохраняет все строки, поэтому
  replacement-критерий R≥0,80 к trusted не применяется.
- Если trusted когда-нибудь станет **заменой default**, R≥0,80 снова станет
  gate; тогда одного marker-гейта недостаточно (нельзя одновременно получить
  P≥0,85 и R≥0,80 из этих бакетов). Это отдельное продуктовое решение, не
  блокер текущей exploratory-вкладки.
- Strict остаётся альтернативой, если P≥0,95 и canonical loss≤5%; в probe 2
  strict P=1,000, R=0,130.
- Никаких выводов по 50 строкам без confidence interval: 14/14 даёт Wilson
  lower 0,785; exploratory-метка честна, production-SLO — нет. SLO
  набирается отдельно, не через probe 3 «для доказательства P=1.0».

## 7. Чего этот дизайн не делает

- Не меняет каталог, owner-таблицы, graph-label set.
- Не удаляет и не скрывает строки автоматически.
- Не вводит HomonymClassifier. Он остаётся возможным только для остаточных
  конкретных случаев после probe, не как абстрактное улучшение.
- Не переоткрывает Phase C и не трогает ONNX/BoW/hybrid.
- Не выбирает strict-view по умолчанию. Это отдельный conservative view;
  default остаётся текущим raw/review. Strict — exploratory, не замена.
- Не заменяет source/catalog audit: `needs_review` лишь объясняет, почему
  строка не стала strong/weak.

## 8. Следующий шаг

1. Source batch 35 проверен 35/35; file-level детектор даёт ровно 35 файлов
   и 0 из 267. Решение **B** реализовано: полный sidecar, 267 — `unknown`,
   не clean; product run подтвердил trusted 3 317 / raw 33 430. Контракт —
   [`OUTPUT_SOURCE_POLICY.md`](OUTPUT_SOURCE_POLICY.md). 302 не использовать
   как список exclusion.
2. Catalog correction: **12 операций одобрены и применены** (13 alias
   strings; 6 owner_reassign + 5 misattribution_removal + 1 homonym_removal),
   3 HOLD и `Leng` no-op; новый hash и residual surfaces —
   [`OUTPUT_CATALOG_AUDIT.md`](OUTPUT_CATALOG_AUDIT.md) §10.5. Post-catalog
   output снят (**33 430 строк**, hash §10.6); для probe 2 он является C2,
   а C0 — pre-change snapshot.
3. Generator policy — **opt-in implementation**: 14 composite names,
   `CompositeVariantMode`, `FullPhraseMorphology` и approved exceptions
   (`Ленгом`, `Цанга`, `Цанге`); production default пока `Legacy`, чтобы не
   ломать selfcheck/guard без отдельного перепрогона. См.
   [`OUTPUT_GENERATOR_POLICY.md`](OUTPUT_GENERATOR_POLICY.md) §8.
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
   повышается; strict canonical-only — отдельный conservative view.
   **Trusted — additive exploratory view**, не default; R отчётный, не gate.
   Probe 3 для marker-gated strong не нужен.
7. **Generator:** не probe 3, а qualitative inspection 20–30
   generator-affected строк в полном diagnostic output; inspection выполнен
   (30 representative + all rare forms), exception list зафиксирован, код
   full-phrase-only реализован opt-in. Это отдельный technical-debt трек,
   не смешивать с release trusted view.

По итогам шага 5 production-код и source-файлы не меняются; каталог изменён
только 12 approved операциями. Primary sample source-usable не измеряет source
B; source cell измеряет отдельно. Manifest включает pre-change C0 и
post-catalog C2; после labels он не меняется. Source B/generator defaults не
включены; catalog corrections применены, default view не менялся. Push
docs-результатов — после per-bucket, не production.

## 9. Реализация exploratory view (2026-10-28)

Код:

- `src/EntityAnalyzer.Core/Analysis/TrustView.cs` — детерминированный
  `TrustViewClassifier`: canonical exact; strong alias с полной фразой или
  независимым anchor; generic/title/частичные формы остаются weak.
- `src/EntityAnalyzer.App/ViewModels/ResultsViewModel.cs` — явные режимы
  `Trusted (exploratory)` и `Canonical only (exploratory)`. Default остаётся
  `Все`; raw/review не меняется.
- `src/EntityAnalyzer.Core/Persistence/ReportExporter.cs` — additive
  `entities_trust.csv` (file,num,entity,variant,line,trust,alias_class,marker,
  source_quality,source_reason,trust_reason,exploratory). Основной
  `entities_table.csv` остаётся 8-колоночным.
- `src/EntityAnalyzer.Core/Analysis/SourceQuality.cs` — file-level detector
  только для воспроизводимой batch-35 сигнатуры (шесть маркеров + density
  ≥1%); `unknown` не повышается до clean.
- `src/EntityAnalyzer.Core/Analysis/SourceQualitySidecar.cs` — полный
  file-level sidecar: 35 degraded + 267 unknown, missing→unknown,
  sidecar>detector. `AnalysisEngine` применяет его после cache lookup и
  наследует худший source для identical duplicate content.
- `tests/.../TrustViewClassifierTests.cs` — проверки canonical, full-phrase,
  marker, weak+marker, homonym, pair-member и shared owner. `SourceQualityTests`
  и `SourceQualitySidecarTests` — batch-35 signature, precedence,
  missing→unknown, duplicate conflict. `AnalysisEngineTests` — cache-hit
  override и duplicate worst-source. Все **900+** Core tests проходят.
- `TrustViewClassifier` строит reverse owner index: alias/name/canonical,
  принадлежащий более чем одной карточке, получает `weak` с причиной
  `shared alias/owner conflict`. Повторная проверка на frozen probe 2 после
  этого ограничения дала те же 14/14 trusted строк.

Ограничения:

- Это lexical heuristic, не owner-таблица. Owner uniqueness проверяется по
  каталогу, но alias ownership не является полной owner-моделью. На population
  результат не измерен и остаётся exploratory.
- Source-гейт в UI и `entities_trust.csv` требует `source=usable`;
  `unknown`/`degraded_*` остаются в raw/review с badge. Полный 267-tail
  sidecar загружен; duplicate content наследует худший source, поэтому
  detector-negative `usable` больше не обходит гейт.
- `entities_trust.csv` ставит `trust=trusted` только для classifier-trusted
  строк с source `usable`; для `degraded_lexical` пишет `review` и
  `trust_reason=lexical trust blocked by degraded source`, для `unknown` —
  `review` и `trust_reason=trust blocked by non-usable source (unknown)`.
- Strict canonical-only — отдельная exploratory-вкладка, не default и не
  замена trusted.
- Selfcheck/frozen baselines не переписывались. Trusted-фильтр не меняет
  ни одного сохранённого CSV/манифеста; sidecar добавляется только при
  экспорте с codex.
