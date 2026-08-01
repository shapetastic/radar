# Task: Company-filtered collection pass — collect for a subset of the universe, never score one

> **Operational affordance (small slice).** Motivated 2026-07-29: the spec-159 onboarding of 23 companies cost
> a full ~90-minute, ~300-source run just to get their evidence backfill in, and the CASS investigation had no
> way to refresh one company's feeds on demand (IDT's transient RSS failure likewise had no cheap re-check).
> The data model already makes partial collection safe — content-derived evidence ids (145), idempotent
> `AddIfNewAsync` (142), cross-run signal dedupe (85/142), append-only stores (AD-1/AD-8) — and spec 144
> already split collection from scoring. What is missing is only the knob.

## Overview

Add `Radar:Companies` — an optional ticker list that restricts a **collection** pass to a subset of the
watch universe. **Filtering is collection-only, by design and by guard:** a filtered run gathers evidence,
prices and AI reads for the named companies; scoring stays whole-universe on the next full/score run, so
every snapshot cohort remains uniform.

**Why scoring is deliberately excluded.** A filtered *scoring* run would: overwrite the date-keyed weekly
report (`radar-weekly-<date>.md`) with a one-company report, clobbering the day's real one; mint sparse as-of
dates into the spec-140 efficacy join (dates where 65 of 66 companies have no observation); and break the
report's "vs last run" deltas onto mismatched cadences. Collection has none of these — its outputs are
append-only, idempotent stores. **Filter the gathering, never the measuring.**

## Assignment

Worktree: any
Dependencies: current main (post 160).
Estimated time: ~1–2 hours.

## Changes

### 1. `Radar:Companies` — bound on `RadarWorkerOptions`, applied as a seed-source decorator

- New `IReadOnlyList<string> Companies` on `RadarWorkerOptions` (default empty = no filter, byte-identical
  behaviour — the off-switch is absence).
- **The filter is applied at the ONE choke point everything downstream reads:** a
  `FilteredCompanySeedSource : ICompanySeedSource` decorator over `LocalFileCompanySeedSource`, registered by
  the Worker composition root ONLY when the list is non-empty (`IConfiguration` never reaches Application —
  the decorator itself takes the resolved ticker set, not config). Because `CompanyUniverseSeeder`,
  `CollectionPass` (companies + source feeds via `ICompanyRepository`), price acquisition and the AI read all
  flow from the seeded repository, filtering the seed filters the whole pass — collectors never see an
  excluded company's feeds, prices fetch only the filtered tickers, and no per-collector code changes.
- **Filtering must be consistent across the whole seed document**: retained `Companies`, and only the
  `Aliases` and `SourceFeeds` belonging to retained company ids. A feed surviving its excluded company would
  collect evidence that resolves to a company the repository does not hold — assert none survives.

### 2. Validation — fail fast, never fail open

- Tokens are matched against seed tickers **case-insensitively**, whitespace-trimmed; duplicates de-duped.
- A blank token, or a token matching **no** seed ticker, fails startup naming the token and listing the
  valid tickers' count (not all 66 — name the near-misses if cheap, count otherwise). A typo silently
  filtering to nothing would be the spec-138/149 fail-open shape: a run that "worked" and collected nothing.
- An empty resulting set is unreachable given the above (every token matched something), but assert the
  guard anyway.

### 3. Mode guard — the filter is `collect`-only

- Non-empty `Radar:Companies` with `Radar:RunMode` = `full`, `score` or `replay` ⇒ **fail fast at startup**,
  naming both keys and stating the reason in one sentence (report clobbering + sparse as-of dates), pointing
  at `RunMode=collect`. Enforced where `RadarRunModes.Resolve` already reconciles mode conflicts
  (`RadarWorkerServices`), mirroring the existing `RunMode` + `Replay:Enabled` conflict guard.
- The filter therefore composes with everything `collect` mode already guarantees (spec 144): no scoring, no
  report, no snapshot, run record with `strategies: null`.

### 4. Provenance — a partial run must never be mistakable for a full one

- The run record gains a trailing nullable `companyFilter` (the resolved, canonicalised ticker list; `null`
  = unfiltered, so every existing record reads correctly). This is run provenance only — the company
  universe is not a fingerprint input (AD-10 unchanged), and a `collect` run stamps no
  `CollectionProvenance` anyway (that is a snapshot field).
- The collection summary log line states the filter when present (`companies=CASS,IDT (filtered 2 of 66)`).

