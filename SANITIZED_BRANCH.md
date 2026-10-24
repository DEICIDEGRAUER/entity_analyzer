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
Validation: `dotnet build` (0 warnings, 0 errors), 877/877 tests in an
isolated profile with the pinned E5 profile (862 Core + 15 ONNX), 12 passed
and 3 model-gated skips without weights, and
`fetch-model.ps1`/`fetch-model.sh -Verify` against the pinned six-file E5 set.
The synthetic pipeline anchor was also verified twice: 11 mentions in 11 files,
canonical `entities_table.csv` SHA-256
`A81A1086EA7D6028EC6F20C3E34C20F8434D00B8E5CB223BAB36ADE207E8C68A`.
The fixture explicitly covers `ё`/`е`, case, and Latin diacritics.
Source: root `release/v18.1`.
