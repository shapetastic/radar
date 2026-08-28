# Task: Attention measures aggregator coverage, not notice — invert the default and classify the volume

## Overview

`OpportunityScore` applies attention as an inverse discount, so a company Radar believes is already noticed
is marked down. The intent is right: Radar exists to surface companies **before** they are noticed. The
measurement is not. Attention is built from third-party publisher breadth, and the publisher tier map that
decides what counts as "genuine market notice" classifies almost none of the actual traffic.

**Measurement basis, stated because the first draft got it wrong.** Population = news observations whose
**`publishedAtUtc`** falls in the 60 days preceding the PINNED as-of instant
**`2026-08-27T21:42:45.4943606Z`** (the last completed baseline's `windowEndUtc`) **and** whose company is
in the **current 74-company universe** (not file mtime, not "now", not every historical score directory).
The instant is pinned because the boundary moves: re-measuring later aged seven 2026-06-29 observations out
and produced 2,858, which is the same corpus read at a different moment, not a different result. Tier resolution uses the **production
`ConfiguredAttentionSourceWeights.Normalize`** — lowercase, strip one trailing TLD from a closed set, remove
non-alphanumerics — not whole-string comparison.

| tier | weight | observations | share |
| --- | ---: | ---: | ---: |
| `Genuine` | 1.0 | **15** | **0.5 %** |
| `Mill` | 0.1 | 1,415 | 49.4 % |
| **unclassified → `UnknownWeight`** | **0.25** | **1,435** | **50.1 %** |

**2,865** observations in window at the pinned instant; ~300 distinct unclassified publishers.
Independently reproduced by an external reviewer.

⚠ **A first draft of this spec published 43.9 / 0.5 / 55.6 % over 3,194 observations across 353 publishers.
Those figures were wrong**: they used file mtime rather than `publishedAtUtc` (admitting 329 observations
published outside the window, some back to 2010), counted a stale 75th score directory, and simulated the
matcher with whole-string comparison instead of calling it. The corrected numbers are above. The diagnosis
is unchanged — half the volume is unclassified and genuine notice is 0.5 % — but the error is recorded
because it is exactly the failure the new "no measure ships without its live distribution" rule exists to
prevent, committed in the same change as this spec.

**Consequence in the output** (75 score directories scanned; the live universe is 74): attention has a mean
of **73.4** with **53 of 75 between 70 and 89**, so it is close to a uniform tax rather than a
discriminator, while `OpportunityScore` is compressed to a mean of **19.2** and a maximum of **38**. §7
re-measures both properly.

**The worked example that prompted this.** DGII (Digi International) scores attention **75**. Of its 45
in-window observations, 38 are from algorithmic aggregators, and its single largest source is **Yahoo
Finance (10)** — unclassified, therefore weighted **0.25, two and a half times a Mill publisher**. The
maintainer, an engaged investor, had never heard of the company.

⚠ A first draft claimed DGII had "no editorial coverage at all". That overstates: the set includes Seeking
Alpha and at least one analytical piece, and Yahoo Finance carries a mixture of press releases, syndicated
analysis and original material. The defensible claim is narrower and sufficient: **the dominant sources are
platforms that publish on every listed ticker regardless of newsworthiness, so breadth across them is not
evidence of selection.**

**The distinction that matters:** Radar is trying to get ahead of *investors*, but the proxy measures
*database coverage*. For a small-cap universe those come apart completely.

## Assignment

Worktree: any

Dependencies: specs 187–195 merged. Every existing evidence, observation, signal, snapshot and efficacy
artifact stays immutable.

Estimated time: ~1–1.5 days.

## 1. Invert the default: unknown is not notice

Today `UnknownWeight = 0.25` and an explicit entry is required to be *discounted*. That is the wrong way
round for this universe: genuine outlets are a short, enumerable list, while content mills and syndication
are an unbounded long tail. Requiring enumeration of the tail is unwinnable — ~300 unclassified
publishers and counting.

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

## 2. Define the tier policy BEFORE assigning publishers

The first draft assigned Seeking Alpha 0.1 and The Motley Fool 1.0 — a tenfold difference between broadly
comparable investor-content platforms — with no stated principle. That is the same unprincipled curation
this spec exists to fix. Define the tiers first, then classify against them.

**Tier definitions (the policy, which is what gets reviewed):**

- **`Wire` (0.05)** — paid or company-originated distribution. Confers visibility, **not independent
  notice**, because the company controls whether the item exists at all. That is the whole rationale; do
  **not** justify it by assuming the item was also captured via RSS, which is not always true.
- **`Mill` (0.1)** — automated, templated or republished material with no demonstrated independent
  selection. The test is *selection*: does this outlet decide which companies to cover, or does it publish
  on every ticker by construction?
- **`Platform` (0.3)** — investor-content platforms carrying a mixture of contributor analysis and
  syndication, where a human chose to write about *this* company but the outlet exercises little editorial
  gatekeeping. **Seeking Alpha and The Motley Fool both belong here**, which is precisely why the first
  draft's tenfold split was wrong. **The weight is 0.3 and is decided HERE, not by the implementer**: it
  moves scores and all six fingerprints, so it is a spec decision. Three times `Mill`, because a human
  chose this company; materially below a professionally gated newsroom, because almost nothing was
  gatekept. **The audit determines publisher MEMBERSHIP, never the tier's weight.**
- **`Genuine` (1.0)** — independent reporting or editorial selection.

**Classify by sampled audit, not by reputation.** No publisher is hard-coded on the strength of its
reputation; each is classified from what it actually published in this corpus.

**Sampling rule, fully reproducible:** for each publisher take the **most-recent in-corpus item per
company**, ordered by `PublishedAtUtc` then `ObservationId`, up to **ten companies**. Same corpus, same
sample, every time.

**Cover at minimum:** Yahoo Finance (478 — 16.7 % of the corpus on its own), Seeking Alpha (64), Quiver
Quantitative (51), Sahm (31), vinanet.vn (31), Kalkine Media (27), The Globe and Mail (19), Revelio Labs
(17), TradingKey (17), plus **MarketWatch, Morningstar and the Business Journals**, which the reviewer
correctly identified as meaningful unknown-tail sources.

**The COMMITTED audit table is the authority** (beside the tier options, or under `docs/cohorts/`): each
sampled item and its resulting tier is recorded there, so the assignment is auditable rather than asserted.
It is the justification for a hashed scoring input, so it must live in the repository — the PR **links** to
it and does not restate it, because a PR body is not a durable record.

Wires to classify from the measured tail: **PR Newswire (41), GlobeNewswire (31), Business Wire (29)**.

**Matching: one real fix, one retraction.**

- ⚠ **RETRACTED:** the first draft claimed `marketscreener.com` does not match the listed `MarketScreener`.
  **It does.** `Normalize` already strips the trailing TLD, and
  `ConfiguredAttentionSourceWeightsTests` already pins this case. No change is needed and none should be
  made.
- **REAL:** `Investing.com Nigeria` normalizes to `investingcomnigeria`, which does not match
  `investingcom`. Add an explicit alias (or a documented prefix rule) and pin it by test. Prefer an alias
  list over broadening `Normalize`, so a change here cannot silently collapse unrelated outlets.

## 3. A typed resolver — the diagnostic is otherwise unimplementable

**This is a structural consequence of §1 that the first draft missed.** Once `UnknownWeight` becomes 0.1,
an explicitly-classified `Mill` publisher and an unclassified one **return the same number**.
`IAttentionSourceWeights.WeightFor` returns only a `double`, so the §3 diagnostic literally cannot tell them
apart — and an implementer would either duplicate the matching rules (they will drift) or silently report
every Mill publisher as unclassified (the diagnostic then lies about the very thing it exists to expose).

Add **one authoritative resolver** on `IAttentionSourceWeights`, returning a typed result carrying at least:

- `TierName` — the matched tier, or the unclassified sentinel;
- `Weight`;
- `IsExplicitlyMapped` — the bit that survives two tiers sharing a weight; and
- `NormalizedPublisher` — the key actually matched on, so a curator can see why something missed.

**Fail fast on an ambiguous publisher.** The current resolver silently takes ordinal last-wins when one
normalized publisher appears in two tiers, so both the score and the diagnostic would depend on tier-NAME
ordering — an invisible dependency that a rename could flip. With named tiers this must **throw at
construction**, naming the publisher and both tiers. A publisher belongs to exactly one tier; "cannot tell"
must never resolve to "whichever sorted last".

`WeightFor` becomes a thin projection of that resolver, and the diagnostic consumes the same call. **One
matching implementation, two consumers** — the CLAUDE.md reuse rule, and here it is load-bearing rather
than tidy: a second copy would make the diagnostic disagree with the score it is describing.

**Persistence seam, chosen — and it must NOT bump the batch schema version.** Record the per-run tier
summary on the **news-observation batch record**: it is already the per-run home of collection-coverage
provenance and is written once per run.

⚠ **A draft of this spec said "bump that record's schema version". That is wrong and would break a shared
field.** `NewsObservationBatch.SchemaVersion` is stamped with `NewsObservationRecord.CurrentSchemaVersion`
(`CollectionPass` line ~479) — the SAME const every individual observation carries. Bumping it would either
churn every new observation record to v2 for no reason, or give one field two meanings. The contract is:

- add **one trailing nullable `AttentionPublisherCoverageSummary`** to the batch record;
- give that **nested** summary **its own** version token, `attention-publisher-coverage-v1`;
- leave `NewsObservationBatch.SchemaVersion` **unchanged**; and
- a pre-196 batch hydrates the summary as **null = not recorded, never zero** (the standing rule).

**Population, defined explicitly because the field sits on a collection batch:** every observation candidate
**attempted** by that batch — written, cross-run deduped and failed alike — so the tier counts **sum to
`ObservationsAttempted`**. Assert that sum in test.

⚠ **Label it a CAPTURE-FLOW diagnostic, not the input to `AttentionScore`.** The two populations genuinely
differ: this counts attempted candidates in one collection pass, whereas scoring consumes tier-weighted
**distinct publishers per company** over the scoring window. The summary is for curating the publisher map;
it must not be read, or described, as the attention input. §7 measures the scoring unit separately.

Emit the same summary as one aggregated log line per run — the spec-145 precedent, never one line per
publisher. Report: observations and share per tier including unclassified, and the **top 10** unclassified
publishers by volume. **Do not auto-classify from it** — the tier map stays curated policy (AD-5); this only makes the
gap legible so the next drift is a number someone sees rather than something discovered by asking why a
familiar company scored 75.

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
- **Normalization and alias cases** are pinned (note: `marketscreener.com` was never a failure — it already
  resolves via TLD stripping, and this test guards that it continues to): `marketscreener.com` resolves by
  normalization, `Investing.com Nigeria` resolves by its new alias, and a genuinely unrelated publisher does
  not collide with a classified family.
- `CanonicalDescriptor()` is deterministic under reordering and culture, and the recomputed pins match.
- The diagnostic reports tier shares that sum to the observation count, and names unclassified publishers by
  descending volume.
- A regression using the measured DGII publisher set: with the shipped table its attention falls materially
  versus the pre-196 value. Assert the **direction and the mechanism**, not a magic number.

## 7. Prove the fix with a PAIRED COUNTERFACTUAL, not consecutive runs

Per CLAUDE.md's **"no measure ships without its live distribution"** rule, this spec must report what the
corrected measure actually produces. But comparing two nightly runs is **confounded**: the tier policy and
the underlying evidence both change, so the delta shows their sum, not the policy's effect.

Require instead a **read-only paired counterfactual**:

- ONE fixed as-of instant;
- the SAME evidence and signal inputs on both sides;
- the current **74-company** universe (not 75, not every historical score directory);
- the **primary `default`** strategy only;
- **only the publisher-tier policy differs** between the two arms; and
- **nothing is persisted** — no snapshot, no run record, no identity record. This mirrors the spec-139
  replay discipline and the spec-183 affine-invariance proof; it is not a new mechanism.

Report, old policy versus new, from that single paired run. **Two populations, because raw article volume
is NOT the scoring unit** — `AttentionReach` weights **distinct third-party publishers per company**, so a
publisher appearing forty times counts once, and observation-level coverage answers a curation question
rather than a scoring one:

1. **Raw observation coverage** — observations and share per tier, plus unclassified publishers remaining.
   This is for publisher-map maintenance.
2. **Distinct publisher/company breadth actually consumed by `AttentionReach`** — the tier-weighted
   contributions per company, counting **both surviving and collapsed-only publishers** (a publisher
   appearing only inside media-collapsed items still contributes breadth, and omitting it would understate
   the change). **This is the population the score actually sees.**
3. The `AttentionScore` distribution across the 74 companies: min, max, mean, decade histogram.
4. The `OpportunityScore` distribution, same statistics.

(1) explains what to curate next; (2) explains why the scores moved; (3) and (4) show the effect.

The **first real post-merge baseline** is reported **separately**, and is not the evidence for the policy.

**The success criterion is discrimination, not direction.** Attention must stop being a near-uniform tax:
the spread widens and the mass stops clustering in one decade. Pre-196 reference from the score store
(75 directories, mean 73.4, 53 of 75 in 70–89; Opportunity mean 19.2, max 38) is indicative only — the
counterfactual's own old-policy arm is the controlled baseline. **If attention is still near-constant
afterwards, say so plainly**: that is a real result and becomes the evidence for re-tuning the discount
weights in a follow-up, which §5 deliberately keeps out of scope. Do not tune weights to manufacture a
better-looking spread.

## Acceptance criteria

- [ ] An unclassified publisher is treated as low-signal (0.1 default), non-zero, and configurable; explicit
      classification is required to count as genuine notice.
- [ ] The four tiers are DEFINED by principle (`Wire` company-originated / `Mill` no independent selection /
      `Platform` contributor analysis with weak gatekeeping / `Genuine` editorial selection) before any
      publisher is assigned, and Seeking Alpha and The Motley Fool land in the SAME tier.
- [ ] Every high-volume publisher hard-coded in this slice is backed by a sampled audit in the COMMITTED
      audit table (the PR links to it, never restates it), including Yahoo Finance, MarketWatch, Morningstar
      and the Business Journals. Sampling is the most-recent item per company by `PublishedAtUtc` then
      `ObservationId`, up to ten companies.
- [ ] `marketscreener.com` is NOT "fixed" — it already resolves. `Investing.com Nigeria` gains an explicit
      alias, pinned by test, without broadening `Normalize`.
- [ ] One typed resolver carries `TierName`, `Weight`, `IsExplicitlyMapped` and `NormalizedPublisher`;
      `WeightFor` and the diagnostic both consume it, so an explicit `Mill` is distinguishable from an
      unclassified publisher despite sharing a weight.
- [ ] The per-run tier summary is a trailing nullable `AttentionPublisherCoverageSummary` on the batch
      record carrying its OWN `attention-publisher-coverage-v1` token; `NewsObservationBatch.SchemaVersion`
      is UNCHANGED (it is shared with every observation record); its tier counts sum to
      `ObservationsAttempted`, asserted; null = not recorded on pre-196 batches; and it is labelled a
      capture-flow diagnostic, never the `AttentionScore` input.
- [ ] `Platform = 0.3` ships as declared here, with Seeking Alpha and The Motley Fool in it; the sampled
      audit (most-recent item per company by `PublishedAtUtc` then `ObservationId`, up to ten companies) is
      COMMITTED to the repo as the authority and determines membership only, never a tier weight.
- [ ] An ambiguous publisher — one normalized key in two tiers — throws at construction naming both tiers,
      rather than resolving by ordinal last-wins.
- [ ] The counterfactual reports BOTH raw observation coverage AND the distinct publisher/company breadth
      `AttentionReach` actually consumes, including collapsed-only publishers.
- [ ] The fix is proven by a read-only PAIRED COUNTERFACTUAL at one as-of instant over the 74-company
      universe and the `default` strategy, varying ONLY the tier policy and persisting nothing; the first
      post-merge baseline is reported separately.
- [ ] Attention's spread widens and stops clustering in one decade — or the PR states plainly that it did
      not, as evidence for a separate weight-tuning slice. Weights are NOT tuned here.
- [ ] All six pins are recomputed and updated, the lineage note names this as the third identity move, and
      the operator step for the gitignored identity records is stated.
- [ ] Discount weights, `FollowingTier`, collection and history are untouched; no strategy, formula, signal
      type or label changes.
- [ ] `dotnet build Radar.sln -c Release` and the full suite pass; `git diff --check` clean; on Windows
      `run-radar.ps1 -Profile default -WhatIf` still resolves.
