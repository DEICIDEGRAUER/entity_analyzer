# ONNX-модели

Модели **не коммитятся** в репозиторий. В git хранятся только этот README и
машиночитаемый манифест `manifest.json`; сами веса и токенизатор лежат в
`models/` и игнорируются.

## Профиль по умолчанию

- Модель: `intfloat/multilingual-e5-small`
- ONNX-конверсия: `Xenova/multilingual-e5-small`
- Ревизия: `3acb1fa45c83e69002b1641b37f3cccc132cdd63`
- Precision: `int8`
- Лицензия: MIT (исходная модель и ONNX-конверсия)
- Назначение: `models/e5-small-int8/`
- Размер: около 118 MB для весов + токенизатор и конфиги

Модель распространяется отдельно от кода под MIT. Ссылка на оригинал:
<https://huggingface.co/Xenova/multilingual-e5-small/tree/3acb1fa45c83e69002b1641b37f3cccc132cdd63>.

## Манифест хешей

<!-- BEGIN MODEL HASHES -->
| Файл | Размер | SHA-256 | Лицензия | Источник |
|------|-------:|---------|----------|----------|
| `onnx/model_int8.onnx` | 118054593 B (112.6 MiB) | `4d24e2bc01a447951524466ef533e52944bf48509e6552810bcee1a2711cb02c` | MIT | `Xenova/multilingual-e5-small @ 3acb1fa45c83e69002b1641b37f3cccc132cdd63` |
| `tokenizer.json` | 17082730 B (16.3 MiB) | `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39` | MIT | `Xenova/multilingual-e5-small @ 3acb1fa45c83e69002b1641b37f3cccc132cdd63` |
| `tokenizer_config.json` | 443 B | `a1d6bc8734a6f635dc158508bef000f8e2e5a759c7d92f984b2c86e5ff53425b` | MIT | `Xenova/multilingual-e5-small @ 3acb1fa45c83e69002b1641b37f3cccc132cdd63` |
| `special_tokens_map.json` | 167 B | `d05497f1da52c5e09554c0cd874037a083e1dc1b9cfd48034d1c717f1afc07a7` | MIT | `Xenova/multilingual-e5-small @ 3acb1fa45c83e69002b1641b37f3cccc132cdd63` |
| `config.json` | 658 B | `cb99455288675345e1a4f411438d5d0adbba5fbd3a67ea4fb03c015433b996c1` | MIT | `Xenova/multilingual-e5-small @ 3acb1fa45c83e69002b1641b37f3cccc132cdd63` |
| `sentencepiece.bpe.model` | 5069051 B (4.8 MiB) | `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` | MIT | `Xenova/multilingual-e5-small @ 3acb1fa45c83e69002b1641b37f3cccc132cdd63` |
<!-- END MODEL HASHES -->

`onnx/model_int8.onnx` — это именно int8-файл. Не путать с
`onnx/model_quantized.onnx`: у них разные SHA-256.

## Загрузка

Загрузка только явная, без автозагрузки в рантайме:

```powershell
# Windows / PowerShell
.\scripts\fetch-model.ps1 -Model e5-small
```

```bash
# Linux / macOS
./scripts/fetch-model.sh -Model e5-small
```

По умолчанию файлы кладутся в `models/e5-small-int8/` с сохранением путей из
репозитория, например `models/e5-small-int8/onnx/model_int8.onnx`.

Полезные режимы:

```powershell
# Только проверить уже скачанные файлы, без сети
.\scripts\fetch-model.ps1 -Verify

# Первая контролируемая фиксация хешей токенизатора:
# скачать, посчитать SHA-256 и записать в manifest.json и README
.\scripts\fetch-model.ps1 -RecordHashes

# Перекачать повреждённый файл
.\scripts\fetch-model.ps1 -Force
```

```bash
./scripts/fetch-model.sh -Model e5-small -Precision int8 -Destination models/e5-small-int8
./scripts/fetch-model.sh -Verify
./scripts/fetch-model.sh -RecordHashes
./scripts/fetch-model.sh -Force
```

Скрипт скачивает во временный `.part`, сверяет размер и SHA-256, и только
потом атомарно переименовывает файл. При несовпадении хеша `.part` удаляется,
команда завершается ошибкой.

`-RecordHashes` — явный режим доверия первому скачиванию: он заполняет только
ещё не зафиксированные хеши и обновляет таблицу. После этого `-Verify`
работает строго: без известного хеша, при отсутствии файла или любом
несовпадении команда падает.

## Токенизатор E5

E5 использует XLM-R SentencePiece, а не BERT WordPiece/`vocab.txt`. В набор
входят:

- `tokenizer.json` — основной Hugging Face-токенизатор;
- `tokenizer_config.json`, `special_tokens_map.json`, `config.json`;
- `sentencepiece.bpe.model` — модель SentencePiece, нужная
  `Microsoft.ML.Tokenizers.SentencePieceTokenizer`.

Префиксы `query: ` и `passage: ` обязательны для retrieval. В коде они уже
применяются через `IRoleAwareEmbedder` / `EmbeddingRole`; ONNX-эмбеддер не
должен добавлять их повторно или полагаться на вызывающий код.

## Границы

- `Microsoft.ML.OnnxRuntime` и `Microsoft.ML.Tokenizers` сюда не добавлены.
- Отдельный проект `EntityAnalyzer.Embedders.Onnx` появится на следующем шаге.
- Если модель не найдена, ONNX-эмбеддер должен явно сообщить:
  «запустите `scripts/fetch-model.ps1 -Model e5-small`», а не скачивать её в
  фоне.
- `models/` исключён из git и не попадает в publish.
