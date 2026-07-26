# Task: Multi-strategy scoring — run N strategies over ONE collection pass

> **FOUNDATION SLICE for the collection/strategy decoupling.** Today a scoring change *is* a formula
> change: every weight tweak or collector toggle re-stamps `ScoringConfigVersion` and **resets the efficacy
> series**, so nothing is ever held still long enough to measure and each improvement destroys the evidence
> for the last one. This slice makes strategies plural — one collection pass, N independently-stamped
> scorings — so a new strategy can be added without disturbing any existing series.
>
> **Safety property (load-bearing): with a single strategy configured — the default — output must be
> byte-identical to today, and the default fingerprints must hold unchanged.** Same shape of guarantee as
> spec 136's forward-run no-op.

## The key structural fact (verified against `main`)

`ScoringEngine` already takes `IScoreFormula`, `ScoringWeights`, `IAttentionSourceWeights`,
`InsiderMaterialityWeights`, `MediaAttentionCollapse` and `ScoringOptions` by **constructor injection**, and
computes `_scoringConfigFingerprint` + `_effectiveConfig` **once in the constructor**
(`ScoringEngine.cs:46-95`).

**So one engine instance already IS one strategy.** It is merely registered as a singleton with a single
config today. This slice therefore does **not** refactor the scoring core, does not introduce per-call
weights, and does not touch the formula. It changes *composition* (build N engines) and *storage* (key
output by strategy).

## Design

### 1. Strategy configuration

```jsonc
"Radar": {
  "Strategies": [
    { "Name": "baseline",  "ScoringProfile": "default" },
    { "Name": "low-media", "ScoringProfile": "low-media" }
  ],
  "PrimaryStrategy": "baseline"
}
```

- `ScoringProfile` names an existing `Radar:Scoring:Profiles:{name}` block — the machinery that already
  binds `ScoringWeights`. **No new weight concept is introduced.**
- **When `Radar:Strategies` is absent or empty**, synthesise exactly one strategy from the current
  `Radar:Scoring:Profile` (default `"default"`), named `"default"`, and treat it as primary. This is what
  makes the byte-identical guarantee hold for every existing config, including `run-radar.ps1`'s profiles.
- **Fail fast at startup** (consistent with the existing DI validation discipline) on: an unknown
  `ScoringProfile`, duplicate strategy `Name`s, a blank `Name`, or a `PrimaryStrategy` not present in
  `Strategies`. Each of these otherwise surfaces later as a confusing empty or mislabelled series.

### 2. Building N engines

Introduce a factory that produces one configured `ScoringEngine` per strategy. Keep layering clean
(CLAUDE.md): the factory interface and implementation live in `Radar.Application` alongside the engine;
**`IConfiguration` must not leak into Application.** Bind the named profiles at composition time in
`Radar.Infrastructure` into a resolved map (e.g. `IReadOnlyDictionary<string, ScoringWeights>`) that the
factory consumes.

`IScoringEngine` itself is **unchanged** — `ScoreCompanyAsync(companyId, windowEndUtc, ct)` keeps its
signature.

### 3. Pipeline loop

`RadarPipelineRunner` stage 6 (~lines 355-366) becomes nested: **for each strategy → for each company**.

- The strategy loop starts **after** signal persistence. Collection, the AI directional read, extraction,
  resolution and review are **shared** and must run exactly once. If anything above the scoring stage runs
  per strategy, the slice is wrong — the whole point is one collection pass.
- `_scoringConfigStore.WriteAsync` (~line 355) currently writes the effective config once; it must write
  one record per strategy.
- The run record should list the strategies that ran, alongside the collectors.

### 4. Snapshot identity

Add a human-readable `StrategyName` to the score snapshot, **alongside** the existing opaque
`ScoringConfigVersion` (fingerprints are not readable and two strategies could in principle share a
config). In the persisted file DTO make it **trailing + nullable**, exactly as `ScoringConfigVersion` was
added in `FileScoreSnapshotStore` (`FileScoreSnapshotStore.cs:295-297`), so pre-existing snapshot files
deserialise cleanly. Treat a **null `StrategyName` as the primary/legacy strategy**.

### 5. Storage layout — zero disruption to existing readers

- **The primary strategy writes to the existing location, unchanged.** This keeps the spec-101/108 efficacy
  read, the weekly report and all accrued history working with no migration and no path fallback logic.
- **Non-primary strategies write to a strategy-scoped location** (e.g. a `strategies/{name}/` segment under
  the scores directory).

This is deliberately the low-risk option. Unifying the layout so *every* strategy including the primary
lives under a strategy segment is recorded as a follow-up — it needs a reader fallback for legacy files and
buys nothing yet.

### 6. Reporting

