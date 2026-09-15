# Task: the acquisition read never reads the 8-K — an inline-XBRL link hides the primary document, so 31 filings fail and 153 of the other 154 scan an exhibit instead

## Overview

Spec 227's live measurement reported that **31 of 185** item-1.01 filing bodies "failed deterministically", with
the reason *"no parseable document table"* or *"no primary document row"*. Spec 227 set this aside for its own spec.

**The cause is one line, and it is far worse than 31 filings.**

`SecFilingIndexTable.ExtractDocumentFileName` (`src/Radar.Infrastructure/Sec/SecFilingIndexTable.cs`) takes each
index row's first `<a href>`, cuts the href at `?`, and keeps the last path segment only if it ends in
`.htm`/`.html`. Since SEC's inline-XBRL rules, **EDGAR links the primary 8-K document through the inline viewer**:

```
href="/ix?doc=/Archives/edgar/data/700923/000070092326000047/myrg-20260908.htm"
```

Cut at `?`, that href is `/ix`. It is not `.htm`, so **the primary document's row is dropped**. Exhibits still
use plain `/Archives/...` links and are kept. Verified 2026-09-15 on two live index pages (HTTP 200):

| filing | index links | parser sees | outcome |
|---|---|---|---|
| MYRG `0000700923-26-000047` (items 1.01, 2.03, 9.01) | `/ix?doc=…/myrg-20260908.htm`, `.txt`, `.xsd`, `.xml` | no `.htm` rows | `no parseable document table` |
| HZO `0001193125-26-290439` (items 1.01, 1.02, 2.03, 7.01, 9.01) | `/ix?doc=…/hzo-20260629.htm`, `hzo-ex99_1.htm`, `.jpg`, `.txt` | one row, EX-99.1 | `no primary document row` |

That explains the 31 loud failures:
- An 8-K with no `.htm` exhibits leaves zero rows: `no parseable document table`.
- One with only EX-99 exhibits leaves no untyped row: `no primary document row`.

**The silent case is the other 154.** `SelectPrimaryDocument` falls back to "the first row whose `Type` is null".
`Type` is resolved only for EX-99 exhibits, so an **EX-10.1 credit agreement, an EX-2.1 merger agreement, an
EX-4.1 indenture or an EX-1.1 underwriting agreement** is silently taken as "the primary document". The
declared primary document the collector recorded from SEC's own submissions feed (`primaryDocument` in the
evidence metadata) never matches, because its row was never extracted.

**Measured from spec 227's harness bodies** (`%TEMP%\radar-spec227-acqscan-bodies`, one file per accession, the
exact text the scan received):

| body began with | filings |
|---|---:|
| EX-10.1 (material contract — credit agreements, purchase agreements) | 81 |
| EX-2.1 (merger / acquisition agreement) | 21 |
| EX-1.1 (underwriting agreement) | 21 |
| EX-4.1 / 4.2 / 4.11 (indentures, credit agreements) | 20 |
| EX-5.1 (legal opinion) | 3 |
| EX-3.1 (charter / bylaws) | 3 |
| **the 8-K itself** | **1** |
| empty (read failed) | 31 |

Only **1 of 154** scanned bodies contains the 8-K cover ("CURRENT REPORT Pursuant to Section 13"), and only 1
contains an "Item 1.01" heading.

**So `acqscan-v1` and `acqscan-v2` have almost never read the document spec 217 designed them for.**
`AcquisitionAgreementScan`'s own documentation says the item-1.01 narrative "lives in the PRIMARY 8-K document"
and that "leg (a) and leg (b) of the scan therefore live in different documents". In practice the scan has
read an exhibit plus EX-99.1.

That is also how the SHOO false positive assembled itself:
- its "primary" was the amended and restated **credit agreement**, whose signature pages ran straight into the
  EX-99.1 results release;
- HZO's genuine recognition came from the **EX-2.1 merger agreement**, whose "par value $0.001 per share"
  recital spec 227 then had to learn to skip.

Consequences:

- **Blind spots.** 31 filings are never scanned, and each is re-fetched every run once the rescan drain reaches
  it: up to 3 wasted `www.sec.gov` requests per filing per run, forever. They include **HZO's own 2026-06-30
  credit agreement**, two OOMA filings (2025-11-03 and 2025-12-01) and MYRG's 2026-09-10 credit facility.
- **Wrong-document answers, cached as authoritative.** 153 not-recognised answers were computed from an
  exhibit. The scan cache makes each permanent under its scan version. A takeover whose target language is in
  the 8-K narrative, while the attached EX-10.1 is, say, a voting agreement, would be missed forever.
- **Scope of the defect.** `SecFilingIndexTable.Parse` is shared with `HttpSecEarningsReleaseReader`, which selects
  only EX-99 rows, so the earnings read is not affected. `SelectPrimaryDocument` has one caller: the acquisition
  reader.

## Assignment

Worktree: any. Dependencies: main at `39eaaf6` or later (spec 227 merged and its operator step taken).

Use `run-next.ps1 -Spec 228`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Read the document the filing says is primary

