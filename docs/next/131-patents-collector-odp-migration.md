# Task: Repoint the patent-activity collector from the retired PatentsView API to the USPTO Open Data Portal

> **FIX / ENDPOINT MIGRATION for spec 127 — not a new collector, not part of the sequential
> `RuleSetVersion` chain.** Spec 127 shipped the opt-in `patents` collector against the PatentsView
> PatentSearch API (`https://search.patentsview.org/api/v1/patent/`). That host was **retired** when
> PatentsView migrated into the USPTO Open Data Portal on **2026-03-20**; `search.patentsview.org` now
> returns **NXDOMAIN** (the DNS record is gone). Because the collector is **opt-in / OFF by default**,
> nothing in the baseline broke — but the collector cannot function until its reader is repointed at the
> replacement API (`https://api.uspto.gov`). This slice changes the **reader only** (base URL + request
> shape + response field mapping). The collector's emitted fixed phrase and the `KeywordSignalExtractor`
> rule are **unchanged**, so — unlike specs 128/129/130 — this slice does **NOT** bump `RuleSetVersion`
> and does **NOT** move the scoring fingerprint.

## Overview

The `patents` collector (spec 127) counts recently-granted patents per seeded company and emits **one**
Neutral `Patent` `CollectedEvidence` per company carrying the fixed phrase `patent activity (recent
grants)`. Only its `IPatentSearchReader` (`HttpPatentSearchReader`) talked to the now-dead PatentsView
host. This slice replaces that reader's target with the USPTO Open Data Portal (ODP) **Patent File
Wrapper (PFW) Search API**, preserving every downstream contract:

- **Same emitted phrase** → same extractor rule → same `SignalType.PatentActivity` (Neutral) → **same
  fingerprint**. No Domain, extractor, scoring, or fingerprint change.
- **Same reader outcome contract** (`PatentSearchOutcome`: `Success / Unreachable / HttpError / Timeout /
  Malformed / MissingApiKey`) and the same collector degrade-to-`SourceFailure`-on-error posture.
- **Same opt-in / OFF-by-default** posture; the baseline 6-collector set is untouched.

Only the reader's **transport** and **parse** change: new host, new request body, new response field names.

## Spike findings (ALREADY DONE — embedded so they are not rediscovered)

A live reachability spike was run on **2026-07-23**. Encode these as the reader's pinned assumptions;
re-confirm only the two items flagged **(needs key)** during implementation.

- **Old host is permanently gone.** `curl https://search.patentsview.org/...` → connection fails; DNS
  lookup returns **NXDOMAIN**. `https://patentsview.org` now 301-redirects wholesale to
  `https://data.uspto.gov/support/transition-guide/patentsview`. There is no drop-in PatentsView host.
- **New host is live and key-gated.** `https://api.uspto.gov` responds (403 at root, expected). The search
  endpoint **`POST https://api.uspto.gov/api/v1/patent/applications/search`** exists — both GET and POST
  return **`401 {"message":"Unauthorized"}`** without a key (endpoint present, auth required).
- **Query model (superset of the old one).** The PFW Search API searches "all patent application
  **bibliographic / front-page** fields," refreshed **daily**, and supports a JSON POST body with
  `q` (OpenSearch query string) + `filters` + **`rangeFilters` (Django-style `__gte` / `__lte` on date
  fields)** + `fields` (projection) + `facets` + pagination. This is strictly more capable than the old
  PatentsView `q`/`f`/`o` shape.
- **Auth header.** API key in the **`X-Api-Key`** request header (same header name spec 127 already sends —
  the reader's header wiring is unchanged; only the value's provenance changes).
- **Key acquisition (documented for the maintainer, not a coding step).** The key is issued from a USPTO
  Open Data Portal account (`https://data.uspto.gov/myodp`), which requires a **USPTO.gov account verified
  with ID.me**. A no-SSN **non-U.S. passport** ID.me path exists (passport + address document + a short
  video call). See [[radar-project-state]] / the collector how-to note.
- **(needs key) Exact field names — the one residual unknown.** The public docs are a JS SPA (the query-spec
  PDF and `openapi.json` both return the Angular shell), so the exact camelCase field names for **grant
  date**, **assignee / applicant organization**, **granted patent number**, and **invention title** could
  **not** be pinned without an authenticated call. `q`/`filters`/`rangeFilters` + "bibliographic/front-page
  fields" make it near-certain these are queryable; the implementation **must** pin them from one real
  authenticated response (see Acceptance criteria) or the live OpenAPI once signed in.
