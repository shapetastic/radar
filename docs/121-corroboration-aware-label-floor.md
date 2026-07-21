# Task: Corroboration-aware label floor — stop under-followed, corroborated names being labelled `Ignore`

> **CALIBRATION / CONVERGENCE (report label layer).** Closes maintainer OPEN DECISION (a): "AEHR Watch vs
> Ignore label threshold — mixed quarters with real forward guidance are getting mislabeled." This is a
> **report action-policy** change only — it touches how a company's already-computed score snapshot is mapped
> onto an AD-9 label. It does **NOT** touch the scoring formula, weights, extractor rules, insider tiers, the
> collector set, or the `ScoringConfigVersion` fingerprint (labels are not a scoring input and are never
> hashed). No `_formula.Version` / `RuleSetVersion` bump; no fingerprint move.
>
> **Maintainer note:** the recommended corroboration-floor rule and its default threshold are spelled out below.
> Running this spec via `run next` is the maintainer's confirmation of the approach; the threshold is a tunable
> `const` a follow-up can adjust after a live re-measure. If the maintainer prefers a different rule, adjust the
> "The rule" section before dispatching.

## Overview

`WeeklyReportActionPolicyV1.Decide` currently maps a company purely on its score-snapshot component values: a
company reaching the steady-state branch with `OpportunityScore < WatchOpportunity (40)` and adequate evidence is
labelled **`Ignore`** (low signal). On the live 2026-07-20 baseline this mislabelled **AEHR** — a genuinely
under-followed small-cap whose thesis was **corroborated** by ~4 `CustomerWin` + a `StrategicPartnership`, but
whose `Mixed` earnings read pulled Trajectory (and therefore Opportunity) toward neutral, dropping it below the
Watch floor. A name with several independent, agreeing directional signals is exactly what Radar exists to
surface ("evidence before opinions, corroboration matters" — the same principle behind `radar-formula-v6`), yet
the policy buried it as `Ignore`.

This slice adds a **corroboration-aware floor**: an **under-followed** company (curated `FollowingTier`
Small/Mid — AD-14) with a **net-positive, corroborated** contributing-signal set is floored at **`Watch`**
instead of falling to `Ignore`. It is a **floor, never an upgrade** — it can only lift `Ignore → Watch`, never
reach `Investigate`, and never fires for already-noticed names (Large/Mega), so it **does not undo spec 117**
(JNJ — Mega, fully-priced — still correctly lands low). The label is a research statistic, advice-free (AD-9).

The policy identity bumps `weekly-report-action-v1 → weekly-report-action-v2` (its own `Version` string, not a
scoring version — no fingerprint impact).

---

## Assignment

Worktree: any. Edits the report action-policy + its context + the report builder that populates it (+ tests).
Dependencies: current main (post 120). No scoring/fingerprint dependency.
Conflicts with: **spec 123** (both edit `WeeklyReportBuilder` and the report-entry plumbing) and any slice
touching `WeeklyReportActionPolicyV1` / `ReportActionContext`. **Sequence before 123** (123 depends on this).
Estimated time: ~1.5–2 hours

---

## Grounding facts (verified against the code, 2026-07-21)

- **`WeeklyReportActionPolicyV1`** (`src/Radar.Application/Reporting/WeeklyReportActionPolicyV1.cs`): pure, no
  I/O. Decision precedence: (1) thin evidence → `NeedsMoreEvidence`; (2) deterioration; (3) improvement;
  (4) steady-state by `OpportunityScore` (`>=60` Investigate, `>=40` Watch, else **`Ignore`**). The new floor
  inserts between the Watch check and the final `Ignore` return.
- **`ReportActionContext`** (`src/Radar.Application/Reporting/ReportActionContext.cs`) is
  `(CompanyScoreSnapshot Current, CompanyScoreSnapshot? Previous, bool PreviousComparable = true)`. It currently
  carries **no** signal set and **no** company metadata — both must be added for the floor.
- **`WeeklyReportBuilder.GenerateAsync`** (`src/Radar.Application/Reporting/WeeklyReportBuilder.cs`): builds the
  `ReportActionContext` at line ~210 and calls `_policy.Decide(...)`. It builds `signals` (a
  `IReadOnlyList<ReportSignalRef>` with `Type` + `Direction`, from the score-evidence links — i.e. the signals
  that **actually contributed to the score**) at line ~212, **after** the `Decide` call. It also holds
  `c.Company` (which carries `FollowingTier`). The floor's corroboration measure is exactly this contributing
  set, so `BuildSignalRefsAsync` must move **before** `Decide` and its result be passed into the context.
- **`ReportSignalRef`** (`src/Radar.Application/Reporting/ReportSignalRef.cs`): `(Guid SignalId, SignalType Type,
  SignalDirection Direction, string Reason)`.
- **`Company.FollowingTier`** (`src/Radar.Domain/Companies/FollowingTier.cs`): `Small | Mid | Large | Mega`.
- Labels are **not** a scoring input and are **not** hashed into `ScoringConfigVersion` (AD-10); the efficacy
  layer (spec 101/108) reads score snapshots, never labels. So this has **zero** fingerprint / efficacy impact.

---

## The rule (recommended; threshold is a tunable `const`)

