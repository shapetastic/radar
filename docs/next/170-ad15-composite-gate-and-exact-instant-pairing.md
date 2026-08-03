# Task: Make the AD-15 gate composite, pair on exact instants, and separate claim support from history

> **This slice changes NO precommitment.** The boundary (`2026-09-29`), the primary composite
> (`disclosure-led-v11`), the metric, the horizon and the decision rule were declared on 2026-08-03 and are
> untouched here. What changes is the *implementation*, so that it enforces the rule that was recorded
> rather than a weaker one. Leaving the divergence in place is what would quietly invalidate the claim;
> correcting it is the same act as AD-16's 2026-08-03 (ii) amendment — **the rule that runs must be the rule
> that is written down.**

All three findings below were verified against `main` @ `678d523`. All are fixable before 2026-09-29, when
the first eligible as-of date arrives and the claim path stops being hypothetical.

## Why this matters

Spec 155 shipped the statistics correctly — the earliest-first purge, the observed-window assertion, the
exact median interval, the sign-test handling and the degeneracy rules are all sound. The defects are in
what the result is allowed to *claim*, which is the half that the whole 136–155 arc exists to protect.

### Finding 1 — the gate can qualify before AD-16 has been calculated

`PairedComparisonHarness.cs:313` sets `QualifiesUnderAd15: gateReasons.Count == 0` from the price-side
result alone. No AD-16 status reaches that method. `PairedComparisonRenderer.cs:270` then renders:

> "**Qualifies under AD-15's amended gate: yes.** … may therefore be described as **adding value** relative
> to these baselines"

AD-15's own suspension note (`docs/architecture-decisions.md:1186`) says the opposite:

> "The OUTCOME-VARIABLE half remains: AD-16's precommitted attention-arrival screen **must actually be
> calculated** … and this gate is **confirmatory only after** that screen has been."

So the artifact can print the licence while the condition the AD makes binding has never been evaluated.
This is the sixth instance today of one shape — *a gate reading as satisfied because its precondition was
never checked* — and it is the one that reaches the reader as a claim.

### Finding 2 — common support is paired by calendar date, not by scoring instant

Spec 155 §1 requires intersecting on `(CompanyId, AsOfInstant)`.
`StrategyObservationBuilder.cs:76` keys on `(Guid CompanyId, DateOnly AsOf)` and takes the last same-day
observation **independently per strategy**, so after a partial rerun two arms can be paired while
representing **different knowledge cutoffs**. The pairing would then attribute to strategy difference what
is actually a difference in what each arm could see.

The instant is already lost upstream: `EfficacyPoint.ScoreDate` is `DateOnly` and spec 140's `AsOfDate` is
`DateOnly?`. Correct for a chart; not sufficient for exact pairing.

Currently latent — a normal full run gives every arm the same `WindowEndUtc` — but the live store already
contains multi-run days, and a partial or interrupted run is exactly the condition under which it bites.

### Finding 3 — rendered "joint support" is all-history, beside a claim about out-of-sample support

`jointSupport` is computed at `PairedComparisonHarness.cs:165`; the boundary is applied at line 242. Line
195 renders that all-history figure while line 270 describes the claim as resting on "joint **out-of-sample**
support". Two different quantities under one label, and the artifact shows no eligible joint support and no
per-date company counts for admitted blocks — though `CandidateDates` already carries the latter.

## 1. The AD-15 gate is COMPOSITE; the harness computes only its price half

**Rename first, because the name is half the defect.** `PairedStrategyComparison.QualifiesUnderAd15` becomes
`SatisfiesPriceGate`, and every reason list, CSV column and markdown label follows. A price-side result must
be unable to *read* as the claim even if someone consumes the record directly.

### 1.1 A NEUTRAL prerequisite contract, because the obvious wiring is circular

The comparison generator cannot receive an `AttentionArrivalScreenResult` while Comparison is forbidden from
referencing Attention. Introduce a new namespace **`Radar.Application.Efficacy.Claims`** holding:

- **`Ad15AttentionPrerequisite`** — a small neutral DTO: the screen's availability, its status, and enough
  detail to render (`{ bool WasCalculated, Ad16ScreenOutcome Outcome }`, where `Ad16ScreenOutcome` is a
  closed enum `NotCalculated | Unavailable | Pending | Miss | ClearsNecessaryScreen | Invalid`);
- **`Ad15ClaimGate`** — pure, deterministic, no I/O;
- **`Ad15ClaimVerdict`**.

`Worker` maps `AttentionArrivalScreenResult` → `Ad15AttentionPrerequisite` before calling the comparison
generator. Attention→Claims and Comparison→Claims are both permitted; Comparison→Attention is not, and stays
guardrail-tested.

