# Task: EvidenceConfidence takes 12 distinct values across 102 companies and removes 45% of every score — measure whether it discriminates anything

## Overview

`Opportunity` is the number the report ranks on, and it is a product of three terms:

```
Opportunity = Trajectory · (EvidenceConfidence / 100) · clamp(followingDiscount, floor, 1)
```

**Measured on 2026-09-12, latest snapshot per company, `default` series, n = 102:**

| component | median | range |
|---|---|---|
| Trajectory | 58.0 | 21 – 85 |
| **EvidenceConfidence** | **55.0** | 34 – 89 |
| Attention | 64.0 | 19 – 92 |
| **Opportunity** | **20.0** | **3 – 44** |

Decomposed for the median company:

```
trajectory 58.0  ×  evidenceConfidence 0.55  =  31.9
31.9             ×  attention discount 0.63  =  20.0   ← observed median Opportunity
```

**Roughly two-thirds of every score is removed before anything company-specific happens**, and Trajectory's
healthy 21–85 spread arrives at the report compressed into 3–44.

**The attention half of that is deliberate** — the notedness discount IS the product thesis, and
`AttentionScore` genuinely discriminates (19–92, sd 13.4), so it is explicitly **not** in scope here.

**The EvidenceConfidence half has never been examined, and it takes only 12 distinct values across 102
companies.** From `ScoreSignalMath.EvidenceConfidenceScore`:

```csharp
bestConf       = signals.Max(s => s.Signal.Confidence);              // 0..1
bestQualWeight = signals.Max(s => QualityWeight(weights, s.Evidence.Quality));
divFactor      = Math.Min(1, distinctSourceTypes / weights.DiversityTarget);
return Clamp0To100(100 * bestConf
     * (EcQualityBase + EcQualitySpan * bestQualWeight)
     * (EcDiversityBase + EcDiversitySpan * divFactor));
```

Three terms, each **max-anchored or small-integer-derived**, multiplied. That is the mechanical reason for 12
distinct outputs: a company holding any SEC filing anchors `bestConf` at 0.95, evidence `Quality` is a
three-valued enum, and `distinctSourceTypes` is a small integer over a handful of collectors. Nearly every
company in a 102-company universe saturates the same way.

The suspicion this spec exists to test: **EvidenceConfidence is not measuring confidence, it is applying a
near-flat ~45% tax.** A multiplier that is the same for everyone changes no ranking — it only compresses the
scale, which then interacts with the Lead arm's fixed label lines. On 2026-09-11 the top name on
`disclosure-led-v11` scored 19 against an Investigate line of 20, and nothing was put forward at all.

This is the same failure class CLAUDE.md already records twice — `MediaAttention` at 98.4% Neutral,
`AttentionScore` at a near-constant 73.4 — where a component is provably correct and discriminates nothing.
Both shipped green and were found only by looking at the live distribution.

## Assignment

Worktree: any. Dependencies: main at `486aacc` or later. (⚠ AMENDED 2026-09-13: this line originally read
`37af962`. Spec 224 has since merged (`06eb740`) and its operator step is taken, so this slice now measures a
post-224 store. That barely matters here: 224 changed the insider input set, not this multiplier, and moved
median Opportunity only 20 → 20.5. The Overview's figures were measured on 2026-09-12, before 224, so re-derive
them rather than quoting them as current.)

Use `run-next.ps1 -Spec 225`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. This slice MEASURES. It changes no score.

No formula change, no weight change, no new component, no fingerprint input, **no operator step**. The
deliverable is a durable artifact and a decision, not a behaviour change. If the measurement shows the
component works, nothing further happens and that is a good outcome.

The reason for the fence: three specs in the last four days each changed scoring and each cost a
comparability boundary. The post-219 cohort becomes evaluable around **2026-09-30** and needs its as-of dates
unfragmented. A measurement slice costs nothing and can land at any time.

## 2. The artifact (`evidence-confidence-distribution-v1`)

Written each `full` run to `data/efficacy/evidence-confidence.{csv,md,json}`:

- **The live distribution**: every distinct value, its company count, and the share of the universe at each.
  12 values over 102 companies is the current claim — report what it actually is.
