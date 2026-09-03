# Task: Add eight AI-robotics small/mid caps to the watch universe

## Overview

The universe (94 companies since spec 199) carries no robotics or AI-robotics name — the closest neighbours
are MRCY (defense electronics), KLIC (chip assembly equipment), DGII (industrial IoT) and HLIO (motion
control components). The maintainer wants the theme represented (2026-09-02). This spec adds **eight
US-listed AI/robotics companies**, selected on 2026-09-02 for fit with the under-covered small/mid thesis
rather than fame: the well-known theme names (Serve Robotics, Ondas, SoundHound, BigBear.ai, Symbotic) were
deliberately **excluded** as heavily covered or too large, Richtech Robotics was excluded because its ticker
`RR` is unusable as a news token (Rolls-Royce), and FARO (acquired by AMETEK 2025-07) and iRobot (Chapter 11
2025-12, ownership transferred to Shenzhen PICEA 2026-01) were excluded because neither is an independent
US-listed company any longer.

Additions only. No removals, renames, re-tiers or feed changes to existing companies. `benchmark-universe-v1`
stays byte-identical; the additions report `NotInBenchmarkUniverse`; no v2 is declared.

## Entry condition — the spec-200 §5 capacity gate (HARD)

Spec 200 §5 precommits: **NOT DRAINING or UNRESOLVED freezes further universe expansion.** Before making any
change, read the spec-200 capacity verdict (its §5 measured record; the spec lives in `docs/next/200-…` until
Phase B promotes it to `docs/`):

- Verdict **DRAINING** recorded from three successful post-199 full runs → proceed.
- Verdict absent, NOT DRAINING, or UNRESOLVED → **stop immediately**, implement nothing, report the blocking
  verdict in your output, and leave this spec in `docs/next/` untouched. Do not argue with the verdict, do not
  substitute your own drain measurement, and do not implement a subset.

## Assignment

Worktree: any. Dependencies: spec 200 **Phase B** completed with a DRAINING verdict (see gate above); specs
205/206 merged. Use `run-next.ps1 -Spec 207` — spec 200 may still be resident in `docs/next/`, so explicit
selection is required.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. The eight additions

Seed each row in `data/companies.json` following the existing entry shape exactly (see the ESQ entry as the
model): minted `id` GUID, `name`/`legalName`, ticker, exchange, `countryCode: "US"`, sector/industry,
`followingTier`, aliases, themes, and the four standard `sourceFeeds` (sec, secform4, sec13dg, newssearch).

| ticker | company | tier | why (2026-09-02 selection record) |
| --- | --- | --- | --- |
| PDYN | Palladyne AI Corp. | small | AI autonomy software for industrial/defense robots (ex-Sarcos); revenue inflecting (record Q2 2026, +470 % YoY) |
| STXS | Stereotaxis, Inc. | small | Robotic surgical navigation; ~$0.14 B; very quiet; FDA catalysts play to the existing fda collector |
| CMCO | Columbus McKinnon Corporation | small | Intelligent motion / automation; ~$0.5 B; classic quiet industrial in the robotics supply chain |
| ALNT | Allient Inc. | small | Precision motion control powering robots; ~$1.6 B; low-coverage industrial |
| OUST | Ouster, Inc. | mid | Digital lidar + AI perception software; ~$2.5 B; **the declared identity RISK CASE — see §2** |
| PRCT | PROCEPT BioRobotics Corporation | mid | Surgical robotics with a real adoption curve |
| NOVT | Novanta Inc. | mid | Photonics/precision-motion subsystems into robotics and medtech; ~$5.4 B upper bound of "mid" |
| AMBA | Ambarella, Inc. | mid | Edge-AI vision silicon that shipping robots actually carry |

Universe becomes **102** (94 + 8; `small` 55 → 59). Market caps above are the 2026-09-02 selection record —
history, not values to re-verify or maintain.