- **Parse inline-viewer links.** An `/ix?doc=/Archives/.../{file}.htm` href yields `{file}.htm` as that row's
  document, taken from the `doc` parameter's last path segment. Plain `/Archives/.../{file}.htm` links are
  unchanged. Fix this in `SecFilingIndexTable` — the one shared parser — not in the acquisition reader.
- **Resolve the primary document from what SEC says, never by position.** Order of authority:
  1. the declared `primaryDocument` from the evidence metadata, when the index carries that row;
  2. the index row whose **Type is the form itself** (`8-K`, `8-K/A`).

  Resolve `Type` for every row, not only EX-99: the index table's Type column is already there. Delete the
  "first untyped row" fallback. It is how an EX-10.1 became "primary" 153 times, and a guessed primary is
  exactly what "never guesses" in its own doc comment forbids. No authoritative primary means a named,
  counted failure.
- **The earnings read must not change.** `SelectEarningsExhibit` and `HttpSecEarningsReleaseReader` behaviour stay
  byte-identical. Prove it with the existing earnings-reader tests plus a fixture carrying an `/ix?doc=` primary.
- **What gets scanned.** The acquisition body becomes the real 8-K primary document, plus EX-99.1 when present
  (as designed).
  - Decide deliberately whether a material-contract exhibit (EX-2.1 / EX-10.1) should also be appended. The
    merger agreement often states the per-share consideration when the press release does not.
  - If you append it, the order is primary → EX-99.1 → agreement, and §3 reports what the exhibit adds.
  - If you don't, §3 reports what it costs.
  - Either way, a body can no longer START with an exhibit that is posing as the 8-K.
- **Real index-page fixtures.** Pin the two index pages quoted above, MYRG (no exhibits) and HZO 2026-06-30 (EX-99.1
  only), trimmed and with provenance noted. Also pin one index page with an EX-10.1 plus an `/ix?doc=` primary,
  where the old code chose the exhibit.

## 2. Identity — the read is part of the answer

A recognition is a function of **which text was read** and of the rule that scans it. Changing the reader changes
the answer for the same accession, so every cached answer and store record computed from the old read is stale in
exactly the way spec 227's v1 records were.

- **Reuse spec 227's machinery; add no second identity axis.** Bump `AcquisitionAgreementScan.Version` to
  `acqscan-v3` and document that the version covers the READ as well as the rule. Spec 227 made the scan version
  part of the store path, the cache key, the `PendingAcquisitions` admission test and the `RetiredByScanVersion`
  count, so a bump retires every v2 answer and record without touching a file.
- **HZO is un-pending until its v3 rescan persists.** The rescan runs newest-first, so this should be the first
  post-merge run. Say so in the PR, as spec 227 did.
- **Pins and operator step.** `acq=` is hashed unconditionally, so both pin families move and one operator step is
  owed after merge. Update `ScoringConfigFingerprintTests` with lineage comments and state the step in the PR body.

## 3. Live measurement (required — produce it, do not report it as owed)

Re-run spec 227's harness pattern over the same population:
- env-gated;
- run against the main store at `C:\Users\scm9d\source\repos\radar\data`;
- store and cache writes throw;
- per-process temp report;
- a no-write confirmation afterwards;
- SEC requests paced through the existing throttle, with a real user agent, and a 200 check on `www.sec.gov` first.

Report:

- **Read outcomes, old reader vs new:** `no parseable document table`, `no primary document row`, any new named
  failure, and success. The expected result is 31 → 0 or near it; name every filing that still fails, with its
  reason.
- **What the body starts with,** old vs new, as in the Overview table. The new reader should put the 8-K cover
  first for (nearly) every success; state the count.
- **Scan tally, v2 vs v3,** with every filing whose outcome changed named (ticker, accession, both outcomes).
- **Every recognition under v3,** with consideration and acquirer. HZO 2026-08-10 must still be recognised at
  `53.00`, and SHOO must still not be.
- **The 31 formerly unreadable filings, one line each:** their v3 outcome. Explicitly: is any of them a takeover of
  a Radar company?
- **Request cost:** `www.sec.gov` requests per filing under the new reader.

## 4. Not in scope

- **Retrying genuinely transient read failures** (403/429/timeouts) is unchanged: counted and re-attempted.
- **Caching deterministic read failures.** Once the parser is fixed there should be (almost) none. If §3 still
  finds deterministic failures, say how many and leave caching them to a follow-up.
- **Whether a takeover signal should come from the 8-K narrative, EX-99.1 or EX-2.1 as a matter of rule design**,
  beyond the append decision in §1.

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. `SecFilingIndexTable.Parse` extracts `/ix?doc=` primary documents. `SelectPrimaryDocument` selects the declared or
   form-typed primary and never falls back to an arbitrary untyped row. Tests pin real index-page fixtures,
   including the old wrong-exhibit case.
3. Earnings-read behaviour is provably unchanged.
4. `acqscan-v3`, with its documentation covering the read; v2 records and cache entries retired and counted through
   spec 227's mechanism; no file under `data/acquisitions/` modified, moved or deleted.
5. §3 comes from the live store with SEC fetches and the no-write confirmation. HZO 2026-08-10 is still recognised
   at 53.00 and SHOO still is not.
