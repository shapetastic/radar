# Task: a dividend is not a takeover — `acqscan-v1` closed SHOO's thesis on a quarterly dividend, and quotes HZO's deal price as the $0.001 par value

## Overview

Spec 217's `AcquisitionAgreementScan` (`acqscan-v1`, `src/Radar.Application/Acquisitions/AcquisitionAgreementScan.cs`)
recognises a pending acquisition only when "BOTH legs hold verbatim", fails CLOSED, and exists because "a false
positive closes a live thesis". The live store holds **two** recognitions (`data/acquisitions/`). **One is false,
and the other quotes the wrong price.**

### SHOO — recognised on a dividend, a credit agreement and an acquisition SHOO made

`data/acquisitions/95074627-…/0001641172-25-008949.json` (Steven Madden, Ltd.):

| field | recorded | what it actually is |
|---|---|---|
| `announcedOnUtc` | 2025-05-07 | SHOO's **Q1 2025 results** 8-K, with an amended and restated **credit agreement** attached |
| `acquirerName` | `Lead Borrower` | a defined role in the credit agreement — **SHOO itself** is the lead borrower |
| `considerationPerShare` | `0.21` | *"The Company's Board of Directors approved a quarterly cash **dividend** of $0.21 per share."* |
| `targetQuote` | credit-agreement signature pages running into *"Steve Madden Announces First Quarter 2025 Results ~ Announces Completion of **Acquisition of** Kurt Geiger ~ LONG ISLAND CITY, N.Y., May 7, 2025 – **Steven Madden**, Ltd. (Nasdaq: SHOO)…"* | SHOO **completing its own acquisition** of Kurt Geiger — SHOO is the ACQUIRER |

How each leg passed on the wrong text:

- **Leg (a), target position.** `SplitSentences` only splits on `. ! ?` followed by whitespace, and press-release
  headings have no terminal punctuation, so a heading, a dateline and the first body sentence became ONE
  "sentence". `"acquisition of"` is in `TargetPhrasesBeforeCompany`, and `IsTargetSide` accepts any company
  mention within `ObjectProximity = 90` chars after it. "Kurt Geiger ~ LONG ISLAND CITY, N.Y., May 7, 2025 – "
  is under 90 chars, so **"Steven Madden" was read as the object of "acquisition of"**. None of the acquirer
  vetoes matched ("announces completion of acquisition of" is not "completed the acquisition of").
- **Leg (b), consideration.** `ExtractConsideration` takes the FIRST sentence in the document with `$X per share`.
  Nothing requires that sentence to be about the merger, so a dividend qualifies. So would an exercise price, a
  conversion price, an offering price or a par value.
- **Acquirer.** `"Lead Borrower"` was accepted as an entity name.

**Consequences, live now.**
- SHOO is `PendingAcquisition` from 2025-05-07. The 2026-09-13 weekly report forces it to **Ignore** with the
  banner *"Acquisition pending — Lead Borrower at $0.21 per share in cash, announced 2025-05-07 … Thesis closed"*,
  and lists it under `## Acquisitions pending` at **494 days**.
- SHOO **is a member of the frozen `benchmark-universe-v1`** (`data/efficacy/benchmark-universe-v1.json`).
  Under `excess-vs-universe-v2` a recognised member leaves the equal-weight peer mean AND the coverage
  denominator from its announcement date. SHOO has therefore been missing from the benchmark for **every date
  since 2025-05-07** — the whole efficacy history that the 2026-09-29 claim reads.
- `observation-eligibility-v2` excludes its outcomes on `CorporateActionInWindow`.
- Spec 226's `acq-supersede-v2` rewrites that filing's keyword read as a Neutral `CorporateAction`.

### HZO — a real takeover, recorded at the par value

`data/acquisitions/4992fcce-…/0001193125-26-341302.json` (MarineMax) is a genuine recognition: MarineMax is being
acquired by Safe Harbor, a Blackstone Infrastructure portfolio company, for **$53.00 per share in cash**. The
record says:

- `considerationPerShare: "0.001"`, quoting *"each share of common stock, **par value $0.001 per share**, of the
  Company … will be converted into the right to receive the Merger Consideration"*.
- `acquirerName: "Parent"` — the merger agreement's defined term, not a name.

The same weekly report renders *"Acquisition pending — **Parent at $0.001 per share** in cash"*. The recognition is
right; the two facts shown to a human are wrong.

**Measured precision of `acqscan-v1` on the live store: 1 correct recognition of 2**, and that one carries a wrong
price and a placeholder acquirer. The scan cache (`data/acquisitions-cache/`, 130 answers) records:

| outcome | count |
|---|---:|
| NoMergerAgreement | 103 |
| CompanyNotTarget | 12 |
| CompanyIsAcquirer | 10 |
| NoStatedConsideration | 2 |
| Recognised | 2 |
| AcquirerNotNamed | 1 |

