# Phase C retrieval results

Дата: 2026-10-25. Labels зафиксированы **до** первого retrieval-score; хеши
ниже. Phase C не меняет `entities_table.csv`; ONNX остаётся opt-in.

## Входы

- `entities_table.csv` SHA-256:
  `1561C5949C32B3F49C32B49D66A4BCECCAE61118AB604C4EC4926F0E55337FB5`
  (совпадает с preflight; extraction не перезапускался).
- `labels-primary.jsonl` SHA-256:
  `E81CBC59C14BE84A6F65A2DB9B231E358FD6F829A474CA76C2A75BFD5ADE1280`
  (11 positive + 11 hard-negative anchors; 11 query contexts).
- `labels-track-a.jsonl` SHA-256:
  `F820DE0915EE563F5E1801839E664C05E8470BC061FA5DB95DCFD6CD3D0A3E85`
  (3 positive + 5 negative supplemental pairs).
- Manifest SHA-256 (file bytes):
  `77D348AB5CFB13E5031EB1DBAD809DA0B7067C778E2F12BAA44725CD76BEDF6B`.
- Candidate pools: primary — 275 Latin-script mention rows; graph — 880
  unique `(entity, context)` rows.
- Model: `models/e5-small-int8` (`model_int8.onnx` +
  `sentencepiece.bpe.model`); model and tokenizer hashes are in the generated
  JSON report.

## Primary cross-lingual gate

Representation: extracted `variant` (primary; see `PHASE_C.md` §5.0), not the
full paragraph.

| metric | BoW | ONNX | ONNX - BoW |
|---|---:|---:|---:|
| Precision@5 | 0.091 | **0.655** | +0.564 |
| nDCG@10 | 0.084 | **0.747** | +0.662 |

Both absolute zones pass and the relative gate passes
(`>= +0.10` P@5, `>= +0.05` nDCG@10). P@5 is at the lower edge of the `>0.65`
win zone (exact 0.6545; n=11; one-query sensitivity is about 0.018–0.036), so
this is a **borderline** win, not a broad generalization claim.

Hard-negative check (corrected 2026-10-25 after Track C): ONNX puts no
hard-negative anchor in top-5; its closest is `pcb-05n` at rank 8. BoW does put
`pcb-07n` at rank 5, so the earlier blanket statement that all hard negatives
were outside top-5 is **false for BoW**. This does not change P@5/nDCG: hard
negatives are non-relevant anchors. It is a BoW top-5 false positive, not a
semantic match claim.

## Supplemental entity graph

| metric | BoW | ONNX | ONNX - BoW |
|---|---:|---:|---:|
| Precision@5 | 0.067 | 0.133 | +0.067 |
| nDCG@10 | 0.210 | 0.477 | +0.267 |

This is a 3-positive-query supplemental set. ONNX is not worse than BoW by more
than 0.05, but absolute P@5 is low; it cannot support a general entity-graph
quality claim. The 8-pair set remains exploratory.

## Top-10 stability

- 100 query contexts, max 4 per entity (not 100 distinct entities).
- 49 Cyrillic / 51 Latin; Track A candidate pool; entity-level comparison by
  `neighbor_entity_id`.
- Metric: `top10_overlap_contexts_100`.
- Average top-10 overlap: **37.4%**; identical top-10: 0/100.
- Zone: **30–70% — ONNX complements BoW**; cross-lingual matching is the main
  argument, not neighborhood replacement.

## Track C: что именно добавляет ONNX (post-hoc)

Полный разбор: [`PHASE_C_TRACK_C.md`](PHASE_C_TRACK_C.md). Это
интерпретация, не новый gate и не новые labels.

- 8 Track A queries / 68 union rows: `both` 12, `ONNX-only` 28, `BoW-only` 28.
- ONNX-only по поверхности: 12 cross-script и 16 intra-script. Ручная
  классификация: **2 strong, 1 semantic, 19 topical, 6 artifact**. Все
  strong/semantic — intra-script; cross-script ONNX-only — 10 topical и 2
  artifact. То есть «cross-lingual» на уровне языка не равно смысловой связи.
- BoW-only: 20/28 строк имеют score 0; из 16 cross-script строк 13 имеют
  score 0. Три ненулевые cross-script пары — хеш-коллизии BoW, не перевод.
- Stability: 281/374 общих top-10 слотов — BoW score 0; ещё 476/626
  BoW-only слотов — score 0. Поэтому 37.4% overlap не является чистым
  смысловым согласием: на ненулевых BoW-слотах общих пар 93/1000 (9.3%).
- Primary-путь остаётся cross-lingual: 36/55 правильных попаданий, все — при
  русском запросе и Latin candidate pool. Track A positive: 2/15, оба
  попадания на выбранной поверхности intra-script (один alias-identity, один
  лексический `Yogash`/`Ghoul`).

## Full-context diagnostic (not the gate)

