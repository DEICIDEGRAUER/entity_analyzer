# Phase C Track C: что ONNX добавляет к BoW

Дата: 2026-10-25. Статус: **post-hoc interpretation**, не gate, не новый
labeled set и не production-решение. Метрики Phase C не пересматривались;
labels и candidate sets не менялись.

`phase-c-manifest.json` заморожен **до** Track C и всё ещё содержит прежнюю
формулировку scope «expand or close». Этот отчёт — post-hoc решение; manifest
и его хеш не переписываются.

## 1. Вопрос

Отделить в top-5 каждой модели:

- `both` — пара есть в top-5 BoW и ONNX;
- `ONNX-only` — пара есть только у ONNX;
- `BoW-only` — пара есть только у BoW.

Затем вручную разобрать `ONNX-only` и `BoW-only`, проверить hard negatives и
понять, есть ли у ONNX сценарий шире cross-lingual: intra-lingual semantic или
intra-lingual topical.

## 2. Данные и метод

- 8 Track A queries из `labels-track-a.jsonl`: 3 positive + 5 negative;
  68 union rows.
- 100 stability query contexts; top-10 на модель; 1000 слотов на модель.
- Representation: extracted `variant` (primary); candidate pool — 880
  `(entity, context)` rows.
- Membership пересчитана из evaluator rankings, а не выведена из
  `semantic_neighbors.csv`: CSV не содержит method-rank и не позволяет
  восстановить membership однозначно.
- `ONNX-only` top-5: 28 строк; `BoW-only`: 28; `both`: 12. Сумма 68 совпадает
  с CSV.
- Ручная классификация — консервативная, по canonical/alias-ам и известным
  labels; полные контексты в публичный отчёт не переносятся.
- Команда: `--track-c-out <dir>` пишет локальный `track-c-analysis.json`.
  Это sidecar, не новый CSV-контракт.

### Классы

| класс | значение |
|---|---|
| `strong` | identity/alias или явная source-связь; не обязательно уникальная |
| `semantic` | синоним/атрибутивная аналогия, но не заявленное тождество |
| `topical` | только общий mythos/пантеон/тема |
| `artifact` | alias collision, имя/морфология, descriptor, отсутствие связи |

Язык (`cross-lingual`/`intra-lingual`) считается отдельно по выбранной
поверхности `query variant` и `neighbor variant`. Это важно: «cross-script» не
означает «смысловая cross-lingual связь».

## 3. Главный результат

### 3.1. Track A, 68 union rows

| membership | rows |
|---|---:|
| both | 12 |
| ONNX-only | 28 |
| BoW-only | 28 |

ONNX-only по выбранной поверхности: **12 cross-script / 16 intra-script**.
Ручная классификация 28 ONNX-only:

| relation | cross-script | intra-script | всего |
|---|---:|---:|---:|
| strong | 0 | 2 | 2 |
| semantic | 0 | 1 | 1 |
| topical | 10 | 9 | 19 |
| artifact | 2 | 4 | 6 |
| **итого** | **12** | **16** | **28** |

Вывод: **cross-script ONNX-only — это в основном topical/artifact, а не
cross-lingual semantic**. Все strong/semantic пары оказались intra-script.
Поэтому «ONNX-only в основном cross-lingual» верно только на уровне языка, но
не на уровне смысловой связи.

### 3.2. Positive queries

3 positive Track A queries дают 15 top-5 слотов ONNX; P@5=0.133, то есть
**2/15**:

- `pca-02p`: Abhoth → Ubbo-Sathla, rank 1; alias-identity, **intra-script**.
- `pca-03p`: Yogash the Ghoul → Ghoul, rank 4; **intra-script**, причём query
  содержит слово `Ghoul`; это лексическое попадание, а не семантический поиск.

Оба попадания — intra-script. Cross-script positive-попаданий в Track A на
выбранной поверхности **нет**. Primary-путь при этом остаётся cross-lingual:
36/55 правильных попаданий, все — русский query и Latin candidate pool; это
другой, same-card сценарий, и Track C его не отменяет.

| path | correct top-5 hits | cross-script | intra-script |
|---|---:|---:|---:|
| primary same-card, n=11 | 36/55 | 36 | 0 |
| Track A positive, n=3 | 2/15 | 0 | 2 |

