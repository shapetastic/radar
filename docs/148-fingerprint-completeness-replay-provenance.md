# Task: Close the two provenance holes the architecture audit found — fingerprint completeness, and replay's missing config/tripwire

> Both findings come from the `radar-architecture-reviewer` sweep of `main` @ `b9b3f65` (2026-07-27), after the
> 136–147 burst. Neither is a HIGH; both are load-bearing for the strategy comparison the arc was built for,
> so they belong **before** anyone leans on replay-based ranking.
>
> **The two parts are separable.** If Part A + Part B together prove too large, ship **Part A** (it is the one
> that moves pins, and doing it once is cheaper than twice) and hand Part B back as its own spec — say so
> explicitly rather than half-doing both.

---

## Part A — two output-affecting inputs are hashed into nothing

`ScoringConfigFingerprint.Compute(engineVersion, formulaVersion, weights, attentionDescriptor,
signalSourceDescriptor, insiderMaterialityDescriptor, mediaCollapseDescriptor)` folds every other
output-affecting input by value. Two are missing:

1. **`ScoringOptions.Window`** (bound from `Radar:ScoringWindowDays`). Consumed at `ScoringEngine.cs:233`
   (`windowStartUtc = windowEndUtc - _options.Window`) and `:338` (previous/velocity window). A 14-day and a
   30-day run produce materially different Trajectory, SignalVelocity and Attention and stamp the **same**
   `ScoringConfigVersion`. `EffectiveScoringConfig` carries no window field.
2. **`ScoringWeights.TrajectoryCorroborationK`** — already recorded as a known gap (spec 146 hand-back); the
   audit confirms it is still the **only** missing `ScoringWeights` field. It is now a v9 channel-direction
   input as well as a v8 one.

**Why this is worse after spec 141.** The window is an in-place edit to a named strategy that
`StrategyIdentityGuard` structurally cannot see, and `ScoreSeriesKey` keeps both cohorts in the same
`default` series. That is precisely the "silently continue one series while measuring something else"
failure the guard's own error message describes — the guard now *promises* to catch in-place edits, so a
category it cannot see is a broken promise rather than a gap.

**Do both in one slice**, because each alone costs a pin move and together they cost one.

### Design

- Append the window as a new fixed-position field in `Compute` (invariant-culture, e.g. whole days or ticks —
  pick one, document it, and keep it injective per AD-3), after `mediaCollapseDescriptor`, following exactly
  the pattern specs 96/109 used to add their descriptors. Carry it verbatim on `EffectiveScoringConfig` so
  the store's descriptor↔fingerprint self-verification still holds.
- Add `TrajectoryCorroborationK` to the `ScoringWeights` fold alongside the other 25 fields.
- **The pins move — deliberately, once.** Update `ScoringConfigFingerprintTests` from
  `2ce20f8fc497` / `3457da53489d` to the new values, and add a lineage note. AD-10 **as amended by spec 141**
  explicitly permits an intended pin move; it is the deliverable here, not scope leakage.
- Add a `docs/architecture-decisions.md` lineage entry recording what moved and why.

**Verify before designing** that no *other* output-affecting input is missing — the audit says these two are
the only ones, but confirm against `ScoringOptions` and `ScoringWeights` rather than trusting it.

---

## Part B — replay writes snapshots with weaker provenance than any other pass

Every forward runner persists the effective config before scoring —
`ScoringPass.cs:75-77` calls `_scoringConfigStore.WriteIfNewAsync(strategy.Engine.EffectiveConfig, ct)` —
and all three open with the tripwire (`RadarPipelineRunner.cs:87-89`, `CollectOnlyPipelineRunner.cs:58-60`,
`ScoreOnlyPipelineRunner.cs:112-114`).

`ReplayRunner` takes **neither** dependency. Its constructor is `(ICompanyRepository,
IReplayScoringStrategyFactory, IReplayScoreSnapshotFileStoreFactory, ReplayPlan, ILogger<ReplayRunner>)`.
Consequences:

- A replay-only run in a fresh data root emits snapshots stamped with a fingerprint that **dereferences to
  nothing** — the weights that produced those scores are unrecoverable.
- An in-place strategy edit, re-replayed under the same label, **silently overwrites** the previous output
  with different scores under the same name (the store is keyed by as-of instant,
  `ReplayScopedScoreSnapshotFileStoreFactory.cs:114-125`).

**This is the arc's intended workflow.** `AddRadarStrategyComparisonOverReplay` and
`Radar:Efficacy:Comparison:ReplayLabel` exist so the spec-140 leaderboard ranks strategies from replay
output — so the path Radar is meant to use for choosing a strategy has the weakest provenance in the system.

### Design

- Inject `IScoringConfigStore` into `ReplayRunner` and call `WriteIfNewAsync(strategy.Engine.EffectiveConfig, ct)`
  once per strategy in the outer loop. Insert-if-new, so it is free when the config already exists.
- Run `StrategyIdentityGuard.VerifyAsync` at the top of `RunAsync`. The guard is read-mostly and degrades to
  "unrecorded" on a read failure, so it cannot make a read-only mode fail spuriously — **confirm that
  degradation behaviour before relying on it.**
- **Do not weaken replay's read-only guarantee.** Replay must still mutate no signal/evidence store and must
  still never write the live scores directory (spec 139). Writing the scoring-config store is a provenance
  record, not a scoring mutation — assert the distinction rather than assuming it is obvious.
- Decide and state whether same-label overwrite should now fail, warn, or remain silent. **Recommended: warn
  loudly**, since a silent overwrite of a ranked series is how a comparison quietly becomes wrong.

---

## Constraints

- **Pins move in Part A only, and exactly once.** Part B must move no fingerprint input.
- **Scores must not change** in either part — this is identity and record-keeping.
- **`replay ⊆ forward` must still hold** (spec 139) — Part B changes what replay *records*, not what it computes.
- **Provenance is sacred**: after this slice, every snapshot Radar writes — forward, score-only or replay —
  must have a dereferenceable `ScoringConfigVersion`.
- **Layering:** no `IConfiguration` in `Radar.Application`.
- AD-14 intact: no price read anywhere in scope.

## Out of scope (record, do not build)

- **M3 (v9 copied v8's EvidenceConfidence/SignalVelocity blocks instead of extracting them)** — real, guarded
  today by a pinning test, and its own slice.
- **M4 (scores not merged into the repository-is-the-file-store shape)** — the audit recommends *documenting*
  it as a deliberate boundary, not changing code.
- **The `StrategyIdentityGuard` vs routine `RuleSetVersion` bump operating procedure** — a real decision the
  audit surfaced, but a policy question, not this slice.
- Stale-doc cleanups (L1–L4), except any doc this slice's own edits make wrong.

## Acceptance criteria

- [ ] `Radar:ScoringWindowDays` changes the `ScoringConfigVersion` — asserted by a test scoring the same
      inputs under two window lengths.
- [ ] `TrajectoryCorroborationK` changes the `ScoringConfigVersion`.
- [ ] The window is carried on `EffectiveScoringConfig`; the store's descriptor↔fingerprint self-verification
      still holds.
- [ ] Both pins updated deliberately, with a lineage entry in `docs/architecture-decisions.md`.
- [ ] Numeric scores are byte-identical to pre-slice for identical inputs; only stamps differ.
- [ ] `ReplayRunner` persists each strategy's `EffectiveScoringConfig` and runs the identity tripwire.
- [ ] Replay still mutates no signal/evidence store and never writes the live scores directory — asserted.
- [ ] Same-label replay overwrite behaviour is decided, implemented and stated.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
