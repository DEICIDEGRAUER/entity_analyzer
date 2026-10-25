# Phase C retrieval results

Дата: 2026-10-25. Labels зафиксированы **до** первого retrieval-score; хеши
ниже. Phase C не меняет `entities_table.csv`; ONNX остаётся opt-in.

**Operational status: CLOSED — Phase C delivered the infrastructure; the current
corpus does not justify the feature.** This is not “ONNX on hold”: for this
corpus the result is final until corpus diversity increases. The historical
matrix verdict `GO (full)` remains below, but it is not an active ONNX approval.

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
this is a **borderline** win, not a broad generalization claim. It is a win over
**raw** BoW only: the alias-normalized BoW baseline below reaches 0.745 and
removes the ONNX advantage on this benchmark.

Hard-negative check (corrected 2026-10-25 after Track C): ONNX puts no
hard-negative anchor in top-5; its closest is `pcb-05n` at rank 8. BoW does put
`pcb-07n` at rank 5, so the earlier blanket statement that all hard negatives
were outside top-5 is **false for BoW**. This does not change P@5/nDCG: hard
negatives are non-relevant anchors. It is a BoW top-5 false positive, not a
semantic match claim.

### Alias-normalized BoW baseline (post-hoc, 2026-10-25)

Exact known-catalog-alias normalization was applied to both query and candidate
text; ambiguous aliases were left unchanged and `query_card_id` was not used.
This is the cheap alternative that must be beaten before spending a day on
30–40 new labels.

| method | P@5 | nDCG@10 | correct/55 |
|---|---:|---:|---:|
| raw BoW | 0.091 | 0.084 | 5 |
| **alias-normalized BoW** | **0.745** | **0.897** | **41** |
| ONNX E5-small | 0.655 | 0.747 | 36 |
| candidate alias-expanded BoW (diagnostic) | 0.564 | 0.690 | 31 |

- 10/11 query aliases resolve uniquely; only `Йог-Сотота` is unknown. No query
  alias is ambiguous.
- 263/275 candidate aliases resolve uniquely to their own card; 9 are ambiguous
  and 3 unknown.
- Alias-normalized BoW moves `pcb-07n` from rank 5 to rank 7 and adds no top-5
  hard negative.
- The candidate-expanded variant is diagnostic only: it uses candidate
  `card_id` metadata and is not a fair text-only baseline.

`0.745`, not `1.0`, is expected for a table lookup because 10/11 query aliases
resolve uniquely, `Йог-Сотота` is unknown, and candidate collisions remain. It is
still exact alias canonicalization, not retrieval by meaning. ONNX cannot beat a
translation table on “find the card for a known alias”; it could help only where
the alias is unknown and the meaning is guessed. The primary set did not test
that.

The candidate alias-expanded variant (`0.564`) is below both exact normalization
(`0.745`) and ONNX (`0.655`). It makes each candidate a bag of all its names,
loses discriminability, and lets an English query alias match any card with
English aliases rather than its owner. Expansion adds more noise than
cross-lingual help; this is evidence for exact canonicalization, not similarity.

Consequence: the headline `+0.564` is a gain over **raw** BoW, not over the cheap
alternative. The benchmark measured **alias coverage, not semantic retrieval**;
ONNX's semantic capability remains untested on this corpus. This is not evidence
that ONNX “lost” in a fair semantic comparison. The n=30–40 confirmation on the
same surface is cancelled.

Paraphrase feasibility was checked before writing new labels: 24/30 drafts were
natural, but 8 of those had more than one reasonable target; only 16 were
unambiguous and 10 were clean non-alias. The failure is labelability, not
naturalness. This is an author-side probe, not blind annotation; the counts are
an upper bound. A clean 30–40 set is not supported by this corpus
(`PHASE_C.md` §5.8). Therefore no labels were created, no retrieval was run, and
alias cleanup was not done for a benchmark. Phase C remains closed; alias
cleanup moves to the entity-graph backlog.

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
- This is **not** evidence that the two methods complement each other. 281/374
  shared slots have BoW score 0, and only 93/1000 slots (9.3%) are shared with a
  nonzero BoW score. The models are **orthogonal on this candidate pool**, and
  BoW is uninformative on cross-lingual queries. A hybrid is not justified by
  this overlap; it would mostly add BoW zero-score tie noise.

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
- Primary BoW hard-negative false positive (`pcb-07n` at rank 5) disappears
  after alias normalization (rank 7); ONNX also has no primary top-5 hard
  negative. But this is not a general negative-avoidance advantage: on Track A
  intra-lingual negative pairs ONNX puts `pca-08n` (Yog-Sothoth → Ubbo-Sathla)
  at rank 3, `pca-06n` at rank 3, and `pca-05n` at rank 4, while BoW misses
  `pca-08n` and `pca-04n`/`pca-07n`. The intra-lingual evidence therefore does
  not support a separate ONNX hard-negative skill.
- Exact-alias-normalized BoW P@5=0.745 / nDCG@10=0.897 beats ONNX
  0.655 / 0.747. This changes the primary decision from "GO for confirmation" to
  **no n=30–40 confirmation on the current alias-based surface**.

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

**Historical matrix verdict: GO (full)** by the pre-registered `PHASE_C.md`
§5.5 matrix: Track B absolute + relative gate pass; Track A is not worse than
BoW by more than 0.05; stability is in the registered 30–70% zone.