**The mapping's state machine must be total, and its invalid state named.** `Availability == Available` with
a null or unrecognised `ScreenStatus` is representable even though the evaluator does not intend it — map it
to `Invalid`, which does **not** satisfy the prerequisite. Do not let it fall through to a
`Pending`-like or satisfied branch: an unreadable prerequisite is the same fail-open shape this slice exists
to close, and it would be arriving inside the fix. Every enum combination must be covered by a test.

### 1.2 The verdict's reasons are STRUCTURED, not strings

The existing price reasons are **not** a closed vocabulary — `PairedComparisonHarness` lines 430/435
interpolate baseline names (`baseline 'x': median-paired-delta-not-positive`) and line 421 composes a count.
Composing them into a "closed" list, as an earlier draft of this spec claimed, is not possible.

Define `Ad15GateReason { string Code, string? BaselineName, string? Detail }` with **`Code` closed** and the
variable parts in their own fields. Migrate the existing price reasons onto it, preserving today's rendered
text so the artifact's human-readable output does not regress. New prerequisite codes:

- `ad16-screen-not-calculated` — no prerequisite supplied at all;
- `ad16-screen-unavailable` — a configuration failure, per AD-16;
- `ad16-screen-pending` — the data has not accrued;
- `ad16-screen-invalid` — the prerequisite could not be interpreted (§1.1).

**Absence must fail closed, by construction.** The gate takes the prerequisite as a nullable parameter and a
`null` yields `ad16-screen-not-calculated`. It must be impossible to obtain a qualifying verdict without
supplying one — assert it. If `Radar:Efficacy:AttentionArrival:Enabled` is off, the claim path is therefore
closed, which is the correct reading of a prerequisite that was never run.

**One judgement call, stated explicitly because it borders on a precommitment.** AD-15 requires the screen
to be **calculated**, not to have passed. Therefore `Miss` and `ClearsNecessaryScreen` both **satisfy** the
prerequisite; only `Pending`/unavailable/absent do not. Tightening this to "must not be a `Miss`" would be
an unrecorded change to a precommitted decision and is out of scope. But a reader must never see a positive
price verdict without seeing the attention outcome beside it: when the prerequisite is met by a `Miss`, the
rendered claim block **must** state that AD-16's precommitted screen returned `Miss` on the same page, in
the same block, before the licence sentence.

**Wiring.** `Worker.RunEfficacyReportAsync` runs the attention screen **before** the strategy comparison and
passes its result into the comparison generator; the comparison generator computes the verdict through
`Ad15ClaimGate` and hands it to the renderer. No Comparison type may reference an Attention type — the gate
and the verdict live in a namespace both can depend on (mirroring spec 155's outcome-agnostic
`Efficacy.Statistics` separation, and its guardrail test).

The renderer emits the "adding value" sentence **only** for a qualifying composite verdict. For a price-gate
pass with an unmet prerequisite it states the price result, names the missing prerequisite, and says plainly
that no claim is licensed.

## 2. Pair on the exact scoring instant; use the date only for block grouping

Add a trailing, nullable **`DateTimeOffset? AsOfInstantUtc`** to `EfficacyPoint`, populated in
`EfficacyDatasetBuilder` from the snapshot's `WindowEndUtc` — the same additive shape spec 140 used for
`AsOfDate`, for the same reason.

### 2.1 TWO projections from one read — changing the shared key would break the leaderboard

`StrategyObservationBuilder.Build` is consumed by **both** harnesses — `PairedComparisonHarness:70` and
`StrategyComparisonHarness:49`. Spec 155 extracted it verbatim precisely so the marginal leaderboard stayed
byte-identical. **Re-keying it on the instant would therefore make the marginal leaderboard count multiple
same-day runs instead of collapsing them**, silently changing the descriptive artifact this slice promises
not to touch.

So do not re-key it. Produce **two projections from one read**:

- **date-deduplicated** — today's `(CompanyId, DateOnly)` last-wins behaviour, byte-for-byte, consumed by
  `StrategyComparisonHarness` (the marginal leaderboard);
- **exact-instant** — `(CompanyId, DateTimeOffset AsOfInstant)`, consumed by `PairedComparisonHarness`.

One traversal of the score/price data feeds both; neither may re-read. The marginal projection's output must
be asserted identical to today's, and the leaderboard artifacts byte-unchanged.

**Same-instant duplicates keep today's rule.** The builder currently resolves a repeated key by
last-occurrence-wins (`byKey[key] = …`), and it does not throw. An earlier draft of this spec said it must
throw; that would introduce a **new fatal condition** over live and replay stores whose duplicate rate has
not been measured. Retain last-occurrence-wins, deterministically, in both projections, and document it. If
a throw is ever wanted it needs its own slice, preceded by an audit of `data/scores/` and `data/replays/`.

### 2.2 Block grouping stays on the calendar date

