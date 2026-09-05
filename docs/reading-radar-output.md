# Reading Radar's output — reports, formulas, and what to watch out for

Radar is a research assistant, not a trading bot or recommendation engine. Nothing it emits is
financial advice, a projection, or a recommendation. Every artifact repeats this because it is the
product's first rule: **Radar ranks; a human decides.**

This page explains the artifacts a live run produces, the scores and labels they carry, the formula
lineage behind them, and — most importantly — the known ways to misread them.

---

## What a run produces, and where

All output lands under `data/` (gitignored — these are local measurement artifacts, not repo content):

| Artifact | Path | What it is |
|---|---|---|
| **Weekly report** | `data/reports/weekly/radar-weekly-<date>.md` | The human-facing ranked report: every scored company, its label, its five scores, and the evidence behind them. Date-keyed; a later run the same day overwrites it. |
| **Strategy leaderboard** | `data/efficacy/strategy-leaderboard.{md,csv}` | Ranks the configured scoring strategies by how well their scores tracked *subsequent* price movement. Regenerated each full run. |
| **Per-ticker efficacy charts** | `data/efficacy/<ticker>.{svg,csv}` | Score-vs-price history per company — the visual join the leaderboard is built on. |
| **Score snapshots** | `data/scores/` (+ `data/scores/strategies/<name>/`) | The durable per-company, per-strategy score records every report number is read from. |
| **Run record** | `data/runs/<yyyy>/<MM>/run-*.json` | Per-run counters: collectors, evidence, signals, companies scored, source failures. The quickest "did it run, did it work" check. |

Provenance is the load-bearing invariant behind all of them: **a score without evidence is invalid.**
Every report entry names its score snapshot id; every snapshot links the signals that contributed;
every signal references the evidence item it came from, with a clickable source URL. If a number
cannot be traced back to evidence, it does not get printed.

---

## The five scores

Each company snapshot carries five integers in [0, 100]:

| Score | Question it answers |
|---|---|
| **Opportunity** | The headline: how interesting is this company *to research now*, after discounting for how noticed it already is. This is what the report ranks by and what the efficacy harness judges. |
| **Trajectory** | Is the business direction improving or deteriorating? 50 is neutral; above is improving. |
| **Attention** | How much third-party notice (media reach, publisher breadth) the company is currently getting. |
| **Evidence** (confidence) | How much corroborated, quality-weighted evidence sits behind the signals. |
| **Velocity** | How fast signals are arriving versus the prior window. |

**Notedness is the discount that makes Radar Radar.** Measured Attention plus the curated
`followingTier` (small / mid / large / mega) *reduce* Opportunity, so an already-famous name scores
low even on great news. That is deliberate: the product exists to surface under-followed improvement,
not to rediscover Apple. A useful sanity check on any report: the mega-cap benchmarks (AAPL, CAT,
JNJ, V, CVX) should sit at the bottom. If one is near the top, be suspicious of the run before being
excited about the company.

---

## The labels

Only six labels exist, and none of them is advice: `Investigate`, `Watch`, `Ignore`,
`Needs more evidence`, `Thesis improving`, `Thesis deteriorating`. Words like "buy", "sell", or
"upside" are banned from the output by rule.

The mapping (`weekly-report-action-v3`) is deterministic, first match wins:

1. **Needs more evidence** — Evidence confidence below 35. Overrides everything: with too little
   evidence, no other claim is made.
2. **Thesis deteriorating** — Trajectory fell ≥ 5 points versus the prior *comparable* snapshot
   (checked before improvement, to stay honest).
3. **Thesis improving** — Trajectory rose ≥ 5 points and sits at/above neutral (50).
4. **Investigate** — Opportunity ≥ 60.
5. **Watch** — Opportunity ≥ 40, **or** the corroboration floor: an under-followed (small/mid tier)
   name with neutral-or-better trajectory and **two or more distinct positive signal types** is
   floored from Ignore up to Watch. Independent axes agreeing is exactly the pattern Radar exists to
   surface, even when a mixed quarter drags the composite below 40. The floor never fires for
   large/mega names and never lifts anything above Watch. Since spec 210 the floor's "Why" line names
   what it counted — for every counted type, each distinct `(source class, observed date[, judgment])`
   support tuple (e.g. `GuidanceChange (filing 2026-07-29) + MediaAttention (news 2026-09-02, judgment)`), or
   past three tuples the type's distinct-date range and tuple count — so one announcement echoed
   through two extractors on the same day is visible on the line rather than hidden inside a count.
   Missing provenance prints as `source unknown` / `date unknown` / `judgment unknown`, never as absent.
6. **Ignore** — adequate evidence, low opportunity. This is "genuine low signal", not "bad company".

Note the comparability gate on rules 2–3: trajectory is only diffed against a prior snapshot from the
**same scoring configuration**. When the scoring logic changed between runs, no improving/deteriorating
story is told — a formula delta must never masquerade as a company development.

---

## Formulas and strategies

Scoring is plural: one collection pass, N **strategies** scored over it, each strategy = a named,
immutable combination of formula + weights (+ optional channel budget / signal-type filter). The
**primary** strategy (currently `default`, formula v8) is what the main report ranks by; every other
strategy gets its own ranked table further down the report and its own score series on disk.

The formula lineage — all still shippable, deliberately kept as controls for each other:

| Formula | Idea | Why it exists |
|---|---|---|
| **v8** (default) | Five components computed over arriving signals; attention as an inverse discount inside Opportunity. | The established baseline every experiment is measured against. |
| **v9** | Opportunity = weighted sum of per-collector **channels** (`Σ weight × channelScore`), plus the notedness discount applied once to the composed score. Weights are never renormalised: a silent channel costs its full weight. | Makes "which sources drive the score" an explicit, budgeted hypothesis. |
| **v10** | v9's channel score becomes `saturation × max(0, preponderance)` — a channel with no *directional* evidence contributes exactly 0. | v9 had a 0.5 floor that let **volume alone produce score** (87.6 % of all signals are Neutral). |
| **v11** | v10, but channel saturation is computed over directional activity only, so Neutral volume moves a channel by exactly 0. Rejects breadth channels. | Isolates directional evidence completely from coverage volume. |
| **baseline-activity-v1** | A plain signal count. | A control that exists to be beaten (if a strategy can't beat "count the signals", it has no edge). |

Two comparability rules that are easy to trip over:

- **Absolute scores are only comparable within one strategy.** v10/v11 scores sit on a visibly lower
  scale than v9 (removing the 0.5 floor lowered nearly everything) — compare *rankings* across
  strategies, never raw numbers. The report says this in its strategy section; believe it.
- **A strategy is immutable by convention.** Changing one means adding a new name
  (`filings-led` → `filings-led-v2`), which restarts its score series. Every snapshot is stamped with
  a config fingerprint (`ScoringConfigVersion`) so a changed configuration is detectable, and a
  startup tripwire refuses to run a renamed-in-place strategy.

---

## Reading the strategy leaderboard

`data/efficacy/strategy-leaderboard.md` relates each strategy's scores to **subsequent** price
movement. Mechanics that matter:

- **Causality is structural.** A score at date D is judged only against price over `(D, D+21]`.
  Price at or before D is never read, and price is *never* an input to scoring — it is
  validation-only, downstream, by architecture.
- **Partial windows are excluded, not mislabelled.** An observation only counts when its last price
  bar reaches at least D+17 (21-day horizon, 4-day market-closure tolerance). "No forward price at
  all" and "some price but short of the horizon" are reported as separate columns. Early in a
  company's or strategy's life, most observations are partial — a mostly-empty leaderboard is the
  honest state, not a bug.
- **The headline is out-of-sample.** Ranking uses the chronologically earlier 70 % of as-of dates;
  the quoted number comes from the later 30 % the ranking never saw. Strategies with fewer than 20
  observations in either window are dropped and named, with reasons.
- **Treat the intervals as dispersion, not significance.** Observations are pooled across companies
  and dates and are not independent, so the Fisher-z confidence intervals are optimistically narrow.
  A rho whose interval crosses zero is exactly that: not yet evidence.
- **A strategy becomes rankable ~3 weeks after its first snapshots** (its as-of dates must be ≥ 21
  days behind the latest price bar, with enough observations in both windows). Newly added strategies
  and newly added companies both go through this accrual quietly.

---

## What to watch out for

The failure modes below were all found by running the system, not by imagining them. They are the
reading discipline.

1. **First snapshots are cold-start artifacts.** A newly added company's first-run rank reflects a
   backfill of its recent filings landing all at once, not a fresh development. Give new names a few
   runs before reading anything into their position.
2. **The AI read can be flattered by GAAP headlines.** A measured example: a "record net income,
   roughly doubled YoY" print earned a 0.90-confidence Positive read — but the doubling was a
   two-sided artifact (a prior-year one-off loss plus a current-quarter one-off recovery), the
   company's own adjusted figure missed consensus, and no guidance actually changed. A deterministic
   comparability scan now caps the reader's confidence when the filing text signals a broken
   year-over-year comparison — but signals accrued *before* that cap stand until they age out of the
   scoring window. When a single high-confidence `GuidanceChange` puts a company at #1, read the
   filing.
3. **No signal is not evidence of absence.** A calibration study measured the reader's false-omission
   rate at roughly a third: of filings the reader passed over as having no directional signal, ~37 %
   contained one a blinded second read found. Recall, not precision, is the reader's weak axis. Never
   treat an absence of Radar signals as a clean bill.
4. **Confidence below 0.90 is warm, not certified.** In the same study the reader's stated confidence
   was directionally ordered but ran hot in the [0.80, 0.90) bin (~50 % correct there), while ≥ 0.95
   went 10/10 (small bins; ordering supported, upper bins not certified). Weight high-confidence
   claims accordingly, and note zero direction *inversions* were observed — errors over-commit toward
   Mixed/Neutral rather than flipping sign.
5. **Direction is scarce.** ~88 % of all accrued signals are Neutral. Most of what Radar collects is
   coverage, not evidence of improvement — which is exactly why v10/v11 exist and why a
   volume-driven rank should always be interrogated.
6. **Attention plays two roles.** Genuine publisher breadth is (budgetably) positive in channel
   formulas, while company-level fame discounts the composite. A high Attention score therefore cuts
   both ways — check which side dominated in the snapshot's explanation before narrating it.
7. **The event-enriched cohort is excluded from the primary screen.** Companies added *because* they
   had recent notable events (recorded in `docs/cohorts/`) are an exploratory cohort; including them
   in the headline efficacy screen would bake selection bias into the result.
8. **Same-day reruns overwrite the day's report.** Reports are date-keyed. The last run of the day
   wins; the score snapshots underneath are append-only and keep everything.
9. **Legacy evidence residue is dropped by design.** Signals whose evidence cannot be resolved on
   disk (an early identity bug, since fixed forward-only) are excluded from scoring with an
   aggregated per-company warning. Do not "heal" them: restoring resolution would multiply the scored
   set several-fold with duplicates.
10. **When a number surprises you, walk the chain.** Report entry → score snapshot id → contributing
    signals → evidence links. The chain is complete by construction; a surprise that survives the
    walk is interesting, and one that doesn't was a misreading.
