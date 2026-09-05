# Watch-floor corroboration audit — raw firings vs support episodes (spec 210 §3)

**Read-only, from persisted data only.** Measured 2026-09-05 (dispatch of spec 210) with a Python pass over
the main checkout's store; no code path was run. Sources: `data/reports/weekly/*.md` (59 reports,
2026-06-30 → 2026-09-05) for the firings (every `- Why:` line containing `floored to Watch`, each paired with
the `Score snapshot:` id two lines below it); `data/scores/<company>/<snapshot>.json` and
`data/scores/strategies/<strategy>/<company>/<snapshot>.json` for the snapshot's score-evidence links;
`data/signals/**/<signalId>.json` for type, direction, `observedAt` and the spec-191/194 metadata envelope;
`data/evidence/raw/**/*.json` (24,554 files, keyed by content hash, read for `evidenceId`/`sourceType`/`title`).

**What was counted.** A *firing* is one floored entry in one report. A *support episode* is keyed on
(company, the DISTINCT positive signal types that satisfied the floor, the evidence ids behind those positive
signals SORTED) — neutral/negative contributing evidence is deliberately excluded from the key, so unrelated
neutral evidence arriving does not mint a new episode. Signal types are the STORED enum names
(`GuidanceChange` is what the report renders as `EarningsTrajectory` — the spec-167 relabel is
presentation-only). The observed date is the signal's `observedAt` (the date the v3 rationale renders).
`judgment` marks a `news-judgment-signal-v2` envelope carrying a judgment id and `retired-v1` a
`news-judgment-signal-v1` envelope — that split is the AUDIT's own finer partition by version token.
`NewsDirectionalSignalMetadata.IsJudgmentDerived` (the predicate the v3 rationale uses) ACCEPTS BOTH (spec 197
§1.3 retired v1 but kept it in `SupportedJudgmentSignalVersions`), so the live rationale prints a `retired-v1`
tuple as `, judgment`; only `inherited-191` (the accrued spec-191 envelope with no version token) is rejected by
that predicate and would print with no judgment marker.

