# Generator policy для composite names: аудит, inspection и opt-in реализация

**Статус (2026-10-29): QUALITATIVE INSPECTION + OPT-IN IMPLEMENTATION;
VERIFY/SELFCHECK GREEN; production default не изменён.** Pre-change audit и
post-catalog diagnostic (33 430 строк; §3.5) не переписаны. 12 approved
catalog corrections применены отдельно; source-файлы, `entities_table.csv`,
frozen-манифесты, Phase C и selfcheck baseline не менялись. Policy
реализована как `CompositeVariantMode` / `FullPhraseMorphology` и проверена
тестами. `verify.ps1 -SkipPublish` (2026-10-29): build 0/0, 928 tests passed,
smoke anchor and Phase C green; `--self-check` passed; 5 255 generator-only
baseline unchanged. `CompositeGeneratorPolicy.DefaultMode` остаётся `Legacy`
до отдельного activation-решения; это не скрытая смена default. Exception
list прошёл qualitative inspection 20–30 affected rows (§10), а не probe 3.

Связанные документы:
[`OUTPUT_CATALOG_AUDIT.md`](OUTPUT_CATALOG_AUDIT.md) §9 —
код-путь `EntityCatalog → VariantFactory → RussianMorphology.DeclineCompound`;
[`OUTPUT_SOURCE_AUDIT.md`](OUTPUT_SOURCE_AUDIT.md) — source batch 35;
[`OUTPUT_TRUST_LEVELS.md`](OUTPUT_TRUST_LEVELS.md) — weak/composite и probe 2.

## 0. Короткий вывод

1. В каталоге **37 enabled-карточек**, из них **14 composite names** — у 13
   есть пробел в `Name`, у 13 — в `Canonical`; объединение даёт 14
   (`Боги Нежити` имеет пустой `Canonical`, `Йогаш-Гуль` — пробел только в
   canonical). В pre-change снимке composite-карточки дают **25 581 из
   37 406 строк (68,4%)**; после catalog corrections — **21 588 из 33 430
   (64,6%)** (§3.5).
2. Standalone-формы, реально совпавшие в выводе, есть у **9 из 14** карточек;
   ещё у 5 нет ни одной standalone-строки. В pre-change снимке на них
   приходится **9 311 строк (24,9% вывода; 36,4% composite-строк)**; после
   corrections — **9 312 (27,9%; 43,1%)**.
3. Из этих строк **4 057 — формы, которые одновременно есть в `aliases`**.
   Generator-only policy их не уберёт. Максимальный эффект `full phrase only`
   в pre-change снимке — **5 254 строки (14,0% вывода)**, после corrections —
   **5 255 (15,7%)**.
4. **4 057 alias-overlap и `Leng`** — это уже не generator-only история.
   `Leng` — alias карточки `Плато Ленг`, а не сгенерированная форма; в
   post-catalog выводе его **6 141** (pre-change 6 139). Он не должен исчезнуть
   от смены генератора. Это отдельный catalog/owner и trust-вопрос.
5. Реализация: **full phrase only** для composite names — генерировать
   полные формы (canonical/name и их падежные варианты), не эмитить
   standalone head/tail case-формы. Исключения — только явный data-driven
   список, а не эвристика по длине/роду. Код добавлен opt-in (§11);
   production default остаётся `Legacy`, пока activation не подтверждён
   отдельным selfcheck/output comparison.

Главная граница: **generator fix не является catalog correction**.
`Книгу` есть в каталоге и одновременно генерируется; удаление alias без
generator policy неполно, а generator policy без catalog correction оставит
alias-overlap.

## 1. Метод и рамка

- Pre-change audit input: текущий диагностический full-output, пресет
  `minimal`; **37 406 строк**, **36 сущностей**, **2 615 файлов**.
- Post-catalog input (§3.5): **33 430 строк**, **37 сущностей**,
  **2 597 файлов**; output hash
  `6553A62C7174FCA26B70D9BB45262EF74E5B51856340429975D036269578B27F`.
- Каталог на момент аудита: **37 enabled-карточек**, **1 977 алиасов**,
  SHA-256 `B0FF04AE5A2679BA647475CCC40291BD95133BEAC4BF89CA41B64DC607146027`.
  После approved corrections: 1 964 алиаса, SHA-256
  `95B54F6DBCB3A12CBAD34EAEA57101AAC40226052C3FAFFFB430A320A0AE7A85`;
  counts §2–§3 — pre-change, §3.5 — post-catalog.
- Pre-change output SHA-256
  `3954D596EED2828859113F16C1EBB9EDED45FC1DFC8DAD0DF471FCC66B253685`.
- Аудит выполнен временным console на собранной `EntityAnalyzer.Core.dll`:
  для каждой composite-карточки вызывался реальный
  `VariantFactory.GenerateTwoPass` для `Name` и `Canonical` с пустым
  `Irregular`. Затем формы точно сопоставлялись с `entity`/`variant` в CSV.
  Это read-only проверка; в репозиторий console не добавлен.
