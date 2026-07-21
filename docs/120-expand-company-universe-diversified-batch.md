# Task: Expand the watch universe — add 11 sector-diversified companies, drop delisted SPNS

> **UNIVERSE / EFFICACY-SAMPLE EXPANSION (data + seed only).** Approved 2026-07-21 after the spec 92→119
> scoring-calibration + model-quality rework landed and the arch checkpoint @`7785cff` came back CLEAN ("safe to
> expand universe / add collectors"). This is a **data change to `data/companies.json`** — it adds monitored
> companies and removes a delisted one. **The company universe is NOT a scoring input**, so this has **no
> fingerprint impact, no formula/RuleSetVersion bump** (unlike adding a *collector*, which would). Every CIK below
> was verified against SEC's authoritative `company_tickers.json`. Motivation: buy efficacy time-in-sample sooner
> (new names accrue score-vs-price history from the next run) and reduce the current semicap/industrial selection
> skew by adding medtech, infrastructure, grid, components, power-EPC, water, payments, and energy.

## Overview

The watch universe (`data/companies.json`) is a curated, diversified efficacy sample (19 companies as of spec
117), deliberately spanning improving-candidates + steady controls + large-cap benchmarks so the spec-101/108
score-vs-price efficacy layer can test **discrimination**, not just measure hand-picked bullish theses. The current
set skews toward semiconductor-capital-equipment / industrial small-caps (AEHR/KLIC/UFPT/MRCY/POWL…). This slice
adds **11 verified companies across new sectors** and **removes SPNS** (Sapiens — reported taken private, absent
from SEC `company_tickers.json`, price feed 404s, already carries no SEC feeds → pure noise in the efficacy sample).

Result: universe 19 → **29** (add 11, drop 1).

## Assignment

Worktree: any
Dependencies: current main (post 119). Pure data/seed change — independent of scoring; **no fingerprint impact**.
Estimated time: ~1–2 hours (mostly per-company IR RSS-feed verification).

## The batch (all CIKs verified against SEC `company_tickers.json`)

| Ticker | Registrant name (SEC) | CIK (10-digit) | Exch* | followingTier | Bucket | Sector / Industry (metadata) |
|---|---|---|---|---|---|---|
| TMDX  | TransMedics Group, Inc.   | 0001756262 | NASDAQ | mid   | candidate | Health Care / Medical Devices (organ perfusion) |
| AXGN  | Axogen, Inc.              | 0000805928 | NASDAQ | small | candidate | Health Care / Medical Devices (nerve repair) |
| STRL  | Sterling Infrastructure   | 0000874238 | NASDAQ | mid   | candidate | Industrials / Engineering & Construction |
| MYRG  | MYR Group Inc.            | 0000700923 | NASDAQ | mid   | candidate | Industrials / Electrical Construction (grid/T&D) |
| BELFB | Bel Fuse Inc. (Class B)   | 0000729580 | NASDAQ | small | candidate | Technology / Electronic Components |
| AGX   | Argan, Inc.               | 0000100591 | NYSE   | small | candidate | Industrials / Power-plant EPC |
| WTRG  | Essential Utilities, Inc. | 0000078128 | NYSE   | large | control   | Utilities / Water & Gas (regulated) |
| WDFC  | WD-40 Company             | 0000105132 | NASDAQ | mid   | control   | Consumer / Specialty Chemicals |
| HRL   | Hormel Foods Corp.        | 0000048465 | NYSE   | large | control   | Consumer Staples / Packaged Foods |
| V     | Visa Inc.                 | 0001403161 | NYSE   | mega  | benchmark | Financials / Payments |
| CVX   | Chevron Corp.             | 0000093410 | NYSE   | mega  | benchmark | Energy / Integrated Oil & Gas |

\* Exchange is metadata (not load-bearing — SEC collection keys on CIK); verify while confirming the RSS feed and
correct if wrong.

Notes carried from verification (do not re-introduce these traps):
- **Do NOT use XOM.** SEC `company_tickers.json` maps `XOM` to CIK `2115436` "ExxonMobil Holdings Corp", a
  non-trading shell (no ticker/exchange in the submissions API). The batch uses **CVX** as the energy benchmark
  instead. (If Exxon is ever wanted, the real registrant is CIK `0000034088`, not `2115436`.)
- **BELFB** (Class B) and BELFA (Class A) share one registrant/CIK `0000729580`; use the more-liquid BELFB ticker.
- **followingTier is a curated NON-PRICE notedness tier (AD-14).** It is not market-cap-derived scoring input; it
  only feeds the spec-117 Opportunity following-discount, exactly as the existing 19 do.

## Changes — `data/companies.json` only

For **each** of the 11 companies, add a seed entry matching the exact shape of the existing entries (see MRCY at
the top of the file). Required/known fields:

- `id`: a fresh **GUID** (generate one per company; unique).
- `name`, `legalName`: the registrant name above. `ticker`, `exchange`, `countryCode: "US"`, `sector`, `industry`.
- `followingTier`: the tier above (case-insensitive string; the seed parser defaults absent → Small).
- `aliases`: a couple of common short forms (e.g. TMDX → `["TransMedics"]`, V → `["Visa"]`, WDFC → `["WD-40"]`).
- `themes`: 2–3 short descriptive themes (e.g. STRL → `["infrastructure construction", "data center sitework"]`).
- `sourceFeeds` (mirror the existing per-company pattern):
  - `sec`, `secform4`, `sec13dg` — **all three required**, each `url:
    "https://data.sec.gov/submissions/CIK<10-digit>.json"` using the CIK above (same URL for all three types, as
    every existing entry does). Names follow the existing convention
    (`"<Name> — SEC filings (EDGAR)"`, `"… — SEC Form 4 insider filings (EDGAR)"`, `"… — SEC 13D/13G ownership
    filings (EDGAR)"`).
  - `newssearch` — **required**, `url: "query=<Registrant Name>&ticker=<TICKER>"`, name `"<Name> — News attention
    (Google News)"`. **Exception (as shipped): omit the `ticker=` token for V (Visa).** The collector's title
    relevance filter is a plain case-insensitive substring match, so a single-letter ticker matches almost any
    headline; relevance for Visa is driven by the `query=` phrase alone.
  - `rss` — **include only if verified live.** Locate the company's investor-relations press-release RSS feed and
    **curl it with the collector User-Agent** (the same UA the RSS collector sends). Include the `rss` feed **only
    if it returns HTTP 200 with valid RSS**; if it 403s (the known FLO/IR-platform gotcha), 404s, or no IR RSS
    exists, **omit the `rss` feed for that company and record which ones were dropped in the PR body.** The company
    is still fully collected via SEC (CIK) + newssearch — RSS is additive, not required.
  - `usaspending` — **omit** for this batch (optional; adding a recipient requires a per-company USASpending
    recipient-ID lookup — out of scope here, can be a later slice for the federal-contractor names like MYRG/STRL/AGX).

Then **remove the SPNS (Sapiens) entry entirely** from `companies.json` (the whole company object). It is delisted,
has no SEC provenance path, and its price feed 404s.

Keep the file valid JSON (trailing-comma-free), and keep the existing 18 non-SPNS companies untouched.

## Tests

- Update any test that pins the **universe size or membership** (e.g. an assertion of "19 companies", or one that
  references SPNS by ticker) to the new set (28 after the drop + 11 add = **29**; SPNS removed). Search tests for
  `SPNS`, `Sapiens`, and a hard-coded company count.
- If `LocalFileCompanySeedSource`/`LocalFileCompanySeedDocument` has parse tests over a fixture, they need no change
  unless they load the production `companies.json`; ensure the production file still round-trips (all new entries
  parse — GUID `id`, tier string, feeds).
- No new production code path is expected — this is a seed-data change. If the build/test gate surfaces a pinned
  universe assertion, fix it to reflect the new set (don't weaken it to a range unless it already was one).

## Constraints

- **Data-only change** to `data/companies.json` (+ any test that pins the universe). **No** `_formula.Version` /
  `KeywordSignalExtractor.RuleSetVersion` bump; the company universe is not a `ScoringConfig` input, so the
  fingerprint does **not** move (AI-OFF `8f4b59efd288` / AI-ON `2ef5ef96cce2` both unchanged — add a pinned-fp
  regression assertion is NOT required, but do not let the fp move).
- Every CIK must be exactly as listed (verified). Do not invent or alter CIKs. Do not add XOM.
- `followingTier` values only from {mega, large, mid, small} — curated, non-price (AD-14 preserved).
- SEC User-Agent for any live RSS/feed verification must be a real contact (not the placeholder that 403s); the
  coder verifies feeds but does not commit any UA into the repo.

## Acceptance criteria

- [ ] All 11 companies added to `data/companies.json` with the exact CIKs above, correct `followingTier`, and
      `sec`/`secform4`/`sec13dg`/`newssearch` feeds; each `id` a unique GUID; file is valid JSON.
- [ ] Each company's IR `rss` feed was **live-verified** with the collector UA and included only if it returned
      200; every omitted RSS (403/404/none) is listed in the PR body. SEC + newssearch attach to all 11 regardless.
- [ ] SPNS (Sapiens) entry **removed** entirely; the other 18 existing companies unchanged.
- [ ] Any universe-size/membership test updated to the new 29-company set (SPNS references removed).
- [ ] Fingerprint unchanged (AI-OFF `8f4b59efd288`, AI-ON `2ef5ef96cce2`); no formula/RuleSetVersion bump.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