Full mention paragraphs, same labels/candidates:

| metric | BoW | ONNX | ONNX - BoW |
|---|---:|---:|---:|
| Precision@5 | 0.055 | 0.182 | +0.127 |
| nDCG@10 | 0.075 | 0.256 | +0.181 |

This fails the absolute P@5/nDCG zones and confirms the documented E5
long-paragraph boundary. It is **not** used for the decision; the treatment is
extracting the entity name for retrieval, not switching model size. The
full-context run was diagnostic only; the primary name rule follows the earlier
E5 boundary in `EMBEDDINGS.md`, and labels/thresholds were not changed.

## Decision

**Matrix verdict: GO (full)** by the pre-registered `PHASE_C.md` §5.5 matrix:
Track B absolute + relative gate pass; Track A is not worse than BoW by more
than 0.05; stability is in the complement zone.

`GO (full)` here is a matrix result, **not** “everything works”. The fixed
scope decision is:

| Scope | Status | Meaning |
|---|---|---|
| Cross-lingual extracted-name retrieval | **GO (opt-in candidate)** | `VectorIndex` + `OnnxE5Embedder` on extracted names; production candidate after labels grow to n≥30–40. |
| Entity graph (Track A) | **NO-GO / closed on current catalog** | n=3 positives, P@5=0.133; Track C: only 2 strong (both alias-confounded) + 1 semantic analogy among 28 ONNX-only. Do not expand to 15–20 on this catalog; reopen after alias cleanup and new frozen labels. |
| Full-context retrieval | **NO-GO** | Confirmed E5-small long-paragraph boundary; do not solve by increasing model size. |
| `semantic_neighbors.csv` | **EXPLORATORY** | Not an anchor, not production; recompute on every corpus/model change. |

n=11 is a wide sampling interval, not a precise P@5: a 20k-resample bootstrap
over the 11 query values gives roughly **95% CI [0.45, 0.84]** (the user-side
binomial-style estimate [0.52, 0.79] points the same way). The +0.564 delta is
large and likely robust, but the exact P@5 is not; expand to 30–40 queries.
Track A n=3 is not a result, only a signal. Track C resolves the limbo: the
current entity graph is closed, not expanded. Its two strong signals depend on
duplicated aliases; the rest are topical, analogy, or unlabeled candidates.

Operational caveat: full graph output is exploratory; rollout should be limited
to the cross-lingual extracted-name path. The cross-lingual name path is the
part with evidence.

## Artifacts

- `test-fixtures/phase-c-corpus-1000/phase-c-manifest.json` — run manifest:
  corpus-slice hash, full-corpus-vs-slice hash distinction, catalog, preset,
  labels, model, metrics, scope decision, and repeat-run determinism.
  File SHA-256: `E76DDB9F20E74D6BB1AD8B7CB879CD94C96321A733E6F6A2354278ED62703CDF`.
- `output/phase-c-1000/semantic_neighbors.csv` — exploratory entity-graph CSV
  (union of BoW/ONNX top-5 entities; both scores on the same rows; 68 rows;
  SHA-256 `96C3F1BBFD9A1EB2C1898E13059A2BC83DCDFDC1E862A306D8E8BCE6A3AC2D59`).
- `output/phase-c-1000/README.md` — explicit exploratory/provenance note next
  to the CSV.
- `docs/PHASE_C_TRACK_C.md` — post-hoc difference analysis, hand classification,
  hard-negative correction, and alias-contamination audit.
- `output/phase-c-1000/phase-c-report.name.{md,json}` — primary report.
- `output/phase-c-1000/phase-c-report.context.{md,json}` — diagnostic report.
- `.gitignore` covers `output/`, `output/phase-c-1000/`,
  `output/**/semantic_neighbors.csv`, and `output/**/phase-c-report.*`; source
  text stays out of the repository.

Repeat check: the evaluator was run twice on the same slice and labels; the two
`semantic_neighbors.csv` files were byte-identical
(`96C3F1BBFD9A1EB2C1898E13059A2BC83DCDFDC1E862A306D8E8BCE6A3AC2D59`).

## Reproduction

```powershell
dotnet run --project tools/PhaseC -- `
  --corpus <pinned>/entities_table.csv `
  --catalog "$env:APPDATA\EntityAnalyzer\codex.json" `
  --model models/e5-small-int8 `
  --manifest test-fixtures/phase-c-corpus-1000/manifest.json `
  --out output/phase-c-1000 `
  --representation name `
  --track-c-out output/phase-c-1000/track-c
```

The Track C sidecar is local/exploratory; `--track-c-out` is optional and does
not alter `semantic_neighbors.csv`. Frozen inputs are checked without model
weights by `scripts/verify-phase-c-artifacts.ps1` (also a CI step).

No labels or thresholds were changed after scores were seen. The context
diagnostic was run separately; both labels hashes are unchanged.