6. `ScoringConfigFingerprintTests` updated with lineage comments; one operator step stated in the PR body.
7. Doc claims this slice touches are amended in place (REVERSAL rule), including:
   - `AcquisitionAgreementScan`'s and `HttpSecAcquisitionFilingReader`'s descriptions of what is read;
   - `SelectPrimaryDocument`'s "never guesses";
   - CLAUDE.md's "`acqscan-v1` reads the item-1.01 8-K itself" bullet, which was not true in practice.

## Appendix — §3 live measurement (MEASURED 2026-09-15)

**How it was produced.** `tests/Radar.IntegrationTests/AcquisitionReadPrimary8KLiveMeasurementTests.cs`, against the
main store (`RADAR_ACQREAD_V3_LIVE_DATA_ROOT`), with `RADAR_ACQREAD_V3_OLD_BODY_DIR` pointing at spec 227's verbatim
bodies (`%TEMP%\radar-spec227-acqscan-bodies`), in two runs on 2026-09-15:

1. **The live run.** A fresh `RADAR_ACQREAD_V3_DOCUMENT_CACHE_DIR` outside the data root, after a 200 check on
   `www.sec.gov`, with the EX-2.1 append already implemented. Every one of the production reader's 477
   `www.sec.gov` Archives requests (2 requests 93 filings, 3 requests 77, 4 requests 15) went over the wire through
   the shared paced SEC client, and each response was kept in that cache directory. It passed, with 0 of 190,922
   files under the data root changed. An earlier run the same morning, before the append, measured HZO
   `0001193125-26-341302` as `acquirer-not-named` without the agreement; that result decided the append, and the
   append table below reproduces it as the counterfactual.
2. **The re-render below.** After review, two report labels were corrected (see the next section) and the first
   line's request wording was reworded, so the harness was re-run against the SAME document cache. That re-run
   issued no Archives request (only the `www.sec.gov` home-page reachability probe), so its report says "0 fetched
   live" and "0 of them went over the wire this run". Every count, table and outcome is identical to the live run;
   the only differences are those labels and that line. The report below is the re-run's output, verbatim.

**The absence claim, checked by hand.** "Not recognised" is not "not a takeover": the scan fails closed, and
company-is-acquirer fires on ordinary 8-K narrative. So the 31 formerly unreadable filings were also READ, from the
same document cache (no new SEC request). The check had two parts. First, the Item 1.01 narrative of each primary 8-K
was read. Second, a phrase search ran across every document fetched for the filing: "to be acquired", "acquired by",
"merger sub", "tender offer", "arrangement agreement", "take private" / "take-private", "going private", "merger
consideration", "plan of merger", "per share in cash", "change of control" / "change in control". Every phrase hit
was read in context:
- CALM's hits are shares "acquired by" members of the family LLC under its operating agreement, and the filing's own
  statement that the conversion "has not" been and would not be a change of control.
- KGS's hit is the notes indenture's change-of-control covenant.
- EOSE's hit (`0000950103-26-009739`) is a JV term-sheet event.
- OOMA's "business to be acquired by Ooma" is Ooma buying.
- EPM's "interests to be acquired" are assets it buys.
- MMSI's "Merger Sub" is Merit's own acquisition vehicle. **Result: none of the 31 is a takeover of the Radar company.**

| ticker | accession | v3 outcome | what the Item 1.01 agreement actually is |
| --- | --- | --- | --- |
| SENEA | 0001437749-24-007174 | no-merger-agreement | third amendment to its loan and security agreement (Bank of America as agent) |
| IRMD | 0001558370-24-008847 | no-merger-agreement | lease amendment with a landlord controlled by the CEO |
| CTO | 0001558370-24-013088 | no-merger-agreement | $100M term-loan credit agreement (KeyBank) |
| AMBA | 0001193125-24-285963 | company-is-acquirer | office lease for new headquarters (Ambarella is the tenant) |
| CALM | 0001562762-25-000030 | no-merger-agreement | Conversion Agreement with the founder family's LLC over Class A shares; the filing itself says no third party would be acquiring control |
| MNRO | 0001193125-25-036829 | no-merger-agreement | amendment to its distribution agreement with American Tire Distributors (earnout payments) |
| MNRO | 0001193125-25-068897 | no-merger-agreement | CEO appointment arrangements incorporated from Item 5.02 |
| MMSI | 0000856982-25-000035 | company-not-target | Merit Medical acquiring Biolife by merger (Merit is the BUYER) |
| SHEN | 0001171843-25-003389 | no-merger-agreement | limited waiver of an existing investor's standstill, letting it buy up to 2,250,000 more shares; no agreement to acquire the company |
| MNRO | 0001193125-25-135933 | no-merger-agreement | consulting-agreement amendment (AlixPartners) |
| IMAX | 0001193125-25-159091 | no-merger-agreement | seventh amended and restated credit agreement (Wells Fargo) |
| DEA | 0001622194-25-000007 | no-merger-agreement | term-loan amendment to its credit agreement (Citibank) |
| MNRO | 0001193125-25-186435 | no-merger-agreement | consulting-agreement amendment (AlixPartners) |
| KGS | 0001767042-25-000061 | no-merger-agreement | senior notes indenture and ABL credit-agreement amendment |
| CTO | 0001104659-25-093346 | no-merger-agreement | credit-agreement amendment |
| OOMA | 0001193125-25-262903 | company-is-acquirer | stock purchase agreement to buy FluentStream (Ooma is the BUYER) |
| MNRO | 0001193125-25-283220 | no-merger-agreement | consulting-agreement amendment (AlixPartners) |
| OOMA | 0001193125-25-304183 | company-is-acquirer | credit-facility amendment at the closing of its FluentStream purchase (Ooma is the BUYER) |
| UMH | 0001493152-25-025730 | no-merger-agreement | addition to its Fannie Mae credit facility |
| GTY | 0001193125-25-306965 | no-merger-agreement | note purchase agreement (private placement of senior notes) |
| STXS | 0001493152-26-016685 | no-merger-agreement | share sale agreement to acquire Robocath (Stereotaxis is the BUYER) |
| PUMP | 0001680247-26-000058 | company-is-acquirer | global framework agreement with Caterpillar for power-generation equipment |
| LBRT | 0001694028-26-000027 | company-is-acquirer | two supply contracts with Bergen Engines for power-generation equipment |
| EOSE | 0001628280-26-034367 | no-merger-agreement | binding term sheet for a joint venture with a Cerberus affiliate |
| OTTR | 0001466593-26-000057 | no-merger-agreement | antitrust class-action settlement agreements (PVC pipe litigation) |
| OTTR | 0001466593-26-000063 | no-merger-agreement | antitrust class-action settlement agreement (end-user class) |
| LBRT | 0001694028-26-000034 | company-is-acquirer | supply contract with Wärtsilä for power-generation equipment |
| EOSE | 0000950103-26-009739 | no-merger-agreement | amended and restated joint-venture term sheet |
| HZO | 0001193125-26-290439 | no-merger-agreement | refinancing of its floor-plan credit facility |
| EPM | 0001104659-26-098332 | no-merger-agreement | purchase and sale agreement to acquire mineral and royalty interests (Evolution is the BUYER) |
| MYRG | 0000700923-26-000047 | no-merger-agreement | fourth amended and restated credit agreement |