The weekly report renders the **primary** strategy only. Because the primary writes to the existing
location, the report builder should need **no change at all** — verify this rather than assume it. Rendering
per-strategy reports is out of scope.

## ⚠️ Known coupling this slice does NOT fix (read before planning the next one)

`SignalSourceDescriptor` folds **both** the enabled collector set **and** the extractor rule-set identity
into the fingerprint. With collection shared across strategies, the collector set describes the *evidence*,
not the *strategy* — so **enabling a collector still re-stamps every strategy's fingerprint simultaneously
and resets all series at once.** That is precisely the coupling this arc exists to remove, and this slice
does not remove it.

It is deliberately deferred because fixing it *moves the fingerprint*, which would break this slice's
byte-identical guarantee. **Recommend it as the immediate next slice**, before long series accrue — the
migration cost only grows. Splitting *data provenance* from *strategy identity* is cheap now and expensive
after months of snapshots.

## Assignment

Worktree: any. Files: `Radar.Application/Scoring/` (new factory + strategy descriptor), `RadarPipelineRunner`
stage 6, the score snapshot record + `FileScoreSnapshotStore`, `InfrastructureServiceCollectionExtensions`
(profile map + fail-fast validation), `appsettings.json`, and tests.
Dependencies: **spec 136 merged** — it touches `ScoringEngine`'s read paths and `ISignalFileStore`; land it
first to avoid a conflict. Not a functional dependency.
Estimated time: ~3–4 h.

## Tests

- **Byte-identical default (the critical one):** with no `Radar:Strategies` configured, a run produces the
  same snapshots, same `ScoringConfigVersion`, same storage paths and the same report as before.
  `ScoringConfigFingerprintTests` green **unmodified, no pin edit** — AI-OFF `radar-scoring-fp-6b2f468041b9`
  / AI-ON `radar-scoring-fp-57356123e09b`.
- Two strategies over one run produce **two independent snapshot sets** with distinct `StrategyName` and
  distinct `ScoringConfigVersion`, and neither overwrites the other.
- **Collection runs exactly once** regardless of strategy count — assert collector invocation counts and
  that the AI directional source is invoked once, not N times.
- The primary strategy's snapshots land in the **legacy path**; non-primary ones do not.
- A pre-existing snapshot file with **no `StrategyName`** deserialises and reads as the primary strategy.
- Fail-fast: unknown `ScoringProfile`, duplicate `Name`, blank `Name`, `PrimaryStrategy` absent from
  `Strategies` — each throws at startup with a message naming the offending config key.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **No formula, rule, weight, tier, enum or collector-set change. No fingerprint move for the default
  single-strategy config.** If a fingerprint pin needs editing, the change has leaked scope.
- **One collection pass.** Nothing above the scoring stage may run per strategy.
- **Layering:** no `IConfiguration` in `Radar.Application`; the factory consumes an already-resolved map.
- Provenance intact: every snapshot keeps its `ScoreEvidenceLink` chain, per strategy.

## Out of scope (record, do not build)

- **Splitting data provenance from strategy identity in the fingerprint** — the known coupling above.
  Recommended as the next slice.
- **Per-strategy signal-type filtering** (a strategy declaring which `SignalType`s it consumes). That is the
  genuinely new capability and lands after the fingerprint split, since it changes what a strategy hashes.
- **Replay across historical as-of dates** — depends on spec 136.
- **Strategy-vs-price comparison** — the eventual payoff; AD-14 keeps price validation-only, and the
  hold-out discipline against multiple-comparisons overfitting belongs in that slice's design from the start.
- **Unifying the storage layout** so the primary also lives under a strategy segment.
- **Reading signals once and scoring N times.** The current-window and previous-window reads are
  strategy-independent until signal-type filtering exists, so N strategies currently repeat them. At 43
  companies this is negligible; optimise only if it ever shows up.

## Acceptance criteria

- [ ] `Radar:Strategies` + `Radar:PrimaryStrategy` are bound; an absent/empty list synthesises the single
      current-profile strategy and behaviour is **byte-identical to today**.
- [ ] One `ScoringEngine` is constructed per strategy via a factory; `IScoringEngine`'s signature is
      unchanged and the scoring core is untouched.
- [ ] Stage 6 iterates strategies × companies; **collection, AI read, extraction, resolution and review run
      exactly once**, asserted by test.
- [ ] Snapshots carry `StrategyName` (trailing + nullable in the file DTO; null ⇒ primary/legacy).
- [ ] Primary writes to the existing location; non-primary strategies are strategy-scoped and do not collide.
- [ ] The weekly report renders the primary strategy, with no report-builder change required.
- [ ] Fail-fast validation on unknown profile / duplicate name / blank name / primary-not-in-set.
- [ ] **Default fingerprints byte-identical; `ScoringConfigFingerprintTests` green with no pin edit.**
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