**Coverage gaps, counted.** 20 of the 102 distinct positive-support evidence ids were not found anywhere under
`data/evidence/raw/` — all 25 affected signals are Aehr Test Systems press releases observed 2026-06-17 →
2026-07-14, collected before spec 145 made evidence identity content-derived. Their source class below is
marked `press release*` (inferred from the signal's stored company mention, not read from evidence). They
also explain why AEHR has nine episodes for what is visibly one support shape: pre-145 runs re-minted
evidence ids for the same releases, so the evidence-id key splits them. Shape-deduplicated (company +
rendered tuples) the 55 episodes collapse to 50.

## Headline

| Measure | Value |
|---|---:|
| Weekly reports scanned | 59 |
| Reports with at least one Watch-floor firing | 41 (2026-07-22 → 2026-09-05) |
| Raw firings (report occurrences) | 288 |
| Deduplicated support episodes | 55 |
| Shape-deduplicated episodes (company + rendered tuples) | 50 |
| Companies floored at least once | 22 |
| Largest single episode | 27 occurrences (Agilysys, AGYS — one unchanged support set across 27 nightly reports) |
| Episodes with ANY two counted types observed on the same date | 24 / 55 |
| Episodes matching the hypothesised shape (filing-typed positive + judgment-derived `MediaAttention` same day) | 9 / 55 (24 of 288 firings) |
| Positive-support signals by provenance | 164 keyword/filing-read · 45 judgment-derived (v2) · 5 retired-v1 · 1 inherited-191 — i.e. 50 accepted by `IsJudgmentDerived`, 1 rejected |
| Positive-support signals by source class | 79 press release · 60 filing · 51 news · 25 press release* (evidence missing) |

## The nine shape matches, inspected

A same-day pair is only a *candidate*; the titles decide whether it is one event seen twice.

| Company | Same day | Filing-typed positive | Judgment-derived `MediaAttention` | One event? | Does the floor rest on the pair alone? |
|---|---|---|---|---|---|
| Ooma (OOMA) — episodes 47, 48 | 2026-08-26 | 8-K item 2.02 (Q2 results) | "OOMA: Record Q2 growth and raised outlook…" | **YES — genuine echo** | **YES** (exactly two counted types; episode 48 adds a second news date 2026-08-28 to the same type, not a third type) |
| Argan (AGX) — episode 11 | 2026-09-02 | 8-K item 2.02 (results) | "Argan Announces Earnings Results, Beats Expectations…" (MarketBeat) | **YES — genuine echo** | **YES** (exactly two counted types) |
| Sterling Infrastructure (STRL) — 52 | 2026-08-03 | 8-K item 2.02 (Q2 results) | "Sterling Infrastructure Beats Q2 Earnings and Revenue Estimates" | YES | No — `StrategicPartnership` (filing 2026-07-08) is a third, separate-date type |
| Digi International (DGII) — 21, 22 | 2026-08-31 | 8-K item 1.01 (material definitive agreement) | "Digi International secures larger credit facility to 2031" / "$350M revolving credit facility" | YES (the same credit facility) | No — `GuidanceChange` (filing 2026-08-05) is a third type |
| IDT (IDT) — 33, 34 | 2026-08-18 | 8-K item 1.01 (material definitive agreement) | "IDT Corp – unit enters amendment to revolving credit agreement…" | YES | Partly — `StrategicPartnership` also has a press release on 2026-08-11, but the same-day filing is the only 8-K support |
| Middlesex Water (MSEX) — 42 | 2026-07-30 | 8-K item 2.02 (results) | "Middlesex Water Invests About $53M in Water and Wastewater Infrastructure" | **NO** — a same-day coincidence of two different announcements | n/a |

Two further probable echoes fall OUTSIDE the same-day heuristic and are listed so the heuristic is not mistaken
for a measurement of echoes: NWPX (episodes 43–45) counts the 2026-07-29 earnings 8-K plus a judgment-derived
"NWPX Reports Strong Earnings…" article observed 2026-07-31 (two-day lag); Middlesex (42) also carries a
2026-08-05 "Stronger Earnings…" article six days after its 8-K. The spec's motivating NWPX claim (that the
September Serpentix coverage echoed a filing) is NOT what the store shows — the Serpentix articles
(2026-09-02/03) have no filing-typed counterpart — but the July earnings pair is a lagged echo of the same
kind. A distinct-date requirement would therefore not be a sufficient identity test either.

**Verdict for spec 210 §3:** a genuine live echo exists. The cleanest instance is **Ooma, 2026-08-26**: the
floor in `radar-weekly-2026-08-30` rested entirely on one earnings release seen by two extractors. It is pinned
in `WeeklyReportActionPolicyV1Tests` beside the synthetic fixture. Whether that should change what gets floored
is the maintainer's call, with this table in hand — this slice changes no label.

## All 55 support episodes

Ordered by company, then first report. Tuples are (source class, observed date[, provenance]) in the order the
v3 rationale renders them (date, then source class). `press release*` = evidence file not found (see above).
`retired-v1` is the audit's token split only — the shipped rationale renders those five tuples as `judgment`.

