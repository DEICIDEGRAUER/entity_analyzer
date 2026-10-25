# Phase C opt-in evaluator

This tool is a one-off evaluation harness, not a production default. It references
`EntityAnalyzer.Core` (BoW) and `EntityAnalyzer.Embedders.Onnx` explicitly; it is
not referenced by Core, App, or Cli.

It reads the private `entities_table.csv` and catalog, the committed labels and
manifest, and the pinned E5 model directory. It writes results outside Git:

- `semantic_neighbors.csv` — exploratory entity-graph output;
- `phase-c-report.md` / `phase-c-report.json` — metrics, hashes, and decision.

The input `entities_table.csv` is never modified. Run it only with the pinned
Phase C corpus; the default expected hash rejects a different extraction.

## Primary gate vs diagnostic

- `--representation name` (default, primary): query/candidate text is the
  extracted mention `variant`. This follows the E5 boundary in
  `docs/EMBEDDINGS.md`: long descriptive paragraphs are a known failure mode;
  extract the entity name instead of escalating the model.
- `--representation context`: full mention context; diagnostic only, not the
  gate. It is used to document the long-paragraph boundary.

## Example

```powershell
dotnet run --project tools/PhaseC -- `
  --corpus <pinned>/entities_table.csv `
  --catalog "$env:APPDATA\EntityAnalyzer\codex.json" `
  --model models/e5-small-int8 `
  --manifest test-fixtures/phase-c-corpus-1000/manifest.json `
  --out output/phase-c-1000 `
  --representation name `
  --stability-queries 100 `
  --max-per-entity 4 `
  --track-c-out output/phase-c-1000/track-c
```

Labels are loaded from `test-fixtures/phase-c-corpus-1000/labels` by default;
pass `--labels` to override. `--track-c-out` is optional; it writes
`track-c-analysis.json` with BoW/ONNX top-5 membership, script relation, and
100-context aggregate differences. The hand classification stays in
`docs/PHASE_C_TRACK_C.md`; the CSV contract is unchanged.

No corpus text, model weights, or ONNX packages are committed.
