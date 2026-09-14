# Task: an SEC 8-K item heading is an event type, not a direction — stop scoring every agreement, acquisition, disposal and credit facility as a positive partnership

## Overview

Two rules in `KeywordSignalExtractor` (`src/Radar.Application/SignalExtraction/KeywordSignalExtractor.cs`,
lines ~131–132, `RuleSetVersion = "radar-keyword-rules-v8"`) mint a **Positive `StrategicPartnership`** at
strength 4, confidence 0.5:

```csharp
new("material definitive agreement", SignalType.StrategicPartnership, SignalDirection.Positive, 4, 5, 0.5m),
new("completion of acquisition",     SignalType.StrategicPartnership, SignalDirection.Positive, 4, 5, 0.5m),
```

**Neither phrase is company language.** They match the SEC's own 8-K item headings, which Radar's filing
collector writes into the evidence title:

- **Item 1.01** — *"Entry into a Material Definitive Agreement"* — any material contract: a merger agreement, a
  credit agreement, a lease, a supply contract, an equity purchase agreement.
- **Item 2.01** — *"Completion of Acquisition **or Disposition** of Assets"* — a company buying a business **or
  selling one**.

An item heading records that an event of a given type happened. It carries no information about whether the
event is good or bad for the business, so a direction minted from it is invented.

**Measured on the live store, 2026-09-14** (`data/evidence/raw/filing`, 8-K titles):

| 8-K item | all time | last ~60 days | companies (60d) |
|---|---:|---:|---:|
| 1.01 material definitive agreement | 190 | 13 | 12 |
| 2.01 completion of acquisition or disposition | 23 | 3 | 3 |

Six 8-Ks carry both. The 60-day window shows what these headings actually cover — every one of these scored a
Positive `StrategicPartnership`:

| date | ticker | items | what the item codes indicate |
|---|---|---|---|
| 2026-08-17 | ASIX | 1.01, 1.02, 2.03 | an agreement entered and one terminated, plus a **new debt obligation** |
| 2026-08-31 | DGII | 1.01, 1.02, 2.03 | same shape — agreement entered, one terminated, **new debt obligation** (a refinancing pattern; the item codes alone cannot say more) |
| 2026-09-01 | CAT | 1.01, 2.03 | agreement plus **new debt obligation** |
| 2026-09-02 | CALM | 1.01, 2.03 | agreement plus **new debt obligation** |
| 2026-09-10 | MYRG | 1.01, 2.03 | agreement plus **new debt obligation** |
| 2026-08-06 | EOSE | 1.01, 3.02 | agreement plus **unregistered sale of equity securities** (typically dilution) |
| 2026-08-03 | ESQ | 2.01, 5.02, 8.01 | **completed acquisition** — Esquire bought Signature Bancorporation |
| 2026-07-23 | THRM | 2.01, 7.01, 8.01 | completed acquisition **or disposition** (direction not determinable from the heading) |
| 2026-07-27 | NOVT | 2.01, 2.03, 7.01 | completed acquisition **or disposition**, plus a new debt obligation |
| 2026-08-10 | HZO | 1.01, 7.01 | the MarineMax **merger agreement** (target side — already superseded by spec 217) |

Item 2.03 is *"Creation of a Direct Financial Obligation"*; item 3.02 is *"Unregistered Sales of Equity
Securities"*; item 1.02 is *"Termination of a Material Definitive Agreement"*. So at least five of the thirteen
in-window 1.01 filings are companies **taking on debt**, one is an **equity issuance**, and the scorer reads each
as a positive strategic partnership.

**How this was found.** A skeptic stress test of ESQ (2026-09-14) noted that ESQ's acquisition 8-K scored as
positive evidence. The 2026-09-13 weekly report confirms it verbatim:
`StrategicPartnership (Positive): Matched phrase 'completion of acquisition'`. The skeptic's
(web-sourced, not orchestrator-verified) account is that ESQ roughly doubled its size by buying a bank with
sharply rising problem loans — a transaction whose merit is at least contestable, and which the keyword rule
scored as unambiguous good news. The defect does not depend on that account: the rule would have minted the
same Positive signal for a disposal.

**Spec 217 fixed only one case.** `CorporateActionSupersede` (`acq-supersede-v1`) rewrites the
`StrategicPartnership` over ONE filing — a recognised pending acquisition where the company is the **target** —
as a Neutral `CorporateAction`. Acquirers, disposals, credit agreements, leases, equity issuances and every
other material agreement are untouched.

This spec removes the invented direction everywhere, deterministically, without trying to guess a real one.

## Assignment

Worktree: any. Dependencies: main at `9b94f68` or later (specs 224 and 225 merged).

Use `run-next.ps1 -Spec 226`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. The rule change (`radar-keyword-rules-v9`)

- The two item-heading rules stop minting a directional signal. A filing matching either heading mints a
  **Neutral `CorporateAction`** — the same type and direction spec 217's supersede already uses — so the event is
  still recorded as collected evidence, with no direction mass in Trajectory.
- The signal's reason must name **which item heading matched** (1.01 or 2.01), so the report says what happened
  ("8-K Item 1.01: material definitive agreement") instead of implying a partnership. Route the reason text
  through `KeywordSignalReasons` (spec 225 made that the single home for extractor reason text); do not add a
  second copy.
- Strength: match spec 217's superseded `CorporateAction`, so the two paths produce identical signals for the
  same kind of event. If a different strength is needed, justify it in the PR and report its effect in §4.
