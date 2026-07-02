# Task: Only diff cross-run scores (and emit thesis improving/deteriorating) when the two snapshots share the same scoring behaviour

## Overview

The weekly report currently emits a **false, misleading week-over-week story** whenever Radar's own
scoring logic changes between two runs. For a research tool whose entire promise is "signals before
stories, evidence before opinions," a spurious `Thesis deteriorating` label is exactly the wrong
output: it tells a human a company's real-world trajectory worsened when nothing about the company
changed — only *our* code did.

This slice makes a cross-run delta clause **and** the improving/deteriorating action label fire **only
when the current and previous snapshots were produced by the same scoring behaviour** (formula +
extraction rules + materiality tiers + scoring options). When they were not, the report must say so
honestly — render `(scoring updated)` instead of a fabricated numeric delta, and the action policy must
fall back to its no-previous behaviour (never `Thesis improving` / `Thesis deteriorating`).

Scope is **a scoring-config/generation stamp on the snapshot + comparability-gating in the builder,
renderer, and action-policy context + serialization back-compat + tests**. It does **not** change any
score value, the formula math, extraction, or the current snapshot's evidence provenance.

---

## The defect (confirmed by a live 2026-07-02 run + code reading — state it precisely)

- Spec 60 renders a week-over-week movement clause on each company's score line
  (`MarkdownWeeklyReportRenderer.FormatMovement`), and spec 65 made `previous` real across runs by
  sourcing it from `FileScoreSnapshotStore.ReadLatestBeforeAsync` in `WeeklyReportBuilder`
  (`previous = await _scoreSnapshotFileStore.ReadLatestBeforeAsync(...)`, ~line 195). The same
  `previous` is handed to `WeeklyReportActionPolicyV1` via
  `_policy.Decide(new ReportActionContext(c.Current, previous))` (~line 199), where a trajectory drop of
  `>= 5` versus `previous` yields `RadarReportAction.ThesisDeteriorating`.
- Between the two live runs, **spec 66 (government-contract materiality) shipped.** It changed how
  `GovernmentContract` signal **Strength** is computed inside `KeywordSignalExtractor` (award-amount
  tiers instead of a flat Strength 6). Because Strength feeds `radar-formula-v2` `TrajectoryScore` (the
  confidence/recency-weighted mean of directional strength, AD-6), Mercury Systems' Trajectory dropped
  **80 → 75**. The report rendered `(Opportunity -3, Trajectory -5 vs last run)` and the policy emitted
  **`Thesis deteriorating`**.
- **That movement is a scoring-logic artifact, not a real-world change.** The two snapshots are not
  comparable — they were produced by different scoring behaviour — so diffing them and labelling the
  company on that diff is misleading a human. This is the defect.

### Why the obvious fix does NOT work — you cannot gate on `ScoringVersion`

`CompanyScoreSnapshot.ScoringVersion` is stamped in `ScoringEngine.ScoreCompanyAsync` as
`$"{EngineVersion}+{_formula.Version}"` = **`"mvp-engine-v1+radar-formula-v2"`**
(`src/Radar.Application/Scoring/ScoringEngine.cs:114`, with `EngineVersion = "mvp-engine-v1"` and
`RadarScoreFormulaV2.Version => "radar-formula-v2"`). Spec 66 changed the **extractor**
(`KeywordSignalExtractor`), **not** the engine version and **not** the formula
`Version` — verify by reading `RadarScoreFormulaV2.Version` (unchanged at `"radar-formula-v2"`) and
confirming spec 66 (`docs/66-government-contract-materiality.md`) never bumps a version. Therefore
**both** the pre- and post-materiality snapshots are stamped the identical `ScoringVersion`
`"mvp-engine-v1+radar-formula-v2"`. A `ScoringVersion`-equality guard would still treat them as
comparable and **still** emit the false label. The stamp we gate on must capture the *whole*
scoring-affecting generation, not just the formula/engine identity.

---

## Design — recommended approach

### Option A (RECOMMENDED): a scoring-config/generation stamp on the snapshot

Add a field to `CompanyScoreSnapshot` — **`ScoringConfigVersion`** (nullable string) — **distinct from
the formula/engine `ScoringVersion`** — that identifies the whole scoring-affecting pipeline generation
(formula + extractor rules + materiality tiers + scoring options). Stamp it when the snapshot is built,
and gate **both** the delta clause **and** the policy's improving/deteriorating branch on this being
present and equal between `current` and `previous`.

