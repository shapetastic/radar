# AI-robotics universe expansion — `ai-robotics-2026-09`

**This file is the committed SELECTION HYPOTHESIS for the 8 companies spec 207 added to
`data/companies.json` (94 → 102).** It was written BEFORE any observation exists for any of these
companies, so that it can be judged against measured `AttentionScore` after three post-207 runs rather
than reinterpreted afterwards.

It is a durable, human-read record. It is deliberately wired into **no code path** — nothing loads,
parses or validates it (unlike `event-enriched-2026-07.json`, which the efficacy evaluator reads). It
exists to be read by a person at the retrospective below. The same band split is quoted in the spec-207
bullet of `docs/architecture-history.md`; **the two must not disagree**, and this file is the fuller record
because it carries the per-company REASON as well as the band.

Every forward-looking number in this file is **PROJECTED** until the three post-207 runs exist.

## Every row below is a PREDICTION, not a measurement

Coverage cannot be measured for a company that is not yet in the universe — it has no observations, no
publishers and no `AttentionScore`. Seed-time selection is therefore an act of judgement, and this file
exists to make that judgement falsifiable instead of invisible.

> If the additions cluster **ABOVE 70**, the small/mid-fit heuristic FAILED for this theme. That is a
> reportable finding, not something to absorb. It would mean seed-time judgement cannot identify quietly
> covered names inside a hot theme — which is worth knowing before any further themed expansion, and is
> exactly the kind of result that gets quietly rationalised if it was never written down in advance.

## The selection variable was FIT WITH THE UNDER-COVERED SMALL/MID THESIS inside one theme; fame was the exclusion

The universe (94 since spec 199) carried no robotics or AI-robotics name — the closest neighbours were MRCY
(defense electronics), KLIC (chip assembly equipment), DGII (industrial IoT) and HLIO (motion-control
components). The maintainer asked for the theme to be represented (2026-09-02). Unlike the spec-199 batch,
this one is themed first: the test applied to each candidate was Radar's own — **would this company's news
volume be dominated by aggregators and trade press rather than editorial financial outlets?** — but applied
only to companies that actually build, or put load-bearing parts into, AI-driven robots.

`followingTier` is curated from **following/coverage evidence only** and is **never** derived from price,
market capitalisation or trading volume (AD-14). Four rows are `small` and four are `mid`, because the theme's
quieter half sits in mid-cap component and subsystem suppliers, not in the well-known robot brands. The
2026-09-02 market caps quoted in spec 207 §1 are the selection record — history, not values to maintain.

**Exclusion record (2026-09-02, from spec 207's Overview):** the well-known theme names — Serve Robotics,
Ondas, SoundHound, BigBear.ai, Symbotic — were deliberately **excluded** as heavily covered or too large;
Richtech Robotics was excluded because its ticker `RR` is unusable as a news token (Rolls-Royce); FARO
(acquired by AMETEK 2025-07) and iRobot (Chapter 11 2025-12, ownership transferred to Shenzhen PICEA
2026-01) were excluded because neither is an independent US-listed company any longer.

**Predicted attention bands: low < 55 / mid 55–70 / high > 70** (against the stored 60-day `AttentionScore`).

## The batch

| ticker | CIK | tier | sector | predicted band | why it is plausibly quietly covered |
| --- | --- | --- | --- | --- | --- |
| PDYN | 0001826681 | small | Technology | mid | AI autonomy software for industrial/defense robots (ex-Sarcos); a revenue inflection (record Q2 2026) draws some retail and aggregator attention, so not low, but no editorial following yet. |
| STXS | 0001289340 | small | Healthcare | low | Robotic surgical navigation, very quiet; FDA catalysts play to the existing `fda` collector, but general financial press does not write about it. |
| CMCO | 0001005229 | small | Industrials | low | Intelligent motion / automation; a classic quiet industrial in the robotics supply chain whose coverage is earnings-wire and trade press only. Its phrase must never be shortened to the bare "Columbus" (the city). |
| ALNT | 0000046129 | small | Industrials | low | Precision motion control powering robots; low-coverage industrial, formerly Allied Motion, whose news flow is order and acquisition releases. |
| OUST | 0001816581 | mid | Technology | mid | Digital lidar + AI perception software; retail-visible name, so mid — **the declared identity RISK CASE, see below**. |
| PRCT | 0001588978 | mid | Healthcare | mid | Surgical robotics with a real adoption curve; medtech trade and sell-side coverage exist, general press is thin. |
| NOVT | 0001076930 | mid | Technology | mid | Photonics/precision-motion subsystems into robotics and medtech; the upper bound of "mid" — covered as a component supplier, rarely as a story. |
| AMBA | 0001280263 | mid | Technology | mid | Edge-AI vision silicon that shipping robots actually carry; semiconductor coverage is broad enough for mid, but it is written about as a chip name, not a robotics one. |

**Band totals: low 3, mid 5, high 0.**

- **low (3)** — STXS, CMCO, ALNT
- **mid (5)** — PDYN, OUST, PRCT, NOVT, AMBA
- **high (0)** — none. Predicting no high-attention addition is itself part of the hypothesis: if any
  addition measures above 70, that row is a miss even though no row predicted it.

Every CIK above was resolved from the canonical SEC mapping `https://www.sec.gov/files/company_tickers.json`
and live-verified against `https://data.sec.gov/submissions/CIK{cik}.json` on 2026-09-03 (HTTP 200; entity
name and ticker matched) and is pinned by `ProductionCompanySeedTests.Spec207Ciks`, which owns those values.
None of the eight was dropped.

## The OUST identity risk case — recorded now, in both directions

`FeedTargetRelevance.IsRelevant` (the predicate `NewsAttentionCollector` uses) is an unanchored,
case-insensitive substring match on the feed's query phrase or ticker token. `OUST`/"ouster" is a common
English noun ("the CEO's ouster"), colliding as BOTH the ticker and the bare company name, so the feed is
exactly `query=Ouster Inc` — the phrase includes `Inc` for precision and carries **no ticker token**; OUST
joins the colliding-ticker allowlist (`TickersWithoutTickerToken`) as the sixth phrase-only ticker, beside
ESQ.

**In both directions, recorded before any collection so the reader is not surprised later:**

- **Recall side.** The `Ouster Inc` phrase deliberately trades recall for precision — headlines that write
  only "Ouster (OUST)" will be missed (pinned as a known miss by
  `CollectAsync_Spec207OustFeed_TickerOnlyHeadline_IsMissedByDesign`). OUST's measured Attention may
  therefore read LOW for reasons that are about feed identity, not coverage. **A low OUST reading is NOT
  evidence of under-coverage without first checking the feed's admitted-item counts** (the per-company
  `companyCoverage` rows in the `data/news-observations/batches/` records).