- Классификация:
  - **full** — форма содержит всю фразу: пробел/дефис между частями или
    конкатенация обеих частей;
  - **standalone** — форма соответствует только одной знаменательной части
    composite-имени (`Книге`, `Тумана`, `Саду`, `Нага`, `Ленгом`);
  - **alias overlap** — standalone-форма одновременно входит в `aliases`
    карточки; такая строка не исчезнет от одного generator fix;
  - **generator-only** — standalone-форма не входит в `aliases`;
  - **other** — строки карточки, которые не совпали ни с одной
    сгенерированной формой (обычно другие aliases/чужие owner-формы).
- Классификация author-side, не blind. Формы с нулевой поддержкой в выводе
  считаются потенциальными, но не влияют на строки; ниже указаны именно
  наблюдаемые строки. Это не population precision.

## 2. Инвентарь composite names

`Composite` здесь — включённая карточка, у которой `Name` или `Canonical`
содержит пробел. `Наг и Йеб` входит через `и`; `Йогаш-Гуль` — пограничный
случай: дефис в `Name`, пробел в `Canonical`.

| Карточка | Canonical | Алиасов | Строк |
|---|---|---:|---:|
| `Наг и Йеб` | `Nug and Yeb` | 600 | 7 022 |
| `Безымянный Туман` | `Magnum Innominandum` | 28 | 1 373 |
| `Magnum Tenebrosum` | `Magnum Tenebrosum` | 82 | 354 |
| `Червь, грызущий в ночи` | `The Worm that Gnaws in the Night` | 33 | 18 |
| `Сад Йига` | `Garden of Yig` | 33 | 1 155 |
| `Боги Нежити` | `Боги Нежити` (fallback) | 51 | 214 |
| `Черное Солнце` | `The Black Sun` | 13 | 137 |
| `Монахи Ленга` | `Monks of Leng` | 44 | 305 |
| `Плато Ленг` | `Plateau of Leng` | 21 | 6 825 |
| `Плато Цанг` | `Plateau of Tsang` | 23 | 85 |
| `Мири Нигри` | `Miri Nigri` | 19 | 70 |
| `Книга Эйбона` | `Book of Eibon` | 49 | 7 881 |
| `Куачиль Уттаус` | `Quachil Uttaus` | 13 | 87 |
| `Йогаш-Гуль` | `Yogash the Ghoul` | 7 | 55 |
| **Итого** | | | **25 581** |

14 composite-карточек — это 68,4% всех строк вывода. Поэтому generator policy
не может считаться косметикой, даже если она меняет только часть строк.

## 3. Standalone-эффект по карточкам

§3.0–§3.4 — pre-change снимок; §3.5 — тот же аудит на post-catalog выводе.

Столбцы: `SA` — наблюдаемые standalone-формы; `SA rows` — строки на них;
`alias` — из них формы, которые есть в `aliases`; `gen-only` — только
generated; `full` — строки на полных формах; `other` — остальные строки
карточки (aliases/чужие owner-формы), которые generator policy не трогает.

| Карточка | Строк | SA форм | SA rows | alias | gen-only | full | other |
|---|---:|---:|---:|---:|---:|---:|---:|
| `Книга Эйбона` | 7 881 | 7 | 6 311 | 2 626 | **3 685** | 296 | 1 274 |
| `Сад Йига` | 1 155 | 11 | 1 143 | 407 | **736** | 0 | 12 |
| `Безымянный Туман` | 1 373 | 9 | 1 259 | 591 | **668** | 50 | 64 |
| `Наг и Йеб` | 7 022 | 12 | 187 | 54 | **133** | 111 | 6 724 |
| `Плато Ленг` | 6 825 | 2 | 314 | 301 | **13** | 243 | 6 268 |
| `Куачиль Уттаус` | 87 | 4 | 8 | 0 | **8** | 58 | 21 |
| `Монахи Ленга` | 305 | 2 | 79 | 73 | **6** | 7 | 219 |
| `Плато Цанг` | 85 | 3 | 5 | 0 | **5** | 18 | 62 |
| `Червь, грызущий в ночи` | 18 | 1 | 5 | 5 | **0** | 3 | 10 |
| `Magnum Tenebrosum` | 354 | 0 | 0 | 0 | 0 | 7 | 347 |
| `Боги Нежити` | 214 | 0 | 0 | 0 | 0 | 0 | 214 |
| `Черное Солнце` | 137 | 0 | 0 | 0 | 0 | 90 | 47 |
| `Мири Нигри` | 70 | 0 | 0 | 0 | 0 | 66 | 4 |
| `Йогаш-Гуль` | 55 | 0 | 0 | 0 | 0 | 24 | 31 |
| **Итого** | **25 581** | | **9 311** | **4 057** | **5 254** | **973** | **15 297** |

