# Task: Make scoring read accrued history — a durable signal/evidence read path

> **This is the blocker under the whole strategy-simulation goal, and it is not the fingerprint.** Spec 139
> shipped a working replay verb — but there is nothing for it to replay. Scoring reads
> `ISignalRepository`, which is registered as `InMemorySignalRepository` (a singleton that starts **empty
> every process**). The durable `data/signals/` history is written through a *different, disconnected*
> abstraction, `ISignalFileStore` → `FileSignalStore`. **Scoring has never read accrued history — only what
> the current process collected in that same run.**

## Why this is the real prerequisite

Everything the 136–140 arc is for depends on scoring being able to see the past:

- **136** made both scoring reads filter `CreatedAtUtc <= windowEndUtc` — point-in-time honesty over a store
  that, in production, only ever contains *this run's* signals. The predicate is correct and currently
  near-vacuous.
- **139** replays across historical as-of dates from "stored signals" — over an empty in-memory store.
  139's own hand-back records this: *"replay can't see accrued history"*.
- **140** (strategy-vs-price) cannot produce a meaningful comparison without a real series.
- **Simulating many strategies to find one that works** — the actual goal — requires scoring N strategies
  over months of accrued signals *without re-collecting*. That is impossible today at the persistence layer,
  independent of process structure.

## Design

### 1. Put a durable implementation behind the read path

`ISignalRepository` (and `IEvidenceRepository`) must resolve to an implementation backed by the existing
file stores rather than to the in-memory singletons. **Reconcile the two abstractions rather than adding a
third** (CLAUDE.md: reuse over copy) — `ISignalFileStore`/`FileSignalStore` already own the durable format;
decide deliberately whether the file store becomes the repository's backing or the repository *is* the file
store, and record the choice. The in-memory implementations stay for tests.

- The read seam the scoring path uses (`ReadApprovedInWindowAsync`, with 136's `knownAsOfUtc`) must work
  against the durable store with the **same** semantics — the 136 predicate applied to real history is the
  entire point.
- Preserve append-only semantics (AD-8). Re-running collection must not rewrite or duplicate stored signals;
  define and test the idempotency key.

### 2. Close the `EvidenceQuality` hole

`FileRawEvidenceStore` does not persist `EvidenceQuality`, which is a **v8 formula input**. Hydrating
evidence without it would silently score history differently from how it was scored live — a correctness
hole disguised as a data-format detail.

- Add `EvidenceQuality` to the raw-evidence schema, trailing and nullable for backward compatibility.
- **Decide and document what a legacy `null` means.** Do not default it to a value that flatters the score.
  If a faithful value cannot be recovered for pre-existing evidence, that evidence is honestly
  quality-unknown and the spec must say how the formula treats it.

### 3. Prove hydration is faithful

The invariant, mirroring 139's `replay ⊆ forward`:

> Scoring a window against the **hydrated durable store** must produce the byte-identical score that
> scoring the **same signals held in memory** produces.

Build the slice around proving this — a round-trip test (write → new process/fresh container → read → score)
that would fail if any field is dropped in serialization, `EvidenceQuality` included. A field silently lost
on the way to disk is the failure mode that makes every downstream measurement a lie.

### 4. Verify against the real accrued store

`data/signals/` and `data/evidence/` hold real history from live baseline runs. Hydrate them and report
honestly: how many signals, over what date span, how many companies covered. **If the accrued history turns
out to be thin, say so plainly in the hand-back** — that is a finding about how much simulation is currently
possible, and it should change what 140 claims, not be smoothed over.

## Files (verify against the tree before planning)

`InfrastructureServiceCollectionExtensions` (persistence registration), `InMemorySignalRepository` /
`InMemoryEvidenceRepository`, `FileSignalStore` / `ISignalFileStore`, `FileRawEvidenceStore` +
`FileRawEvidenceStoreOptions`, the raw-evidence record, and the scoring read seam.

## Constraints

- **No scoring change.** Same signals in ⇒ same scores out. This slice changes where signals come *from*.
- **Append-only (AD-8).** Collection must never rewrite history.
- **Layering:** persistence stays in `Radar.Infrastructure`; `Radar.Application` sees only the interfaces.
- **No fingerprint move.** This slice adds no hashed input. (The identity split is spec 141.)
- Tests must not depend on the developer's live `data/` — keep in-memory/temp-dir fixtures for the suite.

## Out of scope (record, do not build)

- **PostgreSQL.** The file stores are the durable medium for this slice; a database is a later, separate
  decision.
- **Splitting collection from scoring into separate runs** — spec 144. This slice makes it *possible*.
- **Identity / fingerprint changes** — spec 141.
- **Backfilling missing history.** Hydrate what exists; do not synthesise.

## Acceptance criteria

- [ ] `ISignalRepository`/`IEvidenceRepository` resolve to durable implementations in the composed app; a
      fresh process sees signals persisted by a previous run.
- [ ] `EvidenceQuality` round-trips through the raw-evidence store; legacy `null` handling is explicit,
      documented, and does not flatter the score.
- [ ] Hydrated-store scoring is byte-identical to in-memory scoring for the same signals — asserted.
- [ ] 136's `knownAsOfUtc` predicate is exercised against real accrued history.
- [ ] Re-running collection is idempotent — no duplicated or rewritten signals.
- [ ] The hand-back reports actual accrued volume, span and company coverage, honestly.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.
