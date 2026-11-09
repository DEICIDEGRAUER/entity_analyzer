# Release horizon: trusted view and source sidecar

**Решение 1 (2026-10-30): v18.2 exploratory, не v18.1 default.**
**Решение 2 (2026-11-09, owner): v18.2 RC — активация генератора как
default; дедлайн 2026-11-17 закрыт с запасом 8 дней.**

Ветка `release/explore-trusted-view-v18.1` сохраняет историческое имя, но её
продуктовый горизонт — **v18.2 RC**. После активации:
raw/review остаётся default-вкладкой; генератор default —
`FullPhrasePlusExceptions`, `Legacy` доступен как `--generator legacy`
на один релизный цикл (rollback-путь). Source gate и sidecar остаются
additive safety-слоем; они не являются разрешением на replacement/default view.

## Почему не v18.1 default

- Classifier-only probe 2: P=1,000, но R=14/23 (**60,9%**).
- Source-gated product view: 13/23 (**56,5%**) trusted-good; P2-29 уходит в
  review из-за duplicate-content гейта.
- Замена/default требуют `P≥0,85` и `R≥0,80`; условие не выполнено.
- Trusted view — high-precision exploratory, а не production-SLO. Source
  gate — safety-исправление, а не доказательство качества всех 267.

## Milestone

**Target milestone: v18.2 RC** (достигнут активационным commit
2026-11-09). До активации изменения оставались opt-in; после активации
default-вкладка raw/review не меняется, но генератор даёт новый default.

## Условия promotion (pre-registered)

Promotion в v18.2/default допускается только при одновременном выполнении:

1. Независимый probe (не тот же sample) даёт для одного bucket
   **P≥0,85** и **R≥0,80** с заранее зафиксированными labels.
   **Independent** здесь означает все три составляющие: новый sample (не 50
   строк probe 2), новый annotator (если доступен; как минимум не автор
   проверяемых labels) и другой срез корпуса (не phase-c-1000, если
   практически возможно). Повторный запуск probe 2 через неделю не является
   independent.
2. Source safety properties сохранены: `sidecar > detector`,
   missing→`unknown`, duplicate worst-source, raw/review содержит все строки.
3. В trusted попадают только `source=usable`; 0 violations.
4. `verify.ps1 -SkipPublish`, `--self-check`, smoke anchor и CI зелёные;
   основной 8-колоночный CSV не изменён.
5. Отдельный explicit commit меняет default; rollback/default switch
   остаётся доступным.

Если условия не выполнены к v18.2 RC, ветка остаётся opt-in exploratory или
закрывается как evidence. Silent merge в default не выполняется.

## Generator activation отдельно

**Activated 2026-11-09:** criterion v2 was green (2026-10-30), the 198 trace
events stayed frozen and guarded, the smoke anchor was a verified no-op, and
the release owner selected **v18.2 RC** before the 2026-11-17 deadline. The
production default is `FullPhrasePlusExceptions`; `Legacy` stays reachable as
`--generator legacy` for one release cycle (rollback path), then is removed in
v18.3. The decision table below is kept as the historical choice record:

| Option | Meaning | Outcome |
|---|---|---|
| **v18.2 RC** | generator becomes the new default in a versioned release | **SELECTED 2026-11-09 and activated** |
| **v18.2 opt-in** | `FullPhrasePlusExceptions` stays opt-in; `Legacy` remains default | not selected |
| **v18.3** | not in this branch; leave the plan in place | not selected; fallback superseded by the explicit decision |

Activation carried the new corpus anchor, selfcheck re-baseline, release
notes, and downstream notices named in `OUTPUT_GENERATOR_POLICY.md §16/§17`.
It was not a criterion reframe. See §17 for the activation record.

**Deadline: 2026-11-17 — closed early on 2026-11-09 by the explicit
v18.2 RC decision.** The deadline exists so the branch is never closed as
"still waiting"; the explicit decision satisfied it ahead of time.

If the deadline had passed without an explicit owner decision, the branch
would have been reclassified as evidence for v18.3, not "still waiting."
That clause is now superseded by the recorded decision.

## Source gate отдельно

Source gate можно cherry-pick в v18.1 как safety-only изменение, если
release owner решит, что safety-фикс нужен раньше v18.2. Это отдельное
решение и отдельный CI-прогон; trusted view при этом не становится default.

## v18.3 backlog — известное состояние (2026-11-09)

Backlog отражает известное состояние, а не только принятые действия; источник
каждого пункта указан, статус HOLD значит «не решено», а не «сделано». Не все
пункты обязательно попадут в v18.3.

| Пункт | Состояние / источник |
|---|---|
| Удалить `--generator legacy` | После одного цикла без якорных регрессий (OUTPUT_GENERATOR_POLICY §17.7). |
| Кириллическое склонение canon-транслита («Нугу и Йебу», caps «НУГА и ЙЕБА») | OUTPUT_GENERATOR_POLICY §17.8: ~9 строк (~1%); корень — `FullPhraseMorphology.Decline` получает английский canon (`Nug and Yeb`) → `HasCyrillic=false` → нет canon-translit фразы. |
| Provenance-класс `verified_clean` (P2-29) | OUTPUT_SOURCE_POLICY.md: байт-идентичность ≠ верифицированная чистота; класс пуст, P2-29 и ~322 строк остаются в `review`. |
| Финальный provenance 267-tail | OUTPUT_SOURCE_AUDIT.md / OUTPUT_SOURCE_POLICY.md: 267 файлов, 6 363 строки, 1 488 flagged; `unknown`, не `clean`; не переводить в `usable` без owner-review. |
| 32 дубликата aliases | HOLD (OUTPUT_GENERATOR_POLICY.md: HOLDs, Leng no-op и 32 дубликата aliases остаются как есть). |
| HOLD'ы каталога: `The Opener of the Way`, `Ghisguth`/`Гисгут`, `God of the Dead` | OUTPUT_CATALOG_AUDIT.md: misattribution_removal (45 строк, титул Anubis, не Yog); shared_unresolved (alias shared с `Сокар`, карточки не совпадают); owner-review (для `Ghisguth`/`Гизгут` нет подтверждённого reassign). |
| `bench-gate` и power plan | bench gate локальный (CI его не гоняет) и требует Windows High performance; для CI нужен runner с фиксированной частотой или альтернативная методика измерения. |
| env-pin требование в рецептах | Review-export/verify рецепты требуют `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` для байт-сравнений (CHANGELOG/§17.8); при желании консолидировать в readme рецепта смок-цикла. |
