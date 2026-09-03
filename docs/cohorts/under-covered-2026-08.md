# Under-covered universe expansion — `under-covered-2026-08`

**This file is the committed SELECTION HYPOTHESIS for the 20 companies spec 199 added to
`data/companies.json` (74 → 94).** It was written BEFORE any observation exists for any of these
companies, so that it can be judged against measured `AttentionScore` after three post-199 runs rather
than reinterpreted afterwards.

It is a durable, human-read record. It is deliberately wired into **no code path** — nothing loads,
parses or validates it (unlike `event-enriched-2026-07.json`, which the efficacy evaluator reads). It
exists to be read by a person at the retrospective below. The same band split is quoted in CLAUDE.md's
spec-199 bullet; **the two must not disagree**, and this file is the fuller record because it carries the
per-company REASON as well as the band.

## Every row below is a PREDICTION, not a measurement

Coverage cannot be measured for a company that is not yet in the universe — it has no observations, no
publishers and no `AttentionScore`. Seed-time selection is therefore an act of judgement, and this file
exists to make that judgement falsifiable instead of invisible.

> If the additions cluster **ABOVE 70**, the under-covered heuristic FAILED. That is a reportable
> finding, not something to absorb. It would mean seed-time judgement cannot identify under-covered
> names — which is worth knowing before any further expansion, and is exactly the kind of result that
> gets quietly rationalised if it was never written down in advance.

## The selection variable was UNDER-COVERAGE; small cap was the prior, not the test

`followingTier` is curated from **following/coverage evidence only** and is **never** derived from price,
market capitalisation or trading volume (AD-14). "Small cap" and `followingTier: small` are different
things, and the live data shows the curated tier is only a weak proxy for what Radar actually scores:
`small` n=35 mean 62.0 (46–79) against `mid` n=32 mean 66.0 (56–90) — four points of mean separation and
near-total overlap, with a `small` company reaching 79 while a `mega` sits at 58. Selecting purely on
market cap would have optimised the wrong variable.

The practical test applied to each candidate was Radar's own: **would this company's news volume be
dominated by aggregators and trade press rather than editorial financial outlets?** A company was added
because it is plausibly un-noticed, never because it is interesting. All 20 are `followingTier: small`;
no `mid` obscurity case was needed.

**Predicted attention bands: low < 55 / mid 55–70 / high > 70.**

## The batch

