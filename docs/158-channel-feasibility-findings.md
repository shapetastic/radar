# Spec 158 findings — channel feasibility characterization (INPUT ONLY)

> **POST-MERGE DESIGN NOTE · 2026-07-28.** The measurements below are unchanged, but subsequent inspection of
> `data/companies.json` found RSS configured for only **26/43** companies (`sec-edgar`: 43/43). Suggested
> option B would therefore mix valid quiet with missing source configuration. Spec 157 and AD-16 adopt the
> already-measured option A (`sec-edgar` 1.00, S 3) instead. No v11 snapshot or forward outcome was inspected.

**Measured 2026-07-28 against the live durable store, at the PINNED as-of instant — never the audit
execution time.**

- **As-of instant D:** `2026-07-28T08:04:27.7605621Z` — the common `WindowEndUtc` of the latest completed
  full-collection baseline run present when spec 158 was accepted, `PipelineRunRecord`
  **`120c99e2-2b8d-4831-99aa-1f02a0d58896`**.
- **Window:** exactly `(2026-05-29T08:04:27.7605621Z, D]` (60 days), known-at predicate
  `CreatedAtUtc <= D` (spec 136). One window; no sweeping.
- **Companies audited:** 43 (the full seeded watch universe).

**No forward outcome of any kind was computed, read or inspected** — no price, no attention after D, no
efficacy statistic, anywhere in the audit code or in this document (spec 158 §1; AD-16's pre-commitment is
not consumed). The audit is strictly read-only over the store: nothing under any data root was created,
modified or deleted.

How these numbers were produced: `scripts/audit-channel-feasibility.ps1` (read-only launcher, no scoring
math) → `src/Radar.ChannelFeasibilityAudit` (console; references Radar.Application + Radar.Infrastructure,
never Radar.Worker), which replicates `ScoringEngine.ScoreCompanyAsync`'s input assembly against the SAME
production components — the spec-142 durable `FileSignalStore`/`FileRawEvidenceStore` hydration (cross-run
dedupe included), the spec-136 window/known-at/Approved predicates, the evidence join and its
drop-on-unresolvable rule, `GuidanceChangeSupersede` (113), `MediaAttentionCollapse` (109), the spec-151
`ICollectorAttributionResolver` (inference ON for the legacy cohort; recorded always wins), and the shared
`ScoreSignalMath` / `ScoringChannelComposition` primitives, including the two prospective v11 primitives
this slice extracted (`DirectionalActivityMass`, `PositiveAttentionReach`). Weights are the code-default
`ScoringWeights` (the live baseline runs the code defaults), default `MediaCollapseOptions` (3-day window)
and `AttentionSourceTierOptions.Default`. The previous/velocity window is not read — no reported number
consumes velocity. Deterministic: same store + same declared as-of ⇒ same output (AD-3).

---

## 1. Eligibility funnel (global, all 43 companies)

| Stage | Signals |
|---|---|
| Approved, in-window, known-at D (after cross-run dedupe) | **17,616** |
| Dropped: **evidence-unresolvable** (before attribution — never relabelled unattributed) | **14,089** (80.0 %) |
| Resolved `ScoringSignal`s (before supersede) | **3,527** |
| After `GuidanceChangeSupersede` | 3,527 (no-op this window) |
| After `MediaAttentionCollapse` (the scored set) | **736** |
| Attribution over resolved inputs: **recorded** | **64** (8.7 %) |
| Attribution over resolved inputs: **inferred** (spec 151) | **672** (91.3 %) |
| Attribution over resolved inputs: **unattributed** | **0** |

The 80 % evidence-unresolvable drop is the known spec-142/145 legacy evidence-identity gap (healed forward
only, never backfilled — standing rule). The resolved 3,527 therefore skew toward recently collected
evidence. In-window signal types (file-level, pre-dedupe): MediaAttention 12,347; InsiderBuying 3,955;
ExecutiveHire 369; CapitalRaise 364; ProductLaunch 286; StrategicPartnership 221; CustomerWin 219;
GuidanceChange 105; GovernmentContract 19; **InstitutionalOwnership 0**.

**Spec-151 validated-vs-reasoned caveat, restated because 91.3 % of the resolved inputs are inferred:** the
legacy-attribution inference is ground-truth validated only for `newssearch` (337 recorded exemplars),
`sec-form4` (2) and `RssPressReleaseCollector` (2); the `sec-edgar`, `sec-13dg`, `usaspending` and GDELT
mappings are **reasoned, not ground-truth validated**. Every per-channel number below that rests on
inferred attribution inherits that caveat. Forward collection records the real collector (spec 146), so a
live arm accrues on recorded facts, not on this inference.

Per-company funnel (full table in the audit output; ranges): approved 70 (HWKN) – 1,685 (EOSE); resolved
scored set after collapse 4 (GTY) – 48 (AEHR, EOSE); unattributed 0 for every company.

## 2. Candidate collector channels — v11 structural inputs (over the 43 companies)