Purging, block grouping and the boundary comparison keep using `DateOnly.FromDateTime(instant.UtcDateTime)`
— the purge is a 21-day rule, not a sub-second one. Only the **intersection** becomes exact. For a fixture
where every arm shares one instant per day, the admitted-block set must be unchanged.

### 2.3 Declared result fields — "named and counted" is not implementable until they are named

Add to `PairedStrategyComparison`, render both in the summary CSV and markdown:

- **`ObservationsWithoutAsOfInstant`** (int) — count of **(company, as-of) observations** excluded from the
  claim path because `AsOfInstantUtc` was null.
- **`ObservationsWithMismatchedAsOfInstant`** (int) — count of **(company, calendar-date) keys** that were
  present in two or more arms whose instants differed, and were therefore not paired.

The units differ deliberately and must be labelled: the first counts observations, the second counts keys.

**A point with a null `AsOfInstantUtc` cannot enter the claim path** — fail closed, never fall back to
date-pairing, since a legacy point is exactly the case where the two arms' cutoffs are unverifiable. This
costs nothing real: the boundary is 2026-09-29 and every claim-path snapshot will carry the instant.

The **marginal leaderboard and the per-company efficacy CSV/SVG stay byte-identical** — the new
`EfficacyPoint` field is trailing, nullable, and read by neither. Assert it.

## 3. All-history support and eligible claim support are different numbers

Add `EligibleJointSupport` — the joint intersection restricted to as-of dates **at or after** the boundary —
alongside the existing `JointSupport`, and render **both**, labelled so they cannot be confused: the
all-history figure describes the dataset, the eligible figure describes the claim. Where no boundary is
precommitted, the eligible support is empty and rendered as such, not as the all-history number.

Render the per-date company `N` for every **admitted block** (already present on `CandidateDates`), so a
reader can see the cross-section each block's rho was computed over rather than a single pooled total.

**CSV shape, decided: a SEPARATE artifact, not a typed multi-row file.** The existing
`strategy-paired-comparison.csv` is one summary row per baseline, and any consumer of it reasonably assumes
homogeneous rows; adding a `recordType=summary|block` discriminator would break that assumption for every
existing reader to save one file. Write per-block rows to a new
**`data/efficacy/strategy-paired-comparison-blocks.csv`** — one row per (baseline, admitted block) carrying
the block's as-of date, its company `N`, both rhos and the delta. The existing summary CSV keeps its exact
current shape plus the additive support columns from above. Markdown renders the blocks inline, since it has
no schema to break.

## 4. Sweep the efficacy read path for the same failure shape

Six instances of *"a gate reads as satisfied when its precondition was never checked"* have been found in
this arc by four different reviewers, each reading a different file, and none by looking for the shape on
purpose. Do that deliberately, once, across `Radar.Application/Efficacy/**`:

**It is TWO-PHASE, and this slice only ever executes phase one.** An open-ended licence to "fix what you
find" would let this slice change eligibility semantics on the strength of undeclared discoveries — six
weeks before a precommitted boundary, in the exact area the precommitment governs. That is the wrong way to
alter what enters a claim, however correct the individual fix.

**Phase 1 (in this slice) — enumerate and record, change nothing.** For every predicate in
`Radar.Application/Efficacy/**` that decides whether an observation, a date, a company or a result is
**admissible, complete, eligible or qualifying**, record whether the absent/unknown/unparseable input fails
CLOSED or OPEN. Write the full enumeration to `docs/170-findings-efficacy-fail-open-sweep.md`, **including
the predicates found already correct** — a sweep listing only defects cannot be checked for coverage. **If
nothing is found beyond the three declared findings, say so explicitly**; that is a legitimate and useful
result.

**Phase 2 (NOT in this slice) — repair.** The only defects fixed here are the three declared above
(§§1–3). Any newly discovered fail-open that would **materially change what is eligible** gets written up in
the findings doc with a recommendation, and becomes its own reviewed spec. A newly discovered defect that
changes nothing about eligibility — a mislabelled reason, an unreachable branch — may be noted but still not
fixed here.

Add regression tests only for the three declared defects. New findings get their tests in their own slice,
so a green suite here never implies an unreviewed semantic change shipped with it.

## Files (verify against the tree before implementation)

- `src/Radar.Application/Efficacy/EfficacyPoint.cs`
- `src/Radar.Application/Efficacy/EfficacyDatasetBuilder.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyObservationBuilder.cs`
- `src/Radar.Application/Efficacy/Comparison/PairedComparisonHarness.cs`
- `src/Radar.Application/Efficacy/Comparison/PairedStrategyComparison.cs`
- `src/Radar.Application/Efficacy/Comparison/PairedComparisonRenderer.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyComparisonReportGenerator.cs` (+ its interface)
- `src/Radar.Application/Efficacy/Comparison/StrategyComparisonHarness.cs` (consumes the date projection)
- new `src/Radar.Application/Efficacy/Claims/` — `Ad15AttentionPrerequisite`, `Ad16ScreenOutcome`,
  `Ad15ClaimGate`, `Ad15ClaimVerdict`, `Ad15GateReason` — and the namespace guardrail test