Primary-разбивка «cross/intra» тривиальна: candidate pool состоит только из
Latin-упоминаний. Содержательный вывод даёт Track A: оба его попадания —
intra-script; это **не** подтверждает отдельный intra-lingual semantic
сценарий, а показывает alias/lexical эффект.

Пять Track A negative-запросов — это **negative к конкретной паре**, а не
утверждение, что все остальные соседи нерелевантны. Поэтому ONNX-only пары у
negative queries — unlabeled candidates, а не автоматически false positives.
Отдельно: `pca-07n` (Ghoul ↔ Tcho-Tcho, label 0) не попала в ONNX top-5, но
ONNX поставил на 1-е место Nug and Yeb, у которой есть alias `Father of
Ghouls`. Это **кандидат в пропущенную связь**, а не доказательство качества:
alias продублирован на трёх карточках.

### 3.3. BoW-only: это не cross-lingual сигнал

- 20/28 BoW-only строк имеют score 0; top-5 — детерминированный tie-break.
- Из 16 cross-script BoW-only строк 13 имеют score 0.
- Три ненулевые cross-script строки — хеш-коллизии 256-мерного BoW, не перевод:
  - `The Black Sun` → `То То` (0.577);
  - `The Black Sun` → `Вурдалак` (0.577);
  - `Yog-Sothoth` → `Гули` (1.000).
- Поэтому cross-lingual пары в BoW-only **не являются** признаком alias-бага
  или скрытого перевода. Это артефакт нулевых score/коллизий.

### 3.4. Stability: 37.4% overlap требует оговорки

100 contexts, top-10:

| membership | slots |
|---|---:|
| both | 374 |
| ONNX-only | 626 |
| BoW-only | 626 |

- 281/374 общих слотов имеют BoW score 0.
- 476/626 BoW-only слотов имеют score 0.
- 271/306 cross-script BoW-only слотов имеют score 0; ненулевых cross-script
  BoW-only — 35.
- Итого 757/1000 BoW top-10 слотов имеют score 0.

Значит, зарегистрированные 37.4% — не чистый «обе модели нашли одно и то же».
На ненулевых BoW-слотах общих пар 93/1000 (9.3%); это **диагностика**, не
замена зарегистрированной метрики. 37.4% нельзя использовать как отдельное
доказательство смыслового согласия: большая часть — случайное совпадение
tie-break. Основное доказательство cross-lingual сценария — primary same-card
gate, а не stability overlap.

## 4. Hard negatives

Проверка исправляет прежнюю формулировку:

- ONNX: ни один hard negative не в top-5; ближайший — `pcb-05n`
  (Plateau of Leng → Quachil Uttaus / Treader of the Dust) на rank 8.
  Это тот же mythos index, без явной связи; rank 8 не делает negative
  «medium» и не требует переклассификации.
- BoW: `pcb-07n` (Bast → Mordiggian) стоит на **rank 5**, то есть входит в
  top-5. Утверждение «все 11 hard negatives вне top-5» **неверно для BoW**.
  P@5/nDCG это не меняет (hard negatives нерелевантны), но это top-5 false
  positive BoW, а не semantic match.

## 5. Alias-загрязнение каталога

Это не новый label set, а аудит входа; каталог private, хеш
`B0FF04AE5A2679BA647475CCC40291BD95133BEAC4BF89CA41B64DC607146027`.

- 37 карточек; 1943 уникальных normalized aliases (в среднем ~52 на карточку).
- 32 alias-строки встречаются у ≥2 карточек; 12 aliases длиннее 50 символов;
  600 aliases содержат ≥3 слов; у одной карточки пустой canonical.
- Примеры дублей:
  - `Father of Ghouls`: Nug and Yeb, Gods of the Undead, Naggoob;
  - `Cxaxukluth`: Nug and Yeb и отдельная карточка Cxaxukluth;
  - `The Prowler Among the Tombs`: Gods of the Undead и Shaurash-Ho;
  - `Nyogtha`: Gods of the Undead и Nyogtha;
  - `Not-to-be-Named One`: Yog-Sothoth и Magnum Innominandum.

Почему это важно для Track C:

- `neighbor_text` может быть **именем другой сущности**: `Nug and Yeb` с
  выбранным `Cxaxukluth` — alias-коллизия, а не synonym. Такие строки
  помечены `artifact`.
