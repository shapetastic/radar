# Task: Make v10 score-invariant to neutral volume, and put it in the live run

> **AD-16 (accepted 2026-07-28) makes this binding:** *"Neutral volume must never amplify a directional
> read … under this thesis heavy routine volume is the **noticed** company Radar is trying to avoid, and it
> correlates with size."*
>
> `RadarScoreFormulaV10` currently violates that, and does so **deliberately and with a test pinning it**:
> `NeutralCoverage_StillAmplifiesAGenuineDirectionalRead` (`RadarScoreFormulaV10Tests.cs:285`) takes 2
> Positive signals, adds 6 Neutral, and asserts the channel score goes **UP**.
>
> Spec 153 removed v9's unconditional 0.5 floor but left a **conditional** one: volume still produces score,
> now gated on the channel holding at least one directional signal. Neutral mass raises `saturation` while
> diluting `preponderance`, and where activity is below the saturation constant the first effect wins.
> With **87.6 % of all signals Neutral** and neutral volume tracking company size, that multiplier is doing
> a great deal of work — and pointing the wrong way.

> **And there is a second, quieter problem: `radar-formula-v10` is configured NOWHERE.** `default.json`
> runs v8 ×2, v9 ×4 and `radar-baseline-activity-v1` ×2. v10 shipped 2026-07-28 with a composition guard, a
> golden pin and a measured 43-company comparison — and nothing uses it, so **no v10 data is accruing at
> all.** A strategy configured today yields its first rankable number ~21 days later, so this costs calendar,
> which is the scarce resource while Radar is in its find-what-works phase.

## Design

### 1. Neutral additions must be EXACTLY score-invariant, not merely damped

The target property, stated so it is testable rather than aspirational:

> Adding any number of Neutral signals to a channel changes that channel's score by **exactly zero**.

That requires neutral signals to be excluded from **both** terms — the activity that feeds `saturation`
**and** the denominator of `preponderance`. Damping one while leaving the other leaves a residual
size proxy, which is the defect, smaller.

**Decide and document what "directional" means at the boundary**, rather than leaving it to the reader: a
signal whose direction is Neutral contributes to neither term. Confirm against `ScoringChannelComposition`
whether any signal can reach a channel with an absent/unknown direction, and if so state which side of the
line it falls on and why.

### 2. Neutral evidence is still NOT discarded — this is the spec-153 distinction, kept

A Neutral signal must still: appear in the contribution chain with its evidence link, count in
`SignalCount`, keep the channel out of `Dark`, count toward `EvidenceConfidence`, and count in the breadth
channel's reach. **The evidence trail is unchanged; only the channel score becomes blind to it.** An
all-neutral channel (`Score 0`, `Dark false`, `SignalCount > 0`) must stay distinguishable from an absent
one (`Score 0`, `Dark true`, `SignalCount 0`) — spec 153 made `Dark` load-bearing for exactly this and it
becomes more so here.

### 3. Bump `CompositionRevision`; do NOT mint `radar-formula-v11`

This changes which signals feed existing terms, not the composition's shape — it is still
`saturation × max(0, preponderance)` over weighted channels with the notedness discount applied once to the
composite. That is the spec-149-shaped in-place adjustment `IScoreFormula.CompositionRevision` was built for
(spec 153), so bump `rev1` → `rev2` and update the three pins in
`RadarScoreFormulaV10CompositionGuardTests` together, as that file's own contract requires.

The re-stamp is the **intended visible consequence**: it moves the v10 `ScoringConfigVersion` and will trip
`StrategyIdentityGuard` for any recorded v10 strategy. **This is cheap precisely because no live strategy
uses v10 today** — take the free window. If a reviewer judges the change structural rather than
compositional, `radar-formula-v11` is the alternative and must be argued explicitly against AD-6.

### 4. Replace the amplification test with its metamorphic opposite

