# Sanitized root snapshot v18.1

This is an orphan, source-only snapshot of the root `release/v18.1` workspace.
It intentionally has no parent commit, so local history and user artifacts
cannot be reached from this branch.

Excluded from this branch:

- `output_dotnet/`, `reports/`, corpus outputs, verdicts and generated reports;
- `.earepro` packages;
- local `.mcp.json` and `.tessera` state;
- `TestResults/` and `.trx` files (they contain machine/user paths);
- PDB/debug symbols and prebuilt `dotnet/publish*` binaries;
- ONNX model weights and tokenizer files under `dotnet/models/` (only the
  README and hash manifest are tracked);
- local launchers that only work with the prebuilt publish folder.

The .NET application source is in `dotnet/`. The app-only public release branch
is `main`, which uses the app repository layout (contents of `dotnet/` at the
repository root).
Validation: `dotnet build` (0 warnings, 0 errors), 888/888 tests in an
isolated profile with the pinned E5 profile (867 Core + 21 ONNX); without
weights 881 pass and 7 model-gated tests skip, and
`fetch-model.ps1`/`fetch-model.sh -Verify` against the pinned six-file E5 set.
The synthetic pipeline anchor was also verified twice: 11 mentions in 11 files,
canonical `entities_table.csv` SHA-256
`A81A1086EA7D6028EC6F20C3E34C20F8434D00B8E5CB223BAB36ADE207E8C68A`.
Context-window deduplication is scoped to `(entity, context)`, so distinct
entities sharing one sentence are preserved; on a full-corpus diagnostic with a
37-card codex and the `minimal` preset the old key produced 36,012 mentions and
the fixed key produces 37,406 (+1,394 across 1,260 multi-entity contexts). This
diagnostic is not a corpus anchor. The smoke `Наг и Йеб` result is one canonical
pair card, not a conjunction merge.
The fixture explicitly covers `ё`/`е`, case, and Latin diacritics.
The ONNX golden layer is also included: 12 int8 vectors generated from the same
`model_int8.onnx` (C# cosine > 0.9999 on Windows, >= 0.99 on Linux/macOS; one
Ubuntu runner measured `0.996705` with exact token IDs, so the portable floor
is documented as a catastrophe check, not a regression check), plus the fp32
sentence-transformers reference with measured int8 minimum `0.979531` and
documented floor `0.975`; golden tests run in the separate
`onnx-golden-tests` CI job on Ubuntu x64 and macOS 14 ARM64. A separate
`onnx-retrieval-tests` job exercises `VectorIndex` with the real embedder on a
title query and a title-free paraphrase. This is topical retrieval, not
disambiguation: the bare query `Наг` is ambiguous (`0.906874` dog vs `0.895978`
priest), and `HomonymClassifier` remains the disambiguation mechanism.
Source: root `release/v18.1`.
