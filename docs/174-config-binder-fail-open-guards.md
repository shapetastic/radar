# Task: Close the pre-149 config-binder fail-opens — named scoring profiles, insider tiers, media collapse, attention tiers

## Overview

The 2026-08-19 architecture sweep (trunk checkpoint at `ee801cb`) found exactly one material fail-open
(its M-1), and it is a known shape: spec 149 closed "typo'd key silently ignored / mis-shaped section
silently defaulted" for inline `Radar:Strategies[i].Weights` — allowlist + shape guards + bind-failure
rethrow, `InfrastructureServiceCollectionExtensions.cs:462-535` — but the FOUR older binders that predate
it never got the same treatment. All four bind scoring-affecting, fingerprint-hashed inputs, and the named
profile path is the surface `run-radar.ps1` experiments actually use:

1. **`ResolveScoringProfile`** (`InfrastructureServiceCollectionExtensions.cs:220-249`) —
   `section.Get<ScoringWeights>() ?? new ScoringWeights()`. A profile section that exists but is mis-shaped
   (a scalar body) silently binds to **code defaults**, and a typo'd weight key inside a well-formed profile
   (`"MediaReachWieght": 0.1`) is silently ignored by `ConfigurationBinder`. The experiment then runs,
   stamps and gets **ranked** (spec 140) at defaults while reading as tuned. The fingerprint stays honest —
   it hashes resolved values — but the experiment silently measures nothing.
2. **`AddRadarInsiderMateriality` / `BindTiersOrDefault`** (`:813-865`) — same `?? fallback` shape for the
   tier tables: an existing-but-unbindable `BuyTiers`/`SellTiers` section silently reverts to the spec-93
   defaults; a typo'd top-level key (`"ClusterBost"`) or a typo'd tier-entry key is silently ignored.
3. **`AddRadarMediaCollapse`** (`:879-894`) — `section.Get<MediaCollapseOptions>() ?? new …`; a mis-shaped
   `Radar:Scoring:MediaCollapse` section silently keeps the 3-day default; a typo'd key
   (`"EventWindowDay"`) is silently ignored.
4. **The `Radar:Attention` bind** (`RadarWorkerServices.cs:112-114`) —
   `Get<AttentionSourceTierOptions>() ?? AttentionSourceTierOptions.Default`; same shape, and additionally
   it is the ONE scoring-affecting bind living inline in the Worker instead of the single DI home.

Fix: extend the spec-149 guard pattern to all four, extracting ONE shared guard helper rather than pasting
the pattern four times (reuse-over-copy). **For every currently-valid configuration the resolved values are
byte-identical and no fingerprint moves** — the guards reject or accept; they never change what a valid
config resolves to.

## Assignment

Worktree: any
Dependencies: none (main @ ee801cb or later).
Estimated time: ~1.5-2 hours.

## Changes

### 1. Shared guard helper — ONE definition, in the composition root

Add a small private/internal helper in the DI home (`InfrastructureServiceCollectionExtensions.cs`, or a
sibling file in the same namespace if size is a concern — the file is already 2,674 lines) providing the
two guards that are universal across bound option types:

- **Scalar-section shape guard**: a section that `Exists()` but carries a scalar `Value` (e.g.
  `"Profiles": { "low-media": "0.1" }`) fails fast with a message naming the section path and the expected
  shape — mirroring the spec-149 scalar guard at `:472-479`.
- **Unknown-key allowlist**: each immediate child key of the section is checked against the target type's
  public readable+writable instance property names, compared `OrdinalIgnoreCase` — the SAME comparison the
  binder uses, for the reason documented at `:418-423`: the validator must decide exactly what the binder
  decides. Derive the name set by reflection exactly as `ScoringWeightNames` does (`:425-430`); reuse
  `ScoringWeightNames` itself for the `ScoringWeights` call sites rather than deriving it twice. Failure
  names the offending path, the key, and the sorted valid names — same message shape as `:489-493`.

