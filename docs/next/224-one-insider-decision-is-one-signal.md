# Task: one insider's decision is ONE signal, not one per filing — collapse repeated Form 4s the way media already collapses

## Overview

Radar mints an `InsiderActivity` signal from every Form 4 it collects. A discretionary sale carries
**Strength 8, confidence 0.60** — the same weight class as an AI directional earnings read. Those signals
feed the trajectory tug-of-war in `ScoreSignalMath.TrajectoryScore`:

```
Mpos = Σ (confidence · recency · strength) over positive signals
Mneg = Σ (confidence · recency · strength) over negative signals
Trajectory = 50 + TrajectoryBand · (Mpos − Mneg) / (Mpos + Mneg + k)
```

**Each filing adds its full weight to `Mneg` independently.** So one executive selling down a position over
several weeks does not contribute one bearish event — it contributes one per filing.

**Measured on 2026-09-12 over the live store (1,253 `sec-form4` evidence records, 94 companies with any):**

- **244 `discretionary-sale` filings**, and they concentrate in a way headcount does not explain:

  | ticker | distinct filers | sale filings | distinct sellers |
  |---|---|---|---|
  | ATNI | 9 | **13** | **3** |
  | MRCY | 5 | **14** | 5 |
  | FLXS | 9 | 10 | 5 |
  | IDT | 8 | 10 | 6 |
  | DGII | 11 | 7 | 5 |

- **Spearman(distinct reporting insiders, sale count) = +0.201** across 94 companies. More officers does
  mean somewhat more filings, but it explains little. The dominant driver is **repeat filing by the same
  person**: ATNI produced 13 negative signals from **three** people, 4.3 filings each.
- The worked case: OOMA. `STANG ERIC B` filed open-market sales on 2026-06-26 (27,666 sh) and 2026-07-13
  (23,212 sh); `Yeh Jenny C` on 2026-07-07 (12,840 sh) and 2026-07-09 (2,481 sh); `Mann Russell` once. Five
  signals, three decisions, five weeks.

**This is a counting defect, not a weighting defect**, and that distinction decides the fix. Lowering the
Strength-8 tier would make a single genuine sale under-weighted while five repeats still outvote everything
else in `Mneg`. The error is that one decision is counted N times.

**Radar already solved this shape once.** `MediaAttentionCollapse` (`media-collapse-v2`,
`src/Radar.Application/Scoring/MediaAttentionCollapse.cs`) buckets same-event news coverage and scores ONE
representative, which is why report lines read `(collapsed 22 same-event media items)`. There is no
equivalent for insider filings. This spec is that equivalent.

## Assignment

Worktree: any. Dependencies: main at `37af962` or later. No overlap with any queued spec.

Use `run-next.ps1 -Spec 224`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. `InsiderActivityCollapse` (`insider-collapse-v1`)

A new Application type in `Radar.Application.Scoring`, built to the same contract as
`MediaAttentionCollapse` — read that file first and follow its structure rather than inventing a second
shape:

- **Bucket key: `(CompanyId, insider identity, direction)`.** Two different people selling in the same week
  are two decisions and stay two signals. The same person selling three times is one.
- **Bucket boundaries formed greedily against the EARLIEST signal of each bucket**, never against the chosen
  representative — `MediaAttentionCollapse` documents why that separation is load-bearing, and the same
  reasoning applies verbatim: measuring the window from a later member would let the representative choice
  silently widen the bucket.
- **Window magnitude lives in config** (`InsiderCollapseOptions`, parallel to `MediaCollapseOptions`), so it
  is tunable without a code change and is hashed into the fingerprint **by value**.
- **The representative carries the AGGREGATE value.** `insiderNetValue` is summed across the bucket, so a
  person selling $500k four times is one signal at $2m, and the existing materiality tiers
  (`InsiderMaterialityWeights`) then read a true position size rather than four partial ones. This is the
  part that must not be lost: collapsing without aggregating would UNDER-count a large disposal.
