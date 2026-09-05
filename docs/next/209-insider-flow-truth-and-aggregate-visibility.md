# Task: Make the insider-flow signal tell the truth in aggregate and in its label

## Overview

Two skeptic reviews (2026-09-04, AGX and NWPX) found the insider channel misfiring in opposite directions,
and in both cases the per-filing machinery behaved as designed — the defect is what the design cannot see:

1. **NWPX:** eleven Form 4s in the current window all rendered as `InsiderBuying (Neutral)` /
   "insider stock transaction (routine)". Web verification shows they are weekly 10b5-1 planned
   dispositions by the CEO and a director, into record results. Each filing is individually Neutral by
   design — but the sustained aggregate (weekly planned dispositions for six-plus weeks) is a real
   governance observation no per-filing rule can represent, and the rendered type token `InsiderBuying`
   over a disposition stream is an inverted label even at Neutral direction.
2. **AGX:** external sources report ~$119M of insider sales in H1 2026, while Radar's evidence carries a
   single ~$3.3M discretionary-sale signal. How much of the gap is the window, plan-classified filings, or
   never-collected filings is **UNMEASURED** — measuring it (within what the store can say, see below) is
   this spec's first deliverable.

**The hard data constraint this spec is built around (2026-09-05 pre-spec review, verified in code):**
`HttpSecForm4Reader.Classify` treats a 10b5-1 plan filing as Neutral **before** reading transaction codes,
shares or prices (`HttpSecForm4Reader.cs` — "a 10b5-1 plan forces every transaction Neutral"), and the
durable evidence for such filings retains only the plan marker, **no transaction value**. Discretionary
filings retain a net dollar aggregate, not separate purchase/sale totals. Therefore historical plan
filings' codes and values are UNKNOWN and stay unknown (no backfill, no re-fetch of accrued filings); every
aggregate below must render what was captured and say "not captured" for the rest.

What already works and is not re-litigated: `SecForm4TransactionCode.Classify` (P→purchase, S→sale,
plan/derivative codes→excluded); the materiality tiers and cluster boost as config (spec 93/96).

## Assignment

Worktree: any. Dependencies: none beyond current main. Not concurrent with spec 210 (both touch the weekly
report surface). Use `run-next.ps1 -Spec 209`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Measure first — the live insider-flow audit (read-only, within what was persisted)

Audit the accrued store across the live universe, from persisted classification reasons and captured
discretionary values ONLY:

- per company over the current 60-day window: Form 4 evidence count; filings by persisted class
  (plan/routine vs discretionary purchase vs discretionary sale vs excluded) and, where a discretionary
  net value was captured, that value;
- **NWPX case study:** what the durable data can actually say — expected shape "11 planned-disposition
  filings; transaction value not captured; recurring cadence" — plus explicit confirmation that per-filing
  codes/values for plan filings were never persisted (that absence IS the finding);
- **AGX case study:** reconcile Radar's captured discretionary value against the externally reported ~$119M
  H1 figure into named buckets: outside the window / plan-classified (value not captured) / never
  collected / captured. Buckets that cannot be separated from durable data are reported as
  **not established** — external figures are context, never adopted as Radar measurements;
- the distribution rule: report the plan-vs-discretionary split across the universe. If plan filings
  dominate everywhere, that is the headline finding about what the channel can currently see.

The audit lands in the PR body (or `data/audits/` with the PR quoting totals).

## 2. The rendered label — a presentation-only seam over BOTH render paths

`SignalType.InsiderBuying` is a persisted token on accrued signals — it must keep deserializing unchanged
(no enum rename, no stored-JSON rewrite). The fix is presentation-only, and it must cover **both** places
the token reaches the reader (2026-09-05 review): the signal-type rendering AND the evidence-contribution
reason lines that `MarkdownWeeklyReportRenderer` currently renders verbatim. An exact-token,
presentation-layer replacement (e.g. `InsiderBuying` → `InsiderActivity`) applied at render time in both
paths; every other type renders exactly as today. Pin with a test that a planned-disposition stream row
never renders the word "Buying" — and note the report-language tests already forbid the bare substrings
"buy"/"sell" (`MarkdownWeeklyReportRendererTests.ForbiddenWords`), so all new wording uses
**"purchase value" / "sale value" / "planned disposition"**.

## 3. Aggregate visibility — a structured summary, honest about what was captured

Add a structured `InsiderActivitySummary` to `WeeklyReportEntry` (report-side only, numerically inert — no
new signal, no scoring input, no fingerprint move), assembled in the report builder from the DISTINCT Form 4
evidence items inside the snapshot's exact window:

- filings count; planned-disposition count; discretionary purchase-value and sale-value totals where the
  reader captured them; an **unallocated/mixed** bucket for filings whose persisted net value cannot be
  split into purchase vs sale (the reader retains only a net aggregate); and a cadence note derivable from
  filing dates (e.g. "recurring weekly");
- `null`/absent renders "not captured", never 0 — a plan filing with no persisted value is counted as a
  filing and excluded from every value total;
- the rendered NWPX line reads like: "11 planned-disposition filings; transaction value not captured;
  recurring cadence" — legible without opening eleven filings, and claiming nothing the store cannot back.

## 4. Optional, forward-only: capture what tonight's filings actually say

At the implementer's discretion (skippable if it inflates the slice): begin persisting, for NEW Form 4
evidence only, per-filing diagnostic counts — transaction-code tally and gross purchase/sale values even
when the plan flag forces the signal Neutral. Constraints if taken: forward-only (historical filings remain
unknown; no backfill); additive fields; and it must be shown NOT to perturb evidence identity/content-hash
or any fingerprint — if it would, put it in a diagnostic sidecar (the ai-debug pattern) instead of the
evidence record, or drop it and record the deferral.

## 5. Only measured follow-ups may tune

If §1 shows large discretionary sales being systematically missed, the remedy (collection depth, window) is
its own measured spec. Tier-boundary changes are config edits (spec 96). No
`KeywordSignalExtractor.RuleSetVersion` change, no phrase-table change; all six fingerprint pins
byte-identical.

## Non-goals

No change to `SecForm4TransactionCode.Classify`, the 10b5-1-forces-Neutral rule, tier values, cluster
boost, collection windows or fetch depth; no new signal type or scoring input; no enum rename or accrued
JSON rewrite; no re-fetch/backfill of accrued filings; no adoption of external figures as measurements.

## Acceptance criteria

- [ ] The §1 audit is recorded from persisted data only, with both case studies resolved into named buckets
      or explicitly "not established"; the plan-filings-carry-no-value constraint is stated where relied on.
- [ ] No report surface (type rendering OR verbatim reason lines) renders "Buying" for this channel;
      accrued signals deserialize unchanged; wording passes the existing forbidden-substring tests.
- [ ] `InsiderActivitySummary` is structured on the entry, built from distinct window evidence, with
      purchase/sale/unallocated buckets and "not captured" semantics; the NWPX shape is legible from the
      line alone.
- [ ] If §4 is taken: forward-only, additive, and demonstrably identity/fingerprint-inert (or explicitly
      deferred).
- [ ] All six fingerprint pins byte-identical; no `RuleSetVersion` change; build, full suite,
      `git diff --check` clean; actual elapsed time in the PR body.