Доли:
- SA rows — **24,9%** всего вывода и **36,4%** composite-строк;
- generator-only — **14,0%** всего вывода и **20,5%** composite-строк;
- alias overlap — **10,9%** всего вывода;
- full — **2,6%**;
- other — **40,9%**; это не «шум генератора», а в основном другие aliases.

В top-4 (`Книга Эйбона`, `Наг и Йеб`, `Плато Ленг`, `Йог-Сотот`) попадают
**3 831 из 5 254** generator-only строк (72,9% эффекта policy); это 13,9%
top-4. `Йог-Сотот` — не composite, поэтому его generator policy не касается.

### 3.1. Что именно генерируется

Наблюдаемые standalone-формы (не весь список кандидатов; только строки,
которые есть в выводе):

- `Книга Эйбона`: `Книгу` (2 586, alias+generated), `Книге` (2 259),
  `Книгах` (546), `Книгой` (441), `Книгами` (436), `Эйбону` (40,
  alias+generated), `Эйбоне` (3). Полные: `Книга Эйбона` 148,
  `Book of Eibon` 148. `other` 1 274 — `Latin` 789, `Atlantean` 235 и
  издания/части, не generator-формы.
- `Сад Йига`: `Саду` (385, alias), `Сада` (290), `Садов` (132), `Садах`
  (122), `Садами` (83), `Садом` (59), `Садам` (23), `Йигу` (22, alias),
  `Йиге` (15), `Саде` (8), `Сади` (4). Полных строк **0**; `other` 12 —
  другие title/aliases. Это generic «сад» плюс имя Йига.
- `Безымянный Туман`: `Тумана` (467, alias), `Тумане` (334), `Туманом`
  (192), `Безымянные` (124, alias), `Туманов` (55), `Туманами` (34),
  `Туману` (32), `Туманах` (17), `Туманам` (4). Полных 50; `other` 64.
- `Наг и Йеб`: `Нага` (54, alias), `Нагов` (36), `Наги` (29), `Йеба` (16),
  `Нагами` (11), `Нагах` (10), `Наге` (9), `Нагу` (6), `Йебу` (5),
  `Нагам` (5), `Нагом` (5), `Йебом` (1); ещё одна конкатенация `НагиЙеб`
  (1) классифицирована как full/malformed, не standalone. Полные 111
  (`Nug and Yeb` 83, `Наг и Йеб` 27, `НагиЙеб` 1). `other` 6 724 —
  `Nug`/`Yeb`, `Zhar`, `Lloigor` и другие aliases; это не generator-эффект.
- `Плато Ленг`: `Ленга` (301, alias), `Ленгом` (13). Полные 243
  (`Плато Ленг` 136, `Plateau of Leng` 103, `Плато Ленга` 4). `other`
  6 268 — прежде всего `Leng` 6 139 (alias), а также `Плато Лэнг`,
  `Плато Тсанг` и др.
- `Куачиль Уттаус`: `Куачиля` 3, `Куачиле` 2, `Уттауса` 2,
  `Куачилем` 1; полных 58; `other` 21.
- `Монахи Ленга`: `Ленге` 73 (alias), `Ленгу` 6; полных 7; `other` 219 —
  титулы/aliases.
- `Плато Цанг`: `Цангом` 3, `Цанга` 1, `Цанге` 1; полных 18;
  `other` 62 — `Tsang` 43 и другие aliases.
- `Червь, грызущий в ночи`: `грызущие` 5 — это alias, generator-only 0;
  полных 3; `other` 10.
- `Magnum Tenebrosum`, `Черное Солнце`, `Мири Нигри`, `Йогаш-Гуль`,
  `Боги Нежити`: наблюдаемых standalone-строк нет. Формы без пробела у них
  могут быть дефисными/конкатенированными полными формами
  (`Magnum-Tenebrosum`), а не head/tail-формами; их нельзя считать
  standalone. У `Черного Солнца` 90 full-строк, у `Мири Нигри` 66, у
  `Йогаш-Гуль` 24; `Боги Нежити` дают 214 `other` (aliases), не generator.

### 3.5. Post-catalog проверка (2026-10-27)

После 12 approved corrections снят новый диагностический output:
**33 430 строк**, **37 сущностей**, **2 597 файлов** (SHA-256
`6553A62C7174FCA26B70D9BB45262EF74E5B51856340429975D036269578B27F`).
Тот же generator-audit, что и выше, даёт:

| Карточка | Строк | SA форм | SA rows | alias | gen-only | full | other |
|---|---:|---:|---:|---:|---:|---:|---:|
| `Книга Эйбона` | 7 881 | 7 | 6 311 | 2 626 | **3 685** | 296 | 1 274 |
| `Сад Йига` | 1 155 | 11 | 1 143 | 407 | **736** | 0 | 12 |
| `Безымянный Туман` | 1 373 | 9 | 1 259 | 591 | **668** | 50 | 64 |
| `Наг и Йеб` | 3 053 | 12 | 188 | 54 | **134** | 116 | 2 749 |
| `Плато Ленг` | 6 793 | 2 | 314 | 301 | **13** | 245 | 6 234 |
| `Куачиль Уттаус` | 87 | 4 | 8 | 0 | **8** | 58 | 21 |
| `Монахи Ленга` | 305 | 2 | 79 | 73 | **6** | 7 | 219 |
| `Плато Цанг` | 119 | 3 | 5 | 0 | **5** | 54 | 60 |
| `Червь, грызущий в ночи` | 18 | 1 | 5 | 5 | **0** | 3 | 10 |
| `Magnum Tenebrosum` | 354 | 0 | 0 | 0 | 0 | 7 | 347 |
| `Боги Нежити` | 188 | 0 | 0 | 0 | 0 | 0 | 188 |
| `Черное Солнце` | 137 | 0 | 0 | 0 | 0 | 90 | 47 |
| `Мири Нигри` | 70 | 0 | 0 | 0 | 0 | 66 | 4 |
| `Йогаш-Гуль` | 55 | 0 | 0 | 0 | 0 | 24 | 31 |
| **Итого** | **21 588** | | **9 312** | **4 057** | **5 255** | **1 016** | **11 260** |

Доли post-catalog:

- composite — **21 588/33 430 = 64,6%**;
- SA rows — **27,9%** всего вывода и **43,1%** composite-строк;
- generator-only — **15,7%** всего вывода и **24,3%** composite-строк;
- alias overlap — **12,1%** всего вывода;
- full — **3,0%**;
- other — **33,7%** всего вывода и **52,2%** composite-строк.

В top-4 (23 468 строк, 70,2% вывода) попадают **3 832 из 5 255**
generator-only строк (72,9% эффекта policy); это **16,3%** top-4.
`Leng` — **6 141** alias-строка, `Nug`/`Yeb` — **1 813** (656 + 1 157),
`Tsang` — 43; они не являются generator-only. Основные изменения против
pre-change: `Наг и Йеб` потерял 3 969 строк (в основном удалённые чужие
aliases), `Плато Цанг` получил 34, `Книга Эйбона` не изменилась.

Главный вывод не меняется: full phrase only убирает около **5,255
generator-only** строк, но не 4,057 alias-overlap и не alias-only `Leng`/
`Nug`/`Yeb`.

## 4. Что даст `full phrase only`

### 4.1. Policy

Для composite-карточек генерировать:

1. exact `Name`/`Canonical`;
2. полные падежные формы фразы, включая согласование (`Книге Эйбона`,
   `Книгу Эйбона`, `Сада Йига`, `Нага и Йеба`, `Плато Ленга`);
3. полные дефисные/конкатенированные формы, если они уже являются
   surface-формами полного имени.

Не генерировать:

- standalone head-noun (`Книгу`, `Сада`, `Тумана`, `Нага`);
- standalone tail-noun (`Эйбоне`, `Йиге`, `Йеба`, `Ленгом`);
- падежные формы отдельной части, если она не входит в явный exception list.

Aliases остаются отдельным слоем: policy не удаляет и не переписывает
`aliases`. Поэтому `Книгу` останется, пока его не уберут из каталога
отдельной операцией; `Leng` останется, даже если generator policy
реализована.

### 4.2. Ожидаемый эффект

Если реализовать только full phrase only и ничего не менять в aliases:

- исчезнут **5 255** generator-only строк (15,7% post-catalog вывода;
  3 832 top-4; pre-change было 5 254 — разница в одну строку);
- останутся **4 057** alias-overlap строк (`Книгу`, `Тумана`, `Саду`,
  `Ленга`, `грызущие` и др.) — это catalog/trust, не generator;
- останутся **11 260** `other` строк (pre-change 15 297), в том числе
  `Leng` 6 141, `Nug`/`Yeb` 1 813 и др.; generator policy их не касается.

Это верхняя граница эффекта policy на post-catalog выводе. Нельзя складывать
5 255 и 4 057 и называть это «generator fix убирает 9 312 строк»: вторые
4 057 не являются generated-only.

### 4.3. Чего policy не исправит

- `Книга Эйбона` в целом: `Книгу` alias 2 586 + `Книгу` generated; без
  catalog correction `Книгу` останется. `Книге`/`Книгах`/`Книгой`/`Книгами`
  исчезнут, если generator-only форма не входит в aliases; в текущем
  каталоге они generator-only.
- `Leng` 6 141 (post-catalog; pre-change 6 139): alias; не генерируется из
  `Плато Ленг` и не должен удаляться как «шум». Это законное имя/регион,
  вопрос owner/shared.
