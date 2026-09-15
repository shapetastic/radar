# Task: the acquisition scan says WHY truthfully — the "company is the acquirer" label fires on note purchases, and four proximity rules can miss or mislabel a real deal

## Overview

Spec 228 made the item-1.01 read fetch the real 8-K: 185 of 185 bodies now start with the 8-K cover. It also left
one known defect: the `company-is-acquirer` outcome **rose from 13 to 45**. Spec 228 §4 had placed that rule work
out of scope. Two earlier reviews (227 and 228) listed more weak spots in the same file,
`src/Radar.Application/Acquisitions/AcquisitionAgreementScan.cs`. This spec fixes them together.

`acqscan-v3` recognises exactly one takeover on the live store (HZO, $53.00), and nothing here is expected to
change that. The point is that **every not-recognised outcome is a stated reason** — spec 217 made the tally split
by failed leg so a reader can trust *why* — and several reasons are currently false. One rule can also hide a real
takeover.

### 1. `company-is-acquirer` wins even when there is no merger at all

In `Scan`, when no target sentence is found:

```csharp
sawAcquirerSide ? AcquisitionScanOutcome.CompanyIsAcquirer
: hasExplicitMergerPhrase ? AcquisitionScanOutcome.CompanyNotTarget
: AcquisitionScanOutcome.NoMergerAgreement
```

The acquirer label outranks "this is not a merger filing". Part 1 of leg (a) passes on the bare phrase
"definitive agreement", and since spec 228 **every** body contains it, because every item-1.01 8-K carries the
heading "Item 1.01 Entry into a Material Definitive Agreement". So any sentence with the company's name near
`subsidiary of` or `to acquire` now labels an ordinary contract as an acquisition.

Checked on spec 228's cached documents (`%TEMP%\radar-spec228-sec-documents-final`):

| filing | merger phrases | triggering sentence | truth |
|---|---:|---|---|
| OTTR `0001466593-25-000054` (v2 `no-merger-agreement` → v3 `company-is-acquirer`) | 0 | "Otter Tail Power Company (the "Company"), a wholly owned **subsidiary of Otter Tail Corporation**, entered into a Note Purchase Agreement" | a **debt issue** — not an acquisition by anyone |
| AEHR `0001654954-24-009008` (same flip) | 0 | "the Company **agreed to acquire** … all of the outstanding capital stock of Incal" | a **real acquisition by AEHR** — the label is right |

So the 32 new labels are a mixture of true and false, and nobody knows the split. The `subsidiary of {company}`
veto is a **merger-structure** test: "Merger Sub, a wholly owned subsidiary of {acquirer}". Outside a merger it
describes corporate structure, not an acquisition.

### 2. The acquirer veto is proximity, not grammar, and can hide a real takeover

`IsAcquirerSide` fires when a company mention appears **before** `to acquire` / `will acquire` / `agreed to acquire`
within `ObjectProximity = 90` characters. A common target-side sentence has exactly that shape. The example below is
constructed to show the pattern; it is not quoted from HZO's filing, which v3 recognises:

> "On August 10, 2026, **MarineMax, Inc.** entered into an Agreement and Plan of Merger with Parent, pursuant to which
> Parent **agreed to acquire** the Company…"

"MarineMax, Inc." comes before "agreed to acquire", with Parent as the subject in between, so the sentence is
vetoed as acquirer-side and **skipped as a target sentence**. If it is the filing's only target sentence, a real
takeover goes unrecognised. That is the failure spec 217 calls strictly worse than a false positive's opposite,
because it keeps a closed thesis open. (⚠ AMENDED in place by the §2 measurement: for THIS sentence the veto does not
fire under `acqscan-v3`, because "MarineMax" sits 99 characters before "agreed to acquire", beyond the 90-character
proximity. v3 still misses it, but because no target form reads "Parent agreed to acquire the Company". The veto
described here fires on a shorter sentence of the same shape. `AcqScanV3HistoricalControlFixtureTests` pins both, and
`acqscan-v4` recognises both.)

### 3. Three smaller rules from the 227/228 reviews