Retroactive behaviour falls out for free: existing on-disk snapshot files lack the field, so they
deserialize to **null** and are treated as **not comparable**. The first comparison after this deploy
therefore correctly renders `(scoring updated)` and emits no improving/deteriorating label — which is
exactly what should have happened for Mercury.

**Manually-bumped constant vs auto-computed fingerprint — RECOMMENDATION: a manually-bumped constant.**

Use a single, code-visible constant owned by `ScoringEngine`, e.g.
`private const string ScoringConfigVersion = "radar-scoring-config-v1";`, stamped onto every snapshot,
with a **documented convention: bump it on ANY scoring-affecting change** (formula, extractor rules,
materiality tiers, `ScoringOptions`). Reasoning for choosing the manual constant over the
auto-fingerprint here:

1. **It stays inside the maintainer's stated conflict surface.** This slice is scoped to the snapshot,
   builder, renderer, policy, file store, and scoring engine — **not the extractor**. An auto-computed
   fingerprint of the *extraction rules / materiality tiers* would have to reach into
   `KeywordSignalExtractor` (to expose a rules/tier fingerprint) and wire it across two independent
   pipeline stages into `ScoringEngine`. That is new cross-stage coupling and edits outside this slice's
   surface — reject it here.
2. **The auto-fingerprint's robustness is largely illusory unless it hashes the actual rule/tier
   contents.** A "version tag on the rule table" is itself a manual bump (same failure mode). Hashing
   the real contents is genuinely automatic but adds the coupling in (1) and can flip
   *spuriously* on behaviour-neutral refactors (reordering the rules array) — showing `(scoring updated)`
   when nothing really changed.
3. **The failure-mode asymmetry is acceptable and process-mitigated.** A forgotten bump re-creates this
   exact bug (unsafe direction), so the risk is real — but it is mitigated by making the obligation
   explicit where none existed before: record the convention in `docs/architecture-decisions.md` and add
   a spec-implementation-checklist item, so the *next* scoring-affecting change has a single, documented
   place to bump. (The reason spec 66 didn't bump anything is that there was no such concept or
   convention at all.)

The constant must be a **code constant** (not a `ScoringOptions`/config value): bumping it should require
a code edit that trips the checklist, and it must move in lockstep with code, never be ops-tunable.

> Follow-up graduation (out of scope, note in the PR): if a *second* scoring-affecting change ever slips
> without a bump, graduate to an auto-computed fingerprint that hashes the real formula version + a
> content fingerprint the extractor exposes + serialized `ScoringOptions`. Do not pre-build it.

### Option B (considered, REJECTED for the MVP): re-score the previous window under current logic

Re-run the current scoring behaviour over the previous window's signals/evidence before comparing
(apples-to-apples backfill). This is the "most correct" answer but is heavier: it depends on reloading
the prior window's signals *and* evidence back into the pipeline (which spec 65 explicitly deferred as a
separate future slice), re-running extraction+scoring offline, and reconciling determinism across runs.
It is out of scope for a correctness hotfix. **Reject for this slice**; Option A gives an honest,
cheap, and sufficient result now.

---

## Assignment

Worktree: any
Dependencies: 60 (delta clause), 65 (cross-run read-back of `previous`), 66 (materiality — the change
that exposed the defect) — all merged.
Conflicts with: touches `CompanyScoreSnapshot` (Domain), `WeeklyReportBuilder`, `MarkdownWeeklyReportRenderer`,
`WeeklyReportActionPolicyV1` / `ReportActionContext`, `FileScoreSnapshotStore`, and `ScoringEngine` — plus
their tests. **Must NOT run in parallel with any other report/scoring/score-store slice; sequence it.**
Estimated time: ~2–3 h

---

## Project structure changes

