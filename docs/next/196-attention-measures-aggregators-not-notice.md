# Task: Attention measures aggregator coverage, not notice — invert the default and classify the volume

## Overview

`OpportunityScore` applies attention as an inverse discount, so a company Radar believes is already noticed
is marked down. The intent is right: Radar exists to surface companies **before** they are noticed. The
measurement is not. Attention is built from third-party publisher breadth, and the publisher tier map that
decides what counts as "genuine market notice" classifies almost none of the actual traffic.

**Measured over the 3,194 news observations of the last 60 days (368 distinct publishers):**

| tier | weight | observations | share |
| --- | ---: | ---: | ---: |
| `Mill` | 0.1 | 1,403 | 43.9 % |
| `Genuine` | 1.0 | **15** | **0.5 %** |
| **unclassified → `UnknownWeight`** | **0.25** | **1,776** | **55.6 %** |

The unknown default is carrying the majority of the signal, across **353 unclassified publishers**. The
consequence is visible in the output: attention has a mean of **73.4** across 75 companies with **53 of 75
between 70 and 89**, so it is close to a uniform tax rather than a discriminator, while `OpportunityScore`
is compressed to a mean of **19.2** with a maximum of **38** — no company in the universe can reach even
40 % of the nominal range.

**The worked example that prompted this.** DGII (Digi International) scores attention **75**. Its 45
observations are 38 from algorithmic aggregators; there is no editorial coverage at all. Its single largest
source is **Yahoo Finance (10)**, which is unclassified and therefore weighted **0.25 — two and a half times
a Mill publisher** — despite being an automated republisher of press releases for every listed ticker. The
maintainer, an engaged investor, had never heard of the company. Radar believed it was 75 % noticed.

**The distinction that matters:** Radar is trying to get ahead of *investors*, but the proxy measures
*database coverage*. For a small-cap universe those come apart completely — a company can appear in every
aggregator on earth and remain genuinely unknown.

## Assignment

Worktree: any

Dependencies: specs 187–195 merged. Every existing evidence, observation, signal, snapshot and efficacy
artifact stays immutable.

Estimated time: ~1–1.5 days.

## 1. Invert the default: unknown is not notice

Today `UnknownWeight = 0.25` and an explicit entry is required to be *discounted*. That is the wrong way
round for this universe: genuine outlets are a short, enumerable list, while content mills and syndication
are an unbounded long tail. Requiring enumeration of the tail is unwinnable — 353 publishers and counting.

Change the contract so an explicit entry is required to **count as notice**:

- `UnknownWeight` default becomes **0.1** (the Mill weight): an unrecognised publisher is treated as
  low-signal coverage, not as quarter-strength genuine notice.
- It stays **non-zero, deliberately.** The existing doc comment's reason holds — "real coverage is never
  silently zeroed" — and a zero would make an unclassified genuine outlet invisible rather than merely
  quiet. 0.1 keeps it present and discountable.
- The value stays configurable, so the inversion is a declared default rather than a hard-coded belief.

This changes Attention output, and `IAttentionSourceWeights.CanonicalDescriptor()` is already folded into
`ScoringConfigVersion` (AD-10), so **the fingerprints move**. That is correct and intended — two runs with
different tier maps must not be judged comparable. See §4.

## 2. Classify the measured volume, not a guess

Extend the shipped table using the **measured** distribution above, not intuition. At minimum, from the top
of the live traffic:

- **To `Mill` (0.1):** Yahoo Finance (596 — the single largest source in the corpus), Seeking Alpha (77),
  Quiver Quantitative (60), Kalkine Media (27), TradingKey (17), Revelio Labs (18), Sahm (31), vinanet.vn
  (31), Pluang (19).
- **A new `Wire` tier (weight 0.05, below Mill):** PR Newswire (61), Business Wire (49), GlobeNewswire (42).
  A press wire is **the company's own announcement redistributed** — it is not third-party notice at all,
  and it is currently weighted 0.25 as though it were. Wire traffic is already captured as
  first-party evidence by the RSS press-release collector; counting it again as market attention is
  double-counting the company talking about itself.
- **To `Genuine` (1.0):** The Globe and Mail (19), The Motley Fool (21) — plus a review of the remaining
  tail for real outlets. Keep this list short and defensible; when in doubt leave it unclassified, which
  now means 0.1 rather than 0.25.

**Fix the matching, which is silently leaking.** Entries are compared as whole strings, so
`marketscreener.com` (47 observations) does **not** match the listed `MarketScreener`, and
`Investing.com Nigeria` (13) does not match `Investing.com`. Both currently fall to the unknown default
despite their family being classified. Add normalization — trim, casefold, strip a leading `www.` and a
trailing TLD — or an explicit alias list. Whichever is chosen, add a test that pins the specific
`marketscreener.com` and `Investing.com Nigeria` cases, because they are the measured instances.

## 3. Make the unclassified tail visible so it can be curated from evidence

The reason this drifted is that nothing reported it. Add a per-run aggregated diagnostic (the spec-145
one-line-per-cohort precedent, not one line per publisher):

- the observation counts and share falling to each tier, including unknown; and
- the top N unclassified publishers by volume for that run.