- **"Completion of the acquisition of" does not check whose acquisition.** The v2 acquirer vetoes do not ask whether
  the filer governs the phrase ("Parent's completion of the acquisition of the Company" vs "the Company's completion
  of the acquisition of X").
- **Title-case "By".** `AcquiredByRegex` is case-sensitive (`acquired\s+by`), while target detection lowercases. A
  title-case headline such as "MarineMax … to be Acquired By Blackstone Infrastructure Portfolio Company, Safe
  Harbor" recognises the target but cannot name the acquirer. Spec 228 had to append EX-2.1 to reach HZO's
  acquirer; it noted that fixing this "would also let the EX-2.1 be dropped". (⚠ AMENDED in place by the §2
  measurement: HZO's live headline reads "to be Acquired by", with a capital A and a lowercase "by". Rule 4 makes it
  match, but the capture still cannot end the name before its 120-character limit, so HZO without the EX-2.1 is still
  `acquirer-not-named`. The EX-2.1 was KEPT; see the appendix.)
- **A nearby "dividend" excludes a real price.** Spec 227's consideration exclusions look back a fixed distance, so
  "…the regular quarterly dividend will be suspended. Holders will receive $53.00 per share in cash" can exclude
  the real consideration. (⚠ AMENDED in place by the §2 measurement: in that two-sentence form v3 did NOT exclude it,
  because its look-back never crossed a sentence boundary. v3 excludes the amount when "dividend" sits in the SAME
  clause without governing it, e.g. "…dividend is suspended and holders will receive $53.00 per share…". Both forms are
  pinned in `AcqScanV3HistoricalControlFixtureTests`.)

## Assignment

Worktree: any. Dependencies: main at `4761b77` or later (spec 228 merged).

Use `run-next.ps1 -Spec 229`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Rules (`acqscan-v4`)

Bump `AcquisitionAgreementScan.Version` to `acqscan-v4`. Keep fail-closed; keep every named outcome.

1. **The outcome precedence is honest.**
   - With no explicit merger phrase ("agreement and plan of merger" / "merger agreement" / "plan of merger"), and
     no "definitive agreement … to be acquired" target sentence, the answer is `NoMergerAgreement`, whatever an
     acquirer-side sentence said.
   - `CompanyIsAcquirer` requires acquisition context: a merger phrase, or the filer governing an acquisition verb
     over a named object ("the Company agreed to acquire … Incal").
   - The `subsidiary of {company}` veto applies **only** inside a merger-phrase filing.
   - Justify the final precedence in the PR against the OTTR and AEHR bodies.
2. **The acquirer veto binds to the verb's SUBJECT.** A company mention before `to acquire` does not veto when another
   party is the subject of that verb in between: a defined `Parent` / `Purchaser` / `Buyer` / `Acquiror`, or a
   `pursuant to which {X}` clause. The MarineMax sentence above must be a **target** sentence.
   - Keep the rule deterministic and simple; no parser.
   - A mention that is itself inside "pursuant to which {company} agreed to acquire" still vetoes.
3. **Governed "completion of the acquisition of".** It vetoes only when the filer governs it: "the Company's",
   "{company} completed / announced completion of". It does not veto when another party does, or when the company is
   its object.
4. **Case-insensitive acquirer extraction.** `AcquiredByRegex` and its siblings match "Acquired By" and "ACQUIRED BY",
   while the captured name keeps its original case.
5. **Consideration exclusions are sentence-scoped.**
   - An exclusion word (dividend, par value, exercise price, conversion price, offering) disqualifies an amount only
     when it governs **that** amount in the same clause.
   - A per-share amount in a different sentence from "dividend" is not excluded.
   - The named exclusions stay data in one place.
6. **EX-2.1 append — decide with data.**
   - With rule 4 in place, measure whether HZO is recognised with its real acquirer from the 8-K + EX-99.1 alone.
   - If so, and dropping EX-2.1 changes no other recognition, drop it: fewer requests, and the merger agreement's
     boilerplate cannot drive the scan.
   - Otherwise keep it and state why.
   - Either way, report the per-filing request cost.

**Tests.** Pin each rule with a real passage where one exists, using spec 228's cached documents trimmed with
provenance noted, otherwise a minimal constructed sentence:
- OTTR note purchase → `NoMergerAgreement`;
- AEHR buying Incal → `CompanyIsAcquirer`;
- the MarineMax "pursuant to which Parent agreed to acquire the Company" sentence → target;
- a title-case "Acquired By" headline naming the acquirer;
- a "dividend" sentence followed by a separate consideration sentence → the consideration is kept;
- a governed and an ungoverned "completion of the acquisition of".

**Shared test helper.** `OutsideRoot` in `AcquisitionReadPrimary8KLiveMeasurementTests.cs` was fixed after
Copilot's review on #236; `AcquisitionScanV2LiveMeasurementTests.cs:115` still has the text-prefix bug.
- Extract ONE helper and route both harnesses, and this spec's harness, through it.
- Do not paste a third copy.

## 2. Live measurement (required — produce it, do not report it as owed)

Use the read-only harness pattern:
- env-gated;
- the main store at `C:\Users\scm9d\source\repos\radar\data`;
- writes throw;
- per-process temp report;
- a no-write confirmation afterwards.

**Prefer spec 228's document cache** (`%TEMP%\radar-spec228-sec-documents-final`) as the body source, so the 185
filings are scored without re-fetching. Fetch paced from SEC only what the cache lacks: a real user agent, a 200
check on `www.sec.gov` first, the existing throttle.

Report:
- **The tally, `acqscan-v3` vs `acqscan-v4`,** with every filing whose outcome changed named: ticker, accession, both
  outcomes, and the sentence that decided each.
- **A hand-checked truth table for all 45 v3 `company-is-acquirer` filings,** plus any new v4 ones. For each:
  is the filer genuinely acquiring something (yes / no), and does v4's label agree? Report v3 and v4 label
  precision. This is the measurement the label never had.
- **Every recognition under v4,** with consideration and acquirer, with and without EX-2.1:
  - HZO 2026-08-10 must still be recognised at `53.00`;
  - SHOO must still not be.
- **Recall probe.** List every filing with a merger phrase that v4 does not recognise, with its outcome and deciding
  sentence, so a human can see at a glance whether any is a missed takeover of a Radar company.

## 3. Identity and timing

`acq=` is hashed unconditionally, so both pin families move and one operator step is owed after merge. Update
`ScoringConfigFingerprintTests` with lineage comments and state the step in the PR body.

**Timing note for the maintainer, not a blocker.**
- If §2 shows **no recognition changes** (HZO in, SHOO out, nothing new), this slice changes no score, only the
  stated reasons and the identity. Merging it **after 2026-09-29** then avoids starting another comparability cohort
  for no scoring gain.
- If §2 finds a recognition change, say so prominently: that is a reason to merge before the claim date.

## 4. Not in scope

- **Deal lifecycle**: completion via item 2.01 or delisting, termination via item 1.02, and expiry of a pending
  recognition.
- **Using the acquirer label anywhere downstream.** Nothing reads it today, and this spec keeps it that way.
- **An AI read of merger filings.**

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. Each of rules 1–5 is pinned by a test built on a real passage where one exists. The MarineMax
   "pursuant to which Parent agreed to acquire the Company" sentence is recognised as target-side.
3. The EX-2.1 decision is made from §2's data and stated.
4. §2 is produced from the live store with the no-write confirmation. It includes the 45-filing truth table with v3
   and v4 label precision, and HZO at 53.00 / SHOO not recognised.
5. One shared path helper serves every acquisition live harness.
6. `acqscan-v4`; pins updated with lineage comments; one operator step stated.
7. Doc claims this slice touches are amended in place (REVERSAL rule), including the outcome descriptions in
   `AcquisitionScanOutcome` and CLAUDE.md's pending-acquisition bullet where it describes the vetoes.

## Appendix — §2 live measurement (MEASURED 2026-09-15)

**How it was produced.** `tests/Radar.IntegrationTests/AcquisitionScanV4LiveMeasurementTests.cs`, against the main
store (`RADAR_ACQSCAN_V4_LIVE_DATA_ROOT` = `C:\Users\scm9d\source\repos\radar\data`), on 2026-09-15:

- **Bodies.** The production `HttpSecAcquisitionFilingReader` (v4's read) and the harness's rebuilds (v3's read, and
  v4 with and without the EX-2.1) went through a counting cache handler. `RADAR_ACQSCAN_V4_SEED_CACHE_DIR` pointed at
  spec 228's document cache (`%TEMP%\radar-spec228-sec-documents-final`, read-only). `RADAR_ACQSCAN_V4_DOCUMENT_CACHE_DIR`
  (`%TEMP%\radar-spec229-sec-documents`) would have kept anything fetched. `RADAR_SEC_UA` was set and `www.sec.gov`
  answered 200 to a curl check and to the harness's own probe. **The seed cache held every URL: 954 Archives requests,
  0 over the wire.** The store held the same 190 item-1.01 evidence items (185 accessions) spec 228 measured.
- **v3 column.** The frozen `AcqScanV3HistoricalControl` (a verbatim copy of `acqscan-v3` at `b6ba462`, plus
  instrumentation that records the deciding clause and changes no rule) over the spec-228 read. It reproduces spec 228's
  recorded v3 tally exactly (1 / 135 / 4 / 45 / 0 / 0 / 0 / 0), which checks the control.
- **Read-only.** The composition's store / cache / evidence writes throw. All 190,922 files under the data root were
  compared by path, length and last-write time before and after: **0 changed**.
- **What was run more than once.** The harness first ran with production still at v3 (to produce the per-filing Item 1.01
  narratives file, kept in the temp directory, that the hand check read), then with rules 1–5 in place. The report below is the final run, taken after the last
  rule change; it is byte-identical to the second run's report. (Review iteration 1 then added two fail-closed
  guards to rule 2. A role the filer is defined as anywhere in the clause is the filer. And "the Company" is the filer
  only when every (the “Company”) definition in the body directly follows a filer mention; iteration 2 tightened that
  to: at most 60 characters after it, with nothing between but whitespace or an entity apposition such as ", Inc., a
  Florida corporation"; anything else, or ambiguous ownership, is another party. The iterations also added a
  sentence-boundary refusal for acquirer names and a defined-term parenthetical as an exclusion connector. The harness
  was re-run after each iteration against the same cache: 954 requests, 0 live, 190,922 files, 0 changed, HZO at 53.00,
  SHOO not recognised, and a report byte-identical to the one below. No number in this appendix moved.)

### Headline

- **No recognition changed.** HZO `0001193125-26-341302` is recognised under v3 and v4 alike: acquirer `SHM Holdco,
  LLC`, consideration `$53.00` (Cash), with the same target and consideration clauses. SHOO `0001641172-25-008949` is
  `no-merger-agreement` under both. No other filing is recognised under v3, v4, v4 with the EX-2.1 or v4 without it.
  Per §3's timing note, this slice changes no score, only the stated reasons and the identity.
- **31 of 185 reasons changed**, every one a v3 `company-is-acquirer`: 30 became `no-merger-agreement` and 1 (WTRG)
  became `company-not-target`. Nothing else moved: rules 2 (target form), 3 (governed completion) and 5 (exclusions)
  changed no live outcome, and rule 4 changed no live acquirer name.
- **`company-is-acquirer` precision: v3 15 of 45 (33%); v4 14 of 14 (100%)**, hand-checked below. The one true v3
  label v4 drops is GHM `0001193125-26-021705`. Its Item 1.01 is a credit amendment, and the FlackTek acquisition is
  announced as "GRAHAM CORPORATION ACQUIRES FLACKTEK" / "today announced the acquisition of". Neither "acquires" nor
  "announced the acquisition of" is an acquisition verb the rule reads. v4 calls it `no-merger-agreement`, which is
  literally true: the filing has no merger agreement.
- **Rule 6, EX-2.1: KEPT.** With rule 4 in place, HZO without the EX-2.1 is still `acquirer-not-named`. The EX-99.1
  headline is the target clause, and its title-case "Acquired by" now matches. But the name after it, "Blackstone
  Infrastructure Portfolio Company, Safe Harbor, in a $1.5 Billion All-Cash Transaction MarineMax Shareholders to
  Receive $53.00 …", contains no terminator the capture accepts (`, a|an|the `, `. `, `;`, `(`, end of clause) within its
  120 characters, so the capture fails. The 8-K's own party list opens with "the Company", which the rule refuses. So
  the spec's projection that rule 4 "would also let the EX-2.1 be dropped" is **false on the live filing**, and the read
  is unchanged. Request cost: **477 requests over 185 filings with the append (2 requests 93 filings, 3 requests 77,
  4 requests 15; mean 2.58), 456 without (mean 2.46)**. The append costs 21 requests, one per EX-2.1 filing. Under v4
  it changes the same four outcomes it changed under v3: HZO into its recognition; STRL and CLMB `no-merger-agreement` →
  `company-not-target`; ESQ `company-not-target` → `company-is-acquirer`. For ESQ the append makes the reason truer,
  because ESQ is buying Signature. For STRL and CLMB it makes the reason less informative: the merger phrase comes only
  from their purchase agreements' boilerplate.
- **Recall probe: 9 merger-phrase filings v4 does not recognise; one is a MISSED TAKEOVER of a Radar company.**
  Essential Utilities (WTRG) `0001552781-25-000341`, filed 2025-10-27, is the TARGET of American Water Works' all-stock
  merger (0.305 AWK shares per WTRG share). v3 called it `company-is-acquirer`. v4 calls it `company-not-target`. The
  scan cannot recognise it: the filing names the target "Essential" ("Merger Sub will merge with and into Essential"),
  which is not a seeded mention, and states the consideration as "the right to receive 0.305 shares (the “Exchange
  Ratio”)", which the exchange-ratio form ("exchange ratio of X") does not read. **Not fixed here** (§4 scope; a
  recognition would change scores). Under `observation-eligibility-v2` / `excess-vs-universe-v2`, WTRG has therefore been
  treated as an ordinary live thesis since 2025-10-27. The effect on the benchmark and on the 2026-09-29 claim is
  UNMEASURED. A stock deal tracks the acquirer's price rather than pinning at a cash bid, so the size of that effect
  should not be assumed to be MarineMax's. This is a follow-up for the maintainer.
- **A claim in this spec's Overview §2 is corrected.** The exact MarineMax sentence was not vetoed under v3. "MarineMax"
  sits 99 characters before "agreed to acquire", beyond v3's 90-character proximity, so v3 missed it for a different
  reason: no target form read "Parent agreed to acquire the Company". The veto the spec describes fires on a shorter
  sentence of the same shape (80 characters). `AcqScanV3HistoricalControlFixtureTests` pins both, and v4 recognises both.

### Hand-checked truth table — every filing labelled `company-is-acquirer` under either version (45; v4 adds none)