```text
src/Radar.Domain/Scoring/
  CompanyScoreSnapshot.cs              # MODIFIED: add `string? ScoringConfigVersion` (nullable; null = unknown/pre-stamp generation)

src/Radar.Application/Scoring/
  ScoringEngine.cs                     # MODIFIED: define ScoringConfigVersion constant; stamp it on the snapshot

src/Radar.Application/Reporting/
  ReportActionContext.cs               # MODIFIED: add `bool PreviousComparable = true` (defaulted for back-compat)
  WeeklyReportActionPolicyV1.cs        # MODIFIED: only take the improving/deteriorating branch when Previous is not null AND PreviousComparable
  WeeklyReportEntry.cs                 # MODIFIED: add `bool PreviousScoringChanged = false` (previous existed but was incomparable)
  WeeklyReportBuilder.cs               # MODIFIED: compute comparability once; gate the policy context and the entry's previous-score fields on it
  MarkdownWeeklyReportRenderer.cs      # MODIFIED: FormatMovement gains a `(scoring updated)` state

src/Radar.Infrastructure/FileSystem/
  FileScoreSnapshotStore.cs            # MODIFIED: serialize/deserialize ScoringConfigVersion; old files (missing field) -> null on read-back

tests/Radar.Application.Tests/...      # MODIFIED/NEW: builder + policy + renderer + engine comparability cases
tests/Radar.Infrastructure.Tests/...   # MODIFIED/NEW: FileScoreSnapshotStore back-compat + round-trip of the new field
```

---

## Implementation details

### 1. Domain — add the generation stamp (Domain field stays pure, AD-5)

Add a nullable field to `CompanyScoreSnapshot` (append it so positional-record call sites are updated
deliberately):

```csharp
public sealed record CompanyScoreSnapshot(
    Guid Id,
    Guid CompanyId,
    string ScoringVersion,
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore,
    string Explanation,
    string ComponentJson,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset CreatedAtUtc,
    string? ScoringConfigVersion = null);   // NEW: whole-generation stamp; null = unknown/pre-stamp -> never comparable
```

`ScoringConfigVersion` is **nullable** on purpose: `null` is the honest representation of "we don't know
this snapshot's scoring generation" (an old on-disk file, or any snapshot written before this field
existed), and null must always sort to "not comparable." A defaulted trailing parameter keeps most
existing construction sites compiling; update the ones that need a real value (the engine + tests).

Document in an XML/`<remarks>` comment that this is **distinct from `ScoringVersion`**: `ScoringVersion`
identifies the engine+formula identity, whereas `ScoringConfigVersion` identifies the whole
scoring-affecting generation (formula + extractor rules + materiality tiers + scoring options), and only
this field gates cross-run comparability.

### 2. ScoringEngine — stamp the generation

- Add a **code constant**, e.g.
  `private const string ScoringConfigVersion = "radar-scoring-config-v1";`, with a comment stating the
  convention: **bump on ANY scoring-affecting change** (formula, extractor rules, materiality tiers,
  `ScoringOptions`). Since this deploy ships after spec 66, `v1` is the first stamped generation.
- Pass it into the `new CompanyScoreSnapshot(...)` construction (the `ScoringConfigVersion:` argument).
- Do **not** change any score value, `ScoringVersion`, the window, contributions, or provenance.

### 3. Comparability gate in the builder (compute once)

In `WeeklyReportBuilder`, after `previous` is read from the file store (~line 195):

```csharp
// Two snapshots are comparable only when they were produced by the SAME scoring generation.
// A null stamp (old on-disk snapshot, or any pre-stamp snapshot) is never comparable.
var comparable =
    previous is not null
    && !string.IsNullOrEmpty(c.Current.ScoringConfigVersion)
    && string.Equals(
        c.Current.ScoringConfigVersion, previous.ScoringConfigVersion, StringComparison.Ordinal);
```

Then:

- **Action policy:** pass comparability explicitly —
  `_policy.Decide(new ReportActionContext(c.Current, previous, PreviousComparable: comparable))`.
  (Keep handing `previous` through so a future policy could describe "scoring changed"; the policy
  itself must not act on an incomparable previous — see step 4.)
- **Entry previous-score fields (spec 60):** populate `PreviousOpportunityScore` /
  `PreviousTrajectoryScore` **only when `comparable`** (else leave them null), and set
  `PreviousScoringChanged: previous is not null && !comparable`. Concretely:

```csharp
PreviousOpportunityScore: comparable ? previous!.OpportunityScore : (int?)null,
PreviousTrajectoryScore:  comparable ? previous!.TrajectoryScore  : (int?)null,
PreviousScoringChanged:   previous is not null && !comparable,
```

