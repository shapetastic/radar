# Task: a takeover by another name — `acqscan-v4` misses American Water's all-stock purchase of Essential Utilities (WTRG), a frozen benchmark member

## Overview

Spec 229's recall probe found a **missed takeover of a Radar company**. It is a member of `benchmark-universe-v1`.

**WTRG — Essential Utilities, Inc.**, 8-K `0001552781-25-000341`, filed 2025-10-27. Verified 2026-09-15 against
spec 228's cached documents (`%TEMP%\radar-spec228-sec-documents-final`):

- **8-K, Item 1.01:** *"On October 26, 2025, American Water Works Company, Inc. ("American Water" or "Parent"),
  Alpha Merger Sub, Inc. … [and Essential] … Merger Sub will merge with and into **Essential** (the "Merger"), with
  Essential surviving the Merger as a wholly owned subsidiary of American Water."*
- **EX-2.1:** *"… each share … shall thereupon be converted automatically into and shall thereafter represent the
  right to receive **0.305 (the "Exchange Ratio")** validly issued, fully paid and nonassessable shares of common
  stock, par value $0.01 per share, of Parent"*.
- **EX-99.1:** *"American Water and Essential Utilities to Merge …"*.

`acqscan-v3` and `acqscan-v4` both miss it, for two independent reasons. Each is sufficient on its own:

1. **The company is named by a defined short form.**
   - The seed name is "Essential Utilities, Inc.", and `BuildMentionIndex` offers "Essential Utilities, Inc." and
     "Essential Utilities".
   - The operative target sentence says "merge with and into **Essential**", after the filing defines
     `Essential Utilities, Inc. ("Essential")`.
   - Seed aliases are deliberately excluded from mentions, and would not help anyway: the alias is "Essential
     Utilities".
   - Spec 229 already accepts a defined alias for the filer when it is the literal `(the "Company")` directly after
     a company mention. It does not accept any other defined short name.
2. **The consideration is stated as `receive 0.305 (the "Exchange Ratio") … shares`.** `ExchangeRatioRegex` only
   matches `exchange ratio of X`, where the ratio follows the words. In merger agreements the ratio usually comes
   first and is then defined.

**Why it matters before 2026-09-29.**
- WTRG is `followingTier: large` and a member of the frozen 74-company `benchmark-universe-v1`.
- From the 2025-10-27 announcement, its share price has been bound to AWK's through a fixed exchange ratio.
- Under `excess-vs-universe-v2` a recognised member leaves the equal-weight peer mean and the coverage denominator
  from its announcement date. Unrecognised, WTRG has sat in the peer mean for **~11 months**. That is exactly the
  contamination spec 217 introduced `v2` to remove.
- `observation-eligibility-v2` has admitted WTRG's own outcomes, which no strategy could have earned.
- Spec 227 showed how one wrongly excluded benchmark member (SHOO) moved the Lead arm's out-of-sample lower bound
  from 0.0113 to 0.0022. The direction and size of WTRG's effect is **UNMEASURED**.

**The same pass lost one correct label.** v4 no longer marks GHM `0001193125-26-021705` as `company-is-acquirer`.
The filing reads "Graham Corporation **ACQUIRES** FlackTek", and the acquirer vetoes know
`to acquire` / `will acquire` / `has acquired` / `agreed to acquire` but not the present tense `acquires`. The
FlackTek "wholly owned subsidiary of Graham Corporation" sentence used to carry the label; spec 229 correctly
restricted that phrase to merger filings.

## Assignment

Worktree: any. Dependencies: main at `dc69e36` or later (spec 229 merged; the identity records are still empty
from the 2026-09-15 clearing).

Use `run-next.ps1 -Spec 230`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

**Timing note for the maintainer:** specs 227, 228 and 229 have merged with no run between them. If this slice
merges **before the next scheduled baseline (21:30 UTC)**, all four share one comparability boundary and one
operator step. After that run, it is a second boundary.

## 1. Rules (`acqscan-v5`)

Bump `AcquisitionAgreementScan.Version` to `acqscan-v5`. Keep fail-closed, and keep spec 229's buyer-side
protections intact.

1. **A defined short name is the company.**
   - When a quoted defined term directly follows a company mention in the same filing (`Essential Utilities, Inc.
     ("Essential")`, `MarineMax, Inc. ("MarineMax")`, allowing `the` and the quote styles v4 already handles), that
     term becomes a company mention **for this filing only**.
   - Generalise spec 229's `(the "Company")` recognition into this one mechanism rather than adding a parallel copy.
     Keep its ownership test: a defined term that a different party owns, or that is ambiguous, is not the filer's.
   - Refuse generic role words as defined short names (`Parent`, `Purchaser`, `Buyer`, `Merger Sub`, `Borrower`,
     `Seller`, `Guarantor`, …), reusing spec 227's role list rather than re-declaring it.
   - Refuse anything shorter than the existing 3-character mention floor.
2. **The ratio-first exchange form is consideration.**
   - `right to receive {ratio} (the "Exchange Ratio")` and `{ratio} of a share of {Parent} common stock` are stated
     per-share consideration of kind `Stock`, with the ratio as the amount.
   - The ratio must be governed by the conversion of the company's shares: `each share … converted into … the right
     to receive`.
   - Spec 229's clause-scoped exclusions still apply, so `par value $0.01 per share, of Parent` in the same clause is
     not the consideration.
   - The record must never render a ratio as dollars. The banner and `## Acquisitions pending` row say, for example,
     *"American Water Works Company, Inc. at 0.305 shares per share (stock)"*. Use the record's existing fields
     where they can carry this (`ConsiderationCurrency` empty, `ConsiderationKind: Stock`). If the rendering needs
     one more field, add it, and say why in the PR.
