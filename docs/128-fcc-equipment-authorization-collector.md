# Task: FCC Equipment Authorization collector (leading pre-shipment hardware signal)

> **COLLECTOR-EXPANSION SLICE #2 — the next new evidence axis after patents (spec 127).** Follows the
> documented expansion arc in `docs/radar-full-pipeline-spec.md`
> (*Patent → FCC Equipment Authorization → FDA → USPTO Trademark*, sequenced because each bumps
> `RuleSetVersion` and re-pins the same scoring fingerprint). A company must obtain FCC certification
> **before** it may sell a wireless/electronic device in the US, so a new authorization record **leads**
> product shipment by weeks to months — the sharpest "before the market notices" signal for Radar's
> hardware-heavy universe (Rocket Lab, Mercury Systems, Bel Fuse, Energy Recovery). This is a **single
> cohesive slice** (~2–3 h): the extractor rule + Infrastructure collector + reader seam + feed-token
> parser + seed + wiring land together because the collector's emitted phrase and the extractor rule are a
> **verbatim contract** and must ship as one unit (the exact pattern of specs 103/127). It is **opt-in /
> OFF by default** — the baseline run does not enable it.

## Overview

A new deterministic collector (`IEvidenceCollector`, config kind `"fccauth"`, `CollectorName "fccauth"`,
`EvidenceSourceType.EquipmentAuthorization`) reads each seeded company's FCC-authorization feed (a token
carrying that company's **grantee organization name** to query), counts recently-granted equipment
authorizations in a bounded lookback window, and emits **one** `EquipmentAuthorization`
`CollectedEvidence` per company carrying a **fixed authorization-activity phrase** in Title/RawText plus
rich provenance metadata (grantee query, grant-count, window, a few sample FCC IDs + product descriptions,
retrieved timestamp). A new `KeywordSignalExtractor` rule maps that phrase to a **Neutral**
`SignalType.EquipmentAuthorization` signal.

