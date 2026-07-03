# Task: Docs/ledger hygiene — promote AD-11 to Accepted and record the AI-enrichment seam + the FilingSentiment-as-DTO decisions

## Overview

This is a **docs-only** slice — **no production code, no tests, no scoring change**. It closes the ledger gap
the AI arc (specs 72–75) left behind: the decisions ledger `docs/architecture-decisions.md` does not yet
reflect the *settled* reality of the merged AI subsystem, so a future reviewer/planner could re-litigate
choices that are in fact final.

Three concrete ledger updates:

1. **Promote AD-11 (`Proposed` → `Accepted`).** AD-11 records the AI capability seam (config-driven
   `IChatClientFactory` / `IChatClient`, provider SDKs Infrastructure-only, opt-in via a blank
   `Radar:Ai:Provider`). It was recorded as **Proposed · 2026-07-03 (spec 72)** with "No consumer exists
   yet." That is no longer true: specs 73 (`ISecEarningsReleaseReader`), 74 (`IFilingAnalyzer`), and 75
   (`IDirectionalFilingSignalSource`) all consume the seam and are merged, so the seam is exercised and
   settled. Promote it to `Accepted`, keep the original rationale, and append a dated line noting the arc
   consumers that now depend on it.

2. **Record the AI-enrichment integration seam as a new decision (AD-12).** Spec 75 introduced a *pattern*
   that lives nowhere in the ledger: AI enrichment is an **opt-in `RadarPipelineRunner` step** behind an
   Application interface (`IDirectionalFilingSignalSource`, nullable-optional so the default graph is
   byte-for-byte unchanged), threaded through the **same** `map → resolve → review → store` tail as
   deterministic signals — *not* a second `ISignalExtractor`, not a new collector or stage type. Spec 75's
   "Integration-seam design" section explicitly evaluated and rejected those alternatives. Recording it as
   an AD stops the next AI-consumer slice from re-debating "should this be an extractor?" and anchors the
   AD-5/AD-11 boundary (AI/HTTP behind Infrastructure interfaces; the runner depends only on the Application
   abstraction).

3. **Record the L3 decision (AD-13): the Domain `FilingSentiment` doubles as the `GetResponseAsync<T>` DTO.**
   Spec 76's "Out of scope / future" section (L3) flagged that the Domain `FilingSentiment` record is
   currently reused as the AI structured-output DTO, and that a future slice *could* separate the wire/DTO
   shape from the Domain record. That is a real, conscious trade-off that is currently **accepted as-is** (it
   is simple and works). Record it as an accepted decision with its rationale and the explicit "revisit if…"
   trigger (e.g. if the wire shape must diverge from the Domain record, or a second AI structured output
   needs a different DTO), so a reviewer does not flag the coupling as unrecorded drift.

These entries turn three otherwise-tacit, keep-resurfacing AI-subsystem choices into settled ledger records,
exactly the ledger's stated purpose ("do not re-flag it as drift").

---

## Assignment