`NeutralCoverage_StillAmplifiesAGenuineDirectionalRead` **must be deleted, not edited into vagueness** — it
asserts the behaviour AD-16 forbids. Replace it with a metamorphic property test asserting exact equality
across neutral additions (0, 1, many; before, after and interleaved with the directional signals), plus the
distinguishability of all-neutral from absent, and the preservation of the evidence trail from §2.

### 5. Put v10 in the live run — new names, never an edit

Add v10 strategies to `scripts/run-profiles/default.json` so the series starts accruing. Under spec 141's
immutable-by-convention rule these are **new names** (e.g. `filings-led-v10`, `narrative-led-v10`), never an
edit to an existing arm — the five composite arms and three baselines are mid-accrual and **must not be
disturbed, renamed or re-stamped**.

Mirror an existing arm's channel budget so the comparison isolates the formula rather than confounding it
with a different budget. State the resulting per-run cost in the hand-back (43 companies × N strategies) and
keep the addition small — AD-15's control-group discipline applies to composites too.

## Files (verify against the tree before planning)

`RadarScoreFormulaV10.cs`, `ScoringChannelComposition.cs` (the shared pass — note v9 routes through it and
**must stay byte-identical**), `RadarScoreFormulaV10Tests.cs`,
`RadarScoreFormulaV10CompositionGuardTests.cs`, `scripts/run-profiles/default.json`,
`DefaultRunProfileTests.cs`, and `docs/architecture-decisions.md` if an AD-6 note is warranted.

## Constraints

- **v8 and v9 must be byte-identical afterwards — asserted, not argued.** `ScoringOutputStabilityTests` and
  `RadarScoreFormulaV9OutputStabilityTests` are the existing golden pins; they must pass **unmodified**. The
  shared `ScoringChannelComposition` is parameterised by a delegate precisely so v9's arithmetic need not
  move — keep it that way, preserving expression shape and accumulation order (IEEE-754 is not associative).
- **No existing strategy's `ScoringConfigVersion` may move.** Only v10's stamp changes. The four spec-148
  fingerprint pins stand and `ScoringConfigFingerprintTests` stays untouched.
- **Provenance intact**: every scored signal keeps an evidence-linked contribution; a score without evidence
  is invalid.
- Price is never an input (AD-14); no advice vocabulary (AD-9).

## Out of scope (record, do not build)

- **Changing v8 or v9.** Both remain as the controls that make this measurable.
- **Re-tuning channel weights or saturation constants** to compensate for the lower absolute scale — measure
  first (spec 153 said the same, and it still applies).
- **The breadth channel.** Spec 153 recorded the honest tension that breadth still earns share from pure
  coverage; it is budgeted, opt-in and damped by the notedness discount. Re-tuning it needs its own evidence.
- **Migrating any existing strategy onto v10** — opt-in, new names only.
- The attention-arrival and benchmark-adjusted outcome variables (AD-16), and spec 155's paired inference —
  all read-side, retrospectively applicable, deliberately parked.

## Acceptance criteria

- [ ] Adding any number of Neutral signals to a channel changes its score by **exactly zero** — asserted
      metamorphically, not approximately.
- [ ] Neutral evidence still appears in the contribution chain, `SignalCount`, `Dark`, `EvidenceConfidence`
      and breadth reach — asserted.
- [ ] An all-neutral channel remains distinguishable from an absent one.
- [ ] `NeutralCoverage_StillAmplifiesAGenuineDirectionalRead` is **deleted** and replaced by its
      metamorphic opposite.
- [ ] `CompositionRevision` bumped and all three guard pins updated together.
- [ ] v8 and v9 byte-identical, proven by the existing golden pins passing unmodified.
- [ ] At least one v10 strategy is configured in `default.json` under a NEW name, mirroring an existing
      arm's budget; no existing strategy is renamed, edited or re-stamped.
- [ ] The hand-back states the new per-run scoring cost.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