Every company-is-acquirer outcome among them is either the company buying something (OOMA ×2) or not an acquisition
at all (AMBA's lease, PUMP's framework agreement, LBRT's two supply contracts). That is the acquirer-side-veto
over-firing recorded as a finding in `docs/architecture-history.md` (spec-228 bullet).

### Report — the item-1.01 read before and after spec 228, and `acqscan-v2` vs `acqscan-v3`, over every item-1.01 filing in the store

Data root `C:\Users\scm9d\source\repos\radar\data`, read-only. New read: the production `HttpSecAcquisitionFilingReader` through the shared paced SEC client. Old read: spec 227's verbatim bodies/failures where recorded, cross-checked against the pre-228 control; the control itself otherwise. SEC Archives requests this run (production reader and harness together): 0 fetched live over the wire, 954 served from the document cache outside the data root (a URL already fetched — by this run's reader or an earlier run). Scan rule: the production `AcquisitionAgreementScan` for both columns (the rule did not change; the read did).

- Item-1.01 filing evidence items: **190** (+0 untrustworthy identifiers, never fetched); unresolved company 0; second evidence items for an already-measured accession 5; **accessions measured 185**.
- Old read source: 154 spec-227 verbatim bodies, 31 spec-227 verbatim failures, 0 through the control. Control vs recorded read: 185 agree, 0 disagree.

#### Read outcomes — old reader vs new

| outcome | old (pre-228) | new (spec 228) |
| --- | ---: | ---: |
| success | 154 | 185 |
| failed: no parseable document table | 15 | 0 |
| failed: no primary document row | 16 | 0 |

Still failing under the new reader: **0**.

Primary chosen by: declared `primaryDocument` 185, form-typed row 0. Declared primary present in the index but NOT the form-typed row: 0.

#### What the scanned body starts with — the index Type of the FIRST document read

(Old: the document the pre-228 selection took as primary, as the control names it — its file name is in the recorded body's SEC header for every one of the verbatim bodies — typed from today's index Type column. New: the primary the spec-228 reader selected.)

| body began with | old (pre-228) | new (spec 228) |
| --- | ---: | ---: |
| (read failed) | 31 | 0 |
| 8-K | 1 | 185 |
| EX-1.1 | 21 | 0 |
| EX-10 | 1 | 0 |
| EX-10.01 | 1 | 0 |
| EX-10.1 | 81 | 0 |
| EX-10.2 | 1 | 0 |
| EX-10.3 | 1 | 0 |
| EX-2.1 | 21 | 0 |
| EX-3.1 | 3 | 0 |
| EX-4.1 | 18 | 0 |
| EX-4.11 | 1 | 0 |
| EX-4.2 | 1 | 0 |
| EX-5.1 | 3 | 0 |

8-K cover ("CURRENT REPORT" with "Section 13") within the first 5,000 characters of the body: old **1** of 154 read; new **185** of 185. Anywhere in the body: old 24, new 185.

#### Scan tally — `acqscan-v2` (pre-228 read) vs `acqscan-v3` (spec-228 read)