| ticker | CIK | sector | predicted band | why it is plausibly under-covered |
| --- | --- | --- | --- | --- |
| GHM | 0000716314 | Industrials | mid | ~$400M vacuum and heat-transfer manufacturer whose revenue has shifted to US Navy propulsion and space; defence trade press covers it, general financial press does not. |
| CLMB | 0000945983 | Technology | mid | Specialist software/IT distributor; news flow is almost entirely vendor-agreement releases carried by aggregators, with no consumer surface. |
| UTMD | 0000706698 | Healthcare | low | Debt-free niche neonatal/obstetric device maker with minimal IR activity and no growth narrative; the archetypal ignored compounder. |
| MLAB | 0000724004 | Healthcare | mid | Sterilization-monitoring and calibration instruments; some life-science trade coverage, little general press. |
| JOUT | 0000788329 | Consumer Cyclical | mid | Family-controlled; the consumer brands (Minn Kota, Old Town, Eureka) draw enthusiast press while the listed company is rarely written about. |
| FLXS | 0000037472 | Consumer Cyclical | low | Century-old furniture manufacturer; coverage is furniture-trade and earnings-wire only. |
| ITIC | 0000720858 | Financial Services | low | Tiny regional title insurer, no conference calls, almost no press; also a colliding ticker ("critic", "political"), hence no ticker token on its news feed. |
| ESQ | 0001531031 | Financial Services | mid | Niche bank serving litigation and merchant-payments verticals; an unusual model that is nonetheless unwritten about. |
| SGA | 0000886136 | Communication Services | low | Local radio group; station-deal coverage sits in radio trade press, not financial press. |
| OOMA | 0001327688 | Communication Services | mid | Consumer and SMB VoIP; product-review and aggregator coverage exceeds financial coverage. |
| JBSS | 0000880117 | Consumer Defensive | mid | Family-controlled nut processor; food-trade press covers it, financial press rarely does. |
| SENEA | 0000088948 | Consumer Defensive | low | Dual-class canned-vegetable processor, minimal IR, thin float on the A shares. |
| NWPX | 0001001385 | Basic Materials | mid | Water-infrastructure steel pipe, recently renamed from Northwest Pipe; the rename itself fragments coverage, which is why the seed carries both brand phrases. |
| KOP | 0001315257 | Basic Materials | mid | Treated wood and carbon materials; chemical and rail trade press cover it, general press does not. |
| GEOS | 0001001115 | Energy | low | Seismic instrumentation, a post-shale orphan; coverage is sporadic and order-announcement driven. Colliding ticker ("geospatial", "geoscience"), hence no ticker token. |
| EPM | 0001006655 | Energy | mid | Non-operated, dividend-paying E&P whose yield attracts investor-platform writeups; recorded here as the honest RISK CASE for the heuristic. |
| CTO | 0000023795 | Real Estate | mid | Small sunbelt retail REIT with dividend-focused platform coverage; colliding ticker ("director", "sector", "factor", "doctor"), hence no ticker token. |
| OLP | 0000712770 | Real Estate | low | Small family-controlled net-lease REIT; press is limited to acquisition releases. |
| UTL | 0000755001 | Utilities | low | Small New England combined utility; local NH/MA news covers rates and outages, national press does not. Colliding ticker ("outlook", "outlet", "outline"), hence no ticker token. |
| RGCO | 0001069533 | Utilities | low | Tiny Roanoke gas distributor; coverage is local and rate-case driven. |

**Band totals: low 9, mid 11, high 0.**

- **low (9)** — UTMD, FLXS, ITIC, SGA, SENEA, GEOS, OLP, UTL, RGCO
- **mid (11)** — GHM, CLMB, MLAB, JOUT, ESQ, OOMA, JBSS, NWPX, KOP, EPM, CTO
- **high (0)** — none. Predicting no high-attention addition is itself part of the hypothesis: if any
  addition measures above 70, that row is a miss even though no row predicted it.

Every CIK above was live-verified against `https://data.sec.gov/submissions/CIK{cik}.json` on 2026-08-29
(HTTP 200; entity name, ticker and exchange matched; filings within the last month; Form 4 and SC 13
present) and is pinned by `ProductionCompanySeedTests`.

**Spec 200 §1 (2026-08-29): three news-feed phrases were corrected BEFORE first collection.** UTMD
`query=Utah Medical&ticker=UTMD` → `query=Utah Medical Products&ticker=UTMD`; ITIC `query=Investors Title`
→ `query=Investors Title Company`; ESQ `query=Esquire Financial&ticker=ESQ` → `query=Esquire Financial`
(ESQ joins the colliding-ticker allowlist — "Esquire" is an ordinary word and a publisher name). Spec 200 §2
inspected the durable stores and found ZERO history for all three company ids (latest durable run
`fa50b516`, 2026-08-28T21:40Z, pre-199), so **no row above is contaminated** by the pre-correction queries.
No predicted band changed.

**Efficacy boundary for these 20 (spec 200 §4), three-way and explicit:** (1) **live strategy/report
scoring** is immediate for all 94 companies as soon as evidence exists — no price horizon gates it; (2)
**raw forward-return diagnostics** appear only after a company's forward horizon resolves and are
diagnostic only, granting NO benchmark membership; (3) the **official benchmark-adjusted leaderboard and
the paired AD-15 claim** exclude all 20 as `NotInBenchmarkUniverse` under frozen `benchmark-universe-v1`
until a prospective `benchmark-universe-v2` is declared — no v2 exists and the 2026-09-29 AD-15 boundary
is unmoved.