3. **The present-tense acquirer verb.** `{company} acquires {object}` joins the acquirer vetoes under spec 229's
   subject-binding rule, so GHM regains `company-is-acquirer` and "Parent acquires the Company" stays target-side.

**Tests.** Build them on real passages, trimmed and with provenance noted:
- the WTRG 8-K + EX-2.1 → recognised: acquirer American Water Works Company, Inc., consideration `0.305`, `Stock`;
- the GHM headline → `company-is-acquirer`;
- HZO 2026-08-10 → still recognised at `53.00`;
- SHOO → still not recognised.

Add constructed cases for:
- a role word offered as a defined short name (refused);
- a defined short name owned by the counterparty (refused);
- `par value $0.01 per share, of Parent` beside a ratio (the ratio is kept, the par value is not).

## 2. A recall cross-check that does not depend on reading prose

Four specs in a row have tuned prose rules against a population containing two real takeovers. The next miss
should not wait for someone to read a recall table by hand.

A target of a US public merger leaves a **form-type trail** in EDGAR:
- a shareholder-vote merger: `PREM14A` / `DEFM14A`, or an S-4 joint proxy/prospectus, which the target also files
  as `425` communications;
- a tender offer: `SC 14D9`, filed by the target.

These form types are SEC's own classification and need no language reading. Add one deterministic, counted
**recall diagnostic** to the recognition pass's aggregated log line and to the report's acquisitions footer:

- *"N companies filed a merger-proxy / tender-response form (`PREM14A`, `DEFM14A`, `SC 14D9`, …) since
  {date} with no recognised pending acquisition: {tickers}."*
- It is **diagnostic only**. It recognises nothing, closes no thesis and changes no score, so it is **not** a
  fingerprint input. Say so in the PR.
- Source it from filing data Radar already collects if the SEC collector records these form types. Otherwise read
  each company's EDGAR submissions index, paced and cached like the other SEC reads. State which, and the request
  cost.
- Name the form types as data in one place. Decide deliberately whether a company that **files** `425` counts: the
  acquirer also files `425`, so it may need the target check or may be left out. Justify the choice against WTRG
  and HZO.

**Expected live answer:** HZO and WTRG both appear in the form-type trail; under v5 both are recognised, so the
diagnostic names **zero** unrecognised companies. Any name it does list is a finding, and the PR must say what each
one is.

## 3. Live measurement (required — produce it, do not report it as owed)

Use the read-only harness pattern with spec 229's shared path helper:
- env-gated;
- run against the main store at `C:\Users\scm9d\source\repos\radar\data`;
- writes throw;
- per-process temp report;
- a no-write confirmation afterwards.

Bodies come from spec 228's document cache first; paced SEC fetches only for what it lacks, with a real user agent
and a 200 check first.

Report:

- **The tally, `acqscan-v4` vs `acqscan-v5`,** with every changed filing named and its deciding sentence.
- **Every recognition under v5,** with consideration, kind and acquirer. Expected: HZO ($53.00, cash) and WTRG (0.305,
  stock), and nothing else unexplained.
- **The label check for `company-is-acquirer`:** v5 precision against spec 229's hand-checked truth table, plus any
  new labels checked by hand.
- **The §2 diagnostic's live output,** its source, and its request cost.
- **Efficacy before/after WTRG leaves the benchmark,** using the leaderboard's own windows: the Lead arm
  (`disclosure-led-v11`), `default`, `disclosure-led-v10-control` and `baseline-activity-only`, each with its
  out-of-sample rho and 95% CI, and the coverage denominator. Also WTRG's excluded observation count under
  `observation-eligibility-v2`. If the change is negligible, say so; that is a finding.

## 4. Identity

`acq=` is hashed unconditionally, so both pin families move. Update `ScoringConfigFingerprintTests` with lineage
comments. The operator step:
- if this merges before any run follows the 2026-09-15 clearing, the records are still empty and no new step is
  needed — say that conditionally in the PR, do not assume it;
- otherwise it owes one.

The §2 diagnostic must not move a pin.

## 5. Not in scope

- **Deal lifecycle** (completion, termination, expiry) and retiring WTRG or HZO to `Delisted`.
- **Using the form-type trail to RECOGNISE an acquisition.** It is a recall check only. Promoting it is a later
  decision, taken with data.
- **Any change to `benchmark-universe-v1` membership.** It stays frozen; only the v2 exclusion applies.

## Acceptance

1. `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.
2. WTRG is recognised under v5 from its real filing text (American Water Works Company, Inc.; 0.305; Stock), and
   rendered as shares, never dollars. HZO is still recognised at 53.00; SHOO is not; GHM is `company-is-acquirer`.
3. A defined short name is generalised from spec 229's alias mechanism, not duplicated, and role words and
   counterparty-owned terms are refused, with tests.
4. The form-type recall diagnostic is live, counted, named in the log line and the report footer, not a fingerprint
   input, and reports its live output.
5. §3's measurement comes from the live store with the no-write confirmation, including the efficacy before/after.
6. `acqscan-v5`; pins updated with lineage comments; the operator-step statement is conditional on whether a run
   has followed the 2026-09-15 clearing.
7. Doc claims this slice touches are amended in place (REVERSAL rule), including CLAUDE.md's pending-acquisition
   bullet (the consideration forms and the defined-alias rule) and `ExchangeRatioRegex`'s description.