- **Precision side.** Any future loosening of the phrase (to "Ouster", or adding `ticker=OUST`) risks
  admitting ouster-the-noun headlines and inflating Attention.

Neither direction may be "fixed" by tuning inside spec 207. If the three-run read shows the phrase is
starving the feed, that is a measured follow-up spec against the relevance predicate, not a quiet query
edit.

**Efficacy boundary for these 8 (unchanged from spec 200 §4), three-way and explicit:** (1) **live
strategy/report scoring** is immediate for all 102 companies as soon as evidence exists — no price horizon
gates it; (2) **raw forward-return diagnostics** appear only after a company's forward horizon resolves and
are diagnostic only, granting NO benchmark membership; (3) the **official benchmark-adjusted leaderboard and
the paired AD-15 claim** exclude all 8 as `NotInBenchmarkUniverse` under frozen `benchmark-universe-v1`
(byte-identical, still 74 members) until a prospective `benchmark-universe-v2` is declared — no v2 exists
and the 2026-09-29 AD-15 boundary is unmoved.

## Retrospective — OWED

**Entry condition:** three successful post-207 **full** runs (qualification as spec 200 §5 defined it: a
completed run record with the news-typing and judgment passes present; a suspended or partial run does not
count). Nothing in this section may be written before those exist, and it is written from durable stores,
never from memory.

After those three runs, report here, on a snapshot fixed in advance — the `default` primary-strategy
snapshot of the THIRD successful post-207 run and its exact `WindowEndUtc`:

1. **Predicted band vs measured `AttentionScore`, per company** — all 8 rows, the measured value beside the
   band predicted above. Source: the `default` snapshot of each run under
   `data/scores/{companyId}/{snapshotId}.json` (`attentionScore`, `scoringConfigVersion`, `windowEndUtc`).
2. **The hit rate** — how many of 8 landed in their predicted band, split low / mid.
3. **The above-70 clustering test** — how many of 8 measure above 70. Any is a miss (no row predicted high).
4. **OUST specifically** — its measured band AND its feed's admitted-item counts across the three runs
   (`companyCoverage` in the batch records), so a low reading can be attributed to feed identity or to
   coverage rather than assumed.
5. **The post-spike drain check** — the per-run `untypedRemaining` values and their deltas for the three
   runs, using exactly the arithmetic spec 200 §5 defined. The eight unfiltered first collections (the
   spec-198 first-collection exemption — each new company's FIRST collection is unfiltered by design, which
   is what seeds it) are a deliberate seed spike; the question is whether the backlog drains after it, not
   whether it rises on run 1. The per-run typing accounting is read from the durable
   `attention-decomposition-{asOfDate}` artifact where it survived, and from the run log where a same-day
   run overwrote it (the spec 200 §5 defect, still owed its own spec).

**What the three-run read CAN and CANNOT mean (cold-start caveat, identical to spec 200 §4).** The stored
`AttentionScore` uses a **60-day** window; after three daily runs these companies will hold only a few days
of locally captured history against incumbents carrying 60 days, so the whole cohort will be mechanically
depressed (spec 200 §6 measured 26–57 for the spec-199 cohort under the same conditions). The read therefore
tests **query relevance, capture shape and early calibration**. It is **NOT proof** that any company is
durably under-covered or quietly covered, and **no company may be removed, re-tiered or have its feed
tuned** on the strength of it. Name the reason for any material miss rather than revising the hypothesis to
fit the measurement; no predicted band, reason or band total above may be changed.

The mature descriptive read — the first successful run whose 60-day attention window starts no earlier
than the first post-207 collection instant — is to be dated here when that instant is known (first post-207
collection instant + 60 days), descriptive only, not a gate.