## Retrospective — PERFORMED 2026-09-03 (spec 200 Phase B, cold-start read)

After **three** successful post-199 baseline runs (run 1 `70f256e3` 2026-08-29, run 2 `b6d52f64`
2026-08-30, run 3 `7d4dbce3` 2026-09-01; qualification and sources in the spec 200 §5 record), the report:

1. **Predicted band vs measured `AttentionScore`, per company** — all 20 rows are in the table below, the
   measured value beside the band predicted above. Source: the `default` snapshot of each run under
   `data/scores/{companyId}/{snapshotId}.json` (`attentionScore`; `scoringConfigVersion`
   `radar-scoring-fp-11240da5aeb0`); run-3 `WindowEndUtc` 2026-09-01T02:50:16.5514898Z.
2. **The hit rate** — **8 of 20 (40 %)** landed in their predicted band, split: **low 8/9** (SENEA the
   miss) and **mid 0/11** (every mid prediction measured low, range 33–46). The heuristic was right about the
   quiet names and wrong about the rest — but see the cold-start mechanics below before reading that as a
   result about the companies.
3. **The above-70 clustering test** — **0 of 20** measure above 70. The FAILED-heuristic condition is NOT
   triggered.
4. **EPM specifically** — measured **40, low** (predicted mid). The precommitted investor-platform/dividend
   coverage risk did NOT materialise in this window; the miss is in the opposite direction (less attention
   than predicted) and is the same cohort-wide cold-start effect as the other ten mid misses.

| ticker | predicted | run 1 | run 2 | **run 3** | run-3 band | hit? | note on a material miss |
| --- | --- | ---: | ---: | ---: | --- | --- | --- |
| GHM | mid | 32 | 32 | **36** | low | MISS | cohort-wide cold-start depression; its rss IR feed failed (transport error) on runs 2 and 3, so attention rests on newssearch alone |
| CLMB | mid | 38 | 38 | **38** | low | MISS | cohort-wide cold-start depression |
| UTMD | low | 35 | 35 | **42** | low | HIT | |
| MLAB | mid | 32 | 32 | **35** | low | MISS | cohort-wide cold-start depression |
| JOUT | mid | 41 | 43 | **43** | low | MISS | cohort-wide cold-start depression |
| FLXS | low | 33 | 39 | **42** | low | HIT | |
| ITIC | low | 29 | 29 | **29** | low | HIT | |
| ESQ | mid | 28 | 31 | **33** | low | MISS | cohort-wide cold-start depression |
| SGA | low | 31 | 31 | **33** | low | HIT | hit the 25-item limit on runs 2 and 3 (69 → 67 valid items, 0 relevant tail) |
| OOMA | mid | 39 | 40 | **42** | low | MISS | cohort-wide cold-start depression; hit the 25-item limit on runs 2 and 3 (63 → 48 valid items, 37 → 22 relevant tail) |
| JBSS | mid | 42 | 45 | **46** | low | MISS | cohort-wide cold-start depression |
| SENEA | low | 46 | 52 | **57** | mid | MISS | the only row to reach mid: saturated the 25-item retained prefix on runs 2 AND 3 (`maxValidItemsObserved` 49, 24 unadmitted relevant tail items each run) — the noisiest name in the cohort by relevant volume within a 7-day window |
| NWPX | mid | 45 | 46 | **46** | low | MISS | cohort-wide cold-start depression |
| KOP | mid | 35 | 37 | **39** | low | MISS | cohort-wide cold-start depression |
| GEOS | low | 26 | 26 | **26** | low | HIT | the lowest: 0 and 1 valid items on runs 2 and 3 |
| EPM | mid (RISK CASE) | 36 | 39 | **40** | low | MISS | the precommitted investor-platform/dividend coverage risk did NOT materialise in this window; the miss is in the opposite direction, the same cold-start effect as the other mid misses |
| CTO | mid | 33 | 33 | **34** | low | MISS | cohort-wide cold-start depression |
| OLP | low | 36 | 36 | **33** | low | HIT | |
| UTL | low | 35 | 35 | **37** | low | HIT | |
| RGCO | low | 36 | 36 | **36** | low | HIT | |

