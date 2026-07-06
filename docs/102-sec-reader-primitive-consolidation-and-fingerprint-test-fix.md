# Task: Consolidate SEC submissions-JSON primitives + fix the default fingerprint test pin

## Overview

A fresh whole-codebase architecture checkpoint (trunk @ `58c55f5`) surfaced two MEDIUM findings.
Both are pure cleanup — **no behaviour change, no scoring-math change, no new feature, no
production fingerprint change.**

- **M1 — reuse-over-copy (the recurring MEDIUM: CLAUDE.md specs 76/77/83).** The columnar
  submissions-JSON *primitives* used by the three SEC submissions readers were never consolidated
  even though the higher-level `SecRecentFilings.Flatten` (spec 100) already is shared. Byte-identical
  copies of the pure helpers `GetArray` / `At` / `TryParseAcceptance` / `NullIfBlank` / `GetString` /
  `TryGetRecent` remain scattered across `HttpSecFilingReader`, `HttpSecForm4Reader`, and
  `HttpSec13DGReader`. Duplicated primitives silently drift (only one copy gets the next fix). This
  spec lifts them onto the existing shared home `SecRecentFilings` and routes all three readers
  through it.

- **M2 — test-guard drift (AD-10 provenance).** Commit `58c55f5` promoted `sec13dg` into the
  baseline `scripts/run-profiles/default.json` (now 6 collectors; its comment records the true live
  default fingerprint `radar-scoring-fp-8d638b90d4aa`). But
  `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` still hardcodes a
  **five**-collector `SourceDescriptor` (missing `sec-13dg`) and pins the stale
  `radar-scoring-fp-eee8ed0665f2`, with a comment claiming the descriptor "matches what the Worker
  actually produces by default." After the promotion that claim is **false**. Runtime is unaffected —
  the fingerprint self-verifies (EffectiveConfig recompute == filename) — so this is a stale test pin,
  not a runtime bug. This spec re-pins the test to the already-shipped production default.

---

## Assignment

Worktree: any
Dependencies: None
Conflicts with: None. (A separate uncommitted spec 101 — price-efficacy visual — touches entirely
different files: `Efficacy/`, the score store, and the Worker. There is **no** file overlap with this
spec.)
Estimated time: ~1-2 hours

---

## Grounding facts (verified on disk @ `58c55f5`)

**M1 — where each primitive lives today:**

| Primitive | `SecRecentFilings.cs` | `HttpSecFilingReader.cs` | `HttpSecForm4Reader.cs` | `HttpSec13DGReader.cs` |
|---|---|---|---|---|
| `GetArray(JsonElement, string)` | `:105` (private) | `:184` (private, identical) | — (uses `SecRecentFilings.Flatten`) | — (uses `SecRecentFilings.Flatten`) |
| `At(IReadOnlyList<JsonElement>, int)` | `:115` (private) | `:194` (private, identical) | — | — |
| `TryParseAcceptance(string?, out DateTimeOffset)` | `:88` (private) | `:167` (private, identical) | — | — |
| `NullIfBlank(string?)` | `:126` (private) | `:210` (private, identical) | `:434` (private, identical) | — |
| `GetString(JsonElement, string)` | — | `:205` (private) | `:429` (private, identical) | `:170` (private, identical) |
| `TryGetRecent(JsonElement, out JsonElement)` | — | inlined in `ParseFilings` guard `:118-124` | `:414` (private) | `:155` (private, identical) |

- `SecRecentFilings` already owns `GetArray`, `At`, `TryParseAcceptance`, `NullIfBlank` (all
  `private static`), used only by its own `Flatten`. It does **not** yet own `GetString` or
  `TryGetRecent`.
- `HttpSecForm4Reader` and `HttpSec13DGReader` already call `SecRecentFilings.Flatten`; they call
  their **own** `GetString`/`TryGetRecent` only in their `ReadAsync` guards (Form4 `:107`/`:116`,
  13DG `:97`/`:106`). Form4 additionally uses its own `NullIfBlank` at `:255`.
