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
- local launchers that only work with the prebuilt publish folder.

The .NET application source is in `dotnet/`. The app-only public release branch
is `main`, which uses the app repository layout (contents of `dotnet/` at the
repository root).
Validation: `dotnet build` (0 warnings, 0 errors) and 843/843 Core tests in an isolated profile.
Source: root `release/v18.1` at `534a39e`.