Per-entry value-shape rules are NOT universal (`ScoringWeights` is all-numeric; the insider profile carries
tier LISTS; attention carries a free-keyed DICTIONARY), so those stay per-site (below). Do not build a
generic recursive validator — guard the shapes these four types actually have.

### 2. `ResolveScoringProfile` — the spec-149 guards, verbatim semantics, naming the PROFILE

When the profile section exists:

- scalar body ⇒ fail fast (shape guard);
- unknown child key ⇒ fail fast against `ScoringWeightNames`;
- a known key carrying no scalar number (nested object, array, explicit `null`) ⇒ fail fast — every
  `ScoringWeights` field is a plain number, same rationale as `:496-509`;
- a bind failure (non-numeric value) ⇒ rethrow naming the profile and the requesting config key, binder
  exception as `InnerException` — the profile-path analogue of `:518-535`. Today `Get<ScoringWeights>()`
  throws with the config path but nothing naming which experiment profile is broken.
- The `?? new ScoringWeights()` null-coalesce is then unreachable for any guarded shape — remove it or
  replace with a throw, but keep the one legitimate case: a section that exists with NO children and no
  value (an empty object) still binds to code defaults. An empty profile is an honest "all defaults", not
  a mis-shape.

Both callers (`AddRadarScoringWeights` ambient profile, `AddRadarScoringStrategies` per-strategy
`ScoringProfile`) get the guards automatically because there is one implementation — that is the point of
`ResolveScoringProfile` and it must stay the single shared path. Messages use `requestingConfigKey` so an
ambient failure names `Radar:Scoring:Profile` and a strategy failure names its strategy's key, as today.

### 3. `AddRadarInsiderMateriality` — profile keys, tier tables, tier entries

- Top-level profile keys validated against `InsiderMaterialityWeights`' property names
  (`BuyTiers`, `SellTiers`, `ClusterBoost` — reflection-derived, not a hand-written list).
- `BindTiersOrDefault`: a table section that exists but is scalar ⇒ fail fast; a table section that exists
  with children but where `Get<List<InsiderMaterialityTier>>()` returns null or throws ⇒ fail fast naming
  the table path (an existing-but-unbindable table must NEVER silently revert to the code default — that is
  the exact M-1 shape). Absent table ⇒ fallback, unchanged.
- Each tier entry's keys validated against `InsiderMaterialityTier` (`MinInclusive`, `Strength`); a
  non-numeric tier value fails fast naming the entry path.
- A present-but-unparseable `ClusterBoost` fails fast (verify what `GetValue` actually does on a
  non-numeric value — if it silently returns the default, guard it explicitly; if it throws, wrap with the
  profile named).
- The existing replace-don't-append binding semantics (`:825-836`) and `Validate()` call are untouched.

### 4. `AddRadarMediaCollapse` — smallest site, same guards

Scalar section ⇒ fail fast; unknown key ⇒ fail fast (valid names reflection-derived from
`MediaCollapseOptions`, currently just `EventWindowDays`); non-numeric value ⇒ rethrow naming the section.
Absent section, or present-and-empty ⇒ code defaults, unchanged.

### 5. `Radar:Attention` — guard it AND move it into the DI home

Extract the inline Worker bind into an `AddRadarAttentionTiers(configuration)` extension beside the other
`AddRadarXxx` methods (the DI convention is one home; this is the one scoring-affecting bind outside it),
called from `RadarWorkerServices` at the same position (BEFORE `AddRadarApplicationServices`, comment
preserved). Guards:

- scalar `Radar:Attention` section ⇒ fail fast; unknown top-level key ⇒ fail fast (valid:
  `UnknownWeight`, `SourceTiers`, reflection-derived from `AttentionSourceTierOptions`).
