# Task: `radar-formula-v11` — neutral volume must not raise a score, and put it in the live run

> **AD-16 (accepted 2026-07-28) makes this binding:** *"Neutral volume must never amplify a directional
> read … under this thesis heavy routine volume is the **noticed** company Radar is trying to avoid."*
>
> `RadarScoreFormulaV10` violates that, with a test pinning the violation:
> `NeutralCoverage_StillAmplifiesAGenuineDirectionalRead` (`RadarScoreFormulaV10Tests.cs:285`) takes 2
> Positive signals, adds 6 Neutral, and asserts the channel score goes **UP**.

## ⚠️ Two corrections to an earlier draft of this spec — do not re-derive either

**1. Neutral does NOT dilute preponderance. It only raises saturation.** An earlier draft claimed both.
`ScoreSignalMath.DirectionalMasses` skips Neutral/Mixed entirely ("excluded from both masses",
`ScoreSignalMath.cs:187`) and `Preponderance` divides by `mass.Total` = Positive + Negative only
(`:249-255`). So the required change is **narrow**: for a collector channel, `saturation` must be computed
over **directional activity only** instead of all-signal activity. That is the whole of the collector-channel
fix — smaller, easier to justify, and safer to implement than the earlier framing.

**2. "Score-invariant" was never achievable as previously scoped, and the old draft contradicted itself** —
requiring Neutral to keep counting in breadth reach while also demanding the score not move. Both hold for a
collector channel; neither can hold for Opportunity while breadth counts neutral-only publishers. §2 states
the real contract.

## Design

### 1. Collector-channel saturation: directional activity only

Replace the all-signal activity feeding a **collector** channel's `saturation` with directional-only
activity. `ActivityMass` stays as-is for any caller that wants it — v9 and v10 must keep their exact current
arithmetic (§5) — so this is a new companion term, not an edit to the shared one.

Confirm and state which side of the line an absent/unknown direction falls on, rather than leaving it to the
reader.

### 2. THE CONTRACT — three levels, stated separately because they are different guarantees

| Level | Guarantee |
|---|---|
| **Collector directional-channel score** | Adding any number of Neutral signals changes it by **exactly zero** |
| **Final `OpportunityScore`** | Neutral additions may leave it **unchanged or reduce it** (via the notedness discount). They must **never increase it** |
| **Diagnostic components** (`EvidenceConfidence`, `AttentionScore`, `SignalVelocity`, `SignalCount`, `Dark`) | **Permitted to change**, with the semantics stated in the class docs |

The middle row is the one that costs work — see §3. Do not claim the top row's guarantee for the composite.

### 3. Breadth counts DIRECTIONAL publishers only — the maintainer's decision, 2026-07-28

Today the breadth channel's tier-weighted distinct-publisher reach is computed over the whole gated set, so
a Neutral news item adds a publisher, raises breadth and can raise Opportunity — while also raising
`AttentionScore` and so deepening the notedness discount. The net direction is **not fixed**, which is why
row 2 of §2 cannot be guaranteed without changing this.

**Decision: reach counts only publishers that carried at least one DIRECTIONAL signal for that company in
the window.** Breadth then means *breadth of substantive coverage* rather than volume of mentions, which is
what makes row 2 of §2 provable rather than hoped for.

Keep breadth's shape (`reach/(reach + S_c)`), its budget semantics and the never-renormalise rule unchanged
— only the population entering `reach` narrows. Expect lower absolute breadth scores; that is intended, and
re-tuning saturation constants to compensate is out of scope (§ Out of scope).

### 4. This is `radar-formula-v11`, NOT a `CompositionRevision` bump

An earlier draft proposed bumping v10 `rev1 → rev2`. That is now wrong for two independent reasons:

- **§3 changes what a component measures** (the population entering breadth reach), which is a structural
  change under AD-6, not a spec-149-style in-place adjustment.
- **A revision bump destroys the control.** Bumping in place means there is no rev1 left to run, so a live
  comparison would confound "is v11 better" with "did neutral-invariance help". A new class keeps v10
  dispatchable alongside v11 — exactly as v8 and v9 were kept when v10 shipped.

So: add `ScoreFormulaVersions.V11` to `All` (in version order), dispatch it in `RadarScoreFormulaFactory`
with the same ctor args v10 receives, and add it to `ScoreFormulaVersions.ConsumesChannels`. **v10 is
untouched and stays available as the control.**

**v11 bundles the two AD-16 corrections** (§1 and §3). Attributing the effect between them would need a
third arm; that is deliberately not built, so the hand-back must say the comparison attributes to the
corrections *collectively*, never to either individually.

### 5. v8, v9 and v10 must be byte-identical afterwards — asserted, not argued

`ScoringOutputStabilityTests` (v8), `RadarScoreFormulaV9OutputStabilityTests` (v9) and
`RadarScoreFormulaV10CompositionGuardTests` (v10) must pass **unmodified**. `ScoringChannelComposition` is
already parameterised by a delegate so a new formula need not move an existing one's arithmetic — keep it
that way, preserving expression shape and accumulation order (IEEE-754 is not associative).

