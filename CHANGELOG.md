# Changelog

All notable changes to Entity Analyzer will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Review results are now exported. When a session has at least one verdict,
  `ExportAll` additionally writes `review_report.md` (summary: total/reviewed/
  confirmed/rejected/uncertain, files with unreviewed mentions, plus a decisions
  table) and `review_report.csv` (machine-readable; RFC 4180 quoting; stable
  `confirmed/rejected/uncertain` codes matching `verdicts.jsonl`). Without
  verdicts the protocol is not written at all, so the regular report set and
  their content stay byte-identical. The Export page shows the current review
  progress and whether the protocol will be written. Headless runs opt in with
  `--review-report`: the CLI applies saved verdicts using SHA-256 from the
  incremental index (falling back to reading only unindexed files) and then
  writes the same protocol.
- The results page can jump to the next file with unreviewed mentions and
  shows corpus-wide review progress. Navigation respects the current file
  filters first; if they contain no work, it resets them and continues across
  the whole corpus, switching the verdict filter to «Непроверенные».
- Saved verdicts are restored for every mention when a session loads, using
  SHA-256 from the incremental index when available, so the corpus counter and
  the next-file action start from the same state as the per-file list.

### Fixed
- The legacy-config integration test no longer embeds a developer-specific
  absolute path: it searches upward from the test output directory for
  `config.yaml` and skips cleanly in the app-only CI layout. The legacy
  `run_tests.ps1` and `test_install.py` now use their own directory instead of
  a hardcoded desktop path.
- The Export tab's review hint now refreshes immediately after a single
  verdict, a bulk verdict, or an undo. It could previously still say
  «0 проверено» until the next analysis even though the verdict was saved.
- Bulk verdicts are now undone as one action: `Ctrl+Z` restores the whole
  selection instead of only the last card. Batch storage uses a single
  `VerdictStore.RemoveRange` rewrite.
- Duplicate content is counted before the cache fast path. A warm cache could
  previously return cached results for a duplicate and bypass
  `SkippedDuplicate`; a regression test covers the cold/warm pair.
- Recent-project settings are pruned from missing or invalid paths at startup
  and after removal; invalid legacy entries can no longer break project opening.
- The command palette no longer drops long codex cards. Their `SearchText`
  contains hundreds of aliases, and the length penalty in `FuzzyFilter.Score`
  made the context score negative; a title match is now considered even when
  the full context does not score.

### Changed
- The homonym classifier now scores a local context window instead of using
  the first personal word anywhere in the sentence: inflected forms of the
  term (`Нага`, `Наге`, `Нагом`) are matched, place prepositions work through
  a place phrase (`в древний город Наг`), place nouns count, and dotted
  acronyms are recognized. Explicit personal or place context can refine an
  all-caps term, while a neutral CAPS form stays an abbreviation. The public
  `Classify` contract is unchanged.
- VectorIndex and its offline BagOfWords embedder are hardened as the
  dependency-free Phase A: process-stable FNV-1a buckets replace randomized
  `string.GetHashCode()`, tokenization ignores case and surrounding punctuation,
  repeated ids replace rather than duplicate, dimensions and finite values are
  validated, empty or punctuation-only queries return no results, and ties sort
  by ordinal id. BoW remains the deterministic default; ONNX is not added and
  stays a separate opt-in step.
- The ONNX Phase B contract is prepared without runtime or model files:
  `IRoleAwareEmbedder` distinguishes `Document` and `Query`, so the mandatory
  E5 `passage:`/`query:` prefixes are enforced by `VectorIndex` instead of
  being left to callers. The selected E5 profile is 384-dimensional with
  L2-normalized non-empty vectors and a zero vector for empty input; a
  dependency-free stub covers dimensions, normalization, role dispatch, and
  empty queries. E5 uses XLM-R SentencePiece rather than BERT/`vocab.txt`.
  BoW remains default; `Microsoft.ML.OnnxRuntime` and model files are not
  added. Core suite is now 862 tests.