- `src/Radar.Worker/Worker.cs` (generator ordering + `AttentionArrivalScreenResult` → prerequisite mapping)
- `docs/architecture-decisions.md` (record the composite gate and the `Miss`-satisfies-prerequisite reading)
- tests across Application and Worker

## Constraints

- **No precommitment changes.** `PairedFirstEligibleAsOfUtc` stays `2026-09-29`; `PairedPrimaryStrategy`
  stays `disclosure-led-v11`; metric, horizon, minimum-N, purge rule and interval are untouched.
- **Read-side only.** No evidence, signal, review or score is created, amended or deleted (AD-14).
- **No scoring change, no new fingerprint input, no pin move.** Nothing under `Scoring/`, `Domain/` or
  `Pipeline/` is touched.
- **The marginal leaderboard and the per-company efficacy CSV/SVG stay byte-identical** — asserted, not
  assumed.
- Deterministic ordering and serialization throughout (AD-3); no bootstrap, sampling or randomness.
- No advice vocabulary (AD-9). The claim sentence is about Radar's scoring, never about a company or an
  action.
- Every new failure state is a **named machine-readable reason**, never a log line and never a silent drop.

## Out of scope

- Moving the boundary or the primary arm, or making a `Miss` block the price gate (a precommitment change —
  its own recorded decision if ever wanted).
- Implementing the AD-16 confirmatory attention comparison through this harness (spec 155 §5 leaves it to a
  later slice; this slice only enforces the prerequisite).
- Changing the outcome variable, horizon, or the marginal leaderboard's descriptive 70/30 split.
- Re-ranking, correcting or deleting accrued snapshots.

## Acceptance criteria

- [ ] `QualifiesUnderAd15` is renamed to `SatisfiesPriceGate` everywhere including the CSV header and
      markdown labels; no type in `Efficacy/Comparison` references an `Efficacy/Attention` type (guardrail-tested).
- [ ] `Ad15ClaimGate` consumes the neutral `Ad15AttentionPrerequisite`, never an Attention type; `Worker`
      performs the mapping; every `(Availability, ScreenStatus)` combination is covered by a test and
      `Available` + null/unrecognised status maps to `Invalid`, which does NOT satisfy the prerequisite.
- [ ] Gate reasons are `Ad15GateReason { Code, BaselineName?, Detail? }` with `Code` closed; the existing
      price reasons are migrated onto it with their rendered text unchanged.
- [ ] A null prerequisite **cannot** yield a qualifying verdict, asserted.
- [ ] With the attention generator disabled, a price-gate pass renders **no** "adding value" sentence and
      names the unmet prerequisite.
- [ ] When the prerequisite is met by `Miss`, the rendered claim block states the `Miss` before the licence
      sentence; a fixture proves it.
- [ ] `EfficacyPoint.AsOfInstantUtc` is trailing and nullable; the per-company efficacy CSV/SVG and the
      marginal leaderboard are asserted byte-unchanged.
- [ ] `StrategyObservationBuilder` exposes a date-deduplicated projection and an exact-instant projection
      from ONE read; `StrategyComparisonHarness` consumes the former and its output is asserted identical to
      today's; `PairedComparisonHarness` consumes the latter.
- [ ] Same-instant duplicates still resolve last-occurrence-wins in both projections; nothing throws.
- [ ] Two arms whose same-day observations carry **different** `WindowEndUtc` are NOT paired; a fixture with
      one partial-rerun arm proves it, and `ObservationsWithMismatchedAsOfInstant` counts the affected keys.
- [ ] A point with a null `AsOfInstantUtc` never enters the claim path and is counted in
      `ObservationsWithoutAsOfInstant`; both fields are rendered with their units labelled.
- [ ] Block grouping, purging and the boundary comparison still operate on the calendar date; the purge's
      admitted-block set is unchanged for a fixture where every arm shares one instant per day.
- [ ] `EligibleJointSupport` is a distinct field, rendered beside `JointSupport` with unambiguous labels in
      both CSV and markdown; with no boundary it is empty, never the all-history figure.
- [ ] Per-block company N ships in a NEW `data/efficacy/strategy-paired-comparison-blocks.csv`; the existing
      summary CSV keeps its current row shape plus the additive support columns.
- [ ] `docs/170-findings-efficacy-fail-open-sweep.md` enumerates every admissibility/eligibility predicate in
      `Efficacy/**` with a fail-open/fail-closed verdict for each, including those already correct. **No
      newly discovered defect is fixed in this slice** — only the three declared ones — and each new finding
      carries a recommendation for its own spec.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.
