# Task: An enabled component that has never done anything must SAY SO — the AI filing read and the reported-metrics ledger report their supply and their yield

## Overview

The reported-metrics ledger (specs 215/216) is **enabled and has never written a single file**. Establishing
whether that was a defect or an absence of work took an hour of code reading, because nothing in 25,278 log
lines says either. It turned out to be correct behaviour, and the path to that conclusion is the problem:

- `Radar:Ai:ReportedMetrics:Enabled=true` in the shipped profile; `AddFileReportedMetricStore` registers the
  store AND the outbox together (spec 216 §2); `CollectionPass` receives `IReportedMetricOutbox?` by DI.
  The wiring is correct end to end.
- `data/reported-metrics/` **does not exist**. Zero ledger entries, zero outbox writes, zero references
  projected, since the ledger was re-enabled on 2026-09-09.
- Metrics are extracted only on a FRESH analyzer call, and the analyzer reads ONLY earnings 8-Ks
  (`DirectionalFilingSignalSource.EarningsItemCode = "2.02"`). The newest entry in `data/filings-cache` is
  **2026-09-03 22:50**; 500 cached reads all predate the ledger.
- **Measured supply**: `data/evidence/raw/filing/2026/09` holds exactly **2** item-2.02 filings for the
  whole month (published 09-02 and 09-03) — both BEFORE the ledger was enabled. The analyzer is not behind;
  there is nothing to read.

**The consequence nobody can currently see.** Quarterly reporting clusters; the next earnings wave is late
October. So the entire spec-215/216 reference mechanism — a ledger, an outbox, a projector, a verifier, a
fingerprint input, two specs and an operator step — is **inert for roughly six weeks**, and every judgment in
that window is made without reference values. This is not hypothetical: three of the five `Unknown`
judgments in run `9ce33d31` (2026-09-11) name the absence directly — MRCY "no ReferenceIds supplied",
OFG "the comparison benchmark and prior performance are not provided", CMCO "no comparison". Those three
companies are unresolvable until the ledger has something in it, and nothing reports that.

**Why this is a defect and not a missing nice-to-have.** CLAUDE.md: nothing may be discarded without being
counted, and `null` means "not recorded", never `0` or `false`. An enabled component that produces nothing
is currently indistinguishable from a broken one, from a disabled one, and from one whose output was
dropped. A silence is not a measured zero. The same silence would have hidden a genuine defect — and did
hide, for an hour, the question of which it was.

## Assignment

Worktree: any. Dependencies: main at `a75c08d` or later. Use `run-next.ps1 -Spec 223`.

## 1. The directional filing read reports its SUPPLY, every run

One aggregated line per run, emitted **even when every number is zero** — that is the whole point:

- `Item202CandidatesInWindow` — earnings 8-Ks available to the reader.
- `FilingsAnalysedFresh` / `FilingsServedFromCache` — a fresh call is where metrics can be extracted; a
  cache hit is not. Today these are indistinguishable in the log.
- `FilingsSkippedByBudget` (`Ai:MaxFilingsPerRun`), with the budget named when it binds.
- When `Item202CandidatesInWindow` is **0**, say so explicitly and in plain words — "no earnings 8-K was
  available to read this run" — not by omitting the line. A reader must be able to tell "nothing to do"
  from "did nothing".

## 2. The ledger reports its YIELD, and a measured zero is not a silence

- `ReportedMetricsExtracted`, `OutboxPayloadsEnqueued`, `OutboxWritesSucceeded`, `OutboxWritesRetried`,
  `LedgerEntriesWritten` — all emitted every run, including all-zero.
- `LedgerEntriesOnDisk` — the accrued total, so "never written anything" is visible without a filesystem
  check. The distinction that cost an hour today is exactly this one.
- **A zero must be a MEASURED zero.** Where the count cannot be established, report `null` / "not recorded"
  and say why — never `0` (CLAUDE.md: `null` means not recorded, never `0` or `false`).
- **State the policy in the same line**: enabled vs disabled, and the `rm=` descriptor token in force. An
  enabled-but-idle ledger and a disabled one must never render the same.

## 3. The judge reports when it had NO references, and why

`ReferenceValueProjector` already records what it projected. Add the negative case:

- `JudgmentsWithNoReferencesAvailable`, and the REASON, split: ledger empty vs no reference for the metrics
  these facts name vs excluded by the spec-216 priority rules (a later filing, an unverified pair).
- "The ledger holds nothing at all" and "the ledger holds values but none match this company's facts" are
  different facts and must not share a counter. Today's three `Unknown`s are the first kind; the second kind
  is what will appear once earnings season starts, and confusing them would misdirect the next
  investigation exactly as the silence misdirected this one.

## 4. A standing idle-component rule, applied narrowly here

This spec fixes two components, and states the general rule for reviewers WITHOUT retrofitting it
everywhere (that would be a large, unfocused change):

> An optional or gated subsystem that is ENABLED must, once per run, report whether it did any work and —
> when it did none — why. Registration is not evidence of operation.

Apply it here only. A later slice may extend it; do NOT widen this one.

## 5. Live verification

From the first run after merge, in the PR body:

- The §1 and §2 lines verbatim, which on current supply should read as an explicit, legible **zero** with a
  stated reason ("no earnings 8-K available"), NOT an absent line.
- `LedgerEntriesOnDisk` = 0 and the directory still absent — reported, not inferred.
- The §3 split for the run's judgments, against the 2026-09-11 baseline where 3 of 5 `Unknown`s cited
  missing references.
- **State the expected inert window**: on measured supply (2 item-2.02 filings in September, both pre-
  enablement) the ledger is expected to stay empty until the late-October earnings wave. Recording that
  prediction is what makes a still-empty ledger in November a defect rather than a shrug.

## Non-goals

- **NOT changing what the analyzer reads.** Earnings-only (`2.02`) is unchanged; widening it is a separate
  question with its own cost and prompt implications.
- **NOT changing the analyzer's cadence, budget or cache.** `Ai:MaxFilingsPerRun` and the heal-forward cache
  (spec 205) are untouched.
- **NOT changing the ledger, outbox, projector or verifier behaviour.** Specs 215/216 are correct as built;
  this makes their idleness legible, nothing more.
- **NOT a fingerprint input.** Counters and log lines only — no scoring input, no identity move, no
  operator step. If an implementation finds itself moving a pin, it has exceeded this spec.
- **NOT backfilling** the ledger from the 500 cached reads. Heal forward (AD-8/AD-1); those reads were made
  under a policy that extracted no metrics and re-reading them is a separate, costed decision.
- **NOT retrofitting the §4 rule across other subsystems.**

## Acceptance criteria

- [ ] §1 and §2 lines are emitted on EVERY run including when all counts are zero; a zero renders as an
      explicit measured zero with a stated reason, never as an omitted line.
- [ ] `LedgerEntriesOnDisk` is reported so "never written" is visible without inspecting the filesystem.
- [ ] Enabled-but-idle and disabled render differently, and the line names the `rm=` policy token in force.
- [ ] A count that cannot be established renders as not-recorded, never as `0`.
- [ ] §3 distinguishes "ledger empty" from "no matching reference" with separate counters.
- [ ] No scoring fingerprint moves; no operator step is created. Asserted by test.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.
- [ ] The PR body carries the §5 lines verbatim from a real run and states the expected inert window.