| # | Company | Occurrences | First report | Last report | Counted types → support tuples (source class, observed date[, judgment]) | Same-day distinct-type pair | Filing-typed + judgment-derived MediaAttention same day |
| 1 | Aehr Test Systems (AEHR) | 1 | 2026-07-22 | 2026-07-22 | CustomerWin (press release* 2026-06-17; press release* 2026-07-09; press release* 2026-07-14) + StrategicPartnership (press release* 2026-07-14) | 2026-07-14 | none |
| 2 | Aehr Test Systems (AEHR) | 1 | 2026-07-23 | 2026-07-23 | CustomerWin (press release* 2026-06-17; press release* 2026-07-09; press release* 2026-07-14) + StrategicPartnership (press release* 2026-07-14) | 2026-07-14 | none |
| 3 | Aehr Test Systems (AEHR) | 1 | 2026-07-24 | 2026-07-24 | CustomerWin (press release* 2026-06-17; press release* 2026-07-09; press release* 2026-07-14) + StrategicPartnership (press release* 2026-07-14) | 2026-07-14 | none |
| 4 | Aehr Test Systems (AEHR) | 1 | 2026-07-25 | 2026-07-25 | CustomerWin (press release* 2026-06-17; press release* 2026-07-09; press release* 2026-07-14) + StrategicPartnership (press release* 2026-07-14) | 2026-07-14 | none |
| 5 | Aehr Test Systems (AEHR) | 1 | 2026-07-26 | 2026-07-26 | CustomerWin (press release* 2026-06-17; press release* 2026-07-09; press release* 2026-07-14) + StrategicPartnership (press release* 2026-07-14) | 2026-07-14 | none |
| 6 | Aehr Test Systems (AEHR) | 9 | 2026-07-27 | 2026-08-04 | CustomerWin (press release 2026-06-17; press release 2026-07-09; press release 2026-07-14) + StrategicPartnership (press release 2026-07-14) | 2026-07-14 | none |
| 7 | Aehr Test Systems (AEHR) | 6 | 2026-08-05 | 2026-08-10 | CustomerWin (press release 2026-06-17; press release 2026-07-09; press release 2026-07-14; press release 2026-08-04) + StrategicPartnership (press release 2026-07-14) | 2026-07-14 | none |
| 8 | Aehr Test Systems (AEHR) | 1 | 2026-08-13 | 2026-08-13 | CustomerWin (press release 2026-06-17; press release 2026-07-09; press release 2026-07-14; press release 2026-08-04; press release 2026-08-12) + StrategicPartnership (press release 2026-07-14) | 2026-07-14 | none |
| 9 | Aehr Test Systems (AEHR) | 3 | 2026-09-01 | 2026-09-03 | CustomerWin (press release 2026-07-09; press release 2026-07-14; press release 2026-08-04; press release 2026-08-12) + MediaAttention (news 2026-08-12, retired-v1; news 2026-08-22, judgment; news 2026-08-25, judgment) + StrategicPartnership (press release 2026-07-14) | 2026-07-14, 2026-08-12 | none |
| 10 | Agilysys, Inc. (AGYS) | 27 | 2026-07-29 | 2026-08-29 | GuidanceChange (press release 2026-07-13; filing 2026-07-27) + StrategicPartnership (press release 2026-06-30) | none | none |
| 11 | Argan, Inc. (AGX) | 1 | 2026-09-05 | 2026-09-05 | GuidanceChange (filing 2026-09-02) + MediaAttention (news 2026-09-02, judgment) | 2026-09-02 | 2026-09-02 |
| 12 | Cass Information Systems, Inc. (CASS) | 5 | 2026-08-30 | 2026-09-05 | GuidanceChange (filing 2026-07-23) + MediaAttention (news 2026-08-05, judgment) | none | none |
| 13 | Commvault Systems, Inc. (CVLT) | 1 | 2026-08-29 | 2026-08-29 | GuidanceChange (filing 2026-07-28) + MediaAttention (news 2026-08-25, judgment) | none | none |
| 14 | Commvault Systems, Inc. (CVLT) | 2 | 2026-08-30 | 2026-09-01 | GuidanceChange (filing 2026-07-28) + MediaAttention (news 2026-08-25, judgment; news 2026-08-27, judgment) | none | none |
| 15 | Commvault Systems, Inc. (CVLT) | 2 | 2026-09-02 | 2026-09-03 | GuidanceChange (filing 2026-07-28) + MediaAttention (news 2026-08-25, judgment; news 2026-08-27, judgment; news 2026-08-31, judgment) | none | none |
| 16 | Commvault Systems, Inc. (CVLT) | 1 | 2026-09-05 | 2026-09-05 | GuidanceChange (filing 2026-07-28) + MediaAttention (news 2026-08-25, judgment; news 2026-08-27, judgment; news 2026-08-31, judgment; news 2026-09-02, judgment) | none | none |
| 17 | Digi International Inc. (DGII) | 3 | 2026-08-06 | 2026-08-08 | GuidanceChange (filing 2026-08-05) + ProductLaunch (press release 2026-06-09; press release 2026-06-30) | none | none |
| 18 | Digi International Inc. (DGII) | 16 | 2026-08-09 | 2026-08-27 | GuidanceChange (filing 2026-08-05) + ProductLaunch (press release 2026-06-30) | none | none |
| 19 | Digi International Inc. (DGII) | 1 | 2026-08-28 | 2026-08-28 | GuidanceChange (filing 2026-08-05) + MediaAttention (news 2026-08-09, retired-v1) + ProductLaunch (press release 2026-06-30) | none | none |
| 20 | Digi International Inc. (DGII) | 2 | 2026-08-29 | 2026-08-30 | GuidanceChange (filing 2026-08-05) + MediaAttention (news 2026-08-09, retired-v1; news 2026-08-24, judgment) | none | none |
| 21 | Digi International Inc. (DGII) | 2 | 2026-09-01 | 2026-09-02 | GuidanceChange (filing 2026-08-05) + MediaAttention (news 2026-08-09, retired-v1; news 2026-08-24, judgment; news 2026-08-31, judgment) + StrategicPartnership (filing 2026-08-31) | 2026-08-31 | 2026-08-31 |
| 22 | Digi International Inc. (DGII) | 2 | 2026-09-03 | 2026-09-05 | GuidanceChange (filing 2026-08-05) + MediaAttention (news 2026-08-09, retired-v1; news 2026-08-24, judgment; news 2026-08-31, judgment) + StrategicPartnership (filing 2026-08-31) | 2026-08-31 | 2026-08-31 |
| 23 | Easterly Government Properties, Inc. (DEA) | 21 | 2026-08-05 | 2026-08-28 | GuidanceChange (filing 2026-08-03) + StrategicPartnership (filing 2026-06-30) | none | none |
| 24 | Eos Energy Enterprises, Inc. (EOSE) | 10 | 2026-07-27 | 2026-08-05 | CustomerWin (press release 2026-07-15) + ExecutiveHire (press release 2026-07-09) + GovernmentContract (press release 2026-07-15) + ProductLaunch (press release 2026-06-16) + StrategicPartnership (press release 2026-06-17; filing 2026-06-30; press release 2026-06-30; press release 2026-07-09; press release 2026-07-15) | 2026-07-09, 2026-07-15 | none |
| 25 | Eos Energy Enterprises, Inc. (EOSE) | 7 | 2026-08-06 | 2026-08-14 | CustomerWin (press release 2026-07-15) + ExecutiveHire (press release 2026-07-09) + GovernmentContract (press release 2026-07-15) + ProductLaunch (press release 2026-06-16) + StrategicPartnership (press release 2026-06-17; filing 2026-06-30; press release 2026-06-30; press release 2026-07-09; press release 2026-07-15; filing 2026-08-06) | 2026-07-09, 2026-07-15 | none |
| 26 | Eos Energy Enterprises, Inc. (EOSE) | 1 | 2026-08-16 | 2026-08-16 | CustomerWin (press release 2026-07-15) + ExecutiveHire (press release 2026-07-09) + GovernmentContract (press release 2026-07-15) + StrategicPartnership (press release 2026-06-17; filing 2026-06-30; press release 2026-06-30; press release 2026-07-09; press release 2026-07-15; filing 2026-08-06) | 2026-07-09, 2026-07-15 | none |
| 27 | Eos Energy Enterprises, Inc. (EOSE) | 12 | 2026-08-17 | 2026-08-28 | CustomerWin (press release 2026-07-15) + ExecutiveHire (press release 2026-07-09) + GovernmentContract (press release 2026-07-15) + StrategicPartnership (filing 2026-06-30; press release 2026-06-30; press release 2026-07-09; press release 2026-07-15; filing 2026-08-06) | 2026-07-09, 2026-07-15 | none |
| 28 | Eos Energy Enterprises, Inc. (EOSE) | 1 | 2026-08-30 | 2026-08-30 | CustomerWin (press release 2026-07-15) + ExecutiveHire (press release 2026-07-09) + GovernmentContract (press release 2026-07-15) + StrategicPartnership (press release 2026-07-09; press release 2026-07-15; filing 2026-08-06) | 2026-07-09, 2026-07-15 | none |
| 29 | Eos Energy Enterprises, Inc. (EOSE) | 2 | 2026-09-01 | 2026-09-05 | CustomerWin (press release 2026-07-15) + ExecutiveHire (press release 2026-07-09; press release 2026-08-25) + GovernmentContract (press release 2026-07-15) + StrategicPartnership (press release 2026-07-09; press release 2026-07-15; filing 2026-08-06; press release 2026-08-25; press release 2026-08-27) | 2026-07-09, 2026-07-15, 2026-08-25 | none |
| 30 | Esquire Financial Holdings (ESQ) | 1 | 2026-08-29 | 2026-08-29 | GuidanceChange (filing 2026-07-23) + StrategicPartnership (filing 2026-08-03) | none | none |
| 31 | Esquire Financial Holdings (ESQ) | 1 | 2026-08-30 | 2026-08-30 | GuidanceChange (filing 2026-07-23) + MediaAttention (news 2026-08-25, judgment) + StrategicPartnership (filing 2026-08-03) | none | none |
| 32 | Esquire Financial Holdings (ESQ) | 4 | 2026-09-01 | 2026-09-05 | GuidanceChange (filing 2026-07-23) + MediaAttention (news 2026-08-10, judgment; news 2026-08-25, judgment) + StrategicPartnership (filing 2026-08-03) | none | none |
| 33 | IDT Corporation (IDT) | 4 | 2026-08-30 | 2026-09-03 | MediaAttention (news 2026-08-18, judgment) + StrategicPartnership (press release 2026-08-11; filing 2026-08-18) | 2026-08-18 | 2026-08-18 |
| 34 | IDT Corporation (IDT) | 1 | 2026-09-05 | 2026-09-05 | MediaAttention (news 2026-08-18, judgment; news 2026-09-03, judgment) + StrategicPartnership (press release 2026-08-11; filing 2026-08-18) | 2026-08-18 | 2026-08-18 |
| 35 | IMAX Corporation (IMAX) | 5 | 2026-07-27 | 2026-07-31 | GuidanceChange (filing 2026-07-23) + StrategicPartnership (press release 2026-06-01; press release 2026-07-15) | none | none |
| 36 | IMAX Corporation (IMAX) | 21 | 2026-08-01 | 2026-08-25 | GuidanceChange (filing 2026-07-23) + StrategicPartnership (press release 2026-07-15) | none | none |
| 37 | Innospec Inc. (IOSP) | 9 | 2026-08-26 | 2026-09-05 | GuidanceChange (filing 2026-08-05) + MediaAttention (news 2026-07-21, inherited-191) | none | none |
| 38 | Johnson Outdoors (JOUT) | 5 | 2026-08-30 | 2026-09-05 | GuidanceChange (filing 2026-08-07) + MediaAttention (news 2026-08-12, judgment) | none | none |
| 39 | Liberty Energy Inc. (LBRT) | 20 | 2026-08-01 | 2026-08-23 | InsiderBuying (filing 2026-07-30) + StrategicPartnership (filing 2026-06-25) | none | none |
| 40 | Liberty Energy Inc. (LBRT) | 4 | 2026-09-01 | 2026-09-05 | InsiderBuying (filing 2026-07-30) + MediaAttention (news 2026-08-21, judgment) | none | none |
| 41 | Middlesex Water Company (MSEX) | 1 | 2026-08-29 | 2026-08-29 | GuidanceChange (filing 2026-07-30) + MediaAttention (news 2026-08-05, judgment) | none | none |
| 42 | Middlesex Water Company (MSEX) | 5 | 2026-08-30 | 2026-09-05 | GuidanceChange (filing 2026-07-30) + MediaAttention (news 2026-07-30, judgment; news 2026-08-05, judgment) | 2026-07-30 | 2026-07-30 |
| 43 | NWPX Infrastructure (NWPX) | 2 | 2026-09-01 | 2026-09-02 | GuidanceChange (filing 2026-07-29) + MediaAttention (news 2026-07-31, judgment) | none | none |
| 44 | NWPX Infrastructure (NWPX) | 1 | 2026-09-03 | 2026-09-03 | GuidanceChange (filing 2026-07-29) + MediaAttention (news 2026-07-31, judgment; news 2026-09-02, judgment) | none | none |
| 45 | NWPX Infrastructure (NWPX) | 1 | 2026-09-05 | 2026-09-05 | GuidanceChange (filing 2026-07-29) + MediaAttention (news 2026-07-31, judgment; news 2026-09-03, judgment) | none | none |
| 46 | Novanta (NOVT) | 2 | 2026-09-03 | 2026-09-05 | GuidanceChange (filing 2026-08-05) + StrategicPartnership (filing 2026-07-27) | none | none |
| 47 | Ooma (OOMA) | 1 | 2026-08-30 | 2026-08-30 | GuidanceChange (filing 2026-08-26) + MediaAttention (news 2026-08-26, judgment) | 2026-08-26 | 2026-08-26 |
| 48 | Ooma (OOMA) | 4 | 2026-09-01 | 2026-09-05 | GuidanceChange (filing 2026-08-26) + MediaAttention (news 2026-08-26, judgment; news 2026-08-28, judgment) | 2026-08-26 | 2026-08-26 |
| 49 | Palomar Holdings, Inc. (PLMR) | 6 | 2026-08-29 | 2026-09-05 | GuidanceChange (filing 2026-08-04) + MediaAttention (news 2026-08-22, judgment) | none | none |
| 50 | Sterling Infrastructure, Inc. (STRL) | 21 | 2026-08-05 | 2026-08-29 | GuidanceChange (filing 2026-08-03) + StrategicPartnership (filing 2026-07-08) | none | none |
| 51 | Sterling Infrastructure, Inc. (STRL) | 1 | 2026-08-30 | 2026-08-30 | GuidanceChange (filing 2026-08-03) + MediaAttention (news 2026-08-26, judgment) + StrategicPartnership (filing 2026-07-08) | none | none |
| 52 | Sterling Infrastructure, Inc. (STRL) | 4 | 2026-09-01 | 2026-09-05 | GuidanceChange (filing 2026-08-03) + MediaAttention (news 2026-08-03, judgment; news 2026-08-17, judgment; news 2026-08-26, judgment) + StrategicPartnership (filing 2026-07-08) | 2026-08-03 | 2026-08-03 |
| 53 | UMH Properties, Inc. (UMH) | 5 | 2026-08-30 | 2026-09-05 | GuidanceChange (filing 2026-08-05) + MediaAttention (news 2026-08-20, judgment) | none | none |
| 54 | York Water Company (YORW) | 11 | 2026-08-19 | 2026-08-29 | GuidanceChange (filing 2026-08-06) + StrategicPartnership (press release 2026-08-10) | none | none |
| 55 | York Water Company (YORW) | 5 | 2026-08-30 | 2026-09-05 | GuidanceChange (filing 2026-08-06) + MediaAttention (news 2026-08-09, judgment) + StrategicPartnership (press release 2026-08-10) | none | none |
