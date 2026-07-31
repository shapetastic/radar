# Task: Expand the watch universe — batch 4: add 9 verified companies (66 → 75)

> **UNIVERSE EXPANSION (data-only; spec-159 pattern).** Proposed by the external reviewer 2026-07-31 and
> endorsed by the maintainer alongside the standing constraint: **more companies yes, more strategies no.**
> The company universe is NOT a scoring input — no fingerprint impact, no formula/RuleSetVersion bump, no
> `ReportMaxItems` change needed (75 < the spec-159 cap of 90). **All nine CIKs were verified 2026-07-31
> against SEC's authoritative `company_tickers.json`** (single paced request, compliant UA). Unlike spec 159,
> **RSS feeds and filing cadence are NOT pre-verified** — both are implementation steps below, with the exact
> method. Selection was neutral (AD-14): names chosen for sector fill and event-richness, never by price
> action; the reviewer states recent price performance was not consulted.

## Overview

Spec 159's rationale stands: cross-sectional power is the lever, and companies added now become measurable in
the same September window everything else is waiting on. This batch fills genuine gaps — two REIT models
(the calibration study showed REIT prints need FFO/AFFO framing; the universe holds only 3), housing-cycle
exposure, communications infrastructure with comparability-stress events, food distribution — and adds
**zero new Industrials and zero semiconductors**. Mix: 7 improving-candidates + 2 steady controls; the
imbalance vs spec 159's near-even split is deliberate (the existing 66 already carry that batch's 10
controls) but flagged for the maintainer sanity-check below.

Result: universe 66 → **75**. **No companies are removed.**

## Assignment

