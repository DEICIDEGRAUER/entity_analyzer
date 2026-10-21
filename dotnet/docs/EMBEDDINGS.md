# Смысловые эмбеддинги: контракт Phase B

Статус: Phase A (BoW) закрыта. Phase B остановлена на контракте: ONNX-рантайм,
пакет `Microsoft.ML.OnnxRuntime` и файлы модели ещё не добавлены.

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

ONNX-конверсия: `Xenova/multilingual-e5-small` (int8). Точная ревизия,
размеры и SHA-256 файлов фиксируются в `models/README.md` на шаге загрузки;
модель не коммитится и не входит в publish.

## Роли запроса и документа

`IEmbedder` оставлен без изменений. Для асимметричных моделей добавлен
опциональный `IRoleAwareEmbedder`:

- `Add` вызывает роль `Document` → `passage: `;
- `Search` вызывает роль `Query` → `query: `;
- BoW этот интерфейс не реализует и остаётся дефолтным.

Это не даёт забыть обязательные префиксы E5 на уровне индекса.

## Границы

- `EntityAnalyzer.Core` не получает ONNX-зависимостей.
- Будущий `EntityAnalyzer.Embedders.Onnx` будет ссылаться на Core и
  ONNX-пакеты; ONNX-тесты пойдут отдельным job'ом и будут пропускаться без
  скачанной модели.
- Автоматической загрузки модели в рантайме не будет: только явный
  `scripts/fetch-model.*` и локальный `models/`.
- Дефолтный анализ и `entities_table.csv` не меняются: ONNX — явный opt-in.

## Текущая проверка

Контракт зафиксирован заглушкой в
`tests/EntityAnalyzer.Core.Tests/OnnxEmbeddingContractTests.cs`: 384 измерения,
L2-норма, нулевой пустой вход, различимость ролей `Document`/`Query` и
диспетчеризация ролей в `VectorIndex`. ONNX-пакетов и файлов модели нет.