- **(needs key) Assignee-name match quality.** Confirm the applicant/assignee-organization filter matches
  our seed company names well enough to return that company's own recent grants (assignee harmonization is
  the classic patent-data pitfall). Partial coverage is fine and expected (like `usaspending` 3/43); seed
  only names verified to return non-empty results.

## Assignment

Worktree: any — edits are confined to the patents reader/options/wiring introduced by spec 127
(`Radar.Infrastructure/Patents/HttpPatentSearchReader.cs`, `PatentCollectorOptions`, the reader's request
builder + response DTOs, `InfrastructureServiceCollectionExtensions.cs` patents registration,
`appsettings.json` `Radar:Patents` block, and the reader's unit tests + fixtures). It does **NOT** touch
`SignalType.cs`, `EvidenceSourceType.cs`, `KeywordSignalExtractor.cs`, the scoring fingerprint, or its
tests.
Dependencies: **127 (patents collector — MERGED, PR #132 / `c473a64`)** — this repoints the reader 127
introduced. No other dependency.
Conflicts with: nothing in the `RuleSetVersion`/fingerprint chain — because this slice does **not** change
the extractor rule or the fingerprint, it is **independent of specs 128/129/130 and can be implemented in
any order relative to them** (it touches only patents-reader files they don't). The only true blocker is
**external**: the live smoke-test acceptance step needs an ODP API key in hand.
Estimated time: ~1.5–2 h (offline repoint), + one authenticated smoke call to pin fields.

---

## Design

### 1. No Domain / extractor / scoring change (the load-bearing property)

`EvidenceSourceType.Patent`, `SignalType.PatentActivity`, the `KeywordSignalExtractor` rule mapping
`patent activity (recent grants)` → Neutral `PatentActivity` (3 / 5 / 0.45), `RuleSetVersion`, and the
scoring fingerprint are **all unchanged**. The collector still synthesizes the identical evidence phrase.
This slice must leave every fingerprint pin exactly as spec 127 left it — see Tests.

### 2. Reader transport — target the ODP PFW Search API

Replace the PatentsView request/response handling in `HttpPatentSearchReader` (or the collector's request
builder) with the ODP form:

- **Base URL configurable.** Add `Radar:Patents:BaseUrl` (default `https://api.uspto.gov`) and the search
  path `/api/v1/patent/applications/search`, so a future host move is a config edit, not a code change.
  (Spec 127 hard-coded `search.patentsview.org`; the migration is the moment to make it configurable.)
- **Request.** `POST` a JSON body: a grant-date `rangeFilters` floor (`now − LookbackDays`, default 180,
  via the injected `TimeProvider`) **AND** an applicant/assignee-organization filter for the seed token,
  with a `fields` projection limited to what the evidence needs (patent number, grant date, title,
  assignee) and a bounded page size (`MaxPageSize`, default 100 — the **count** matters, not full
  enumeration). Pin the exact field/operator names as named constants once observed.
- **Auth.** Continue sending `X-Api-Key` from the env var named by `Radar:Patents:ApiKeyEnvVar`. Keep the
  default var name **`PATENTSVIEW_API_KEY`** for back-compat, but document that the value is now a **USPTO
  ODP** key; optionally also accept `USPTO_ODP_API_KEY` as an alias (cosmetic — do not break the existing
  default). Blank key ⇒ `MissingApiKey`, **no HTTP call**, clear log (unchanged behaviour).

### 3. Reader parse — map ODP response → grant count + sample titles

Map the ODP response's records into the existing reader result (recent-grant count within the window + up
to `MaxSampleTitles`, default 5, sample invention titles + a stable human-viewable link if one exists). A
missing/absent `patent_date`-equivalent row is **skipped**, not coerced (this is exactly the
Copilot-flagged fix already applied in spec 127's `ParseGrantDate` returning `DateOnly?` — carry the same
skip-don't-coerce discipline to the ODP field). Malformed root / missing results container ⇒ `Malformed`;
timeout ⇒ `Timeout`; `HttpRequestException` ⇒ `Unreachable`; `401`/other non-2xx ⇒ `HttpError`;
cancellation re-throws. No outcome-enum change.

### 4. Seed re-verification

Re-confirm the seed grantee/assignee tokens against the **new** API (spec 127 seeded MRCY/ERII/EOSE
against the old host). The ODP assignee-name matching may differ; keep only seed names that return
non-empty recent-grant results from `api.uspto.gov`. Update `data/companies.json` patents feed tokens
only if the verified names differ. Still additive; no feed-Id churn beyond corrected tokens.

## Fingerprint + sequencing (load-bearing)

- **No `RuleSetVersion` bump. No fingerprint move.** This slice changes transport/parse, not the rule
  table, so the AI-OFF / AI-ON default fingerprints must remain **exactly** those spec 127 stamped
  (`cb80a5809882` → `b4a040144f66` AI-OFF, `c908f03a554a` → `63c096e531ec` AI-ON were 127's moves; this
  slice moves them **no further**). `ScoringConfigFingerprintTests` and siblings must stay **green with no
  pin edit** — if any fingerprint test needs re-pinning, the change has leaked scope and is wrong.
- **Independent of the collector chain.** Because it neither bumps `RuleSetVersion` nor touches the
  extractor/enum/fingerprint surfaces, it may land before, between, or after 128/129/130 without a
  cross-fingerprint conflict. It shares no files with them.
- **The real gate is the ODP key.** The offline repoint (request builder + DTOs + parse) can be built
  against a captured fixture; the live smoke-test acceptance step needs the key.

## Tests

- Reader unit tests updated to the ODP request/response shape, driven by **captured JSON fixtures** (a real
  ODP response saved during the smoke call, secrets stripped). Cover: happy path (N grants in window →
  count + sample titles), empty result (`Success`, 0 grants), a row with an **absent/unparseable grant
  date is skipped** (regression-lock the skip-don't-coerce behaviour), blank API key ⇒ `MissingApiKey`
  with **no** HTTP call, malformed body ⇒ `Malformed`, non-2xx ⇒ `HttpError`, cancellation propagates.
- A test asserting the request targets the **configured** `BaseUrl` + path and carries the `X-Api-Key`
  header, and that `Radar:Patents:BaseUrl` override is honoured.
- **Fingerprint guard:** the existing `ScoringConfigFingerprintTests` (and the descriptor tests) remain
  green **unmodified** — an explicit assertion that this slice did not move the fingerprint.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **Secret handling:** the ODP API key is read at runtime from the env-var named in config and is **never**
  written to logs, evidence, provenance, run records, or committed files (unchanged from spec 127).
- **Reuse over copy:** reuse the existing non-SEC reader HTTP scaffolding (`SecHttpFetch` is SEC-only; the
  patents/usaspending readers already share the `HttpClient`/JSON posture — route through the established
  primitives, do not paste a new HTTP/JSON helper).
- Provenance intact: one `Patent` evidence per covered company, metadata records the assignee query, the
  grant window, the count, sample patent numbers/titles, the source host, and the retrieved timestamp.

## Out of scope / future slices (record, do not build)

- **Directional slice B** (Positive on a *newly-appearing* grant / accelerating count vs the accrued
  patent-evidence history) — unchanged from spec 127's deferral; this slice keeps v1 **Neutral**.
- **Enabling `patents` in the baseline** — stays opt-in / OFF; enabling it live is a maintainer action once
  the key exists.
- **Renaming the env var / config key beyond the optional cosmetic alias** — keep `PATENTSVIEW_API_KEY`
  working; a hard rename is a separate, lower-value cleanup.

## Acceptance criteria

- [ ] `HttpPatentSearchReader` targets **`POST {Radar:Patents:BaseUrl}/api/v1/patent/applications/search`**
      (default host `https://api.uspto.gov`), sends `X-Api-Key`, and builds an ODP `rangeFilters`
      grant-date-floor + assignee-organization query with a bounded `fields`/page size. The old
      `search.patentsview.org` target is **fully removed** (no dead reference).
- [ ] The exact ODP field names for grant date, assignee/applicant org, patent number, and title are
      **pinned as named constants from one real authenticated response**, and a sanitized fixture of that
      response is committed for the tests. (This is the step that consumes the ODP key.)
- [ ] At least **one** seed company returns non-empty recent-grant results from `api.uspto.gov`;
      `data/companies.json` patents tokens corrected to only verified names.
- [ ] Reader outcome contract preserved (empty ⇒ `Success` 0; blank key ⇒ `MissingApiKey`, no HTTP call;
      unparseable grant date row **skipped**, not coerced; malformed ⇒ `Malformed`; non-2xx ⇒ `HttpError`;
      timeout ⇒ `Timeout`; `HttpRequestException` ⇒ `Unreachable`; cancellation re-throws).
- [ ] **No `_formula.Version` / `RuleSetVersion` / weight / tier / enum change; the default fingerprint(s)
      are byte-identical to spec 127's post-merge values** — `ScoringConfigFingerprintTests` and siblings
      green **without any pin edit**.
- [ ] `patents` remains opt-in / OFF by default; default `Radar:Collectors` and
      `scripts/run-profiles/default.json` unchanged; no `feeds-lost-before-collection` warning for the
      declared `patents` feeds on a default (patents-off) run.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
