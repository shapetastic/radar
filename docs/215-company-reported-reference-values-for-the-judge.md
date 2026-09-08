# Task: Company-reported reference values — the filing read extracts and durably records the metrics a release states, and the judge is handed the prior values of any metric a news fact quotes

## Overview

Spec 214 stops the judge treating a level as a trend. It cannot make the judge RIGHT about the trend,
because Radar holds no prior value to compare against: on AGX the store's only backlog figure is the $2.5B
the judge misread, the 8-K's raw evidence is a 145-character stub, and the EX-99.1 body that
`ChatFilingAnalyzer` reads live is never persisted (the ai-debug record keeps a 2,000-char `inputHead`).
The comparison the skeptic made in ten minutes — $2.93B (Jan 31) → ≈$2.8B (Apr 30) → $2.52B (Jul 31) — is
three numbers the company itself reported in filings Radar READ and then forgot.

This spec makes the filing read keep what it reads: a small, verified, append-only ledger of the metrics
each earnings release states, and a projection of that ledger into the judge's input so a news fact
quoting "backlog $2.5B" arrives beside "backlog: $2.93B as of 2026-01-31 (10-K), ≈$2.8B as of 2026-04-30
(8-K)" — reference values with their own citable ids. **Heal-forward only: no backfill.** The body of an
accrued filing is not in the store (AD-8 — nothing is re-fetched or regenerated), so the ledger starts at
the first post-merge filing read and a company's first comparison appears at its NEXT release. For AGX that
is ≈ December 2026. That is the honest cost of never having kept the text; it is stated, not hidden.

## Assignment

Worktree: any. Dependencies: spec 214 merged (this spec extends its `ComparisonBasis` rendering and shares
its identity boundary — **merge 214 and 215 back-to-back before the next baseline**, spec 214 §5). Use
`run-next.ps1 -Spec 215`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. The filing read also returns the metrics the release STATES — verified verbatim, then persisted

- `ChatFilingAnalyzer` (`Radar.Infrastructure.Filings`) extends its typed response with
  `reportedMetrics[]`: `{ metric, value, unit, periodEnd | asOf, priorValue?, priorPeriod?, quote }`.
  `metric` is a CLOSED enum in Application (`ReportedMetric`: Revenue, NetIncome, DilutedEps, GrossMargin,
  OperatingIncome, Backlog, CashAndInvestments, TotalDebt, FreeCashFlow, Guidance-* is NOT here — guidance
  is a statement, not a measurement); anything else is dropped and COUNTED (`metricsDroppedUnrecognised`).
  The prompt asks for "the figures the release STATES, with the period they are stated for, and the
  prior-period figure ONLY if the release itself states it; never compute, never infer".
- **Deterministic verification before anything is kept** (the spec-160 shape: the scan is code, not the
  model's word): `value` (and `priorValue` / `priorPeriod` when present — the prior period was added to
  the check in the PR #222 Copilot fix pass, since the prompt asks for it "exactly as printed") must appear
  VERBATIM — same digits, same unit token — inside `quote`, and `quote` must appear verbatim in the
  stripped body the analyzer read. A metric
  that fails either check is dropped and counted (`metricsDroppedUnverified`), never persisted. The
  `MaxInputLength` truncation applies as today; a quote beyond the truncation point cannot verify and is
  therefore dropped — counted, so a release whose numbers live past the cap shows up as a gap, not a zero.
- Persisted as `ReportedMetricRecord`s in a new append-only file store,
  `data/reported-metrics/{companyId}/{accession}.json` (`RadarFileStoreJson`/`GracefulFileWriter`, typed
  `DurableWriteResult`, spec-206 pattern), one record per (accession, metric, period): the value, unit,
  period, prior pair if stated, the verbatim quote, the evidence id, the filing date, the reader identity,
  and `verification: Verbatim`. Content-derived id (spec 145) so a re-read of the same accession is a no-op.
- `AnalyzedFilingRecord` gains `reportedMetricsPolicy` (nullable; null = written pre-215, a HIT — the
  spec-160 null-policy precedent: the accrued cache is never mass-invalidated, so ONLY NEW filing reads
  extract metrics). The policy token `reported-metrics-v1` names the metric enum + verification rule; a
  record whose non-null policy differs is a bounded automatic MISS (as for `cmpscan`).
- **Not a scoring input.** The ledger feeds the judge (§2) and the report (§4); it changes no signal,
  score, weight or fingerprint on its own. The filing read's DIRECTION/confidence/rationale are untouched.

## 2. The judge is handed reference values for the metrics its facts quote

- New `ReferenceValueProjector` (Application, pure): given the judge's supplied families (post-214, each
  carrying `ComparisonBasis`) and the company's ledger, select every ledger record whose `metric` matches a
  metric NAMED in a supplied statement (the spec-214 stock-noun table plus the flow metrics — revenue, net
  income, EPS, margin; matching is the closed table, `reference-projection-v1`), most recent first, at most
  4 per metric and 16 per judgment (caps declared, remainder COUNTED as `referenceValuesOmitted`).
