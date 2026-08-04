# Task: The scheduled baseline run must hold a power request and capture a log

> ⚠️ **DEFERRED — operational, not scoring.** Deferred by maintainer choice, not by a technical gate: there
> is no fingerprint move, no formula change, no scoring behaviour of any kind here, so nothing blocks it
> except priority. Touches `scripts/` only.

## Overview

The daily baseline run is being suspended mid-flight by Windows Modern Standby, and nothing records that it
happened. On **2026-08-04** the scoring stage spent **114 of 184 wall-clock minutes** in connected standby.
The gaps are not throttling artefacts — they line up with the power events almost to the second:

| Snapshot gap (primary strategy) | Standby window (local) | Length |
| --- | --- | ---: |
| 314 s before `LZB` | 09:56:20 → 10:01:49 | 5.5 min |
| 96 s before `STRL` | 10:11:22 → 10:13:00 | 1.6 min |
| 693 s before `WTTR` | 10:13:00 → 10:24:33 | 11.6 min |
| **4,681 s before `CYRX`** | **10:24:33 → 11:42:52** | **78.3 min** |

`CYRX`'s snapshot was written at 11:42:53 — **one second after wake**. The same pattern on 2026-08-03 put a
confirmed 132.6-minute standby (10:35:58 → 12:48:32) inside `narrative-led-v2`'s 195.7-minute span.

**With standby removed, the pipeline is not slow.** Seven of the ten strategies ran entirely awake on
2026-08-04 at **3.0–3.8 s per company** — ~3.7 min per strategy, **~37 min for all ten**. The three showing
~11 s/company are exactly the three with standby interleaved. So this is a host-power defect, not a
performance defect, and the measured cost of leaving it is roughly **3–5× the run's wall clock**.

### Why it happens, precisely

The machine is a Modern Standby (S0ix) host. Connected standby is entered when the **display** turns off,
not when the sleep timer expires, and the Desktop Activity Moderator then suspends background work.
Measured settings at time of writing:

- AC display-off: **15 min** (`SUB_VIDEO/VIDEOIDLE` = 0x384) ← the knob that actually fires
- AC sleep-idle: 180 min (`SUB_SLEEP/STANDBYIDLE` = 0x2a30) — never reached, and therefore a red herring
- DC display-off / sleep-idle: 3 min each
- `powercfg /requests`: **nothing** holds a power request — the Worker has no protection at all
- `RadarBaselineDaily` settings: `WakeToRun False`, `RunOnlyIfIdle False`, `StopIfGoingOnBatteries False`,
  `ExecutionTimeLimit PT72H` — none of which prevent standby

Raising the display timeout would mask it on this host and silently regress on the next one. The run should
declare that it needs the system awake.

### Why this needed forensics

`scripts/run-baseline-scheduled.ps1` discards the Worker's stdout entirely. Nothing under the repo captures
it, so the only evidence available was snapshot mtimes cross-referenced against the Windows System event log
(`Kernel-Power` 506/507). A four-hour run that silently loses three hours should say so in a log.

## Assignment

Worktree: any
Dependencies: none.
Estimated time: ~1 hour.

## Changes

### 1. Hold a system power request for the duration of the run

In `scripts/run-baseline-scheduled.ps1`, before the Worker is launched:

```powershell
Add-Type -Name Power -Namespace Win32 -MemberDefinition @'
  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern uint SetThreadExecutionState(uint esFlags);
'@
# ES_CONTINUOUS (0x80000000) | ES_SYSTEM_REQUIRED (0x00000001)
[void][Win32.Power]::SetThreadExecutionState(0x80000000 -bor 0x00000001)
```

Decisions to preserve, each with its reason:

- **`ES_SYSTEM_REQUIRED` only — deliberately NOT `ES_DISPLAY_REQUIRED`.** The run needs the CPU, not the
  panel. Requesting the display would keep a screen lit for hours for no benefit.
- **In the wrapper, not via `powercfg /requestsoverride`.** The override is persistent, needs elevation, and
  survives a crashed run — it fails toward "machine never sleeps again", which is a worse failure than the
  one being fixed. A thread execution state is released when the wrapper process exits, so a crash cannot
  leave the host permanently awake.
- **The flag is per-thread and holds while that thread lives.** The wrapper's main thread already stays
  alive for the whole run (it waits on the Worker), which is what makes this valid. If the wrapper is ever
  restructured to hand off and exit, this breaks silently — note it at the call site.
- **Reset explicitly on the way out** (`SetThreadExecutionState(0x80000000)`) in a `finally`, so the
  intent is visible rather than relying on process teardown.

**This step must FAIL OPEN — and that is a deliberate choice, stated because fail-open defaults have been a
recurring defect in this repo.** If `Add-Type` or the P/Invoke fails (locked-down host, non-Windows), log a
warning and continue. It is correct here specifically because the run's *output is unaffected* — a
suspended run produces byte-identical scores, just later. Refusing to run would trade a slow-but-correct
result for no result. This is the opposite of the vocabulary/coverage fail-opens (specs 169/174), where
continuing produced a wrong answer that read as a right one.

