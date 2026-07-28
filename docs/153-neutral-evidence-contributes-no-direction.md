# Task: `radar-formula-v10` — neutral evidence establishes coverage but contributes no directional opportunity

> **Activity is currently being scored as improvement.** Measured on the live store: **87.6% of all 49,793
> signals are Neutral** (Positive 8.1%, Negative 4.3%). And a v9 collector channel scores
> `channelScore = saturation × directionFactor`, where
> `directionFactor = 0.5 + 0.5 × preponderance` and **a channel with no directional mass sits at exactly
> `0.5`** (`RadarScoreFormulaV9.cs:129-133`, `:282-292`).
>
> The code calls that "neither rewarded nor punished", and relative to a *mixed* channel it is. But relative
> to an **inactive** channel — which contributes 0 — an all-neutral channel scores `saturation × 0.5`,
> **rising with activity.** Volume alone produces score.

## Why this matters more than it looks

It lands exactly on the strategies built to test the thesis. `filings-led`'s two channels are:

- `sec-form4` → routine Form 4s, extracted as `InsiderBuying` **Neutral** ("matched phrase 'insider stock
  transaction (routine)'"), and
- `sec-13dg` → passive 13G filings, which spec 99 made **Neutral by design** precisely so they "never
  misfire bullish".

So `filings-led` is substantially ranking **filing volume**. Larger companies file more, which is a better
explanation of the observed CAT/CVX-vs-GTY inversion than notedness alone.

**The corroborating evidence:** the 2026-07-28 replay backtest ranked five deliberately-different strategies
(one v8, four v9 with different channels and different notedness settings) and they came in at in-sample rho
−0.0849 / −0.0969 / −0.0999 / −0.1000 / −0.1009. A spread of **0.016**. Five strategies that should disagree
producing the same number is what you would expect if a single common factor — activity — dominates them all.

## Design

### 1. No directional mass ⇒ no directional opportunity

A channel whose signals carry no net direction must contribute **zero** to Opportunity, not half. Neutral
evidence should establish that a source is **covered and alive** — it must keep counting toward coverage,
confidence and the "we looked and saw activity" record — but it must not read as "improving".

**Decide and document the exact shape**, because two different behaviours are being conflated today:

- a channel with **no directional signals at all** (all Neutral/Mixed), and
- a channel with **balanced** positive and negative mass.

Both currently land at `directionFactor = 0.5`. Whether they *should* land in the same place is a real
question — argue it explicitly rather than picking silently. (A defensible reading: both mean "no net
improvement signal", so both contribute zero directional opportunity while differing in what the evidence
trail shows.)

### 2. This is a formula STRUCTURE change ⇒ `radar-formula-v10`

Component shape changes, so under AD-6 this earns a new `RadarScoreFormulaV10` class with its own version
token. **v9 stays byte-identical and stays available** — it is the control that makes the change
measurable, exactly as v8 remained when v9 shipped.

⚠️ **And close the hole 149 exposed while you are here, or state why not:** v9's composition changed in spec
149 *without* its fingerprint moving, which silently mixed pre- and post-change scores in one series. A v10
strategy must get a distinct identity, and it must be impossible to change v10's composition later without
that being visible.

### 3. Leave v8 alone

v8's Trajectory has a related property — an all-neutral company lands at `TrajectoryNeutral = 50`, mid-scale
rather than zero — but v8 is the established baseline and the control for every comparison. **Do not change
it in this slice.** Record the observation; if it needs fixing that is a separate, deliberate decision.

### 4. Show the effect on real data

Re-score the accrued window under v9 and v10 and report, per strategy: the score distribution, how the
company ranking changes, and specifically **what happens to the companies whose score was previously driven
by routine Form 4s and passive 13Gs**. If v10 does not visibly separate from v9 on the real store, the
change did not do what this spec claims and that should be reported, not smoothed over.

## Files (verify against the tree before planning)

`RadarScoreFormulaV9` (untouched; source for the shared pieces), new `RadarScoreFormulaV10`,
`ScoreSignalMath` (extract rather than copy — v9 already carries verbatim copies of v8's EvidenceConfidence
and SignalVelocity blocks, the audit's M3, and a third copy would make it worse), `ScoreFormulaVersions`,
`IScoreFormulaFactory`, and the strategy binding.

## Constraints

- **v9 and v8 are byte-identical after this slice** — asserted, reusing `ScoringOutputStabilityTests` or the
  spec-146 differential harness rather than writing a third mechanism.
- **Neutral evidence still counts toward coverage/confidence and still appears in the evidence trail.** This
  slice removes a *directional* contribution, not the evidence.
- **Provenance intact**; price never an input (AD-14).
- **No pin move for existing strategies**; a v10 strategy gets its own identity.
- Layering unchanged; no `IConfiguration` in `Radar.Application`.

## Out of scope (record, do not build)

- **Changing v8.**
- **Re-tuning channel weights or saturations** to compensate — measure first.
- **The efficacy horizon / outcome variable** — spec 152 and the open question after it.

## Acceptance criteria

- [ ] A channel with no directional mass contributes **zero** directional opportunity under v10; the
      all-neutral vs balanced-mass distinction is decided and documented.
- [ ] Neutral evidence still contributes to coverage/confidence and remains in the evidence trail — asserted.
- [ ] v8 and v9 output byte-identical — asserted.
- [ ] `radar-formula-v10` exists as its own version with a distinct strategy identity; changing its
      composition later cannot silently reuse an existing fingerprint.
- [ ] Shared per-signal maths is extracted, not copied a third time.
- [ ] The hand-back reports the measured v9-vs-v10 effect on the accrued store, including the
      routine-Form-4 / passive-13G cases.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