- `HttpSecFilingReader.ParseFilings` (`:107-165`) is the **per-source hook that must NOT be merged
  into the shared type**: it reads three EXTRA columns (`reportDate`, `primaryDocDescription`,
  `items`), has **no** form predicate, and builds the wider `SecFilingItem` (with `IndexUrl`) rather
  than `SecRecentFilingRow`. Its wider row shape genuinely can't reuse `SecRecentFilingRow`/`Flatten`.
  It **does** call the same pure primitives (`GetArray`/`At`/`TryParseAcceptance`/`NullIfBlank`/
  `GetString`) — share only those, keep its bespoke flatten loop in place.
- Verified signatures (must be preserved exactly for behaviour-parity):
  - `bool TryGetRecent(JsonElement root, out JsonElement recent)` — resolves `filings.recent`;
    returns `false` (with `recent = default`) when the shape is absent. Both copies are byte-identical.
  - `bool TryParseAcceptance(string? value, out DateTimeOffset utc)` — `DateTimeOffset.TryParse` with
    `InvariantCulture` + `AssumeUniversal | AdjustToUniversal`, then `.ToUniversalTime()`.
  - `string GetString(JsonElement, string)` returns `string.Empty` when absent/non-string (note:
    non-nullable `string`, empty-string fallback — distinct from `At`, which returns `string?`).
  - `IReadOnlyList<JsonElement> GetArray` returns `[]` when absent/non-array; `string? At` returns
    `null` out of range or non-string; `string? NullIfBlank` maps whitespace → `null`.

**M2 — the stale test artifacts (all in `ScoringConfigFingerprintTests.cs`):**

- `SourceDescriptor` const (`:21-22`):
  `"rules=radar-keyword-rules-v2;collectors=RssPressReleaseCollector,newssearch,sec-edgar,sec-form4,usaspending;"`
  — missing `sec-13dg`.
- Pinned fingerprint (`:94`): `radar-scoring-fp-eee8ed0665f2` — the pre-promotion (spec-99) 5-collector value.
- Misleading comment (`:15-20` and `:81-89`).
- `Sec13DGCollector.CollectorName` is `"sec-13dg"` (verified). The descriptor is **Ordinal-sorted**;
  Ordinal places uppercase before lowercase, and among the `sec-*` tokens the next char after `sec-`
  is `1` (0x31) < `e` (0x65) < `f` (0x66). So `sec-13dg` sorts **before** `sec-edgar`. The correct
  6-collector CSV is:
  `RssPressReleaseCollector,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending`.

**M2 — files scanned for other stale pins (do NOT change — they do not represent the default):**

- `tests/Radar.Application.Tests/Scoring/SignalSourceDescriptorTests.cs:94` uses
  `collectors=newssearch,rss,sec,sec-form4,usaspending` — arbitrary fake tokens fed to `DescriptorFor(...)`
  to test the **ordinal-sort/build logic**, not the default worker set. Out of scope.
- `tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs:673` (and its harness) uses a
  self-consistent `SourceDescriptor` with a **stub** formula (`stub-formula-vX`); it asserts internal
  consistency, never the pinned default fingerprint. Out of scope.
- Only `ScoringConfigFingerprintTests.cs` claims to represent "what the Worker actually produces by
  default", so it is the **only** file M2 corrects. (`eee8ed0665f2` appears only in `default.json`'s
  historical prose — inside the older-value cross-reference — and in this test.)

---

## Design

### M1 — lift the pure primitives onto `SecRecentFilings`

`SecRecentFilings` (in `Radar.Infrastructure/Sec/`) is the established shared home for submissions-JSON
parsing (per its own XML doc and the reuse-over-copy rule). Make it the single source for all six
primitives:

1. Change its four existing helpers (`GetArray`, `At`, `TryParseAcceptance`, `NullIfBlank`) and add
   `GetString` and `TryGetRecent` as `internal static` members of `SecRecentFilings` (a small
   `internal static` surface — all three readers are in the same assembly, so `internal` is
   sufficient; no need for `public`). Keep `Flatten` calling them unchanged.
   - Recommended shape: keep them as `internal static` methods on `SecRecentFilings` itself (simplest,
     one type). Do **not** introduce a new type — reuse the established home.
2. In `HttpSecFilingReader`: delete the local `GetArray`/`At`/`TryParseAcceptance`/`NullIfBlank`/
   `GetString` copies and the inlined `filings.recent` guard's duplication. Route `ParseFilings`
   through `SecRecentFilings.GetArray/At/TryParseAcceptance/NullIfBlank/GetString` and
   `SecRecentFilings.TryGetRecent`. **Keep** `ParseFilings`'s bespoke wider-row loop (the three extra
   columns + `SecFilingItem`/`IndexUrl` build) exactly as-is — that is the defensible per-caller hook.
