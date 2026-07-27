# Task: Report each strategy separately — one plain ranked table per strategy, no composition

> Spec 137 made the **primary** strategy "the series the weekly report renders", so a run with three
> strategies still produces a report about one of them. The first live 3-strategy run (2026-07-27) scored 43
> companies under `default`, `filings-led` and `narrative-led` — and only `default` appeared anywhere in the
> report. The other two existed solely as JSON under `data/scores/strategies/{name}/`.
>
> **Deliberately simple: report each strategy on its own terms, and do not combine.** No disagreement metric,
> no merged ranking, no composite score. The reader compares by eye. Composition is a later question, worth
> asking once the series have accrued enough to mean something — a computed "these strategies disagree"
> number over one day of history would rank noise and invite trusting it.

## Design

### 1. One section per strategy, after the existing content

Append a section per configured strategy, in configuration order with the primary first. Each is a **plain
ranked table** — no prose, no evidence blocks, no "why noticed":

```
## Strategy: filings-led (radar-formula-v9)
Fingerprint: radar-scoring-fp-7ef38390f90b · 43 companies scored

| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |
```

- Ordered by Opportunity descending, ties broken deterministically (AD-3 — reuse the existing ordering rule
  rather than inventing one).
- Include the strategy's `ScoringConfigVersion`, so a reader can tell two runs apart and can see at a glance
  when two strategies are *not* the same configuration.
- **The existing report is unchanged.** "Highest opportunity", "Watch", "Ignore / Low signal", the evidence
  entries and "Why noticed" all stay exactly as they are and stay **primary-only**.

### 2. No labels outside the primary

`Watch`/`Ignore`/`Investigate` stay the primary's. A company labelled `Watch` under one strategy and
`Ignore` under another is a contradiction the output-language rules do not contemplate, and it would read as
Radar equivocating. Non-primary sections carry **scores only**.

The hard output rule is unchanged: no advice vocabulary anywhere, and the standing "not financial advice /
research only / human review required" preamble still governs the whole document.

### 3. Gate it on more than one strategy

With a single configured strategy (the synthesised `default`, i.e. every existing deployment) the report
must be **byte-identical to today** — no new heading, no trailing whitespace. Assert that; it is what keeps
this additive.

### 4. Honest about what is being shown

Add one line under the first strategy section noting that these are independent scorings of the **same**
collection pass, that they are not directly comparable as absolute numbers when formulas differ, and that
ranking them against price is `data/efficacy/strategy-leaderboard.md` (spec 140), not this table. A reader
who eyeballs two rankings will otherwise infer a winner, which is the multiple-comparisons trap arriving via
the reader instead of the statistics.

## Files (verify against the tree before planning)

`WeeklyReportBuilder`, `MarkdownWeeklyReportRenderer`, `WeeklyReportActionPolicyV1` (labels stay
primary-only — confirm it is not invoked for non-primary series), and the report model. The per-strategy
score stores are reached through the same `IScoreRepository`/strategy-scoped factory the scoring stage
already uses — **do not add a second read path** to the strategy score files.

## Constraints

- **Single-strategy output byte-identical to today.** No pin move, no scoring change — this slice reads.
- **Reads only.** No new hashed input; `ScoringConfigVersion` is displayed, never computed here.
- **Provenance intact**: every rendered score traces to its snapshot; do not synthesise rows for a company a
  strategy did not score — omit it and say so in the section's count line.
- **Layering:** rendering stays in `Radar.Application.Reporting`.
- `ReportMaxItems` currently caps entries at 60; decide explicitly whether it applies per strategy section
  and document the choice — silently truncating a strategy's table is the spec-125 failure that motivated
  raising that cap in the first place.

## Out of scope (record, do not build)

- **Any cross-strategy composition** — disagreement metrics, merged rankings, composite scores, "consensus"
  columns. Explicitly deferred until the series have accrued.
- **Per-strategy evidence blocks or "why noticed".** Primary only, for now.
- **Per-strategy labels.**
- **Strategy-vs-price ranking** — spec 140 already does that.

## Acceptance criteria

- [ ] With N > 1 strategies the report gains one plain ranked table per strategy, primary first, each
      showing its `ScoringConfigVersion` and company count.
- [ ] With exactly one strategy the report is **byte-identical** to today — asserted.
- [ ] Labels, evidence blocks and "why noticed" remain primary-only.
- [ ] A company a strategy did not score is omitted from that strategy's table and reflected in its count.
- [ ] Ordering is deterministic and reuses the existing rule.
- [ ] The `ReportMaxItems` interaction is decided, documented and tested — no silent truncation.
- [ ] No fingerprint input added; pins unmoved.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