## Assignment

Worktree: any. Dependencies: main at `27cf972` or later (spec 226 merged and its operator step taken).

Use `run-next.ps1 -Spec 227`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. `acqscan-v2` — each leg must be about THE deal

Bump `AcquisitionAgreementScan.Version` to `acqscan-v2`. Leave the fail-closed posture and the named outcomes
intact, and tighten the three places the SHOO and HZO records got through.

**Leg (a), target position.**
- A heading, a dateline or a line break must not let a company mention bind to a phrase in a different clause.
  Choose the mechanism deliberately and justify it in the PR. Candidates:
  - split on line breaks and the `~` / `–` heading separators as well as terminal punctuation;
  - require the company mention to be the phrase's DIRECT object, with nothing but articles, quotes or
    whitespace between them;
  - or both.
- `"acquisition of"` is the weakest target phrase. It reads identically in "announces completion of acquisition of
  X" (the filer is the buyer) and "the acquisition of the Company by Parent". Either require it to be followed
  by the company *and* a `by {acquirer}` in the same clause, or remove it. Measure what removing it would lose
  (§3).
- Add acquirer-side vetoes for "completion of (the) acquisition of" and "completes/completed acquisition of"
  governed by the filer.

**Leg (b), consideration — the amount must be the merger consideration.**
- A `$X per share` match does not count when it is a **par value**, a **dividend**, an **exercise price**, a
  **conversion price** or an **offering/purchase price of an offering**. Name each exclusion as data in one place,
  not as scattered string checks.
- A qualifying sentence must carry merger vocabulary: "merger consideration", "per share in cash" beside
  "acquired" / "merger" / "transaction", "offer price", or "right to receive $X".
- When a sentence holds more than one per-share amount, the excluded one is skipped and scanning continues **in
  that sentence and after it**. HZO's sentence names the par value first; a later sentence — *"MarineMax
  Shareholders to Receive $53.00 Per Share in Cash"* — names the price.
- Say in the PR which amount v2 records for HZO. The expected answer is `53.00`; if v2 cannot reach it, report
  that as the finding rather than bending the rule to the fixture.

**Acquirer.**
- A bare defined-term role is not a name: `Parent`, `Purchaser`, `Buyer`, `Acquiror`, `Merger Sub`, `Borrower`,
  `Lead Borrower`, `Company`, `Guarantor`, `Lender`, `Administrative Agent`, and the like. When the defined-party
  form yields only a role, keep looking — the text before `("Parent")` is usually the entity — and otherwise
  fail `AcquirerNotNamed`.
- If v2 reads HZO's acquirer as the entity the filing names for Parent, say so. If it cannot reach a real name,
  HZO must fail `AcquirerNotNamed`, **not** keep "Parent". A banner naming nobody is refused by design, and one
  naming a placeholder is worse.

**Tests.** Pin both real bodies as fixtures, trimmed to the relevant passages with provenance noted, alongside the
existing spec-217 cases:
- The SHOO 8-K (results + credit agreement + dividend + completion of its OWN acquisition) must NOT be recognised.
  It should land on `CompanyIsAcquirer`, `NoMergerAgreement` or `CompanyNotTarget`; say which and why.
- The HZO 8-K must be recognised with consideration `53.00`.
- Separate cases for: a par-value-only sentence, a dividend-only sentence, a heading-without-punctuation
  adjacency, and a role-only acquirer.

## 2. Retiring a v1 recognition — heal forward, without rewriting the store

The acquisitions store is append-only (AD-8) and must stay so. Today a version bump **cannot** correct a record:

- `FileAcquisitionStore` writes `{companyId}/{accession}.json` with no version in the path, so a v2 record for the
  same accession collides with the v1 file.
- `AcquisitionRecognitionPass` skips a filing when `_store.ExistsAsync(companyId, accession)` is true — **before**
  consulting the version-aware scan cache — so HZO and SHOO would never be rescanned.
- `PendingAcquisitions` admits **every** record regardless of `ScanVersion`, so SHOO's v1 record would keep
  closing its thesis forever.

Make the version part of recognition identity end to end:

- **Store.** A record's durable path includes its scan version. Existing v1 files stay where they are, unmoved and
  unedited, and stay readable. `ExistsAsync` answers for (company, accession, **current scan version**).
- **Pass.** A filing whose only record is from an older scan version is rescanned under the current one, within the
  existing per-run fetch budget (the version bump already retires the scan cache).
- **Projection.** `PendingAcquisitions` admits only records whose `ScanVersion` equals the current
  `AcquisitionAgreementScan.Version`. Records from an older version are **counted, never silently dropped**:
  expose a `RetiredByScanVersion` count and name it in the recognition pass's aggregated log line and the report's
  acquisitions footer.