Worktree: any
Dependencies: current main (post #167). Pure data change — independent of scoring; no fingerprint impact.
Estimated time: ~1–2 hours (seed authoring + the two verification passes below).

## The batch (CIKs verified 2026-07-31 against `company_tickers.json`; registrant titles as returned by SEC)

| Ticker | Registrant name (SEC) | CIK (10-digit) | followingTier* | Bucket | Sector / Industry |
|---|---|---|---|---|---|
| PSTL | Postal Realty Trust, Inc. | 0001759774 | small | candidate | Real Estate / REIT—Office |
| FR | FIRST INDUSTRIAL REALTY TRUST INC | 0000921825 | mid | control | Real Estate / REIT—Industrial |
| CCOI | COGENT COMMUNICATIONS HOLDINGS, INC. | 0001158324 | mid | candidate | Communication Services / Telecom Services |
| ATNI | ATN International, Inc. | 0000879585 | small | candidate | Communication Services / Telecom Services |
| CARS | Cars.com Inc. | 0001683606 | small | candidate | Communication Services / Internet Content & Information |
| MHO | M/I HOMES, INC. | 0000799292 | mid | candidate | Consumer Cyclical / Residential Construction |
| THRM | Gentherm Inc | 0000903129 | small | candidate | Consumer Cyclical / Auto Parts |
| CHEF | Chefs' Warehouse, Inc. | 0001517175 | mid | candidate | Consumer Defensive / Food Distribution |
| BKE | BUCKLE INC | 0000885245 | small | control | Consumer Cyclical / Apparel Retail |

\* `followingTier` values are PROPOSALS (spec-159 caveat verbatim: inferred from coverage density and
market-cap scale; sanity-check each before committing — the tier drives the notedness discount directly).

Resulting sector fill: Real Estate 3 → 5, Communication Services 4 → 7, Consumer Cyclical 5 → 8, Consumer
Defensive 6 → 7. Industrials stay 10; no new semiconductors.

Why each (research-sample rationale, not investment views): PSTL — a fourth REIT model (USPS-leased,
acquisition-driven, AFFO guidance, government-linked). FR — industrial-REIT steady control with clean
occupancy/same-store disclosures. CCOI — event-rich communications infrastructure; its 2026 data-centre sale
is a ready-made comparability stress case for the cmpscan work. ATNI — rural/international telecom + towers,
different economics from existing telecom names. CARS — digital marketplace with restructuring and
recurring-revenue disclosures; third-party-attention potential. MHO — housing cycle via orders/backlog/
cancellations/margins, a dimension the universe lacks. THRM — automotive supplier with FX/volume/transaction
comparability (pending Modine combination — an event-rich name). CHEF — food DISTRIBUTION (vs the existing
branded manufacturers), explicit organic-vs-acquired growth splits. BKE — steady retail control with frequent,
uncomplicated disclosures.

## ⚠ Ticker substring collisions (`IsRelevant` is an unanchored case-insensitive `Contains`)

**Three tickers MUST omit the `ticker=` token** (the established V/DEA/KGS/PLUS treatment):

| Ticker | Collides with |
|---|---|
| FR | "**fr**om", "**fr**ee", "**Fr**iday", "**Fr**ance" — near-universal bigram |
| CARS | "**cars**", "used **cars**", "**cars** recalled" |
| CHEF | "**chef**", "**chef**s", "celebrity **chef**" |

The other six (PSTL, CCOI, ATNI, MHO, THRM, BKE) are distinctive letter-runs — keep `ticker=`.

## `newssearch` `url` tokens (no `&` inside any value — the `TwoKeyFeedToken` contract, spec 159's JJSF trap)

| Ticker | `url` |
|---|---|
| PSTL | `query=Postal Realty&ticker=PSTL` |
| FR | `query=First Industrial Realty` |
| CCOI | `query=Cogent Communications&ticker=CCOI` |
| ATNI | `query=ATN International&ticker=ATNI` |
| CARS | `query=Cars.com` |
| MHO | `query=M/I Homes&ticker=MHO` |
| THRM | `query=Gentherm&ticker=THRM` |
| CHEF | `query=Chefs' Warehouse` |
| BKE | `query=The Buckle&ticker=BKE` |

Notes: **CHEF** — headlines vary between straight (`Chefs'`) and curly (`Chefs’`) apostrophes; the straight
form will miss curly-quoted headlines (`Contains` is exact). Accepted: SEC + any RSS still cover the company,
and a broader phrase (`Chefs`) would admit junk. Record, don't widen. **MHO** — the `/` in `M/I Homes` is
fine inside a token value (only `&` is forbidden); verify the composed Google News URL encodes it. **BKE** —
`The Buckle` (not bare `Buckle`, which `Contains`-matches "buckle up"); ticker `BKE` carries the
finance-styled headlines.

## RSS feeds — NOT pre-verified; verify during implementation (this differs from spec 159)

For each of the nine, probe the standard patterns (`/rss/news-releases.xml` on the IR host,
`/news-events/press-releases/rss`, site `/feed/`) using **`curl -H "User-Agent:" <url>`** — the collector
sends NO User-Agent, so verifying with one (spec 125's original mistake) admits feeds that 403 in production.
Include a feed only on HTTP 200 **plus** ≥10 real `<item>`s **plus** a content check that items are press
releases, not marketing/culture blogs (spec 159's ASIX/MMSI trap). Record the per-ticker verdict (URL or
named failure) in the PR body. Omitted feeds are fine — SEC (CIK) + `newssearch` fully cover every name.

## Filing-cadence check — during implementation, paced

Fetch each CIK's `data.sec.gov` submissions record (sequential, ~3/sec max, compliant UA from the
environment — never committed): confirm 2026 8-Ks and Form 4s exist and note the most-recent filing date in
the PR body. A name with no 2026 filings would sit inert in the sample — flag it rather than silently seed
it. (Reviewer states all nine have active 2026 filings; verify rather than trust.)

## Changes

### `data/companies.json` — add 9 entries (the ONLY file change)

Exact spec-159 shape per entry: fresh unique GUID `id`; `name` natural-case (e.g. "Postal Realty Trust",
"First Industrial Realty", "Cogent Communications", "ATN International", "Cars.com", "M/I Homes", "Gentherm",
"The Chefs' Warehouse", "The Buckle") with `legalName` faithful to the SEC registrant title above; `ticker`,
`countryCode: "US"`, sector/industry/tier from the batch table; `sec`/`secform4`/`sec13dg` all three on
`https://data.sec.gov/submissions/CIK<10-digit>.json`; `newssearch` exactly per the token table; `rss` only
where verified above; all other collectors omitted. All 66 existing entries byte-untouched.

`aliases` / `themes`:

| Ticker | aliases | themes |
|---|---|---|
| PSTL | "Postal Realty", "Postal Realty Trust" | "USPS-leased properties", "net lease REIT", "postal logistics real estate" |
| FR | "First Industrial", "First Industrial Realty" | "industrial REIT", "logistics real estate", "warehouse development" |
| CCOI | "Cogent", "Cogent Communications" | "internet transit", "fiber network", "data center leasing", "IPv4 leasing" |
| ATNI | "ATN International", "ATN" | "rural broadband", "international telecom", "tower infrastructure" |
| CARS | "Cars.com", "Cars Commerce" | "auto marketplace", "dealer software", "digital advertising" |
| MHO | "M/I Homes", "M/I" | "homebuilding", "orders and backlog", "housing cycle" |
| THRM | "Gentherm" | "thermal management", "automotive seating comfort", "medical thermal devices" |
| CHEF | "Chefs' Warehouse", "The Chefs' Warehouse" | "specialty food distribution", "independent restaurants", "organic vs acquired growth" |
| BKE | "Buckle", "The Buckle" | "apparel retail", "denim", "mall retail" |

## Tests

- Universe-size/membership tests: 66 → 75.
- Extend the ticker-collision regression test: FR, CARS and CHEF carry no `ticker=` token; BKE's phrase is
  exactly `The Buckle`; a representative distinctive newcomer (e.g. CCOI) does carry `ticker=`; the JJSF
  `query=JJSF` pin stays untouched.
- `companies.json` round-trips through `LocalFileCompanySeedSource` (GUID ids, tiers, feeds parse).

## Constraints

- Data only; **no fingerprint moves** (current live-baseline stamps: AI-OFF `radar-scoring-fp-4eb2fe5d3cdf`,
  AI-ON 60d `radar-scoring-fp-5ffa8c9e25f0` post-160); `StrategyIdentityGuard` must not trip on the next run.
- CIKs exactly as tabled (verified 2026-07-31); do not invent or alter. No `&` inside any token value. No
  SEC UA committed; all SEC checks paced and sequential.
- **No new strategies** (standing constraint, 2026-07-31): this batch adds observations for the EXISTING ten
  arms, nothing else.

## Expected operational consequences (record, don't fix)

Per-run cost 660 → **750 scorings**; sources ~300 → ~330; temporary DeepInfra 8-K backlog over ~1–2 runs
(bounded by `Ai:MaxFilingsPerRun`); 1-year price backfill per new ticker on first run; first rankable
observations ≈ 21 days after first snapshots (≈ **2026-08-21+** if merged promptly — the same late-August
window the other arms are waiting on). If the spec-161 filtered-collect affordance has merged, a
`-Companies` collect pass can backfill the nine cheaply before the next scheduled full run.

## Acceptance criteria

- [ ] 9 entries added with the exact CIKs, proposed tiers sanity-checked, all-three SEC feeds + `newssearch`
      tokens exactly as tabled; RSS only where the no-UA verification passed, with per-ticker verdicts
      recorded in the PR body; filing cadence confirmed per name.
- [ ] Collision/regression tests extended (FR/CARS/CHEF no-ticker, BKE phrase, JJSF pin untouched);
      universe tests at 75; all 66 existing entries byte-untouched; file valid JSON.
- [ ] No fingerprint moves; `dotnet build Radar.sln -c Release` and
      `dotnet test Radar.sln -c Release --no-build` pass.