Emit it in the run log and record it on the news-observation batch or the attention artifact — wherever it
sits beside existing coverage provenance. This makes the next miscalibration a number a maintainer sees
rather than something discovered by asking why a familiar-looking company scored 75.

Do not auto-classify from it. The tier map stays curated policy (AD-5); this only makes the gap legible.

## 4. Fingerprints, lineage and the operator step

The tier map is a hashed input, so all six pins move. Recompute the 30/60/120-day AI-off/AI-on values,
update `ScoringConfigFingerprintTests`, the `scripts/run-profiles/default.json` operator record and a
`CLAUDE.md` lineage note beside the spec-194 entry. State plainly that this is the **third** identity move
in as many weeks and that the attention regime before it is not comparable with the one after.

**Operator action, which cannot ride in the PR:** `data/scoring-configs/` is gitignored, so every
`data/scoring-configs/strategies/{name}.json` must be deleted after merge and before the next baseline, or
`StrategyIdentityGuard` halts the run before collection. That halt is correct and must not be bypassed.

## 5. What this does NOT do

Recorded so the next reader does not assume more was decided than was:

- **It does not re-tune the discount weights.** `OpportunityAttentionDivisor`,
  `OpportunityAttentionDiscountWeight` and `FollowingTierDiscountWeight` are untouched. The hypothesis here
  is that attention was measuring the *wrong thing*, not that the discount was too strong. Fixing the input
  first means any later weight tuning is judged against a signal that means something. If attention still
  looks like a near-uniform tax after this lands, THAT is the evidence for re-tuning — and it is a separate
  spec with its own measurement.
- **It does not touch `FollowingTier`**, the curated per-company notedness field, which is a different and
  better-grounded measure and remains as-is.
- **It does not change collection.** No feed, cap, collector or admission rule moves; no evidence or
  observation is added, removed or re-mapped.
- **It does not rewrite history.** Accrued snapshots keep their old attention values; the discontinuity is
  taken and noted, per the spec-148 precedent.
- No new strategy, arm, formula class, signal type, label or Lead change.

## 6. Tests

- The inverted default: an unrecognised publisher weights 0.1, not 0.25; the value is still configurable
  and a configured override wins.
- Each newly classified publisher resolves to its intended tier, `Wire` included, with the wire tier
  strictly below `Mill`.
- The measured alias failures are pinned: `marketscreener.com` and `Investing.com Nigeria` resolve to
  `Mill`, and a genuinely unrelated publisher does not collide with a classified family.
- `CanonicalDescriptor()` is deterministic under reordering and culture, and the recomputed pins match.
- The diagnostic reports tier shares that sum to the observation count, and names unclassified publishers by
  descending volume.
- A regression using the measured DGII publisher set: with the shipped table its attention falls materially
  versus the pre-196 value. Assert the **direction and the mechanism**, not a magic number.

## 7. Report the live distribution — this spec must prove its own fix

Per CLAUDE.md's **"no measure ships without its live distribution"** rule (added with this spec), the
implementation is not done until it reports what the corrected measure actually produces across the live
universe. This is the check that would have caught the defect years earlier, so it is a deliverable, not a
courtesy.

Record in the PR body, measured against the live store — not a fixture:

- **tier coverage before and after**: observations and share resolving to `Genuine` / `Mill` / `Wire` /
  unclassified. The pre-196 baseline is 43.9 % Mill, 0.5 % Genuine, **55.6 % unclassified across 353
  publishers** over 3,194 observations. State the post-196 figures beside it.
- **the `AttentionScore` distribution across all 75 companies**, before and after — minimum, maximum, mean
  and a decade histogram. The pre-196 baseline is min 0, max 95, **mean 73.4, with 53 of 75 between 70 and
  89**.
- **the `OpportunityScore` distribution**, before and after, same statistics. Pre-196: min 3, max 38,
  **mean 19.2**.
- the top unclassified publishers that remain, by volume.

**The success criterion is discrimination, not direction.** Attention should stop being a near-uniform tax:
the spread must widen and the mass must stop clustering in one decade. If after this change attention is
*still* near-constant, say so plainly in the PR — that is a real result and it becomes the evidence for
re-tuning the discount weights in a follow-up, which §5 deliberately keeps out of scope. Do not tune weights
to manufacture a better-looking spread.

## Acceptance criteria

- [ ] An unclassified publisher is treated as low-signal (0.1 default), non-zero, and configurable; explicit
      classification is required to count as genuine notice.
- [ ] Yahoo Finance and the other measured aggregators are classified; press wires sit in their own tier
      below Mill; publisher-name variants no longer leak to the default.
- [ ] Every run reports its tier shares and top unclassified publishers, aggregated, so the gap stays
      visible.
- [ ] All six pins are recomputed and updated, the lineage note names this as the third identity move, and
      the operator step for the gitignored identity records is stated.
- [ ] Discount weights, `FollowingTier`, collection and history are untouched; no strategy, formula, signal
      type or label changes.
- [ ] `dotnet build Radar.sln -c Release` and the full suite pass; `git diff --check` clean; on Windows
      `run-radar.ps1 -Profile default -WhatIf` still resolves.