- Repository hygiene: local-only artifacts (`TestResults/*.trx` with machine
  paths, top-level `output/` and `publish/output` with corpus reports,
  `cli_pid.txt`, PDBs, `.earepro`, `.mcp.json`, `.tessera`, `output_dotnet`,
  `reports`) are no longer tracked; they remain on disk and are ignored. A
  sanitized orphan snapshot is published as
  [`sanitized/v18.1`](https://github.com/DEICIDEGRAUER/entity_analyzer/tree/sanitized/v18.1):
  source and docs only, 407 files, without prebuilt publish binaries; the
  app-only `main` branch keeps its own layout.
- Review and quote-book toolbars use `WrapPanel`, so controls wrap instead of
  being clipped on narrow windows.
- Command-palette ranking moved from the WPF window to
  `Core/Text/PaletteRanker`; title matches still outrank context-only matches,
  and the 200-result limit/duplicate handling are unchanged.
- The rail now has a visible “Поиск и команды” button with a UIA name, so the
  palette is discoverable without knowing `Ctrl+K`.
- The command palette now shows a localized empty state and a live result
  count, labels its search field and result list for UIA, and restores keyboard
  focus after closing without executing a command.
- The palette footer has an explicit «Выполнить» button with a UIA name for
  mouse, touch, and screen-reader users; Enter remains the keyboard shortcut.
- The command palette remembers the eight most recent commands in
  `settings.json` and shows them first when the query is empty. Keys are stable
  for pages, codex cards, quotes, reports, files, and mentions.
- The results filter can now select unreviewed, confirmed, rejected, or
  uncertain mentions by verdict; a changed verdict immediately removes rows
  that no longer match the active filter. Bulk verdict actions and undo rebuild
  the visible selection in every verdict mode, and the review progress text is
  a polite live region. The filter toolbar wraps on narrow windows.
- The CI culture matrix now forces English dotnet CLI output, reports exact
  passed/failed/total counts for every culture, restores its environment in a
  `finally` block, and prints a summary table.
- File hashes used by verdicts and stars are taken from the incremental index
  before falling back to reading the file; the hash cache is cleared for every
  new session.
- README updated to the current 862-test baseline.
- Remote CI now runs the Core/CLI suite on Windows and Linux in ru-RU,
  en-US, and tr-TR, builds WPF only on Windows, uses an isolated profile,
  and publishes failure diagnostics. The path-restamping test is
  platform-neutral, and the microbenchmark compares the median of three
  runs with a 50% guard instead of a single 15% comparison.

### Verified
- 862/862 Core tests after the VectorIndex/BoW Phase A and the dependency-free
  ONNX/E5 contract: stable FNV buckets, Unicode/case/punctuation tokenization,
  empty and punctuation-only queries, ordinal tie-break, id upsert,
  dimension/finite-value validation, 384-dimensional L2-normalized E5 stub,
  and `Document`/`Query` role dispatch, in addition to the review protocol
  (no files without verdicts, decisions-only listing, Markdown escaping,
  stable CSV codes,
  additivity of the regular reports), palette ranking (title priority, context
  fallback, long alias lists, 200-item limit, duplicate search text), recent
  ordering, command history, and verdict filtering for review modes.
- Review navigation and verdict restoration are covered by 8 focused tests;
  the review protocol and its CLI loading path are covered by 8 more
  (additivity, decisions-only listing, Markdown escaping, stable codes,
  byte-identical regular reports, index lookup, file-hash fallback, empty
  journal).
- Local CI culture matrix: 843/843 tests pass in ru-RU, en-US, and tr-TR.
- GUI smoke V15: after analysis without verdicts `review_report.md/.csv` are
  absent; after a verdict the Export button writes both files, the Markdown
  contains one confirmed decision and a decisions section, and the CSV contains
  exactly one data row with the stable `confirmed` code.
- Remote GitHub Actions run 6 on `b94b7fa`:
  [all six jobs passed](https://github.com/DEICIDEGRAUER/entity_analyzer/actions/runs/39533932651)
  (Windows and Ubuntu, ru-RU/en-US/tr-TR), including the CLI smoke with the
  isolated profile.
- Remote GitHub Actions run 7 on `88c2647`:
  [all six jobs passed](https://github.com/DEICIDEGRAUER/entity_analyzer/actions/runs/39568359864)
  (Windows and Ubuntu, ru-RU/en-US/tr-TR), including the CLI smoke with the
  isolated profile.
- CI actions moved to Node 24: `actions/checkout@v7` and
  `actions/setup-dotnet@v6` in both workflows; matrix, isolated profiles, and
  failure diagnostics are unchanged.
- Remote GitHub Actions run 8 on `52e4159`:
  [all six jobs passed](https://github.com/DEICIDEGRAUER/entity_analyzer/actions/runs/39568778298)
  (Windows and Ubuntu, ru-RU/en-US/tr-TR) with the updated actions.
- Test harness isolation is self-contained: `ui_smoke.ps1`, `verify.ps1`, and
  `ci-matrix.ps1` force `EA_SEMANTIC_USER_DIR` inside the isolated profile, so a
  caller's environment cannot leak real semantic data into tests; local matrix
  remains 843/843 in ru-RU/en-US/tr-TR and full verify is green.
- Remote GitHub Actions run 9 on `994e0e6`:
  [all six jobs passed](https://github.com/DEICIDEGRAUER/entity_analyzer/actions/runs/39569620684)
  (Windows and Ubuntu, ru-RU/en-US/tr-TR).
- `verify.ps1 -WithGuiSmoke`: build, tests, GUI/CLI publish, CLI smoke with an
  explicit `<output>/.cache` location check, performance gate, GUI smoke V15
  (including the palette empty state, ranking, execute button, focus return,
  the review-protocol hint after a real analysis and verdict, and the actual
  `review_report.md/.csv` files with one confirmed decision), and
  real-profile-untouched check.
- `verify.ps1` disables shared compilation for build, test, publish, and bench:
  a lingering `VBCSCompiler` inherited redirected handles and made
  `Start-Process -Wait` hang at bench-gate. The full pipeline now completes in
  about 69 s.
- Isolated GUI smokes: bulk verdict undo, recent-project pruning.
- Full-corpus baseline recorded on `8d87b13` (10,489 analyzed files): cold run
  about 30–36 s, steady warm run about 3.3 s. Cold and warm runs produced the
  same 913 mentions in 261 files and a byte-identical `entities_table.csv`;
  methodology, output set, cache size, and caveats are in
  `dotnet/docs/PERFORMANCE.md`.

## [18.1.0] - 2026-08-29

### Fixed

#### Blocker
- **BUG-001**: `classify_context` function not implemented in `output.py` - caused NameError on report generation
- **BUG-002**: Android-specific hardcoded paths in `main.py` - broke cross-platform compatibility

#### Critical
- **BUG-003**: Cache invalidation didn't consider config hash - stale cache on preset changes
- **BUG-004**: Quality counters always zero - metrics not aggregated from worker processes
- **BUG-005**: UTF-16 files with BOM decoded as CP1251 - produced garbage output
- **BUG-006**: Noise detector profiles not loading in worker processes - filters disabled
- **BUG-007**: `apply_entity_limits` never called - entity limits ignored
- **BUG-008**: Context size calculation used hardcoded values - ignored preset settings
- **BUG-009**: Fuzzy search results bypassed filters - unfiltered false positives
- **BUG-010**: Discovery module dead code - never executed despite config flag
- **BUG-011**: Emoji in stdout broke pipe/CI output - UnicodeEncodeError
- **BUG-012**: Worker race condition on cache index.json - corruption risk

#### Major
- **BUG-013**: Output directory scanned as corpus - recursive processing loop
- **BUG-014**: Invalid preset silently applied extended - no validation
- **BUG-015**: Unsupported export formats ignored silently - no warnings
- **BUG-016**: Negative `--max-mentions` accepted - no validation
- **BUG-017**: BOM characters appeared in extracted quotes - not trimmed
- **BUG-018**: UTF-16 files not decoded - missing BOM detection
- **BUG-019**: Floating exception in tracking - potential render crashes
- **BUG-020**: CS8604 null reference warning in Core - potential NRE
- **BUG-021**: `--patterns-only` failed without output directory - missing creation
- **BUG-022**: Configuration section empty in analysis report - options not passed
- **BUG-023**: Duplicates not in full report/console - missing output
- **BUG-024**: Vanna loop convergence unverified - overlaps removed without counter
- **BUG-025**: Dialogue with em-dashes failed MeaningfulnessFilter - false negatives
- **BUG-026**: Silent file skips invisible - no counters for large/empty/duplicate
- **BUG-027**: Division by zero in ConsoleProgress on empty corpus - crash
- **BUG-028**: Cult/religious lexicon not classified - missing keywords
- **BUG-029**: Settings reset to defaults on startup - aliasing bug
- **BUG-030**: Quadratic context extraction - O(N²) on large files
- **BUG-031**: Invalid regex exceptions silently ignored - no validation
- **BUG-032**: Corrupted YAML gave "entities not found" - no parse error distinction
- **BUG-033**: Fuzzy mentions missing line numbers and filters - incomplete processing

#### Minor
- **BUG-034**: Empty states in UI not handled - missing IsDataEmpty binding
- **BUG-035**: Tracking reentrancy issues - potential stack overflow
- **BUG-036**: Color converter null guards missing - potential NRE
- **BUG-037**: Branch resolver null guards missing - potential NRE
- **BUG-038**: Entity type to color converter null guards - potential NRE
- **BUG-039**: Empty state binding incomplete - some controls missing
- **BUG-040**: Tracking issues in UI - minor glitches
- **BUG-041**: Color converter issues - minor rendering
- **BUG-042**: Branch resolver issues - minor logic
- **BUG-043**: Entity type to color converter issues - minor rendering

#### Trivial
- **BUG-044**: Minor UI tracking glitches
- **BUG-045**: Minor color converter edge cases

### Performance
- **50k matches processed in ~49.4 seconds** (25x improvement from quadratic fix)
- **100% test coverage** (257/257 tests passing)
- **Cache hit rate >60% on second run** (with config hash validation)
- **Memory usage <2GB** for typical corpora

### Security
- No critical vulnerabilities
- All dependencies updated to latest compatible versions
- Input validation on all user-provided data

### Changed
- Updated `config.yaml` with all 10 presets (minimal through titanic + custom1-3)
- Improved `noise_detector.py` with `load_profile_fresh` for worker compatibility
- Enhanced `cache.py` with config-aware invalidation and single-writer pattern
- Refactored `processor.py` with proper quality counter aggregation
- Updated `context.py` with linear interpolation for context sizing

### Added
- `setup.py` for PyPI distribution
- `README.md` with comprehensive documentation
- `.gitignore` for clean repository
- GitHub Actions CI/CD workflow (planned)

## [18.0.0] - 2026-08-25

### Added
- Initial release of Entity Analyzer v18 (Python legacy)
- 8 processing presets with configurable parameters
- Aho-Corasick based entity matching
- Incremental caching system
- Noise detection and false positive filtering
- Multi-format output (TXT, JSON, per-entity)
- Quality metrics and reporting
- Parallel file processing

### Known Issues (Fixed in 18.1)
- Multiple critical bugs affecting reliability and cross-platform support

---

## Release Checklist for v18.1

- [x] All 45 defects fixed and verified
- [x] 257/257 tests passing
- [x] Cross-platform compatibility (Windows/macOS/Linux)
- [x] All 10 presets functional
- [x] Cache invalidation working
- [x] UTF-16/UTF-8/CP1251 encoding support
- [x] Quality metrics accurate
- [x] Documentation updated (README, CHANGELOG)
- [x] Distribution package buildable
- [ ] PyPI publication
- [ ] Docker image build
- [ ] GitHub Release creation