Worktree: any
Dependencies: specs 72–75 (the AI arc — all merged). **Independent of spec 78** (that slice touches
`RadarPipelineRunner`/`ScoringEngine` code + tests; this slice touches only `docs/architecture-decisions.md`),
so there is **no file overlap** — but see sequencing below.
Conflicts with: any other slice that edits `docs/architecture-decisions.md` (none currently queued besides,
potentially, spec 78's optional cross-reference note — sequence this AFTER 78 to avoid a ledger edit clash).
Estimated time: ~0.5–1 h

---

## Sequencing note

Run this **after** spec 78. Spec 78 is the high-value feature (directional-filing supersede) and it *may* add
an optional one-line cross-reference under AD-6/AD-10; running this docs slice afterwards keeps the ledger
edits from colliding and ensures this low-priority hygiene never displaces the feature. This slice must not be
picked up before 78.

---

## Project structure changes

```text
docs/architecture-decisions.md    # MODIFIED: AD-11 status Proposed -> Accepted (+ dated consumer note);
                                  #   NEW AD-12 (AI-enrichment runner-step seam); NEW AD-13 (FilingSentiment
                                  #   doubling as the GetResponseAsync<T> DTO — accepted trade-off).
```

No source, no tests, no config, no scoring. `Radar.sln` is untouched (still builds/tests green — nothing
changed).

---

## Implementation details

### 1 — AD-11: `Proposed` → `Accepted`

- Change the `**Status.**` line from `Proposed · 2026-07-03 (spec 72; …)` to `Accepted · <today UTC> (spec 72;
  seam consumed by specs 73–75, all merged)`. Keep the original 2026-07-03 proposal date visible (e.g.
  "Proposed 2026-07-03; Accepted <today>").
- Append a short sentence: the seam now has real consumers — `ISecEarningsReleaseReader` (73),
  `IFilingAnalyzer` (74), and `IDirectionalFilingSignalSource` (75) all code against `IChatClient` /
  `IChatClientFactory` behind Infrastructure, confirming the abstraction held. Do **not** rewrite the
  existing rationale; only update status + append the consumer line.

### 2 — New AD-12: AI enrichment is an opt-in `RadarPipelineRunner` step behind an Application interface

Record (following the ledger's entry format — decision, why, status, date):

- **Decision.** AI enrichment of the pipeline is introduced as an **opt-in step in `RadarPipelineRunner`**
  behind a nullable-optional Application interface (the first being `IDirectionalFilingSignalSource`, spec
  75), threaded through the **same** `map → resolve → review → store` tail (`MapResolveReviewStoreAsync`) as
  deterministic keyword signals. It is **not** a second `ISignalExtractor` (the runner injects a single
  extractor and has no multi-extractor composition seam), **not** a new collector, and **not** a new stage
  type. When AI is disabled (blank `Radar:Ai:Provider`) the service is not registered, the runner's optional
  dependency is `null`, and the step is skipped — the default graph is byte-for-byte unchanged.
- **Why.** Reuses the runner's existing provenance/validation/review/store machinery verbatim; keeps AI/HTTP
  entirely behind Infrastructure interfaces (materialises AD-5 + AD-11); leaves the deterministic extractor
  untouched (deterministic-before-AI); and makes "AI off ⇒ zero change" structural (registered only inside the
  `Ai.Provider`-non-blank gate). The alternatives (second extractor, dedicated collector/stage) were evaluated
  and rejected in spec 75.
- **Status.** Accepted · `<today UTC>` (pattern established by spec 75). Cross-references AD-5, AD-11.
- Note that future AI consumers (further filing reads, other enrichment) should follow this same
  opt-in-runner-step-behind-an-Application-interface shape rather than re-debating the integration seam.

### 3 — New AD-13: the Domain `FilingSentiment` doubles as the AI structured-output DTO

- **Decision.** The Domain `FilingSentiment` record (`FilingDirection Direction`, `decimal Confidence`,
  `string Rationale`) is **reused as the `GetResponseAsync<T>` structured-output DTO** for the AI filing
  analyzer (spec 74), rather than maintaining a separate wire/DTO type. Accepted as-is for the MVP.
- **Why.** The Domain shape and the AI structured-output shape are currently identical; a separate DTO would
  be duplicative ceremony with a hand-written mapping for no present benefit. The analyzer already validates
  and clamps the AI output (spec 74) before it becomes a Domain value, so the coupling does not weaken the
  typed-and-validated-before-persistence rule.
- **Status.** Accepted · `<today UTC>` (L3, deferred by spec 76). **Revisit if** the AI wire shape must
  diverge from the Domain record (e.g. extra provider-specific fields, a different confidence encoding), or a
  second AI structured output needs its own DTO — at which point separate the DTO from the Domain record in a
  dedicated slice. Recorded so the reviewer does not flag the Domain-as-DTO coupling as unrecorded drift.

### Ledger conventions

- Match the existing entry style exactly (heading `## AD-N — <title>`, bold `**Decision.**` / `**Why.**` /
  `**Status.**`, absolute UTC dates, a horizontal rule `---` between entries).
- Use today's UTC date for the new/updated status lines.
- Do **not** renumber or edit AD-1…AD-10; only AD-11's status line changes, and AD-12/AD-13 are appended.

---

## Tests

None — this slice changes only `docs/architecture-decisions.md`. The build/test gate still runs (nothing in
`Radar.sln` changed, so it stays green); no test is added or modified.

---

## Constraints

- **Docs-only.** No production code, no tests, no config, no scoring change — therefore **no** AD-10
  obligation (`ScoringEngine.ScoringConfigVersion` is untouched).
- Preserve the ledger's format and every existing AD-1…AD-10 entry verbatim; only AD-11's status changes and
  AD-12/AD-13 are appended.
- No advice language; AD-9 labels unchanged. Layering/AD-5/AD-11 unchanged (this only *records* them).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green (unchanged
  by a docs edit).

---

## Acceptance criteria

- [ ] AD-11's status is `Accepted` (with today's UTC date; the original 2026-07-03 proposal date still
      visible) and a line records that specs 73–75 now consume the seam.
- [ ] A new **AD-12** records the opt-in `RadarPipelineRunner`-step-behind-an-Application-interface AI
      enrichment seam (not a second extractor / collector / stage), with rationale, `Accepted` status, and
      cross-references to AD-5/AD-11.
- [ ] A new **AD-13** records the accepted trade-off that Domain `FilingSentiment` doubles as the
      `GetResponseAsync<T>` DTO, with rationale and an explicit "revisit if…" trigger.
- [ ] AD-1…AD-10 are unchanged; ledger formatting/date conventions match the existing entries.
- [ ] No source/test/config/scoring change; `ScoringConfigVersion` untouched; `dotnet build`/`dotnet test` on
      `Radar.sln -c Release` remain green.