Directional activity mass is the v11 rule (`DirectionalMasses(...).Total`; Neutral excluded). "Score > 0"
means net-positive preponderance — `max(0, preponderance)` floors net-negative at 0, and the test is
saturation-independent, so no channel score is fabricated for collectors without a declared saturation.
Variances are population variances over all 43 companies (zeros included); distinct pairs counts distinct
`(directional mass, preponderance)` values.

| Collector | Companies with signals | With directional mass | **Score > 0 (net-positive)** | all-neutral | balanced | net-negative | Σ directional mass | Var(mass) | Var(prep) | Distinct pairs | Recorded sigs | Inferred sigs |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `sec-form4` | 32 | 19 | **1** | 13 | 0 | 18 | 62.0 | 5.31 | 0.0187 | 20 | 11 | 267 |
| `sec-13dg` | 0 | 0 | **0** | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 |
| `RssPressReleaseCollector` | 9 | 9 | **9** | 0 | 0 | 0 | 44.3 | 8.33 | 0.0128 | 10 | 1 | 30 |
| `newssearch` | 43 | 0 | **0** | 43 | 0 | 0 | 0 | 0 | 0 | 1 | 51 | 337 |
| `usaspending` | 0 | 0 | **0** | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 |
| `sec-edgar` | 28 | 14 | **13** | 14 | 0 | 1 | 56.7 | 4.81 | 0.0228 | 15 | 1 | 38 |
| `fda` | 0 | 0 | **0** | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 |

Net-positive companies, named:

- `sec-form4`: **ERII only** (mass 2.703, preponderance ≈ 0.0004 — marginal; its channel score rounds to
  integer 0 in every budget below). Re-derived per-company on the live window, as the spec required — and it
  confirms spec 153's earlier measurement (1 positive / 13 all-neutral / 18 net-negative of 32 active).
- `RssPressReleaseCollector`: AEHR, AGYS, AAPL, CVX, CYRX, EOSE, HRL, IMAX, V — all 9 active companies are
  net-positive (press releases are uniformly self-favourable; that is a property of the source, not a
  discovery about the companies).
- `sec-edgar`: AGYS, ATEX, AGX, DEA, EOSE, GTY, IMAX, LZB, HZO, OFG, SFBS, STRL, WDFC (13 companies; driven
  by the spec-119 AI directional earnings reads — GuidanceChange — plus keyword extraction on 8-K/10-Q/10-K;
  typically 1–3 resolved signals per company, so the channel is sparse but genuinely directional in both
  directions — 14 companies carry directional mass, of which 13 are net-positive and 1 net-negative).
- `sec-13dg` / `usaspending` / `fda` / `newssearch`: **zero net-positive companies.** `sec-13dg` is dark
  because **zero in-window Approved `InstitutionalOwnership` signals exist at all** (measured file-level,
  pre-dedupe — genuine quiet, not a resolution artifact; and spec 99 makes passive 13Gs Neutral by design,
  so even active windows contribute no direction under v11). `newssearch` is structurally all-neutral:
  `NewsArticle` evidence always becomes Neutral `MediaAttention` (spec 70).

## 3. §5 breadth answer — spec 157 §3-narrowed breadth is STRUCTURALLY ZERO

Measured with the exact prospective term (`ScoreSignalMath.PositiveAttentionReach`: positive-only filter on
both the post-collapse and pre-collapse inputs, then the unchanged third-party-publisher, tier-weight,
collapsed-publisher-credit and media-count terms):

- Companies with non-zero §3-narrowed breadth reach: **0 of 43**.
- Distinct third-party publishers carrying ≥ 1 Positive signal, per company: **0 for every company**;
  cross-company sum **0**; globally de-duplicated total **0**.
- Var(positive reach) = 0; one distinct reach value (0).

**A first-party RSS feed does NOT count as a publisher in the existing reach computation** —
`ScoreSignalMath.IsBreadthPublisher` admits only `EvidenceSourceTypes.IsThirdPartyAttentionSource` types
(`NewsArticle`, `SocialMedia`, `ConferenceMention`); `PressRelease` and `Filing` are first-party. So the
answer to §5's "small or zero" is **zero**, for the structural reason spec 158 predicted: every news
publisher's signals are Neutral `MediaAttention` (spec 70) and can never qualify under a positive-only
rule, and the sources that DO carry Positive signals (RSS press releases, filings) are not publishers.

**This is a finding about spec 157 §3 itself, reported as such and not worked around:** as normatively
written, §3 zeroes the breadth channel for every company — its 0.20 of the predeclared budget is dead
weight by construction, not merely quiet this window. Spec 157's pause note anticipated exactly this; §3
needs amendment (or the breadth channel dropped from the arm) before any v11 budget that pays for breadth
is declared.