- `Nug`/`Yeb` 1 813 (post-catalog; pre-change 1 764): alias-only, не
  generated; partial pair, а не generator-форма. Policy может убрать падежи
  `Нага`/`Йеба`, но не сами aliases.
- `Сад Йига`: generic `Сад`/`Сада` исчезнут из generator-only, но `Саду` и
  `Йигу` останутся как aliases; `Йиг` — отдельная сущность/owner review, не
  tail-noun этой карточки.
- `Zhar`/`Lloigor` и другие чужие owner-формы: catalog corrections, не
  generator; approved removals уже применены.

## 5. Exception list: только по решению, не по эвристике

Исключение нужно, если standalone surface — **самостоятельное имя/место**, а
не случайная часть composite-фразы. Кандидаты ниже **не одобрены**; это
список для owner-review перед реализацией.

| Composite | Standalone surface | Строк | Статус | Почему |
|---|---|---:|---|---|
| `Плато Ленг` | `Leng` | 6 141 | **candidate exception / HOLD** | alias, не generated; вероятно законное имя региона; нельзя удалять из-за source-порчи |
| `Наг и Йеб` | `Nug` | 656 | **candidate pair exception / HOLD** | член пары; сейчас alias-only, partial; не full-имя пары |
| `Наг и Йеб` | `Yeb` | 1 157 | **candidate pair exception / HOLD** | то же; оба члена пары должны оставаться видимыми как weak/partial, не strong |
| `Плато Цанг` | `Tsang` | 43 | **candidate exception / HOLD** | возможно, отдельное место/провинция; owner-review |
| `Монахи Ленга` | `Ленге` | 73 alias | **shared/HOLD** | место, не монах; owner/shared с `Плато Ленг`; не удалять автоматически |
| `Сад Йига` | `Йиг`/`Йигу` | 22 alias | **owner-review / HOLD** | `Йиг` — отдельная сущность, не часть «сада»; не generator exception, а catalog/owner |
| `Книга Эйбона` | `Эйбон`/`Эйбону` | 40 alias | **owner-review / HOLD** | автор/персонаж, не книга; не exception для book |

Границы:

- `Leng` и `Nug`/`Yeb` — **не** generator-формы; exception list здесь
  защищает aliases от будущей «чистки», а не разрешает генератору эмитить
  head/tail.
- `Йиг` и `Эйбон` не должны становиться exception для composite: это
  отдельные owner-сущности. Если карточки нет, нужен owner decision, а не
  молчаливое оставление generic-формы.
- `Цанг`/`Tsang` и `Ленга` требуют lore/owner review; сейчас HOLD.
- `Червь, грызущий в ночи` содержит относительную конструкцию; `грызущие`
  — alias, но full-phrase policy должна сохранить полную фразу и не
  добавлять новые bare-формы.
- `Йогаш-Гуль` — пограничный дефисный случай; нужно проверить, считается ли
  `Йогаш` самостоятельным именем. В текущем выводе standalone-строк нет;
  не менять до отдельного решения.

**Решение 2026-10-27:** approved exception set — **пустой**. Все перечисленные
кандидаты остаются HOLD/owner-review; design baseline — `FullPhraseOnly` без
исключений. HOLD не означает удаление alias: `Leng`, `Nug`/`Yeb`, `Tsang`,
`Йиг`, `Эйбон` остаются в каталоге и выводе как есть.

## 6. Дизайн реализации (без кода)

### 6.1. API

Добавить data-driven режим, а не жёстко зашитое условие «если пробел, не
склонять»:

```text
CompositeVariantMode:
  FullPhraseOnly   // default для composite
  FullPhrasePlusExceptions
  Legacy           // текущее поведение; только для диагностики/сравнения
```

`EntityDefinition` (или отдельный `VariantPolicy`) получает режим и
`StandaloneExceptions` — явный список surface-форм, которым разрешено
оставаться самостоятельными. Alias-слой (`Irregular`) не меняется.

### 6.2. Генерация

1. `VariantFactory.Generate` для composite вызывает `DeclineFullPhrase`, а не
   `DeclineCompound`.
2. `DeclineFullPhrase` склоняет фразу как единицу:
   - согласует прилагательное с существительным (`Безымянного Тумана`);
   - сохраняет союз `и` и склоняет оба имени (`Нага и Йеба`);
   - для `Плато Ленг`/`Плато Цанг` сохраняет indeclinable `Плато`, склоняя
     вторую часть;
   - для `Книга Эйбона` даёт `Книгу Эйбона`, а не `Книгу`;
   - для дефисного `Йогаш-Гуль` — отдельный тест, без допущения, что
     `DeclineCompound` уже корректен.
3. `HyphenShapes`/concatenated shapes остаются только для полной фразы.
4. Exact `Name`/`Canonical` и `Irregular` добавляются как раньше.
5. `GenerateTwoPass` сохраняет API, но получает `CompositeMode`; `Legacy`
   нужен для сравнения и для тестов, не для production default.