| outcome | acqscan-v2 | acqscan-v3 |
| --- | ---: | ---: |
| recognised | 1 | 1 |
| no-merger-agreement | 127 | 135 |
| company-not-target | 12 | 4 |
| company-is-acquirer | 13 | 45 |
| acquirer-not-named | 0 | 0 |
| no-stated-consideration | 1 | 0 |
| empty-body | 0 | 0 |
| verbatim-check-failed | 0 | 0 |
| (not scanned — read failed) | 31 | 0 |

#### Filings whose outcome changed — 71

| ticker | accession | filed | acqscan-v2 (old read) | acqscan-v3 (new read) |
| --- | --- | --- | --- | --- |
| SENEA | 0001437749-24-007174 | 2024-03-08 | (not scanned) | no-merger-agreement |
| IRMD | 0001558370-24-008847 | 2024-06-03 | (not scanned) | no-merger-agreement |
| AEHR | 0001654954-24-009008 | 2024-07-16 | no-merger-agreement | company-is-acquirer |
| SKWD | 0001519449-24-000039 | 2024-09-06 | no-merger-agreement | company-is-acquirer |
| CTO | 0001558370-24-013088 | 2024-09-30 | (not scanned) | no-merger-agreement |
| MHO | 0000799292-24-000088 | 2024-10-25 | no-merger-agreement | company-is-acquirer |
| KOP | 0001193125-24-280510 | 2024-12-17 | no-merger-agreement | company-is-acquirer |
| AMBA | 0001193125-24-285963 | 2024-12-27 | (not scanned) | company-is-acquirer |
| WTTR | 0001104659-25-006825 | 2025-01-29 | no-merger-agreement | company-is-acquirer |
| SKWD | 0001519449-25-000003 | 2025-02-05 | no-merger-agreement | company-is-acquirer |
| CALM | 0001562762-25-000030 | 2025-02-25 | (not scanned) | no-merger-agreement |
| MNRO | 0001193125-25-036829 | 2025-02-26 | (not scanned) | no-merger-agreement |
| OTTR | 0001466593-25-000054 | 2025-03-31 | no-merger-agreement | company-is-acquirer |
| MNRO | 0001193125-25-068897 | 2025-03-31 | (not scanned) | no-merger-agreement |
| PUMP | 0001104659-25-032851 | 2025-04-08 | no-merger-agreement | company-is-acquirer |
| SHEN | 0001171843-25-002268 | 2025-04-17 | no-merger-agreement | company-is-acquirer |
| FR | 0001193125-25-119870 | 2025-05-14 | no-merger-agreement | company-is-acquirer |
| MMSI | 0000856982-25-000035 | 2025-05-20 | (not scanned) | company-not-target |
| SHEN | 0001171843-25-003389 | 2025-05-22 | (not scanned) | no-merger-agreement |
| MNRO | 0001193125-25-135933 | 2025-06-05 | (not scanned) | no-merger-agreement |
| CCOI | 0001104659-25-060259 | 2025-06-17 | company-is-acquirer | no-merger-agreement |
| KOP | 0000950170-25-087681 | 2025-06-18 | no-merger-agreement | company-is-acquirer |
| LZB | 0000057131-25-000054 | 2025-07-02 | company-not-target | no-merger-agreement |
| UTL | 0001193125-25-158782 | 2025-07-14 | no-merger-agreement | company-is-acquirer |
| IMAX | 0001193125-25-159091 | 2025-07-15 | (not scanned) | no-merger-agreement |
| NWPX | 0001437749-25-027357 | 2025-08-19 | company-not-target | no-merger-agreement |
| DEA | 0001622194-25-000007 | 2025-08-21 | (not scanned) | no-merger-agreement |
| MNRO | 0001193125-25-186435 | 2025-08-22 | (not scanned) | no-merger-agreement |
| WDFC | 0000105132-25-000031 | 2025-09-02 | company-not-target | no-merger-agreement |
| KGS | 0001767042-25-000061 | 2025-09-05 | (not scanned) | no-merger-agreement |
| RGCO | 0001437749-25-028650 | 2025-09-09 | no-merger-agreement | company-is-acquirer |
| CTO | 0001104659-25-093346 | 2025-09-25 | (not scanned) | no-merger-agreement |
| ASIX | 0000950157-25-000887 | 2025-10-23 | company-not-target | no-merger-agreement |
| OOMA | 0001193125-25-262903 | 2025-11-03 | (not scanned) | company-is-acquirer |
| IMAX | 0001193125-25-270049 | 2025-11-06 | no-merger-agreement | company-is-acquirer |
| CTO | 0001104659-25-109229 | 2025-11-10 | no-merger-agreement | company-is-acquirer |
| MNRO | 0001193125-25-283220 | 2025-11-14 | (not scanned) | no-merger-agreement |
| CLFD | 0001171843-25-007385 | 2025-11-17 | no-merger-agreement | company-is-acquirer |
| OOMA | 0001193125-25-304183 | 2025-12-01 | (not scanned) | company-is-acquirer |
| UMH | 0001493152-25-025730 | 2025-12-02 | (not scanned) | no-merger-agreement |
| GTY | 0001193125-25-306965 | 2025-12-03 | (not scanned) | no-merger-agreement |
| SHEN | 0001171843-25-007857 | 2025-12-10 | no-merger-agreement | company-is-acquirer |
| DGII | 0000854775-25-000029 | 2025-12-30 | company-not-target | no-merger-agreement |
| GHM | 0001193125-26-021705 | 2026-01-26 | no-merger-agreement | company-is-acquirer |
| THRM | 0001193125-26-029374 | 2026-01-29 | company-not-target | company-is-acquirer |
| PUMP | 0001104659-26-012332 | 2026-02-10 | no-merger-agreement | company-is-acquirer |
| THRM | 0001193125-26-082690 | 2026-02-27 | no-stated-consideration | company-not-target |
| RGCO | 0001437749-26-009011 | 2026-03-19 | no-merger-agreement | company-is-acquirer |
| OTTR | 0001466593-26-000034 | 2026-03-23 | no-merger-agreement | company-is-acquirer |
| KGS | 0001767042-26-000022 | 2026-03-24 | company-is-acquirer | no-merger-agreement |
| RGCO | 0001437749-26-010853 | 2026-04-01 | no-merger-agreement | company-is-acquirer |
| STXS | 0001493152-26-016685 | 2026-04-15 | (not scanned) | no-merger-agreement |
| GHM | 0001193125-26-155977 | 2026-04-15 | no-merger-agreement | company-is-acquirer |
| PUMP | 0001680247-26-000058 | 2026-04-30 | (not scanned) | company-is-acquirer |
| UTL | 0000755001-26-000012 | 2026-05-05 | no-merger-agreement | company-is-acquirer |
| PUMP | 0001104659-26-057128 | 2026-05-07 | company-not-target | no-merger-agreement |
| LBRT | 0001694028-26-000027 | 2026-05-07 | (not scanned) | company-is-acquirer |
| EOSE | 0001628280-26-034367 | 2026-05-13 | (not scanned) | no-merger-agreement |
| CCOI | 0001104659-26-066279 | 2026-05-26 | no-merger-agreement | company-is-acquirer |
| OTTR | 0001466593-26-000057 | 2026-05-29 | (not scanned) | no-merger-agreement |
| RGCO | 0001437749-26-019526 | 2026-06-04 | no-merger-agreement | company-is-acquirer |
| OTTR | 0001466593-26-000063 | 2026-06-18 | (not scanned) | no-merger-agreement |
| LBRT | 0001694028-26-000034 | 2026-06-25 | (not scanned) | company-is-acquirer |
| EOSE | 0000950103-26-009739 | 2026-06-30 | (not scanned) | no-merger-agreement |
| HZO | 0001193125-26-290439 | 2026-06-30 | (not scanned) | no-merger-agreement |
| THRM | 0001193125-26-293500 | 2026-07-02 | company-not-target | no-merger-agreement |
| ASIX | 0000950157-26-000916 | 2026-08-17 | company-not-target | no-merger-agreement |
| EPM | 0001104659-26-098332 | 2026-08-18 | (not scanned) | no-merger-agreement |
| IDT | 0001437749-26-028388 | 2026-08-18 | no-merger-agreement | company-is-acquirer |
| DGII | 0001104659-26-103966 | 2026-08-31 | company-not-target | no-merger-agreement |
| MYRG | 0000700923-26-000047 | 2026-09-10 | (not scanned) | no-merger-agreement |

