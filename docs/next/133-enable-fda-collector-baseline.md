# Task: Promote the `fda` collector into the live baseline run profile

> **CONFIG + FINGERPRINT RE-PIN slice — no collector logic changes.** Spec 129 shipped the openFDA
> device-clearance collector (`FdaClearanceCollector`, `CollectorName` = `"fda"`) as **opt-in / OFF**.
> It works live today against the **keyless** `api.fda.gov` and is Radar's first *directional* (Positive)
> collector. This slice switches it **on** in the canonical live run profile. It writes **no new production
> logic** — the only code touched is the fingerprint test's descriptor constant and its two pinned values.

## Overview

Enabling a collector changes the **enabled-collector set**, which `SignalSourceDescriptor` folds into the
`ScoringConfigVersion` fingerprint by concrete `IEvidenceCollector.CollectorName`. Per **AD-10 (as
amended)** this re-stamps the fingerprint **automatically** and needs **no** `_formula.Version` and **no**
`KeywordSignalExtractor.RuleSetVersion` bump. Scoring math is byte-identical — only the input hash and the
resulting comparability stamp change.

The `RegulatoryApproval` extractor rule group already exists (spec 129, `radar-keyword-rules-v5`); it has
simply never fired because no enabled collector produced the phrase. This slice is what makes it fire.

## Scope of the change

1. **`scripts/run-profiles/default.json`** — add `"fda"` to `Radar:Collectors`:

   ```json
   "Collectors": [ "rss", "sec", "usaspending", "newssearch", "secform4", "sec13dg", "fda" ]
   ```

   (Order in this array is irrelevant — the descriptor Ordinal-sorts by `CollectorName`.)

2. **`scripts/run-profiles/default.json` `_comment`** — extend the fingerprint lineage narrative to record
   this promotion and the new stamps, in the same style as the `secform4` (2026-07-05) and `sec13dg`
   (2026-07-06) promotions already recorded there. State explicitly that this is a **collector-set**
   re-stamp with **no** `RuleSetVersion` / `_formula.Version` bump and byte-identical scoring math.

3. **`tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs`** — update the
   `SourceDescriptor` constant (currently line ~27) to the **7**-collector CSV. `"fda"` sorts **second**
   under Ordinal comparison (`'R'` 0x52 < `'f'` 0x66 < `'n'` 0x6E):

   ```csharp
   private const string SourceDescriptor =
       "rules=radar-keyword-rules-v6;collectors=RssPressReleaseCollector,fda,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending;";
   ```

   Update the explanatory comment above it: the set is now 7 collectors, `fda` is **no longer** in the
   opt-in-OFF list (`hiringats`, `patents`, and `trademarks` remain OFF), and the rule-set identity
   `radar-keyword-rules-v6` is **unchanged**.

4. **Re-pin both fingerprints in the same file.** `AiOnSourceDescriptor` is derived from `SourceDescriptor`,
   so the single CSV edit moves **both** pinned values:
   - `Compute_DefaultConfig_MatchesPinnedFingerprint` — currently `radar-scoring-fp-c1e126884b7c`
   - the AI-ON pin — currently `radar-scoring-fp-74c5e077f728`

   > **The new values MUST be read from the failing assertion output — do not attempt to predict them.**
   > Run the test, take the actual computed values, pin them, and record both in the updated comments and
   > in the `default.json` `_comment` lineage.

5. **`docs/architecture-decisions.md`** — append the new stamps to the fingerprint lineage record, noting
   the cause is a **collector-set** change (AD-10 automatic re-stamp), not a rule or formula change.

## Assignment

Worktree: any. Dependencies: **129 (openFDA collector — MERGED)**. Independent of 131 and 132; it shares no
files with 131 (patents reader) and touches the fingerprint pins that 132 will later move again, so **if
both are queued, land this one first and let 132 re-stamp on top of it**.
Estimated time: ~45 min.

## Coverage — known and accepted

`data/companies.json` currently declares `fda` feeds for **2 of 43** companies:

| Ticker | Feed token |
|---|---|
| **TMDX** (TransMedics) | `applicant=TransMedics` |
| **AXGN** (Axogen) | `applicant=Axogen` |

Both are correctly chosen medical-device names. Thin coverage is expected and accepted — `usaspending`
runs 3-of-43. **Do not** seed additional companies in this slice; widening the FDA seed set is a separate,
evidence-led task (a company only earns a feed once its applicant token is verified to return results).

## Expected live effects (record in the PR description)

- **A new efficacy segment.** The fingerprint change starts a fresh `ScoringConfigVersion` segment, so the
  spec-101/108 score-vs-price overlay restarts its connected line. This is correct, expected behaviour —
  the spec-108 continuity-aware segmentation exists precisely to mark it — but the next few runs will show
  a short score series.
- **First directional non-filing collector live.** `RegulatoryApproval` is Positive, routine-strength. TMDX
  or AXGN may move on a clearance where they previously would not have.
- **No score change for the other 41 companies.** They declare no `fda` feed, so their evidence set,
  signals, and component scores are byte-identical; only their stamped `ScoringConfigVersion` differs.

## Tests

- `ScoringConfigFingerprintTests` green with the updated descriptor and the two **newly pinned** values.
- The discrimination tests in the same file (`Compute_ChangedWeight_ChangesFingerprint`,
  `Compute_ChangedAiStrength_ChangesFingerprint`, and any collector-set discrimination test) remain green
  **unmodified** — they assert inequality and must not need re-pinning.
- Confirm **no** other test pins `radar-scoring-fp-c1e126884b7c` or `radar-scoring-fp-74c5e077f728`; if one
  does, it is part of this slice and must be re-pinned with the same values.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **No production code changes.** If this slice edits anything under `src/` other than the `default.json`
  run profile (which lives under `scripts/`), the change has leaked scope and is wrong.
- **No `RuleSetVersion` bump. No `_formula.Version` bump. No `ScoringWeights` / tier / insider-materiality
  edit.** The only fingerprint input that moves is the collector set.
- `appsettings.json`'s code-default `Radar:Collectors` (currently `[ "rss" ]`) stays **unchanged** — this
  slice changes *how we run*, not the code default.
- openFDA is **keyless**; no secret handling, no env var, no key gate is introduced.

## Acceptance criteria

- [ ] `"fda"` is present in `Radar:Collectors` in `scripts/run-profiles/default.json` (now 7 collectors).
- [ ] `SourceDescriptor` in `ScoringConfigFingerprintTests` lists the 7-collector Ordinal-sorted CSV with
      `fda` in second position, and its comment is updated (7 collectors; `fda` removed from the opt-in-OFF
      list; rule-set identity unchanged at `radar-keyword-rules-v6`).
- [ ] Both default fingerprints are re-pinned to the **actual computed** values, and both are recorded in
      the test comments, the `default.json` `_comment` lineage, and `docs/architecture-decisions.md`.
- [ ] **No** `RuleSetVersion` / `_formula.Version` / weight / tier / enum / production-code change.
- [ ] `appsettings.json` code-default `Radar:Collectors` unchanged.
- [ ] `patents`, `trademarks`, and `hiringats` remain opt-in / OFF.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