6. Никаких изменений в `RussianMorphology.Decline` для одиночных имён:
   policy применяется только к composite-карточкам, иначе можно сломать
   обычные имена.

### 6.3. Тесты

Для всех 14 composite:

- нет standalone-формы, кроме явно разрешённых;
- есть exact `Name`/`Canonical`;
- есть минимум одна полная падежная форма;
- `Наг и Йеб` сохраняет `и`, а не эмитит `Нага`/`Йеба` отдельно;
- `Книга Эйбона` не эмитит `Книге`/`Книгу`/`Книгах`/`Книгой`/`Книгами`,
  но `Книгу` остаётся, если он всё ещё alias;
- `Leng`, `Nug`, `Yeb`, `Tsang` остаются aliases и не считаются
  сгенерированными; approved exception set пуст, поэтому standalone-форм у
  composite быть не должно (HOLD-формы не удаляются);
- `Legacy` воспроизводит текущий baseline для сравнения;
- счётчик generator-only строк на post-catalog CSV равен **5 255** (pre-change
  5 254); тест должен фиксировать снимок, а не магическое число.

### 6.4. Rollout

1. Design review зафиксирован 2026-10-27; approved exception list пуст,
   остальные кандидаты — HOLD.
2. Реализовать `FullPhraseOnly` за флагом; default не менять до probe 2.
3. Снять current/legacy и full-phrase варианты на одном 50-строчном sample;
   generator contribution считать как **incremental** к source B + catalog.
4. Только после probe 2 решать, включать ли policy по умолчанию.
5. Production default, CSV-контракт, Phase C и frozen-манифесты не трогать
   до этого решения.

## 7. Связь с probe 2 и порогами

Probe 2 (см. [`OUTPUT_QUALITY_PROBE2.md`](OUTPUT_QUALITY_PROBE2.md)) должен
измерять не «всё вместе», а incremental-вклад:

| Конфигурация | Что добавляет |
|---|---|
| Current | baseline |
| + source B | source sidecar/trusted-view filter |
| + source B + catalog | 12 approved catalog corrections |
| + source B + catalog + generator | full phrase only |
| Strict canonical-only | cheap non-ML baseline |

Порог для generator policy — **P +≥0,10 при R≥0,90** (зафиксирован до
разметки). Если суммарный P <0,85, три фикса не решают задачу; нужен
trust-level/product decision.

Для sample важна оговорка: если он набран только из `source_quality=usable`,
source B не может изменить ни одну строку, и его incremental contribution
будет нулевым **по построению**. Source B нужно измерять отдельной
source-cell или на полном выводе; нельзя приписывать ему эффект, которого
не было в sample. Это ограничение вынесено в probe 2 design.

Вторая оговорка: post-catalog output уже включает C2. Решение 2026-10-27:
C0 — pre-change snapshot, C2 — post-catalog; control run не делается. Для
incremental C0→C2 нужны оба хэша и один label set; post-catalog output сам по
себе C0 не является, иначе catalog contribution равен нулю по построению.

## 8. Ограничения

- 14 composite-карточек — по каталогу на момент аудита; при добавлении
  карточек число изменится.
- Строки §2–§3 считаны по pre-change выводу; §3.5 — post-catalog. Это не
  end-to-end recall.
- Standalone-классификация author-side; формы с нулевой поддержкой не
  перечислены; alias overlap и `other` — разные вещи.
- `full phrase only` не является catalog cleanup: 4 057 alias-overlap и
  11 260 other строк остаются в post-catalog снимке (pre-change: 15 297).
- Exception list ограничен inspection-решением: `Ленгом`, `Цанга`, `Цанге`;
  `Leng`, `Nug`/`Yeb`, `Tsang`, `Йиг`, `Эйбон` остаются aliases и HOLD.
- Production default пока `Legacy`; policy реализована opt-in, чтобы не
  ломать selfcheck/guard без отдельного перепрогона.
- Каталог изменён только 12 approved операциями; source-файлы не менялись.

## 9. Probe 2: generator не удалось измерить (2026-10-28)

Frozen probe 2 не содержит **ни одной** generator-only строки: все 50
primary-строк — alias (38), catalog_target (9) или canonical_postonly (3).
Причина: generator-only формы не имеют alias-владельца, поэтому
owner-unique фильтр их исключает. C3 в отчёте равна C2, но это **не**
результат generator policy; C3 помечена `n/e`.

Следствия:

- `FullPhraseOnly` реализован opt-in; default пока `Legacy`, activation —
  отдельное решение после перепрогона guard/selfcheck.
- Для будущего generator probe нужна отдельная generator cell с owner,
  взятым из output-строки, и labels на expected owner; нельзя повторно
  использовать primary 50.
- 5 255 post-catalog generator-only строк — population, не sample; их нельзя
  смешивать с primary precision.
