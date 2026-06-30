# Task: RSS press-release evidence carries a baseline quality (stop defaulting first-party PRs to `Unknown`)

## Overview

The first live end-to-end run (7 real IR feeds, 84 evidence, 33 signals, 7 companies scored) labelled **every**
company "Needs more evidence", even ones with genuine recent customer wins / product launches. Root cause is a
**quality-default chain** that penalises first-party press releases twice:

1. `RssPressReleaseCollector` declares **no** quality, so `CollectedEvidenceMapper.ParseQuality` defaults every
   item to `EvidenceQuality.Unknown` (only the local-file collector reads a declared `quality`; AD-7).
2. `DeterministicSignalReviewer` treats `Unknown` as a **weak source** and applies a `ReduceConfidence` haircut
   — the extractor's `0.6` confidence becomes the `0.30` observed in the score-evidence links.
3. `RadarScoreFormulaV1`'s `EvidenceConfidenceScore` *also* weights `Unknown` quality low (≈0.4 quality factor,
   AD-6).

Penalised twice, `EvidenceConfidenceScore` collapsed to ~17–18 — below the policy's evidence-confidence floor
of 35 — so `WeeklyReportActionPolicyV1` returns "Needs more evidence" for everyone regardless of the actual
news. An official IR-newsroom press release is a **primary source** (the company's own announcement); `Unknown`
is the wrong default for it.

This slice has the RSS collector declare a sensible baseline evidence quality so genuine signals can clear the
floor and reach `Watch`/`Investigate`.

---

## Assignment

Worktree: any
Dependencies: existing trunk (RSS collector + mapper + reviewer + scoring already merged).
Conflicts with: None directly. **Interacts with spec 51 (rule-table tuning):** raising confidence amplifies any
false-positive keyword matches, so 51's precision tightening matters more once this lands. No file overlap
(this touches the collector; 51 touches the extractor) — either order is fine.
Estimated time: ~45 min

---

## Project structure changes

```text
src/Radar.Infrastructure/Rss/
  RssPressReleaseCollector.cs   # MODIFIED: stamp a baseline quality on each CollectedEvidence

tests/Radar.Infrastructure.Tests/Rss/
  RssPressReleaseCollectorTests.cs   # MODIFIED: assert emitted evidence carries the baseline quality
tests/Radar.Application.Tests/Collectors/
  CollectedEvidenceMapperTests.cs    # (only if a new mapping path is exercised)
```

---

## Implementation details

- `CollectedEvidenceMapper.ParseQuality` already reads `collected.Metadata["quality"]` (case-insensitive,
  defined-enum-only, AD-7). So the smallest change is for `RssPressReleaseCollector` to add
  `"quality" = "<level>"` to each `CollectedEvidence.Metadata` it emits. Do **not** change the mapper's parsing
  contract or the `IEvidenceCollector` shape; reuse the existing declared-quality seam.
- **Choose the baseline level and justify it in the PR.** Recommended: **`Medium`** as the conservative default.
  Rationale / floor math (so the choice is evidence-based, not arbitrary):
  - `EvidenceConfidenceScore = 100·avgConf·(0.6+0.4·qualFactor)·(0.7+0.3·divFactor)` (AD-6).
  - With one source type, `divFactor ≈ 0.33` → `(0.7+0.3·0.33) ≈ 0.80`.
  - `Medium` is not a "weak source", so the reviewer no longer halves confidence → `avgConf ≈ 0.6`; `Medium`
    quality factor ≈ 0.6 → `(0.6+0.4·0.6)=0.84`. Result ≈ `100·0.6·0.84·0.80 ≈ 40` → **clears the 35 floor.**
  - `High` (≈0.85) → ≈45; `Primary` (1.0) → ≈48. Any of `Medium`/`High`/`Primary` clears the floor; `Medium` is
    the least aggressive that does. Confirm the reviewer's weak-source rule does **not** fire on the chosen
    level (it fires on `Low`/`Unknown`) so the confidence haircut is avoided.
  - If the reviewer prefers `High`/`Primary` (a first-party announcement is arguably a primary source), that is
    acceptable — pick one, justify it, and make the test assert that exact level.
- Keep it a single constant baseline for now (do **not** add per-feed quality configuration — that is a larger,
  separate slice if ever wanted). Note in a comment that feeds mixing thought-leadership/blog posts (e.g. some
  newsroom feeds) inherit the same baseline; tightening that is out of scope here.
- No scoring-formula change (`radar-formula-v1`/`mvp-engine-v1` versions unchanged); this only changes a
  declared input.

---

## Tests

- `RssPressReleaseCollectorTests` (MODIFIED): assert each emitted `CollectedEvidence` carries the chosen baseline
  quality (via the metadata key the mapper reads), and that it maps through to `EvidenceItem.Quality` ==
  the chosen level (not `Unknown`).
- Confirm existing collector behaviour (ordering, dedupe, hints, read-outcomes/summary) is unchanged.
- Existing reviewer/scoring tests stay green (no formula change).

---

## Constraints

- Target `net10.0`. Deterministic; honour AD-7 (quality is a declared input — here declared by the collector for
  a source type it knows is first-party press releases) and AD-6 (quality weights unchanged).
- Scope strictly to the RSS collector (+ its tests). Do not touch the reviewer, the formula, the policy, or the
  floor constant.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

## Acceptance criteria

- [ ] RSS press-release evidence maps to a non-`Unknown` baseline `EvidenceQuality` (level chosen + justified in
      the PR), via the existing declared-quality seam.
- [ ] A test proves the emitted/mapped quality is the chosen level.
- [ ] No formula/policy/reviewer change; `build`/`test` green.
- [ ] PR notes the expected downstream effect (genuine signals can now clear the evidence-confidence floor and
      reach Watch/Investigate), referencing the floor math above.