**What "genuinely acquiring something" means here:** the filer, or a subsidiary acting for it, agrees to acquire, or
completes the acquisition of, a business, a company, or an ownership interest in one, **anywhere in the filing** (the
8-K or its EX-99.1). Buying equipment, issuing notes, leasing space or selling a business is **no**. Each row was judged
by reading the Item 1.01 narrative of the primary 8-K (the harness's narratives file, from the same bodies) and, where
the deciding clause came from elsewhere, that clause and its surrounding release. v3 decided every row on the clause in
the spec-229 report table below. Where v4 is `no-merger-agreement` no clause decided it: an absence did.

| # | ticker | accession | what the filing actually is | filer acquiring? | v3 label right? | v4 outcome | v4 right? |
| ---: | --- | --- | --- | --- | --- | --- | --- |
| 1 | AEHR | 0001654954-24-009008 | stock purchase agreement to acquire Incal Technology | **yes** | yes | company-is-acquirer ("Aehr Test Systems to Acquire Incal Technology") | yes |
| 2 | SKWD | 0001519449-24-000039 | FHLB loan to its insurance subsidiary | no | no | no-merger-agreement | yes |
| 3 | MHO | 0000799292-24-000088 | mortgage repurchase facility amendment (M/I Financial) | no | no | no-merger-agreement | yes |
| 4 | KOP | 0001193125-24-280510 | credit agreement Amendment No. 5 (Koppers Inc.) | no | no | no-merger-agreement | yes |
| 5 | AMBA | 0001193125-24-285963 | office lease for new headquarters | no | no | no-merger-agreement | yes |
| 6 | WTTR | 0001104659-25-006825 | sustainability-linked credit facility | no | no | no-merger-agreement | yes |
| 7 | SKWD | 0001519449-25-000003 | commutation of a loss portfolio transfer agreement | no | no | no-merger-agreement | yes |
| 8 | OTTR | 0001466593-25-000054 | note purchase agreement (Otter Tail Power senior notes) | no | no | no-merger-agreement | yes |
| 9 | UTL | 0001193125-25-073493 | stock purchase agreement to acquire Maine Natural Gas | **yes** | yes | company-is-acquirer (release: "definitive agreement to acquire Maine Natural Gas Company") | yes |
| 10 | PUMP | 0001104659-25-032851 | master loan and security agreement with Caterpillar Financial | no | no | no-merger-agreement | yes |
| 11 | SHEN | 0001171843-25-002268 | credit agreement Amendment No. 4 | no | no | no-merger-agreement | yes |
| 12 | UTL | 0001193125-25-117855 | definitive agreement to acquire the Aquarion water companies | **yes** | yes | company-is-acquirer (release) | yes |
| 13 | FR | 0001193125-25-119870 | $450M senior notes offering (First Industrial, L.P.) | no | no | no-merger-agreement | yes |
| 14 | KOP | 0000950170-25-087681 | credit agreement Amendment No. 6 | no | no | no-merger-agreement | yes |
| 15 | UTL | 0001193125-25-158782 | note purchase agreement (Bangor Natural Gas) | no | no | no-merger-agreement | yes |
| 16 | SKWD | 0001519449-25-000050 | share purchase agreements to acquire Apollo Group Holdings | **yes** | yes | company-is-acquirer ("SKYWARD SPECIALTY INSURANCE GROUP TO ACQUIRE APOLLO GROUP HOLDINGS") | yes |
| 17 | RGCO | 0001437749-25-028650 | RGC Midstream credit agreement | no | no | no-merger-agreement | yes |
| 18 | WTRG | 0001552781-25-000341 | Agreement and Plan of Merger under which American Water acquires Essential Utilities, so the filer is the TARGET | no | no | company-not-target | yes as a reason; the takeover is missed (see headline) |
| 19 | PLMR | 0001193125-25-258571 | equity purchase agreement: its subsidiary buys The Gray Casualty & Surety Company | **yes** | yes | company-is-acquirer ("Palomar Holdings, Inc. Announces Agreement to Acquire The Gray Casualty & Surety Company") | yes |
| 20 | OOMA | 0001193125-25-262903 | stock purchase agreement to acquire FluentStream | **yes** | yes | company-is-acquirer ("Ooma Announces Definitive Agreement to Acquire FluentStream") | yes |
| 21 | IMAX | 0001193125-25-270049 | convertible senior notes offering and capped calls | no | no | no-merger-agreement | yes |
| 22 | CTO | 0001104659-25-109229 | management-fee waiver letter (Alpine Income Property manager) | no | no | no-merger-agreement | yes |
| 23 | CLFD | 0001171843-25-007385 | SALE of its Finnish subsidiary (a divestiture) | no | no | no-merger-agreement | yes |
| 24 | OOMA | 0001193125-25-304183 | credit amendment at the CLOSING of its FluentStream acquisition | **yes** | yes | company-is-acquirer ("Ooma Completes Acquisition of FluentStream") | yes |
| 25 | SHEN | 0001171843-25-007857 | fiber-network securitization notes closing | no | no | no-merger-agreement | yes |
| 26 | GHM | 0001193125-26-021705 | credit amendment, plus the announced acquisition of FlackTek (Item 7.01 / EX-99.1) | **yes** | yes | no-merger-agreement | **no** (a true acquirer label lost; the stated reason is literally true) |
| 27 | THRM | 0001193125-26-029374 | Reverse Morris Trust: Gentherm's Merger Sub merges into Modine's Performance Technologies SpinCo | **yes** | yes | company-is-acquirer ("… Platinum Gold Merger Sub Inc., … a wholly owned subsidiary of Gentherm") | yes |
| 28 | PLMR | 0001193125-26-033708 | closing of The Gray Casualty & Surety acquisition, plus a $450M credit facility | **yes** | yes | company-is-acquirer ("Palomar Holdings, Inc. Completes Acquisition of The Gray Casualty & Surety Company …") | yes |
| 29 | CMCO | 0001193125-26-037694 | closing of the Kito Crosby acquisition (credit agreement, indenture, preferred issuance) | **yes** | yes | company-is-acquirer ("Columbus McKinnon Completes Acquisition of Kito Crosby") | yes |
| 30 | KGS | 0001193125-26-039600 | agreement to acquire Distributed Power Solutions | **yes** | yes | company-is-acquirer ("Kodiak Gas Services to Acquire Distributed Power Solutions") | yes |
| 31 | PUMP | 0001104659-26-012332 | amendment to the Caterpillar Financial equipment loan | no | no | no-merger-agreement | yes |
| 32 | ESQ | 0001104659-26-026781 | Agreement and Plan of Merger to acquire Signature Bancorporation | **yes** | yes | company-is-acquirer ("Esquire Bank is the wholly owned subsidiary of Esquire Financial Holdings, Inc.", in EX-2.1) | yes |
| 33 | RGCO | 0001437749-26-009011 | Roanoke Gas loan agreement amendment | no | no | no-merger-agreement | yes |
| 34 | OTTR | 0001466593-26-000034 | note purchase agreement (Otter Tail Power) | no | no | no-merger-agreement | yes |
| 35 | RGCO | 0001437749-26-010853 | private shelf agreement amendment | no | no | no-merger-agreement | yes |
| 36 | GHM | 0001193125-26-155977 | PIPE: T. Rowe Price accounts buy 5% of Graham's shares | no | no | no-merger-agreement | yes |
| 37 | PUMP | 0001680247-26-000058 | global framework agreement to purchase Caterpillar power-generation equipment | no | no | no-merger-agreement | yes |
| 38 | UTL | 0000755001-26-000012 | note purchase agreement (Fitchburg Gas and Electric) | no | no | no-merger-agreement | yes |
| 39 | LBRT | 0001694028-26-000027 | two supply contracts for Bergen power-generation equipment | no | no | no-merger-agreement | yes |
| 40 | CCOI | 0001104659-26-066279 | SALE of 10 data centers to an I Squared affiliate | no | no | no-merger-agreement | yes |
| 41 | RGCO | 0001437749-26-019526 | delayed-draw promissory note (Roanoke Gas) | no | no | no-merger-agreement | yes |
| 42 | NOVT | 0001193125-26-262867 | equity purchase agreement to acquire Riverpoint Medical's parent | **yes** | yes | company-is-acquirer ("Novanta will acquire all outstanding equity interests of the parent company of Riverpoint Medical …") | yes |
| 43 | LBRT | 0001694028-26-000034 | supply contract for Wärtsilä power-generation equipment | no | no | no-merger-agreement | yes |
| 44 | IDT | 0001437749-26-028388 | revolving credit agreement amendment (IDT Telecom) | no | no | no-merger-agreement | yes |
| 45 | AXGN | 0001628280-26-061208 | Agreement and Plan of Merger to acquire BioCircuit Technologies | **yes** | yes | company-is-acquirer ("Axogen Enters into Definitive Agreement to Acquire BioCircuit Technologies") | yes |

**Tally.** The filer is genuinely acquiring something in **15** of the 45 filings. **v3 precision 15 / 45 = 33.3%**:
every v3 label on a "yes" row was right and every one on a "no" row was wrong. **v4 labels 14 filings, all "yes": precision
14 / 14 = 100%.** v4 keeps 14 of the 15 true v3 labels (GHM `0001193125-26-021705` lost) and removes all 30 false ones.
v4 adds no `company-is-acquirer` filing that v3 did not have. On the "no" rows, v4's replacement reason is true in 29 of
30 as stated: the filing carries no merger agreement. The 30th is WTRG, whose `company-not-target` is true as a
statement of what the scan could read, but hides a real takeover. THRM's RMT is marked "yes" because Gentherm's merger
sub acquires the SpinCo business. That reads the legal structure; who ends up owning the combined company is not
judged here.

### Recall probe, hand-checked — the 9 merger-phrase filings v4 does not recognise

| ticker | accession | v4 outcome | what it is | a takeover of the Radar company? |
| --- | --- | --- | --- | --- |
| UTL | 0001193125-25-073493 | company-is-acquirer | Unitil buying Maine Natural Gas; the phrase is in its EX-2.1 | no, UTL is the buyer |
| MMSI | 0000856982-25-000035 | company-not-target | Merit Medical acquiring Biolife by merger | no, MMSI is the buyer |
| STRL | 0001193125-25-142774 | company-not-target | Sterling's subsidiary buying CEC Facilities Group's assets; "plan of merger" is a covenant in the EX-2.1 | no, STRL is the buyer |
| WTRG | 0001552781-25-000341 | company-not-target | American Water Works acquiring Essential Utilities in an all-stock merger | **YES, MISSED** |
| THRM | 0001193125-26-029374 | company-is-acquirer | Gentherm's Reverse Morris Trust with Modine's SpinCo | no, Gentherm's merger sub is the acquiring vehicle |
| CLMB | 0001437749-26-005335 | company-not-target | Climb buying Interworks; "demerger agreement" is a definition in the EX-2.1 | no, CLMB is the buyer |
| THRM | 0001193125-26-082690 | company-not-target | credit amendment permitting the Modine transactions | no |
| ESQ | 0001104659-26-026781 | company-is-acquirer | Esquire acquiring Signature Bancorporation | no, ESQ is the buyer |
| AXGN | 0001628280-26-061208 | company-is-acquirer | Axogen acquiring BioCircuit Technologies | no, AXGN is the buyer |

This probe covers only filings that carry an explicit merger phrase. A takeover worded without one, such as a tender
offer or a scheme of arrangement, is outside it. That remainder is **UNMEASURED** by this probe. Spec 227's recall
check over all evidence text ("to be acquired by", "take-private", …) is the complementary search, and on 2026-09-14 it
found only HZO. It did not find WTRG, because the WTRG filings say "to merge" / "to combine".

### Rule effects on the live population, per rule (from the report below)

- **Rule 1** accounts for 30 of the 31 changes, all without an explicit merger phrase. In 29 the v3 decision was the
  `subsidiary of {company}` veto: 28 of them debt, lease, supply, fee and divestiture filings, plus GHM FlackTek, the
  lost true label. In 1 (GHM `0001193125-26-155977`) it was an acquisition verb whose object names no party ("to acquire
  599,808 shares … of Graham common stock").
- **Rule 1's direct-object binding for `subsidiary of`** accounts for WTRG.
- **Rules 2, 3 and 5** changed no live outcome. They are pinned by the constructed cases in
  `AcquisitionScanV4ConstructedCases`, and `AcqScanV3HistoricalControlFixtureTests` proves each case is one v3 got wrong.
- **Rule 4** changed no live acquirer name. It matched HZO's headline, but the capture failed to terminate (see rule 6).

### Report — `acqscan-v3` vs `acqscan-v4` over every item-1.01 filing in the store (harness output, verbatim)

Data root `C:\Users\scm9d\source\repos\radar\data`, read-only. v4: the production `AcquisitionAgreementScan` (acqscan-v4) over the production `HttpSecAcquisitionFilingReader` body, which for an EX-2.1 filing is **with EX-2.1**. v3: the frozen `AcqScanV3HistoricalControl` (acqscan-v3) over the spec-228 read (primary → EX-99.1 → EX-2.1), rebuilt from the same responses. SEC Archives requests this run (production reader and rebuilds together): 954 issued, **0 fetched live over the wire** through the shared paced SEC client, 954 served from spec 228's read-only document cache or this harness's own document cache outside the data root.

- Item-1.01 filing evidence items: **190** (+0 untrustworthy identifiers, never fetched); unresolved company 0; second evidence items for an already-measured accession 5; **accessions measured 185**.
- Production read: success 185, failed 0. Production body is the rebuild no EX-2.1 shown 164, with EX-2.1 21.

#### Tally — `acqscan-v3` vs `acqscan-v4`

| outcome | acqscan-v3 | acqscan-v4 |
| --- | ---: | ---: |
| recognised | 1 | 1 |
| no-merger-agreement | 135 | 165 |
| company-not-target | 4 | 5 |
| company-is-acquirer | 45 | 14 |
| acquirer-not-named | 0 | 0 |
| no-stated-consideration | 0 | 0 |
| empty-body | 0 | 0 |
| verbatim-check-failed | 0 | 0 |
| (not scanned — read failed) | 0 | 0 |

#### Filings whose outcome changed — 31

| ticker | accession | filed | acqscan-v3 | v3 deciding clause | acqscan-v4 | v4 deciding clause |
| --- | --- | --- | --- | --- | --- | --- |
| SKWD | 0001519449-24-000039 | 2024-09-06 | company-is-acquirer | Item 2.03 Creation of a Direct Financial Obligation or an Obligation under an Off-Balance Sheet Arrangement of a Registrant On August 30, 2024, Houston Specialty Insurance Company (“HSIC”), a wholly owned insurance company subsidiary of Sky… | no-merger-agreement | — |
| MHO | 0000799292-24-000088 | 2024-10-25 | company-is-acquirer | ☐ ITEM 1.01 ENTRY INTO A MATERIAL DEFINITIVE AGREEMENT On October 22, 2024, M/I Financial, LLC (“M/I Financial”), a wholly-owned subsidiary of M/I Homes, Inc. (the “Company”), entered into a Second Omnibus Amendment and Joinder to Transacti… | no-merger-agreement | — |
| KOP | 0001193125-24-280510 | 2024-12-17 | company-is-acquirer | On December 17, 2024 (the “Closing Date”), Koppers Inc. (“Koppers” or the “Company”), a wholly-owned subsidiary of Koppers Holdings Inc. (“Holdings”), entered into Amendment No. 5 (“Amendment No. 5”) to the Credit Agreement, dated June 17, … | no-merger-agreement | — |
| AMBA | 0001193125-24-285963 | 2024-12-27 | company-is-acquirer | Item 1.01 Entry into a Material Definitive Agreement On December 20, 2024, Ambarella Corporation (the “Subsidiary”), a wholly-owned subsidiary of Ambarella, Inc. (the “Company”), entered into a Lease Agreement (the “Lease”) with The Quad Sa… | no-merger-agreement | — |
| WTTR | 0001104659-25-006825 | 2025-01-29 | company-is-acquirer | On January 24, 2025 (the “Closing Date”), SES Holdings, LLC (“SES Holdings” or “Parent”), a subsidiary of Select Water Solutions, Inc. (NYSE: WTTR) (the “Company”), Select Water Solutions, LLC, a subsidiary of SES Holdings (the “Borrower”),… | no-merger-agreement | — |
| SKWD | 0001519449-25-000003 | 2025-02-05 | company-is-acquirer | o Item 1.01 Entry into a Material Definitive Agreement On January 31, 2025, Skyward Re, a wholly owned insurance company subsidiary of Skyward Specialty Insurance Group, Inc., (the “Company”), commuted its existing Loss Portfolio Transfer a… | no-merger-agreement | — |
| OTTR | 0001466593-25-000054 | 2025-03-31 | company-is-acquirer | ☐ Item 1.01 Entry into a Material Definitive Agreement On March 27, 2025, Otter Tail Power Company (the “Company”), a wholly owned subsidiary of Otter Tail Corporation (“OTC”), entered into a Note Purchase Agreement (the “Note Purchase Agre… | no-merger-agreement | — |
| PUMP | 0001104659-25-032851 | 2025-04-08 | company-is-acquirer | On April 2, 2025, ProPetro Energy Solutions, LLC (“ Borrower ”), a wholly-owned subsidiary of ProPetro Holding Corp. (the “ Company ”), entered into a Master Loan and Security Agreement (the “ Master Agreement ”) by and among Borrower, Cate… | no-merger-agreement | — |
| SHEN | 0001171843-25-002268 | 2025-04-17 | company-is-acquirer | On April 16, 2025, Shentel Broadband Operations LLC (the “Borrower”), a Delaware limited liability company and a wholly-owned subsidiary of Shentel Broadband Holding Inc. and Shenandoah Telecommunications Company (“Shentel”), entered into A… | no-merger-agreement | — |
| FR | 0001193125-25-119870 | 2025-05-14 | company-is-acquirer | (the “ Issuer ”), a Delaware limited partnership and subsidiary of First Industrial Realty Trust, Inc. (the “ Guarantor ”), completed an underwritten public offering of $450,000,000 aggregate principal amount of its 5.250% Senior Notes due … | no-merger-agreement | — |
| KOP | 0000950170-25-087681 | 2025-06-18 | company-is-acquirer | On June 17, 2025, Koppers Inc. (“Koppers” or the “Company”), a wholly-owned subsidiary of Koppers Holdings Inc. (“Holdings”), entered into Amendment No. 6 (“Amendment No. 6”) with Holdings, certain lenders and letter of credit issuers, and … | no-merger-agreement | — |
| UTL | 0001193125-25-158782 | 2025-07-14 | company-is-acquirer | Item 1.01 Entry into a Material Definitive Agreement On July 8, 2025, Bangor Natural Gas Company (“ Bangor ”), a natural gas distribution utility subsidiary of Unitil Corporation (the “ Company ” or the “ Registrant ”), entered into a Note … | no-merger-agreement | — |
| RGCO | 0001437749-25-028650 | 2025-09-09 | company-is-acquirer | On September 5, 2025, RGC Midstream, LLC (“Midstream”), a wholly owned subsidiary of RGC Resources, Inc. (“Resources”), entered into a Credit Agreement ("Agreement") borrowing $53,600,000 with Atlantic Union Bank (“Atlantic Union”) and CoBa… | no-merger-agreement | — |
| WTRG | 0001552781-25-000341 | 2025-10-27 | company-is-acquirer | On October 26, 2025, American Water Works Company, Inc. (“American Water” or “Parent”), Alpha Merger Sub, Inc., a direct wholly owned subsidiary of Parent (“Merger Sub”), and Essential Utilities, Inc. (“Essential” or the “Company”), entered… | company-not-target | Execution of Agreement and Plan of Merger with American Water Works Company, Inc. |
| IMAX | 0001193125-25-270049 | 2025-11-06 | company-is-acquirer | Shares of IMAX China Holding, Inc., a subsidiary of IMAX Corporation, trade on the Hong Kong Stock Exchange under the stock code “1970.” IMAX ® , IMAX ® 3D, Experience It In IMAX ® , The IMAX Experience ® , DMR ® , Filmed For IMAX ® , IMAX … | no-merger-agreement | — |
| CTO | 0001104659-25-109229 | 2025-11-10 | company-is-acquirer | Pursuant to the terms of the management agreement among Alpine Income Property Trust, Inc. (“PINE”), Alpine Income Property OP, LP and Alpine Income Property Manager, LLC (the “Manager”), a wholly-owned subsidiary of CTO Realty Growth, Inc.… | no-merger-agreement | — |
| CLFD | 0001171843-25-007385 | 2025-11-17 | company-is-acquirer | On November 11, 2025, Clearfield, Inc. (the “Company”) entered into a Share Sale and Purchase Agreement (the “Agreement”) pursuant to which the Company agreed to sell all of the issued and outstanding shares of its wholly-owned subsidiary, … | no-merger-agreement | — |
| SHEN | 0001171843-25-007857 | 2025-12-10 | company-is-acquirer | On December 5, 2025, Shentel Issuer, LLC (the “ Issuer ”), a limited-purpose, bankruptcy remote subsidiary of Shenandoah Telecommunications Company (“ Shentel ”), closed its previously announced inaugural offering of $567,405,000 aggregate … | no-merger-agreement | — |
| GHM | 0001193125-26-021705 | 2026-01-26 | company-is-acquirer | FlackTek will operate as a wholly owned subsidiary of Graham Corporation, maintaining its headquarters in Louisville, Colorado with a satellite location in Greenville, South Carolina, and will be integrated into Graham’s financial, complian… | no-merger-agreement | — |
| PUMP | 0001104659-26-012332 | 2026-02-10 | company-is-acquirer | On February 6, 2026, ProPetro Energy Solutions, LLC (“ Borrower ”), a wholly owned subsidiary of ProPetro Holding Corp. (the “ Company ”), entered into the First Amendment to Master Loan and Security Agreement (the “ Amendment ”) by and amo… | no-merger-agreement | — |
| RGCO | 0001437749-26-009011 | 2026-03-19 | company-is-acquirer | On March 17, 2026, Roanoke Gas Company, the utility subsidiary of RGC Resources, Inc. (“Resources”), amended and restated its Promissory Note ("Revolving Note") and entered into a Third Amendment to the Loan Agreement ("Loan Agreement") (co… | no-merger-agreement | — |
| OTTR | 0001466593-26-000034 | 2026-03-23 | company-is-acquirer | ☐ Item 1.01 Entry into a Material Definitive Agreement On March 19, 2026, Otter Tail Power Company (the “Company”), a wholly owned subsidiary of Otter Tail Corporation (“OTC”), entered into a Note Purchase Agreement (the “Note Purchase Agre… | no-merger-agreement | — |
| RGCO | 0001437749-26-010853 | 2026-04-01 | company-is-acquirer | On March 30, 2026, Roanoke Gas Company (“Roanoke”), the utility subsidiary of RGC Resources, Inc., entered into the Fourth Amendment to Private Shelf Agreement ("Amendment") with PGIM, Inc., fka Prudential Investment Management, Inc., (“Pru… | no-merger-agreement | — |
| GHM | 0001193125-26-155977 | 2026-04-15 | company-is-acquirer | Rowe Price Investment Management, Inc. to invest $50 million in Graham to acquire 599,808 shares (5%) of Graham common stock at $83.36 per share based on 20-day average closing price | no-merger-agreement | — |
| PUMP | 0001680247-26-000058 | 2026-04-30 | company-is-acquirer | On April 28, 2026 (the “Effective Date”), ProPetro Energy Solutions, LLC (“PROPWR”), a Delaware limited liability company and a wholly owned subsidiary of ProPetro Holding Corp. (the “Company”), a Delaware corporation, entered into a Global… | no-merger-agreement | — |
| UTL | 0000755001-26-000012 | 2026-05-05 | company-is-acquirer | On April 30, 2026, Fitchburg Gas and Electric Light Company (“Fitchburg”), an electric and natural gas distribution utility subsidiary of Unitil Corporation (the “Company” or the “Registrant”), entered into a Note Purchase Agreement with St… | no-merger-agreement | — |
| LBRT | 0001694028-26-000027 | 2026-05-07 | company-is-acquirer | Supply Contracts for Power Generation Equipment On May 1, 2026, Liberty Advanced Equipment Technologies LLC (the “Purchaser”), a wholly owned subsidiary of Liberty Energy Inc. (the “Company”), entered into two supply contracts with Bergen E… | no-merger-agreement | — |
| CCOI | 0001104659-26-066279 | 2026-05-26 | company-is-acquirer | On May 22, 2026, Cogent Fiber, LLC, a Delaware limited liability company (the “Seller”) and an indirect wholly owned subsidiary of Cogent Communications Holdings, Inc. (the “Company”), entered into a Purchase and Sale Agreement (the “Purcha… | no-merger-agreement | — |
| RGCO | 0001437749-26-019526 | 2026-06-04 | company-is-acquirer | On June 2, 2026, Roanoke Gas Company (“Roanoke”), the utility subsidiary of RGC Resources, Inc. (“Resources”), entered into an unsecured delayed-draw Promissory Note in the principal amount of $15,000,000 (“Note”) through a Fourth Amendment… | no-merger-agreement | — |
| LBRT | 0001694028-26-000034 | 2026-06-25 | company-is-acquirer | Supply Contract for Power Generation Equipment On June 22, 2026, Liberty Advanced Equipment Technologies LLC (the “Purchaser”), a wholly owned subsidiary of Liberty Energy Inc. (the “Company”), entered into an equipment supply contract with… | no-merger-agreement | — |
| IDT | 0001437749-26-028388 | 2026-08-18 | company-is-acquirer | On August 13, 2026, IDT Telecom, Inc. (“IDT Telecom”), a subsidiary of IDT Corporation (the “Company”), entered into an amendment (the “Amendment”) to its revolving credit agreement with TD Bank, N.A. | no-merger-agreement | — |

#### Truth-table inputs — every filing labelled company-is-acquirer under either version — 45 (v3 45, v4 14)

(The harness cannot judge truth. "Is the filer genuinely acquiring something" is a human read of the Item 1.01 narrative, written to the narratives file.)

| ticker | accession | filed | explicit merger phrases | acqscan-v3 | v3 deciding clause | acqscan-v4 | v4 deciding clause |
| --- | --- | --- | ---: | --- | --- | --- | --- |
| AEHR | 0001654954-24-009008 | 2024-07-16 | 0 | company-is-acquirer | aehr_ex991.htm EXHIBIT 99.1 Contacts: Aehr Test Systems MKR Investor Relations Inc. Chris Siu Todd Kehrli or Jim Byers Chief Financial Officer Analyst/Investor Contact csiu@aehr.com (323) 468-2300 : aehr@mkr-group.com Aehr Test Systems to A… | company-is-acquirer | aehr_ex991.htm EXHIBIT 99.1 Contacts: Aehr Test Systems MKR Investor Relations Inc. Chris Siu Todd Kehrli or Jim Byers Chief Financial Officer Analyst/Investor Contact csiu@aehr.com (323) 468-2300 : aehr@mkr-group.com Aehr Test Systems to A… |
| SKWD | 0001519449-24-000039 | 2024-09-06 | 0 | company-is-acquirer | Item 2.03 Creation of a Direct Financial Obligation or an Obligation under an Off-Balance Sheet Arrangement of a Registrant On August 30, 2024, Houston Specialty Insurance Company (“HSIC”), a wholly owned insurance company subsidiary of Sky… | no-merger-agreement | — |
| MHO | 0000799292-24-000088 | 2024-10-25 | 0 | company-is-acquirer | ☐ ITEM 1.01 ENTRY INTO A MATERIAL DEFINITIVE AGREEMENT On October 22, 2024, M/I Financial, LLC (“M/I Financial”), a wholly-owned subsidiary of M/I Homes, Inc. (the “Company”), entered into a Second Omnibus Amendment and Joinder to Transacti… | no-merger-agreement | — |
| KOP | 0001193125-24-280510 | 2024-12-17 | 0 | company-is-acquirer | On December 17, 2024 (the “Closing Date”), Koppers Inc. (“Koppers” or the “Company”), a wholly-owned subsidiary of Koppers Holdings Inc. (“Holdings”), entered into Amendment No. 5 (“Amendment No. 5”) to the Credit Agreement, dated June 17, … | no-merger-agreement | — |
| AMBA | 0001193125-24-285963 | 2024-12-27 | 0 | company-is-acquirer | Item 1.01 Entry into a Material Definitive Agreement On December 20, 2024, Ambarella Corporation (the “Subsidiary”), a wholly-owned subsidiary of Ambarella, Inc. (the “Company”), entered into a Lease Agreement (the “Lease”) with The Quad Sa… | no-merger-agreement | — |
| WTTR | 0001104659-25-006825 | 2025-01-29 | 0 | company-is-acquirer | On January 24, 2025 (the “Closing Date”), SES Holdings, LLC (“SES Holdings” or “Parent”), a subsidiary of Select Water Solutions, Inc. (NYSE: WTTR) (the “Company”), Select Water Solutions, LLC, a subsidiary of SES Holdings (the “Borrower”),… | no-merger-agreement | — |
| SKWD | 0001519449-25-000003 | 2025-02-05 | 0 | company-is-acquirer | o Item 1.01 Entry into a Material Definitive Agreement On January 31, 2025, Skyward Re, a wholly owned insurance company subsidiary of Skyward Specialty Insurance Group, Inc., (the “Company”), commuted its existing Loss Portfolio Transfer a… | no-merger-agreement | — |
| OTTR | 0001466593-25-000054 | 2025-03-31 | 0 | company-is-acquirer | ☐ Item 1.01 Entry into a Material Definitive Agreement On March 27, 2025, Otter Tail Power Company (the “Company”), a wholly owned subsidiary of Otter Tail Corporation (“OTC”), entered into a Note Purchase Agreement (the “Note Purchase Agre… | no-merger-agreement | — |
| UTL | 0001193125-25-073493 | 2025-04-04 | 1 | company-is-acquirer | HAMPTON, NH, April 1, 2025 : Unitil Corporation (NYSE: UTL) ( unitil.com ) today announced that it has entered into a definitive agreement to acquire Maine Natural Gas Company (Maine Natural) from Avangrid Enterprises, Inc., for $86.0 mil… | company-is-acquirer | HAMPTON, NH, April 1, 2025 : Unitil Corporation (NYSE: UTL) ( unitil.com ) today announced that it has entered into a definitive agreement to acquire Maine Natural Gas Company (Maine Natural) from Avangrid Enterprises, Inc., for $86.0 mil… |
| PUMP | 0001104659-25-032851 | 2025-04-08 | 0 | company-is-acquirer | On April 2, 2025, ProPetro Energy Solutions, LLC (“ Borrower ”), a wholly-owned subsidiary of ProPetro Holding Corp. (the “ Company ”), entered into a Master Loan and Security Agreement (the “ Master Agreement ”) by and among Borrower, Cate… | no-merger-agreement | — |
| SHEN | 0001171843-25-002268 | 2025-04-17 | 0 | company-is-acquirer | On April 16, 2025, Shentel Broadband Operations LLC (the “Borrower”), a Delaware limited liability company and a wholly-owned subsidiary of Shentel Broadband Holding Inc. and Shenandoah Telecommunications Company (“Shentel”), entered into A… | no-merger-agreement | — |
| UTL | 0001193125-25-117855 | 2025-05-12 | 0 | company-is-acquirer | HAMPTON, NH, May 6, 2025 : Unitil Corporation (NYSE: UTL) ( unitil.com ) today announced that it has entered into a definitive agreement to acquire Aquarion Water Company of Massachusetts Inc., Aquarion Water Company of New Hampshire, Inc.,… | company-is-acquirer | HAMPTON, NH, May 6, 2025 : Unitil Corporation (NYSE: UTL) ( unitil.com ) today announced that it has entered into a definitive agreement to acquire Aquarion Water Company of Massachusetts Inc., Aquarion Water Company of New Hampshire, Inc.,… |
| FR | 0001193125-25-119870 | 2025-05-14 | 0 | company-is-acquirer | (the “ Issuer ”), a Delaware limited partnership and subsidiary of First Industrial Realty Trust, Inc. (the “ Guarantor ”), completed an underwritten public offering of $450,000,000 aggregate principal amount of its 5.250% Senior Notes due … | no-merger-agreement | — |
| KOP | 0000950170-25-087681 | 2025-06-18 | 0 | company-is-acquirer | On June 17, 2025, Koppers Inc. (“Koppers” or the “Company”), a wholly-owned subsidiary of Koppers Holdings Inc. (“Holdings”), entered into Amendment No. 6 (“Amendment No. 6”) with Holdings, certain lenders and letter of credit issuers, and … | no-merger-agreement | — |
| UTL | 0001193125-25-158782 | 2025-07-14 | 0 | company-is-acquirer | Item 1.01 Entry into a Material Definitive Agreement On July 8, 2025, Bangor Natural Gas Company (“ Bangor ”), a natural gas distribution utility subsidiary of Unitil Corporation (the “ Company ” or the “ Registrant ”), entered into a Note … | no-merger-agreement | — |
| SKWD | 0001519449-25-000050 | 2025-09-08 | 0 | company-is-acquirer | Document SKYWARD SPECIALTY INSURANCE GROUP TO ACQUIRE APOLLO GROUP HOLDINGS LIMITED, AMPLIFYING “RULE OUR NICHE” STRATEGY Houston, TX - September 02, 2025 (Globe Newswire) - Skyward Specialty Insurance Group, Inc.® (Nasdaq: SKWD) (“Skyward … | company-is-acquirer | Document SKYWARD SPECIALTY INSURANCE GROUP TO ACQUIRE APOLLO GROUP HOLDINGS LIMITED, AMPLIFYING “RULE OUR NICHE” STRATEGY Houston, TX - September 02, 2025 (Globe Newswire) - Skyward Specialty Insurance Group, Inc.® (Nasdaq: SKWD) (“Skyward … |
| RGCO | 0001437749-25-028650 | 2025-09-09 | 0 | company-is-acquirer | On September 5, 2025, RGC Midstream, LLC (“Midstream”), a wholly owned subsidiary of RGC Resources, Inc. (“Resources”), entered into a Credit Agreement ("Agreement") borrowing $53,600,000 with Atlantic Union Bank (“Atlantic Union”) and CoBa… | no-merger-agreement | — |
| WTRG | 0001552781-25-000341 | 2025-10-27 | 40 | company-is-acquirer | On October 26, 2025, American Water Works Company, Inc. (“American Water” or “Parent”), Alpha Merger Sub, Inc., a direct wholly owned subsidiary of Parent (“Merger Sub”), and Essential Utilities, Inc. (“Essential” or the “Company”), entered… | company-not-target | Execution of Agreement and Plan of Merger with American Water Works Company, Inc. |
| PLMR | 0001193125-25-258571 | 2025-10-30 | 0 | company-is-acquirer | On October 27, 2025, Palomar Insurance Holdings, Inc. (the “Buyer”), a direct, wholly owned subsidiary of Palomar Holdings, Inc. (the “Company”) entered into an equity purchase agreement (the “Purchase Agreement”) with The Gray Casualty & S… | company-is-acquirer | Exhibit 99.1 Palomar Holdings, Inc. Announces Agreement to Acquire The Gray Casualty & Surety Company |
| OOMA | 0001193125-25-262903 | 2025-11-03 | 0 | company-is-acquirer | Exhibit 99.1 PRESS RELEASE Ooma Announces Definitive Agreement to Acquire FluentStream • Acquisition to increase Ooma’s revenue, earnings and cash flow following closing • Expected to add approximately 80,000 business users extending Ooma’s… | company-is-acquirer | Exhibit 99.1 PRESS RELEASE Ooma Announces Definitive Agreement to Acquire FluentStream • Acquisition to increase Ooma’s revenue, earnings and cash flow following closing • Expected to add approximately 80,000 business users extending Ooma’s… |
| IMAX | 0001193125-25-270049 | 2025-11-06 | 0 | company-is-acquirer | Shares of IMAX China Holding, Inc., a subsidiary of IMAX Corporation, trade on the Hong Kong Stock Exchange under the stock code “1970.” IMAX ® , IMAX ® 3D, Experience It In IMAX ® , The IMAX Experience ® , DMR ® , Filmed For IMAX ® , IMAX … | no-merger-agreement | — |
| CTO | 0001104659-25-109229 | 2025-11-10 | 0 | company-is-acquirer | Pursuant to the terms of the management agreement among Alpine Income Property Trust, Inc. (“PINE”), Alpine Income Property OP, LP and Alpine Income Property Manager, LLC (the “Manager”), a wholly-owned subsidiary of CTO Realty Growth, Inc.… | no-merger-agreement | — |
| CLFD | 0001171843-25-007385 | 2025-11-17 | 0 | company-is-acquirer | On November 11, 2025, Clearfield, Inc. (the “Company”) entered into a Share Sale and Purchase Agreement (the “Agreement”) pursuant to which the Company agreed to sell all of the issued and outstanding shares of its wholly-owned subsidiary, … | no-merger-agreement | — |
| OOMA | 0001193125-25-304183 | 2025-12-01 | 0 | company-is-acquirer | Exhibit 99.1 Ooma Completes Acquisition of FluentStream Sunnyvale, CA | company-is-acquirer | Exhibit 99.1 Ooma Completes Acquisition of FluentStream Sunnyvale, CA |
| SHEN | 0001171843-25-007857 | 2025-12-10 | 0 | company-is-acquirer | On December 5, 2025, Shentel Issuer, LLC (the “ Issuer ”), a limited-purpose, bankruptcy remote subsidiary of Shenandoah Telecommunications Company (“ Shentel ”), closed its previously announced inaugural offering of $567,405,000 aggregate … | no-merger-agreement | — |
| GHM | 0001193125-26-021705 | 2026-01-26 | 0 | company-is-acquirer | FlackTek will operate as a wholly owned subsidiary of Graham Corporation, maintaining its headquarters in Louisville, Colorado with a satellite location in Greenville, South Carolina, and will be integrated into Graham’s financial, complian… | no-merger-agreement | — |
| THRM | 0001193125-26-029374 | 2026-01-29 | 141 | company-is-acquirer | On January 29, 2026, Gentherm Incorporated, a Michigan corporation (“Gentherm”), entered into definitive agreements with Modine Manufacturing Co., a Wisconsin corporation (“Modine”), Platinum SpinCo Inc., a Delaware corporation and wholly o… | company-is-acquirer | On January 29, 2026, Gentherm Incorporated, a Michigan corporation (“Gentherm”), entered into definitive agreements with Modine Manufacturing Co., a Wisconsin corporation (“Modine”), Platinum SpinCo Inc., a Delaware corporation and wholly o… |
| PLMR | 0001193125-26-033708 | 2026-02-02 | 0 | company-is-acquirer | Introductory Note As previously announced, on October 27, 2025, Palomar Insurance Holdings, Inc. (the “Buyer”), a direct, wholly owned subsidiary of Palomar Holdings, Inc. (the “Company”) entered into an equity purchase agreement (the “Purc… | company-is-acquirer | Exhibit 99.1 Palomar Holdings, Inc. Completes Acquisition of The Gray Casualty & Surety Company and Closes $450 Million Credit Facility LA JOLLA, Calif., February 2, 2025 (GLOBE NEWSWIRE) |
| CMCO | 0001193125-26-037694 | 2026-02-04 | 0 | company-is-acquirer | 13320 Ballantyne Corporate Place Suite D Charlotte, NC 28277 Immediate Release Columbus McKinnon Completes Acquisition of Kito Crosby | company-is-acquirer | 13320 Ballantyne Corporate Place Suite D Charlotte, NC 28277 Immediate Release Columbus McKinnon Completes Acquisition of Kito Crosby |
| KGS | 0001193125-26-039600 | 2026-02-05 | 0 | company-is-acquirer | NEWS RELEASE Kodiak Gas Services to Acquire Distributed Power Solutions THE WOODLANDS, Texas | company-is-acquirer | NEWS RELEASE Kodiak Gas Services to Acquire Distributed Power Solutions THE WOODLANDS, Texas |
| PUMP | 0001104659-26-012332 | 2026-02-10 | 0 | company-is-acquirer | On February 6, 2026, ProPetro Energy Solutions, LLC (“ Borrower ”), a wholly owned subsidiary of ProPetro Holding Corp. (the “ Company ”), entered into the First Amendment to Master Loan and Security Agreement (the “ Amendment ”) by and amo… | no-merger-agreement | — |
| ESQ | 0001104659-26-026781 | 2026-03-12 | 127 | company-is-acquirer | WHEREAS , Esquire Bank is the wholly owned subsidiary of Esquire Financial Holdings, Inc., a Maryland corporation (“ Esquire ”); | company-is-acquirer | WHEREAS , Esquire Bank is the wholly owned subsidiary of Esquire Financial Holdings, Inc., a Maryland corporation (“ Esquire ”); |
| RGCO | 0001437749-26-009011 | 2026-03-19 | 0 | company-is-acquirer | On March 17, 2026, Roanoke Gas Company, the utility subsidiary of RGC Resources, Inc. (“Resources”), amended and restated its Promissory Note ("Revolving Note") and entered into a Third Amendment to the Loan Agreement ("Loan Agreement") (co… | no-merger-agreement | — |
| OTTR | 0001466593-26-000034 | 2026-03-23 | 0 | company-is-acquirer | ☐ Item 1.01 Entry into a Material Definitive Agreement On March 19, 2026, Otter Tail Power Company (the “Company”), a wholly owned subsidiary of Otter Tail Corporation (“OTC”), entered into a Note Purchase Agreement (the “Note Purchase Agre… | no-merger-agreement | — |
| RGCO | 0001437749-26-010853 | 2026-04-01 | 0 | company-is-acquirer | On March 30, 2026, Roanoke Gas Company (“Roanoke”), the utility subsidiary of RGC Resources, Inc., entered into the Fourth Amendment to Private Shelf Agreement ("Amendment") with PGIM, Inc., fka Prudential Investment Management, Inc., (“Pru… | no-merger-agreement | — |
| GHM | 0001193125-26-155977 | 2026-04-15 | 0 | company-is-acquirer | Rowe Price Investment Management, Inc. to invest $50 million in Graham to acquire 599,808 shares (5%) of Graham common stock at $83.36 per share based on 20-day average closing price | no-merger-agreement | — |
| PUMP | 0001680247-26-000058 | 2026-04-30 | 0 | company-is-acquirer | On April 28, 2026 (the “Effective Date”), ProPetro Energy Solutions, LLC (“PROPWR”), a Delaware limited liability company and a wholly owned subsidiary of ProPetro Holding Corp. (the “Company”), a Delaware corporation, entered into a Global… | no-merger-agreement | — |
| UTL | 0000755001-26-000012 | 2026-05-05 | 0 | company-is-acquirer | On April 30, 2026, Fitchburg Gas and Electric Light Company (“Fitchburg”), an electric and natural gas distribution utility subsidiary of Unitil Corporation (the “Company” or the “Registrant”), entered into a Note Purchase Agreement with St… | no-merger-agreement | — |
| LBRT | 0001694028-26-000027 | 2026-05-07 | 0 | company-is-acquirer | Supply Contracts for Power Generation Equipment On May 1, 2026, Liberty Advanced Equipment Technologies LLC (the “Purchaser”), a wholly owned subsidiary of Liberty Energy Inc. (the “Company”), entered into two supply contracts with Bergen E… | no-merger-agreement | — |
| CCOI | 0001104659-26-066279 | 2026-05-26 | 0 | company-is-acquirer | On May 22, 2026, Cogent Fiber, LLC, a Delaware limited liability company (the “Seller”) and an indirect wholly owned subsidiary of Cogent Communications Holdings, Inc. (the “Company”), entered into a Purchase and Sale Agreement (the “Purcha… | no-merger-agreement | — |
| RGCO | 0001437749-26-019526 | 2026-06-04 | 0 | company-is-acquirer | On June 2, 2026, Roanoke Gas Company (“Roanoke”), the utility subsidiary of RGC Resources, Inc. (“Resources”), entered into an unsecured delayed-draw Promissory Note in the principal amount of $15,000,000 (“Note”) through a Fourth Amendment… | no-merger-agreement | — |
| NOVT | 0001193125-26-262867 | 2026-06-09 | 0 | company-is-acquirer | Equity Purchase Agreement On June 8, 2026, Novanta Inc., a Canadian corporation (the “ Company ”), Novanta Medical Technologies Corp., a Delaware corporation and an indirect subsidiary of the Company (“ Buyer ”), Novanta Corporation, a Mich… | company-is-acquirer | Under the terms of the agreement, Novanta will acquire all outstanding equity interests of the parent company of Riverpoint Medical for an upfront cash consideration of $1.2 billion and a milestone payment of $250 million in the first quart… |
| LBRT | 0001694028-26-000034 | 2026-06-25 | 0 | company-is-acquirer | Supply Contract for Power Generation Equipment On June 22, 2026, Liberty Advanced Equipment Technologies LLC (the “Purchaser”), a wholly owned subsidiary of Liberty Energy Inc. (the “Company”), entered into an equipment supply contract with… | no-merger-agreement | — |
| IDT | 0001437749-26-028388 | 2026-08-18 | 0 | company-is-acquirer | On August 13, 2026, IDT Telecom, Inc. (“IDT Telecom”), a subsidiary of IDT Corporation (the “Company”), entered into an amendment (the “Amendment”) to its revolving credit agreement with TD Bank, N.A. | no-merger-agreement | — |
| AXGN | 0001628280-26-061208 | 2026-09-10 | 16 | company-is-acquirer | Document Exhibit 99.1 Axogen Enters into Definitive Agreement to Acquire BioCircuit Technologies Acquisition adds NerveTape™, the 1st FDA-approved device for sutureless peripheral nerve repair, broadening Axogen’s addressable market within … | company-is-acquirer | Document Exhibit 99.1 Axogen Enters into Definitive Agreement to Acquire BioCircuit Technologies Acquisition adds NerveTape™, the 1st FDA-approved device for sutureless peripheral nerve repair, broadening Axogen’s addressable market within … |

#### Every recognition under v3, v4, v4 with EX-2.1 or v4 without it — 1

- **MarineMax, Inc. (HZO)** · 0001193125-26-341302 · filed 2026-08-10
  - acqscan-v3 (with EX-2.1): recognised · acquirer `SHM Holdco, LLC` · consideration `$53.00` (Cash) · target clause: MarineMax Enters into Definitive Agreement to be Acquired by Blackstone Infrastructure Portfolio Company, Safe Harbor, in a $1.5 Billion All-Cash Transaction Ma… · consideration clause: Merger Consideration On the terms and subject to the conditions set forth in the Merger Agreement, at the effective time of the Merger (the “Effective Time”), e…
  - acqscan-v4 with EX-2.1: recognised · acquirer `SHM Holdco, LLC` · consideration `$53.00` (Cash) · target clause: MarineMax Enters into Definitive Agreement to be Acquired by Blackstone Infrastructure Portfolio Company, Safe Harbor, in a $1.5 Billion All-Cash Transaction Ma… · consideration clause: Merger Consideration On the terms and subject to the conditions set forth in the Merger Agreement, at the effective time of the Merger (the “Effective Time”), e…
  - acqscan-v4 without EX-2.1: acquirer-not-named
  - acqscan-v4 as shipped (with EX-2.1): recognised · acquirer `SHM Holdco, LLC` · consideration `$53.00` (Cash) · target clause: MarineMax Enters into Definitive Agreement to be Acquired by Blackstone Infrastructure Portfolio Company, Safe Harbor, in a $1.5 Billion All-Cash Transaction Ma… · consideration clause: Merger Consideration On the terms and subject to the conditions set forth in the Merger Agreement, at the effective time of the Merger (the “Effective Time”), e…

SHOO 0001641172-25-008949: v3 no-merger-agreement, v4 **no-merger-agreement** (with EX-2.1 no-merger-agreement, without no-merger-agreement).

#### Recall probe — every filing whose v4 body carries an explicit merger phrase ("agreement and plan of merger" / "merger agreement" / "plan of merger") that v4 does NOT recognise — 9

(A prompt for a human read, not an absence claim: whether any is a takeover of the Radar company is judged from the text.)

| ticker | accession | filed | phrases | acqscan-v4 | deciding clause | title |
| --- | --- | --- | ---: | --- | --- | --- |
| UTL | 0001193125-25-073493 | 2025-04-04 | 1 | company-is-acquirer | HAMPTON, NH, April 1, 2025 : Unitil Corporation (NYSE: UTL) ( unitil.com ) today announced that it has entered into a definitive agreement to acquire Maine Natural Gas Company (Maine Natural) from Avangrid Enterprises, Inc., for $86.0 mil… | 8-K — 8-K (2025-04-04) [items: 1.01,2.03,7.01,9.01] Items: Entry into a Material… |
| MMSI | 0000856982-25-000035 | 2025-05-20 | 2 | company-not-target | On May 16, 2025, Merit Medical Systems, Inc., a Utah corporation (“Merit”), entered into an Agreement and Plan of Merger (the “Agreement”) by and among, Merit, Biolife, L.L.C., a Florida limited liability company (“FL Biolife”), Biolife Tra… | 8-K — 8-K (2025-05-20) [items: 1.01,2.02,7.01,9.01] Items: Entry into a Material… |
| STRL | 0001193125-25-142774 | 2025-06-18 | 1 | company-not-target | (s) Adopted of any plan of merger, consolidation, reorganization, liquidation or dissolution, or filing of a petition in bankruptcy under any provisions of federal or state bankruptcy Law, or consented to the filing of any bankruptcy petiti… | 8-K — 8-K (2025-06-18) [items: 1.01,3.02,9.01] Items: Entry into a Material Defi… |
| WTRG | 0001552781-25-000341 | 2025-10-27 | 40 | company-not-target | Execution of Agreement and Plan of Merger with American Water Works Company, Inc. | 8-K — 8-K (2025-10-27) [items: 1.01,7.01,9.01] Items: Entry into a Material Defi… |
| THRM | 0001193125-26-029374 | 2026-01-29 | 141 | company-is-acquirer | On January 29, 2026, Gentherm Incorporated, a Michigan corporation (“Gentherm”), entered into definitive agreements with Modine Manufacturing Co., a Wisconsin corporation (“Modine”), Platinum SpinCo Inc., a Delaware corporation and wholly o… | 8-K — 8-K (2026-01-29) [items: 1.01,9.01] Items: Entry into a Material Definitiv… |
| CLMB | 0001437749-26-005335 | 2026-02-24 | 2 | company-not-target | "Final Demerger Deed" means the demerger agreement dated 19 September 2025 entered into between the Target and Infiterra Single Member SA. | 8-K — FORM 8-K (2026-02-24) [items: 1.01,7.01,9.01] Items: Entry into a Material… |
| THRM | 0001193125-26-082690 | 2026-02-27 | 2 | company-not-target | The First Amendment, among other things, (i) permits the transactions (the “Transactions”) contemplated by that certain Agreement and Plan of Merger, dated as of January 29, 2026, by and among Modine Manufacturing Company (“Modine”), Platin… | 8-K — 8-K (2026-02-27) [items: 1.01,2.03,9.01] Items: Entry into a Material Defi… |
| ESQ | 0001104659-26-026781 | 2026-03-12 | 127 | company-is-acquirer | WHEREAS , Esquire Bank is the wholly owned subsidiary of Esquire Financial Holdings, Inc., a Maryland corporation (“ Esquire ”); | 8-K — FORM 8-K (2026-03-12) [items: 1.01,8.01,9.01] Items: Entry into a Material… |
| AXGN | 0001628280-26-061208 | 2026-09-10 | 16 | company-is-acquirer | Document Exhibit 99.1 Axogen Enters into Definitive Agreement to Acquire BioCircuit Technologies Acquisition adds NerveTape™, the 1st FDA-approved device for sutureless peripheral nerve repair, broadening Axogen’s addressable market within … | 8-K — 8-K (2026-09-10) [items: 1.01,7.01,8.01,9.01] Items: Entry into a Material… |

#### The EX-2.1 append decision — v4 with and without the merger agreement, for each of the 21 filing(s) whose index carries one

| ticker | accession | filed | v4 without EX-2.1 | v4 with EX-2.1 | v3 (with EX-2.1) |
| --- | --- | --- | --- | --- | --- |
| SHOO | 0001493152-25-007575 | 2025-02-19 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| CYRX | 0001104659-25-029642 | 2025-03-31 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| UTL | 0001193125-25-073493 | 2025-04-04 | company-is-acquirer | company-is-acquirer | company-is-acquirer |
| UTL | 0001193125-25-117855 | 2025-05-12 | company-is-acquirer | company-is-acquirer | company-is-acquirer |
| STRL | 0001193125-25-142774 | 2025-06-18 | no-merger-agreement | company-not-target ⚠ differs | company-not-target |
| PLUS | 0001022408-25-000049 | 2025-06-23 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| SKWD | 0001519449-25-000050 | 2025-09-08 | company-is-acquirer | company-is-acquirer | company-is-acquirer |
| WTRG | 0001552781-25-000341 | 2025-10-27 | company-not-target | company-not-target | company-is-acquirer |
| PLMR | 0001193125-25-258571 | 2025-10-30 | company-is-acquirer | company-is-acquirer | company-is-acquirer |
| CLFD | 0001171843-25-007385 | 2025-11-17 | no-merger-agreement | no-merger-agreement | company-is-acquirer |
| CMCO | 0001193125-26-012326 | 2026-01-14 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| THRM | 0001193125-26-029374 | 2026-01-29 | company-is-acquirer | company-is-acquirer | company-is-acquirer |
| UTL | 0001193125-26-029469 | 2026-01-29 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| KGS | 0001193125-26-039600 | 2026-02-05 | company-is-acquirer | company-is-acquirer | company-is-acquirer |
| CLMB | 0001437749-26-005335 | 2026-02-24 | no-merger-agreement | company-not-target ⚠ differs | company-not-target |
| UTL | 0001193125-26-067209 | 2026-02-24 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| ESQ | 0001104659-26-026781 | 2026-03-12 | company-not-target | company-is-acquirer ⚠ differs | company-is-acquirer |
| UTL | 0000755001-26-000019 | 2026-05-27 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| NOVT | 0001193125-26-262867 | 2026-06-09 | company-is-acquirer | company-is-acquirer | company-is-acquirer |
| UTL | 0000755001-26-000026 | 2026-07-07 | no-merger-agreement | no-merger-agreement | no-merger-agreement |
| HZO | 0001193125-26-341302 | 2026-08-10 | acquirer-not-named | recognised ⚠ differs | recognised |

Filings whose v4 outcome the EX-2.1 append changes: **4**; filings whose v4 RECOGNITION (outcome, acquirer, consideration or quote) differs with vs without it: **1**.
- STRL · 0001193125-25-142774: without no-merger-agreement → with company-not-target
- CLMB · 0001437749-26-005335: without no-merger-agreement → with company-not-target
- ESQ · 0001104659-26-026781: without company-not-target → with company-is-acquirer
- HZO · 0001193125-26-341302: without acquirer-not-named → with recognised · acquirer `SHM Holdco, LLC` · consideration `$53.00` (Cash) · target clause: MarineMax Enters into Definitive Agreement to be Acquired by Blackstone Infrastructure Portfolio Company, Safe Harbor, in a $1.5 Billion All-Cash Transaction Ma… · consideration clause: Merger Consideration On the terms and subject to the conditions set forth in the Merger Agreement, at the effective time of the Merger (the “Effective Time”), e…

#### Request cost — `www.sec.gov` requests per filing issued by the production reader (index → primary → EX-99.1 when shown → EX-2.1 when shown and appended)

| requests | filings |
| ---: | ---: |
| 2 | 93 |
| 3 | 77 |
| 4 | 15 |

Production reader: 477 requests over 185 filings (mean 2.58); 0 went over the wire this run. Computed from each index: with the EX-2.1 append 477 (mean 2.58), without it 456 (mean 2.46) — the append costs 21 request(s) over the population, one per EX-2.1 filing.

#### No-write confirmation

Every file under the data root was snapshotted (path, length, last-write UTC) before and after the harness: 190922 file(s) before, 190922 after, **0 changed**.