3. In `HttpSecForm4Reader`: delete the local `GetString`, `TryGetRecent`, `NullIfBlank` copies; route
   the `ReadAsync` guard and the `:255` call through `SecRecentFilings`. Leave the ownership-XML fetch
   / transaction-parsing code untouched.
4. In `HttpSec13DGReader`: delete the local `GetString`, `TryGetRecent` copies; route through
   `SecRecentFilings`. Leave the form-classify code untouched.
5. Remove now-unused `using System.Globalization;` only where nothing else needs it (the readers keep
   whatever their remaining code requires — verify per file; do not remove usings still in use).

After M1: exactly **one** copy of each primitive, owned by `SecRecentFilings`; all three readers
routed through it; the 8-K wider-row flatten stays as its per-caller hook (share the core, not the
divergent edge).

### M2 — re-pin the default fingerprint test

1. Add `sec-13dg` to the `SourceDescriptor` collector CSV in its correct Ordinal position (between
   `newssearch` and `sec-edgar`), giving:
   `"rules=radar-keyword-rules-v2;collectors=RssPressReleaseCollector,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending;"`
2. Re-pin `Compute_DefaultConfig_MatchesPinnedFingerprint` from `radar-scoring-fp-eee8ed0665f2` to the
   true 6-collector default. **Obtain the exact hash by running the test and reading the assertion
   failure's *actual* value — do NOT hand-type it.** It MUST equal `radar-scoring-fp-8d638b90d4aa`
   (the value `default.json`'s comment records as the live default). If the test's actual value does
   **not** equal `8d638b90d4aa`, stop and report — that means the descriptor CSV/order is wrong; do
   not force a mismatched pin.
3. Correct the comments (`:15-20`, `:81-89`) to describe the 6-collector default incl. `sec-13dg`
   (spec 100 promotion), and update the spec-99→ lineage note to point at this re-pin.
4. **Recommended (reviewer's cleaner variant), only if low-friction:** derive the descriptor's
   collector list in the test from the real default DI collector set (via `SignalSourceDescriptor`
   over the default-registered `IEvidenceCollector`s) so it can't silently diverge again. If wiring
   the full default DI graph into this unit test is awkward, the minimal fix (steps 1-3) is
   acceptable — **call out which path was taken and why** in the PR description.

---

## Project structure changes

- `src/Radar.Infrastructure/Sec/SecRecentFilings.cs` — **MODIFIED**: becomes the single home for the
  six primitives (`GetArray`, `At`, `TryParseAcceptance`, `NullIfBlank`, `GetString`, `TryGetRecent`),
  promoted to `internal static`; XML docs updated to note it now owns the shared guards.
- `src/Radar.Infrastructure/Sec/HttpSecFilingReader.cs` — **MODIFIED**: delete 5 local primitive
  copies + inlined recent-guard; route `ParseFilings` through `SecRecentFilings`; keep the wider-row
  loop (per-caller hook).
- `src/Radar.Infrastructure/Sec/HttpSecForm4Reader.cs` — **MODIFIED**: delete local `GetString`,
  `TryGetRecent`, `NullIfBlank`; route through `SecRecentFilings`.
- `src/Radar.Infrastructure/Sec/HttpSec13DGReader.cs` — **MODIFIED**: delete local `GetString`,
  `TryGetRecent`; route through `SecRecentFilings`.
- `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — **MODIFIED**: add
  `sec-13dg` to `SourceDescriptor`, re-pin to `radar-scoring-fp-8d638b90d4aa`, fix comments.
- No other stale-pin file found requiring change (see Grounding facts — `SignalSourceDescriptorTests`
  and `ScoringEngineTests` do not represent the default).

---

## Tests

- **M1 behaviour parity — rely on existing SEC reader tests.** The three readers are already covered
  by their existing test suites, which exercise these primitives end-to-end (valid submissions,
  missing `filings.recent`, absent columns, non-string cells, unparseable acceptance, blank
  accession). Because the primitives are byte-identical before/after, all existing reader tests must
  stay **green with no test edits** — that is the parity proof. Do not weaken or delete any.
- Optionally (only if a primitive lacks direct coverage) add a small focused test for the consolidated
  helpers; but the preference is to rely on the existing reader tests rather than test private/internal
  helpers directly. State in the PR which primitives, if any, gained a direct test and why.
- **M2** — `Compute_DefaultConfig_MatchesPinnedFingerprint` re-pinned to
  `radar-scoring-fp-8d638b90d4aa` and green; all other `ScoringConfigFingerprintTests` cases stay
  green unchanged.
- Full gate: `dotnet build Radar.sln -c Release` then `dotnet test Radar.sln -c Release --no-build` —
  entire suite green.

---

## Out of scope / recorded decisions

- **M3 (SEC collector `CollectAsync` scaffold duplication) — DEFERRED this round (maintainer
  decision).** `SecEdgarFilingCollector.CollectAsync` (`:50-122`), `SecForm4Collector.CollectAsync`
  (`:54-121`), and `Sec13DGCollector.CollectAsync` (`:52-119`) share ~50 lines of near-identical
  feed-loop / dedupe / summary scaffold; only the feed token, the 8-K `_forms.Contains` filter, and
  `MapToEvidence` diverge. The reviewer's suggested fix was a shared
  `SecFeedCollection.Collect(context, token, reader, map, ct)` helper taking a map delegate + optional
  filter — but flagged it as **weaker** than M1: the three collectors have genuine per-source hooks and
  three differing `*ReadResult` types that would need a generic/delegate seam, so extracting now adds
  abstraction risk for modest gain. The reviewer explicitly said declining is "a defensible call if
  recorded." **Decision: do NOT extract the collector scaffold now.** Recorded here so the third
  instance of the scaffold duplication is on the record and can be picked up later if the collectors
  drift further.
- **Spec 101 (price-efficacy visual)** — separate uncommitted spec, entirely different files, no
  overlap. Not touched here.

---

## Constraints

- Target `.NET 10` / `net10.0`, C# 14.
- **No behaviour change, no scoring-math change, no formula / `RuleSetVersion` / `_formula.Version`
  bump.** M1 is a pure refactor (identical primitives → shared home). M2 corrects a **test** pin +
  comment to match the already-shipped production default.
- **Production `ScoringConfigVersion` output is byte-identical before and after this spec.**
  `radar-scoring-fp-8d638b90d4aa` is what `58c55f5` already produces at runtime (the fingerprint
  self-verifies); the test was simply never updated after the `sec13dg` promotion. This spec changes
  no production value.
- Layering (AD-5) unchanged: all M1 work stays within `Radar.Infrastructure/Sec/`; M2 is test-only.
- Reuse-over-copy: after M1 there is exactly **one** copy of each consolidated primitive, all three
  readers routed through `SecRecentFilings`; the 8-K's wider-row flatten stays as its per-caller hook.
- Preserve provenance; keep changes scoped; do not implement unrelated features.
- Do not commit — the maintainer will review.

---

## Acceptance criteria

- [ ] `SecRecentFilings` owns the six primitives (`GetArray`, `At`, `TryParseAcceptance`,
      `NullIfBlank`, `GetString`, `TryGetRecent`) as the single source; XML doc updated.
- [ ] `HttpSecFilingReader`, `HttpSecForm4Reader`, `HttpSec13DGReader` route all guard/primitive calls
      through `SecRecentFilings`; their local copies are deleted (zero duplicated primitives remain).
- [ ] `HttpSecFilingReader.ParseFilings` keeps its bespoke wider-row loop (3 extra columns,
      `SecFilingItem`/`IndexUrl` build) as a per-caller hook — not merged into `SecRecentFilings`.
- [ ] All existing SEC reader tests pass unchanged (behaviour parity proof).
- [ ] `ScoringConfigFingerprintTests.SourceDescriptor` includes `sec-13dg` in correct Ordinal position
      (`...,newssearch,sec-13dg,sec-edgar,sec-form4,...`).
- [ ] `Compute_DefaultConfig_MatchesPinnedFingerprint` pins `radar-scoring-fp-8d638b90d4aa` (obtained
      by running the test and reading the actual value; equals `default.json`'s recorded default).
- [ ] Misleading comments in `ScoringConfigFingerprintTests` corrected to the 6-collector default.
- [ ] M3 deferral recorded (this spec) — no collector scaffold extracted.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.
