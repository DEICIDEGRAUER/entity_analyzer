# Phase C labels

These labels were written **before** any BoW or ONNX retrieval score was
computed. The relevance codebook and thresholds are in
[`docs/PHASE_C.md`](../../docs/PHASE_C.md), commit `b5ec14c`.

## Primary cross-lingual set (`labels-primary.jsonl`)

- 11 Russian query mentions, one per verified card from
  `cross-lingual-pairs.json`.
- 11 positive anchors (`label: 1`): the English mention of the **same**
  `card_id`; the pair is an identity label, not a similarity judgement.
- 11 hard-negative anchors (`label: 0`): a different `card_id` that co-occurs
  in the pinned slice or belongs to the same mythos, without an asserted
  relation.
- Candidate pool: 275 unique Latin-script mention rows from the pinned minimal
  run. For each query, **all** candidate rows with the same `card_id` as the
  query are relevant; all other rows are `0`. The 11 positive anchors are
  representatives, not the complete positive set.
- The two labels per `query_id` (`p` and `n`) share the same Russian query
  context. `labels.meta.json` records the label file SHA-256 and the expansion
  rule.

## Supplemental entity graph (`labels-track-a.jsonl`)

- 8 pairs: 3 positive + 5 negative, distinct `card_id`s among the 30 entities
  with contexts in the pinned slice.
- Positive: `Баст → Гуль` (explicit source relation),
  `Абхот ↔ Уббо-Сатла` (source-specific same referent),
  `Йогаш-Гуль → Гуль` (type/instance). Negative: similar names, co-occurrence
  only, or topic-only pairs.
- Candidate pool: the same 30 entities, excluding the query. Pairs not listed
  are `0` for this exploratory track. This set does not gate the cross-lingual
  decision; it can only limit a full rollout (see PHASE_C §5.5).

## Storage

Full source contexts are **not** stored in Git. Each label row identifies a
mention by `card_id`, basename, variant, and `context_sha256`; join it to the
pinned `entities_table.csv` (or the private corpus) to recover text. Text and
hashes are intentionally separate so the label file stays copyright-safe.
