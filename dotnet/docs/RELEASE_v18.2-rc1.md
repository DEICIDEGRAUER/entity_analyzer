# Entity Analyzer v18.2 RC — release notes

Tags `v18.2-rc1` (commit `f1a3eb0`) and `v18.2-rc2` (the commit holding this
file — docs-only delta vs rc1). CI 6/6 green (windows-latest / ubuntu-latest ×
ru-RU / en-US / tr-TR) on the RC commits · 2026-11-09.

**v18.2-rc2 delta vs rc1:** this document, the root-README reproducibility
section and .NET banner, and the v18.3 backlog in RELEASE_HORIZON.md. No code
changes vs rc1 — the full rc1..rc2 tree diff touches only `.md` files
(verified before tagging).

Full change list: [CHANGELOG.md](../../CHANGELOG.md) («Unreleased»). Activation
record: [OUTPUT_GENERATOR_POLICY.md](OUTPUT_GENERATOR_POLICY.md) §17. Anchor
bookkeeping: [corpus-manifest.json](corpus-manifest.json).

## What's new

1. **Important — reproducibility.** Byte-identical reproduction of corpus
   anchors requires `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. Without it,
   the same binary produces different tie-breaking on 27 rows; counts are
   identical. Details in «Reproducibility» below.
2. **Generator v2 is the default.** Composite canonical names are now matched
   as whole phrases including declined forms (`Нага и Йеба` / `Нагу и Йебу`)
   instead of per-component standalone rows. Effect on the default one-card
   corpus: **915 → 852 mentions (265 → 243 files)**; on the
   37-file diagnostic corpus: **33 430 → 28 314 rows**. The removed mass is
   dominated by junk standalone rows (Russian «наг», Belarusian «на нагах»,
   Indian nāgas, Naga the toponym), while 54 genuine whole-phrase rows were
   added. A row-by-row audit of the full delta is in
   [OUTPUT_GENERATOR_POLICY.md](OUTPUT_GENERATOR_POLICY.md) §17.8.
3. **Rollback for one cycle.** `--generator legacy | full-phrase | full-phrase-plus`
   selects the generator explicitly; `full-phrase-plus` is the default and
   `legacy` keeps the previous engine reachable through v18.2.
4. **Exploratory trust view.** Two new explicit filter modes on the results
   page — «Trusted (exploratory)» and «Canonical only (exploratory)». High
   precision by construction (14/14 trusted-good in the pre-registered probe);
   the raw/review default view does not change.
5. **File-level source-quality gate + `entities_trust.csv` sidecar.** Every
   export writes a 12-column sidecar next to the main CSV (`file,num,…,exploratory`),
   joined by `file,num`; it never removes or rewrites main-CSV rows. A trust
   row enters `trusted` only when the file-level source quality is `usable`;
   `unknown`, `degraded_lexical`, `degraded_ocr` and duplicate-content
   worst-source inheritance stay in `review`. Headless runs supply overrides
   via `--source-quality-sidecar <file.csv>` or by dropping
   `entities_source_quality.csv` next to the reports.
6. **Review report export.** When the session has at least one verdict,
   `review_report.md` (human-readable summary and decisions table) and
   `review_report.csv` (machine-readable; `confirmed/rejected/uncertain`,
   RFC 4180) are written alongside the regular reports; without verdicts the
   regular report set stays byte-identical. Headless runs opt in with
   `--review-report`. The results page can jump to the next file with
   unreviewed mentions and shows corpus-wide progress.
7. **Version markers 18.2.0.** `About.Version`, the app csproj `<Version>` and
   the app manifest now read `18.2.0`; the runtime version constant had been
   `1.0.0` for the entire 18.x line. The value is surfaced in the GUI About,
   the headless API `version` field, and is written into run/repro manifests
   (`.earepro` and equivalents) — if a script parses those manifests, expect
   the new string. This is the only string-level compatibility change in the
   release.

## Known limitations

- Declined and caps canonical-pair forms (`Нугу и Йебу`, caps `НУГА и ЙЕБА`)
  are not matched in v18.2: phrase morphology declines only the Cyrillic
  composite, while the English canon transliteration carries no Cyrillic
  morphology. This is ~9 rows (~1%) of the anchor corpus. Tracked for v18.3
  via Cyrillic-aware decline of the transliterated canon (release horizon).
- `--generator legacy` is a one-cycle rollback path and will be removed in
  v18.3 (recorded in §17.7).

## Reproducibility

- **Set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`** for any run whose output
  you compare byte-for-byte (corpus anchors, smoke baselines). Without the
  pin, the same binary on the same corpus yields byte-different CSV: 27 of
  915 rows differ (context normalization `…` vs `...` and one adjacent-row
  permutation of equal contexts). Mention counters are identical in both
  environments (915 / 265).
- **The built-in release self-check is unaffected.** It compares counts only
  (files, files-with-mentions, mentions, per-entity counts) — no byte hashes —
  so it passes with or without the pin. The CI matrix (ru-RU / en-US /
  tr-TR) runs without the pin as a standing check of this.
- **Correction on legacy-mode output.** The previously recorded claim that
  the prior corpus anchor hash `690644D6…` reproduces byte-identically via
  `--generator legacy` was byte-imprecise: legacy mode preserves **counts**
  (915 / 265, verified in three runs), not the historic bytes. The hash of
  the current source's legacy output is `BEB7C0E5…` (`legacy_mode_recheck`
  in [corpus-manifest.json](corpus-manifest.json)); historic hashes remain
  epoch-specific.
- Current default-mode anchor hashes live in
  [corpus-manifest.json](corpus-manifest.json) and
  [OUTPUT_GENERATOR_POLICY.md](OUTPUT_GENERATOR_POLICY.md) §17; the
  full 915 → 852 row-by-row audit is §17.8, and the unpinned-env hash
  (`06A3DE3B…`) is recorded in the manifest as `env_sensitivity`.

## What did not change

- The 8-column `entities_table.csv` contract and the regular report set.
- The default raw/review view; the trusted view is strictly opt-in.
- Private catalogs; frozen engine diagnostics (Phase C / ONNX artifacts and
  the trace corpus) stay frozen and guarded; the smoke anchor is a verified
  no-op under the release recipe.

## Upgrade notes

- On the first GUI launch of v18.2 the built-in release self-check (R2-16)
  runs once — by design, because the version marker moved from `1.0.0`;
  scripts or users upgrading will see this one-time check and a
  `selfcheck_baseline`-backed verdict. It is count-based, so the
  globalization environment does not change its outcome.
- Headless flags introduced or relevant here: `--generator <mode>`,
  `--source-quality-sidecar <file.csv>`, `--review-report`.
- If any automation compares the engine version string, update expectations
  from `1.0.0` to `18.2.0`.