#### Every recognition under either — 1

- **MarineMax, Inc. (HZO)** · 0001193125-26-341302 · filed 2026-08-10
  - acqscan-v2: recognised · acquirer `SHM Holdco, LLC` · consideration `$53.00` (Cash) · quote: Each issued and outstanding share of Company Common Stock as of immediately prior to the Effective Time (other than the Excluded Shares, which shall be treated in accordance with S…
  - acqscan-v3: recognised · acquirer `SHM Holdco, LLC` · consideration `$53.00` (Cash) · quote: Merger Consideration On the terms and subject to the conditions set forth in the Merger Agreement, at the effective time of the Merger (the “Effective Time”), each share of Company…

SHOO 0001641172-25-008949: v2 no-merger-agreement, v3 **no-merger-agreement**.

#### The 31 formerly unreadable filings — v3 outcome, and whether any is RECOGNISED as a takeover of a Radar company under acqscan-v3

(Not recognised is NOT a checked "no takeover": the scan fails closed, and no-merger-agreement / company-not-target / company-is-acquirer say only that the rule did not recognise one. Whether a filing actually is a takeover is a human read of its text, recorded outside this report.)

- SENEA · 0001437749-24-007174 · filed 2024-03-08 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — FORM 8-K (2024-03-08) [items: 1.01] Items: Entry into a Material Definitive Agreemen…
- IRMD · 0001558370-24-008847 · filed 2024-06-03 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2024-06-03) [items: 1.01,9.01] Items: Entry into a Material Definitive Agreemen…
- CTO · 0001558370-24-013088 · filed 2024-09-30 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2024-09-30) [items: 1.01,2.03,7.01,9.01] Items: Entry into a Material Definitiv…
- AMBA · 0001193125-24-285963 · filed 2024-12-27 · old: no parseable document table · new read: success · v3: **company-is-acquirer** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2024-12-27) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- CALM · 0001562762-25-000030 · filed 2025-02-25 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-02-25) [items: 1.01,1.02,5.01,5.03,5.07,7.01,9.01] Items: Entry into a Mat…
- MNRO · 0001193125-25-036829 · filed 2025-02-26 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-02-26) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- MNRO · 0001193125-25-068897 · filed 2025-03-31 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-03-31) [items: 1.01,5.02,7.01,9.01] Items: Entry into a Material Definitiv…
- MMSI · 0000856982-25-000035 · filed 2025-05-20 · old: no primary document row · new read: success · v3: **company-not-target** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-05-20) [items: 1.01,2.02,7.01,9.01] Items: Entry into a Material Definitiv…
- SHEN · 0001171843-25-003389 · filed 2025-05-22 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — FORM 8-K (2025-05-22) [items: 1.01] Items: Entry into a Material Definitive Agreemen…
- MNRO · 0001193125-25-135933 · filed 2025-06-05 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-06-05) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- IMAX · 0001193125-25-159091 · filed 2025-07-15 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-07-15) [items: 1.01,2.03] Items: Entry into a Material Definitive Agreemen…
- DEA · 0001622194-25-000007 · filed 2025-08-21 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-08-21) [items: 1.01,2.03,7.01,9.01] Items: Entry into a Material Definitiv…
- MNRO · 0001193125-25-186435 · filed 2025-08-22 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-08-22) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- KGS · 0001767042-25-000061 · filed 2025-09-05 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-09-05) [items: 1.01,2.03,9.01] Items: Entry into a Material Definitive Agr…
- CTO · 0001104659-25-093346 · filed 2025-09-25 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-09-25) [items: 1.01,2.03,7.01,9.01] Items: Entry into a Material Definitiv…
- OOMA · 0001193125-25-262903 · filed 2025-11-03 · old: no primary document row · new read: success · v3: **company-is-acquirer** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-11-04) [items: 1.01,7.01] Items: Entry into a Material Definitive Agreemen…
- MNRO · 0001193125-25-283220 · filed 2025-11-14 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-11-14) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- OOMA · 0001193125-25-304183 · filed 2025-12-01 · old: no primary document row · new read: success · v3: **company-is-acquirer** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-12-02) [items: 1.01,2.03,8.01,9.01] Items: Entry into a Material Definitiv…
- UMH · 0001493152-25-025730 · filed 2025-12-02 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-12-02) [items: 1.01,2.03,7.01,9.01] Items: Entry into a Material Definitiv…
- GTY · 0001193125-25-306965 · filed 2025-12-03 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2025-12-03) [items: 1.01,2.03,7.01,9.01] Items: Entry into a Material Definitiv…
- STXS · 0001493152-26-016685 · filed 2026-04-15 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-04-15) [items: 1.01,3.02,7.01,9.01] Items: Entry into a Material Definitiv…
- PUMP · 0001680247-26-000058 · filed 2026-04-30 · old: no primary document row · new read: success · v3: **company-is-acquirer** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-04-30) [items: 1.01,2.02,7.01,9.01] Items: Entry into a Material Definitiv…
- LBRT · 0001694028-26-000027 · filed 2026-05-07 · old: no parseable document table · new read: success · v3: **company-is-acquirer** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-05-07) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- EOSE · 0001628280-26-034367 · filed 2026-05-13 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-05-13) [items: 1.01,2.02,3.02,7.01,9.01] Items: Entry into a Material Defi…
- OTTR · 0001466593-26-000057 · filed 2026-05-29 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-05-29) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- OTTR · 0001466593-26-000063 · filed 2026-06-18 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-06-18) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- LBRT · 0001694028-26-000034 · filed 2026-06-25 · old: no parseable document table · new read: success · v3: **company-is-acquirer** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-06-25) [items: 1.01] Items: Entry into a Material Definitive Agreement.
- EOSE · 0000950103-26-009739 · filed 2026-06-30 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — FORM 8-K (2026-06-30) [items: 1.01,8.01] Items: Entry into a Material Definitive Agr…
- HZO · 0001193125-26-290439 · filed 2026-06-30 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-06-30) [items: 1.01,1.02,2.03,7.01,9.01] Items: Entry into a Material Defi…
- EPM · 0001104659-26-098332 · filed 2026-08-18 · old: no primary document row · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — FORM 8-K (2026-08-18) [items: 1.01,7.01,9.01] Items: Entry into a Material Definitiv…
- MYRG · 0000700923-26-000047 · filed 2026-09-10 · old: no parseable document table · new read: success · v3: **no-merger-agreement** · recognised as a takeover under acqscan-v3? **no** · title: 8-K — 8-K (2026-09-10) [items: 1.01,2.03,9.01] Items: Entry into a Material Definitive Agr…