- Два `strong`-сигнала тоже alias-зависимы: Abhoth/Ubbo делят
  `Unbegotten Source`; у Nug and Yeb есть `Father of Ghouls` (дубль на трёх
  карточках). Они не независимы от загрязнения каталога.
- Поэтому graph-метрики на этом каталоге нельзя расширять до 15–20 пар без
  предварительной чистки aliases: рост n увеличит не сигнал, а alias-шум.

Primary same-card gate страдает меньше: relevance — это `card_id`, а не
конкретный alias. Поэтому P@5 primary остаётся валидным; но alias-шум надо
учитывать при интерпретации любых graph-выходов.

## 6. Ручная таблица ONNX-only (28)

`strong`/`semantic`/`topical`/`artifact` — ручная оценка, не метрика. Строки
с `c_...` вместо canonical — карточки с пустым canonical.

| pair | query → neighbor (`neighbor_text`) | language | relation | evidence |
|---|---|---|---|---|
| pca-01p | Bast → Liao (`Mind-altering substance`) | intra | topical | mythos drug; no Bast relation; descriptor alias |
| pca-01p | Bast → Yog-Sothoth (`Umr at-Tawil`) | intra | topical | shared mythos; Umr at-Tawil is a Yog-Sothoth alias |
| pca-01p | Bast → Ubbo-Sathla (`Ubbo-Sathla`) | intra | topical | shared mythos; no direct relation |
| pca-01p | Bast → Abhoth (`Абхота`) | cross | topical | shared mythos; no direct relation |
| pca-01p | Bast → Nug and Yeb (`Cxaxukluth`) | intra | artifact | alias collision: Cxaxukluth is a separate card |
| pca-02p | Abhoth → Ubbo-Sathla (`Уббо-Сатла`) | intra | strong | source-specific identity; shared Unbegotten Source; positive label |
| pca-02p | Abhoth → Nug and Yeb (`Cxaxukluth`) | cross | artifact | alias collision; card also has Yeb/Abhoth cult aliases |
| pca-02p | Abhoth → Book of Eibon (`The Book of Eibon`) | cross | topical | shared mythos object; no direct relation |
| pca-02p | Abhoth → Yogash the Ghoul (`Yogash the Ghoul`) | cross | topical | shared mythos; no direct relation |
| pca-03p | Yogash the Ghoul → Ubbo-Sathla (`Уббо-Сатлы`) | cross | topical | shared mythos; no direct relation |
| pca-03p | Yogash the Ghoul → Nug and Yeb (`Cxaxukluth`) | intra | artifact | alias collision; no direct relation |
| pca-04n | Ycnagnnisssz → The Black Sun (`The Black Sun`) | intra | semantic | dark-celestial near-synonym; not asserted identity |
| pca-04n | Ycnagnnisssz → Ubbo-Sathla (`Уббо-Сатла`) | cross | topical | shared mythos; no direct relation |
| pca-04n | Ycnagnnisssz → Quachil Uttaus (`Treader of the Dust`) | intra | topical | death/dust theme; no direct relation |
| pca-05n | Magnum Innominandum → `c_d28862a72e46` (`The Prowler Among the Tombs`) | cross | artifact | alias collision: Prowler shared with Shaurash-Ho; empty canonical |
| pca-05n | Magnum Innominandum → Hypnos (`Гипнос`) | intra | topical | shared mythos; no direct relation |
| pca-05n | Magnum Innominandum → Quachil Uttaus (`Treader of the Dust`) | cross | topical | mythos/death theme; no direct relation |
| pca-06n | The Black Sun → Nar-Marratuk (`Нар-Марратук`) | cross | topical | shared mythos; no direct relation |
| pca-06n | The Black Sun → Ubbo-Sathla (`Уббо-Сатла`) | cross | topical | shared mythos; no direct relation |
| pca-06n | The Black Sun → Plateau of Leng (`Плато Ленг`) | cross | topical | shared mythos; no direct relation |
| pca-07n | Ghoul → Nug and Yeb (`Nug`) | intra | strong | explicit `Father of Ghouls` alias; duplicated on 3 cards |
| pca-07n | Ghoul → The Worm that Gnaws in the Night (`грызущие`) | cross | topical | nocturnal/scavenger theme; no direct relation |
| pca-07n | Ghoul → Quachil Uttaus (`Treader of the Dust`) | intra | topical | death/dust theme; no direct relation |
| pca-08n | Yog-Sothoth → Yogash the Ghoul (`Yogash`) | intra | artifact | name-prefix collision; no asserted relation |
| pca-08n | Yog-Sothoth → `c_d28862a72e46` (`Nyogtha`) | intra | artifact | name-suffix collision/duplicate card; no asserted relation |
| pca-08n | Yog-Sothoth → Ubbo-Sathla (`Ubbo-Sathla`) | intra | topical | shared mythos; no direct relation |
| pca-08n | Yog-Sothoth → Tcho-Tcho (`Tcho-Tcho people`) | intra | topical | shared mythos; no direct relation |
| pca-08n | Yog-Sothoth → Abhoth (`Abhoth`) | intra | topical | shared mythos; no direct relation |

