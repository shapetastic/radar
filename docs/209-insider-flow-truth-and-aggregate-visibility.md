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
filings retain a single stored magnitude whose mixed-filing meaning is neither net nor total
(`Math.Max(purchaseValue, saleValue)`), not separate purchase/sale totals. Therefore historical plan
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

- per company over the current 60-day window: Form 4 evidence count; filings by the ACTUAL persisted
  classification tokens (`SecForm4ClassificationReasons`): `plan-10b5-1`, `discretionary-buy`,
  `discretionary-sale`, `mixed-buy-sell`, `no-discretionary-transactions`, plus a **missing/unknown**
  bucket for legacy evidence that predates the tokens. There is no separable "excluded" class — grants,
  holdings-only and empty filings all land in `no-discretionary-transactions` and cannot be told apart
  after the fact; the audit says so rather than inventing a split. Where a discretionary value was
  captured, report it — noting that for `mixed-buy-sell` the persisted figure is
  `Math.Max(purchaseValue, saleValue)` (`HttpSecForm4Reader.cs`), NOT a net or a total, so it must never
  be summed into any value column: report the mixed-filing COUNT and state that its purchase/sale split
  and total were not captured;
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

- buckets mirroring the persisted taxonomy exactly: filings count; planned-disposition count;
  `discretionary-buy` purchase-value total and `discretionary-sale` sale-value total where captured; a
  **mixed-buy-sell count** whose value is deliberately NOT totalled (the persisted figure is
  `Math.Max(purchaseValue, saleValue)` — neither a net nor a total; render "split and total not
  captured"); a `no-discretionary-transactions` count; and an **unknown** count for legacy evidence with
  no classification token;
- the summary is read through ONE shared contract: an Application-level `InsiderActivityMetadata`
  reader/record that Infrastructure WRITES through — the classification tokens currently live in
  `SecForm4ClassificationReasons` (Infrastructure) while `WeeklyReportBuilder` is Application, so without
  the shared home the implementation either duplicates magic strings or inverts the dependency direction
  (both forbidden; reuse-over-copy);
- no fuzzy cadence adjective: the span is stated objectively as "N planned-disposition filings across D
  days" (D = `(lastDate - firstDate).Days`, elapsed days; omitted when N < 2);
- `null`/absent renders "not captured", never 0 — a plan filing with no persisted value is counted as a
  filing and excluded from every value total;
- the rendered NWPX line reads like: "11 planned-disposition filings across 29 days; transaction value not
  captured" — legible without opening eleven filings, and claiming nothing the store cannot back.

## 4. Forward transaction-code capture is DEFERRED to its own slice

Persisting per-filing code tallies and gross purchase/sale values for new filings (even when the plan flag
forces Neutral) is explicitly OUT of this spec — one spec must have one implementation, and this slice is
already large. Record it as a deferred follow-up in the PR body; when specced, it is forward-only, additive
and must be shown identity/fingerprint-inert (sidecar if not).

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
- [ ] `InsiderActivitySummary` is structured on the entry, built from distinct window evidence through the
      shared Application-level `InsiderActivityMetadata` contract (Infrastructure writes through it), with
      buckets mirroring the persisted tokens (incl. mixed and unknown), mixed values never totalled, the
      deterministic "N filings across D days" span, and "not captured" semantics; the NWPX shape is legible
      from the line alone.
- [ ] Forward transaction-code capture is deferred (recorded in the PR body), not partially implemented.
- [ ] All six fingerprint pins byte-identical; no `RuleSetVersion` change; build, full suite,
      `git diff --check` clean; actual elapsed time in the PR body.
