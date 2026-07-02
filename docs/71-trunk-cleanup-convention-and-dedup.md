# Task: Trunk cleanup — document the scoring-config bump-convention, fix stale extractor comments, de-duplicate `BuildCompanyHints`

## Overview

This is a **pure cleanup / convergence slice** turning the three MEDIUM findings from the
`radar-architecture-reviewer` checkpoint on the trunk (origin/main `09caf60`, verdict CLEANUP, no HIGH)
into one small slice. It reduces drift accumulated across specs 66, 67, 69, and 70:

1. **M1** — the `ScoringConfigVersion` bump-convention lives only as a code comment; promote it to the
   decisions ledger (**AD-10**) and the `CLAUDE.md` checklist so the next scoring change has a
   discoverable obligation to bump it.
2. **M2** — `KeywordSignalExtractor`'s class-level doc and one inline comment now **contradict the code**
   (they claim the extractor never reads `SourceType`/`Metadata`, but specs 66 and 70 made it do exactly
   that in two defined ways). Correct the stale global claims.
3. **M3** — `BuildCompanyHints` is now **byte-identical in all four collectors**; promote it to one shared
   Infrastructure helper and delete the four private copies.

Plus one cheap regression guard (**L1**): assert a freshly-produced `CompanyScoreSnapshot` carries a
non-null `ScoringConfigVersion` so the stamp cannot silently regress to null.

**There is NO behaviour change here.** Extracted signals, collector `CompanyHints`, and scores must be
byte-identical before and after this slice. This slice does **not** change scoring output, so it does
**not** bump `ScoringConfigVersion` (staying `radar-scoring-config-v2`).

---

## Assignment

Worktree: any
Dependencies: specs 66, 67, 69, 70 — all merged.
Conflicts with: touches `KeywordSignalExtractor` (comments only), the four collectors
(`RssPressReleaseCollector`, `SecEdgarFilingCollector`, `UsaSpendingContractCollector`,
`GdeltNewsCollector`) plus a new shared Infrastructure helper, `ScoringEngineTests`, and the two docs
files (`docs/architecture-decisions.md`, `CLAUDE.md`). **Must NOT run in parallel with any
extractor / collector / scoring slice — sequence it.**
Estimated time: ~1.5–2 h

---

## Project structure changes

```text
docs/architecture-decisions.md                                   # MODIFIED: append AD-10
CLAUDE.md                                                        # MODIFIED: one new item in "Spec implementation checklist"

src/Radar.Application/SignalExtraction/
  KeywordSignalExtractor.cs                                      # MODIFIED: comments/XML-doc ONLY (no logic change)

src/Radar.Infrastructure/Sources/
  CollectorCompanyHints.cs                                       # NEW: single shared hint helper

src/Radar.Infrastructure/Rss/RssPressReleaseCollector.cs         # MODIFIED: call shared helper; delete private copy
src/Radar.Infrastructure/Sec/SecEdgarFilingCollector.cs          # MODIFIED: call shared helper; delete private copy
src/Radar.Infrastructure/UsaSpending/UsaSpendingContractCollector.cs  # MODIFIED: call shared helper; delete private copy
src/Radar.Infrastructure/Gdelt/GdeltNewsCollector.cs             # MODIFIED: call shared helper; delete private copy

tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs      # MODIFIED: add L1 non-null ScoringConfigVersion guard
```

`LocalFileEvidenceCollector` legitimately uses `CompanyHints = []` and MUST stay unchanged.

---

## Implementation details

### M1 — Document the `ScoringConfigVersion` bump-convention

The rule currently lives only as a comment in
`src/Radar.Application/Scoring/ScoringEngine.cs` (~lines 25–32). `docs/architecture-decisions.md` ends at
AD-9 with no entry, and `CLAUDE.md` has no checklist item (spec 69 flagged this exact follow-up and it
was never done). Promote it to both docs. **Do not change `ScoringEngine.cs` code** — the existing comment
may stay as-is (it correctly documents the v2 generation).

**(a) Append AD-10 to `docs/architecture-decisions.md`**, matching the existing entry format (heading,
bold `**Decision.**` / `**Why.**` / `**Status.**` paragraphs, `---` separator above it). Use this exact
prose:

```markdown
---

## AD-10 — Any scoring-affecting change MUST bump `ScoringEngine.ScoringConfigVersion`

**Decision.** `ScoringEngine.ScoringConfigVersion` (a code constant, currently
`"radar-scoring-config-v2"`) stamps every `CompanyScoreSnapshot` and identifies the whole
scoring-affecting pipeline **generation** — distinct from the formula/engine identity `ScoringVersion`
(AD-6). Any change that can move scoring output — the scoring formula, the extractor rules (including the
`GovernmentContract` materiality tiers), or `ScoringOptions` — **MUST bump `ScoringConfigVersion`** in the
same slice. It is a **code constant**, never an ops-tunable config value: bumping it must require a code
edit that trips the spec-implementation checklist, and it must move in lockstep with the code.

**Why.** The stamp (spec 69) gates the cross-run delta clause **and** the
`Thesis improving`/`Thesis deteriorating` action label: two snapshots are compared only when their
`ScoringConfigVersion` values are non-null and equal. When they differ, the report renders
`(scoring updated)` instead of a numeric delta and the policy falls back to its no-previous behaviour —
so a scoring **recalibration** can never fabricate a thesis-trajectory label (the exact defect spec 69
fixed, where spec 66's materiality change dropped Mercury Systems' Trajectory 80→75 and produced a false
`Thesis deteriorating`). This correctness property holds **only** if every scoring-affecting change bumps
the stamp; a forgotten bump silently re-creates that bug. Spec 70 correctly bumped v1→v2 — but only by
author discipline against a convention that lived nowhere discoverable. Recording it here (and in the
`CLAUDE.md` checklist) gives the next scoring-affecting change a single documented obligation.
Cross-reference AD-6 (formula versioning) and spec 69 (the stamp and its comparability gate).

**Status.** Accepted · 2026-07-02 (trunk cleanup slice; convention introduced by spec 69, first bumped
by spec 70).
```

**(b) Add one item to the `CLAUDE.md` "Spec implementation checklist"** section (currently a 5-item list
ending at item 5, ~lines 233–241). Append as item 6, verbatim:

```markdown
6. Bump `ScoringEngine.ScoringConfigVersion` when a change affects scoring output (formula, extractor
   rules incl. materiality tiers, or `ScoringOptions`) — see AD-10.
```

### M2 — Fix `KeywordSignalExtractor` comments that now contradict the code (comments only)

After spec 66 (metadata-aware `GovernmentContract` Strength, reads `evidence.MetadataJson`) and spec 70
(`NewsArticle` early-return branch, reads `evidence.SourceType`), the extractor **is** source-type-aware
in two defined places — but the stale **global** claims still describe a pure keyword scanner. The
per-branch comments (news ~163–169; metadata ~123–127) are accurate; only the global claims mislead.
**No logic changes** in this section — do not touch `Rules`, the tier table, or any method body.

**(a) Rewrite the class-level XML doc** (`src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs`
~lines 10–19) so it states the extractor is deterministic and keyword-based **and** source-type-aware in
exactly two defined ways:
- `NewsArticle` evidence → emits exactly one **Neutral `MediaAttention`** signal (the directional keyword
  rules are suppressed for news; spec 70);
- `GovernmentContract` signal **Strength** is scaled by the award amount read from
  `evidence` metadata (`awardAmount`; spec 66).

Keep the accurate parts of the existing doc (deterministic, offline/reproducible, verbatim excerpt
provenance, no entity resolution — `CompanyMention` is the `SourceName` placeholder, never a guessed
ticker).

**(b) Delete/correct the false invariant clause.** In the comment at ~lines 104–108 (the
`GovernmentContract` rule group), the sentence asserting the extractor
"**still never reads evidence.SourceType or evidence.Metadata**" is now FALSE. Remove that clause; the
rest of that comment (explaining these are ordinary business phrases claimed first-match-per-type across
awarding agencies, not a source-type coupling) stays. Likewise soften the parenthetical at ~line 126
("the extractor still never reads evidence.SourceType (spec 63 invariant preserved)") — spec 70 broke
that specific invariant; state instead that this metadata read is one of the two defined
source/metadata-aware behaviours (the other being the spec 70 `NewsArticle` branch).

**(c) Add a brief watch-item comment** near the class-level doc or the `NewsArticle` branch: a **third**
source-type special-case should trigger a refactor to an explicit per-`EvidenceSourceType`
dispatch/strategy rather than a third inline branch. **Do NOT refactor now** — two justified branches are
fine; this is only a signpost for the next person.

### M3 — De-duplicate `BuildCompanyHints` into one shared Infrastructure helper

The private static `BuildCompanyHints(Guid, IReadOnlyDictionary<Guid, Company>)` is **byte-identical** in
all four collectors (confirmed against RSS ~130, SEC ~189, USASpending ~255, GDELT ~264 — same body:
missing-company → `[]`; else prefer non-blank `Ticker`; else non-blank `Name`; else `[]`; never invent a
ticker). This was LOW-1 (triplication) last checkpoint; spec 67's GDELT collector made it a fourth copy.

**(a) Add a new shared helper** at `src/Radar.Infrastructure/Sources/CollectorCompanyHints.cs`
(namespace `Radar.Infrastructure.Sources`, matching the existing folder), `internal static` — it is an
Infrastructure-only collector concern (AD-5). Move the verbatim body into it:

```csharp
using Radar.Domain.Companies;

namespace Radar.Infrastructure.Sources;

/// <summary>
/// Shared, deterministic company-hint builder for the fetching collectors. Given a bound companyId and
/// the run's company lookup, returns the high-confidence hint from the configured binding: prefer the
/// ticker, fall back to the name, never invent a ticker. Returns an empty list when the company is
/// unknown or has neither a ticker nor a name. Consolidates the byte-identical helper previously copied
/// into the RSS, SEC, USASpending, and GDELT collectors (LocalFileEvidenceCollector deliberately uses
/// no hints and does not call this).
/// </summary>
internal static class CollectorCompanyHints
{
    public static IReadOnlyList<string> For(
        Guid companyId, IReadOnlyDictionary<Guid, Company> companiesById)
    {
        if (!companiesById.TryGetValue(companyId, out var company))
        {
            return [];
        }

        // High-confidence hint from the configured binding: prefer the ticker, fall back to the name.
        // Never invent a ticker.
        if (!string.IsNullOrWhiteSpace(company.Ticker))
        {
            return [company.Ticker];
        }

        return string.IsNullOrWhiteSpace(company.Name) ? [] : [company.Name];
    }
}
```

**(b) In each of the four collectors**, replace the `BuildCompanyHints(feed.CompanyId, companiesById)`
call site with `CollectorCompanyHints.For(feed.CompanyId, companiesById)` and **delete the private static
`BuildCompanyHints` method**. Each collector already builds `companiesById` via
`context.Companies.ToDictionary(c => c.Id)` — keep that where it is (RSS builds it at ~line 48, the others
at ~56 / ~66 / ~67) and pass it to the helper. Add `using Radar.Infrastructure.Sources;` where needed
(SEC/USASpending/GDELT/RSS live in sibling namespaces). Confirm the four bodies are identical before
merging (they are).

**Behaviour must be identical**: the hint output for every collector is unchanged before and after.

### L1 — Guard: freshly-produced snapshot carries a non-null `ScoringConfigVersion`

In `tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs`, add one small test asserting that a
`CompanyScoreSnapshot` produced by `ScoringEngine.ScoreCompanyAsync` has a **non-null, non-empty**
`ScoringConfigVersion` (reuse an existing test's arrange/act path — one approved windowed signal with
present evidence is enough). This pins the stamp so it cannot silently regress to null. Do not assert the
exact string value (that would couple the test to the version and defeat AD-10's bump convention); assert
only non-null/non-empty.

---

## Tests

- **L1 (new):** `ScoringEngineTests` — a produced snapshot's `ScoringConfigVersion` is non-null and
  non-empty.
- **M3 (existing coverage must stay green):** the collectors' existing tests already assert `CompanyHints`
  output (ticker-preferred / name-fallback / unknown-company → empty). They must pass **unchanged** — that
  is the proof the refactor is behaviour-preserving. If a collector test referenced the private method
  reflectively (it should not), update it to the new call path; otherwise no test edits for M3. Optionally
  add one focused `CollectorCompanyHints` unit test covering the three branches (ticker, name-fallback,
  unknown → `[]`), but the existing collector tests are sufficient.
- **M1 / M2:** docs/comments only — no test changes.

---

## Constraints

- Target `net10.0`, C# 14.
- **This is a CLEANUP slice — NO new feature behaviour and NO scoring-output change.** Extracted signals,
  collector `CompanyHints`, and scores must be **byte-identical** before and after (the reviewer must be
  able to confirm this). It therefore does **NOT** bump `ScoringConfigVersion` (stays
  `radar-scoring-config-v2`).
- The shared hint helper stays in `Radar.Infrastructure` (AD-5); Domain/Application layering unchanged.
- `KeywordSignalExtractor` changes are **comments/XML-doc only** — no logic, no rule-table, no tier-table
  edits.
- Determinism, files-first (AD-8), no AI, no provider SDK, no DB. No advice language; the AD-9 labels are
  unchanged.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `docs/architecture-decisions.md` gains **AD-10** (exact prose above), matching the existing entry
      format, status Accepted · 2026-07-02, cross-referencing AD-6 and spec 69.
- [ ] `CLAUDE.md` "Spec implementation checklist" gains item 6: bump `ScoringEngine.ScoringConfigVersion`
      on any scoring-output-affecting change.
- [ ] `KeywordSignalExtractor`'s class-level doc now describes it as deterministic keyword-based **and**
      source-type-aware in the two defined ways (`NewsArticle` → one Neutral `MediaAttention`;
      `GovernmentContract` Strength scaled by `awardAmount`); the false "never reads
      SourceType/Metadata" claims are removed/corrected; a "third special-case → refactor to per-source
      dispatch" watch-item comment is added. **No logic changed.**
- [ ] A single `CollectorCompanyHints.For(...)` helper exists in `Radar.Infrastructure.Sources`; all four
      collectors call it; their four private `BuildCompanyHints` copies are deleted;
      `LocalFileEvidenceCollector` is unchanged.
- [ ] Collector hint output is byte-identical to before (existing collector tests pass unchanged).
- [ ] `ScoringEngineTests` asserts a produced snapshot's `ScoringConfigVersion` is non-null/non-empty.
- [ ] No score value, signal, or hint moves; `ScoringConfigVersion` is **not** bumped.
- [ ] Layering (AD-5) and determinism preserved; `dotnet build` / `dotnet test` on `Radar.sln -c Release`
      green.
