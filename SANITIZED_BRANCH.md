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
Validation: `dotnet build` (0 warnings, 0 errors), 881/881 tests in an
isolated profile with the pinned E5 profile (862 Core + 19 ONNX); without
weights 876 pass and 5 model-gated tests skip, and
`fetch-model.ps1`/`fetch-model.sh -Verify` against the pinned six-file E5 set.
The synthetic pipeline anchor was also verified twice: 11 mentions in 11 files,
canonical `entities_table.csv` SHA-256
`A81A1086EA7D6028EC6F20C3E34C20F8434D00B8E5CB223BAB36ADE207E8C68A`.
The fixture explicitly covers `ё`/`е`, case, and Latin diacritics.
The ONNX golden layer is also included: 12 int8 vectors generated from the same
`model_int8.onnx` (C# cosine > 0.9999), plus the fp32 sentence-transformers
reference with measured int8 minimum `0.979531` and documented floor `0.975`;
golden tests run in the separate `onnx-golden-tests` CI job.
Source: root `release/v18.1`.
