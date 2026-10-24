# Synthetic smoke corpus — pipeline anchor

This directory is a **committed, byte-pinned pipeline anchor**, independent of the
real corpus. It answers one question: did the published CLI change its observable
`entities_table.csv` output for a small, documented set of edge cases?

The real-corpus snapshots are documented separately in
[`../../docs/CORPUS.md`](../../docs/CORPUS.md) and
[`../../docs/corpus-manifest.json`](../../docs/corpus-manifest.json). They are a
**corpus anchor**; this directory is a **pipeline anchor**. Keep them separate.

## Layout

```text
test-fixtures/smoke-corpus/
├── README.md              # this file (outside the analyzed corpus)
├── manifest.json          # pinned hash, Merkle root, counts, history
├── .gitattributes         # LF for every fixture file
└── corpus/                # the actual analyzed input
    ├── 01-russian.txt
    ├── ...
    └── 15-html.html
```

`README.md` and `manifest.json` live outside `corpus/` on purpose: they are
metadata, not input documents. `corpus/` contains only files whose effect on the
pipeline is intentional.

## What each file covers

| File | Purpose |
|---|---|
| `01-russian.txt` | Pure Russian text; Russian aliases of the default entity at sentence boundaries. |
| `02-english.txt` | Pure English text; English aliases (`Nug and Yeb`, `Yeb`). |
| `03-mixed.txt` | Russian and English in one sentence; no language-specific branch should be required. |
| `04-mixed-entity.txt` | The same entity written with Russian and English aliases in one sentence; alias normalization and overlap handling. |
| `05-homonym-person.txt` | Person context for `Наг` (`Жрец Нага`). Context input for `HomonymClassifier`; the classifier itself has dedicated unit tests and is not exposed as a sense column in `entities_table.csv`. |
| `06-homonym-place.txt` | Place context for `Наг` / `Нуг` (`Город Наг`, `Дорога в Нуг`). |
| `07-homonym-abbrev.txt` | All-caps abbreviation context (`НАГ`) plus a dotted form (`Н.А.Г.`); tests that an abbreviation-looking token does not crash or silently swallow adjacent text. |
| `08-boundaries.txt` | Quotation marks, em dash, apostrophe (`Yeb's`), parentheses, brackets, and an entity at the end of a line. |
| `09-unicode.txt` | Uppercase/lowercase, `ё`/`е`, Latin diacritics (`Yēb`, `café`); only the exact alias `Yeb` should match. |
| `10-markdown.md` | `.md` extension and Markdown markup (`#`, `**`). |
| `11-empty.txt` | Zero-byte file; exercises the empty-file skip path. |
| `12-punctuation.txt` | Non-empty punctuation-only file; must not produce mentions or crash the tokenizer. |
| `13-duplicate-a.txt` | First half of an exact-content duplicate pair. |
| `14-duplicate-b.txt` | Second half of the same exact-content pair; one path must be skipped by dedup. |
| `15-html.html` | `.html` extension and HTML tags around an entity. |

The duplicate pair is intentionally **mention-free**. This keeps the pinned CSV
hash independent of which duplicate path is chosen as canonical, while the
manifest still asserts that the dedup path found one duplicate group and skipped
one file. The real-corpus manifest separately documents the engine's canonical
selection policy.

## Pinned values

The current snapshot is in `manifest.json` under `snapshots.smoke_current`:

| Metric | Value |
|---|---:|
| Scanned files | 15 |
| Supported files | 15 |
| Empty files | 1 |
| Exact duplicate groups | 1 |
| Duplicate files skipped | 1 |
| Analyzed files | 13 |
| Mentions | 11 |
| Files with mentions | 11 |
| `entities_table.csv` SHA-256 (canonical LF) | `A81A1086EA7D6028EC6F20C3E34C20F8434D00B8E5CB223BAB36ADE207E8C68A` |

The CSV writer uses `Environment.NewLine`; the anchor normalizes `CRLF`/`CR` to
`LF` before hashing, so the same semantic output has the same hash on Windows and
Linux. This is documented as `hash_normalization` in the manifest. The Merkle
root covers all supported files, including the empty file, using the same
algorithm as the real-corpus manifest.

## Determinism rules

The anchor is only useful if its hash is stable. The fixture follows these rules:

- **ASCII file names only**, no spaces, no case-only collisions.
- **LF line endings** enforced by `.gitattributes`; no timestamps in content.
- **No incremental cache** (`--no-incremental`).
- **Isolated profile** via temporary `ENTITYANALYZER_DATA_DIR` and
  `EA_SEMANTIC_USER_DIR`.
- **Invariant globalization** for the child CLI
  (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`) so case folding and culture do not
  depend on the host locale.
- **Canonical CSV hash**: output line endings are normalized to LF before
  SHA-256, because the CSV writer uses `Environment.NewLine`.
- **Sorted discovery** is already applied by the engine
  (`StringComparer.OrdinalIgnoreCase` over full paths). All fixture names are
  lowercase ASCII, so this order is identical on Windows and Linux.
- `-Check` runs the CLI twice and fails if the two hashes differ before comparing
  the first hash to the pinned value.

## Verify and update

Run from the `dotnet/` directory:

```powershell
# Read-only check used by verify.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/update-smoke-baseline.ps1 `
  -Check -Corpus test-fixtures/smoke-corpus

# Deliberate re-baseline: clean working tree is required, reason is mandatory
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/update-smoke-baseline.ps1 `
  -Corpus test-fixtures/smoke-corpus `
  -Reason "Explain why the expected pipeline output changed"
```

The update command:

1. refuses to run on a dirty Git working tree;
2. runs `publish-cli` on `corpus/`;
3. recomputes counts, Merkle root, and the `entities_table.csv` hash;
4. updates `manifest.json` and appends a history entry with the previous hash,
   the new hash, and the required `-Reason`.

Never edit the pinned hash by hand just to make CI green. A changed hash means
the pipeline output changed; investigate it, then record the reason.

## Scope

This is not a unit-test replacement and not an ONNX golden fixture. It is a
fast end-to-end pipeline anchor. The ONNX model-gated sanity tests and the
separate int8/fp32 golden fixture in `test-fixtures/onnx-golden/` are
documented in [`../../docs/EMBEDDINGS.md`](../../docs/EMBEDDINGS.md).
