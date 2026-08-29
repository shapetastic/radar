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

## Retrospective — OWED, NOT YET DONE

After **three** successful post-199 baseline runs, report:

1. **Predicted band vs measured `AttentionScore`, per company** — all 20 rows, measured value beside the
   band predicted above.
2. **The hit rate** — how many of the 20 landed in their predicted band, split low/mid so a heuristic that
   is right about the quiet names and wrong about the rest is visible as such.
3. **The above-70 clustering test** — how many additions measure above 70. Per spec 199 §5, clustering
   above 70 means the under-covered heuristic FAILED and must be reported as a failed heuristic.
4. **EPM specifically**, recorded above as the risk case: an investor-platform-covered dividend payer is
   the most likely single miss, and calling it in advance is what makes the retrospective honest.

Name the reason for any material miss rather than revising the hypothesis to fit the measurement. This
retrospective **has not been performed** — no post-199 run exists at the time of writing.

**What the three-run read CAN and CANNOT mean (spec 200 §4 cold-start caveat).** The stored
`AttentionScore` uses a **60-day** window; after three daily runs these companies have only a few days of
locally captured history. The read therefore tests **query relevance, capture shape and early
calibration**. It is **NOT proof** that any company is durably under-covered, and **no company may be
removed, re-tiered or have its feed tuned** on the strength of it. The mature descriptive read is the first
successful run whose 60-day attention window starts no earlier than the first post-199 collection instant;
that date is to be recorded here (spec 200 Phase B) once the first run exists. The run-3 snapshot is fixed
in advance: the `default` primary-strategy snapshot of the third successful run and its exact
`WindowEndUtc` (spec 200 §6).