**Two new append-only Domain slots** are added at the end of their enums:
`EvidenceSourceType.EquipmentAuthorization` and `SignalType.EquipmentAuthorization`. Unlike patents
(where both slots were pre-reserved), FCC has no reserved slot, so this slice makes the **minimal**
append-only enum additions (never reorder/remove; persisted by name). `EquipmentAuthorization` is
**first-party** (a company's own regulatory clearance is not third-party market attention) — it is **not**
added to `EvidenceSourceTypes.IsThirdPartyAttentionSource`, so it contributes nothing to Attention reach —
correct.

**v1 is deliberately Neutral, not directional.** The roadmap describes FCC as "Positive, count/new-grant
based (**same anti-misfire shape as the patent collector**)" — and the patent collector achieves its
anti-misfire shape by being **Neutral** in v1 (spec 127). A single-window authorization COUNT cannot
distinguish a company whose product cadence is genuinely *accelerating* from a large hardware incumbent
that *always* refreshes many device SKUs; a mild-Positive on raw presence would systematically favour the
most-prolific filers — the selection-bias trap the diversified universe avoids. So v1 **establishes the
mechanism** end-to-end (provenance: FCC EAS → `EquipmentAuthorization` evidence → `EquipmentAuthorization`
signal → score), lifts source diversity / Evidence / Velocity breadth, and **accrues the grant counts in
timestamped evidence metadata** — without misfiring Trajectory (Neutral contributes 0 to
`TrajectoryScore`). **Directional NEW-authorization / surge detection** (Positive when a *newly-appearing*
FCC ID or an accelerating count is seen vs the accrued authorization-evidence history this slice writes) is
a deferred **slice B** that changes the DIRECTION, not the type name — no separate history store is built
now.

This threads the normal `collect → map → resolve → review → store → score → report` path, so provenance is
intact end-to-end. Opt-in via `Radar:Collectors`; the default baseline run is byte-for-byte unchanged **in
scoring math** (see the Fingerprint section for the one automatic re-stamp).

---

## Assignment

Worktree: any — mostly new Infrastructure files under a new `Fcc/` folder + one additive extractor rule +
two append-only Domain enum members + additive DI + one enable-able collector kind + seed data. It edits
shared surfaces (`SignalType.cs`, `EvidenceSourceType.cs`, `KeywordSignalExtractor.cs`,
`RadarWorkerServices.cs`, `RadarWorkerOptions.cs`, `appsettings.json`,
`InfrastructureServiceCollectionExtensions.cs`, `data/companies.json`, and the fingerprint/descriptor
tests), so **sequence** it rather than parallelizing against any slice that touches the extractor rule
table, `SignalType`/`EvidenceSourceType`, the scoring fingerprint, Worker composition/DI, or the seed.
Dependencies: **127 (patents — must be MERGED first)** because this slice extracts the shared single-key
feed-token splitter and routes spec 127's `PatentFeedTarget` through it (reuse-over-copy), and because the
fingerprint re-pin is computed from the post-127 tree (`RuleSetVersion` v4 → v5). Also 103 (the
opt-in-off collector this mirrors — merged), 95 (`SignalSourceDescriptor` folds `RuleSetVersion` into the
fingerprint — merged), 97 (feed-Id folds feed type — merged), 98 (`SeedFeedInventoryValidator` — verify no
false warning; merged), 83 (`TwoKeyFeedToken` shared two-key splitter precedent — merged).
Conflicts with: **spec 127, 129, 130** (all bump `RuleSetVersion` and re-pin the same fingerprint) and any
slice touching `KeywordSignalExtractor` rules / `RuleSetVersion`, `SignalType`/`EvidenceSourceType`,
`ScoringConfigFingerprint`/its tests, Worker composition/DI, or the seed. **Do NOT dispatch in parallel
with any other `RuleSetVersion`-bumping collector.**
Estimated time: ~2–3 h

---

## Grounding facts to VERIFY (a short reachability spike) before dispatch

A new **external** source — confirm before/at implementation (exactly as specs 103/127 required a
maintainer-verified reachability spike). The collector + reader + parser + wiring + tests are **fully
offline** (fake `IFccAuthReader`, JSON/HTML fixtures), so the coder can complete the whole slice against
fixtures; only the live **endpoint/response schema** and the **seed grantee names** are the live gate.

- **Endpoint (FCC OET Equipment Authorization System / EAS).** The FCC publishes a public grantee/grant
  database searchable by grantee (applicant) name. **Confirm in the spike the exact reachable machine
  form** — a JSON/REST endpoint if one exists, else the EAS `GenericSearch` results (which may be
  HTML/CSV). Pin the **actual** observed request shape and response schema in the reader; the
  collector/extractor/wiring do not change if the schema differs. Capture per authorization: **FCC ID**,
  **grant date**, **grantee/applicant name**, and a short **product/equipment description** if present.
- **Query.** Filter by grantee organization name and a grant-date floor (last `LookbackDays`, default
  **180**). Request/parse only the fields the evidence needs. Pin field/operator names as named constants.
- **No API key required (confirm).** FCC EAS is a free public database. If the spike finds the reachable
  form needs no key, `IFccAuthReader` has no key handling; if it does, mirror spec 127's env-var posture
  (`Radar:Fcc:ApiKeyEnvVar`, missing key ⇒ `MissingApiKey` degrade, never committed). **Default assumption:
  no key** — do not add key plumbing unless the spike proves it necessary.
- **Grantee names (seed).** The per-company token is the company's **grantee/applicant organization name**
  as it appears on FCC grants (often a legal entity or a division). Verify **3–4** names that return
  non-empty results and seed only those; partial coverage is normal and expected (exactly like `usaspending`
  covering 3/43 and `hiringats` 4/43). First candidates to verify: **RKLB** (Rocket Lab), **MRCY** (Mercury
  Systems), **BELFB** (Bel Fuse), **ERII** (Energy Recovery).
- **Politeness.** Generic `User-Agent` on the named `HttpClient`; small bounded page size (the count
  matters, not full enumeration). No pagination beyond the first bounded page; cap at the reader's page
  size and note "≥ N" if the source reports a larger total.

---

## The signal decision (settled — encode as fixed; mirrors patents/hiring Neutral)

**v1 emits a NEUTRAL `EquipmentAuthorization` signal, not directional.** Rationale to state in code
comments and the PR:

- A **single-window authorization COUNT cannot distinguish** genuine product-cadence *acceleration* from a
  large hardware incumbent that *always* refreshes many SKUs. A mild-Positive on raw presence would
  systematically favour the most-prolific filers — the selection-bias trap the diversified universe avoids.
- So v1 **establishes the mechanism** (the FCC axis + `EquipmentAuthorization` source-type provenance +
  Evidence / Velocity breadth) and **accrues the counts in timestamped evidence metadata**, **without**
  moving Trajectory (Neutral contributes 0 to `TrajectoryScore`).
- **Directional NEW-authorization / surge detection** (Positive when a newly-appearing FCC ID or an
  accelerating count is seen vs the accrued authorization-evidence history) is **deferred to slice B**. The
  timestamped evidence metadata this slice writes **is** the record slice B will read. Same
  conservative-first / build-the-mechanism-defer-conviction pattern as patents (spec 127), the hiring
  Neutral (spec 103), and the 13G Neutral (spec 99).
- **Name is `EquipmentAuthorization`** on purpose (honest — it is authorization *activity*, not a proven
  acceleration). **Slice B changes the DIRECTION, not the type name.**

---

## Design

### 1. Domain — two append-only enum members (minimal)

- `EvidenceSourceType.EquipmentAuthorization` appended at the **end** of `EvidenceSourceType` (append-only;
  persisted by name; never reorder/remove). Do **not** add it to
  `EvidenceSourceTypes.IsThirdPartyAttentionSource` (first-party regulatory clearance, not market
  attention).
- `SignalType.EquipmentAuthorization` appended at the **end** of `SignalType`.
- Confirm no exhaustive `switch` over `SignalType`/`EvidenceSourceType` needs a new arm (there was none for
  `HiringActivity`/`PatentActivity` — the extractor maps by string and the formula folds by direction).

### 2. Extractor rule — one Neutral phrase (verbatim contract)

Add **one** rule group to the `KeywordSignalExtractor` `Rules` table (after the `PatentActivity` row from
spec 127), mapping the single fixed phrase the collector emits to `EquipmentAuthorization` **Neutral** at
routine strength (mirror the Neutral routine rows — hiring `3/4/0.45`, 13G `3/5/0.5`, patents `3/5/0.45`):

```csharp
// EquipmentAuthorization (FCC EAS; spec 128). The FccEquipmentAuthorizationCollector synthesizes exactly
// this fixed phrase into the EquipmentAuthorization evidence Title/RawText; the extractor only maps phrase
// -> fixed type+direction+strength (never re-derives valence — the PatentActivity/HiringActivity precedent).
// v1 is NEUTRAL by design: a single-window authorization COUNT cannot tell genuine product-cadence
// acceleration from an always-prolific filer, so it never misfires bullish (Neutral contributes 0 to
// Trajectory). Directional NEW-authorization/surge detection vs accrued history is deferred to slice B
// (changes DIRECTION, not this type name). NO raw product descriptions in searchable text (metadata only).
new("fcc equipment authorization (recent grants)", SignalType.EquipmentAuthorization, SignalDirection.Neutral, 3, 5, 0.45m),
```

- **ONE phrase** (simplest; keeps the verbatim contract tiny).
- Bump **`KeywordSignalExtractor.RuleSetVersion`** `"radar-keyword-rules-v4"` → **`"radar-keyword-rules-v5"`**
  and update the surrounding comment.
- **No-contamination rule:** the collector embeds **only** the fixed phrase + numeric count + grantee/window
  in Title/RawText — **never raw product descriptions or FCC IDs' free text** (a description like "wireless
  launch controller" could otherwise trip the `launches` rule). Sample FCC IDs + descriptions live in
  **metadata only** (the extractor does not scan metadata for phrases). State this in both the collector and
  the rule comment.

### 3. Infrastructure — reader seam + collector (new `Radar.Infrastructure/Fcc/` folder)

**Reader seam** (`internal`, mirrors the non-SEC reader pattern — `HttpUsaSpendingAwardReader`,
`HttpNewsSearchReader`, spec-103 job-board readers, spec-127 `HttpPatentSearchReader`):

- `EquipmentAuthorization(string FccId, string Description, DateOnly GrantDate)` — the normalized per-grant
  shape.
- `FccAuthResult(int GrantCount, IReadOnlyList<EquipmentAuthorization> Grants)` — `GrantCount` is the count
  of parsed grants in the returned page (deterministic from the payload); if the source reports a larger
  total, keep the parsed page count and record the source total in metadata as a cross-check only.
- `FccAuthOutcome` enum: `Success, Unreachable, HttpError, Timeout, Malformed` (mirror
  `UsaSpendingReadOutcome`; add `MissingApiKey` **only if** the spike proves a key is required).
- `FccAuthReadResult(FccAuthOutcome Outcome, FccAuthResult? Result, string? Detail)` with `IsSuccess` +
  `Success(...)`/`Failure(...)` factories (mirror `UsaSpendingReadResult`).
- `IFccAuthReader { Task<FccAuthReadResult> ReadAsync(string granteeName, DateOnly grantFloor, CancellationToken ct); }`.
- `HttpFccAuthReader : IFccAuthReader`: injected typed `HttpClient` + `System.Text.Json` (or the appropriate
  parser if the spike shows CSV/HTML), builds the EAS request (verified in the spike, operators pinned as
  constants), `HttpCompletionOption.ResponseHeadersRead`, materialize body before dispose, then the standard
  ladder: `HttpRequestException`→`Unreachable`, `TaskCanceledException`(non-ct)→`Timeout`, caller-cancellation
  re-throw (`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`), non-success
  status→`HttpError`, parse failure / unexpected root→`Malformed`, empty result set→`Success` with 0 grants
  (a valid no-recent-grants result, not an error). All HTTP/parse stays in `Radar.Infrastructure` (AD-5); all
  new types `internal`.

**Feed-token parser + shared single-key splitter extraction (reuse-over-copy — HARD RULE).**
- Extract a shared `internal static class SingleKeyFeedToken` in `Radar.Infrastructure.Sources` with
  `TrySplit(string trimmedToken, string key, out string value)` — the single-key analogue of the existing
  `TwoKeyFeedToken` (the `key=<value>` idiom). Route **both** spec-127's `PatentFeedTarget` **and** the new
  `FccFeedTarget` through it. Do **not** paste a divergent copy — `TwoKeyFeedToken`'s XML-doc explicitly
  frames per-parser blank-value policy as an explicit per-caller hook; keep that discipline.
- `FccFeedTarget(string GranteeName)` (`internal sealed record`, in `Radar.Infrastructure.Fcc`):
  `Parse(string?) -> FccFeedTarget?` for the token `grantee=<name>`; trimming/blank-null discipline;
  malformed/blank ⇒ `null` so the collector degrades to a `SourceFailure`.

**Collector** `FccEquipmentAuthorizationCollector : IEvidenceCollector`:

- `CollectorName => "fccauth"`, `SourceType => EvidenceSourceType.EquipmentAuthorization`.
- Injected: `IFccAuthReader`, `ILogger`, `TimeProvider`, `FccCollectorOptions`.
- Iterate `context.FeedsOfType("fccauth")` (deterministic order). Build `companiesById`. Per feed:
  `FccFeedTarget.Parse(feed.Url)`; `null` ⇒ `SourceFailure(feed.Name, feed.Url, "malformed fcc feed token")`
  + Warning + continue. Compute `grantFloor = today - options.LookbackDays` (default 180) via the injected
  `TimeProvider`. `ReadAsync(target.GranteeName, grantFloor, ct)`; `!IsSuccess` ⇒ `SourceFailure`
  (+ `result.Detail`) + continue.
- On success compute `count = result.Result.GrantCount`,
  `sample = result.Result.Grants.Take(options.MaxSampleAuthorizations)`, and emit **one**
  `CollectedEvidence` (`MapToEvidence`):
  - **Title** embeds the verbatim phrase + count, e.g.
    `"FCC equipment authorization (recent grants) — {count} authorizations granted to '{grantee}' in the last {lookbackDays} days"`.
  - **RawText** embeds the phrase + count + grantee + window + retrieved timestamp for hash distinctness (NO
    raw product descriptions), e.g. `"Grantee '{grantee}': {count} FCC equipment authorizations granted
    since {grantFloor:o}, as of {retrievedAtUtc:o}. Signal: fcc equipment authorization (recent grants)."`.
  - `SourceUrl` = a stable human-viewable EAS grantee link if the spike finds one, else the query URL;
    `PublishedAt` = `CollectedAt` = `TimeProvider.GetUtcNow()` (a snapshot window has no single publish date).
    **Each run produces a distinct timestamped snapshot** (the RawText timestamp makes the ContentHash
    distinct run-to-run) — this **is** the accrued authorization history slice B reads; state this explicitly.
  - **Metadata** (`Dictionary<string,string>`, Ordinal): `quality = "High"` (FCC grants are an authoritative
    public-record source, on par with SEC/USASpending `High`); `fccFeedUrl` (= `feed.Url`); `grantee`;
    `grantCount`; `lookbackDays`; `grantFloor` (`o`/`yyyy-MM-dd`); `sampleAuthorizations` (the first N
    `"{fccId}: {description}"` joined with `" | "` — provenance/debug only, **not** scanned by the extractor);
    `sourceReportedTotal` (optional cross-check); `retrievedAtUtc` (`o`).
  - `CompanyHints = CollectorCompanyHints.For(feed.CompanyId, companiesById)` — never invent a ticker.
- Populate `CollectionSummary` exactly like `SecForm4Collector`/`UsaSpendingContractCollector`/
  `HiringBoardCollector`/`PatentActivityCollector` (checked, ok, failed, count, failures); log per-feed +
  aggregate.
- `FccCollectorOptions`: `LookbackDays` (default 180), `MaxSampleAuthorizations` (default 5), `MaxPageSize`
  (default 100). (Add `ApiKeyEnvVar` only if the spike proves a key is required.)

### 4. Feed-token seed (additive, `data/companies.json`)

Add one `fccauth` feed to the **3–4** companies verified in the reachability spike (record the exact grantee
names here as the seed of record; leave the other names out — partial coverage is normal, exactly like
`usaspending`/`hiringats`/`patents`). Token form `grantee=<verified grantee organization name>`; feed `name`
e.g. `"Rocket Lab — Recent FCC equipment authorizations (EAS)"`. Additive edit only — leave every existing
feed untouched. Per spec 97 the feed-Id folds the feed **type**, so these do not collide with any existing
feed (incl. the same company's `patents` feed).

### 5. Wiring (opt-in, OFF by default)

- `AddFccEquipmentAuthorizationCollector(this IServiceCollection, FccCollectorOptions options)` in
  `InfrastructureServiceCollectionExtensions.cs`: register a named typed `HttpClient`
  (`ConfigurePrimaryHttpMessageHandler` with gzip/deflate `AutomaticDecompression`, generic `User-Agent`,
  mirroring `AddUsaSpendingContractCollector`/`AddPatentActivityCollector`), register `IFccAuthReader`,
  `AddSingleton(options)`, `TryAddSingleton(TimeProvider.System)`, and
  `AddSingleton<IEvidenceCollector, FccEquipmentAuthorizationCollector>()`.
- `RadarWorkerServices.cs`: add a `"fccauth"` enable-able kind branch (register via
  `AddFccEquipmentAuthorizationCollector` from a new `RadarWorkerOptions.Fcc` block); extend **all three**
  valid-kinds messages (the empty-list, the null/blank-entry, and the unknown-kind messages) to include
  `"fccauth"`.
- `RadarWorkerOptions.cs`: add `FccWorkerOptions Fcc { get; init; } = new();` (bound from `Radar:Fcc`) with
  `LookbackDays` (180), `MaxSampleAuthorizations` (5), `MaxPageSize` (100), mirroring the other
  `*WorkerOptions` blocks.
- `appsettings.json`: add a documented, disabled-by-default `Radar:Fcc` section; **leave `Radar:Collectors`
  default unchanged** (fccauth stays opt-in).
- **`scripts/run-profiles/default.json`: do NOT add `"fccauth"` to `Collectors`.** It stays opt-in/off.
  Promoting it into the baseline is a later deliberate fingerprint re-stamp after a live measurement
  validates the axis (mirrors how `secform4`/`sec13dg` were promoted) — out of scope.

---

## Fingerprint + sequencing (load-bearing)

Shipping this bumps `KeywordSignalExtractor.RuleSetVersion` **v4 → v5**, which spec 95's
`SignalSourceDescriptor` folds into the scoring-config fingerprint **independently of which collectors are
enabled**. Therefore:

- **The DEFAULT/baseline fingerprint re-stamps the moment this merges — even though `fccauth` is
  opt-in-OFF.** The default descriptor's enabled-collector CSV is **unchanged** (still the 6-collector set);
  the fingerprint moves **solely** because `rules=radar-keyword-rules-v4` → `…-v5`. There is no
  fingerprint-safe way to add a new scoring-affecting signal type — this is spec 95 working as intended
  (exactly as specs 103/127 re-stamped).
- **No `_formula.Version` bump** (formula shape unchanged — `radar-formula-v8` stays). Only `RuleSetVersion`
  bumps. No weight / attention-tier / insider-tier change.
- **Efficacy interaction (spec 101/108) — this re-stamp is SCORE-NEUTRAL, a cosmetic boundary.** The
  collector is opt-in-OFF and the new rule matches only the FCC phrase (which no existing evidence contains),
  so **every company scores byte-identical before and after** — only the fingerprint *string* changes. Spec
  108's continuity-aware segmentation connects the score line across a score-continuous re-stamp; state
  plainly that this boundary is an input-hash artifact, not a measurement break.
- **Re-pin the fingerprint by RUNNING the test, not by hand** (same procedure as specs 103/127): update the
  `RuleSetVersion` literal to v5, run `dotnet test`, read the **actual** default fingerprints the failing
  pins report, and paste those exact values in. Do **not** hand-compute a SHA. The re-pin surface
  (grep-verify against the post-127 tree before editing):
  - `src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs` — the `RuleSetVersion` const (+ comment).
  - `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — the `…rules-v4…` descriptor
    literal → `…rules-v5…`; the `ChangedSignalSourceDescriptor` variant; the pinned **AI-OFF** default
    fingerprint → the NEW value from the run; the lineage comment.
  - `tests/Radar.Application.Tests/Scoring/SignalSourceDescriptorTests.cs` — `…rules-v4…` → `…rules-v5…`.
  - `tests/Radar.Application.Tests/Scoring/ScoringEngineTests.cs` — any `…rules-v4…` literal.
  - `tests/Radar.Infrastructure.Tests/FileSystem/FileScoringConfigStoreTests.cs` — the fixture descriptor
    `…rules-v4…` → `…rules-v5…` **if** it fails.
  - `scripts/run-profiles/default.json` — the `_comment`'s `radar-keyword-rules-v4` → `v5` reference and the
    AI-OFF / AI-ON default-fingerprint references (re-pin from the RUN, never by hand; note the AI-ON
    transition in the comment if no test pins it live).
  - `docs/architecture-decisions.md` — add an AD-10 lineage line recording `RuleSetVersion` v4→v5 (new
    `EquipmentAuthorization` rule group) and the default fingerprint transition(s) (old → new), noting scoring
    math is byte-identical and the collector is opt-in-off (mirror the spec-103/127 lineage entries).
- **Spec-98 collection-health check:** a declared-but-disabled `fccauth` feed type has **declared ==
  reached** ⇒ **no** `feeds-lost-before-collection` warning. **No `SeedFeedInventoryValidator` change.** Prove
  it with a test; introduce no spurious warning on a default (fcc-off) run.

---

## Tests

- **`HttpFccAuthReaderTests`** (offline, fake `HttpMessageHandler`): a fixture in the spike's confirmed shape
  → correct `GrantCount` + FCC IDs + grant dates; empty result set → `Success` with 0 grants; missing/
  unexpected root → `Malformed`; non-success status → `HttpError`; thrown `TaskCanceledException` (timeout) →
  `Timeout`; `HttpRequestException` → `Unreachable`; caller cancellation re-throws. **No network.**
- **`SingleKeyFeedTokenTests`**: `key=<value>` splits (value may contain spaces/commas); missing key / blank
  value / blank token behave per the caller's policy; whitespace trimmed. Prove `PatentFeedTarget` (from
  spec 127) still parses identically after being routed through the shared splitter (regression guard).
- **`FccFeedTargetTests`**: `grantee=Rocket Lab USA, Inc.` parses; missing key / blank value / blank token →
  `null`; whitespace trimmed.
- **`FccEquipmentAuthorizationCollectorTests`** (fake `IFccAuthReader`): a successful search → **one**
  `EquipmentAuthorization` `CollectedEvidence` whose Title/RawText contain the verbatim
  `fcc equipment authorization (recent grants)` phrase and the count (and **no** raw product descriptions);
  metadata carries `grantee`/`grantCount`/`lookbackDays`/`grantFloor`/`sampleAuthorizations`/`retrievedAtUtc`/
  `quality=High`; `CompanyHints` = feed-bound ticker; UTC instants from the injected `TimeProvider`;
  `grantFloor` = now − `LookbackDays`; a malformed token and a reader failure each degrade to a
  `SourceFailure`/no-evidence without throwing; `CollectionSummary` counts correct; deterministic order.
- **Extractor mapping** (extend `KeywordSignalExtractorTests`): `EquipmentAuthorization` evidence whose text
  contains `fcc equipment authorization (recent grants)` → exactly one `EquipmentAuthorization` **Neutral**
  signal at 3/5/0.45, with a verbatim excerpt; and a guard that the phrase does **not** also fire an
  unrelated rule (and that a sample product description placed only in metadata does not leak into matching).
- **Fingerprint re-pin**: `ScoringConfigFingerprintTests` green with the `RuleSetVersion`-v5 descriptor and
  the NEW pinned default fingerprint(s) (obtained by running); `SignalSourceDescriptorTests`,
  `ScoringEngineTests`, `FileScoringConfigStoreTests` updated to v5 where they pin it.
- **Default (fcc-off) composition / no false warning**: a Worker-DI or runner test with the default
  `Collectors` (no `fccauth`) but the seed containing the `fccauth` feeds registers the collector set
  **without** `fccauth` and produces **no** `feeds-lost-before-collection` warning for the `fccauth` feed
  type. If a Worker-DI test asserts the enable-able kind list, extend it to include `"fccauth"`.
- **Seed**: a `LocalFileCompanySeedSource` test asserts the verified companies carry an `fccauth` feed with
  the exact grantee tokens, each with a distinct feed Id (does not collide with the same company's other
  feeds — spec 97).
- Existing tests (all collectors, runner merge, DI list, Worker DI, extractor, scoring) stay green. Full
  gate: `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic collector + rule-based mapping; **NO AI**.
- All HTTP/parse confined to `Radar.Infrastructure` (AD-5); no provider SDK; no DB (AD-8). Reuse
  `CollectorCompanyHints`, `CollectionSummary`/`SourceFailure`, the `CollectedEvidence`/
  `CollectedEvidenceMapper` path, and the established non-SEC reader pattern. **Extract and share the
  single-key feed-token splitter (`SingleKeyFeedToken`) — do NOT paste a divergent copy** (reuse-over-copy
  hard rule; `TwoKeyFeedToken` is the two-key precedent). Do **not** reuse SEC-specific helpers.
- Append-only Domain enum edits (add at the END; never reorder/remove; persisted by name).
- Graceful degradation: typed non-throwing outcomes; a company with zero recent authorizations is a valid
  `Success` (not an error); a bad token / unreachable source yields a `SourceFailure` + zero evidence; only
  genuine caller cancellation propagates.
- **Provenance preserved** (FCC EAS → `EquipmentAuthorization` evidence with grantee + count + window →
  `EquipmentAuthorization` signal → score). **No advice language** (AD-9): the factual "fcc equipment
  authorization (recent grants)" phrasing carries no recommendation; direction is internal to scoring (and
  Neutral in v1).
- Store timestamps in UTC; IDs `Guid`. Keep `MaxSampleAuthorizations`/`MaxPageSize` bounded. Sample product
  descriptions are metadata-only and never enter the extractor's searchable text.
- **Only `RuleSetVersion` bumps** (v4→v5); **no** `_formula.Version` / weight / tier change.
  `ScoringConfigVersion` re-stamps automatically via `SignalSourceDescriptor`; re-pin the tests to the value
  obtained by RUNNING them.

---

## Out of scope / future slices (record, do not build)

- **Slice B — directional NEW-authorization / surge detection** (Positive when a newly-appearing FCC ID or an
  accelerating count is seen vs the accrued authorization history this slice writes). Changes the
  `EquipmentAuthorization` **direction**, not the type name. No separate history store needed.
- **Promoting `fccauth` into the baseline `default.json`** — a later deliberate fingerprint re-stamp after a
  live measurement validates the axis.
- **Additional grantee names / division de-duplication** (a company may hold grants under several grantee
  codes) — additive seed edits + optional multi-grantee token later.
- **Product-class / equipment-type enrichment** — a richer materiality read; future slice.

---

## Acceptance criteria

- [ ] Two append-only Domain members added at the END of their enums:
      `EvidenceSourceType.EquipmentAuthorization` (NOT in `IsThirdPartyAttentionSource`) and
      `SignalType.EquipmentAuthorization`; no exhaustive `SignalType`/`EvidenceSourceType` switch broken.
- [ ] One `KeywordSignalExtractor` rule maps the verbatim phrase
      `fcc equipment authorization (recent grants)` → `EquipmentAuthorization` **Neutral** (3/5/0.45); no
      materiality metadata read; the phrase does not trip another rule; `RuleSetVersion` bumped
      `radar-keyword-rules-v4` → `v5`.
- [ ] `FccEquipmentAuthorizationCollector` (kind `"fccauth"`, `CollectorName "fccauth"`, `SourceType
      EquipmentAuthorization`) reads `FeedsOfType("fccauth")`, parses the `grantee=…` token via the shared
      `SingleKeyFeedToken`, reads recent authorizations via `IFccAuthReader` (FCC EAS), and emits **one**
      `EquipmentAuthorization` evidence per company with the fixed phrase in Title/RawText (no raw
      descriptions) + rich metadata (grantee, count, window, sample authorizations, retrievedAtUtc,
      `quality=High`) + feed-bound hint.
- [ ] Reader returns typed non-throwing outcomes (empty result = `Success` 0 grants); malformed token /
      reader failure degrade to `SourceFailure` + zero evidence; caller cancellation propagates.
- [ ] `SingleKeyFeedToken` extracted into `Radar.Infrastructure.Sources` and **both** `PatentFeedTarget`
      (spec 127) and `FccFeedTarget` routed through it (no divergent copy); `PatentFeedTarget` parsing
      unchanged (regression test green).
- [ ] Additively registered via `AddFccEquipmentAuthorizationCollector`; enable-able by `Radar:Collectors`
      containing `"fccauth"`; **default `Radar:Collectors` and `scripts/run-profiles/default.json` unchanged
      (opt-in OFF)**; the verified `fccauth` feeds seeded in `data/companies.json` with the exact grantee
      tokens, each with a distinct feed Id (spec 97).
- [ ] A default (fcc-off) run/composition registers the collector set **without** `fccauth` and produces
      **no** `feeds-lost-before-collection` warning for the declared `fccauth` feeds (spec-98 interaction; no
      validator change).
- [ ] **No `_formula.Version`/weight/tier change; only `RuleSetVersion` v4→v5.** The default fingerprint(s)
      re-stamp automatically; `ScoringConfigFingerprintTests` (and the other v4-pinning tests) re-pinned to
      the value(s) obtained by **running** the test; `default.json` `_comment` + `architecture-decisions.md`
      AD-10 lineage updated; the efficacy-segment boundary is noted as score-neutral.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
