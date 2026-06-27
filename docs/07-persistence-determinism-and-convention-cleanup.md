# Task: Persistence Determinism and Convention Cleanup

## Overview

A cross-slice architecture audit (after slices 01–03) found drift that individual per-PR reviews
missed: collection-returning repository queries return data in unspecified order, the
`CancellationToken` is honored in exactly one method and ignored everywhere else, and the Worker
scaffold logs a non-UTC timestamp. None break a hard rule today, but all three will compound — the
ordering issue in particular matters once scoring/reporting consume these lists in a *replayable*
pipeline, and the Dapper repositories will replicate whatever convention is set now.

This slice converges those conventions before more code lands on top. It is pure cleanup: no new
features, no new public types, behaviour changes limited to making query output deterministic.

---

## Assignment

Worktree: any
Dependencies: 03-repository-abstractions-and-inmemory
Conflicts with: None — modifies the existing in-memory repository files, `Worker.cs`, and repository
interface XML docs; does **not** touch `InfrastructureServiceCollectionExtensions.cs`, so it does not
overlap specs 05/06. Sequence it last (after 04–06).
Estimated time: ~1 hour

---

## Project structure changes

Modify only (no new production files):

```text
src/Radar.Application/Abstractions/Persistence/
  ICompanyRepository.cs        # L2 doc remarks
  IEvidenceRepository.cs        # L2 doc remarks
  ISignalRepository.cs          # L2 doc remarks
  IScoreRepository.cs           # L2 doc remarks
  IReportRepository.cs          # L2 doc remarks

src/Radar.Infrastructure/Persistence/InMemory/
  InMemoryCompanyRepository.cs   # M1 ordering
  InMemoryEvidenceRepository.cs  # M1 ordering, M2 cancellation
  InMemorySignalRepository.cs    # M1 ordering
  InMemoryScoreRepository.cs     # M1 ordering
  # InMemoryReportRepository.cs is already deterministic — it is the reference, leave it

src/Radar.Worker/
  Worker.cs                      # L1 UTC, L4 seal

tests/Radar.Infrastructure.Tests/Persistence/
  # extend existing test classes / add ordering tests (see Tests)
```

---

## Implementation details

### M1 — Deterministic ordering on every collection query

`InMemoryReportRepository.GetItemsAsync` already orders (`OrderBy(Rank).ThenBy(Id)`); make the other
four repositories match that style. Each collection-returning query must apply a stable, deterministic
`OrderBy(...).ThenBy(...)` before returning (never return raw `ConcurrentDictionary.Values`). Use these
keys (timestamp first, `Id` as the stable tiebreaker):

| Method | Order by |
|---|---|
| `InMemoryCompanyRepository.GetAllAsync` | `CreatedAtUtc`, then `Id` |
| `InMemoryCompanyRepository.GetAliasesAsync` | `CreatedAtUtc`, then `Id` |
| `InMemoryEvidenceRepository.GetAllAsync` | `CollectedAtUtc`, then `Id` |
| `InMemorySignalRepository.GetByCompanyAsync` | `ObservedAtUtc`, then `Id` |
| `InMemorySignalRepository.GetObservedBetweenAsync` | `ObservedAtUtc`, then `Id` |
| `InMemoryScoreRepository.GetSnapshotsForCompanyAsync` | `CreatedAtUtc`, then `Id` |
| `InMemoryScoreRepository.GetLinksForSnapshotAsync` | `Id` (no timestamp on `ScoreEvidenceLink`) |

Confirm the field names against the domain records before using them.

### M2 — One consistent CancellationToken convention

The in-memory stores complete synchronously, so honoring the token is meaningless and is currently
done in only one place. Converge by **removing** the lone `ct.ThrowIfCancellationRequested()` in
`InMemoryEvidenceRepository.AddIfNewAsync`, so every in-memory method treats `ct` uniformly (accepted
to satisfy the interface, not observed). Add a single short comment at the top of one in-memory repo
(or an XML remark on the interface, see L2) stating the convention:

> In-memory implementations complete synchronously and do not observe the CancellationToken; the
> real (Dapper) implementations honor it.

Do not remove `ct` from the method signatures — the interface contract keeps it.

### L1 / L4 — Worker scaffold

- `Worker.cs`: change `DateTimeOffset.Now` to `DateTimeOffset.UtcNow` (UTC is a hard rule).
- `Worker.cs`: mark the class `sealed` (every other sealable type in the tree is sealed).
- Do not otherwise expand the Worker — it stays a placeholder hosted service.

### L2 — Decide and document write semantics

Decision for the MVP (record it, do not change behaviour): **only `EvidenceItem` is immutable**
(insert-only with dedupe, per the schema spec). `Company`/`CompanyAlias`/`Signal`/
`CompanyScoreSnapshot`/`ScoreEvidenceLink`/`RadarReport` use **last-write-wins by `Id` (upsert)** —
which is the current in-memory behaviour and is in-spec.

Encode the contract as XML `<remarks>` on the relevant repository interface methods (in
`Radar.Application.Abstractions.Persistence`) so the future Dapper implementation honors it deliberately:

- `IEvidenceRepository.AddIfNewAsync` — "Insert-only: existing evidence is never overwritten
  (immutable); a duplicate `ContentHash` is rejected and returns false."
- `ICompanyRepository.AddAsync` / `ISignalRepository.AddAsync` / `IScoreRepository.AddSnapshotAsync` /
  `IScoreRepository.AddEvidenceLinkAsync` / `IReportRepository.AddAsync` — "Upsert by `Id`
  (last-write-wins). The relational implementation must preserve these semantics; do not silently
  switch evidence to upsert or these to insert-only."

This is documentation only — no behavioural change.

---

## Tests

Extend `Radar.Infrastructure.Tests/Persistence`. For each query method changed in M1, add (or extend)
a test that **inserts records out of order and asserts the returned sequence is in the specified
order** — mirror the existing pattern in `InMemoryReportRepositoryTests` (which already covers rank
ordering). Cover at least:

- Company `GetAllAsync` ordered by `CreatedAtUtc` then `Id`.
- Evidence `GetAllAsync` ordered by `CollectedAtUtc` then `Id`.
- Signal `GetByCompanyAsync` and `GetObservedBetweenAsync` ordered by `ObservedAtUtc` then `Id`.
- Score `GetSnapshotsForCompanyAsync` ordered by `CreatedAtUtc` then `Id`; `GetLinksForSnapshotAsync`
  ordered by `Id`.

Use deterministic hand-built records with explicit UTC timestamps (the established test convention).
Existing tests must still pass; if any test implicitly relied on insertion order, update it to assert
the new deterministic order.

---

## Constraints

- Target .NET 10; keep `Radar.Application` package-free and the layering intact.
- Pure cleanup: no new public types, no new DI registrations, no new packages.
- Behaviour change is limited to deterministic ordering; `AddIfNewAsync` dedupe behaviour and all
  provenance invariants must remain exactly as they are.
- Do not modify `InMemoryReportRepository` (it is already the reference) beyond what consistency needs.
- Keep the solution buildable and green at every step.

---

## Acceptance criteria

- [ ] All five in-memory repositories return collection queries in a deterministic, documented order
      (no raw `ConcurrentDictionary.Values`).
- [ ] The `CancellationToken` convention is uniform across all in-memory methods, with the convention
      stated once in code/docs.
- [ ] `Worker.cs` uses `DateTimeOffset.UtcNow` and the class is `sealed`.
- [ ] Repository interface methods carry `<remarks>` documenting insert-only (evidence) vs upsert
      (others) write semantics.
- [ ] New/updated tests assert the deterministic ordering for each changed query method.
- [ ] `dotnet build Radar.sln -c Release` (warnings-as-errors) and `dotnet test Radar.sln -c Release`
      are green.
