# Release horizon: trusted view and source sidecar

**Решение (2026-10-30): v18.2 exploratory, не v18.1 default.**

Ветка `release/explore-trusted-view-v18.1` сохраняет историческое имя, но её
продуктовый горизонт — **v18.2 exploratory**. Это не меняет default v18.1:
raw/review остаётся default-вкладкой, generator default остаётся `Legacy`.
Source gate и sidecar остаются additive safety-слоем; они не являются
разрешением на replacement/default view.

## Почему не v18.1 default

- Classifier-only probe 2: P=1,000, но R=14/23 (**60,9%**).
- Source-gated product view: 13/23 (**56,5%**) trusted-good; P2-29 уходит в
  review из-за duplicate-content гейта.
- Замена/default требуют `P≥0,85` и `R≥0,80`; условие не выполнено.
- Trusted view — high-precision exploratory, а не production-SLO. Source
  gate — safety-исправление, а не доказательство качества всех 267.

## Milestone

**Target milestone: v18.2 RC** (следующий minor после v18.1). До этого
изменения остаются opt-in; default-вкладка и `Legacy` не меняются.

## Условия promotion (pre-registered)

Promotion в v18.2/default допускается только при одновременном выполнении:

1. Независимый probe (не тот же sample) даёт для одного bucket
   **P≥0,85** и **R≥0,80** с заранее зафиксированными labels.
2. Source safety properties сохранены: `sidecar > detector`,
   missing→`unknown`, duplicate worst-source, raw/review содержит все строки.
3. В trusted попадают только `source=usable`; 0 violations.
4. `verify.ps1 -SkipPublish`, `--self-check`, smoke anchor и CI зелёные;
   основной 8-колоночный CSV не изменён.
5. Отдельный explicit commit меняет default; rollback/default switch
   остаётся доступным.

Если условия не выполнены к v18.2 RC, ветка остаётся opt-in exploratory или
закрывается как evidence. Silent merge в default не выполняется.

## Source gate отдельно

Source gate можно cherry-pick в v18.1 как safety-only изменение, если
release owner решит, что safety-фикс нужен раньше v18.2. Это отдельное
решение и отдельный CI-прогон; trusted view при этом не становится default.