**Totals: low 8/9; mid 0/11; overall 8/20 (40 %); above 70 = 0 of 20; EPM 40, low; unresolved 0;
contaminated 0; sensitivity total excluding contaminated rows = primary total (8/20).** (Supplementary,
context only, NOT in the hit/miss — run 4 as-of 2026-09-01T21:46:09Z / run 5 as-of 2026-09-02T21:50:03Z:
GHM 36/36, CLMB 38/39, UTMD 42/42, MLAB 35/36, JOUT 46/46, FLXS 43/47, ITIC 29/33, ESQ 34/34, SGA 33/33,
OOMA 43/50, JBSS 49/49, SENEA 57/57, NWPX 48/51, KOP 41/41, GEOS 26/26, EPM 42/44, CTO 34/34, OLP 35/38,
UTL 38/38, RGCO 36/39.)

**The mechanical reason for the material miss — the whole mid band measuring low — without revising the
hypothesis:** `AttentionScore` is a 60-day window (run-3 `windowStartUtc` 2026-07-03 → `windowEndUtc`
2026-09-01), but these 20 were first collected at 2026-08-29T21:44Z, so by run 3 they held roughly three
days of capture (one unfiltered first-collection pull capped at the 25-item retained prefix per feed, then
two 7-day-windowed pulls admitting 29 and 40 observations across all 20) against incumbents carrying a full
60 days (the `small` tier measured mean 62.0 at spec 199). The cohort is mechanically depressed as a whole
(26–57) and the within-cohort mid/low separation cannot be tested yet — the spec 200 §4 cold-start caveat
in effect. Within the cohort the ordering tracks capture volume (per-company `companyCoverage` rows in the
run-2/run-3 batch records `data/news-observations/batches/20260830T214625Z.json` and
`20260901T025016Z.json`). The read tested query relevance (every one of the 20 companies returned relevant
items on run 1; no company returned zero relevant items in all three runs), capture shape and early
calibration;
it does NOT validate the under-coverage thesis. **No company is removed, re-tiered or feed-tuned as a result;
no predicted band, reason or band total above was changed.** Full record with sources: spec 200 §6 record.

Name the reason for any material miss rather than revising the hypothesis to fit the measurement. This
retrospective **was performed on 2026-09-03** on the snapshot fixed in advance: run 3
`7d4dbce3-f24d-4eff-bd5f-1ebccd5cfc93`, strategy `default`, `WindowEndUtc` 2026-09-01T02:50:16.5514898Z.

**What the three-run read CAN and CANNOT mean (spec 200 §4 cold-start caveat).** The stored
`AttentionScore` uses a **60-day** window; after three daily runs these companies have only a few days of
locally captured history. The read therefore tests **query relevance, capture shape and early
calibration**. It is **NOT proof** that any company is durably under-covered, and **no company may be
removed, re-tiered or have its feed tuned** on the strength of it. The mature descriptive read is the first
successful run whose 60-day attention window starts no earlier than the first post-199 collection instant;
that date was recorded here by spec 200 Phase B on 2026-09-03: the first successful run whose `WindowEndUtc`
≥ **2026-10-28T21:44:52Z** (first post-199 collection instant 2026-08-29T21:44:52Z + 60 days) —
operationally the 2026-10-28 nightly slot if its as-of instant falls at or after 21:44:52Z, otherwise the
2026-10-29 slot; descriptive only, not a gate. The run-3 snapshot is fixed
in advance: the `default` primary-strategy snapshot of the third successful run and its exact
`WindowEndUtc` (spec 200 §6).