- **Every collapsed member is counted and surfaced**, exactly as media does: the surviving signal names how
  many filings it represents and their total value, and the count reaches the report line.

**Insider identity** comes from the Form 4's reporting-owner field. The store currently carries the name only
inside the evidence `title`; if the collector does not persist a structured owner field, add one — do NOT
parse the title in the scorer. A filing whose owner cannot be resolved is **never bucketed** (it stays its own
signal) and is counted on a named axis.

## 2. What this does NOT change

- **No formula version bump.** The math in `RadarScoreFormulaV8` and every other formula is untouched; only
  the insider input set changes. `MediaAttentionCollapse`'s own header states this precedent explicitly.
- **No change to Strength, confidence, or the materiality tiers.** Whether Strength 8 is right is a separate
  question (§5), and answering it before the counting is fixed would answer it on contaminated data.
- **`plan-10b5-1` and `no-discretionary-transactions` classifications are untouched.** They carry no
  direction today and still won't.
- **No backfill.** Accrued signals stay exactly as written (AD-8). The collapse applies at scoring time to
  the signals in the window, so history heals forward only.

## 3. Fingerprint and identity

`insider-collapse-v1` and its window magnitude are folded into `ScoringConfigVersion` via
`CanonicalDescriptor`, the same way `media-collapse-v2` is. **This moves every pin and owes ONE operator step**
— delete/re-record `data/scoring-configs/strategies/{name}.json`, then verify the first run's stamp against
`ScoringConfigFingerprintTests`. Say so in the PR body; do not let it be discovered by a halted run.

## 4. Live distribution (required, not optional)

The PR body must carry, from a real run over the live universe:

- filings collapsed, buckets formed, and the distribution of bucket sizes (how many buckets hold 1, 2, 3, 4+
  filings) — if almost every bucket holds one filing, this spec changed nothing and should be said so plainly;
- the count of filings whose owner could not be resolved, and therefore were not bucketed;
- **before/after `Mneg` for the named companies above** — ATNI, MRCY, FLXS, IDT, DGII, OOMA — since those are
  the cases the diagnosis rests on. ATNI's negative mass should fall from ~13 units to ~3;
- **before/after trajectory and opportunity across the whole universe**: median, and the count of companies
  whose Opportunity moves by more than 2 points. A change that moves nothing is a finding; a change that
  moves everything needs explaining.

Expect the effect to be **uneven and largest on quiet companies**, where insider filings are most of the
mass. OOMA is the counter-example and should be stated as such: its trajectory is 76 because positive mass
dominates, so collapsing five sales there should barely move it.

## 5. Deliberately deferred, with its entry condition

Whether Strength 8 is the right weight for a discretionary sale, and whether `plan-10b5-1` should carry a
direction at all, are **not** decided here. The current evidence (`scripts/audit-insider-forward-returns.ps1`,
250 events, 21-session forward windows, excess of the universe mean):

```
discretionary-buy    n= 29   median excess +0.62%   24% negative
discretionary-sale   n=137   median excess -0.52%   55% negative
plan-10b5-1          n= 84   median excess -3.77%   68% negative
```

That inversion — the classification Radar ignores outperforming the one it weights most heavily — is
interesting but rests on ~3 months, overlapping (non-independent) forward windows and **no confidence
intervals**. Re-run that audit after this collapse lands and after bootstrap intervals exist. If the
inversion survives both, it earns its own spec; the tiers are config, so the fix would be a profile edit.

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. `InsiderActivityCollapse` mirrors `MediaAttentionCollapse`'s bucketing contract; no second copy of the
   greedy-bucketing primitive — extract and share if the logic is genuinely common.
3. Aggregate value is summed into the representative; a test proves four $500k sales produce one $2m signal
   and not one $500k signal.
4. Every collapsed member is counted and reaches the report line.
5. An unresolvable owner is never bucketed and is counted on its own axis.
6. §4's numbers are in the PR body from a real run, not a fixture.
7. The pin move and its single operator step are stated in the PR body.