- Rendered to the judge as a block AFTER the families:
  `Company-reported reference values (from SEC filings Radar read; cite by ReferenceId):`
  `ReferenceId: … · Backlog · $2.929B · as of 2026-01-31 · stated in 10-K filed 2026-04-xx · "quote"`.
  Prompt rule (12), `news-judgment-prompt-v4 → v5`: "Reference values are the company's own prior
  statements of the same metric. When a supplied fact quotes a metric with a reference value, read the
  DIRECTION from the comparison and cite BOTH the fact and the ReferenceId. Never cite a ReferenceId as a
  trajectory fact on its own — a reference value is a comparison basis, not news."
- Schema `news-judgment-schema-v3 → v4`: `TrajectoryReferenceIds[]` and per-finding `ReferenceIds[]`
  (complete ids, same copy-verbatim rule as FactIds; the validator resolves them against the projected set
  and fails a citation of an unsupplied id with a named reason, exactly as for FactIds). The judgment record
  persists the projected reference set (ids only) and the counts.
- Spec 214's `trajectoryBasis` gains a third value: `ReferenceSupported` — the trajectory rests on a
  LevelOnly fact PLUS a cited reference value for the same metric. That is the case this whole pair exists
  for ("backlog $2.5B vs $2.93B reported in January: down"), and it materializes normally under
  `news-judgment-signal-v3` (no fourth materializer version: a reference-supported comparison IS a
  directional basis).

## 3. Live distribution — what the ledger and the projection actually produce

Because the ledger is heal-forward, the first PR cannot show a populated ledger. The PR body reports,
from the read-only harness over the store at implementation time:

| measure | value |
| --- | ---: |
| analyzed-filing cache records (all null policy = pre-215) | n |
| earnings 8-K reads per baseline run (the forward accrual rate — how fast the ledger fills) | last 5 runs |
| supplied families per judgment naming a ledger metric (the projection's would-be hit rate on TODAY's facts) | share |
| AGX: which of its four 2026-09-07 cited facts would receive a reference block once one Argan release has been read | rows |

And the first post-merge run's PR-body follow-up (owed, descriptive): ledger records written, metrics
dropped unverified / unrecognised, judgments that received ≥ 1 reference value, `ReferenceSupported`
count. **A ledger that verifies < 50% of what the model returns is a prompt/verification defect, not a
finding** — say so and fix the tables before calling §1 done.

## 4. Report and docs

- Weekly report evidence line for an earnings 8-K gains, when the ledger has records for that accession:
  `— reported: revenue $384.0M (Q2 FY27), backlog $2.518B (as of 2026-07-31)` — values as stated, no
  arithmetic, no direction word. The judgment appendix row shows `basis: ReferenceSupported` and the
  reference ids cited.
- `docs/reading-radar-output.md`: what a reference value is, that it is the company's own prior statement
  read from a filing, that the ledger is heal-forward (a company's first comparison appears at its NEXT
  release), and that it is not a scoring input.
- `docs/architecture-history.md`: spec-215 bullet; amend spec 119's filing-read bullet IN PLACE ("the body
  is read and discarded" → "since spec 215 the stated metrics are kept, verified verbatim").
- `default.json` `_comment` gets NO history (spec 213); `Radar:Ai:ReportedMetrics:Enabled` (default true)
  is declared with its one-line reason.

## 5. Identity — the same boundary as spec 214

Prompt v5, schema v4 and `reference-projection-v1` enter the `news=` segment; the AI-ON pins move, the
AI-OFF pins must not. Merged back-to-back with 214, the operator step (spec 214 §5) is performed ONCE after
both merges and the first run's stamp is verified against `ScoringConfigFingerprintTests`.

## Non-goals

- Backfilling the ledger from accrued filings (the text is not in the store; no re-fetch — AD-8/AD-1).
- Reading 10-Q/10-K bodies for metrics (only what the earnings-release reader already fetches; widening
  the reader is its own evidence-led slice — and it is where the Jan-31 backlog actually lives).
- Any arithmetic on reference values (growth rates, deltas) — the judge compares; Radar states.
- Making the ledger a scoring input or a signal source.

## Acceptance criteria

- [ ] `reportedMetrics[]` in the analyzer response, closed `ReportedMetric` enum, verbatim verification of
      value-in-quote and quote-in-body, both drop classes counted; `AnalyzedFilingRecord.reportedMetricsPolicy`
      nullable with null = hit.
- [ ] `data/reported-metrics/{companyId}/{accession}.json` append-only, typed durable-write result,
      content-derived ids, re-read no-op.
- [ ] `ReferenceValueProjector` selects by the closed metric table with declared caps and counts omissions;
      the block renders after the families; prompt v5 rule 12; schema v4 `TrajectoryReferenceIds` /
      `ReferenceIds` validated like FactIds.
- [ ] `trajectoryBasis == ReferenceSupported` materializes under v3; `LevelOnly` still mints nothing.
- [ ] §3 table in the PR body; the post-merge follow-up owed and labelled; the 50% verification sanity
      bound stated.
- [ ] Report evidence line, appendix, operator guide, history (new + 119 in-place) updated.
- [ ] AI-ON pins moved once for 214+215 together and asserted by the tests; AI-OFF proven unchanged.
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.