- Решение probe 2: combined C2 P=0,523<0,85 → trust-level design; generator
  не является следующим фиксом.

## 10. Qualitative inspection (2026-10-28)

Inspection выполнен на post-catalog output (33 430 строк; baseline §3.5
даёт **5 255** generator-only строк для composite-карточек; reconciliation с
широким счётчиком 5 269 — §12): 30
representative строк плюс все редкие формы `Куачиль Уттаус` (8), `Монахи
Ленга` (6), `Плато Цанг` (5), `Плато Ленг` (13). Контексты читались
author-side; это не precision-измерение. Повторный form-level пересчёт в
ходе inspection не заменяет baseline и не используется как метрика.

| Наблюдение | Строк | Классификация |
|---|---:|---|
| `Книга Эйбона`: `Книге`, `Книгах`, `Книгой`, `Книгами` | 3 682 | generic head, чужой/общий контекст → **suppress** |
| `Сад Йига`: `Сада`, `Садов`, `Садах`, `Садами`, `Садом`, `Садам` | 709 | generic «сад» → **suppress** |
| `Безымянный Туман`: `Тумане`, `Туманом`, `Туманов`, `Туманами`, `Туману`, `Туманах`, `Туманам` | 668 | generic «туман» → **suppress** |
| `Наг и Йеб`: `Нагов`, `Наги`, `Нагами`, `Нагах`, `Нагам` | 91 | race/naga/чужие омонимы → **suppress** |
| `Книга Эйбона`: `Эйбоне` | 3 | человек Eibon, не книга → **suppress** |
| `Сад Йига`: `Йиге` | 15 | Yig, не сад → **suppress** |
| `Сад Йига`: `Саде`, `Сади` | 12 | персидский праздник / поэт → **suppress** |
| `Монахи Ленга`: `Ленгу` | 6 | регион Leng, не монахи → **suppress** |
| `Плато Цанг`: `Цангом` | 3 | 2/3 человек Chogsum Tsang, 1/3 «Горы за Цангом»; mixed → **suppress** |
| `Плато Ленг`: `Ленгом` | 13 | регион Leng, 13/13 валидны → **exception approve** |
| `Плато Цанг`: `Цанга`, `Цанге` | 2 | место Tsang, 2/2 валидны → **exception approve** |
| `Куачиль Уттаус`: `Куачиля` | 1 | короткое имя, валидно, но редко → **candidate / HOLD** |
| `Наг и Йеб`: `Нагом`, `Йебом`, `Йебу`, `Йеба` | 28 | легитимные члены пары смешаны с `Шуб-Наге`, городом `Кри-Йеб` и race naga → **HOLD, не exception** |

Full-phrase контексты `Куачиль Уттаус`/`Куачил Уттаус` подтверждают, что
полные формы нужны; bare `Куачиля` без второй части — только 1 строка и
остаётся HOLD. `Ленгом`, `Цанга`, `Цанге` — единственные approved
standalone exceptions; они эмитятся **только** для своей карточки, не как
общий tail-генератор. `Leng`, `Nug`, `Yeb`, `Tsang`, `Йиг`, `Эйбон`
остаются aliases и не удаляются.

## 11. Реализация (2026-10-28)

- `CompositeVariantMode` (`Legacy`, `FullPhraseOnly`,
  `FullPhrasePlusExceptions`) и `CompositeGeneratorPolicy` —
  `src/EntityAnalyzer.Core/Variants/CompositeVariantPolicy.cs`.
- `FullPhraseMorphology` — полные формы: `Книге Эйбона`, `Нага и Йеба`,
  `Плато Ленгом`, `Безымянного Тумана`, `Черного Солнца`,
  `Монахов Ленга`, `Куачиля Уттауса`.
- `VariantFactory.Generate/GenerateTwoPass` принимают mode/exceptions;
  `EntityDefinition` получает `CompositeMode` и `StandaloneExceptions`.
- `Legacy` оставлен default в `CompositeGeneratorPolicy.DefaultMode`:
  selfcheck baseline и frozen guards не переписаны. `verify.ps1 -SkipPublish`
  и `--self-check` прошли 2026-10-29, 5 255 generator-only baseline не
  изменился; activation всё равно остаётся отдельным решением/commit, а не
  скрытой сменой default.
- Tests: `FullPhraseMorphologyTests`, `CompositeVariantPolicyTests` (900
  Core tests проходят). Проверяется отсутствие bare head/tail, сохранение
  союза `и`, approved exceptions и Legacy-совместимость.
- Layer check по `OUTPUT_QUALITY_AUDIT.md` §9(d): `EntityCatalog` передаёт
  mode и exceptions в оба вызова (`Canon` и `Name`); `VariantFactory`
  применяет full-phrase branch к composite-форме и не пропускает
  standalone-формы в итоговый `VariantsByEntity`. Более поздний слой не
  добавляет их обратно. Тесты проверяют итоговый `VariantsByEntity`, а не
  только helper, поэтому fix не является dead code. При `Legacy` production
  output остаётся прежним.
