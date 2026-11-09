# Phase C: retrieval соседей — критерии, результаты и scope

**Статус (2026-10-25): CLOSED — Phase C delivered the infrastructure; the
current corpus does not justify the feature.** Preflight, historical gate и
результаты сохранены ниже; matrix verdict `GO (full)` — не активное одобрение
ONNX. Операционное решение: §5.6–§5.9. Пересматривать при росте разнообразия
корпуса.

Этот документ фиксирует входной срез, критерий релевантности, размер labeled
set, метрики и пороги **до** первого запуска BoW/ONNX на labeled set. Контракт
CSV, метрика стабильности и зоны overlap уже зафиксированы в
[`EMBEDDINGS.md`](EMBEDDINGS.md#phase-c-контракт-semantic_neighborscsv-до-кода);
здесь они не переписываются, а получают конкретные значения для этого прогона.

Дата фиксации: 2026-10-25.

## 0. Решение preflight

- Pinned-срез: **1 000 файлов**, `35 ru + 227 mixed + 738 en`, manifest SHA-256
  `EA2AC56494EDEB8193D9E1C464F19F5934B8CC226363F510B4EB13A148DA4E95`.
- Minimal-прогон по срезу: **880 строк, 30 сущностей, 387 файлов с
  упоминаниями**, 7.67 с; `entities_table.csv` SHA-256
  `1561C5949C32B3F49C32B49D66A4BCECCAE61118AB604C4EC4926F0E55337FB5`.
- Cross-lingual existence check: **11 проверенных пар** «ru-форма ↔ en-форма,
  один объект, разные документы» (fixture
  `test-fixtures/phase-c-corpus-1000/cross-lingual-pairs.json`). По
  зафиксированному дереву решений это `≥ 10`, поэтому **cross-lingual остаётся
  главным go/no-go**, а не второстепенной метрикой.
- Старый 1 000-файловый performance-срез (`968 en + 3 ru + 8 mixed`; ещё 21
  `unknown`/`skip`) даёт только **4 из 11** точных пар; он не используется для
  Phase C. Это не «корпус плохой», а доказательство, что срез должен быть
  language-stratified, а не first-1000-by-traversal.
- Ограничение каталога: 37 карточек дают 30 фактически найденных сущностей.
  Требование «top-10 stability на 100 сущностях» в текущем каталоге
  невыполнимо. Для этого прогона стабильность считается на **100 query
  contexts** (не более 4 контекстов одной сущности), а не на 100 разных
  сущностях. Это зафиксировано **до** метрик; если каталог расширится до
  ≥ 100 сущностей, протокол переключается на 100 разных `entity_id` без
  изменения формулы.
- Аудит 30 сущностей дал три чистые entity-graph связи после второго прохода:
  `Баст → Гуль` (explicit relation), `Абхот ↔ Уббо-Сатла` (source-specific
  отождествление), `Йогаш-Гуль → Гуль` (type/instance). Этого хватает на
  supplemental 8 пар, но не на 20–30 graph-positive. Поэтому основной labeled
  set — **22 cross-lingual пары** (11 positive + 11 hard negative); entity graph
  идёт отдельным **supplemental** набором 8–12 пар и не подменяет основной
  набор. Форсировать 8–10 graph-positive пар значило бы нарушить §3; лучше
  меньше чистых пар, чем размытая граница.

## 1. Входы и provenance

### 1.1 Корпус

- `test-fixtures/phase-c-corpus-1000/manifest.json` — только пути, размеры,
  SHA-256 и языковой bucket; **тексты корпуса не коммитятся**.
- Отбор: все файлы, классифицированные как `ru` или `mixed` по первому 64 KiB,
  плюс 738 файлов `en`, выбранных детерминированно по возрастанию
  `SHA-256(relative_path)`. Всего 1 000.
- Языковой классификатор: UTF-8/CP1251/CP866, порог 5% кириллических и 5%
  латинских букв; менее 20 букв — `unknown` и не включается. Классификатор
  консервативен: русская книга с длинным английским предисловием может попасть
  в `en`-пул. Это ограничение среза, а не утверждение о языке файла.
- В срезе есть одна пара файлов с одинаковым SHA-256; оба пути сохранены как
  разные элементы корпуса. Для `(entity, context)` dedup это не создаёт
  отдельной сущности; на 1 000 файлов дубль не влияет на решение.
- Материализация для прогона — временный hardlink-каталог; в Git он не
  попадает. Проверка: 1 000/1 000 файлов, размеры и SHA-256 совпали.

### 1.2 Каталог и CLI

- Каталог: приватный `codex.json` (37 карточек), SHA-256
  `B0FF04AE5A2679BA647475CCC40291BD95133BEAC4BF89CA41B64DC607146027`.
  В публичный Git он не добавляется; в manifest/fixture фиксируется только
  хеш.
- CLI: `publish-cli/EntityAnalyzer.Cli.exe`, SHA-256
  `909813835CBE69BC916ACF1FBA3BEF4B67B58366B53A17CC10C4B884221DEF50`.
- Команда preflight:
  `--preset minimal --catalog <codex.json> --formats csv,txt --no-incremental`.
  **Не** `extended`: 37-карточный fuzzy-rescue — известная стоимость, не
  baseline Phase C.
- `entities_table.csv` — вход, а не выход Phase C. Его нельзя перезаписывать
  Phase C; хеш выше фиксирует конкретный preflight-выход. Если extraction
  даёт другой хеш, это новый прогон корпуса, а не изменение retrieval.

## 2. Cross-lingual existence check

Правило: exact или явно склонённая русская форма и canonical английская форма,
в **разных документах**. Общие алиасы (`Книгу`, `Тумана`, `Мерзость`, формы
`Сад`) и формы только на одной стороне не считаются парой. Это защита от
ложных срабатываний catalog aliases.

| # | card_id | ru-форма | ru-документ | en-форма | en-документ |
|---|---|---|---|---|---|
| pcxl-01 | `c_6167639c2c80` | `Йог-Сотота` | `flibusta_downloads/Derlet_Dom-v-doline.gZIS1Q.229561.txt` | `Yog-Sothoth` | `Lovecraft_Wiki/At the Mountains of Madness.txt` |
| pcxl-02 | `c_1de783bbfcd6` | `Наг и Йеб` | `Уилл_Мюррей_О_природе_Нага_и_Йеба.txt` | `Nug and Yeb` | `scraped_texts/Cthulhu_Universalis_Section_N_81d68f626f [Cthulhu Universalis_ Section N.txt].txt` |
| pcxl-03 | `c_1b55915ecdd7` | `Тчо-Тчо` | `flibusta_downloads/Derlet_Sdelka-Sandvina.9FIEAg.229567.txt` | `Tcho-Tcho` | `Lovecraft_Wiki/Call of Cthulhu, End Time.txt` |
| pcxl-04 | `c_68277bce3260` | `Гуль` | `Апокриф писания_a1-10.txt` | `Ghoul` | `Lovecraft_Wiki/The Ghoul.txt` |
| pcxl-05 | `c_ec7836eb27c7` | `Плато Ленг` | `flibusta_downloads/Shvaycer_Ten-smerti.Se7VNw.384240.txt` | `Plateau of Leng` | `Lovecraft_Wiki/At the Mountains of Madness.txt` |
| pcxl-06 | `c_71d00b31c728` | `Уббо-Сатла` | `flibusta_downloads/Mayers_Bobok-Almanah-_4_Rozha-v-alkove.a6OS0w.383433.txt` | `Ubbo-Sathla` | `Lovecraft_Wiki/Grinding Rock.txt` |
| pcxl-07 | `c_ee009e69719f` | `Баст` | `Блох, Роберт. Безликий бог - сборник.txt` | `Bast` | `Lovecraft_Wiki/Bast.txt` |
| pcxl-08 | `c_3615566d6aac` | `Абхот` | `flibusta_downloads/Mayers_Bobok-Almanah-_4_Rozha-v-alkove.a6OS0w.383433.txt` | `Abhoth` | `CthulhuFiles/Cthulhu Universalis_ Section A [Cthulhu_Universalis_Section_A_133c395081.txt].txt` |
| pcxl-09 | `c_c15630411030` | `Гипнос` | `flibusta_downloads/Говард Лавкрафт - Сны Ктулху [сборник, litres с оптимизированной обложкой] [553175].txt` | `Hypnos` | `scraped_texts/Stories_f5b8a5be94 [H. P. Lovecraft, Hypnos.txt].txt` |
| pcxl-10 | `c_e605f8642ebc` | `Мири Нигри` | `Ужас с Холмов (2).txt` | `Miri Nigri` | `Lovecraft_Wiki/The Very Old Folk.txt` |
| pcxl-11 | `c_d10d6ff6d557` | `Книга Эйбона` | `flibusta_downloads/Hembli_Plemyannica-antikvara.MfkkSw.384241.txt` | `Book of Eibon` | `CthulhuFiles/H. P. Lovecraft and Hazel Heald, The Man of Stone [Оригинал.txt].txt` |

Near-misses, которые **не** вошли в 11 и не будут выдаваться за успех:

- `Безымянный Туман` / `Magnum Innominandum`: в русском срезе только общие
  формы `Тумана/Тумане/Туманом`, полного имени нет.
- `Йкнаггнсизсс` / `Dark Star`: только алиасы/перевод, не одна поверхностная
  форма; `Мерзость` — общее слово.
- `Мордиггиан` / `Mordiggian`: русская сторона — перевод `Крысиный Король`;
  тождество требует отдельного источника.
- `Ляо` / `Black Lotus`: русская сторона — перевод `Чёрный Лотос`; тождество
  требует отдельного источника.

Итог: 11 проверенных пар — это `≥ 10`, поэтому cross-lingual остаётся главным
тестом. Это **не** означает, что ONNX уже выиграл: пары доказывают, что
материал для теста есть; сам win ещё не измерен.

## 3. Критерий релевантности (до разметки)

Релевантность бинарная, `1/0`; решение принимается только по свидетельству в
корпусе, а не по «похожести» и не по тому, что выдал BoW/ONNX.

### 3.1 Label 1

Ставится, если выполнено хотя бы одно:

1. **Same referent.** Две записи/контекста обозначают один объект: кросс-языковой
   алиас, транслитерация, перевод или явное отождествление в тексте.
2. **Explicit relation.** В хотя бы одном контексте явно утверждается
   каноническая связь: родство, служение/культ, место—обитатель,
   автор—произведение, тождество, тип/представитель (именованная сущность
   явно названа представителем класса). Простого соседства в предложении или
   списке недостаточно.

### 3.2 Label 0

- **Co-occurrence only**: обе сущности в одном предложении/списке, но связь не
  утверждается.
- **Homonym**: одинаковая поверхностная форма, разные объекты.
- **Similar names**: похожие имена, разные сущности.
- **Topic only**: оба относятся к «Мифам Ктулху», но локального свидетельства
  связи нет.

### 3.3 Ambiguous — исключить

Если для решения нужны внешние знания, источник противоречив или связь
косвенная — пара **не включается** в labeled set и не считается ни `1`, ни `0`.
Лучше 15 чистых пар, чем 25 с размытой границей.

### 3.4 Примеры для калибровки

**Положительные (примеры, не финальные метки):**

- `Йог-Сотот` ↔ `Yog-Sothoth` — same referent, ru/en, разные документы
  (pcxl-01).
- `Наг и Йеб` ↔ `Nug and Yeb` — same referent, ru/en, разные документы
  (pcxl-02).
- `Тчо-Тчо` ↔ `Tcho-Tcho` — same referent, ru/en, разные документы
  (pcxl-03).
- `Баст` → `Гуль` — explicit relation: в `Lovecraft_Wiki/Bast.txt` сказано
  «Bast is also linked with ghouls…»; это не co-occurrence.
- `Абхот` ↔ `Уббо-Сатла` — same referent в конкретном источнике: в
  `Mayers_Bobok...` поздние гиперборейцы называют Уббо-Сатлу «Абхот»; это
  source-specific отождествление, а не глобальная каноническая истина.
- `Йогаш-Гуль` → `Гуль` — type/instance: `Yogash the Ghoul` явно назван
  ghoul’ом; это связь именованного представителя с классом.

**Отрицательные (примеры, не финальные метки):**

- `Гизгут` / `Гуль` — похожие имена, разные сущности.
- `Наггуб` / `Наг и Йеб` — похожие имена, разные сущности.
- `Безымянный Туман` / `Magnum Tenebrosum` — оба «Magnum…», но разные
  сущности; алиасное сходство не делает их одним объектом.
- `Баст` / `Сокар` — оба египетские божества; если в контексте только
  перечисление, без утверждения связи, это `0`, а не `1`.
- Text-level homonym: запрос `наг` (порода собак) против `Наг и Йеб` — `0`;
  это отдельный пример для текстового трека, не пара card_id.

**Ambiguous — исключить:** `Плато Ленг` / `Плато Цанг` (возможное
пересечение), `Мордиггиан` / `Крысиный Король` (перевод vs отдельная
идентичность), `Боги Нежити` / `Йогаш-Гуль` (catalog aliases/conflation;
нужен внешний разбор карточки).

### 3.5 Blind-протокол

1. Сначала выбрать кандидатов и разметить их по контекстам, **не запуская**
   BoW/ONNX на labeled set.
2. Зафиксировать `labels.jsonl` и его SHA-256 в коммите/артефакте.
3. Только после этого считать BoW/ONNX. Любая правка метки после просмотра
   метрик — новая версия labels с новым хешем и полным пересчётом; нельзя
   «поправить одну пару».
4. Два прохода по спорным парам; расхождения — в ambiguous, не в `0`/`1`.
5. Cross-lingual пары из §2 считаются positive по identity, без просмотра
   retrieval-скоров.

## 4. Labeled set: состав и размер

Основной labeled set — **22 пары** (в пределах 20–30): **11 positive + 11 hard
negative** cross-lingual пар. Entity graph идёт отдельным supplemental-набором
**8–12 пар**; он не подменяет основной набор и не суммируется с ним как одна
выборка.

### Track B — cross-lingual text retrieval (решающий тест)

- **11 positive**: по одной паре из §2; query — русский mention-контекст,
  positive — английские mention-контексты **того же `card_id`**. Это identity
  label, а не оценка «похожести».
- **11 hard negative**: для каждого positive-`card_id` — один английский
  mention-контекст **другой** карточки, который co-occurрится с ним в
  pinned-срезе или относится к тому же мифосу, но не утверждает связи. Label
  `0`. Примеры: `Йог-Сотот` → `Уббо-Сатла` (общий список Outer Gods),
  `Тчо-Тчо` → `Гуль` (общий blurb), `Книга Эйбона` → `Черное Солнце`
  (co-occurrence без связи). Спорные пары (`Абхот`/`Уббо-Сатла`, конфликты
  catalog aliases) в negatives **не** берутся.
- Candidate pool — 275 Latin-script mention-строк pinned minimal-прогона
  (после `(entity, context)` уникальности). Query-строки — русские
  упоминания; все кандидаты, не помеченные в 22 labels, считаются `0`
  (closed-world для этого прогона).
- **Представление для gate:** query text — извлечённый русский `variant`
  (например, `Йог-Сотота`), candidate text — `variant` кандидата (например,
  `Yog-Sothoth`). Полный `context` остаётся доказательством метки и отдельным
  diagnostic-прогоном, но **не** решает gate: длинные описательные параграфы —
  известный binary failure mode E5 (`EMBEDDINGS.md` §Phase C); лечение —
  извлечение имени, а не модель больше. Это правило зафиксировано до primary
  прогона; labels и thresholds не меняются.
- Это текстовый трек, а не `semantic_neighbors.csv`: у одной карточки один
  `card_id`, поэтому «сосед» здесь — контекст/упоминание, а не отдельная
  карточка. Метрики те же; CSV-контракт Track A не нарушается.
- 11 positive — existence evidence, а не 11 запросов с одним кандидатом: в
  пуле 275 кандидатов, поэтому P@5 не раздувается. Hard negatives нужны,
  чтобы top-5 не выглядел идеальным только из-за отсутствия конкурентов.

### Track A — entity graph (`semantic_neighbors.csv`, supplemental)

- **8–12 пар**: 3–5 positive + 5 negative, разные `card_id` из 30 сущностей,
  у которых есть контекст в pinned-срезе.
- Query — сущность из 30 найденных; candidate pool — те же 30 сущностей с
  контекстом, кроме самой query. 7 карточек без упоминаний в срезе не
  участвуют: у них нет представления, и выдавать их за кандидатов — значит
  измерять отсутствие данных. Пары, не перечисленные в labels, считаются `0`
  (closed-world для этого прогона).
- Аудит после второго прохода дал три чистые positive-связи:
  `Баст → Гуль` (explicit relation), `Абхот ↔ Уббо-Сатла` (source-specific
  same referent), `Йогаш-Гуль → Гуль` (type/instance). Если чистых positive
  меньше 3, Track A — **HOLD**, а не повод ослабить §3. Cross-lingual Track B
  от этого не блокируется.
- `semantic_neighbors.csv` пишется только для Track A; `query_entity_id` и
  `neighbor_entity_id` — стабильные `card_id`, не номера строк.

Итого: основной набор — **22**; supplemental — **8–12**, отдельно. Primary
меньше 20 невозможен без размывания критерия; если 11 positive/11 hard
negative не набираются, это HOLD и пересмотр §2, а не «решение на малом n».

## 5. Метрики и пороги (зафиксированы до первого score)

### 5.0 Представление текста

- **Primary (gate):** query и candidate — извлечённые `variant` (имя/алиас
  упоминания), не полный абзац. Это то же правило, что в `EMBEDDINGS.md`:
  длинные описательные парафразы не считаются успехом, пока имя не извлечено.
- **Diagnostic (не gate):** полный `context` query/candidate. Он нужен, чтобы
  показать известное ограничение E5; его числа не подменяют primary и не
  двигают thresholds.
- **Track A / CSV:** `query_entity_text` и `neighbor_text` — `variant`
  сущностей; `neighbor_entity_id` — стабильный `card_id`. Полные контексты
  остаются во входной таблице; CSV содержит только variant и scores.
- **Stability:** query — `variant` строки; candidate pool — Track A; top-10
  сравнивается по `neighbor_entity_id`.

### 5.1 Формулы

- `Precision@5` = среднее по запросам `(#relevant в top-5) / 5`.
- `nDCG@10` = среднее `DCG@10 / IDCG@10`; gain бинарный, `DCG = Σ rel_i /
  log2(i+1)`, `IDCG` — идеальный порядок для `min(R, 10)` релевантных.
- Stability: `mean(|top10_bow ∩ top10_onnx| / 10)` по 100 query contexts;
  сравнение по `neighbor_entity_id`, tie-break — ordinal `neighbor_entity_id`;
  candidate pool — Track A, query — извлечённый `variant`.
- BoW и ONNX считаются на **одних и тех же** запросах, кандидатах и labels;
  сырые score не вычитаются и не калибруются между методами.

### 5.2 Абсолютные зоны

| Метрика | fail | acceptable | win |
|---|---:|---:|---:|
| `Precision@5` | `< 0.40` | `0.40–0.65` | `> 0.65` |
| `nDCG@10` | `< 0.45` | `0.45–0.70` | `> 0.70` |

Это ориентиры из preflight; они не двигаются после просмотра чисел. «Win» —
обе метрики в верхней зоне, а не одна.

### 5.3 Относительный gate (обязателен)

ONNX должен **выиграть**, а не «не проиграть»:

- `Δ Precision@5 = ONNX − BoW ≥ +0.10` абсолютных;
- `Δ nDCG@10 = ONNX − BoW ≥ +0.05` абсолютных.

Если BoW = 0.65, а ONNX = 0.68, это **не** win: ΔP@5 = +0.03. Если оба ниже
fail-зоны, это no-go независимо от дельты. Для Track B BoW ожидается около
нуля из-за нулевого лексического пересечения; это ожидание, а не разрешение
считать любой положительный score победой.

### 5.4 Top-10 stability

Зоны уже зафиксированы в `EMBEDDINGS.md`; здесь они не переопределяются:

- `< 30%` — принципиально разные окрестности; нужен labeled set, чтобы понять,
  какая выдача лучше; cross-lingual остаётся отдельным решающим тестом.
- `30–70%` — ONNX дополняет BoW; cross-lingual — главный аргумент.
- `> 70%` — ONNX почти не добавляет информации; полный rollout не
  обосновывается, даже если cross-lingual проходит; максимум — ограниченный
  cross-lingual-сценарий.

### 5.5 Decision matrix

- **GO (full)**: Track B проходит absolute + relative gate; Track A набрал
  ≥ 8 пар и ONNX не хуже BoW более чем на `0.05` по P@5 и nDCG@10; stability
  ≤ 70%.
- **GO (cross-lingual only)**: Track B проходит; Track A хуже, Track A — HOLD
  (< 8 пар), либо stability > 70%. Раскатка ограничивается кросс-языковым
  сценарием; это фиксируется как результат, а не замалчивается.
- **HOLD**: Track B проходит absolute, но не проходит relative gate; или
  primary labeled set < 20 / доля ambiguous > 20%; или не разрешены
  positive/hard-negative пары. Нужно расширить labels, а не объявлять победу.
  Track A HOLD сам по себе не блокирует cross-lingual-only GO, но блокирует
  full.
- **NO-GO**: Track B ниже fail-зоны хотя бы по одной метрике; или relative
  gate не выполнен; или BoW ≥ ONNX на тех же парах. «Интересные числа» не
  заменяют go/no-go.

### 5.6 Зафиксированное scope-решение (2026-10-25)

Матрица §5.5 — исторический pre-registered gate, а не production SLO и не
активное разрешение на ONNX. Post-hoc baseline (§5.7), Track C и проверка
парафраз (§5.8) показали: текущий primary set измеряет **alias coverage, а не
semantic retrieval**.

**CLOSED — Phase C delivered the infrastructure; the current corpus does not
justify the feature.** Это не «ONNX on hold»: для этого корпуса результат
завершён. Re-evaluate when corpus diversity increases.

| scope | status | meaning |
|---|---|---|
| Phase C semantic retrieval (ONNX) | **CLOSED — delivered, not currently needed** | ONNX остаётся opt-in research path, не default и не production. ONNX semantic capability remains untested on this corpus. |
| ONNX confirmation on the current benchmark | **NO-GO** | n=30–40 на точных aliases подтвердит alias coverage, а не semantic advantage. Текущий primary P@5=0.655 — не semantic evidence. |
| Exact alias lookup (catalog-aware) | **fallback, not a Phase C feature** | Для известных aliases exact canonicalization дешевле ONNX. Это table lookup, не retrieval и не semantic entity resolution. |
| Entity graph (Track A) | **NO-GO / closed on current catalog** | n=3 positive, P@5=0.133. Track C (2026-10-25): лишь 2 strong + 1 semantic среди 28 ONNX-only; оба strong confounded alias-дублями, semantic — аналогия. Не расширять до 15–20 пар на этом каталоге. |
| Full-context retrieval | **NO-GO** | Подтверждено ограничение E5-small: длинные описательные абзацы не лечатся увеличением модели; нужен extracted name. |
| Paraphrase benchmark (30–40 non-alias) | **FAIL — feasibility** | 24/30 черновиков естественны, но только 16 имеют однозначную цель, а 10 — чистые non-alias. Чистый набор 30–40 корпус не поддерживает; labels не создавались. §5.8. |
| `semantic_neighbors.csv` | **EXPLORATORY** | Не anchor, не production; пересчитывать при каждом изменении корпуса, каталога, labels, модели или представления. |

Historical matrix verdict was **GO (full)** by §5.5. It remains a
**topical cross-lingual matching** result, not semantic entity resolution, and
it is not an active ONNX approval. The current benchmark measured alias
coverage, not semantic retrieval; ONNX's semantic capability remains untested
on this corpus. The candidate alias-expanded diagnostic (P@5=0.564) is below
both exact normalization and ONNX, which supports the exact-matching reading
rather than a similarity claim.

**Alias cleanup:** 32 alias strings still belong to ≥2 cards. This is no longer
a Phase C precondition: the corpus does not support a clean paraphrase
benchmark, so cleaning the catalog solely for it would not buy an answer. Move
alias deduplication to the **entity-graph backlog** (§8); do not auto-delete
aliases.

**Reopen conditions (all must hold):**

1. A larger, more diverse corpus supports at least 20 **clean** non-alias
   paraphrase queries with one target card each; run the feasibility probe
   again before writing labels.
2. A new 30–40 query set is frozen before any scoring; exact aliases and
   `query_card_id` are excluded from query construction.
3. The pre-registered Δ thresholds in §5.9 are applied against alias-normalized
   BoW. A marginal result (+0.10…+0.20 P@5) does not justify the complexity;
   only > +0.20 returns ONNX to confirmation.
4. If the new set depends on catalog ownership, aliases for the selected
   targets are resolved and the post-cleanup catalog hash is recorded.

Track C также показал, что 37.4% top-10 overlap нельзя читать как чистое
смысловое согласие: 281/374 общих слотов — BoW score 0; на ненулевых слотах
пересечение 93/1000 (9.3%). Модели **orthogonal on this candidate pool**, а BoW
на cross-lingual неинформативен; «complement» здесь не означает, что гибрид
улучшит результат. Детали: `docs/PHASE_C_TRACK_C.md`.

### 5.7 Alias-normalized BoW baseline (post-hoc, 2026-10-25)

Это **не новый gate и не новые labels**. Точная нормализация известных
catalog aliases применяется к query и candidate text; ambiguous aliases
остаются без изменений, `query_card_id` не используется. Baseline отвечает на
вопрос: «сколько даёт дешёвая alias-normalization без ONNX?»

| method | P@5 | nDCG@10 | correct/55 |
|---|---:|---:|---:|
| raw BoW | 0.091 | 0.084 | 5 |
| **alias-normalized BoW** | **0.745** | **0.897** | **41** |
| ONNX E5-small | 0.655 | 0.747 | 36 |
| candidate alias-expanded BoW (diagnostic upper bound) | 0.564 | 0.690 | 31 |

- Query aliases: 10/11 resolve uniquely, 1 unknown (`Йог-Сотота`); no query was
  ambiguous. Candidates: 263/275 resolve uniquely to their own card, 9 are
  ambiguous, 3 unknown.
- Alias-normalized BoW also moves `pcb-07n` from rank 5 to rank 7; it does not
  add a top-5 hard negative.
- Candidate alias-expanded BoW is a diagnostic only: it augments candidate text
  with aliases of its card and therefore uses card metadata; it is not a fair
  text-only baseline.

`0.745`, а не `1.0`, — это не «exact lookup почти работает»: 10/11 query
aliases резолвятся однозначно, `Йог-Сотота` остаётся unknown, а между
карточками есть коллизии. Но сама операция — поиск в таблице aliases, а не
retrieval по смыслу. ONNX не может выиграть у такой таблицы на задаче «найди
карточку по известному alias»; он может выиграть только там, где alias
неизвестен, а смысл угадывается. Primary set этого не тестировал.

Candidate alias-expanded BoW (`0.564`) ниже и exact normalization (`0.745`), и
ONNX (`0.655`). Это информативнее, чем кажется: если добавить кандидату все его
aliases, он становится «мешком всех своих имён», теряет различимость, а
английский alias запроса матчится со всеми карточками, у которых есть
английские aliases, а не с владельцем. Расширение добавляет шум сильнее, чем
помогает cross-lingual matching. Это аргумент за точную канонизацию, а не за
similarity.

Вывод: на текущем benchmark ONNX **не побеждает** дешёвую
alias-normalization; его преимущество над raw BoW — в основном покрытие
известных aliases. Это не «ONNX проиграл» и не доказательство, что semantic
retrieval не нужен. Это доказательство, что **benchmark measured alias
coverage, not semantic retrieval**: текущий primary set не отделяет semantic
retrieval от alias lookup, а ONNX semantic capability remains untested on this
corpus. Поэтому n=30–40 на той же поверхности отменён; новая non-alias
постановка проверена на feasibility в §5.8 и не поддержана корпусом.

Manifest прогона и хеши: `test-fixtures/phase-c-corpus-1000/phase-c-manifest.json`.
Отчёт: `docs/PHASE_C_RESULTS.md`.

### 5.8 Paraphrase feasibility (2026-10-25, no labels)

До alias cleanup и до любых n=30–40 labels проверено, может ли корпус дать
естественные запросы, которые **не являются точными catalog aliases**.
Это author-side feasibility probe, а не frozen set, не benchmark и не scoring.
Локальная таблица черновиков не коммитится; drafts писались по canonical-именам
и исходным контекстам, alias-таблица использовалась только для post-hoc
проверки. Это **не blind annotation**: автор мог знать часть aliases; counts —
верхняя оценка, а не измерение аннотатора.

- 10 карточек выбраны по покрытию контекстами (не по aliases): top-10 по числу
  `(entity, context)`.
- 30 черновиков, по 3 на карточку, на русском; писались по canonical-именам и
  исходным контекстам, без retrieval.
- **24/30** звучат естественно. Это проходит буквальный порог «20+
  естественных запросов», но не порог пригодного benchmark: у запроса должна
  быть одна карточка-цель. Провал здесь не в естественности, а в
  labelability: 8 из 24 natural имеют больше одной разумной цели.
- **16/30** одновременно естественны и имеют однозначную цель. Остальные
  распадаются на alias-like подсказки и на пересечения: Ghoul / Yogash the
  Ghoul, Ubbo-Sathla / Abhoth, Plateau of Leng / Plateau of Tsang / Monks of
  Leng, Tcho-Tcho / Plateau of Tsang, Book of Eibon / другие гримуары.
- **10/30** естественны, однозначны и не являются alias-like. Для набора 30–40
  пришлось бы повторяться, добавлять alias-подсказки или разрешать несколько
  карточек на запрос.

Вердикт: **FAIL для чистого paraphrase benchmark**. Корпус производит
естественно звучащие фразы, но не 20+ чистых однозначных labels; top-10
карточек — лучшая выборка по покрытию, у остальных контекстов меньше. No labels
were created and no scoring was run; alias cleanup не делался ради benchmark.
Это подтверждает границу текущего корпуса: задача — exact matching, а не
similarity; embedding-based retrieval структурно не получает преимущества там,
где применим exact lookup.

### 5.9 Pre-registration lesson и будущие Δ-пороги

**Pre-registration protected against motivated reasoning. It did not protect
against an unmodeled cheap baseline.** §5.5 сравнил ONNX с raw BoW, но raw BoW
на cross-lingual вариантах имён неинформативен; exact alias normalization была
очевидной дешёвой альтернативой и появилась только post-hoc. **Future
pre-registrations must include at least one cheap non-ML baseline (exact match,
alias normalization, BM25) before declaring a gate.**

Для любого будущего paraphrase run пороги фиксируются **до** scoring. Сравнение
идёт с alias-normalized BoW, не с raw BoW; набор — 30–40 запросов, у каждого
одна карточка-цель; запросы не являются точными aliases, `query_card_id` не
используется; labels замораживаются до первого score.

| Δ P@5 (ONNX − alias-normalized BoW) | решение |
|---|---|
| **> +0.20** | ONNX justified; вернуться к confirmation |
| **+0.10 … +0.20** | marginal; сложность, вероятно, не оправдана |
| **< +0.10** | not justified; Phase C остаётся closed для этого корпуса |

Эти пороги записаны до любого paraphrase run; такого run не было (no such run
has been executed), поэтому они не применялись ретроактивно. nDCG@10 остаётся
описательной метрикой, а не вторым gate. Если feasibility снова даёт FAIL,
пороги не запускают набор: сначала корпус, потом labels.

## 6. Stability: 100 query contexts, не 100 сущностей

Причина: 37 карточек, 30 найденных сущностей. Считать 100 разных `entity_id`
нельзя без выдумывания сущностей; использовать 100 строк и называть их
«100 сущностями» — подмена метрики.

Алгоритм (фиксируется до запуска):

1. Взять уникальные `(entity_id, context)` из pinned minimal-выхода (880).
2. Отсортировать по `SHA-256(entity_id + "\0" + context)`.
3. Жадно брать строки в этом порядке, пока на одну `entity_id` не набрано 4,
   до 100 строк.
4. Для каждой строки — query `variant`, candidate pool Track A; top-10
   BoW/ONNX; overlap по `neighbor_entity_id`.

Метрика называется `top10_overlap_contexts_100`; в отчёте явно указывается
число разных `entity_id` (ожидается 30). Если каталог расширится до ≥ 100
сущностей, тот же код переключается на 100 разных `entity_id`; формула
overlap не меняется.

## 7. Ограничения этапа

- Retrieval реализован только как opt-in `tools/PhaseC`; production-интеграция
  в Core/App/Cli не сделана, ONNX-ссылок вне `EntityAnalyzer.Embedders.Onnx`
  нет.
- Не используется `extended`/37-карточный fuzzy-rescue как baseline.
- Не меняются `entities_table.csv`, smoke/полный anchor, `bench/baseline.json`,
  порог ×1.5.
- Не добавляются тексты корпуса, `.earepro`, `.mcp.json`, `.tessera`, веса
  модели или ONNX-пакеты в публичный Git.
- Не расширяется модель до e5-base/granite ради длинных парафраз: это
  отдельный binary failure mode, лечение — представление запроса.
- Alias-normalized baseline использует точные известные aliases; он не
  проверяет unseen aliases, опечатки и парафразы. Его высокая P@5 — свойство
  текущего frozen set, а не общая оценка alias lookup. Это table lookup, а не
  retrieval; `0.745` не равно `1.0` из-за unknown `Йог-Сотота` и коллизий.
- Paraphrase feasibility — author-side probe: 10 карточек × 3 черновика, один
  reviewer, без inter-annotator agreement; это не blind annotation, автор мог
  знать часть aliases. Counts — верхняя оценка пригодности labels, не метрика;
  таблица черновиков локальная и не является frozen set.
- `semantic_neighbors.csv` — exploratory, не anchor и не production-выход.

## 8. Следующие шаги

1. **Phase C закрыт для текущего корпуса.** Нет активного ONNX confirmation,
   нет n=30–40 на текущей alias-based поверхности, нет production-интеграции.
   Инфраструктура остаётся opt-in и пригодной к переиспользованию.
2. **Reopen только при росте разнообразия корпуса.** Сначала повторить
   feasibility (§5.8): нужны 20+ чистых non-alias запросов с одной целью.
   Если feasibility снова FAIL — не писать labels и не чистить aliases ради
   benchmark. Если PASS — заморозить 30–40 запросов, применить уже
   зафиксированные Δ-пороги §5.9 и сравнить с alias-normalized BoW.
3. **Entity graph backlog:** alias cleanup остаётся нужен **для графа**, а не
   для Phase C. Убрать/разрешить 32 alias-строки, принадлежащие ≥2 карточкам,
   без auto-delete; зафиксировать владельца каждого alias и post-cleanup хеш
   каталога. Затем — новый frozen graph-label set и только потом 15–20 пар.
   Примеры дублей: `Father of Ghouls` (3 карточки), `Cxaxukluth` (2 карточки).
4. **Stability:** Track C выполнен; 37.4% overlap содержит 281/374 общих
   слотов с BoW score 0. Для будущих сравнений считать также overlap только
   на ненулевых BoW-слотах, не заменяя зарегистрированную метрику.
5. **Production:** `VectorIndex` + `OnnxE5Embedder` на extracted names не
   интегрируются. Текущий результат не оправдывает функцию; для известных
   aliases дешевле exact lookup. Пересматривать только по условиям §5.6 и
   после нового non-alias evidence.