## 7. Интерпретация и решение

1. **Primary cross-lingual same-card retrieval:** evidence remains. Track C не
   нашёл причины отменять GO-кандидат. Нужны 30–40 новых frozen labels, потому
   что n=11 даёт широкий CI; текущий P@5 не production-число. Это
   **подтверждение** primary-сценария, а не expansion за счёт intra-lingual
   шума.
2. **Intra-lingual semantic сценарий:** сигнала мало. 2 strong + 1 semantic на
   28 ONNX-only; оба strong alias-зависимы, semantic — атрибутивная аналогия.
   Это не 30–40% сценария и не основание для production graph.
3. **Entity graph (Track A):** **NO-GO / closed на текущем каталоге.** Не
   расширять до 15–20 пар. Сначала почистить aliases и убрать дубли, затем
   новый frozen graph-label set; только после этого — новая попытка.
4. **BoW cross-lingual signal:** в Track A его нет. Cross-script BoW-only —
   score 0 или хеш-коллизии; 37.4% overlap нельзя читать без поправки на
   757/1000 нулевых BoW-слотов. Primary BoW P@5=0.091 — одна query из 11;
   Track C не объясняет её как перевод и не использует для cross-lingual
   вывода.
5. **`semantic_neighbors.csv`:** остаётся exploratory. Track C не добавляет
   новый CSV и не меняет контракт
   `query_entity_id;query_entity_text;rank;neighbor_entity_id;neighbor_text;score_bow;score_onnx`.

## 8. Ограничения

- 8 Track A queries, из них 5 negative; 28 ONNX-only — малая выборка.
- Ручная классификация одного ревьюера, без inter-annotator agreement;
  `strong/semantic/topical/artifact` — качественные классы, не метрика.
- Классы смотрят на выбранную поверхность `variant`, а не на все aliases
  карточки; alias-дубли могут менять поверхность.
- Negative labels утверждают отсутствие связи только для конкретной пары, не
  для всех соседей. ONNX-only у negative query — unlabeled, не false positive.
- Каталог private; отчёт фиксирует counts и имена, но не выкладывает aliases
  целиком и не переносит source contexts.

## 9. Воспроизведение и guard

```powershell
dotnet run --project tools/PhaseC -- `
  --corpus <pinned>/entities_table.csv `
  --catalog "$env:APPDATA\EntityAnalyzer\codex.json" `
  --model models/e5-small-int8 `
  --manifest test-fixtures/phase-c-corpus-1000/manifest.json `
  --out <local-output> `
  --representation name `
  --stability-queries 100 `
  --max-per-entity 4 `
  --track-c-out <local-output>\track-c
```

`track-c-analysis.json` остаётся локальным (output/ ignored); default CSV
проверен побайтово: `96C3F1BBFD9A1EB2C1898E13059A2BC83DCDFDC1E862A306D8E8BCE6A3AC2D59`.

Frozen artifacts:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase-c-artifacts.ps1
```

Guard проверяет labels, meta, `phase-c-manifest.json`, corpus manifest,
`cross-lingual-pairs.json` и embedded slice hash; private `entities_table.csv`
и `semantic_neighbors.csv` проверяются только если переданы локально. CI
запускает guard как отдельный шаг. SHA-сравнение принимает raw либо
LF/CRLF-нормализованный вариант одного и того же содержимого: Git checkout
может переписать EOL на Windows/Linux; изменение содержимого всё равно
падает.
