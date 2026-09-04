# Task: Make the insider-flow signal tell the truth in aggregate and in its label

## Overview

Two skeptic reviews (2026-09-04, AGX and NWPX) found the insider channel misfiring in opposite directions,
and in both cases the per-filing machinery behaved as designed — the defect is what the design cannot see:

1. **NWPX:** eleven Form 4s in the current window all rendered as `InsiderBuying (Neutral)` /
   "insider stock transaction (routine)". Web verification shows they are weekly 10b5-1 **sales** by the CEO
   and a director, into record results. Each filing is individually below the spec-93 materiality tiers, so
   each is correctly routine — but the sustained aggregate (weekly sells for six-plus weeks) is a real
   governance observation that no per-filing tier can represent, and the rendered signal-type token
   `InsiderBuying` over a stream of sells is an inverted label even at Neutral direction.
2. **AGX:** external sources report ~$119M of insider sales in H1 2026 (~$79M in the trailing three months),
   while Radar's evidence set carries a single ~$3.3M open-market-sale signal. Whether the gap is the
   scoring window, collection depth, filing shapes the reader excludes (derivative/plan codes → 
   `NeutralExcluded` by design), or something else is **UNMEASURED** — that measurement is this spec's first
   deliverable, not an assumption.

What already works and must not be re-litigated: `HttpSecForm4Reader` parses `transactionCode` and
`SecForm4TransactionCode.Classify` maps P→Buy, S→Sell, plan/derivative codes→NeutralExcluded (conservative
by design); the materiality tiers and cluster boost are config (`InsiderMaterialityWeights`, spec 93/96).
This spec adds truth at the AGGREGATE and the LABEL, and measures before it tunes.

## Assignment

Worktree: any. Dependencies: none beyond current main. Use `run-next.ps1 -Spec 209`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Measure first — the live insider-flow audit (read-only)

Before changing anything, audit the accrued store across the live universe:

- per company over the current 60-day window: Form 4 evidence count; signals by rendered class
  (routine / open-market buy / open-market sale / excluded) and direction; the dollar value where the
  reader captured one;
- the two case studies in full: **NWPX** (list every current-window Form 4 with its actual transaction
  code(s) and value — confirm or refute the skeptic's sell-stream reading from Radar's own fetched data)
  and **AGX** (reconcile Radar's captured insider value against the externally reported ~$119M H1 figure:
  how much falls outside the window, how much was code-excluded, how much was never collected);
- the distribution rule applies: report what the routine bucket actually contains across the universe
  (share of S-coded vs P-coded vs excluded-code filings). If routine turns out to be ~all sells
  everywhere, that is a finding about the label, not a reason to tune tiers here.

The audit lands in the PR body (or `data/audits/` with the PR quoting totals). If a case-study fact cannot
be established from durable data, record **not established** — never adopt the skeptic's web numbers as
Radar measurements.

## 2. The rendered label stops saying "buying" over sells

`SignalType.InsiderBuying` is a persisted token on accrued signals — it must keep deserializing unchanged
(AD-8; do NOT rename the enum member or rewrite stored JSON). Fix the READER-FACING surfaces only: wherever
reports/leaders/evidence lines render the type for this channel, render a neutral channel name (e.g.
`InsiderActivity`) with the per-filing class already carried in the reason/title ("routine", "open-market
sale…"). One display mapping, applied at render time; every other signal type renders exactly as today.
Pin with a test that a routine sell-stream row never renders the word "Buying".

## 3. Aggregate visibility — the windowed net-flow line

Add to the weekly report's per-company section (report-side only, numerically inert — no new signal, no
scoring input, no fingerprint move):

- one line per company with Form 4 activity in the window: filings count, captured buy value, captured sell
  value, excluded-code count, and the dominant filer pattern (e.g. "11 filings, 0 buys, ~$X sells captured,
  weekly cadence") — derived from the window's evidence/signals at render time;
- `null`/absent values render "not captured", never 0 — a filing whose value the reader did not extract is
  counted as such;
- the NWPX shape must be legible from this line alone (a human sees "sustained small sells" without opening
  eleven filings), and the AGX shape must be visible as "captured $X of sells" so an external claim of a
  larger figure is checkable against what Radar actually holds.

## 4. Only measured follow-ups may tune

If §1 shows large sales being systematically missed (AGX), the remedy may be collection depth or window —
its own spec with its own measurement. If §1 shows the routine tier boundaries misplaced, that is a
**config edit** (`Radar:Insider` profiles, spec 96 — no `RuleSetVersion` bump). Neither is done in this
spec. A phrase→direction/strength TABLE change would bump `KeywordSignalExtractor.RuleSetVersion` and move
fingerprints — this spec deliberately makes no such change, and all six pins must be byte-identical.

## Non-goals

No change to `SecForm4TransactionCode.Classify`, the materiality tiers' values, cluster boost, collection
windows or the Form 4 reader's fetch depth; no new signal type or scoring input; no enum rename or accrued
JSON rewrite; no adoption of externally reported figures as Radar measurements.

## Acceptance criteria

- [ ] The §1 audit is recorded with the universe distribution and both case studies resolved from durable
      data (or explicitly "not established"), before any code change is justified by it.
- [ ] No report surface renders "InsiderBuying"/"Buying" for the insider channel; accrued signals
      deserialize unchanged; the display mapping is pinned by test.
- [ ] Every per-company section with window Form 4 activity carries the net-flow line; not-captured values
      render as such; the NWPX sell-stream is legible from the line alone.
- [ ] All six fingerprint pins byte-identical; no `RuleSetVersion` change; build, full suite,
      `git diff --check` clean; actual elapsed time in the PR body.
