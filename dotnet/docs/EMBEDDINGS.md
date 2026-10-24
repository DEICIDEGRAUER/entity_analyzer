# Смысловые эмбеддинги: контракт Phase B

Статус: Phase A (BoW) закрыта. Phase B реализована как отдельный opt-in:
добавлен проект `EntityAnalyzer.Embedders.Onnx` с ONNX Runtime и
`SentencePieceTokenizer`; Core по-прежнему не содержит ONNX-зависимостей.
BoW остаётся дефолтом, а основной пайплайн анализа не вызывает ONNX.
Веса модели в репозиторий не добавлены.

## Контракт `IEmbedder`

- `Dimensions` положительна и постоянна для экземпляра.
- `Embed` возвращает массив ровно длины `Dimensions` с конечными значениями.
- Реализация детерминирована и не зависит от процесса/культуры.
- `VectorIndex` дополнительно нормализует вектор по L2 и отвергает `NaN`/`Infinity`.

## ONNX-профиль E5

Выбранный профиль — `intfloat/multilingual-e5-small`:

- лицензия MIT, 384-мерный выход, максимум 512 токенов;
- токенизатор XLM-R SentencePiece (`sentencepiece.bpe.model`), не BERT
  WordPiece/`vocab.txt`;
- для retrieval обязательны префиксы `query: ` и `passage: `;
- pooling — среднее по `last_hidden_state` с учётом `attention_mask`,
  затем L2-нормализация;
- пустой или пробельный текст даёт нулевой вектор, а `VectorIndex` на пустой
  запрос возвращает пустой список.

ONNX-конверсия: `Xenova/multilingual-e5-small` (int8), ревизия
`3acb1fa45c83e69002b1641b37f3cccc132cdd63`. Точные размеры и SHA-256
зафиксированы в `models/README.md` и `models/manifest.json`; веса, токенизатор
и конфиги не коммитятся и не входят в publish. Явная загрузка —
`scripts/fetch-model.ps1` / `scripts/fetch-model.sh`.

## Проект `EntityAnalyzer.Embedders.Onnx`

Проект ссылается на Core и только в нём подключены:

- `Microsoft.ML.OnnxRuntime` 1.31.0 (managed API и native-библиотеки в одном
  пакете; отдельный `Microsoft.ML.OnnxRuntime.Managed` не нужен);
- `Microsoft.ML.Tokenizers` 2.0.1 — последняя стабильная версия на момент
  реализации, с `SentencePieceTokenizer.Create(modelStream)`.

Публичная точка входа — `OnnxE5Embedder` с `OnnxE5Options`. Модель и
токенизатор задаются явным каталогом; при отсутствии файла выбрасывается
`FileNotFoundException` с командой `scripts/fetch-model.ps1 -Model e5-small`
(или `.sh` для Linux/macOS). Автоматической загрузки в рантайме нет.

```csharp
using var embedder = new OnnxE5Embedder(new OnnxE5Options
{
    ModelDirectory = Path.Combine(repoRoot, "models", "e5-small-int8"),
});

var query = embedder.Embed("кто такой Наг", EmbeddingRole.Query);
var passage = embedder.Embed("Наг — древний жрец.", EmbeddingRole.Document);
```

Этот код нужно вызывать явно из будущей opt-in точки; текущие App/CLI его не
используют.

### SentencePiece и ID XLM-R

Сырой `sentencepiece.bpe.model` использует ID:

```text
0 = <unk>, 1 = <s>, 2 = </s>, 3 = ',', 4 = '.', ...
```

E5/`XLMRobertaTokenizer` ожидает раскладку Hugging Face `tokenizer.json`:

```text
0 = <s>, 1 = <pad>, 2 = </s>, 3 = <unk>, 4 = ',', 5 = '.', ...
```

Поэтому все обычные SentencePiece-токены сдвигаются на `+1`, а четыре
специальных ID переставляются. `SentencePieceTokenizer` сам этот fairseq-
сдвиг не применяет; `E5Tokenizer` делает его после токенизации. BOS/EOS
добавляются явно (`<s>` и `</s>`), а при обрезке до 512 токенов последняя
позиция всегда резервируется под EOS. `tokenizer.json` не используется
рантаймом и остаётся справочным/тестовым артефактом.

### Входы и pooling

Модель Xenova имеет три входа: `input_ids`, `attention_mask` и обязательный
`token_type_ids` (все `int64`). Эмбеддер передаёт нулевой `token_type_ids`.
Выход — `last_hidden_state` формы `[1, sequence, 384]`.

Сначала каждый токен-вектор умножается на соответствующий attention mask,
затем суммируется по последовательности и делится на число ненулевых
позиций. В текущем пайплайне маска состоит из единиц, поэтому BOS и EOS
**входят** в среднее; это зафиксировано golden-эталоном, а не оставлено на
усмотрение библиотеки. После этого вектор нормализуется по L2; пустая маска
даёт нулевой вектор. CLS pooling не используется.

### Роли запроса и документа

`IEmbedder` оставлен без изменений. Для асимметричных моделей добавлен
опциональный `IRoleAwareEmbedder`:

- `Add` вызывает роль `Document` → `passage: `;
- `Search` вызывает роль `Query` → `query: `;
- BoW этот интерфейс не реализует и остаётся дефолтным.

ONNX-эмбеддер реализует `IRoleAwareEmbedder`; префикс добавляется **до**
токенизации, в тот же SentencePiece-поток. `VectorIndex` не менялся и
автоматически передаёт нужную роль.

### Golden-эталон

В `test-fixtures/onnx-golden/` лежат два независимых артефакта:

- `golden_int8.json` — 12 случаев, сгенерированных Python `onnxruntime` из
  **того же** `model_int8.onnx` и `tokenizer.json`. Python-код явно повторяет
  префикс, BOS/EOS, обрезку с резервом EOS, masked mean pooling (включая
  BOS/EOS) и L2. C#-тест требует cosine > 0.9999 к этим векторам и отдельно
  сверяет `input_ids` с `E5Tokenizer`/`PrepareIds`. Это ловит ошибки в
  pooling и префиксах, которые sanity-тесты формы и детерминизма пропускают.
- `reference_fp32.json` — те же тексты через `sentence-transformers` и
  fp32-модель `intfloat/multilingual-e5-small @
  614241f622f53c4eeff9890bdc4f31cfecc418b3`. Это не проверка C#, а
  количественная база деградации int8. Измерено min `0.979531`, mean
  `0.985313`; поэтому честный порог здесь `0.975`, а не `0.99`.

Состав: ru/en/mixed, каждый текст в обеих ролях; 966-токенный случай с
обрезкой до 512 и сохранённым EOS; почти пунктуационная строка; `ё` и
латинская диакритика. Генератор и версии зафиксированы в
`test-fixtures/onnx-golden/README.md`; веса модели не коммитятся.

### Semantic usefulness

`OnnxSemanticTests` — отдельный model-gated тест, который использует реальный
`OnnxE5Embedder` через `VectorIndex`: документ «Лев Толстой написал роман
„Война и мир“» должен оказаться выше «Александр Пушкин написал роман „Евгений
Онегин“» для запроса «кто написал роман „Война и мир“». Это проверка
полезности, а не чисел: golden уже доказал, что векторы правильные.

Важное ограничение pinned int8-модели: голый запрос `Наг` **не**
разрешает омонимию — в локальном замере cosine с «Наг — порода собак.» равен
`0.906874`, а с «Наг — древний жрец.» — `0.895978`. Поэтому тест не выдаёт
пример с `Наг` за успех и использует однозначный контекст. Это ограничение
качества small-модели, а не ошибка пайплайна; golden-слой его не маскирует.

## Тесты и CI

Без модели (всегда в CI):

- контракт `IEmbedder`: 384 измерения, L2-норма, пустой вход → нулевой
  вектор без вызова токенизатора/сессии;
- `IRoleAwareEmbedder`: `Document` и `Query` дают разные векторы, префиксы
  доходят до токенизатора;
- обрезка сохраняет BOS/EOS;
- отсутствие модели даёт понятную ошибку с командой загрузки;
- чистая проверка fairseq-сдвига SentencePiece ID.

С моделью (model-gated, отдельный CI job):

- `sentencepiece.bpe.model` загружается, BOS/EOS и ожидаемые ID проверяются;
- ONNX-сессия загружается и возвращает нормализованный 384-мерный вектор;
- эмбеддинг детерминирован между запусками и различается для ролей;
- `golden_int8.json`: `input_ids` после `PrepareIds` и векторы совпадают с
  Python-эталоном того же int8-профиля (cosine > 0.9999);
- `reference_fp32.json`: int8-векторы лежат в документированном допуске
  `cosine >= 0.975` к fp32-эталону;
- `VectorIndex` + реальный int8-эмбеддер: правильный документ стоит первым
  для однозначного семантического запроса.

CI job `onnx-model-tests` кэширует `models/e5-small-int8` по хешу
`models/manifest.json`. На PR без модели job пропускает тяжёлые тесты с явным
сообщением; на push в `main` модель может быть однократно скачана через
`scripts/fetch-model.sh` и проверена `-Verify` перед тестами. Это не runtime-
автозагрузка и не блокирует контрибьюторов.

Golden-тесты помечены категорией `OnnxGolden` и запускаются отдельным job
`onnx-golden-tests` на Ubuntu. Это отделяет числовое сравнение от
`onnx-model-tests`, чтобы падение golden не маскировало быстрый feedback
остальных ONNX-тестов. Semantic-тест помечен `OnnxSemantic` и запускается
отдельным job `onnx-semantic-tests`, чтобы падение качества ранжирования не
смешивалось с числовой ошибкой. Обычные job’ы исключают
`Category=OnnxGolden` и `Category=OnnxSemantic`; без весов эти тесты
пропускаются с явной причиной.

## Границы

- `EntityAnalyzer.Core` не получает ONNX-зависимостей.
- `EntityAnalyzer.Embedders.Onnx` — отдельный проект; основной пайплайн,
  CLI и GUI его не ссылаются.
- Автоматической загрузки модели в рантайме нет: только явный
  `scripts/fetch-model.*` и локальный `models/`.
- Дефолтный анализ и `entities_table.csv` не меняются: ONNX — явный opt-in.
- Golden-фикстуры против Python-эталона добавлены отдельным слоем:
  `golden_int8.json` проверяет именно production-int8-пайплайн, а
  `reference_fp32.json` фиксирует деградацию int8 относительно fp32. Это не
  меняет BoW-дефолт и не подключает ONNX к основному пайплайну.
- Semantic-тест подтверждает полезность ranking, но не обещает разрешения
  всех омонимов; ограничение `Наг` зафиксировано выше.

## Текущая проверка

Phase A/Phase B: 882 теста (862 Core + 20 ONNX), из них шесть тестов с моделью
(три обычных ONNX, два golden и один semantic) пропускаются при отсутствии
весов. Локально с `models/e5-small-int8` проходят все 882. Загрузчик проверен в PowerShell и Git Bash: `-RecordHashes` заполнил
manifest/README, `-Verify` офлайн подтвердил все шесть файлов, включая
SHA-256 `model_int8.onnx`. `entities_table.csv` остаётся вне ONNX-пути.