`GO (full)` is a historical matrix result, **not** “everything works”, and it is
not rewritten retroactively. It is also not an active ONNX approval. The
post-hoc alias baseline, Track C, and the paraphrase feasibility probe change
the operational scope:

| Scope | Status | Meaning |
|---|---|---|
| Phase C semantic retrieval (ONNX) | **CLOSED — delivered, not currently needed** | Infrastructure is delivered and opt-in; the current corpus does not justify the feature. ONNX is not default and not production. ONNX semantic capability remains untested on this corpus. |
| ONNX confirmation on current benchmark | **NO-GO** | Alias-normalized BoW P@5=0.745 / nDCG@10=0.897 beats ONNX 0.655 / 0.747. Do not spend n=30–40 on the same alias-based surface; this would confirm alias coverage, not semantic retrieval. |
| Exact alias lookup (catalog-aware) | **fallback, not a Phase C feature** | Known aliases can be resolved by exact normalization; it is cheaper than ONNX. It is table lookup, not retrieval and not semantic entity resolution. |
| Entity graph (Track A) | **NO-GO / closed on current catalog** | n=3 positives, P@5=0.133; Track C: only 2 strong (both alias-confounded) + 1 semantic analogy among 28 ONNX-only. Do not expand to 15–20 on this catalog. Alias cleanup is entity-graph backlog, not a Phase C precondition. |
| Full-context retrieval | **NO-GO** | Confirmed E5-small long-paragraph boundary; do not solve by increasing model size. |
| Paraphrase benchmark (30–40 non-alias) | **FAIL — feasibility** | 24/30 natural drafts, 16 unambiguous, 10 distinct clean non-alias. A clean 30–40 set is not supported; no labels were written. |
| `semantic_neighbors.csv` | **EXPLORATORY** | Not an anchor, not production; recompute on every corpus/model change. |

The historical `GO (full)` was a **topical cross-lingual matching** gate, not
semantic entity resolution. The benchmark's queries are largely exact aliases
from the same catalog used to resolve them; that is why the cheap baseline is
strong. The primary benchmark measured alias coverage, not semantic retrieval;
it cannot show whether ONNX has semantic capability on unseen aliases.

Reopen conditions (all): (1) a larger, more diverse corpus supports 20+ clean
non-alias paraphrase queries, checked again by feasibility before labels;
(2) a 30–40 query set is frozen before scoring, with one target card per query
and no exact aliases/`query_card_id` in construction; (3) the pre-registered
Δ-thresholds below are applied against alias-normalized BoW. Alias cleanup is
not part of reopening Phase C; it stays in the entity-graph backlog.

Future paraphrase run — pre-registered decision rule (recorded before any such
run; none has been executed):

| Δ P@5 (ONNX − alias-normalized BoW) | Decision |
|---|---|
| **> +0.20** | ONNX justified; return to confirmation |
| **+0.10 … +0.20** | Marginal; complexity, likely not justified |
| **< +0.10** | Not justified; keep Phase C closed for this corpus |

n=11 is a wide sampling interval, not a precise P@5: a 20k-resample bootstrap
over the 11 query values gives roughly **95% CI [0.45, 0.84]** (the user-side
binomial-style estimate [0.52, 0.79] points the same way). The raw-BoW delta is
large, but the exact ONNX P@5 is not; more importantly, the cheap alias baseline
already exceeds ONNX, so enlarging this set would not answer the remaining
question. Track A n=3 is not a result, only a signal. Track C resolves the
limbo: the current entity graph is closed, not expanded. Its two strong signals
depend on duplicated aliases; the rest are topical, analogy, or unlabeled
candidates.

Operational caveat: for known aliases, use exact alias lookup; ONNX remains an
opt-in research path for a residual (paraphrase/unseen alias) scenario. No
residual advantage has been demonstrated, so do not make ONNX default or ship
it as semantic search. The current corpus does not support the experiment that
could change this; re-evaluate when corpus diversity increases.

Methodological lesson: **pre-registration protected against motivated reasoning.
It did not protect against an unmodeled cheap baseline.** The gate compared ONNX
with raw BoW, but raw BoW is uninformative on cross-lingual name variants;
exact alias normalization was added only post-hoc. Future pre-registrations must
include at least one cheap non-ML baseline (exact match, alias normalization,
BM25) before declaring a gate.

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
  alias-normalized BoW baseline, hard-negative correction, and
  alias-contamination audit.
- `output/phase-c-1000/alias-baseline.json` — optional local sidecar from
  `--alias-baseline-out`; not committed and not part of the CSV contract.
- `output/phase-c-1000/paraphrase-feasibility.md` — local, ignored feasibility
  scratch: 10 cards × 3 drafts, 30 rows, no labels and no scoring; not a frozen
  artifact and not a benchmark.
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
  --track-c-out output/phase-c-1000/track-c `
  --alias-baseline-out output/phase-c-1000
```

The Track C and alias-baseline sidecars are local/exploratory; both options are
optional and do not alter `semantic_neighbors.csv`. The paraphrase feasibility
scratch is also local and ignored; it is not consumed by the evaluator. Frozen
inputs are checked without model weights by
`scripts/verify-phase-c-artifacts.ps1` (also a CI step), which pins the scope
markers in `PHASE_C.md` §5.6 and the feasibility/lesson markers in §5.8–§5.9.

No frozen labels were changed after scores were seen. The future paraphrase
thresholds were recorded before any paraphrase run; no such run was executed.
The context diagnostic was run separately; both labels hashes are unchanged.
