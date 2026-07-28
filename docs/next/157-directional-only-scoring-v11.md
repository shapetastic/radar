# Task: `radar-formula-v11` — neutral volume must not raise a score, and put it in the live run

> ## ⛔ PAUSED 2026-07-28 — do NOT implement until spec 158 reports
>
> Two things this spec predeclares are in doubt, and both are measurable from **inputs alone, today**:
>
> 1. **The §7 budget may not produce a usable ranking.** Spec 153 measured that of 32 companies with an
>    active `sec-form4` channel, **1** was net-positive on the live window; spec 156 found
>    `InstitutionalOwnership` is **98.79 %** Neutral by design (spec 99). Under `max(0, preponderance)` a
>    net-negative channel scores 0, and under the never-renormalise rule its weight is simply lost — so
>    0.80 of the predeclared budget may be dead.
> 2. **§3 may zero out breadth entirely — and that is a doubt about §3 itself, not just the budget.**
>    Narrowing reach to publishers carrying a **Positive** signal looked like a safe tightening, but
>    `NewsArticle` evidence always becomes **Neutral** `MediaAttention` (spec 70), so no news publisher can
>    ever qualify, and RSS is **first-party**, so it is not a third-party publisher. §3-narrowed breadth may
>    therefore be structurally zero for every company.
>
> `docs/next/158-channel-feasibility-characterization.md` measures both, **input-only** — no forward
> outcome, so AD-16's pre-commitment is not consumed. When it reports, amend **§3 if breadth is unusable**,
> and **§7 and AD-16 §7 together** to the smallest viable matched pair. Changing the arm before any v11
> result exists is legitimate; changing it after would not be.

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

### 3. Breadth counts POSITIVE-carrying publishers only — the maintainer's decision, 2026-07-28

Today the breadth channel's tier-weighted distinct-publisher reach is computed over the whole gated set, so
a Neutral news item adds a publisher, raises breadth and can raise Opportunity — while also raising
`AttentionScore` and so deepening the notedness discount. The net direction is **not fixed**, which is why
row 2 of §2 cannot be guaranteed without changing this.

**Decision: reach counts only publishers that carried at least one POSITIVE signal for that company in the
window.** Breadth then means *breadth of substantive positive coverage* rather than volume of mentions,
which is what makes row 2 of §2 provable rather than hoped for.

**The exact mechanics, fixed 2026-07-28 — implement this, not a paraphrase of the sentence above.** Two
readings of "publishers that carried a Positive signal" are possible (binary inclusion vs. accumulating mass
per Positive signal) and they differ materially for a publisher carrying one Positive among many Neutral.
The rule is:

1. **Filter first**: pass **only `Positive` signals** — from **both** the post-collapse and the pre-collapse
   sets — into the **existing** reach calculation. The filter is applied to the inputs; the reach terms
   themselves (third-party publisher test, tier weights, collapsed-publisher credit, media-count) are
   **unchanged** and simply see a smaller input set.
2. **Publisher inclusion stays BINARY and DISTINCT**: a publisher qualifies if it carries **at least one**
   Positive signal, and qualifying publishers are counted **once** each, exactly as distinct-publisher reach
   has always worked. A publisher does **not** earn extra reach for carrying several Positive signals.
3. **Neutral and Negative signals contribute NEITHER publisher reach NOR the media-count term.** A Neutral
   `MediaAttention` signal adds nothing even when the same publisher already qualifies via some other
   Positive signal.
4. **`AttentionScore` is unchanged and stays over the FULL gated set** — see the boxed note below; this
   filter applies to the breadth channel only.

Spec 158 §4 measures precisely this rule and extracts it as the shared helper; **v11 must call that helper
rather than add a second positive-reach implementation.**

⚠️ **POSITIVE, not merely "directional" — an earlier draft said directional and that was wrong.**
"Directional" includes Negative, so broad *negative* coverage would have raised breadth and therefore raised
`OpportunityScore`. A score whose name is Opportunity rising because a company is widely reported to be in
trouble is indefensible, and deterioration already has its own home: the (v8-meaning) `TrajectoryScore` v10
and v11 retain. Assert the negative case explicitly — adding a publisher that carried only Negative signals
must not raise Opportunity.

Keep breadth's shape (`reach/(reach + S_c)`), its budget semantics and the never-renormalise rule unchanged
— only the population entering `reach` narrows. Expect lower absolute breadth scores; that is intended, and
re-tuning saturation constants to compensate is out of scope (§ Out of scope).

⚠️ **THIS NARROWS THE BREADTH CHANNEL ONLY. The `AttentionScore` COMPONENT IS NOT TOUCHED** and keeps its
v8 meaning over the whole gated set, exactly as v10 retains it. The two both derive from publisher reach and
are easy to conflate, but they are different things: breadth is a *budgeted channel* competing for weight,
`AttentionScore` is a *diagnostic component* that also feeds the notedness discount. Narrowing both would
additionally corrupt AD-16's secondary comparator `baseline-attention-score`, which reads this component and
must remain "all attention so far" — turning it into "positive-only attention persistence", a weaker
predictor and an easier one to beat. Assert that `AttentionScore` is unchanged between a v10 and a v11
snapshot over the same signals.

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