Diagnostics that stay unfiltered, for contrast: full-set reach is non-zero for all 43 companies (range 1.55
V – 18.75 IMAX); `AttentionScore@D` 34–86; notedness discount 0.218 (CVX) – 0.836 (AGX). The
`AttentionScore` component (and AD-16's secondary comparator) is untouched by the §3 narrowing.

## 4. §6 — the predeclared `filings-led-v11` budget, evaluated exactly as written

Budget: insider `sec-form4` .50 (S 2) / institutional `sec-13dg` .30 (S 3) / breadth .20 (S 3), evaluated
in memory through the shared `ScoringChannelComposition.Compose` with the prospective v11 delegates
(directional-only activity, `max(0, preponderance)`, positive-only breadth), then the current-at-D
`AttentionScore` notedness discount, following-tier discount and normal `[0,100]` rounding. No strategy
configured, no snapshot persisted.

**Final stored-shape integer `OpportunityScore` distribution over the 43 companies:**

| Metric | Value |
|---|---|
| Companies with `OpportunityScore > 0` | **0 of 43** |
| Distinct integer scores | **1** (all 43 score exactly 0) |
| Largest tie-group | **43** |
| Cross-company variance | **0** |

The predeclared budget produces the **constant-zero ranking**. All three channel shares are dead:
insider — 42/43 companies floor at 0 (18 net-negative, 13 all-neutral, 10 inactive) and the single
net-positive (ERII, prep ≈ 0.0004) rounds to 0; institutional — zero in-window signals exist;
breadth — structurally zero (§3 above). AD-16's evaluator cannot rank a constant; running this arm would
accrue three weeks of unusable snapshots.

---

## 5. Design recommendation — NOT a measured finding

> **Everything above this line is measurement. This section is a design suggestion for the maintainer's
> decision; nothing here amends spec 157 §7 or AD-16 §7, configures an arm, or claims any ranking is
> "usable" — and no efficacy claim is made or possible from input-only data (AD-15 suspension untouched).**

The predeclared `filings-led-v11` / `filings-led-v10-control` budget should not go live as written: its
input distribution is a 43-way tie at zero (§4). Only two channels carry positive directional variation on
this window — `sec-edgar` (13/43 net-positive, both directions represented) and `RssPressReleaseCollector`
(9/43, uniformly positive) — and `newssearch` is excluded from inputs by constraint (under AD-16 it is the
outcome). Breadth is omitted: §5 measured zero positive mass, structurally.

**Smallest candidate input sets, both run through the same in-memory integer-`OpportunityScore`
distribution (spec 158 §6 requirement), inference-ON attribution:**

| Candidate | Channels (exact) | > 0 | Distinct ints | Largest tie | Variance |
|---|---|---|---|---|---|
| A (1 channel, 1 collector) | `filings` = `sec-edgar` 1.00, S 3 | 13 / 43 | 9 | 30 | 30.39 |
| **B (2 channels, 2 collectors) — suggested** | `filings` = `sec-edgar` 0.60, S 3; `press` = `RssPressReleaseCollector` 0.40, S 3 | **17 / 43** | **10** | **26** | 13.92 |
| C (2 channels, 2 collectors) | `press` = `RssPressReleaseCollector` 0.60, S 3; `insider` = `sec-form4` 0.40, S 2 | 7 / 43 | 6 | 36 | 7.05 |

**Suggestion: ONE matched pair on candidate B** — e.g. `filings-press-v11` (`radar-formula-v11`) and
`filings-press-v10-control` (`radar-formula-v10`), identical budgets, replacing (not joining) the
predeclared pair, keeping the arm count at two (AD-15). Candidate A is strictly smaller and remains the
fallback if the maintainer weighs minimality over coverage, but it leaves 30/43 companies indistinguishable
at 0 and hangs the entire ranking on single AI filing reads; B scores 17/43 above zero across two
independent input-side sources. Candidate C adds nothing over B (its insider half contributes ~0
everywhere).

Honest weaknesses of B, stated up front: 26/43 companies still tie at 0 (a real limit on rank resolution);
the RSS channel is active for only 9/43 companies this window (feed coverage vs genuine quiet was not
separated here) and press releases are uniformly self-favourable, so that channel measures *presence and
recency of positive self-reporting*, not third-party confirmation; and the per-channel inputs rest on
spec-151 **inferred** attribution whose `sec-edgar` mapping is reasoned, not ground-truth validated —
though a live arm accrues forward on recorded attribution. If the maintainer reads the 26-way zero tie as
disqualifying, the honest alternative is the spec-156 conclusion: **no budget is viable yet, and the next
step is the collector mix** (sources that produce directional, resolvable evidence for more of the
universe), not another formula.

Also for the maintainer's spec-157 amendment list, from §3 above: spec 157 §3's positive-only breadth rule
is structurally zero-valued for every company. Either amend §3 (its stated purpose — "neutral additions
must never increase Opportunity" — is already met by omitting the breadth channel entirely) or drop breadth
from any declared arm.

---

*Reproduce: `powershell -File scripts/audit-channel-feasibility.ps1 -DataRoot <store-root>` (read-only;
refuses an -OutFile inside the store). The pinned instant and run-record id are constants in
`src/Radar.ChannelFeasibilityAudit/ChannelFeasibilityAudit.cs`.*
