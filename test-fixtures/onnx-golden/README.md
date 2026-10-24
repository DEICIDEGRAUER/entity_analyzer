# ONNX golden fixtures

Закоммиченные числовые якоря для E5. Веса модели здесь **не лежат**: оба JSON
содержат только метаданные и векторы, а `generate.py` читает локальный
проверенный профиль `models/e5-small-int8`.

## Два артефакта, два разных вопроса

| Файл | Источник | Что доказывает | Допуск |
|------|----------|----------------|--------|
| `golden_int8.json` | тот же `onnx/model_int8.onnx` + `tokenizer.json`, Python `onnxruntime` | C#-пайплайн выдаёт те же числа, что эталонная реализация того же int8-пайплайна | cosine > 0.9999 к C# |
| `reference_fp32.json` | `intfloat/multilingual-e5-small` в fp32 через `sentence-transformers` | насколько int8 деградирует относительно «чистой» fp32-модели | cosine >= 0.975 между int8- и fp32-векторами |

`golden_int8.json` — это не эталон «правильного смысла», а эталон именно
выбранного production-профиля. Любое расхождение C# с ним означает ошибку в
префиксе, токенизации, обрезке, pooling или L2-нормализации.

`reference_fp32.json` не используется для проверки C#. Он нужен, чтобы
количественно знать цену int8 и иметь базу для будущего перехода на fp16/fp32.
Измеренная деградация на этих 12 случаях: min `0.979531`, mean `0.985313`.
Поэтому порог `0.99` для int8-vs-fp32 был бы неправдой; зафиксирован `0.975`.

## Что именно повторяет Python

`generate.py` не использует `optimum` и не полагается на pooling из
`sentence-transformers` для int8-эталона. Шаги выписаны явно:

1. роль → префикс `query: ` или `passage: `;
2. `tokenizer.json` (XLM-R IDs: BOS=0, PAD=1, EOS=2, UNK=3);
3. если токенов больше 512 — первые 511 и EOS на последней позиции;
4. `input_ids`, `attention_mask` (все единицы) и нулевой `token_type_ids`
   подаются как `int64 [1, sequence]`;
5. `last_hidden_state` → masked mean по всем позициям, **включая BOS и EOS**;
6. L2-нормализация.

Именно включение BOS/EOS в mean pooling — исторически неоднозначное место у
E5/sentence-transformers. Здесь оно зафиксировано явно и проверяется тем, что
C#-вектор совпадает с `golden_int8.json`.

## Состав набора

12 случаев = 6 текстов × 2 роли (`Query`, `Document`). Один и тот же текст
специально используется в обеих ролях, чтобы поймать неправильный префикс, а не
только «векторы различаются».

- `ru-*` — русский, с `ё`/`е` и пунктуацией;
- `en-*` — английский;
- `mixed-*` — русский + английский;
- `long-*` — 966 токенов до обрезки, 512 после, EOS сохранён;
- `punctuation-*` — почти пунктуационная строка, короткий pooling denominator;
- `unicode-*` — `ё`, латинская диакритика `é`/`ē`, длинное тире.

## Регенерация

Нужен Python 3.14.2 (или совместимый), зависимости из
`requirements-golden.txt`, локально скачанный и проверенный профиль:

```powershell
.\scripts\fetch-model.ps1 -Model e5-small
python -m pip install -r test-fixtures/onnx-golden/requirements-golden.txt
python test-fixtures/onnx-golden/generate.py --model-dir models/e5-small-int8
```

Скрипт сверяет SHA-256 всех файлов профиля с `models/manifest.json` и падает
при несовпадении. `reference_fp32.json` требует сеть для загрузки базовой
модели по закреплённой ревизии; веса не коммитятся. Для повторной генерации
только int8-эталона (если fp32-файл уже есть) используйте `--skip-reference`.

## Версии и ревизии

- int8: `Xenova/multilingual-e5-small @ 3acb1fa45c83e69002b1641b37f3cccc132cdd63`
- fp32: `intfloat/multilingual-e5-small @ 614241f622f53c4eeff9890bdc4f31cfecc418b3`
- `onnxruntime==1.31.0`, `tokenizers==0.22.2`,
  `sentence-transformers==3.0.1`, `transformers==4.57.6`, `torch==2.13.0`,
  `numpy==2.5.2`

Версии и ревизии также записаны в самих JSON. Точные SHA-256 модели и
токенизатора берутся из `models/manifest.json`.

## CI

Тесты помечены `[Trait("Category", "OnnxGolden")]` и запускаются отдельным
job `onnx-golden-tests`, а не вместе с `onnx-model-tests`. Обычный
`onnx-model-tests` и test-matrix исключают эту категорию. Без весов
model-gated golden-тесты пропускаются с явной причиной; тест согласованности
двух JSON выполняется всегда.
