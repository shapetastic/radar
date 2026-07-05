# Task: Fold the enabled signal-source set into the scoring-config fingerprint

## Overview

`CompanyScoreSnapshot.ScoringConfigVersion` is an auto-derived content fingerprint (spec 89/91, AD-10 as
amended) computed by `ScoringConfigFingerprint.Compute(...)` over the **structure identity** (engine version +
`_formula.Version`), **every `ScoringWeights` value**, and the **attention tier-map descriptor**
(`IAttentionSourceWeights.CanonicalDescriptor()`). It gates the spec-69 cross-run comparability clause and the
`Thesis improving` / `Thesis deteriorating` labels: two snapshots are compared only when their
`ScoringConfigVersion` values are non-null and equal.

The fingerprint does **not** include the **enabled evidence-collector set** (nor the deterministic
extractor's rule identity). So enabling/disabling a collector changes scoring **output** while leaving the
stamp **unchanged**. The 2026-07-05 live re-measure exposed this concretely: promoting the `secform4`
collector (spec 93) into the baseline adds directional `InsiderBuying` signals that move `TrajectoryScore`,
yet a run *with* insider signals and a run *without* them carry the **same** fingerprint and are therefore
**falsely judged comparable** — the exact defect spec 69 exists to prevent (a scoring-affecting change must
never silently fabricate a thesis-trajectory delta). Spec 93 explicitly deferred this as a follow-up
("Folding the enabled-collector set (and/or the extractor-rule identity) into the fingerprint is a bigger,
separate cross-collector decision").

This slice closes that gap: it folds a canonical **signal-source descriptor** — the enabled collector-name
set plus the deterministic extractor's rule-set identity — into the fingerprint input, so the stamp
re-derives automatically when the signal-production surface changes. The self-verifying content-fingerprint
property (AD-10) is **preserved and strengthened**: no new hand-bumped constant gates comparability; the
descriptor is derived from the composed graph. This is the **first** of two sequenced slices; spec 96 (move
the insider materiality tiers to config) builds on the fingerprint plumbing added here.

---

## Assignment

Worktree: any — additive Application seam + a new field on `ScoringConfigFingerprint.Compute` /
`EffectiveScoringConfig` / `ScoringEngine`, plus a DI registration and a rule-set-version constant on the
extractor. It **edits `ScoringEngine.cs`, `ScoringConfigFingerprint.cs`, `EffectiveScoringConfig.cs`,
`KeywordSignalExtractor.cs`, `InfrastructureServiceCollectionExtensions.cs`** and repins the fingerprint
tests, so it must **NOT** run in parallel with any scoring/extractor/fingerprint slice (including spec 96,
which depends on this one).
Dependencies: 89 (config-driven `ScoringWeights` + the derived fingerprint), 91 (content-addressed effective
config store), 93 (the `secform4` collector this gap was exposed by) — all merged.
Conflicts with: spec 96 (sequence AFTER this); any slice touching `ScoringConfigFingerprint` /
`EffectiveScoringConfig` / `ScoringEngine` / `KeywordSignalExtractor`.
Estimated time: ~1.5–2 h

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  ISignalSourceDescriptor.cs        # NEW: CanonicalDescriptor() over the enabled collector set + extractor rule identity
  SignalSourceDescriptor.cs         # NEW: default impl — distinct CollectorNames (sorted ordinal) + rule-set version, canonicalized (AD-3)
  ScoringConfigFingerprint.cs       # MODIFIED: add a signalSourceDescriptor field to Compute(...)
  EffectiveScoringConfig.cs         # MODIFIED: add the SignalSourceDescriptor field (preserve the self-verification invariant)
  ScoringEngine.cs                  # MODIFIED: inject ISignalSourceDescriptor, fold its descriptor into the fingerprint + EffectiveConfig

src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs         # MODIFIED: add a public const RuleSetVersion (the deterministic extractor's rule identity)

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: register ISignalSourceDescriptor in AddRadarApplicationServices (TryAddSingleton)

docs/architecture-decisions.md      # MODIFIED: amend AD-10 (fingerprint now folds the signal-source set)
CLAUDE.md                           # MODIFIED: extend the spec-implementation checklist note (rule-set version obligation)

tests/Radar.Application.Tests/Scoring/
  ScoringConfigFingerprintTests.cs  # MODIFIED: new signalSourceDescriptor arg; repin default; changed-source-set-changes-fingerprint case
  SignalSourceDescriptorTests.cs    # NEW: canonical/deterministic/culture-invariant descriptor over collector sets + rule version
  ScoringEngineTests.cs (or equiv)  # MODIFIED IF it pins a fingerprint / EffectiveConfig shape
```

---

## Implementation details

### `ISignalSourceDescriptor` (Application, `Radar.Application.Scoring`)

Mirror the `IAttentionSourceWeights.CanonicalDescriptor()` precedent exactly — a read-only, deterministic
descriptor consumed only for fingerprinting:

```csharp
namespace Radar.Application.Scoring;

/// <summary>
/// Canonical, deterministic descriptor of the run's SIGNAL-PRODUCTION surface — the enabled
/// evidence-collector set plus the deterministic extractor's rule-set identity — folded into the
/// <c>ScoringConfigVersion</c> content fingerprint (AD-10) so that enabling/disabling a collector (or
/// changing the extractor rule set) re-stamps the scoring generation. Two runs whose signal-production
/// surface differs must NOT be judged comparable by the spec-69 gate. Deterministic: stable ordering,
/// culture-invariant, no clock/IO/randomness (AD-3).
/// </summary>
public interface ISignalSourceDescriptor
{
    string CanonicalDescriptor();
}
```

### `SignalSourceDescriptor` (default impl, Application)

- Constructor injects `IEnumerable<IEvidenceCollector>` (the same collection the runner composes) and reads
  the extractor rule identity from the const below. **Read only `CollectorName`** — never call
  `CollectAsync`; the descriptor must have zero collection side effects.
- Build the canonical string ONCE at construction (immutable field), so it stays a pure function:
  `rules={ruleSetVersion};collectors={csv};` where `csv` = the **distinct** `CollectorName`s, ordered
  `Ordinal`, joined by `,`. De-dupe defensively (a mis-registration listing a collector twice must not change
  the descriptor). Collector names are a controlled, delimiter-free vocabulary
  (`rss`/`sec`/`sec-form4`/`usaspending`/`news`/`newssearch`/`localfile`); still, keep the serialization
  injective — if any name could contain a reserved delimiter, escape it the way
  `ConfiguredAttentionSourceWeights.Escape` does (percent-escape `%`, `=`, `;`, `,`). A comment noting the
  names are delimiter-free today is sufficient if you choose not to escape.
- The **rule-set identity**: add a `public const string RuleSetVersion = "radar-keyword-rules-v1";` to
  `KeywordSignalExtractor` (both types live in `Radar.Application`, so referencing it directly introduces no
  layering issue). The descriptor takes it as `KeywordSignalExtractor.RuleSetVersion`. This captures the
  deterministic extractor's rule *identity* (the phrase→direction/strength table shape); it is bumped by a
  human when the rule table changes in a scoring-affecting way, exactly as `_formula.Version` is bumped for a
  formula-shape change (AD-6). NOTE for spec 96: once the insider **magnitudes** move to config they will be
  hashed by *value* and no longer require a `RuleSetVersion` bump — only rule *structure* changes will.

### `ScoringConfigFingerprint.Compute`

- Add a `string signalSourceDescriptor` parameter (null-guarded) and `Append` it as a new field **after**
  `attnDesc` (e.g. key `srcDesc`). Appending keeps the existing field ordering stable; the default fingerprint
  value changes (expected — repin below). Do not reorder existing fields.

### `EffectiveScoringConfig`

- Add a `string SignalSourceDescriptor` record field. Update the class doc: the fingerprint now hashes
  engine + formula + weights + attention descriptor **+ the signal-source descriptor**, and the store's
  self-verification invariant (recompute the fingerprint from the stored fields == the filename) MUST still
  hold — so the persisted config carries this field verbatim. No change to `FileScoringConfigStore` logic is
  needed (it serializes the whole record), but confirm the added field round-trips.

### `ScoringEngine`

- Inject `ISignalSourceDescriptor sourceDescriptor` (null-guarded) alongside the existing `IAttentionSourceWeights`.
- In the constructor, compute `var signalSourceDescriptor = sourceDescriptor.CanonicalDescriptor();` and pass
  it to both `ScoringConfigFingerprint.Compute(...)` and the `EffectiveScoringConfig` projection so
  `EffectiveConfig.Fingerprint` still equals the stamp on every snapshot. No change to the scoring math,
  windowing, provenance, or `ScoringVersion` (structure identity `EngineVersion+_formula.Version`) — only the
  fingerprint input widens.

### DI

- In `AddRadarApplicationServices`, register `services.TryAddSingleton<ISignalSourceDescriptor, SignalSourceDescriptor>();`.
  DI resolves `IEnumerable<IEvidenceCollector>` at resolution time, so the descriptor sees **all** collectors
  regardless of the fact that `RadarWorkerServices` registers collectors AFTER `AddRadarApplicationServices`
  (verify with a Worker-graph test). `TryAdd` keeps a composition root free to substitute its own descriptor.

### Ledger + checklist

- Amend **AD-10** in `docs/architecture-decisions.md`: the derived fingerprint now folds the enabled
  signal-source set (collector names + extractor rule-set identity) in addition to structure + weights +
  attention descriptor; enabling/disabling a collector re-stamps automatically, restoring the spec-69
  comparability guarantee across a collector-set transition. Record the new default-fingerprint value.
- Extend the `CLAUDE.md` spec-implementation checklist: a scoring-affecting **extractor rule-structure**
  change bumps `KeywordSignalExtractor.RuleSetVersion` (parallel to `_formula.Version`); the enabled-collector
  set is captured automatically.

---

## Tests

- `SignalSourceDescriptorTests`: same collector set ⇒ same descriptor; different collector set (add/remove a
  collector) ⇒ different descriptor; duplicate `CollectorName` registration ⇒ unchanged descriptor; order of
  registration does not matter (sorted `Ordinal`); the descriptor is culture-invariant and contains the
  `RuleSetVersion`; empty collector set yields a stable `rules=...;collectors=;`.
- `ScoringConfigFingerprintTests` (MODIFIED): thread the new `signalSourceDescriptor` arg through every case;
  add a `Compute_ChangedSignalSourceDescriptor_ChangesFingerprint` case; **repin**
  `Compute_DefaultConfig_MatchesPinnedFingerprint` to the recomputed hex (pass an explicit representative
  descriptor literal built from the concrete `IEvidenceCollector.CollectorName` values the default DI graph
  registers — NOT the `Radar:Collectors` config kinds — i.e. the baseline
  `rules=radar-keyword-rules-v1;collectors=RssPressReleaseCollector,newssearch,sec-edgar,sec-form4,usaspending;`
  (`rss`→`RssPressReleaseCollector`, `sec`→`sec-edgar`) — recompute and pin the resulting
  `radar-scoring-fp-…` value; do not guess it). Update the pin comment to
  note the signal-source descriptor is now hashed. Keep the culture-invariance and lowercase-hex-shape
  assertions.
- Worker/DI graph test: resolving `ISignalSourceDescriptor` (or `IScoringEngine`) from the fully-composed
  Worker graph sees the configured collector set (e.g. the baseline `["rss","sec","usaspending","newssearch","secform4"]`),
  proving registration order does not drop late-registered collectors.
- Any existing test that pins the default fingerprint value, or asserts the `EffectiveScoringConfig` shape /
  `ScoringEngine.EffectiveConfig`, is updated (grep the repo for `radar-scoring-fp-5cd50423f408` and
  `EffectiveScoringConfig`). Snapshots still stamp `ScoringConfigVersion == EffectiveConfig.Fingerprint`.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic, culture-invariant, no clock/IO in the descriptor (AD-3).
- Layering (AD-5): `ISignalSourceDescriptor` + its default impl live in `Radar.Application` and depend only
  on Application types (`IEvidenceCollector`, `KeywordSignalExtractor`). No Infrastructure/provider leakage.
- Preserve provenance and the self-verification invariant: `EffectiveConfig.Fingerprint` == every snapshot's
  `ScoringConfigVersion`, and recomputing the fingerprint from the stored `EffectiveScoringConfig` fields
  equals the content-addressed filename (AD-10/spec 91).
- No scoring **math** change: `ScoringVersion`, the formula, windowing, contributions/links are byte-for-byte
  unchanged. Only the fingerprint *input* widens (the default fingerprint value re-stamps — expected).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Out of scope (note explicitly)

- **Moving the insider (or GovernmentContract) materiality tiers to config** — that is spec 96, which folds
  their *values* into the fingerprint on top of this plumbing.
- **Hashing per-collector option values** (e.g. `MaxFilingsPerCompany`, lookback windows) into the
  fingerprint — the enabled-collector *set* is the correctness gap here; option-value sensitivity is a
  possible future refinement, out of scope.
- **A run-record pointer** from snapshot to collector set — the fingerprint already captures it.

## Acceptance criteria

- [ ] `ScoringConfigFingerprint.Compute` folds a `signalSourceDescriptor` field; enabling/disabling a
      collector (or bumping `KeywordSignalExtractor.RuleSetVersion`) changes `ScoringConfigVersion`
      automatically.
- [ ] `ISignalSourceDescriptor` + `SignalSourceDescriptor` produce a deterministic, culture-invariant,
      duplicate-safe, registration-order-independent descriptor from the enabled `IEvidenceCollector` set +
      the extractor rule-set version; the descriptor never triggers collection.
- [ ] `EffectiveScoringConfig` carries the signal-source descriptor; the store self-verification invariant
      still holds (`EffectiveConfig.Fingerprint` == snapshot `ScoringConfigVersion` == `{fingerprint}.json`
      filename); recompute-from-stored equals the filename.
- [ ] `ScoringEngine` injects `ISignalSourceDescriptor` and folds it into both the fingerprint and
      `EffectiveConfig`; scoring math / `ScoringVersion` / provenance unchanged.
- [ ] Registered in `AddRadarApplicationServices`; the fully-composed Worker graph resolves it and sees the
      configured collector set despite collectors being registered later.
- [ ] Fingerprint tests repinned to the recomputed default; AD-10 amended and the CLAUDE.md checklist
      extended. `dotnet build` / `dotnet test` on `Radar.sln -c Release` green.