**v11 gets its own `CompositionRevision` and its own golden guard**, mirroring
`RadarScoreFormulaV10CompositionGuardTests`: the revision constant declared beside the composition, and one
test pinning revision + full output + the `ScoringConfigVersion` a v11 strategy stamps, **together in one
file**. Without it the next in-place change to v11 is invisible — precisely the spec-149 hole spec 153
built this mechanism to close. The alternative (requiring every subsequent composition change to mint v12)
is explicitly rejected: it would make the versioning ratchet do a guard's job.

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

**Predeclared here, not delegated** — leaving the arm or its budget to the implementer would let it be
chosen while looking at data, which is the breach AD-16's pre-commitment clause exists to prevent.

Add to `scripts/run-profiles/default.json`, under **new names** (spec 141 — never an edit; the five
composite arms and three baselines are mid-accrual and **must not be renamed, edited or re-stamped**):

| Name | Formula | Channels |
|---|---|---|
| `filings-led-v11` | `radar-formula-v11` | insider `sec-form4` 0.50 / institutional `sec-13dg` 0.30 / breadth 0.20 |
| `filings-led-v10-control` | `radar-formula-v10` | **identical to the above** |

Saturations mirror `filings-led-v2` (2 / 3 / 3) so the **only** difference between the pair is the formula.

**Filings-led, not narrative-led, and the reason is AD-16.** Narrative-led is budgeted on `newssearch` and
press — which under AD-16 *is* the attention Radar means to predict, so scoring on it confounds the input
with the outcome. Filings are the slow structured sources the stealth thesis rests on.

State the resulting per-run cost (43 companies × N strategies) in the hand-back.

### 8. Precommit the AD-16 outcome BEFORE the first live snapshot is inspected

AD-16 requires the outcome variable and horizon to be declared before results are seen; starting a live arm
while leaving them open would breach the AD this spec exists to serve. **No evaluator need be implemented** —
this is a declaration, recorded as an AD-16 amendment, fixing at minimum:

**This is ALREADY DONE — do not re-open it, do not propose alternatives, and do not treat any of it as a
default.** The precommitment is recorded and accepted as the **AMENDMENT · 2026-07-28** to AD-16 in
`docs/architecture-decisions.md`, which fixes all seven values: the primary metric (distinct third-party
publishers with a resolving `MediaAttention` signal in `(D, D+h]`), the deliberate **non-use of publisher
novelty** (89.5 % of accrued evidence is unresolvable, so novelty would measure the gap rather than the
market), `h = 21` days with **complete attention-collection coverage and no price-market tolerance**, the
**first eligible as-of date of 2026-08-22**, the valid-zero and missing-data rules, the two read-side
comparators (**primary**: the trailing 21-day distinct-publisher count; **secondary, reported not screened**:
the `AttentionScore` from the paired `filings-led-v11` snapshot), and the date-blocked descriptive failure
screen.

⚠️ Recorded so it is not re-derived: an earlier draft proposed `AttentionScore(D+h) − AttentionScore(D)`.
That is **wrong and must not be used** — `AttentionScore` is a rolling 60-day *stock*, so two readings h days
apart overlap heavily and their difference mixes new arrivals with old events ageing out, saturation
curvature and `[0,100]` rounding. A company can receive substantial new attention and show a **negative**
delta.

**This slice implements no evaluator.** Its only obligation here is not to contradict the amendment: the
live arms must begin accruing on or before the first eligible as-of date so the declared window is
populated when the evaluator is eventually built.

## Hypotheses, labelled as such

Recorded so they are not read as findings. **Measured:** the 87.6 % Neutral share, and v10's current
amplification. **Hypotheses, not yet characterised:** that neutral volume tracks company size; that the
amplification materially moves live rankings. **Thesis-consistent, not empirical:** that Neutral
`MediaAttention`'s neutrality is coherent with AD-16 — news being the attention the thesis means to predict
rather than an input to it — which is a statement about design consistency, not a measured result.

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
- [ ] Breadth reach counts only publishers carrying **Positive** evidence; a publisher carrying only
      Negative signals does **not** raise Opportunity — asserted. Shape, budget semantics and the
      never-renormalise rule are unchanged.
- [ ] `radar-formula-v11` exists as its own version, wired into `All` / factory / `ConsumesChannels`; v10 is
      untouched and still dispatchable.
- [ ] The `AttentionScore` **component** is byte-identical between a v10 and a v11 snapshot over the same
      signals — §3 narrows the breadth channel only, and AD-16's secondary comparator depends on it.
- [ ] v11 carries its own `CompositionRevision` and a golden guard pinning revision + output + stamp
      together in one file.
- [ ] The precommitted attention outcome is a forward **flow** over `(D, D+h]` — never a difference of
      `AttentionScore` stocks — and preserves a complete-window zero as a valid outcome.
- [ ] The live pair is exactly `filings-led-v11` and `filings-led-v10-control`, identical budgets.
- [ ] v8, v9 and v10 byte-identical, proven by the three existing golden pins passing **unmodified**.
- [ ] An all-neutral channel stays distinguishable from an absent one; the evidence trail is unchanged.
- [ ] `default.json` gains a v11 arm **and** a matched v10 arm with an identical budget, under new names,
      disturbing no existing strategy.
- [ ] The AD-16 outcome precommitment (metric, horizon, eligible observations, failure criterion) is
      recorded as an amendment in this slice.
- [ ] The hand-back states the new per-run scoring cost, and attributes any v11-vs-v10 difference to the
      corrections collectively rather than to either individually.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
