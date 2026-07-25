# Task: FCC equipment-authorization collector via decoupled browser fetch → cache → file reader

> **SUPERSEDES the transport of spec 128 (PR #133).** Spec 128 built the `fccauth` collector against an
> `HttpClient` → FCC EAS GenericSearch reader (`HttpFccAuthReader`). That transport is **proven unworkable**:
> `apps.fcc.gov` EAS is **Akamai Bot Manager**-walled and returns `403 Access Denied` to *any* non-browser
> HTTP client, **regardless of IP** (verified from both datacenter and the maintainer's residential egress
> on 2026-07-23). There is **no sanctioned bulk/API grants source** — the only open-data surface
> (`opendata.fcc.gov` Socrata `3b3k-34jp`) carries **grantee *registrations*** (one-time `date_received`),
> not per-device *grants*. **A real Chromium DOES pass the wall** (live-spiked 2026-07-23 — see below), so
> FCC grant data requires a **browser**. This spec keeps FCC viable by **decoupling** the browser from the
> Worker: a standalone Playwright fetch step writes a provenance-stamped cache file; the `fccauth` collector
> becomes a plain **file reader** (no `HttpClient`, no browser in `Radar.Worker`, zero baseline risk). It
> stays **opt-in / OFF by default**.

## Spike findings (LIVE, 2026-07-23 — encode as the pinned contract)

A Playwright/Chromium spike was run against the real EAS search. Results:

- **Chromium clears Akamai.** `https://apps.fcc.gov/oetcf/eas/reports/GenericSearch.cfm` loaded (title *FCC
  OET Authorization Search*), and a submitted query returned results (title *OET Authorization Search
  Results*, body text `"56 results were found that match the search criteria"`). No Access Denied.
- **Form contract.** `form[name=generic_search_form]`, **POST** to
  `https://apps.fcc.gov/oetcf/eas/reports/GenericSearchResult.cfm?RequestTimeout=500`. Query fields:
  `applicant_name`, `grantee_code`, `grant_date_from`, `grant_date_to` (dates `MM/DD/YYYY`),
  `application_purpose`, `grant_code_1..3`, `application_status`, `tcb_code`, `product_code`.
- **Results-grid columns (pin these):** `Applicant Name`, `Address`, `City`, `State`, `Country`,
  `Zip Code`, **`FCC ID`**, **`Application Purpose`** (e.g. *Original Equipment*), **`Final Action Date`**
  (= the grant date, `MM/DD/YYYY`), `Lower Frequency In MHz`, `Upper Frequency In MHz`. Each row links to a
  grant detail (`Eas731GrantForm.cfm?...&fcc_id=<FCC ID>`), giving a stable per-grant provenance URL.
- **⚠️ Seed accuracy is the real risk (demonstrated).** `applicant_name=Mercury` matched **GUANGZHOU
  MERCURY NAVIGATION TECHNOLOGY** and **MERCURY Corporation (South Korea)** — **NOT** Mercury Systems Inc
  (`MRCY`). A loose substring counts unrelated companies' grants. **The seed token per company MUST be the
  exact applicant name or (preferably) the FCC `grantee_code`**, verified via the fetch step to resolve to
  the correct entity. Partial coverage is expected and fine (e.g. Energy Recovery makes pumps — likely zero
  RF devices; seed only companies that resolve to real, correct grants).

## Architecture (decoupled — the load-bearing decision)

```
[ standalone Playwright fetch step ]  →  data/fcc-cache/{ticker}.json  →  [ fccauth collector = file reader ]
  own script + own schedule (weekly)      provenance-stamped grant list      no HttpClient, no browser,
  NOT part of Radar.Worker                (fetchedAtUtc, sourceUrl, query)    deterministic, baseline-safe
```

### Part A — the fetch step (NEW, outside `Radar.Worker`)

A standalone step — `scripts/fetch-fcc-eas.ps1` driving Playwright, **or** a small dedicated console tool
(`tools/` or a `Radar.Tools.FccFetch` project) using `Microsoft.Playwright` — that:

1. Reads the same seeded `fccauth` feed tokens the collector uses (exact applicant name / `grantee_code`).
2. Launches Chromium, navigates GenericSearch once (to acquire the Akamai clearance cookie), then for each
   seed submits the form with the grantee token + `grant_date_from = today − LookbackDays` (default 180) …
   `grant_date_to = today`.
3. Scrapes the results grid into typed rows (`fccId`, `applicationPurpose`, `finalActionDate`,
   `applicantName`, `freqLowMhz`, `freqHighMhz`) and writes **one cache file per company** to
   `data/fcc-cache/{ticker}.json` with provenance: `fetchedAtUtc`, `sourceUrl` (the results URL + query),
   `grantee` token, `lookbackWindow`, and the row list. Also record the source-reported total count.
4. Is **resilient and isolated**: a fetch failure (Akamai change, timeout, zero matches) writes nothing new
   / leaves the prior cache — it must **never** be able to break a Worker run. Politeness: one query per
   seed, small bounded result read, generic browser UA, weekly cadence is ample.

`data/fcc-cache/` is **gitignored** (generated data, like `data/evidence` etc.).

### Part B — the collector (REVISE from PR #133, do not rebuild)

Reuse spec 128's `FccEquipmentAuthorizationCollector`, the Neutral extractor rule
(`fcc equipment authorization (recent grants)`, `3/5/0.45`), the `EquipmentAuthorization`
`EvidenceSourceType`/`SignalType` enum members, the `SingleKeyFeedToken`/`FccFeedTarget` parser, the DI
wiring, and the seed mechanism **unchanged**. **Only the reader transport changes:**

- Replace `HttpFccAuthReader` (HTTP → EAS, dead) with a **`FileFccAuthReader`** that reads
  `data/fcc-cache/{ticker}.json`, filters rows to `finalActionDate >= now − LookbackDays` (injected
  `TimeProvider`), and returns the same reader result (count + up to `MaxSampleTitles` sample FCC IDs).
  Preserve the outcome ladder shape (missing/empty cache ⇒ `Success` 0 grants; malformed file ⇒ `Malformed`;
  the `MissingApiKey` case is dropped — no key). The collector still emits **one** Neutral
  `EquipmentAuthorization` evidence per covered company with the fixed phrase + count + metadata (grantee,
  window, count, sample FCC IDs, `sourceUrl`, the cache `fetchedAtUtc`).
- Keep the spec-128 Copilot fixes conceptually: skip rows with an unparseable `finalActionDate` (don't
  coerce); if the fetch step capped its scrape, propagate a truncated `"{count}+"` + `grantCountTruncated`
  flag through the cache so the collector renders a lower bound.

## Disposition of PR #133

Close PR #133 (its `HttpFccAuthReader` transport is proven dead). Its reusable parts
(collector/extractor/enums/wiring/`SingleKeyFeedToken`) are carried forward by this spec — the coder may
branch from #133 and swap the reader + add the fetch step, or re-implement from `main`; either way the
**net diff vs `main`** must match this spec (fingerprint move identical to #133: `RuleSetVersion` v4→v5).

## Fingerprint + sequencing

- **`RuleSetVersion` v4 → v5** (the FCC extractor rule + `EquipmentAuthorization` enums are introduced here,
  same as #133) ⇒ the default fingerprint re-stamps **score-neutrally** (opt-in-OFF; the rule matches only
  the collector's synthesized phrase). Re-pin `ScoringConfigFingerprintTests` + siblings **by running**;
  add the AD-10 lineage line. If FDA (spec 129) has already merged and taken v5, this becomes the next
  version — the coder pins from the actual post-merge tree.
- **Independent of the FDA/trademark chain content-wise** but shares the `RuleSetVersion`/fingerprint
  surface, so **do not dispatch in parallel** with 129/130. Sequence it.
- The **browser dependency is confined to Part A** — `Radar.Worker`, `Radar.Infrastructure`, and the
  baseline gain **no** Playwright/Chromium reference.

## Tests

- `FileFccAuthReader` unit tests over committed cache-file fixtures: happy path (N in-window grants →
  count + sample FCC IDs), empty/missing cache ⇒ `Success` 0, out-of-window rows excluded, unparseable
  `finalActionDate` row skipped, malformed file ⇒ `Malformed`, truncated flag → `"{count}+"`.
- Collector test: cache → one Neutral `EquipmentAuthorization` evidence with correct phrase + metadata;
  no-contamination guard (FCC IDs/descriptions in metadata only, never as extra signal phrases).
- Fingerprint tests re-pinned by running; opt-in-OFF composition asserted (default `Radar:Collectors` and
  `scripts/run-profiles/default.json` unchanged; no `feeds-lost-before-collection` warning for `fccauth`).
- The fetch step (Part A): a thin parse test over a **saved EAS results HTML fixture** proving the grid
  scrape yields the pinned columns — the live browser leg is inherently a manual/maintainer gate, so keep
  the parsing logic pure + fixture-tested and isolate the Playwright navigation behind a seam.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **Browser strictly out of the Worker.** No `Microsoft.Playwright`/Chromium reference from
  `Radar.Domain/Application/Infrastructure/Worker`. It lives only in the standalone fetch step.
- **Provenance intact.** The cache file records source URL + query + `fetchedAtUtc`; the evidence traces to
  it. A grant the collector counts is traceable to a real FCC ID and its detail URL.
- **`data/fcc-cache/` gitignored**; never commit fetched data.
- Politeness / good citizenship: weekly cadence, one query per seed, bounded read — this is low-volume
  access to public data, not a scrape.

## Out of scope / future

- Directional slice B (Positive on a *newly-appearing* FCC ID / accelerating count vs the accrued cache
  history) — stays deferred; v1 is Neutral.
- Enabling `fccauth` in the baseline — stays opt-in / OFF; a maintainer action once the fetch step is
  scheduled and seeds are verified.
- Auto-scheduling the fetch step (a `RadarFccFetch` task analogous to `RadarBaselineDaily`) — note it, do
  not build it here; running the fetch manually to populate the cache is the v1 gate.

## Acceptance criteria

- [ ] A standalone Playwright fetch step (outside `Radar.Worker`) retrieves **real recent grants for at
      least one seeded company whose token resolves to the CORRECT entity** (exact applicant name /
      `grantee_code`, verified — not a loose substring) and writes a provenance-stamped
      `data/fcc-cache/{ticker}.json`.
- [ ] `fccauth` collector reads `data/fcc-cache/`, counts in-window grants (`finalActionDate >= now −
      LookbackDays`), and emits one Neutral `EquipmentAuthorization` evidence per covered company with the
      fixed phrase + count + provenance metadata. `HttpFccAuthReader` is removed.
- [ ] No `Microsoft.Playwright`/Chromium reference anywhere in `Radar.Domain/Application/Infrastructure/
      Worker`; the browser lives only in the fetch step.
- [ ] Seed tokens in `data/companies.json` corrected to exact applicant names / grantee codes that resolve
      to the right companies; wrong-entity substrings removed; partial coverage documented.
- [ ] `RuleSetVersion` bumped and the default fingerprint(s) re-pinned **by running the tests** (score-
      neutral, opt-in-OFF); AD-10 lineage line added; no `_formula.Version`/weight/tier change.
- [ ] `fccauth` remains opt-in / OFF; default `Radar:Collectors` and `scripts/run-profiles/default.json`
      unchanged; `data/fcc-cache/` gitignored.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
