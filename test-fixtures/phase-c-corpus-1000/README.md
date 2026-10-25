# Phase C pinned 1000-file slice

This directory contains **metadata only**. The corpus texts are not committed,
copied, or published. `manifest.json` pins 1 000 paths relative to the private
corpus root with per-file size, SHA-256, and language bucket.

## What is pinned

- `manifest.json` — the slice; 1 000 entries: 35 `ru`, 227 `mixed`, 738 `en`.
- `cross-lingual-pairs.json` — 11 verified same-referent ru↔en pairs and 4
  near-misses from the preflight existence check. No source text is stored,
  only entity IDs, forms, file paths, and a one-line rationale.

Selection is deterministic: all files classified `ru` or `mixed` by the fixed
first-64-KiB classifier, plus 738 `en` files ordered by ascending
`SHA-256(relative_path)`. The classifier and its limitation are documented in
[`docs/PHASE_C.md`](../../docs/PHASE_C.md). The old first-1000 performance
slice was language-biased (`968 en / 3 ru / 8 mixed`) and yielded only 4 of the
11 exact pairs; it is not the Phase C input.

## Hashes

- Canonical entries hash (the value in `manifest.json.selection.manifest_sha256`
  and in `cross-lingual-pairs.json`):
  `EA2AC56494EDEB8193D9E1C464F19F5934B8CC226363F510B4EB13A148DA4E95`
- File-byte SHA-256 of `manifest.json`:
  `77D348AB5CFB13E5031EB1DBAD809DA0B7067C778E2F12BAA44725CD76BEDF6B`
- File-byte SHA-256 of `cross-lingual-pairs.json`:
  `865D98328E071B760522E8F1B39264B484DFF953C9F9E883B27168E73380C289`

The canonical entries hash is computed over the sorted entries, each as
`path\0sha256\0size\0language\n`, UTF-8. Changing any path, file hash, size, or
language bucket invalidates it.

## Materializing locally

Resolve every `entries[].path` against the private corpus root, verify
`size` and `sha256`, and place the files in a temporary directory. A temporary
hardlink tree is sufficient; do not commit it. `corpus/` is ignored here for
that reason.

One source duplicate exists: two paths share the same SHA-256
(`64E6781D...`). Both paths are retained because they are distinct corpus
entries; `(entity, context)` deduplication in extraction is unaffected.

## Scope

This slice is for Phase C preflight and retrieval evaluation, not a new corpus
anchor. It does not change `entities_table.csv`, smoke/full anchors,
`bench/baseline.json`, or the ×1.5 threshold.