Recognised as a takeover of a Radar company under acqscan-v3 among them: **0** (a count of recognitions, not a checked absence of takeovers); among them with a merger-agreement phrase present but not recognised (company-not-target / acquirer-not-named / no-stated-consideration): 1.

#### Request cost — `www.sec.gov` requests per filing issued by the new reader

| requests | filings |
| ---: | ---: |
| 2 | 93 |
| 3 | 77 |
| 4 | 15 |

Total 477 over 185 filings (mean 2.58); 0 of them went over the wire this run. (Index → primary → EX-99.1 when shown → EX-2.1 when shown: at most 4; a filing that fails before a document fetch costs 1.) Filings with EX-99.1 and EX-2.1 both: 15.

#### The append decision — what the EX-2.1 adds, and what it costs

Index rows by Type across the 185 filings: EX-2.1 in 21 filing(s) (any EX-2.* in 22); EX-10.1 in 98 (any EX-10.* in 103); EX-99.1 in 84. Distinct .htm row types: 8-K 185, EX-10.1 98, EX-99.1 85, EX-10.2 30, EX-4.1 23, EX-5.1 22, EX-1.1 21, EX-2.1 21, EX-99.2 21, EX-10.3 9, EX-4.2 9, EX-99.3 6, EX-10.4 5, EX-4.3 5, EX-2.2 4, EX-10.5 3, EX-10.6 3, EX-3.1 3, EX-99.4 3, EX-10.10 2, EX-10.11 2, EX-10.12 2, EX-10.7 2, EX-10.8 2, EX-10.9 2, EX-3.2 2, EX-4.4 2, EX-4.5 2, EX-5.2 2, EX-8.1 2, EX-99 2, EX-1.2 1, EX-1.3 1, EX-10 1, EX-10.01 1, EX-4.11 1.