### 2. Capture the Worker's output to a log

- Tee the Worker invocation to a dated file, e.g. `logs/baseline-<yyyyMMdd'T'HHmmss'Z'>.log`, keeping the
  console output intact so an interactive invocation is unchanged.
- Add `logs/` to `.gitignore`. Logs are host-local operational output; they are not repo content.
- Retention: prune files older than N days (default 30) so an unattended daily task cannot fill the disk.
- Log the run's start and end wall-clock and the total elapsed time, so "did this run get suspended?" is
  answerable from the log alone next time without reaching for the event log.

**Hard constraint — the API key must not reach the log.** `run-baseline-scheduled.ps1`'s header states that
the key VALUE is never printed, logged or written, and the script only ever echoes the fact that a key was
loaded. Adding output capture changes the threat model, so this must be **verified, not assumed**: after a
real run, confirm the produced log contains no key material, and confirm the Worker itself does not echo
`Radar:Ai` credentials at any log level the run uses. Prefer targeted redirection of the Worker invocation
over `Start-Transcript`, which captures the whole session and is easier to widen by accident later.

### 3. Thread the same treatment through the sibling scripts

`scripts/run-radar.ps1` is the interactive entry point and is usually run with someone at the keyboard, so
it is lower risk — but a long `-Mode score` sweep has the same exposure. Apply the keep-awake helper there
too, extracted **once** into a shared helper dot-sourced by both rather than pasted (reuse-over-copy — a
duplicated primitive gets fixed in one copy only).

## Verification

This is a `scripts/`-only change with no .NET surface, so `dotnet test` cannot cover it. Verification is
operational and must actually be performed:

- [ ] During a live run, `powercfg /requests` lists a `SYSTEM` request attributed to the wrapper.
- [ ] Across a full baseline run, `Get-WinEvent` for `Kernel-Power` 506/507 shows **no** standby entry
      between the first and last snapshot write.
- [ ] Wall-clock for the scoring stage lands near the awake-rate prediction (~37 min for 10 strategies at
      74 companies), rather than the 3–5× inflation measured on 2026-08-03 and 2026-08-04.
- [ ] The log file exists, contains the Worker's output, and contains **no** API key material.
- [ ] After the wrapper exits, no lingering power request remains (`powercfg /requests` clean).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` still pass
      (unchanged — no production code is touched).

## Constraints

- No production code, no scoring change, no new fingerprint input, no formula or `RuleSetVersion` bump. All
  four spec-148 pins stand untouched.
- The key-handling rule in `run-baseline-scheduled.ps1`'s header is unchanged and unweakened.
- Do not change `RadarBaselineDaily`'s registered settings as part of this — `WakeToRun` wakes the host to
  *start* a task, which is a different question from keeping it awake once running, and changing the task
  registration is an elevated maintainer step (`setup-baseline-task.ps1`).

## Follow-up — recorded, NOT built here

**`FileSignalStore.GetByCompanyAsync` is O(total signals) per company per strategy** and deserves its own
spec. It scans the whole hydrated index (`_byId.Values.Where(s => s.CompanyId == companyId)`), collapses
cross-run duplicates and sorts — **740 full scans of a ~50k-signal store per run** at the current 74
companies × 10 strategies. Two observations for whoever picks it up:

1. Within a scoring pass the signal store is immutable (persistence completes before the strategy loop), so
   the identical per-company result is recomputed once per strategy — memoising it for the pass would remove
   ~90% of the work with no concurrency involved. Cache lifetime in a long-lived process needs thought.
2. A `CompanyId`-keyed index maintained on hydrate and write turns the scan into a bucket lookup and fixes
   the **scaling curve**, which is the real argument: the cost grows linearly with accrued history, and the
   store only grows.

Parallelising `ScoringPass`'s loops is possible — the shared state is already built for it
(`ConcurrentDictionary` index, `volatile` + `SemaphoreSlim` hydration gate, per-strategy engines and score
stores) — but it divides a cost the index removes, and it would need `Interlocked` on `companiesScored`,
care around concurrent `IScoringConfigStore.WriteIfNewAsync` for two strategies sharing a fingerprint, and
`Interlocked` on replay's `OverwrittenCount`. Lower priority than the index, and lower still now that
standby is understood to be the dominant cost.

## Acceptance criteria

- [ ] `run-baseline-scheduled.ps1` holds `ES_CONTINUOUS | ES_SYSTEM_REQUIRED` for the run and releases it in
      a `finally`; the display is not held.
- [ ] The power-request step fails open with a warning, with the reason recorded at the call site.
- [ ] The Worker's output is captured to a dated, gitignored, pruned log; start/end/elapsed recorded.
- [ ] Verified on a real run that the log contains no API key material.
- [ ] The keep-awake helper is shared between `run-baseline-scheduled.ps1` and `run-radar.ps1`, not pasted.
- [ ] A full baseline run completes with zero `Kernel-Power` 506 events between first and last snapshot.
