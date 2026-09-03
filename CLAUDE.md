# Orchestrator Agent

You are a senior engineering lead coordinating a coding pipeline. When given a task,
you follow the steps below precisely.

---

## Configuration

REVIEW_LOOPS=3

---

## Step 0 — Spec Selection (if working from docs/next/)

If you are given a spec file from `docs/next/`, or if you are started without a task:

1. Check `docs/next/` for pending spec files
2. If in a worktree, look for specs assigned to this worktree (check the `Worktree:` field in the spec's Assignment section)
3. Read the full spec file — this IS your task description
4. Proceed to Step 1 (Plan mode) using the spec as your task

If no specs are assigned to your worktree, or `docs/next/` is empty, wait for a task from the user.

> Note: `docs/next/` is tracked and committed, so specs are visible in every worktree after
> the Step 1/2 reset to `origin/main`. Completed specs are promoted into `docs/` — see Step 4.

---

## Step 1 — Plan mode

**Before doing anything else**, fetch the latest code from origin:

```
git fetch origin main
git log --oneline HEAD...origin/main
```

If there are commits you don't have, reset to origin/main before proceeding.
Plans must be based on the current state of the codebase, not stale local state.

Once you have the latest code, produce a written plan covering:

- Which files will be changed and why
- The approach you intend to take
- Edge cases or risks to consider
- Any unknowns that need resolving first

Present the plan clearly and **wait for explicit approval** before proceeding.
Do not create a branch or make any changes until the plan is approved.

**Unattended exception:** if your task states you are running unattended/headless (e.g.
dispatched by `scripts/run-next.ps1` via `claude -p`), do **not** wait for approval — record the
plan in your output and proceed automatically through Steps 2–4.

---

## Step 2 — Create a branch

Once the plan is approved:

1. Detect if you are in a worktree:
   ```
   git rev-parse --git-dir
   git rev-parse --git-common-dir
   ```
   If these differ, you are in a worktree.

2. Fetch latest from origin:
   ```
   git fetch origin main
   ```

3. Reset the current branch to origin/main:
   - In the main repo: `git checkout main && git reset --hard origin/main`
   - In a worktree: you are already on `<worktree-folder-name>-main` (e.g.,
     `radar-claude-2-main`). Do NOT checkout `main` — it is checked out
     by the main repo. Instead, reset the current branch directly:
     ```
     git reset --hard origin/main
     ```

4. Create the feature branch (kebab-case, max 5 words):
   ```
   git checkout -b feature/<short-task-description>
   ```

---

## Step 3 — Review loop

**The reviewer step must not be skipped under any circumstances**, even for small or seemingly trivial changes. No code proceeds to Step 4 without an explicit APPROVED from the reviewer.

Delegate the full task description to the `radar-coder` sub-agent.
The loop runs for a maximum of **REVIEW_LOOPS** iterations:

```
iteration = 1

while iteration <= REVIEW_LOOPS:
    1. radar-coder sub-agent implements (or fixes) the changes
    2. radar-code-reviewer sub-agent reviews the changes
    3. If reviewer returns APPROVED → exit loop
    4. If reviewer returns ISSUES FOUND → pass issues back to coder, iteration += 1

If loop ends without APPROVED → stop, report to user, do not commit
```

---

## Step 4 — Finalise

When the reviewer approves:

1. Stage and commit:
   ```
   git add -A
   git commit -m "<type>: <short description of what changed>"
   ```
   Commit types: `feat`, `fix`, `refactor`, `test`, `chore`, `docs`

2. Push the branch:
   ```
   git push origin <branch-name>
   ```

3. Open a pull request:
   ```
   gh pr create \
     --title "<concise task summary>" \
     --body "<what changed, why it changed, and any reviewer notes>"
   ```

4. If working from a spec in `docs/next/`:
   - Move the spec file from `docs/next/` to `docs/`:
     ```
     git mv docs/next/<spec-file>.md docs/<spec-file>.md
     git commit -m "docs: promote completed spec to docs/"
     git push origin <branch-name>
     ```
   This marks the spec as completed and keeps `docs/` as the source of truth.

---

## Project-specific overrides

Add any repo-specific instructions below this line (e.g. test commands, framework
conventions, branch naming rules). These take precedence over the general rules above.

### Project Radar

Radar surfaces public companies whose business trajectory may be improving before the
market notices. It is a research assistant, **not** a trading bot or recommendation engine.

> Signals before stories. Evidence before opinions. AI assists. Humans decide.

Reference specs (master/reference — do not implement directly, plan from them):

- `docs/radar-full-pipeline-spec.md` — architecture and pipeline stages
- `docs/radar-schema-spec.md` — domain records and persistence schema
- `.claude/agents/radar-philosophy.md` — principles and allowed output language

### Running "run next"

`scripts/run-next.ps1` is the canonical entry point for picking up the next pending spec.
When the user says **"run next"** / **"run next now"** in an interactive session, do NOT run
the Step 0–4 loop in this session — invoke the script, which resets a clean worktree and
dispatches a headless, unattended claude to implement the next spec:

```
powershell -File scripts/run-next.ps1 -CopilotReview
```

`-CopilotReview` makes the script wait for Copilot's first PR review and dispatch a follow-up
fix pass for its inline comments. The headless session the script launches is the one that
follows Steps 0–4 above; this interactive session only launches, monitors, and reports its
outcome.

### Running the app live (`run-radar.ps1`) — measurements & scoring experiments

`scripts/run-radar.ps1` runs the **Worker itself** for a live measurement (distinct from `run-next.ps1`,
which dispatches the coder/reviewer pipeline). Config comes from named JSON profiles under
`scripts/run-profiles/`:

```
powershell -File scripts/run-radar.ps1                     # 'default' profile = the canonical baseline run
powershell -File scripts/run-radar.ps1 -Profile low-media  # an experiment: overlays a delta on default
powershell -File scripts/run-radar.ps1 -Profile low-media -WhatIf   # print the resolved --Radar args, don't run
```

- **`default.json` captures how we run** (the **7** collectors — rss/sec/usaspending/newssearch/secform4/sec13dg/**fda** — + DeepInfra DeepSeek-V4-Flash for the AI earnings-read, spec 119; `-Profile` can route back to local Ollama. The baseline therefore requires `DEEPINFRA_API_KEY` in the environment — see the key-handling note under "Running the app live"). Every other profile is loaded
  **on top of** it and carries only its delta (e.g. `low-media.json` overrides `MediaReachWeight`) — so the
  baseline is never lost and experiments are minimal diffs. This pairs with the config-driven `ScoringWeights`
  (a profile can set `Radar:Scoring:Profiles:{name}:*`), which the snapshot fingerprint (AD-10) then stamps.
- **A named profile writes to `data/experiments/<profile>/`** (baseline `data/` is untouched), so runs are
  comparable side-by-side.
- **SEC User-Agent is not committed** (public repo): pass `-SecUserAgent "Name email"` or set
  `$env:RADAR_SEC_UA`; the placeholder default deliberately 403s so a missing UA fails loudly.
- The script supplies all `Radar:*Directory` overrides itself (they default to relative paths that would
  otherwise write cruft under `src/Radar.Worker/`).

### Tech stack

- Target framework `.NET 10` / `net10.0`, C# 14.
- ASP.NET Core / Worker Service. Persistence is the insert-only FILE store (AD-8, `docs/radar-schema-spec.md`); PostgreSQL/Dapper are NOT wired and no such package exists (stale line corrected 2026-08-29 by the spec-201 audit).
- AI behind `Microsoft.Extensions.AI` via application interfaces.

### Build & test gate

Every task must leave the solution buildable and testable. Before handing back
(applies once `Radar.sln` exists — created by the solution-skeleton task):

```
dotnet build Radar.sln -c Release
dotnet test Radar.sln -c Release --no-build
```

Do not hand back broken code.

### Architecture rules (must hold)

- **Provenance is sacred.** Evidence is the source of truth. Signals must reference
  evidence; scores must trace back to contributing signals and evidence; reports must
  reference score snapshots and evidence. A score without evidence is invalid.
- **Nothing may be discarded without being counted.** If a code path drops, collapses, caps, skips,
  supersedes, defaults or fails a thing, the count is surfaced — on a record, in an artifact, or as an
  aggregated log line (one per company/store/cohort, never one per item). A silent `continue`, a swallowed
  `catch`, a discarded `bool` outcome, a `?? 0` over a nullable-meaningful value, or a `Take(n)` whose
  remainder is invisible are all defects, **including in log lines and rendered text** — a defaulted zero
  must never render as a measured zero, and `null` means "not recorded", never `0` or `false`. This is
  "provenance is sacred" pointed the other way: provenance says know where a number came from, this says
  know what did not make it in. Found the hard way — specs 191/192/193/194 each closed one instance
  (findings binned unread over a rationale's length; a failed durable write reported as stored; signals
  superseded with no trace; syndication collapsed uncounted).
- **No measure ships without its live distribution.** A spec that introduces or materially changes a score
  component, classifier, weight table or threshold must report what that thing actually produces **across
  the live universe** — the distribution, not a unit-test fixture — and the reviewer must check it. **A
  measure that comes out near-constant is a defect even when the code is perfect**, because a near-constant
  discriminates nothing while looking precise. The failure this exists to prevent: rigour about internal
  consistency (fingerprints, immutability, mutation-proven tests) with none about external validity, so a
  number can be provably correct and mean nothing. Measured instances: `MediaAttention` was 98.4% Neutral,
  so news reached scoring as pure volume (spec 191/194); `AttentionScore` classified 25 of 368 live
  publishers and came out 73.4 ± a few for every company, discounting every score by an unvalidated
  constant (spec 196). Both shipped green and were only found by looking at live data.
- **A REVERSAL must be recorded where the original claim lives.** Adding a note is not enough; the failure
  mode is not a missing statement, it is a surviving one. When a slice **withdraws, supersedes, replaces or
  changes direction on** earlier work — including a strategy, a default, a constraint or a decision that
  merely *stopped being true* — amend the ORIGINAL bullet **in place** with the supersession, rather than
  appending a second bullet beside it. Two bullets that disagree are worse than one that is stale, because
  a reader acts on whichever they find first. Concretely, all of these were live in this file and each was
  written correctly at the time:
  - a bullet describing types that a later spec **DELETED** (spec 191's read seam, after 194 removed it);
  - "a genuinely NEW structure still earns `radar-formula-v11`" — after v11 was **taken** and persisted, so
    following the instruction would have re-meant an existing strategy's stamps;
  - three "**CURRENT values**" pin tables and a live "verify the first run reports `…`" imperative, five
    moves stale — an agent greps that phrasing and acts on it;
  - "NO NEW STRATEGIES", which was a **scope fence inside one spec** (166: "this batch adds observations for
    the EXISTING ten arms") and hardened into a standing prohibition that blocked real work. See
    [[radar-constraints-arent-rules]] — before citing any constraint, find its origin.

  Three mechanical rules, because judgement alone has already failed here:
  1. **Never duplicate a value that code defines.** Fingerprint pins, version tokens, counts, defaults and
     limits must CITE where they live (`ScoringConfigFingerprintTests`, `ScoreFormulaVersions.cs`,
     `default.json`) rather than being copied. A copied value is a fact with no owner and it goes stale
     silently. Quoting one as *history* is fine — quoting one as *current* is not.
  2. **Scope a per-slice claim to its slice.** "The pins do not move" and "no fingerprint input changed" are
     true OF THAT SLICE and read as standing guarantees. Write "spec N moved nothing" — never a bare
     present-tense guarantee.
  3. **A false doc claim is a BLOCKING review defect, not a nit.** `radar-code-reviewer` must verify the
     claims a slice touches against the code, and has already blocked three merges this week on exactly
     that (a doc asserting an unachievable prompt property; `default.json` claiming news contributed no
     direction while 24 signals said otherwise; a lineage block pointing the operator at the wrong pin
     pair). This is the enforcement — the rest is guidance.

  The same applies to the reference specs (`docs/radar-full-pipeline-spec.md`, `docs/radar-schema-spec.md`,
  `.claude/agents/radar-philosophy.md`): CLAUDE.md tells planners to **plan from them**, so a stale
  structural claim there misdirects work at design time, which is worse than at implementation time. Both
  were found (2026-08-29) to describe a single-pass single-strategy pipeline with no news reading and no
  `RunMode`, and to recommend implementing **two interfaces that never existed**.
- **Layering:** `Radar.Domain` references nothing; `Radar.Application` references Domain;
  `Radar.Infrastructure` references Application + Domain; `Radar.Worker` references
  Application + Infrastructure. Nothing references Worker.
- **No provider SDK leakage.** No class outside `Radar.Infrastructure` may call a specific
  AI provider SDK directly. Use provider-independent application interfaces.
- **Reuse over copy — extract shared primitives, do not paste a second copy.** When a slice
  needs a helper/primitive that a sibling reader/collector/store already has (a feed-token
  parser, a URL/HTTP builder, a company-hint/quality mapper, JSON/file scaffolding, etc.),
  **extract it into a shared type and route both call sites through it** — do not copy it. The
  established shared homes: `Radar.Infrastructure.Sources` (e.g. `CollectorCompanyHints`,
  `QueryFeedTarget`), the SEC helpers `SecEdgarUrls`/`SecHttpFetch`, `EvidenceMetadata`,
  `RadarFileStoreJson`, `GracefulFileWriter`. Duplicated primitives silently drift (only one
  copy gets the next fix), and the `radar-architecture-reviewer` has flagged exactly this as a
  recurring MEDIUM (specs 76, 77, 83). Keep genuinely per-source behaviour (e.g. a title-suffix
  strip that applies to one source only) as an explicit per-caller hook rather than forcing it
  into the shared type — share the common core, not the divergent edges.
- **Scoring weights live in config.** Tunable magnitudes/weights live in config (`Radar:Scoring` profiles
  bound onto `ScoringWeights`); the formula *structure* (component shape, direction-sign semantics) stays
  versioned code (`radar-formula-vN`). Don't add a new formula class to change a number — edit/add a profile.

### Per-spec architecture history (MOVED — read it before planning)

The per-spec decision bullets that used to live here (specs 137→199: the strategy-decoupling
arc, replay, formulas v9–v11, the news typing/judgment arc, six fingerprint moves, the
universe expansion) now live **verbatim** in `docs/architecture-history.md`. They remain the
authoritative record:

- **Planners (Step 1, `radar-work-planner`) and reviewers MUST read the bullets for any
  subsystem a slice touches** before trusting a plan or approving a change — a stale or
  unread claim there misdirects work at design time.
- **The REVERSAL rule above applies to that file identically**: amend the original bullet in
  place, never append a contradicting sibling.
- **New spec bullets are appended THERE, not here.** This file gains a bullet only for a
  genuinely standing rule.

### Standing facts distilled (verify against the cited source, never against this list)

- **Scoring is plural; collection is not** (specs 137/144): N strategies over ONE collection
  pass; `Radar:RunMode` ∈ full|collect|score|replay; exactly one stage-6 scoring loop;
  replay ⊆ forward field-for-field (spec 139). `Radar:PrimaryStrategy` is storage/series
  identity only; user-facing prominence follows the LEAD arm from
  `data/strategy-operating-calls.json` (spec 184); the journal is `docs/strategy-lifecycle.md`.
- **The repository IS the file store** (spec 142): scoring reads accrued durable history, and
  evidence identity is content-derived (spec 145). **Accrued history is never backfilled,
  regenerated or rewritten** (AD-8/AD-1) — every fix heals forward only.
- **Formulas are a closed set**: `ScoreFormulaVersions.All` (v8 default; v9/v10/v11 channel
  formulas; the `radar-baseline-activity-v1` control). v11 and the control are TAKEN — a new
  structure earns **v12**. An in-place composition change bumps
  `IScoreFormula.CompositionRevision` (spec 153); a strategy that changes formula or weights
  gets a NEW NAME (spec 141, immutable-by-convention).
- **Fingerprint pins are window-dependent and have moved six times in three weeks** (191,
  194 ×2, 196, 197, 198). `ScoringConfigFingerprintTests` is the ONLY authority for current
  values — never trust a pin quoted in prose. The three windows (30d unit pins / 60d live
  baseline / 120d `long-window`) are three correct answers — never reconcile them onto one
  value. After a pin move, `StrategyIdentityGuard` halting before collection is CORRECT: the
  remedy is consciously deleting/re-recording `data/scoring-configs/strategies/{name}.json`
  (git-ignored — NEVER fabricate one), then verifying the first run's stamp.
- **Do not pool across regime boundaries**: pre/post spec 191 (news direction), 194
  (grounded judgment signals), 196 (attention tiers), 197 (judgment join), 198 (news
  recency). The spec-191 inherited-direction cohort is known DEFECTIVE and is not a control.
- **News is a two-stage read** (specs 177–198): stage-1 typing (facts, structurally no
  direction) → stage-2 judge (cited `BusinessTrajectory`) → ONE judgment-derived
  `MediaAttention` signal per judgment (`news-judgment-signal-v2`), which supersedes the
  ordinary attention event for its evidence; every leaders row carries the mandatory
  semantic-read marker. All of it is hashed into `ScoringConfigVersion` via the `news=` and
  `newsquery=` segments (specs 194 §2 / 198 §3), so a `score`/`replay` pass needs the same
  news/judgment config validated as a `full` run.
- **The universe is 102 companies** (spec 207; 59 `small` — spec 199 took it 74 → 94, spec 207
  94 → 102); `benchmark-universe-v1` stays frozen at 74 members — additions report
  `NotInBenchmarkUniverse` until a prospective v2 is declared. Pooled efficacy is
  benchmark-adjusted; the paired AD-15 path deliberately is not (spec 183).
- **Owed follow-ups**: spec 200 Phase B is DONE (2026-09-03; capacity verdict DRAINING; spec
  200 promoted to `docs/`). Still owed: (i) the mature 60-day attention read of the 20 spec-199
  additions — the first successful run with `WindowEndUtc` ≥ 2026-10-28T21:44:52Z, descriptive
  only, no gate (the cold-start read carried the spec 200 §4 caveat); (ii) a spec to stop
  `FileNewsTypingArtifactStore`'s date-keyed `attention-decomposition-{asOfDate}` artifact
  silently overwriting an earlier same-day run (run 3 of spec 200 §5 lost its durable typing
  accounting this way); (iii) the spec-207 three-run retrospective (predicted-band vs measured
  `AttentionScore` for the eight AI-robotics additions, plus the post-spike `untypedRemaining`
  drain check using spec 200 §5's arithmetic) owed in `docs/cohorts/ai-robotics-2026-09.md`
  after three successful post-207 full runs — descriptive only, no removal/re-tier/feed tuning.

### General conventions

- Prefer deterministic code before AI. Use typed records and validated structured outputs.
- Store all timestamps in UTC. IDs are `Guid` unless there is a strong reason otherwise.
- AI outputs must be typed and validated before persistence. If AI confidence is low,
  persist the evidence but do not create high-confidence signals.

### Output language (hard rule)

Radar must never produce financial advice. Do not emit "buy", "sell", "guaranteed upside",
or "safe bet". Allowed labels only: `Investigate`, `Watch`, `Ignore`, `Needs more evidence`,
`Thesis improving`, `Thesis deteriorating`. (`Ignore` was re-added per the collector-driven
master spec — see AD-9. The five non-`Ignore` labels remain valid.)

### Sub-agents

- **`radar-coder`** — implements specs (the coder in the Step 3 loop).
- **`radar-code-reviewer`** — reviews code changes; returns `APPROVED` or `ISSUES FOUND`
  (the reviewer in the Step 3 loop).
- **`radar-work-planner`** — splits master specs into small implementation specs in
  `docs/next/` (planning, not part of the per-task loop).
- **`radar-architecture-reviewer`** — read-only, ad-hoc audit of the *whole* codebase for
  cross-slice drift (layering, DI/naming/error-handling consistency, duplication, provenance
  erosion). Run every few merged slices to checkpoint the trunk; not part of the per-task loop.
- **`radar-signal-reviewer`** / **`radar-skeptic-reviewer`** — *runtime pipeline* reviewers
  that judge extracted signals and emerging theses for evidence quality and hype. These are
  domain reviewers invoked inside the Radar pipeline, **not** the Step 3 code-review loop.

### Spec implementation checklist

When implementing a spec that replaces existing functionality:

1. Identify all code paths being replaced.
2. Update or remove tests for the old code paths.
3. Ensure tests exercise the new production path.
4. Delete deprecated code rather than leaving it dormant.
5. Update this CLAUDE.md if the architecture changes.
6. A tunable **magnitude/weight** change is now a **config edit** (a new/edited `Radar:Scoring` profile bound
   onto `ScoringWeights`) — it needs **no code version bump**, and the `ScoringConfigVersion` fingerprint
   re-stamps automatically. The only remaining code-version obligation is bumping `_formula.Version` (a new
   `radar-formula-vN` class) when the formula **structure/shape** changes (AD-6). See AD-10 (as amended).
   Since spec 146 a new class must also be added to `ScoreFormulaVersions.All` and dispatched in
   `RadarScoreFormulaFactory` — that list is the closed set of shippable formulas, so config can only pick
   between structures the maintainer wrote, never define one. **Which** formula a strategy runs is now a
   per-strategy config choice (`Radar:Strategies[i].Formula`, default `radar-formula-v8`); *writing* one is
   still code. ⚠ **CURRENT SHIPPED SET (verify against `ScoreFormulaVersions.cs`, not this line):
   `All = { radar-formula-v8, v9, v10, v11, radar-baseline-activity-v1 }`. `v11` and the
   `radar-baseline-activity-v1` CONTROL are both TAKEN — a new structure earns **v12**, not v11.** Two
   predicates, not one, and both are read by `ScoringStrategySet` AND the factory so they cannot drift:
   `ConsumesChannels` is true for **v9, v10, v11 and `radar-baseline-activity-v1`**; and
   `RejectsBreadthChannels` is true for **v11** — a v11 strategy declaring a breadth channel FAILS AT
   STARTUP. The baseline control is deliberately NOT numbered in the `vN` lineage. **And an in-place change to
   an EXISTING formula's composition now has a mechanism**: `IScoreFormula.CompositionRevision` (default
   empty ⇒ nothing changes for v8/v9), composed by `FormulaIdentity.Of` into the hashed + persisted +
   stamped identity. Bump it in the same change that alters a composition — it re-stamps and trips
   `StrategyIdentityGuard`. It is NOT a substitute for AD-6: a genuinely new structure still earns a new
   `radar-formula-vN` class.
7. A scoring-affecting **extractor rule-STRUCTURE** change (the `KeywordSignalExtractor` phrase→direction/strength
   table shape) bumps `KeywordSignalExtractor.RuleSetVersion` (parallel to `_formula.Version`) — it is folded into
   the `ScoringConfigVersion` fingerprint via `SignalSourceDescriptor` (spec 95, AD-10 amended). The
   **enabled-collector set** is captured automatically by that same fingerprint, so enabling/disabling a collector
   needs **no** bump — it re-stamps on its own. The **insider buy/sell materiality tiers + cluster boost** are now
   config too (`Radar:Insider` profiles bound onto `InsiderMaterialityWeights`, default == spec 93) and are hashed
   into that fingerprint **by value** (spec 96, AD-10 amended) — so a tier **magnitude** change is a **config edit**
   needing **no** `RuleSetVersion` bump; only a rule **structure** change bumps `RuleSetVersion`.