`current` and its `ScoreEvidenceLink`s still come from this run's in-memory repo — **provenance
unchanged**. The read-back still supplies only the previous snapshot's scalar fields (now including its
`ScoringConfigVersion`).

### 4. Action policy — do not label off an incomparable previous

- Add `bool PreviousComparable = true` to `ReportActionContext` (defaulted `true` so existing
  `new ReportActionContext(current, previous)` call sites and tests compile and behave unchanged).
- In `WeeklyReportActionPolicyV1.Decide`, change the improving/deteriorating guard from
  `if (previous is not null)` to `if (previous is not null && context.PreviousComparable)`. When the
  previous is incomparable, the method **falls through to its steady-state (no-previous) branch** —
  Investigate / Watch / Ignore / NeedsMoreEvidence — and never emits `ThesisImproving` /
  `ThesisDeteriorating`. No other policy logic changes; the thin-evidence rule (rule 1) still runs first.

### 5. Renderer — the `(scoring updated)` state

In `MarkdownWeeklyReportRenderer.FormatMovement`, add the new state **before** the existing
previous-score check, and keep everything else byte-identical:

- If `entry.PreviousScoringChanged` is `true` → return `" (scoring updated)"`. (Do NOT render a numeric
  delta and do NOT render `(first snapshot)` — `(first snapshot)` would be misleading, since a prior
  snapshot *does* exist; it is just not comparable.)
- Else if both previous scores are present → the existing delta / `(no change vs last run)` clause.
- Else → the existing `" (first snapshot)"`.

The renderer stays pure/deterministic (no clock, no I/O); ASCII-only, invariant culture, `\n` endings.
No new label, no advice language — `(scoring updated)` is descriptive metadata, exactly like the
existing movement clauses.

### 6. Serialization back-compat (Infrastructure, AD-5)

In `FileScoreSnapshotStore`:

- Add `string? ScoringConfigVersion` to the private `ScoreSnapshotFile` record.
- `Serialize(...)` writes `ScoringConfigVersion: snapshot.ScoringConfigVersion`.
- `ReadLatestBeforeAsync` reconstruction maps `ScoringConfigVersion: parsed.ScoringConfigVersion` onto
  the rebuilt `CompanyScoreSnapshot`.