- **Timing.** A v1 record is retired the moment v2 ships, not when its rescan lands. "Recognised under the rule we
  no longer trust" must not keep closing a thesis while a rescan waits in the budget queue. The consequence is that
  HZO is briefly un-pending until its v2 rescan persists, normally the first post-merge run since the backlog is
  two filings. State this in the PR.

**Snapshots already stamped** `CompanyStatusAtScoring = PendingAcquisition` for SHOO stay exactly as written.
That field is recorded provenance of what the run believed, and this spec does not rewrite it.

## 3. Live measurement (required — produce it, do not report it as owed)

Spec 217 §1's live acqscan distribution over the item-1.01 population was left OWED; it needed `RADAR_SEC_UA` to
fetch bodies. Produce it for v1 and v2 side by side, through the production pass's population and mention
selection (`AcquisitionRecognitionPass.FormCode`, `ItemCode` and `BuildMentionIndex` are public for exactly
this):

- **Harness.** Use the read-only pattern (`tests/Radar.IntegrationTests/InsiderCollapseCounterfactualTests.cs`,
  `EvidenceConfidenceDistributionTests.cs`):
  - env-gated;
  - run against the main store at `C:\Users\scm9d\source\repos\radar\data`;
  - store and cache writes throw;
  - report to a per-process temp path;
  - confirm afterwards that no file under `data/` was modified.
  Body fetches are live SEC reads: pace them with the existing SEC throttle and a real user agent, and check
  `www.sec.gov` returns 200 before starting. An unpaced burst self-blocks the IP.
- **Outcome tallies.** v1 vs v2 over every item-1.01 filing, plus the filings whose outcome changed, each named
  with ticker, accession and both outcomes.
- **Every recognition under either version**, with its consideration and acquirer as each version reads them.
- **Recall check.** Did v2 lose any takeover v1 would have found? The known positive is HZO. If any other
  item-1.01 filing in the store is a real takeover of a Radar company (news or an EX-99.1 headline says "to be
  acquired"), list it and say whether each version finds it.
- **Efficacy effect.** SHOO re-enters the `benchmark-universe-v1` peer mean and coverage denominator. Report the
  pooled benchmark-adjusted efficacy figures before and after for the Lead arm and `default`, using the same
  windows the leaderboard already renders. If the change is negligible, say so — that is a finding.

## 4. Identity and fingerprint

`acq=acqscan-v…` is hashed into `ScoringConfigVersion` unconditionally and outside the AI gate (spec 217 §2), so
**both pin families move** and this owes **one operator step** after merge. Update `ScoringConfigFingerprintTests`
with lineage comments, and state the step in the PR body. If `acq-supersede-v2` or `excess-vs-universe-v2` must
also change version to stay honest, justify it; otherwise leave them alone.

**Timing note for the maintainer, not a blocker:** unlike spec 226, this one corrects data the 2026-09-29 claim
reads directly — SHOO's absence from the benchmark since 2025-05-07. A pin move before that date starts a new
comparability cohort; leaving SHOO excluded means the claim runs on a known-wrong benchmark. Weigh that when
merging.

## 5. Not in scope — recorded so they are not lost

- **A pending acquisition never expires.** SHOO sat "pending" for 494 days and nothing noticed. A deal completes
  (item 2.01 by the target, a Form 25 / delisting) or terminates (item 1.02). Retiring to `Delisted` stays the
  conscious, journaled step spec 217 defined. A cheap counted diagnostic — "N pending recognitions older than 365
  days", named — may be added if it is trivial. Anything more is its own spec.
- **Item 2.01 / 1.02 lifecycle recognition** (completion, termination).
- **The Lead arm's near-constant Opportunity** found during spec 226, and the other follow-ups listed in spec 226 §5.

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. SHOO's real 8-K is not recognised under `acqscan-v2`; HZO's is, at `53.00`, with a real acquirer name or an
   explicit `AcquirerNotNamed`, never a role word.
3. A v1 record no longer governs any consumer (scoring status, report banner/section/footer, efficacy exclusion,
   observation eligibility, supersede), and the retired count is surfaced. No existing file under
   `data/acquisitions/` is modified, moved or deleted.
4. A filing with only a stale-version record is rescanned under the current version within the fetch budget.
5. §3's v1-vs-v2 distribution and efficacy before/after come from the live store, with the no-write confirmation.
6. `ScoringConfigFingerprintTests` updated with lineage comments; one operator step stated in the PR body.
7. Every doc claim this slice touches is amended in place (REVERSAL rule), including CLAUDE.md's "A pending
   acquisition is a CLOSED THESIS" bullet and the `acqscan-v1` docs in `AcquisitionAgreementScan`.
