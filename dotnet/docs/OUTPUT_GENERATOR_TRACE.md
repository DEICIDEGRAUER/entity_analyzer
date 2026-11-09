# Trace of 5 + 29 + 164 generator-policy row events

This is the row-level audit trail requested for the re-running generator
comparison selfcheck. It covers the **198** explicit row events from the
selfcheck:

- **5** pure removals of alias rows,
- **29** pure additions of alias rows,
- **164** pure additions of full-phrase/approved rows.

The frozen artifact is
[`OUTPUT_GENERATOR_TRACE.csv`](OUTPUT_GENERATOR_TRACE.csv), pinned by
`scripts/verify-generator-policy.ps1` (SHA-256
`1B72B940802750B2E0C3861BCEA2366767E721B534BDD2DC6FC2B763FD358816`).
Changing the trace without re-running and re-accepting the corpus comparison
fails the guard. Trace is invariant to a default switch: the 198 events
compare the same two modes, so pinned hash and trace vectors stay frozen.
If a later criterion v2 rerun legitimately changes the trace, regenerate the
CSV and update the pinned hash in the guard in the same commit.

Format: one row per event,
`(kind, entity, previous_variant, current_variant, classification)`.
There is an explicit `context_hash` column because the same normalized
context often repeats across many rows; the pair
`(entity, context_hash)` plus the `line`/`offset` columns is enough to
audit each event without inserting long Russian contexts into the doc.

## Summary

| classification | kind | rows | note |
|---|---|---:|---|
| `nested_span_improvement` | `removed` | 5 | alias mention replaced by a longer/more specific mention in plus |
| `same_context_re_attribution` | `added` | 29 | alias mention appears at same normalized context; standalone variant replaced |
| `approved_addition` | `added` | 164 | new full-phrase/approved mention in plus |

The 5 alias removals are **not** losses under the policy: in plus, the
same context contains a broader, more specific mention. Similarly, the 29
alias additions are not orphan false positives: they are alias mentions
in the same normalized context where legacy had a different mention.
The 164 full-phrase additions are explicitly approved variants and are
classified as new generator-approved mentions, not as re-attributions.

## Artifacts

- `dotnet/docs/OUTPUT_GENERATOR_TRACE.csv` — the plain-text trace rows.
- `dotnet/scripts/verify-generator-policy.ps1` — the frozen trace guard
  called by `verify.ps1`.
- An intermediate machine-readable trace is kept in the local probe
  directory before being reduced to the CSV.

## Constraints

- Activation (2026-11-09) does **not** rewrite this trace: the 198 events
  compare the same two modes in either direction, so the CSV and its pinned
  hash are invariant to which mode is the production default.
- Only a deliberate criterion-v2 rerun updates the trace, together with the
  trace doc and the pinned hash.
- The trace is not a precision metric; it is an audit list.