Insert a new branch in the steady-state section, **after** the `Watch` check and **before** the final `Ignore`
return. A company that would otherwise be `Ignore` is floored to **`Watch`** iff **all** hold:

1. **Under-followed** — `context.FollowingTier` is `Small` or `Mid` (never floors Large/Mega — preserves the
   spec-117 notedness posture; JNJ-style noticed names still fall to `Ignore`).
2. **Net-positive trajectory** — `Current.TrajectoryScore >= NeutralTrajectory (50)` (do not floor a
   deteriorating/negative-leaning name up to Watch).
3. **Corroborated** — the contributing signal set contains **≥ `MinCorroboratingSignalTypes` (default 2)
   DISTINCT `SignalType`s whose `Direction == Positive`** (distinct *types*, not merely count — two independent
   axes agreeing, e.g. `CustomerWin` + `StrategicPartnership`, not two rows of the same phrase). `Neutral`/
   `Mixed`/`Negative` contributions do not count toward corroboration.

Rationale for the label + reason string (advice-free, AD-9), e.g.:
`"Opportunity {opp} below {WatchOpportunity} but {n} corroborating positive signal types across an under-followed
name; floored to Watch (not Ignore)."`

New `const`s (documented): `MinCorroboratingSignalTypes = 2`. Keep the existing thresholds unchanged. This is a
**floor only** — it never changes an `Investigate`/`Watch`/improving/deteriorating/`NeedsMoreEvidence` outcome,
and never lifts above `Watch`.

Bump `Version => "weekly-report-action-v2"` and update the class XML-doc to describe the corroboration floor and
why it is tier-gated (spec-117 consistency).

---

## Project structure changes

- **Modify** `src/Radar.Application/Reporting/ReportActionContext.cs` — add
  `IReadOnlyList<ReportSignalRef> ContributingSignals` and `FollowingTier FollowingTier` (with sensible defaults
  — empty list / `FollowingTier.Small` — so existing construction sites and tests stay compilable/conservative).
- **Modify** `src/Radar.Application/Reporting/WeeklyReportActionPolicyV1.cs` — add the floor branch + `const` +
  `Version` bump + doc.
- **Modify** `src/Radar.Application/Reporting/WeeklyReportBuilder.cs` — build `signals` **before** `Decide`
  (reuse the existing `BuildSignalRefsAsync` result — do not fetch twice), and pass `signals` + `c.Company.FollowingTier`
  into the `ReportActionContext`.

---

## Tests

- **`WeeklyReportActionPolicyV1Tests`** (extend):
  - Under-followed (Small) + `TrajectoryScore >= 50` + 2 distinct Positive signal types + `Opportunity < 40`
    → **`Watch`** (floored), with a rationale naming the corroboration.
  - Same but **Mega** tier → stays **`Ignore`** (tier gate; spec-117 preserved).
  - Same but only **1** distinct Positive type (or two rows of the **same** type) → stays **`Ignore`**.
  - Same but `TrajectoryScore < 50` → stays **`Ignore`** (net-positive gate).
  - Floor never fires when `Opportunity >= 40` (still `Watch` by the normal branch) or `>= 60` (`Investigate`),
    and never overrides thin-evidence `NeedsMoreEvidence` or improving/deteriorating.
  - `Version == "weekly-report-action-v2"`.
- **`WeeklyReportBuilderTests`** (extend/adjust): the builder passes the contributing signals + the company's
  `FollowingTier` into the context; a corroborated under-followed low-opportunity company now surfaces as
  `Watch` (was `Ignore`); `BuildSignalRefsAsync` is invoked once (no double fetch); ordering/cap unchanged.
- Existing report/policy/builder tests stay green. Full gate: `dotnet build Radar.sln -c Release` and
  `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Pure policy (no clock/I/O); corroboration measured from the **contributing** signal
  set (the score-evidence links), preserving provenance (report → snapshot → signals/evidence).
- **No scoring change**: no `_formula.Version`, no `RuleSetVersion`, no weight/tier/collector change, **no
  `ScoringConfigVersion` fingerprint move**. Labels are not hashed. Do not add a scoring dependency.
- **Floor only, tier-gated** — never upgrades above `Watch`, never fires for Large/Mega (spec-117 preserved).
- **AD-9**: advice-free rationale; allowed labels only.
- Keep changes scoped to the reporting label path. Do not implement unrelated features.

---

## Acceptance criteria

- [ ] `ReportActionContext` carries the contributing signal refs + the company `FollowingTier`; the builder
      populates both (signals built once, before `Decide`).
- [ ] `WeeklyReportActionPolicyV1` floors an under-followed (Small/Mid), net-positive, corroborated
      (≥2 distinct Positive signal types) company from `Ignore` up to `Watch`, and does so **only** in that case;
      `Version` bumped to `weekly-report-action-v2`.
- [ ] Large/Mega names and single-type / non-positive cases still label `Ignore` (spec-117 posture preserved).
- [ ] No `_formula.Version` / `RuleSetVersion` / weight / collector change; `ScoringConfigVersion` fingerprint
      unchanged (AI-OFF `8f4b59efd288` / AI-ON `2ef5ef96cce2`).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.
