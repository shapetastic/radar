# Task: `radar-formula-v11` — neutral volume must not raise a score, and put it in the live run

> ## ✅ UNPAUSED 2026-07-28 — spec 158 reported; post-review observability amendment applied
>
> This spec was paused pending measurement. Spec 158 (PR #161, merged) answered both original doubts against
> the original design, and a post-merge input-only review closed one further observability gap:
>
> 1. **The predeclared budget was unrankable.** `sec-form4` .50 / `sec-13dg` .30 / breadth .20 scored a
>    **constant integer 0 across all 43 companies** — `sec-13dg` had zero in-window signals at all. AD-16 §7
>    excludes a date whose predictor is constant, so that arm could never have cleared or failed the screen.
> 2. **Positive-only breadth was structurally zero** (`Var(positive reach) = 0`, one distinct value). Spec 70
>    makes every news signal Neutral and first-party RSS is not a publisher, so nothing can qualify. **§3
>    amended** — v11 rejects breadth channels outright.
> 3. **The initially suggested replacement B was not uniformly observable.** Only **26/43** seeded companies
>    have an RSS feed; 17 have none. Its 0.40 press share would therefore mix valid quiet with missing source
>    configuration. **§7 adopts measured option A instead:** one `sec-edgar` channel, observable for 43/43.
>
> Every amendment was made **before any v11 snapshot existed** and without reading a forward outcome, so
> AD-16's pre-commitment is intact. AD-16 §§4/7 are amended in step.

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

### 3. A v11 strategy declares COLLECTOR CHANNELS ONLY — breadth is rejected at startup

> **AMENDED 2026-07-28 after spec 158 measured it. The previous decision — "reach counts only publishers
> carrying a Positive signal" — is WITHDRAWN, because it is structurally zero.** Spec 158 measured
> `Var(positive reach) = 0`, one distinct reach value (`0`), for all 43 companies: spec 70 makes every
> `NewsArticle` signal Neutral, and first-party RSS is not a third-party publisher, so **no publisher can
> ever qualify**. The rule was not a tightening; it was an off switch. The mechanics that were fixed for it
> are recorded in `docs/158-channel-feasibility-findings.md` and implemented as
> `ScoreSignalMath.PositiveAttentionReach`; that helper stays (158 measured through it) but **no shipped
> strategy declares a channel that uses it.**

Breadth is now cornered, and both ways out are closed:

- **Positive-filtered** ⇒ structurally zero, measured. A declared breadth weight would be silently lost
  under the never-renormalise rule — the "dead budget" failure this whole slice exists to avoid.
- **Unfiltered** ⇒ a Neutral news item raises reach, raises breadth and can raise `OpportunityScore`, which
  breaks row 2 of §2 and contradicts AD-16.

**Decision: `radar-formula-v11` REJECTS a breadth channel at startup**, with a message citing this finding
and pointing at `docs/158-channel-feasibility-findings.md`. Fail-fast, not fail-silent — a legal-but-useless
breadth channel would reintroduce exactly the dead-weight problem in a form nobody notices. If the collector
mix later produces Positive third-party signals, positive-only breadth becomes viable again and earns
`radar-formula-v12` under AD-6; it does not get retrofitted into v11.

**This STRENGTHENS §2's contract rather than weakening it.** With no breadth channel, a Neutral addition
touches no collector channel (§1) and can only deepen the notedness discount — so row 2's "never increases"
holds *by construction* instead of resting on a filter.

**The withdrawn rule, recorded so it is not reinvented.** It was: filter to `Positive` on both the
post-collapse and pre-collapse inputs before the existing reach terms; binary and distinct publisher
inclusion; Neutral *and* Negative excluded from publisher reach and the media-count term. That rule is
correct as specified and is implemented and tested as `ScoreSignalMath.PositiveAttentionReach` — it simply
has no qualifying inputs in this collector mix. Two things it also settled remain true and are worth keeping
in view: broad **Negative** coverage must never raise a score named Opportunity (deterioration belongs to
the v8-meaning `TrajectoryScore` that v10 and v11 retain), and the filter never applied to `AttentionScore`.

⚠️ **THE `AttentionScore` COMPONENT IS NOT TOUCHED BY ANY OF THIS** and keeps its
v8 meaning over the whole gated set, exactly as v10 retains it. The two both derive from publisher reach and
are easy to conflate, but they are different things: breadth is a *budgeted channel* competing for weight,
`AttentionScore` is a *diagnostic component* that also feeds the notedness discount. Narrowing both would
additionally corrupt AD-16's secondary comparator `baseline-attention-score`, which reads this component and
must remain "all attention so far" — turning it into "positive-only attention persistence", a weaker
predictor and an easier one to beat. Assert that `AttentionScore` is unchanged between a v10 and a v11
snapshot over the same signals.

### 4. This is `radar-formula-v11`, NOT a `CompositionRevision` bump

An earlier draft proposed bumping v10 `rev1 → rev2`. That is now wrong for two independent reasons:

- **Directional-only collector saturation is a structural formula change** under AD-6, not a
  spec-149-style in-place adjustment. Separately, §3 makes rejection of a breadth channel part of v11's
  configuration contract so a legal configuration cannot violate §2.
- **A revision bump destroys the control.** Bumping in place means there is no rev1 left to run. A new class
  keeps v10 dispatchable alongside v11 — exactly as v8 and v9 were kept when v10 shipped.

So: add `ScoreFormulaVersions.V11` to `All` (in version order), dispatch it in `RadarScoreFormulaFactory`
with the same ctor args v10 receives, and add it to `ScoreFormulaVersions.ConsumesChannels`. **v10 is
untouched and stays available as the control.**

**The live matched comparison isolates the collector-saturation change in §1.** Both configured arms have
an identical collector-only budget and neither declares breadth, so breadth rejection cannot contribute to
their score difference. No third arm is needed for attribution: any v11-v10 ranking difference in this pair
is attributable to directional-only rather than all-signal collector saturation. State exactly that in the
hand-back; do not attribute the observed difference to the two amendments collectively.

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

> **AMENDED 2026-07-28 after spec 158 and the post-merge observability review.** The previously predeclared
> pair (`filings-led-v11` / `filings-led-v10-control`, insider `sec-form4` .50 / institutional `sec-13dg`
> .30 / breadth .20) was measured to score a **constant integer 0 for all 43 companies**. A constant
> predictor is not merely weak: AD-16 §7 excludes the date, so that arm could never have cleared or failed
> the screen. It is withdrawn.
>
> Spec 158 initially suggested option B (`sec-edgar` .60 / RSS .40), but a post-merge seed-coverage audit
> found that only **26/43** companies have an RSS feed while all **43/43** have `sec-edgar`. B therefore mixed
> a valid zero with an unobserved source for 17 companies. No forward outcome or v11 snapshot was inspected.
> The observable, strictly smaller option A is adopted instead.

| Name | Formula | Channels |
|---|---|---|
| `disclosure-led-v11` | `radar-formula-v11` | `filings` = `sec-edgar` **1.00**, S 3 |
| `disclosure-led-v10-control` | `radar-formula-v10` | **identical to the above** |

The fixed-window input-only distributions remain useful context:

| Option | companies > 0 | distinct integers | largest tie-group | variance |
|---|---:|---:|---:|---:|
| **A — adopted** (`sec-edgar` 1.00) | **13 / 43** | **9** | **30** | 30.39 |
| B (`sec-edgar` .60 / RSS .40) | 17 / 43 | 10 | 26 | 13.92 |
| C (RSS .60 / `sec-form4` .40) | 7 / 43 | 6 | 36 | 7.05 |

Raw variance is not the choice criterion for a Spearman screen, and B has slightly better tie resolution.
But that small gain does not justify making 0.40 of the score depend on whether Radar happens to have an RSS
feed. Option A is the fewest-channel/fewest-collector candidate, has uniform configured source coverage, and
keeps the construct on regulated company disclosure rather than self-favourable press-release syndication.
`newssearch` remains excluded because third-party pickup is the outcome.

⚠️ **The weak point, stated because the whole budget rests on it:** `sec-edgar`'s legacy collector
attribution is spec-151 **inferred by elimination** and is **reasoned, not ground-truth validated** — 151's
validation cohort was 337 `newssearch` / 2 `sec-form4` / 2 RSS. If that elimination rule is wrong, the pinned
measurement is mis-populated. The live arm accrues on forward recorded attribution; re-check the mapping once
that cohort is large enough, without changing this budget after results are visible.

⚠️ **The pinned measurement is coverage-limited, not a lower bound on score or rank quality.** Spec 158
dropped **14,089 of 17,616** in-window signals (80 %) as evidence-unresolvable. Resolving more signals can
add Positive or Negative mass, move preponderance in either direction, and create or remove ties; improvement
is not monotone. The arm may start accruing immediately, but AD-16 §4 excludes primary-screen dates until a
complete 60-day scoring window is post-spec-145. Do not re-tune the budget to this transitional measurement.

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
**first eligible primary-screen as-of date of 2026-09-26**, the valid-zero and missing-data rules, the two read-side
comparators (**primary**: the trailing 21-day distinct-publisher count; **secondary, reported not screened**:
the `AttentionScore` from the paired `disclosure-led-v11` snapshot), and the date-blocked descriptive failure
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
- [ ] A v11 strategy declaring any breadth channel fails startup with a message citing spec 158; no legal v11
      configuration can silently spend weight on breadth.
- [ ] `radar-formula-v11` exists as its own version, wired into `All` / factory / `ConsumesChannels`; v10 is
      untouched and still dispatchable.
- [ ] The `AttentionScore` **component** is byte-identical between a v10 and a v11 snapshot over the same
      signals. Breadth is rejected as a strategy channel; the diagnostic remains full-set attention because
      AD-16's secondary comparator depends on it.
- [ ] v11 carries its own `CompositionRevision` and a golden guard pinning revision + output + stamp
      together in one file.
- [ ] The precommitted attention outcome is a forward **flow** over `(D, D+h]` — never a difference of
      `AttentionScore` stocks — and preserves a complete-window zero as a valid outcome.
- [ ] The live pair is exactly `disclosure-led-v11` and `disclosure-led-v10-control`, identical budgets
      (one `sec-edgar` channel at 1.00, S 3), and neither declares a breadth channel.
- [ ] v11 calls the existing `ScoreSignalMath.DirectionalActivityMass` and shared composition seam extracted
      by spec 158; the retained `PositiveAttentionReach` helper is not used by a shipped v11 arm.
- [ ] v8, v9 and v10 byte-identical, proven by the three existing golden pins passing **unmodified**.
- [ ] An all-neutral channel stays distinguishable from an absent one; the evidence trail is unchanged.
- [ ] `default.json` gains a v11 arm **and** a matched v10 arm with an identical budget, under new names,
      disturbing no existing strategy.
- [ ] The AD-16 outcome precommitment (metric, horizon, eligible observations, failure criterion) is
      recorded as an amendment in this slice.
- [ ] The hand-back states the new per-run scoring cost and attributes any v11-vs-v10 difference specifically
      to directional-only versus all-signal collector saturation; the identical arms contain no breadth.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