- **Term decomposition per company**: `bestConf`, `bestQualWeight`, `distinctSourceTypes`, `divFactor`, and
  the resulting score. This is what shows WHICH term is saturated. The hypothesis is that `bestConf` is
  0.95 for nearly every company because one SEC filing is enough; the artifact must confirm or refute it.
- **Discrimination**: the Spearman rank correlation between `EvidenceConfidence` and `Trajectory` across the
  universe, and between `EvidenceConfidence` and `Opportunity`. A component that ranks nobody differently
  from the thing it multiplies is doing no work.
- **Counterfactual, computed but NOT applied**: the Opportunity each company would have had with
  `EvidenceConfidence` held at the universe median, and how many companies' RANK changes. **Rank change is
  the number that matters** — a multiplier that shifts every score identically changes no ranking, and the
  report ranks.
- Every company excluded for missing components is counted on its own named axis; a company with no signals
  reports `(not recorded)`, never 0.

## 2a. How to produce the live numbers from a worktree (required — do not report them as "owed")

A worktree has no `data/` store and no API keys, and a real `full` run would stamp the baseline store from an
unmerged branch. Specs 218 and 224 both first reported their live figures as owed or projected for exactly this
reason. That is not acceptable here, because this slice's only deliverable IS the measurement.

Produce the §2 numbers the way spec 224's amendment did, with an **env-gated, read-only integration harness**
modelled on `tests/Radar.IntegrationTests/InsiderCollapseCounterfactualTests.cs`, run against the MAIN
repository's live store at `C:\Users\scm9d\source\repos\radar\data`:

- Compute through the **production** code path (`ScoreSignalMath.EvidenceConfidenceScore` and the real
  scoring engine over real accrued signals), never a re-implementation of the formula inside the harness. A
  second copy of the formula would measure itself.
- **Strictly read-only.** Any evidence or score write must throw; scores are held in memory; the report goes to
  the temp directory only. Confirm afterwards that no file under that `data/` directory was modified during the
  run, and say so in the PR body.
- Use the latest `windowEndUtc` present in the store and the `default` strategy, and name both in the report.
- Skip cleanly when the gating environment variable is absent, so the normal test gate stays green without a
  store.

Put the harness's figures in the PR body, labelled as a read-only re-score at one instant, not a persisted run.
Only if the harness genuinely cannot run against that path read-only may you report a figure as owed, and you
must then say exactly why. Never invent a number.

## 3. The question it must answer explicitly

The markdown states, in one sentence, which of these the data supports:

- **(a) It discriminates.** Values spread meaningfully and rank changes when held constant — leave it alone.
- **(b) It is a flat tax.** Near-constant, few rank changes — it is removing ~45% of every score for nothing,
  and the fix (rescale, or drop it to a gate) is a separate spec with its own boundary.
- **(c) It discriminates the WRONG thing.** It varies, but tracks something irrelevant — e.g. how many
  collectors happened to fire for that company rather than how well-evidenced its thesis is.

(c) is the outcome worth dwelling on, because it would look like success under (a). A company covered by four
collectors is not better-evidenced than one covered by two; it is more *collected*. The artifact must report
`distinctSourceTypes` per company alongside the score so this is visible rather than inferred.

## 4. Non-goals, recorded with reasons

- **The attention discount is not in scope.** It is the product thesis, it discriminates, and bundling it
  would make neither measurable.
- **No rescaling in this slice**, even if (b) is the answer. Changing a multiplier on every company's score
  is a boundary-moving change and deserves its own spec with its own before/after.
- **No change to `QualityWeight`, `DiversityTarget` or the `Ec*` weights.** They are config; this slice
  reports what they currently produce.
- **No backfill.** The artifact describes the current window; accrued scores are never recomputed (AD-8).

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. The artifact is written on a `full` run; absent-but-not-fatal on `collect`.
3. **No fingerprint pin moves** — verified against `ScoringConfigFingerprintTests` and stated in the PR body.
4. The PR body carries the live distribution, the term decomposition, the rank-change count, and an explicit
   (a)/(b)/(c) verdict in one sentence — **measured through the §2a read-only harness against the live store**,
   with confirmation that no file under `data/` was modified.
5. Every excluded company is counted; no defaulted value renders as a measured one.