- **Old files lack the property.** With the existing `RadarFileStoreJson.Options`, a missing JSON member
  deserializes to the record's default (`null`) — confirm the options do not reject missing/unknown
  members (default System.Text.Json tolerates them). A null `ScoringConfigVersion` flows through as "not
  comparable," which is the required retroactive behaviour. Add a comment noting old-format files
  intentionally read back as null → `(scoring updated)`, and that this is NOT dropped provenance (the
  current report's links are unchanged).

---

## Tests

1. **Same generation → real delta + label may fire.** Two snapshots for one company with the **same**
   `ScoringConfigVersion` (previous on disk in a real `FileScoreSnapshotStore` over a temp dir, or a fake
   store returning it). The built entry carries non-null `PreviousOpportunityScore` /
   `PreviousTrajectoryScore`; the renderer emits the numeric `(… vs last run)` clause; and a trajectory
   drop `>= 5` between comparable snapshots yields `ThesisDeteriorating` (policy improving/deteriorating
   path is live).
2. **Different generation → `(scoring updated)`, no delta, no thesis label.** Same two snapshots but with
   **different** `ScoringConfigVersion` values (mirrors Mercury: previous `v1`, current `v2`). The entry's
   previous-score fields are null and `PreviousScoringChanged` is true; the renderer emits
   `" (scoring updated)"` and no numeric delta; the policy does **not** emit `ThesisImproving` /
   `ThesisDeteriorating` (it falls back to steady-state). Assert the exact rendered string.
3. **Old on-disk snapshot lacking the field → not comparable, no crash.** Write (or hand-craft) a
   snapshot JSON file **without** a `scoringConfigVersion` property; `ReadLatestBeforeAsync` returns a
   snapshot with `ScoringConfigVersion == null`; the builder treats it as incomparable → entry renders
   `(scoring updated)`; no exception. Also add a round-trip test: a snapshot written WITH the field reads
   back with the same value.
4. **Existing spec-60 / spec-65 tests updated to set matching stamps.** Any builder/renderer test that
   asserts a real numeric delta (or a `Thesis improving/deteriorating` label) must set **matching**
   `ScoringConfigVersion` on both the current snapshot and the on-disk/previous snapshot so it still
   exercises the real-delta path. The renderer's own unit tests for `first snapshot` / `no change` /
   signed-delta remain valid; add one for `(scoring updated)`.
5. **Policy unit tests.** `WeeklyReportActionPolicyV1`: `ReportActionContext` with `PreviousComparable:
   false` (and a previous that would otherwise trip deterioration) returns a steady-state label, never
   `ThesisDeteriorating` / `ThesisImproving`; with `PreviousComparable: true` (default) the existing
   improving/deteriorating behaviour is unchanged.
6. **Engine stamp.** `ScoringEngine` writes snapshots whose `ScoringConfigVersion` equals the current
   constant (non-null), proving new snapshots are always stamped.

---

## Constraints

- Target `net10.0`, C# 14. Layering per AD-5: the new field is a **pure Domain** record field
  (no packages); the comparability logic lives in **Application** (builder/policy/renderer);
  serialization lives in **Infrastructure** (`FileScoreSnapshotStore`). Nothing references Worker.
- **This slice changes only whether a delta/label is shown — never a score value, the formula math,
  extraction, or the current snapshot's evidence provenance.** The current snapshot + its
  `ScoreEvidenceLink`s (score→signal→evidence) still come from this run's in-memory repo, unchanged.
- Serialization back-compat is **required and tested**: existing snapshot files (missing the field)
  read back as `null` → not comparable → `(scoring updated)`, never a crash.
- Deterministic, files-first (AD-8), no AI, no provider SDK, no DB. Renderer stays byte-identical for the
  same model. No advice language; the six AD-9 labels are unchanged (`(scoring updated)` is descriptive
  metadata, not a label).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Docs / ledger follow-up (planner does not edit ADs)

This spec introduces a **scoring-config/generation concept alongside the formula `ScoringVersion`**. AD-6
covers formula versioning but not this whole-generation stamp. Flag as a follow-up (PR note + a proposed
`docs/architecture-decisions.md` entry, and a `CLAUDE.md` spec-implementation-checklist item): **"Any
scoring-affecting change — formula, extractor rules, materiality tiers, or `ScoringOptions` — MUST bump
`ScoringEngine.ScoringConfigVersion`."** The planner does not edit the ledger; the implementing PR should
raise it for the maintainer.

---

## Acceptance criteria

- [ ] `CompanyScoreSnapshot` gains a nullable `ScoringConfigVersion`, distinct from `ScoringVersion`,
      documented as the whole scoring-generation stamp; `null` means "unknown/pre-stamp → never
      comparable."
- [ ] `ScoringEngine` stamps every new snapshot with a code-constant `ScoringConfigVersion` (non-null),
      with a comment stating the "bump on any scoring-affecting change" convention.
- [ ] A cross-run delta clause is rendered **only** when current and previous share a non-null, equal
      `ScoringConfigVersion`; otherwise the renderer emits `(scoring updated)` (not `(first snapshot)`,
      not a numeric delta).
- [ ] `WeeklyReportActionPolicyV1` emits `Thesis improving` / `Thesis deteriorating` **only** when the
      previous snapshot is present **and** comparable (`ReportActionContext.PreviousComparable`);
      otherwise it falls back to its no-previous steady-state behaviour. The Mercury false
      `Thesis deteriorating` no longer occurs across a scoring change.
- [ ] `FileScoreSnapshotStore` serializes and reads back `ScoringConfigVersion`; existing on-disk files
      lacking the field read back as `null` (treated as not comparable) with **no crash** — covered by a
      back-compat test.
- [ ] The current snapshot's scores and its `ScoreEvidenceLink` provenance (score→signal→evidence) are
      unchanged; no score value moves as a result of this slice.
- [ ] Tests cover: same-stamp → real delta + label; different-stamp → `(scoring updated)` + no label;
      old-format file → not comparable, no crash; round-trip of the new field; policy comparability
      gating; engine stamping. Existing spec-60/65/66 tests stay green (delta tests updated to matching
      stamps).
- [ ] Layering (AD-5), determinism, files-first (AD-8) preserved; no advice language; the six AD-9 labels
      unchanged. `dotnet build` / `dotnet test` on `Radar.sln -c Release` green.
```