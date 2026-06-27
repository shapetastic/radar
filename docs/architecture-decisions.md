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

---

## AD-5 — Application may use Microsoft.Extensions.* abstractions (supersedes "package-free Application")

**Decision.** `Radar.Domain` stays pure — **no package references** (records/enums only).
`Radar.Application` **MAY reference the `Microsoft.Extensions.*` abstraction packages**:
`Microsoft.Extensions.Logging.Abstractions` (`ILogger<T>`), `…DependencyInjection.Abstractions`,
`…Options`, `…Configuration.Abstractions`, and `Microsoft.Extensions.AI`. **Concrete provider /
infrastructure SDKs** — database drivers (Npgsql, Dapper), and concrete LLM client SDKs — remain in
`Radar.Infrastructure` only.

This **reverses** the earlier implicit "`Radar.Application` keeps zero package references" rule that
the planner had baked into specs 04/09/10/11 (it forced spec 11 to drop a requested `ILogger`). That
rule was an over-strict extrapolation, not a master-spec requirement.

**Why.** Depending on framework *abstractions* (logging, DI, options, config, `Microsoft.Extensions.AI`)
from the Application layer is standard Clean Architecture and keeps the app testable while still
keeping concrete providers behind interfaces in Infrastructure. The real hard rule is unchanged: **no
concrete AI/data provider SDK outside `Radar.Infrastructure`** — Application gets the abstractions, not
the implementations.

**Scope note.** This is about the `Microsoft.Extensions.*` *abstraction* family, not full ASP.NET Core
hosting/web packages (`Microsoft.AspNetCore.*`), which belong in the `Radar.Api`/`Radar.Worker` host
layer, not in Application.

**Status.** Accepted · 2026-06-27 (decision by maintainer). Existing merged slices are not retrofitted;
new work may add these packages to Application as needed.

---

## AD-6 — Scoring formula v1 (`radar-formula-v1`): shape, constants, and the previous-window input

**Decision.** The first real `IScoreFormula`, `RadarScoreFormulaV1` (`Version = "radar-formula-v1"`),
was **co-designed with and approved by the maintainer**. Its five components are:

- **TrajectoryScore** — confidence-and-recency-weighted mean of directional strength, mapped `50 + 5·T_raw`
  (50 = neutral). Direction signs: `Positive +1`, `Negative −1`, **`Neutral` and `Mixed` = 0**.
- **AttentionScore** — saturating breadth `100·reach/(reach+5)`, `reach = distinctSourceNames + 0.5·mediaSignals`.
- **EvidenceConfidenceScore** — `100·avgConf·(0.6+0.4·qualFactor)·(0.7+0.3·divFactor)`; quality weights
  Primary 1.0 / High .85 / Med .6 / Low .35 / Unknown .4; diversity saturates at 3 distinct source types.
- **SignalVelocityScore** — `50·(actNow+10)/(actPrev+10)` over `Strength` sums (50 = steady).
- **OpportunityScore** — **multiplicative** `Trajectory·(EC/100)·(1 − Attention/200)` (under-the-radar:
  high attention halves, never zeroes).

To feed velocity, **`ScoringInput` carries `PreviousSignals`** — the immediately-preceding equal-length
window `(start−W, start]`, **signals only, no evidence loaded** (velocity needs `Strength` magnitude, not
provenance). **Only current-window signals build `ScoreContribution`s / `ScoreEvidenceLink`s**;
`PreviousSignals` never carries provenance. A signal observed exactly at `windowStart` belongs to the
**previous** window (shared inclusive-end boundary, no double-count).

**Why.** These are deliberate, visible, versioned product choices (full-pipeline spec §Stage 6). They
are settled — the reviewer/planner must **not** re-flag as drift: Neutral/Mixed contributing 0 to
trajectory, the multiplicative Opportunity, the no-evidence-for-previous-window rule, or the
`windowStart`→previous boundary. To change the formula, bump `Version` and update this entry; existing
snapshots remain reproducible under their recorded `ScoringVersion`.

**Status.** Accepted · 2026-06-28 (specs 16–17; formula co-designed with maintainer).