### 5. Script threading — `run-radar.ps1 -Companies`

- `run-radar.ps1` gains `-Companies "CASS,IDT"` (comma-separated), split and threaded as
  `--Radar:Companies:0=… --Radar:Companies:1=…`. The script **requires `-Mode collect` when `-Companies` is
  passed** (same script-level mirror the `-Mode`/`-Replay` rejection uses) — the config guard would catch it
  anyway, but the script failing early with a one-line message beats a Worker startup exception.
- `run-baseline-scheduled.ps1` / `setup-baseline-task.ps1` are **not** touched — the scheduled baseline is
  always full-universe.

## Implementation checkpoints (verify, don't assume)

- **Collection-health validation on a tiny source count**: confirm the existing collection-health rules
  (failure thresholds etc.) behave sensibly when `sourcesChecked` is ~5 rather than ~300. If any rule uses an
  absolute minimum that a small filtered run would trip, scope that rule to unfiltered runs — do not weaken
  it globally.
- **`SeedFeedInventoryValidator`** must pass on a filtered subset (its rules are per-company, so it should;
  assert it).
- **Efficacy generation in collect mode**: spec 144 says `collect` writes no score and no report; confirm
  the Worker's efficacy/leaderboard generation is likewise not reached in collect mode (if it is reached, it
  is idempotent over unchanged score stores — but confirm rather than argue).

## Tests

- Decorator: filtering retains exactly the named companies with their aliases and feeds; no excluded
  company's feed survives; token matching is case-insensitive and trimmed; duplicates collapse.
- Validation: unknown ticker fails naming the token; blank token fails; the error style matches the existing
  `Radar:Collectors` fail-fast messages.
- Mode guard: filter + `full` / `score` / `replay` each fail fast naming both keys; filter + `collect`
  starts.
- Off-switch: absent/empty `Radar:Companies` resolves the undecorated seed source — asserted byte-identical
  (same instance type, same seed content), in every mode.
- Run record: filtered collect run stamps `companyFilter` with the canonical list; unfiltered run stamps
  null (existing records unaffected — trailing nullable).
- Composition: in `collect` mode with a filter, `Assert.Empty(GetServices<IEvidenceCollector>())` does NOT
  hold (collect mode registers collectors) — but the seeded repository holds only the filtered companies and
  the collection context's source feeds are only theirs.

## Constraints

- **No scoring change, no fingerprint input, no pin move** — the universe is not hashed (AD-10), the filter
  never reaches a scoring pass, and `ScoringConfigFingerprintTests` is untouched. The spec-160 pins
  (AI-ON 30d `ebd7d11a58d0` / 60d `5ffa8c9e25f0` / 120d `19fecdb64e3a`, AI-OFF unmoved) all stand.
- Layering: the decorator lives in Infrastructure (or Worker, beside its registration) and takes plain
  resolved values; `IConfiguration` stays out of Application.
- No collector code changes; no change to the seed file format or `data/companies.json`.
- Append-only stores untouched — a filtered run writes through the same idempotent paths as a full one.

## Out of scope, recorded not built

- Filtered **scoring** (the guard exists precisely to keep it out; if a per-company score preview is ever
  wanted, it should be a read-only replay-style affordance with its own isolated output root, not a filtered
  live pass).
- Per-collector filtering (`-Companies` × `-Collectors` matrix) — `Radar:Collectors` already exists and
  composes naturally; nothing new needed.
- Automatic "collect just the newly added companies" detection on universe expansion — the operator names
  the tickers.

## Acceptance criteria

- [ ] `Radar:Companies` bound, validated (case-insensitive match, unknown/blank fails naming the token),
      applied via a seed-source decorator that filters companies + aliases + feeds consistently.
- [ ] Non-empty filter outside `collect` mode fails fast naming both keys; `collect` + filter runs
      end-to-end.
- [ ] Absent/empty filter is asserted byte-identical to today in every mode.
- [ ] Run record carries trailing nullable `companyFilter`; collection summary log states the filter.
- [ ] `run-radar.ps1 -Companies` threads the list and requires `-Mode collect`.
- [ ] Implementation checkpoints (collection health on small source counts, `SeedFeedInventoryValidator`,
      efficacy-in-collect) verified and asserted where applicable.
- [ ] No fingerprint moves; `dotnet build Radar.sln -c Release` and
      `dotnet test Radar.sln -c Release --no-build` pass.
