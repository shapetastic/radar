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
