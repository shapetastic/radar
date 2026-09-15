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
because it keeps a closed thesis open.

### 3. Three smaller rules from the 227/228 reviews

- **"Completion of the acquisition of" does not check whose acquisition.** The v2 acquirer vetoes do not ask whether
  the filer governs the phrase ("Parent's completion of the acquisition of the Company" vs "the Company's completion
  of the acquisition of X").
- **Title-case "By".** `AcquiredByRegex` is case-sensitive (`acquired\s+by`), while target detection lowercases. A
  title-case headline such as "MarineMax … to be Acquired By Blackstone Infrastructure Portfolio Company, Safe
  Harbor" recognises the target but cannot name the acquirer. Spec 228 had to append EX-2.1 to reach HZO's
  acquirer; it noted that fixing this "would also let the EX-2.1 be dropped".
- **A nearby "dividend" excludes a real price.** Spec 227's consideration exclusions look back a fixed distance, so
  "…the regular quarterly dividend will be suspended. Holders will receive $53.00 per share in cash" can exclude
  the real consideration.

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