`NeutralCoverage_StillAmplifiesAGenuineDirectionalRead` stays exactly as it is: it pins **v10**, which is
still shipped and still the control. v11 gets its own metamorphic test asserting the opposite.

### 6. Tests: metamorphic, not example-based

- Adding Neutral signals (0, 1, many; before, after and interleaved with the directional ones) leaves a v11
  collector channel's score **exactly** equal.
- Adding Neutral signals never raises `OpportunityScore` — including the case that motivated §3, a
  neutral-only publisher.
- An all-neutral channel (`Score 0`, `Dark false`, `SignalCount > 0`) stays distinguishable from an absent
  one (`Score 0`, `Dark true`, `SignalCount 0`).
- Every scored signal, Neutral included, keeps its evidence-linked contribution — the trail is unchanged;
  only the score is blind to it.

### 7. Live arms, and the matched comparator

Add to `scripts/run-profiles/default.json`, under **new names** (spec 141 — never an edit; the five
composite arms and three baselines are mid-accrual and **must not be renamed, edited or re-stamped**):

- one **v11** arm, and
- one **v10** arm with an *identical* channel budget as the matched control.

Mirror an existing arm's budget so the only difference between the pair is the formula. State the resulting
per-run cost (43 companies × N strategies) in the hand-back.

### 8. Precommit the AD-16 outcome BEFORE the first live snapshot is inspected

AD-16 requires the outcome variable and horizon to be declared before results are seen; starting a live arm
while leaving them open would breach the AD this spec exists to serve. **No evaluator need be implemented** —
this is a declaration, recorded as an AD-16 amendment, fixing at minimum:

- the exact **attention metric** (proposed default: the change in `AttentionScore` from D to D+h, using the
  same stored snapshots the efficacy join already reads);
- the **horizon** h, declared in calendar days;
- **eligible observations** — minimum companies per as-of date, and the spec-152 `PartialWindow` treatment;
- the **failure criterion**: what result would count as the thesis failing.

Propose concrete values, mark them as requiring maintainer sign-off, and land the amendment **in this
slice** — it is the thing that makes the accruing series interpretable later.

## Hypotheses, labelled as such

Recorded so they are not read as findings. **Measured:** the 87.6 % Neutral share, and v10's current
amplification. **Hypotheses, not yet characterised:** that neutral volume tracks company size; that the
amplification materially moves live rankings. **Thesis-consistent, not empirical:** that Neutral
`MediaAttention` is correct because news is the attention AD-16 wants to predict.

## Files (verify against the tree before planning)

`ScoreSignalMath.cs`, `ScoringChannelComposition.cs`, new `RadarScoreFormulaV11.cs`,
`ScoreFormulaVersions.cs`, `RadarScoreFormulaFactory.cs`, `ScoringStrategySet.cs`,
`scripts/run-profiles/default.json`, `DefaultRunProfileTests.cs`, `docs/architecture-decisions.md`.

## Constraints

- **No existing strategy's `ScoringConfigVersion` may move.** The four spec-148 pins stand and
  `ScoringConfigFingerprintTests` stays untouched.
- **Provenance intact**: every scored signal keeps an evidence-linked contribution.
- Price is never an input (AD-14); no advice vocabulary (AD-9).
- AD-15's positive-claim suspension (amended 2026-07-28) remains in force — this slice makes a candidate
  *internally consistent with* AD-16; it does not prove the thesis and must not be described as doing so.

## Out of scope (record, do not build)

- **Changing v8, v9 or v10** — all three remain the controls that make this measurable.
- **Re-tuning weights or saturation constants** for the lower absolute scale — measure first.
- **A third arm** to separate §1 from §3.
- **Implementing** the attention-arrival evaluator, benchmark-adjusted price, or spec 155's paired inference.
- Migrating any existing strategy onto v11.

## Acceptance criteria

- [ ] A v11 collector channel's score is **exactly** invariant to Neutral additions — metamorphic, not
      approximate.
- [ ] Neutral additions never **increase** `OpportunityScore`, including via a neutral-only publisher.
- [ ] Breadth reach counts only publishers carrying directional evidence; shape, budget semantics and the
      never-renormalise rule are unchanged.
- [ ] `radar-formula-v11` exists as its own version, wired into `All` / factory / `ConsumesChannels`; v10 is
      untouched and still dispatchable.
- [ ] v8, v9 and v10 byte-identical, proven by the three existing golden pins passing **unmodified**.
- [ ] An all-neutral channel stays distinguishable from an absent one; the evidence trail is unchanged.
- [ ] `default.json` gains a v11 arm **and** a matched v10 arm with an identical budget, under new names,
      disturbing no existing strategy.
- [ ] The AD-16 outcome precommitment (metric, horizon, eligible observations, failure criterion) is
      recorded as an amendment in this slice.
- [ ] The hand-back states the new per-run scoring cost, and attributes any v11-vs-v10 difference to the
      corrections collectively rather than to either individually.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