- `RuleSetVersion` bumps `radar-keyword-rules-v8` → `radar-keyword-rules-v9`: this is a rule-**structure**
  change (a phrase's direction and type change), which is exactly what that version exists for.
- **Spec 217's target-side supersede must keep working and must not double-apply.** A recognised pending
  acquisition already becomes a Neutral `CorporateAction`; after this change the extractor may already emit one.
  Make the interaction explicit and test it: exactly one `CorporateAction` per recognised filing, with the
  supersede's counters still accurate (a supersede that now finds nothing to rewrite is counted as such, not
  silently skipped).

**Scope the phrases to the headings.** If either phrase could plausibly appear in non-filing evidence (a press
release or article saying "completion of acquisition of X"), decide deliberately whether the rule change applies
there too, measure how many non-filing matches exist, and state the choice in the PR. Do not leave it implicit.

## 2. What this deliberately does NOT do

- **No attempt to assign a real direction.** Reading the filing body to tell an acquirer from a disposer from a
  credit facility, and deciding which is good news, is a separate and much larger job (an extension of
  `acqscan-v1`, or an AI read). Removing an invented direction is strictly better than keeping it; assigning a
  correct one is future work, and this spec should say so rather than half-doing it.
- **No change to item 2.03 or 3.02 handling** beyond what falls out of the two rules above. Whether new debt or
  an equity issuance should ever carry a direction is out of scope.
- **No backfill.** Accrued signals stay as written (AD-8). The change applies to newly extracted evidence, and to
  any re-extraction that already happens through the normal path — state plainly in the PR which of the
  accrued in-window signals will and will not change, and when.

## 3. Identity and fingerprint

`RuleSetVersion` is folded into `ScoringConfigVersion` through `SignalSourceDescriptor` in the `rules=` segment,
outside the AI-gated descriptors — so **both AI-OFF and AI-ON pin families move**. This owes **one operator
step** after merge: delete/re-record `data/scoring-configs/strategies/{name}.json`, then verify the first run's
stamp against `ScoringConfigFingerprintTests`. State it in the PR body.

**Timing note for the maintainer, not a blocker:** the post-219 cohort becomes evaluable against price around
2026-09-30, and every pin move starts a new comparability cohort. Merging before that read fragments the series
it depends on; merging after keeps the read clean. The implementation can land in either order.

## 4. Live distribution (required — produce it, do not report it as owed)

Use the established read-only harness pattern (`tests/Radar.IntegrationTests/InsiderCollapseCounterfactualTests.cs`,
`EvidenceConfidenceDistributionTests.cs`): env-gated, run against the main store at
`C:\Users\scm9d\source\repos\radar\data`, through the **production** extractor and scoring engine, writes throw,
scores in memory, report to a per-process temp path, and confirmation afterwards that no file under `data/` was
modified. Report:

- how many signals in the current 60-day window change from `StrategicPartnership (Positive)` to
  `CorporateAction (Neutral)`, split by item 1.01 / 2.01 and by filing vs non-filing evidence;
- how many companies are affected, and **before/after Trajectory and Opportunity for each**, named — at minimum
  ESQ, DGII, EOSE, CAT, CALM, MYRG, ASIX, NOVT, THRM;
- universe-wide: median Trajectory and Opportunity before/after, and the count of companies whose Opportunity
  moves by more than 2 points and whose rank changes;
- the interaction with spec 217 on HZO: exactly one `CorporateAction`, supersede counters correct.

State the expected shape honestly: each affected company loses strength-4 positive mass, so the effect is largest
where these headings are a big share of positive evidence (quiet companies with little else), and small where
positive mass is large. If almost nothing moves, say so — that is a finding, not a failure.

## 5. Related findings, recorded here so they are not lost — NOT in scope

From the same 2026-09-14 ESQ stress test. Each needs its own verification and, if confirmed, its own spec:

- **Merger share-conversion Form 4s may be counted as insider activity.** ESQ's Caronia and O'Rourke filings on
  2026-08-03 were reported by the skeptic as merger conversions, not trades, and may be inflating ESQ's Velocity
  of 100. Unverified by the orchestrator.
- **`followingTier` is curated seed data and can go stale.** ESQ is `"small"` in `data/companies.json`, but the
  skeptic reports (unverified) that after the acquisition it is a multi-billion-dollar bank with analyst coverage. A data review, not code.
- **The AI earnings read quotes totals.** From Q3 2026 through Q3 2027, ESQ's reported net income and loans may
  grow 50–70% year on year from the acquisition alone while per-share growth is much smaller. The judge could
  read deal growth as organic trajectory — the same failure the OOMA stress test found. The likeliest largest
  of the four.
- **`GuidanceChange Positive` without identified guidance.** The skeptic found no formal guidance in ESQ's Q2 8-K;
  the only forward margin comment was a step down. That is spec 218's subject area.

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. Neither item heading can produce a directional signal; tests prove it for each heading, including an Item 2.01
   disposal and an Item 1.01 + 2.03 credit facility.
3. The reason text names the matched item heading and lives in `KeywordSignalReasons`.
4. `RuleSetVersion` is `radar-keyword-rules-v9`; pins updated in `ScoringConfigFingerprintTests` with lineage
   comments; the one operator step is stated in the PR body.
5. Spec 217's supersede produces exactly one `CorporateAction` per recognised filing, with accurate counters.
6. §4's numbers come from the read-only harness against the live store, with the no-write confirmation.
7. Every claim in docs this slice touches is amended in place (REVERSAL rule), including any doc that describes
   these two rules as partnership signals.