- Следующий шаг: при отдельном решении включить
  `FullPhrasePlusExceptions` в `CodexSearchAdapter`/настройке, снять новый
  diagnostic output и сравнить 5 255 generator-only строк; до этого policy
  не влияет на production output.

## 12. Reconciliation 5 255 vs 5 269 (2026-10-29)

Это разные счётчики, а не расхождение в данных. Оба воспроизведены на одном
post-catalog CSV; параметры прогона: 14 composite-карточек, **33 430** строк,
фильтр `entity ∈ composites`, `variant.casefold() ∉ aliases`, full phrase =
пробел или конкатенация имени; независимый локальный Python-счётчик (не
коммитится, приватных имён в отчёте нет). Каталог `codex.json` SHA-256
`95B54F6DBCB3A12CBAD34EAEA57101AAC40226052C3FAFFFB430A320A0AE7A85`,
output SHA-256
`6553A62C7174FCA26B70D9BB45262EF74E5B51856340429975D036269578B27F`.

- **5 255** — `standalone_generated_rows`: variant не входит в alias set и не
  является полной фразой (нет пробела и это не конкатенация имени). Именно это
  число используется как generator-only baseline в §3 и §4.
- **5 269** — все non-alias формы composite-карточек, включая полные фразы,
  которые generator всё равно обязан сохранять. Это широкая проверка
  «сколько строк не являются alias», а не population для удаления.
- Разница **14** строк — ровно полные фразы:

| Карточка | Полная форма | Строк |
|---|---:|---:|
| Куачиль Уттаус | Куачил Уттаус | 9 |
| Плато Ленг | Плато Ленга | 4 |
| Наг и Йеб | НагиЙеб | 1 |
| **Итого** | | **14** |

Проверка по карточкам для 5 255: Книга Эйбона 3 685, Сад Йига 736,
Безымянный Туман 668, Наг и Йеб 134, Плато Ленг 13, Куачиль Уттаус 8,
Монахи Ленга 6, Плато Цанг 5. Сумма — 5 255. Остальные composite-карточки
не дают standalone generator-only строк в этом output.

Правило на будущее: regression-сравнение generator behavior ведётся по
**5 255** при `Legacy`; 5 269 можно использовать только как широкую
диагностику, иначе полные фразы будут ошибочно засчитаны как standalone
шум. В отчётах указывать оба числа нельзя без определения метрики.

## 13. Verification 2026-10-29 (activation not performed)

- `verify.ps1 -SkipPublish`: build 0 warnings / 0 errors; **928/928** tests;
  CLI isolated-corpus, smoke-corpus anchor `E50A1559…`, Phase C guard and
  bench-gate green; real profile untouched.
- `EntityAnalyzer.Cli --self-check` passed in 804 ms.
- Product output with deterministic duplicate representative (SHA-256
  `9444106C4E41B36F8872B6EAEBAA0F949A84485212555AC81C9D6209F50D8BA3`)
  has the same per-entity counts as the frozen `6553A62C…` snapshot, so
  generator-only baseline remains **5 255** / broad **5 269** (14 full
  phrases).
- `CompositeGeneratorPolicy.DefaultMode` remains `Legacy`;
  `FullPhrasePlusExceptions` remains explicit opt-in. No generator activation
  or replacement/default view was shipped.

## 14. Pre-registered activation criterion (2026-10-30)

**One-line gate:** Legacy must reproduce **5 255** on the same corpus/catalog/
preset, and `FullPhrasePlusExceptions` must be a strict superset of Legacy's
`(entity, variant)` multiset with only approved full-phrase additions, zero
standalone/exception regressions, deterministic output, and green
verify/selfcheck.

Operational checks:

1. Same corpus, catalog, preset and cache policy; Legacy first reproduces the
   **5 255** generator-only baseline (5 269 is broad diagnostic only, not the
   acceptance number).
2. `FullPhrasePlusExceptions` preserves every Legacy `(entity, variant)` row;
   additions are limited to approved full phrases. No generated standalone
   head/tail form outside the approved exception set.
3. **0** regressions on the 14 approved exceptions; `Наг и Йеб` keeps `и`;
   HOLDs, `Leng` no-op and the 32 duplicate aliases stay unchanged.
4. Two consecutive runs produce identical `(entity, variant)` multisets;
   output hash may change because approved full phrases are added, so report
   hash plus per-card/count delta rather than requiring the old hash.
5. `verify.ps1 -SkipPublish` and `--self-check` green; no catalog, source,
   frozen-manifest or Phase C changes.
6. Activation is a separate explicit commit changing the default; until then
   `Legacy` remains default and `FullPhrasePlusExceptions` stays opt-in. Any
   failed check means no activation.