- `SourceTiers` is a DICTIONARY whose keys are free-form tier names — tier names are NOT validated against
  anything. Each tier's VALUE is validated: unknown keys fail fast against `SourceTier`
  (`Weight`, `Publishers`); a scalar where the tier object was meant fails fast; a scalar `Publishers`
  (where an array was meant) fails fast.
- A bind failure ⇒ rethrow naming `Radar:Attention`. The `?? Default` fallback survives ONLY for the
  absent-section case (unchanged behaviour); an existing-but-unbindable section must fail, not default.
- `ConfiguredAttentionSourceWeights`' existing startup validation of bound values is untouched — these
  guards are about SHAPE and KEY validity, which the value validator structurally cannot see.

### 6. What does NOT change

- No new config keys, no renamed keys, no behaviour change for any well-formed configuration.
- No fingerprint input changes: the guards never alter resolved values, so **no pin moves** — the spec-148
  pin table (30d `0c46e07b94db`/`28226897f97b`, 60d `4eb2fe5d3cdf`/`4da4b5ff6ec9`, 120d
  `0a7058d94582`/`81e9fab711f8`) stands untouched, and `ScoringConfigFingerprintTests` is not edited.
- `ScoringWeights.Validate()` / `InsiderMaterialityWeights.Validate()` / `MediaCollapseOptions.Validate()`
  run exactly where they run today — value-range validation and shape/key validation are different layers
  and both remain.
- The spec-149 inline-`Weights` path is not modified (it already has the guards); it may be refactored to
  route through the shared helper ONLY if the produced messages remain byte-identical (its tests pin them).

## Tests

- Per site (all four): a typo'd key fails fast naming the path, the key and the sorted valid names; a
  scalar section fails fast; an existing-but-unbindable section fails fast rather than defaulting; a bind
  failure names the profile/section, binder exception preserved as `InnerException`.
- Insider: a scalar `BuyTiers`, an unknown tier-entry key, and a non-numeric `MinInclusive` each fail
  fast naming the table; an absent table still falls back to the code default.
- Attention: free-form tier names accepted; unknown key inside a tier value rejected; scalar `Publishers`
  rejected; absent section still yields `AttentionSourceTierOptions.Default`.
- Compatibility (the load-bearing ones):
  - absent/empty sections and profiles resolve byte-identically to today (same resolved values,
    same registrations) at every site;
  - a well-formed named profile resolves byte-identically to today;
  - **every shipped `scripts/run-profiles/*.json` overlay (applied onto `default.json` the way
    `run-radar.ps1` composes them) passes the new guards** — the guards must reject typos, not the
    profiles we actually run.
- The `ScoringWeightNames` set is used (not re-derived) for the profile path — asserted or verified by
  inspection in review.

## Constraints

- Composition root only: `IConfiguration` still never reaches `Radar.Application`; no Application or
  Domain file changes.
- Fail-fast messages follow the house shape: name the exact config path, the offending key/value, and the
  remedy. No advice-language concerns (operator-facing config errors).
- No scoring change, no formula/`RuleSetVersion` bump, no new fingerprint input; the pins do not move.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

## Acceptance criteria

- [ ] All four sites reject: scalar-where-object sections, unknown keys (case-insensitive allowlist,
      reflection-derived), and existing-but-unbindable sections — each with the path + key + valid names
      + remedy in the message.
- [ ] `ResolveScoringProfile` guards apply to BOTH callers via the single shared implementation; failures
      name the requesting config key.
- [ ] `Radar:Attention` bind moved into an `AddRadarAttentionTiers` extension in the DI home; free-form
      tier names still accepted; absent section still yields `Default`.
- [ ] One shared guard helper; `ScoringWeightNames` reused, not duplicated; no generic recursive
      validator built.
- [ ] Absent/empty/well-formed configs resolve byte-identically; all shipped run-profiles pass.
- [ ] No fingerprint input changed; pins untouched; `ScoringConfigFingerprintTests` unedited.
- [ ] Build + tests green.
