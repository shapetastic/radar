# Task: Persist the AI filing-read request/response as a diagnostic record (incl. no-signal outcomes)

> **EARNINGS-READ UN-STICK — slice 3 of 3 (observability).** Requested by the maintainer on 2026-07-18 after a
> multi-step cache autopsy was needed to answer "what did the model actually say?" — because today we save
> **nothing** about the AI read unless a signal clears the gate. Independent of 113/114; can land any time.

## Overview

`ChatFilingAnalyzer` builds a prompt (fixed system instruction + truncated EX-99.1 text), calls the model via
`Microsoft.Extensions.AI`'s typed `GetResponseAsync<FilingSentiment>`, parses `FilingSentiment`
(direction/confidence/rationale), and then **discards all of it** unless a directional signal is produced. It
logs only on failure. So for a `NoDirectionalSignal` outcome — the exact case that failed silently across the
whole universe during the SEC-block era — the direction, confidence, rationale, and the input the model saw are
**all lost.** Diagnosing today's issue required busting a cache entry and re-running to infer the verdict. That
is the wrong cost for a routine question.

This slice persists a **diagnostic record** of each AI filing read — including no-signal and empty-body outcomes —
so the model's actual behaviour is inspectable without re-running the pipeline.

## Desired behaviour

For every AI filing analysis attempt, persist (opt-in, diagnostic-only): the accession/evidence identity, the
input the model saw (at minimum its length + a bounded head; ideally the full truncated prompt text), the parsed
verdict (`direction`, `confidence`, bounded `rationale`), and the resulting outcome (signal produced / below
confidence / Mixed / Unknown / empty-body-skipped). Never a scoring input; never surfaced in the report/score.

## Assignment

Worktree: any
Dependencies: none (independent of 113/114; touches the AI adapter + a debug store)
Conflicts with: 114 lightly (both touch the earnings-read path) — coordinate.
Estimated time: ~1–2 hours

## Project structure changes

Add:
- A diagnostic store, e.g. `src/Radar.Infrastructure/Filings/FileFilingReadDebugStore.cs` behind an Application
  seam `IFilingReadDebugSink` (Application defines the interface; Infrastructure writes JSON under a
  `Radar:*Directory`-configured path, e.g. `data/ai-debug/filings/{accession}.json`). Reuse `RadarFileStoreJson`
  / `GracefulFileWriter` scaffolding (do not hand-roll file IO).

Modify:
- `src/Radar.Infrastructure/Filings/ChatFilingAnalyzer.cs` (and/or `DirectionalFilingSignalSource`) to emit the
  debug record at the point the verdict is known — for **all** outcomes, not just signal-produced.
- DI registration + a config toggle (default **off** so normal runs are byte-identical and no cruft is written).

## Implementation details

- **Opt-in, diagnostic-only.** Behind `Radar:Ai:Filings:PersistReadDebug` (default `false`). When off, behaviour
  and outputs are byte-for-byte unchanged (the sink is a null/no-op). It is **never** an evidence/signal/scoring
  input (AD-14 read-side discipline) and never appears in the report.
- **Advice-language scrub (AD-9).** The rationale is already scrubbed by `ChatFilingAnalyzer`; persist the
  scrubbed form. Do not persist raw model text that could carry advice language unscrubbed — run it through the
  same `AdviceLanguage` guard before writing.
- **Bounded.** Cap the persisted input head and rationale lengths (as `ChatFilingAnalyzer` already bounds the
  rationale). Full prompt persistence is acceptable behind the toggle but must be length-capped.
- **No provider SDK leakage (AD-5).** The sink sees only the already-abstracted prompt text + parsed
  `FilingSentiment` — no `IChatClient`/provider types cross into it.
- **Determinism.** Diagnostic write is best-effort (a write failure never aborts or changes the run). No
  wall-clock in any scored path; if a timestamp is recorded it is from `TimeProvider`/`asOfUtc`, not
  `DateTime.Now`.
- **No scoring/version impact.** No `_formula.Version` / `RuleSetVersion` / fingerprint change; default run with
  the toggle off is unchanged.

## Tests

- With the toggle **on**, a signal-produced read, a below-confidence read, a Mixed/Unknown read, and an
  empty-body skip each write a debug record capturing the verdict + outcome + input metadata.
- With the toggle **off** (default), no debug file is written and the pipeline output is byte-identical.
- Advice language in a model rationale is scrubbed before persistence (never written raw).
- A debug-store write failure does not abort the run or change any signal/score/counter.

## Constraints

- Target .NET 10 / `net10.0`, C# 14. Layering + AD-5 (no provider SDK outside Infrastructure) intact.
- Diagnostic-only, opt-in, default off; never a scoring input (AD-14); no advice language (AD-9).
- Reuse shared file scaffolding (`RadarFileStoreJson`/`GracefulFileWriter`) — do not paste new file IO.
- No formula/ruleset/fingerprint version change.

## Acceptance criteria

- [ ] With the toggle on, every AI filing-read outcome (signal / below-confidence / Mixed / Unknown / empty-body)
      persists a bounded, advice-scrubbed diagnostic record with the verdict + input metadata.
- [ ] With the toggle off (default), outputs are byte-for-byte unchanged and nothing is written.
- [ ] Diagnostic-only: no evidence/signal/scoring/report dependency; no fingerprint change (confirm).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