The shipped read appends the EX-2.1 (primary → EX-99.1 → EX-2.1). Counterfactual: the same body WITHOUT it (primary → EX-99.1), rebuilt from the same responses; the shipped body equals that rebuild plus the agreement exactly for 185 of 185 successful reads (0 mismatch, 0 not rebuilt). For each of the 21 filing(s) whose index carries an EX-2.1:

| ticker | accession | filed | without EX-2.1 (counterfactual) | with EX-2.1 (shipped v3) |
| --- | --- | --- | --- | --- |
| SHOO | 0001493152-25-007575 | 2025-02-19 | no-merger-agreement | no-merger-agreement |
| CYRX | 0001104659-25-029642 | 2025-03-31 | no-merger-agreement | no-merger-agreement |
| UTL | 0001193125-25-073493 | 2025-04-04 | company-is-acquirer | company-is-acquirer |
| UTL | 0001193125-25-117855 | 2025-05-12 | company-is-acquirer | company-is-acquirer |
| STRL | 0001193125-25-142774 | 2025-06-18 | no-merger-agreement | company-not-target ⚠ differs |
| PLUS | 0001022408-25-000049 | 2025-06-23 | no-merger-agreement | no-merger-agreement |
| SKWD | 0001519449-25-000050 | 2025-09-08 | company-is-acquirer | company-is-acquirer |
| WTRG | 0001552781-25-000341 | 2025-10-27 | company-is-acquirer | company-is-acquirer |
| PLMR | 0001193125-25-258571 | 2025-10-30 | company-is-acquirer | company-is-acquirer |
| CLFD | 0001171843-25-007385 | 2025-11-17 | company-is-acquirer | company-is-acquirer |
| CMCO | 0001193125-26-012326 | 2026-01-14 | no-merger-agreement | no-merger-agreement |
| THRM | 0001193125-26-029374 | 2026-01-29 | company-is-acquirer | company-is-acquirer |
| UTL | 0001193125-26-029469 | 2026-01-29 | no-merger-agreement | no-merger-agreement |
| KGS | 0001193125-26-039600 | 2026-02-05 | company-is-acquirer | company-is-acquirer |
| CLMB | 0001437749-26-005335 | 2026-02-24 | no-merger-agreement | company-not-target ⚠ differs |
| UTL | 0001193125-26-067209 | 2026-02-24 | no-merger-agreement | no-merger-agreement |
| ESQ | 0001104659-26-026781 | 2026-03-12 | company-not-target | company-is-acquirer ⚠ differs |
| UTL | 0000755001-26-000019 | 2026-05-27 | no-merger-agreement | no-merger-agreement |
| NOVT | 0001193125-26-262867 | 2026-06-09 | company-is-acquirer | company-is-acquirer |
| UTL | 0000755001-26-000026 | 2026-07-07 | no-merger-agreement | no-merger-agreement |
| HZO | 0001193125-26-341302 | 2026-08-10 | acquirer-not-named | recognised ⚠ differs |

Filings whose outcome the EX-2.1 append changes: **4** (into a recognition: 1; out of one: 0).
- STRL · 0001193125-25-142774: without EX-2.1 no-merger-agreement → with EX-2.1 (shipped) company-not-target
- CLMB · 0001437749-26-005335: without EX-2.1 no-merger-agreement → with EX-2.1 (shipped) company-not-target
- ESQ · 0001104659-26-026781: without EX-2.1 company-not-target → with EX-2.1 (shipped) company-is-acquirer
- HZO · 0001193125-26-341302: without EX-2.1 acquirer-not-named → with EX-2.1 (shipped) recognised · acquirer `SHM Holdco, LLC` · consideration `$53.00` (Cash) · quote: Merger Consideration On the terms and subject to the conditions set forth in the Merger Agreement, at the effective time of the Merger (the “Effective Time”), each share of Company…

#### No-write confirmation

Every file under the data root was snapshotted (path, length, last-write UTC) before and after the harness: 190922 file(s) before, 190922 after, **0 changed**.