**CIK resolution — never from memory.** Resolve every CIK from the canonical SEC mapping
(`https://www.sec.gov/files/company_tickers.json`), then live-verify each
`https://data.sec.gov/submissions/CIK{padded}.json` returns the expected company name and ticker (spec-199
procedure; respect the SEC UA and pacing rules — [[radar-sec-www-block]] is the failure mode). The same
check settles each row's current exchange; do not copy an exchange from this spec or a stale quote page. A
ticker whose CIK cannot be verified live is **dropped from the batch and named in the PR body** — never
seeded on a guess (a wrong CIK silently collects another company's filings).

## 2. News-feed identity — collision analysis (the spec-198/200 lessons, applied at the seed)

`NewsAttentionCollector.IsRelevant` is an unanchored case-insensitive substring match on the query phrase or
ticker token, so every phrase/ticker below was chosen against that predicate. Exact required URLs:

| ticker | newssearch url | collision reasoning |
| --- | --- | --- |
| PDYN | `query=Palladyne AI&ticker=PDYN` | distinctive name and token |
| STXS | `query=Stereotaxis&ticker=STXS` | unique word |
| CMCO | `query=Columbus McKinnon&ticker=CMCO` | two-word name is distinctive; bare "Columbus" would collide with the city, so never shorten it |
| ALNT | `query=Allient&ticker=ALNT` | unique word |
| OUST | `query=Ouster Inc` — **no ticker token** | `OUST`/"ouster" is a common English noun ("CEO's ouster"), colliding as BOTH ticker and bare company name. Phrase includes `Inc` for precision; OUST joins the colliding-ticker allowlist beside ESQ |
| PRCT | `query=PROCEPT BioRobotics&ticker=PRCT` | distinctive |
| NOVT | `query=Novanta&ticker=NOVT` | unique word |
| AMBA | `query=Ambarella&ticker=AMBA` | unique word |

**OUST is the declared identity risk case, in both directions, recorded now so the reader is not surprised
later:** the `Ouster Inc` phrase deliberately trades recall for precision — headlines that write only
"Ouster (OUST)" will be missed, so OUST's measured Attention may read LOW for reasons that are about feed
identity, not coverage; while any future loosening of the phrase risks admitting ouster-the-noun headlines
and inflating Attention. Neither direction may be "fixed" by tuning inside this spec; if the three-run read
shows the phrase is starving the feed, that is a measured follow-up spec against the relevance predicate,
not a quiet query edit.

No global change to `IsRelevant` (spec 200's deliberate-narrowness rule stands). No rss/IR press feeds for
this batch — newssearch + the three SEC feeds only; per-company IR feeds are a separate, measured decision
(the existing rss set already carries recurring transport-error noise).

## 3. Tests

Extend `ProductionCompanySeedTests`:

- assert **102** companies and byte-stable membership;
- pin the eight exact `newssearch` URLs above (not merely ticker presence/absence);
- add `OUST` to `TickersWithoutTickerToken` with the collision reason, beside ESQ/ITIC;
- pin the eight live-verified CIKs (the values the implementer resolved, cited to
  `company_tickers.json` — this test is then the owner of those values);
- the 20 spec-199 ids and all prior pins unchanged.

Extend `NewsAttentionCollectorTests` through the public collection surface (never a public `IsRelevant`)
with at minimum the OUST adversarial pair:

| feed | must reject | must accept |
| --- | --- | --- |
| OUST | `Shareholders demand the CEO's ouster after proxy fight` | `Ouster Inc. reports quarterly results` |
| CMCO | `Columbus city council approves transit plan` | `Columbus McKinnon expands automation line` |

Accepted cases must produce evidence for the intended company; rejected cases none. URL dedupe, the spec-198
`when:7d` recency window (each new company's FIRST collection is unfiltered by design — that exemption is
what seeds it), the 25-slot retained prefix, the 100-item parse ceiling and evidence identity all unchanged.

## 4. Predeclared attention predictions (falsifiable, before any collection)

Create `docs/cohorts/ai-robotics-2026-09.md` following `under-covered-2026-08.md`'s shape. Predictions
against the stored 60-day `AttentionScore`, bands low `<55` / mid `55–70` / high `>70`, committed now:

| ticker | predicted band |
| --- | --- |
| PDYN | mid |
| STXS | low |
| CMCO | low |
| ALNT | low |
| OUST | mid |
| PRCT | mid |
| NOVT | mid |
| AMBA | mid |

Predicted: **3 low / 5 mid / 0 high.** OUST is the declared risk case (§2 — its measurement is entangled with
its feed identity in both directions; a low reading is NOT evidence of under-coverage without checking the
feed's admitted-item counts first). The cohort doc carries a **three-run retrospective, OWED after three
successful post-207 full runs** (cold-start caveats identical to spec 200 §4: a few days of capture under a
60-day window tests query relevance and capture shape, not the coverage thesis; no company may be removed,
re-tiered or have its feed tuned from that read). Also record there, from the same three runs, the
`untypedRemaining` deltas — the eight unfiltered first collections are a deliberate seed spike, and the
post-spike drain check is the same arithmetic spec 200 §5 defined. Label every forward-looking number in
this spec PROJECTED until those runs exist.

This spec is **single-phase**: it promotes to `docs/` on implementation (unlike spec 200), because the owed
measurement lives in the cohort doc, which names its own entry condition. Do not hold the spec in
`docs/next/` waiting for the retrospective.

## Non-goals

- No removals, renames, re-tiers, alias/feed edits to existing companies; no rss feeds for the new eight.
- No `benchmark-universe-v2`; benchmark-v1 byte-identical; no AD-15/AD-16 boundary movement.
- No score formula, weight, strategy, channel, prompt, typing-budget, recency-window or collector change.
- No global ticker/relevance predicate redesign; OUST's allowlist entry is the whole predicate change.
- No use of the attention predictions or any price outcome as a scoring input.
- Seed-only edits move no scoring fingerprint: all six pins unchanged (`ScoringConfigFingerprintTests` is
  the authority).

## Acceptance criteria

- [ ] The spec-200 §5 verdict was read and is DRAINING; the PR body quotes it. (Otherwise: nothing changed,
      blocking verdict reported.)
- [ ] Eight companies added, additions-only, universe 102; every CIK live-verified via
      `company_tickers.json` + `data.sec.gov` at implementation time; any unverifiable ticker dropped and
      named.
- [ ] Exact newssearch URLs as specified; OUST on the colliding-ticker allowlist; adversarial accept/reject
      tests pass through the public surface.
- [ ] `docs/cohorts/ai-robotics-2026-09.md` records the eight predictions unchanged, the OUST risk case, and
      the owed three-run retrospective + post-spike drain check.
- [ ] benchmark-v1 byte-identical; new ids resolve `NotInBenchmarkUniverse`; all six fingerprint pins
      unchanged.
- [ ] `run-radar.ps1 -Profile default -WhatIf` resolves 102 companies; build, full suite and
      `git diff --check` clean; actual elapsed time in the PR body.
