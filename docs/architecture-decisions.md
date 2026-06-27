# Architecture Decisions (Radar)

A running ledger of consciously-made architecture/convention decisions and accepted trade-offs.

**`radar-architecture-reviewer` and `radar-work-planner` MUST read this and treat every decision
recorded here as settled — do not re-flag it as drift, and do not propose work to undo it.** To
change a decision, update its entry here (status → `Superseded`) and record the replacement.

Each entry: the decision, why, status, and date (UTC, absolute).

---

## AD-1 — Persistence write semantics: evidence is immutable, everything else is upsert-by-Id

**Decision.** `EvidenceItem` is **insert-only / immutable**: an existing record is never overwritten,
and a duplicate `ContentHash` is rejected (`IEvidenceRepository.AddIfNewAsync` returns `false`).
All other aggregates — `Company`, `CompanyAlias`, `Signal`, `CompanyScoreSnapshot`,
`ScoreEvidenceLink`, `RadarReport` — use **upsert by `Id` (last-write-wins)** in the repositories.

**Why.** The schema/pipeline specs mandate immutability for *evidence* only (provenance is anchored
there). For the MVP, last-write-wins on the others is simple and in-spec. The contract is documented
as `<remarks>` on the repository interfaces, and the future Dapper implementation **must preserve
these exact semantics** — do not silently switch evidence to upsert or the others to insert-only.

**Status.** Accepted · 2026-06-27 (spec 07). Revisit if append-only history for signals/scores is
needed later.

---

## AD-2 — In-memory repositories do not observe the CancellationToken

**Decision.** The in-memory repository implementations complete synchronously and **do not** check
the `CancellationToken` (the parameter stays on the interface for the contract). Cancellation is the
responsibility of the real (Dapper) implementations, where it is meaningful.

**Why.** Honoring the token on instantaneous in-memory work is noise, and having it observed in one
method but not others (the pre-07 state) was worse — it read as an accident. Uniform non-observance
is the clear convention. Recorded so the reviewer does not re-flag the in-memory methods for "ignoring
`ct`".

**Status.** Accepted · 2026-06-27 (spec 07).

---

## AD-3 — Collection queries return a deterministic order

**Decision.** Every repository method that returns a collection applies a stable
`OrderBy(...).ThenBy(Id)` (never returns raw `ConcurrentDictionary.Values`). Established keys:
companies/aliases by `CreatedAtUtc`, evidence by `CollectedAtUtc`, signals by `ObservedAtUtc`, score
snapshots by `CreatedAtUtc`, score-evidence links by `Id`, report items by `Rank` — each with `Id` as
the tiebreaker.

**Why.** Radar is an evidence-first, **replayable** pipeline; observable output order must be stable.
This is now a positive convention — the reviewer should flag *violations* (unordered query output),
not re-debate the convention itself.

**Status.** Accepted · 2026-06-27 (spec 07).

---

## AD-4 — Application test project may reference Infrastructure

**Decision.** `Radar.Application.Tests` may take a `ProjectReference` on `Radar.Infrastructure` in
order to seed real in-memory repositories (e.g. `InMemoryCompanyRepository`) in tests.

**Why.** It is a test-only dependency with no production layering cycle, and it keeps tests exercising
the real persistence behaviour. Accepted for now. If the team later prefers to keep
`Radar.Application.Tests` free of an Infrastructure dependency, the alternative is an in-test fake
`ICompanyRepository`; until then this is not drift.

**Status.** Accepted · 2026-06-27 (spec 06).
