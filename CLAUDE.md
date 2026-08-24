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

- **`default.json` captures how we run** (the 6 collectors — rss/sec/usaspending/newssearch/secform4/sec13dg — + DeepInfra DeepSeek-V4-Flash for the AI earnings-read, spec 119; `-Profile` can route back to local Ollama. The baseline therefore requires `DEEPINFRA_API_KEY` in the environment — see the key-handling note under "Running the app live"). Every other profile is loaded
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
- ASP.NET Core / Worker Service, PostgreSQL, Dapper.
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
- **Scoring is plural; collection is not.** Stage 6 runs **N strategies over ONE collection pass** (spec 137).
  A strategy is `{ Name, ScoringProfile, SignalTypes? }` under `Radar:Strategies`, with
  `Radar:PrimaryStrategy` naming the primary; an absent/empty list synthesises the single
  current-`Radar:Scoring:Profile` strategy, so every existing config is unaffected. One `ScoringEngine`
  instance **is** one strategy (it resolves its config fingerprint once in the ctor) — build them via
  `IScoringStrategyFactory`, never per-call weights. Rules: collection, the AI directional read, extraction,
  resolution and review run **exactly once** — nothing above the scoring stage may run per strategy; the
  **primary** writes to the existing scores path (and the shared `IScoreRepository`) and is the series the
  weekly report renders, while non-primary strategies get their own repository instance and a
  `strategies/{name}/` scoped path; and `StrategyName` (on `CompanyScoreSnapshot`, trailing + nullable,
  `null` ⇒ primary/legacy) is **not** a fingerprint input. A strategy may additionally declare
  `Radar:Strategies[i].SignalTypes` — the `SignalType`s it consumes (spec 138), its *hypothesis* as opposed
  to its magnitudes; omitted/empty/exhaustive all canonicalise onto `SignalTypeFilter.All`, so "all types"
  is byte-identical to the default and the pins do not move. Unlike `StrategyName` it **is** folded into
  that strategy's fingerprint (the engine composes `filter.Describe(sourceDescriptor.CanonicalDescriptor())`,
  so the gate and the hashed identity cannot drift), and it is applied **after** the spec-136 point-in-time
  read predicate and the spec-85/113 dedupe — to **both** the current and the previous (velocity) window —
  as a pure membership gate: nothing is deleted, evidence chains for consumed signals are intact, and a
  strategy that consumes zero signals gets the same neutral zero-evidence-link snapshot a zero-signal
  company already gets. ~~Known coupling, not yet fixed: `SignalSourceDescriptor` still folds the
  enabled-collector set into every strategy's fingerprint~~ — **FIXED by spec 141**, see the next bullet: the
  collector set is out of the hash entirely and enabling a collector no longer re-stamps any strategy.
- **Strategy identity is the NAME; the fingerprint is a tripwire; collection provenance is recorded, not
  hashed (spec 141).** AD-10 conflated *stamp the config correctly* (kept) with *the stamp must never change*
  (dropped — it had already moved 17 times over 851 live snapshots, largest cohort ≈ 3 runs, the pinned AI-ON
  value exactly **one** run). Rules:
  - **`ScoreSeriesKey` is the ONE definition of the series key**: `snapshot.StrategyName`, `null`/blank ⇒
    `"default"`, compared case-insensitively (matching `ScoringStrategySet`'s uniqueness rule). Both consumers
    route through it — the weekly report's comparability gate and the spec-101/108 efficacy segmentation — so
    a legacy `null`-named snapshot reads as the primary series instead of being orphaned, and a fingerprint
    re-stamp *within* one strategy no longer renders "(scoring updated)" or shreds the efficacy line. The
    efficacy SVG still draws the dashed fingerprint-boundary tick: the stamp stays visible provenance, it just
    stops breaking the line.
  - **A strategy is IMMUTABLE BY CONVENTION** — to change one, add a new name (`momentum` → `momentum-v2`).
    `StrategyIdentityGuard` enforces it at the very start of `RadarPipelineRunner.RunAsync` (before Stage 1,
    so a misconfiguration costs no collection), comparing each strategy's computed fingerprint against the
    per-NAME record at `data/scoring-configs/strategies/{name}.json` — a mutable upsert record living *beside*
    the immutable content-addressed `{fingerprint}.json` files, never inside them. No record ⇒ record and
    continue; equal ⇒ continue; different ⇒ throw naming the strategy, both fingerprints and the remedy. A
    read failure degrades to "unrecorded" and never trips (AD-8) — "cannot tell" must not read as "changed".
  - **`ISignalSourceDescriptor` has two members**: `CanonicalDescriptor()` = strategy identity
    (`rules=…;[ai=…;]`, the fingerprint input) and `CollectionProvenance()` = `collectors=<csv>;`, stamped
    verbatim on `CompanyScoreSnapshot.CollectionProvenance` (trailing + nullable) and **hashed into nothing**.
    It is deliberately NOT added to `EffectiveScoringConfig`: that store is content-addressed and
    insert-if-new, so a per-run fact stored there would be pinned forever to whichever run wrote the file
    first. The `ai=` segment stays on the identity side — it carries per-signal magnitudes and the reading
    model, which change signal DIRECTION (spec 119).
  - **Scores are byte-identical; only stamps move.** Asserted: two engines differing solely in the enabled
    collector set stamp the SAME `ScoringConfigVersion`, DIFFERENT `CollectionProvenance`, and identical
    components/explanation/component JSON/evidence links.
  - **The pins MOVED, deliberately, and that move IS the deliverable**: AI-OFF
    `radar-scoring-fp-6b2f468041b9 → radar-scoring-fp-2ce20f8fc497`, AI-ON
    `radar-scoring-fp-57356123e09b → radar-scoring-fp-3457da53489d` (both **superseded by spec 148**, which
    moved them again for its own reasons — see the spec-148 bullet for the current values). No `_formula.Version` bump, no
    `RuleSetVersion` bump, no weight edit. `ScoringConfigFingerprintTests` documents the pins as
    **change-detectors**: moving one is a normal, intended act that requires a conscious update plus a lineage
    note — not "scope leakage". History was **not** regenerated (the spec permits taking the discontinuity);
    nothing was rewritten, deleted or backfilled.
- **Replay is read-only and never forks the scoring path.** `Radar:Replay:Enabled` (spec 139) turns a run into
  a read-only OFFLINE replay *instead of* a pipeline run: it scores the configured strategies across a
  `From`/`To`/`Step` series of historical as-of instants by calling the **same** `ScoringEngine` with a past
  `windowEndUtc` — no second copy of the scoring logic, no collection, no AI read, no report, no price
  (AD-14). It is honest only because spec 136's `CreatedAtUtc <= windowEndUtc` predicate is load-bearing, so
  the replay tests assert that predicate rather than trust it. **The hard invariant is replay ⊆ forward:** a
  replay at as-of D reproduces the forward snapshot at D field-for-field (excluding the per-call minted
  snapshot/link `Guid`s, which forward runs mint too). Replay writes ONLY under its own
  `Radar:ReplayDirectory` root (`{root}/{label}/strategies/{name}/{companyId}/{asOf}.json`, as-of-named so a
  re-run overwrites in place ⇒ idempotent); every strategy — **including the primary** — gets an isolated
  score repository, so the shared repo the weekly report renders and the spec-101/108 forward series are
  never touched. No new fingerprint input; the pins do not move. ~~Known gap~~ **CLOSED by spec 142** — see
  the durable-read-path bullet below: the repositories now hydrate accrued history and the raw-evidence
  schema carries `EvidenceQuality`, so replay finally has something to replay.
- **The repository IS the file store — scoring reads accrued history (spec 142).** Before 142 there were two
  disconnected abstractions over the same facts: `ISignalFileStore`/`IRawEvidenceStore` owned the durable
  format, while `ISignalRepository`/`IEvidenceRepository` resolved to in-memory singletons that started
  **empty every process** — so scoring had *never once* read accrued history, which made spec 136's
  point-in-time predicate near-vacuous and spec 139's replay inert. The recorded reconciliation choice is
  **(b): `FileSignalStore` additionally implements `ISignalRepository` and `FileRawEvidenceStore`
  additionally implements `IEvidenceRepository`** — no third abstraction, no second copy of the persisted
  shape (one record definition, one deserializer, one skip-don't-throw rule set, one hydration cache). Each
  is registered ONCE as a concrete singleton and exposed under both interfaces;
  `AddDurableRadarSignalHistory()` (called from `RadarWorkerServices`, **no config toggle**) `RemoveAll`s the
  in-memory registrations and repoints both interfaces at those same instances. The in-memory repositories
  **stay, unchanged, for tests**, and `AddFileSignalStore`/`AddFileRawEvidenceStore` still do exactly what
  they did. Rules:
  - **Hydration is lazy** (never in the ctor), once per instance, thread-safe, and `TryAdd`-only, so a
    signal/item this process wrote always wins over its own on-disk copy. Writes update disk **and** the
    index, so a write is immediately visible to a later read in the same process. A malformed file is logged
    and skipped, never thrown; `OperationCanceledException` still propagates.
  - **`ISignalRepository.AddAsync` is index-only, deliberately.** It carries no `SignalReview` and the
    durable format requires one (`WriteAsync` has a review→signal provenance guard), so writing a
    review-less file would either break that guard or invent a review. Durability keeps coming from the
    pipeline's existing `ISignalFileStore.WriteAsync` call right after it — append-only (AD-8) and the
    provenance guard are both preserved.
  - **Cross-run duplicate collapse on every durable list read.** `SignalCrossRunDedupe` is the ONE
    definition of the stable identity `(CompanyId, EvidenceId, Type, Direction)` (spec 85's key, extracted),
    shared by `ReadApprovedInWindowAsync` and the repository reads. Survivor rule differs by call site *and
    that difference is load-bearing*: the window read collapses **lowest `SignalId`** because it has already
    applied the known-at predicate, whereas `GetByCompanyAsync`/`GetObservedBetweenAsync` collapse
    **earliest `CreatedAtUtc`, then lowest `SignalId`** because `ScoringEngine` applies
    `CreatedAtUtc <= windowEndUtc` *after* the read — keeping a later-created copy would hide, from a replay
    at T, a signal Radar demonstrably knew about at T.
  - **`EvidenceQuality` is now persisted** (`quality`, trailing + nullable) — it is a v8 formula input, and
    hydrating without it would silently score history differently from how it was scored live. Legacy files
    **recover** it from the `metadata.quality` the collector persisted all along, via the shared
    `EvidenceQualityParser` — the *exact* rule `CollectedEvidenceMapper` applied at collection time, so this
    reproduces the real value rather than defaulting. Neither present ⇒ `Unknown`, which is exactly what the
    mapper itself produces for quality-less evidence and whose weight (`QualityUnknown` 0.40) sits **below**
    Medium 0.60 / High 0.85 / PrimarySource 1.00 — it never flatters a score. Legacy null is **never** mapped
    to Medium or higher. `summary` is persisted too (trailing, nullable, omitted when null so real files are
    byte-unchanged) so the round-trip is lossless rather than green by accident, and `sourceType` parses back
    from its snake_case token via a table built *from* the enum (every member round-trips by construction);
    an unparseable value degrades the **file** (log + skip), never the source type, because `SourceType` feeds
    attention breadth/diversity. `EvidenceItem.MetadataJson` is re-composed through the shared
    `EvidenceMetadata.Compose` the mapper authors it with, so the envelope is byte-identical **by
    construction**.
  - **The invariant, asserted:** scoring a window against the hydrated durable store is field-for-field
    identical to scoring the same signals held in memory (excluding the per-call minted snapshot/link
    `Guid`s) — mirroring replay ⊆ forward. No scoring change, no formula bump, **no fingerprint input**;
    the pins do not move.
  - **Real behaviour change:** `AddIfNewAsync` now returns `false` for evidence collected in a **previous**
    run, so re-running collection no longer re-extracts signals from already-seen evidence. That is the
    idempotency the spec asked for, and it changes how a live baseline run behaves.
  - **Measured against the live store (2026-07-26), and it is not comfortable:** 49,454 signals / 6,044
    evidence items; signals span 2006-02→2026-07 (observed) over 44 companies, evidence collected
    2026-06-30→2026-07-26. Only **10.5 % of signals' `EvidenceId`s resolve** on disk. Cause: the mapper mints
    a fresh evidence `Guid` per run while raw files are keyed by `contentHash`, so a re-collected item's new
    id was never persisted while its signals were. The store therefore holds ~9.2× content-equivalent
    duplication that spec 85's key **cannot** collapse (the duplication is in *evidence* identity, not signal
    identity) — Radar is protected from 9× score inflation only by the accident that those duplicates'
    evidence is unresolvable and `ScoringEngine` drops them. ~~Do not backfill evidence without fixing
    evidence identity first (spec 141)~~ — the evidence-identity fix is **spec 145** (not 141, which is
    *strategy* identity), and it is now **done, forward only**: new evidence gets a content-derived id, so
    the duplication stops accruing. What remains true, and is the standing rule: **do not backfill or rewrite
    the accrued 89.5 %.** 145 deliberately left history exactly as it is, so retro-healing resolution would
    still turn the live 30-day window's 1.03× scored set (2,618 signals) into a 4.6× one (12,145). 142 and
    145 both heal going forward and neither touches history.
- **Evidence identity is content-derived (spec 145) — the fix that made spec 85's key non-vacuous.**
  `CollectedEvidenceMapper` minted `Guid.NewGuid()` per run while `FileRawEvidenceStore` path-keyed files on
  `contentHash`, so the id a signal referenced was unrelated to the id the file carried: resolution failed
  (10.5 %) *and* the spec-85 dedupe key `(CompanyId, EvidenceId, Type, Direction)` — which **contains**
  evidence identity — could never collapse identity duplication (measured **1.000×** key-collapse vs
  **9.213×** content-collapse over 49,454 signals). `EvidenceIdentity.ForContentHash` now derives the id from
  the namespaced canonical string `"radar:evidence:" + contentHash`, through the shared
  `DeterministicGuid.FromCanonicalString` that `LocalFileCompanySeedSource` was **extracted onto** rather than
  copied (reuse-over-copy; the seed source keeps its own `companyId|kind|value` canonicalisation, and its
  produced Guids are byte-identical — pinned by value). Rules:
  - **Identity is the normalized title+body hash ALONE.** Explicitly excluded: `CollectedAt`, `PublishedAt`,
    run id, any minted id, collector/source name, source URL (hence every volatile query parameter and
    tracking token), the metadata bag, company hints, and `SourceType`.
  - **Cross-collector: the same content from two collectors is ONE evidence record**, because identical
    normalized content is one *fact* and two collectors finding it is two retrieval paths, not two facts.
    Provenance is **not** collapsed — every contributing source's own raw file stays on disk under its own
    `{sourceTypeFolder}/{yyyy}/{MM}/{contentHash}.json` (insert-only, AD-1); only the identity **index**
    collapses, deterministically by ordinal path order, and hydration now **reports** that collapse in its own
    counter, separate from unreadable-file skips (two counters, not one — the duplication rate and data loss
    mean different things).
  - **Scores do not rise, and the reason is structural, not directional.** "Lower breadth ⇒ lower score" is
    *not* universally true: `OpportunityScore` consumes `AttentionScore` as an **inverse** discount, so a
    lower attention would *raise* opportunity. It is never exercised because `AddIfNewAsync` has always
    rejected a second item with an already-seen content hash, so at most one record per distinct content
    could ever be persisted or resolved — the breadth of a set of identical copies was already exactly 1
    *before* this slice. 145 changes **which id** that one record carries, not how many there are. Asserted:
    N runs over identical content yield ONE scored signal, and the duplicated fixture scores **equal** to the
    single-copy fixture component-for-component. **Measured, not just argued** (spec-139 read-only replay at
    as-of 2026-07-26 over the live store, run on `origin/main` and on this branch): all **43** companies came
    back field-for-field identical excluding the per-call minted snapshot/link `Guid`s — **703** evidence links
    on both sides, **0** components risen, **0** fallen, same `radar-scoring-fp-97207902fd70` stamp.
  - **Accrued history: left as-is, dedupe forward only** (the chosen option). Nothing deleted, nothing
    rewritten, no migration, no backfill, no supersede marker. Legacy evidence keeps its legacy ids and legacy
    signals keep their references, so no historical series moves.
  - **The dropped-signal warning is aggregated, not silenced.** `ScoringEngine` emitted one Warning per
    dropped signal (~9,500 per run **per strategy**); since the legacy residue deliberately survives, it is now
    **one Warning per company** carrying the dropped count *and* the distinct-evidence-id count, with per-signal
    detail at Debug. Measured over the same live replay: **13,625 → 43** warning lines (one per company, a 317×
    reduction) with the total dropped count preserved in aggregate rather than lost.
  - **No fingerprint move**: no `ScoringConfigVersion` input changed, no `_formula.Version` bump, no
    `KeywordSignalExtractor.RuleSetVersion` bump. The pins do not move.
- **The FORMULA is part of the strategy, and a v9 strategy is a weighted array of channels (spec 146).**
  Additive, not a migration: **v8 is untouched and stays the default**, so a v8 strategy and a v9 channel
  strategy run over the SAME collection pass (137) and are directly comparable against price (140).
  `ScoringStrategyDefinition` gains two additive init-only properties — `Formula` (default
  `radar-formula-v8`) and `Channels` (default `ScoringChannelSet.Empty`) — resolved through
  `IScoreFormulaFactory`, whose input widened from bare `ScoringWeights` to the whole definition
  (`RadarScoreFormulaV8Factory` → `RadarScoreFormulaFactory`). **A strategy that names neither is
  byte-identical to before and the pins do NOT move** (the then-current AI-OFF
  `radar-scoring-fp-2ce20f8fc497` / AI-ON `radar-scoring-fp-3457da53489d`, both unmoved *by this slice*;
  spec 148 has since moved both — see its bullet for the current values). Rules:
  - **Why v8 cannot express this**: every v8 component is computed over the signals that ARRIVED, so a
    missing source is invisible; when visible it is *incoherent* (`SignalVelocity` correctly falls while
    `AttentionScore`, an INVERSE discount inside Opportunity, perversely rises); and contributions are
    incommensurable, so a high-traffic source dominates a high-value one.
  - **`score = Σ (weight_c × channelScore_c)`, and THE WEIGHTS ARE NEVER RENORMALISED.** A channel that
    produced nothing contributes 0 with the denominator unchanged — that is the entire point. A strategy whose
    0.50-weight patents channel is dark is down by up to 0.50; a strategy that never declared patents is
    completely unaffected (asserted side by side). **Renormalising the surviving weights is the
    obvious-looking wrong fix** and would erase exactly the penalty the design exists to create.
  - **Channel shapes.** Collector channel: `saturation × directionFactor`, where
    `activity = Σ(strength·confidence·recency·quality)` over signals whose evidence that channel's collectors
    retrieved, `saturation = activity/(activity+S_c)`, and `directionFactor = (1+preponderance)/2` (no
    directional mass ⇒ exactly 0.5). Breadth channel: `reach/(reach+S_c)` over the tier-weighted
    distinct-publisher reach across the whole gated set — **direction-correct in v9: more genuine breadth
    contributes MORE.** v8's inverse *per-component* attention discount stays in v8 and is deliberately not
    carried over. ⚠ **AMENDED BY SPEC 149**: dropping the discount ENTIRELY was the over-correction — v9 now
    applies the shared notedness discount once, to the COMPOSED score, so attention enters v9 twice with
    opposite signs (positive budgetable breadth; negative company-level fame). See the spec-149 bullet.
    **Per-channel saturation is mandatory** (RSS emits constantly, Form 4 rarely; a shared saturation pins the
    chatty channel at 1.0 and makes the weights decorative).
  - **Reuse, not copy**: v8's per-signal machinery (recency, direction sign, quality weight, directional
    masses + preponderance, the [0,100] clamp, the whole Attention reach term) was **extracted** into
    `ScoreSignalMath` and **v8 routes through it**. Every helper preserves v8's original expression shape and
    accumulation order — `Preponderance` takes the `band` as a parameter precisely so `band*(p−n)/(t+k)` is
    not re-associated — because IEEE-754 is not associative and a 1-ULP move can flip a midpoint rounding.
  - **Range reconciliation, verified not assumed**: `ScoreComponents` is five ints clamped to [0,100]. The v9
    composite ∈ [0,1] maps as `composite×100` into **`OpportunityScore`** (what `WeeklyReportBuilder` ranks by
    and the 101/108 efficacy read consumes); the other four keep their exact v8 meanings over the gated set,
    so `WeeklyReportActionPolicyV1`'s Trajectory/EvidenceConfidence thresholds stay valid. `ComponentJson`
    keeps `ScoreComponents`' five properties first and by name (an existing reader still deserializes it) and
    adds the unrounded composite plus a per-channel breakdown.
  - **Collector provenance had to be BUILT first.** The spec assumed channels select on "the recorded
    provenance of each signal's evidence" — but **there was no recorded collector**: `SourceType` is shared by
    several collectors (sec-edgar/sec-form4/sec-13dg all emit `Filing`), `SourceName` is the *feed*, and
    `CollectionResultMerger.Merge` discards per-collector attribution. `CollectionProvenanceMetadata` is now
    the ONE definition of the `collector` metadata key + its reader, stamped at ONE site
    (`RadarPipelineRunner`'s collector loop, **before** the merge — after it the information no longer
    exists), so the twelve collectors are untouched. The bag is **not** an input to evidence identity (145:
    normalized title+body hash alone) nor to `ContentHash`, so no evidence id moves and no `AddIfNewAsync`
    decision changes. Two honest caveats, both tested: legacy evidence has no `collector` key and so is
    consumed by no collector channel (contributes 0 — consistent with never backfilling accrued history), and
    identical content from two collectors is ONE record (145) carrying the ordinally-first collector.
  - **"Ran and found nothing" vs "did not run" is recorded, never scored.** `ISignalSourceDescriptor` gains
    `EnabledCollectors()` — the SAME ordered-distinct projection `CollectionProvenance()` renders, never a
    second answer — threaded onto `ScoringInput.EnabledCollectors` and split per channel into
    `CollectorsRan`/`CollectorsNotRun`. A 0 is a 0 either way (absence of evidence is not evidence); only the
    provenance differs. Hashed into nothing. Under replay this reflects the *replaying* process's collectors.
  - **Fail-fast at startup, every message naming the strategy**: weights outside [0,1] or not summing to 1.0
    (tolerance `1e-9`, and the message carries the ACTUAL round-trip-formatted sum), non-positive saturation,
    blank/duplicate channel names, a breadth channel declaring collectors, a collector channel declaring none,
    an unknown `Formula`, v9-without-channels, channels-without-v9, and — in `ScoringStrategyFactory`, the
    first place the registry is known and forced before Stage 1 by `StrategyIdentityGuard` — a channel naming
    an unregistered collector (matched **exactly**, so a case near-miss also fails rather than silently
    scoring 0).
  - **Identity**: the channel set folds into `ScoringConfigVersion` on the SAME `Describe` chain
    `SignalTypeFilter` uses inside `ScoringEngine` (order: `signalTypes=…;` then `channels=…;`), so the
    composition and its hashed identity cannot drift; the channels are canonicalised by name (config order is
    irrelevant, at runtime as well as in the hash) and escaped through the new
    `DescriptorEscaping.EscapeNested` (a second method, not a widening of `Escape`, because the AI descriptor
    legitimately contains `:` and widening would move the AI-ON pin). Adding a v9 strategy moves **no**
    existing strategy's stamp — asserted.
  - **Out of scope, recorded not built**: replacing/deleting v8, migrating existing strategies onto v9,
    auto-tuning weights to price, per-channel collector *scheduling*, and strategy-vs-price comparison (140).
    Price is never an input (AD-14).
  - ~~**Known pre-existing gap, deliberately NOT fixed here**: `ScoringWeights.TrajectoryCorroborationK` is
    not a `ScoringConfigFingerprint` field, so tuning it re-stamps nothing — for v8 *and* now for v9's channel
    direction factor.~~ — **FIXED by spec 148**, which folded it (and the scoring window) in and moved both
    pins deliberately. See the spec-148 bullet.
- **Collection and scoring are two independently invokable passes; there is still ONE scoring code path
  (spec 144).** `Radar:RunMode` ∈ `full` (default) | `collect` | `score` | `replay`, case-insensitive, unknown
  ⇒ fail fast listing the valid tokens. **`full` is byte-for-byte the pre-144 combined run** — same stage
  order, counters, log line and run record. The point: collection runs daily on its own schedule and writes
  durable evidence + signals; scoring runs separately over whatever has accrued, as often as you like, so
  adding or re-running a strategy costs a scoring pass — no collector runs, hence no SEC fair-access exposure,
  no GDELT/Google-News traffic and no AI spend. **It is not request-free**, though: `Radar:Prices:Enabled` is
  independent of `RunMode` and price acquisition runs OUTSIDE `IRadarPipeline` (AD-14), so with the shipped
  `default.json` (which enables it) a score pass still fetches daily price history per ticker — turn
  `Radar:Prices:Enabled` off on a frequently-repeated score pass. Rules:
  - **The runner was SPLIT, not copied.** `RadarPipelineRunner.RunAsync`'s body is now `ICollectionPass`
    (stages 1–5, incl. the collection-health validation and the after-collection `asOfUtc` capture) +
    `IScoringPass` (the stage-6 strategy × company loop). `RadarPipelineRunner` (combined),
    `CollectOnlyPipelineRunner` and `ScoreOnlyPipelineRunner` all implement `IRadarPipeline` and all compose
    those SAME two types — there is exactly one stage-6 loop in the codebase, and replay (139) still drives
    the same `ScoringEngine`. A second copy would drift and silently invalidate `replay ⊆ forward`.
  - **`StrategyIdentityGuard` (141) stays the FIRST statement of all three runners** — "a misconfiguration
    costs no collection", and for the score pass "…and no snapshot lands under the old name".
  - **A `score` pass registers NO collector — but DOES register the AI seam.** In score mode
    `RadarWorkerServices` registers no collector: CONSTRUCTION is what opens the typed HttpClients, so
    "constructs and invokes no collector" has to mean "is never registered". (It *used* to skip reading
    `Radar:Collectors` entirely — **spec 147 changed that**: the key is now resolved and validated in every
    mode, because it is also the recorded-provenance vocabulary. Only the "at least one" rule is relaxed.)
    The AI block still runs, deliberately, because
    `IDirectionalFilingSignalSource.ScoringDescriptor()` is a `ScoringConfigVersion` input via
    `SignalSourceDescriptor`'s `ai=` segment — omitting it would move the fingerprint and break the
    byte-identical-scores criterion. It is only ever *invoked* by the collection pass, so "no AI read" holds
    structurally. **Operational consequence: a score pass needs the same `Radar:Ai` config (and
    `DEEPINFRA_API_KEY`) — and the same `Radar:Sec:UserAgent`, since the AI seam wires the earnings reader — as
    a collect pass, even though it issues no request.**
  - ~~**Recorded limitation, same class as replay's**: with no collector registered, a score pass's snapshots
    record `collectors=;`… and **a v9 strategy declaring collector channels cannot start up in `score`
    mode**~~ — **BOTH FIXED by spec 147**, see the vocabulary bullet below. A score pass now records the
    CONFIGURED collector set plus a `collection=none-this-pass;` marker, and a v9 collector-channel strategy
    starts and scores in `score` mode with the spec-146 guard unweakened.
  - **A past-dated standalone `score` is refused.** `Radar:Score:AsOfUtc` (blank ⇒ now, parsed through the
    same `AssumeUniversal|AdjustToUniversal` helper the replay bounds use) may not be in the past: `score`
    writes the LIVE series (what Radar thinks now) while `replay` writes the replay-scoped series (what Radar
    *would* have thought). The guard throws before anything is loaded or written and points at
    `Radar:Replay:*`; the boundary is inclusive, so "exactly now" runs.
  - **Reconciled with 139, not a third mechanism**: `Radar:Replay:Enabled` alone still selects replay
    (unchanged, and `run-radar.ps1 -Replay` still just sets it); `RunMode=replay` also selects it (a missing
    range then fails in `BuildReplayPlan`, one message); `RunMode` `collect`/`score` **with**
    `Replay:Enabled=true` fails fast naming both keys.
  - **`collect` writes no score and no report** (even with `GenerateReport` true — it has no reporting stage),
    and its run record carries `Strategies`/`PrimaryStrategy` `null` rather than claiming a scoring that never
    happened. `score` reports zero collection counters, `Collectors: []` and `CollectionWarnings: null`.
  - **Spec 142 is the load-bearing prerequisite** — mutation-proven: without `AddDurableRadarSignalHistory`
    the score pass's container starts empty and scores nothing.
  - **Scripts**: `run-radar.ps1 -Mode full|collect|score` (rejects `-Mode` + `-Replay` itself),
    `run-baseline-scheduled.ps1 -Mode` passthrough, `setup-baseline-task.ps1 -Mode` threaded into the
    registered task. **All three default to `full`, so `RadarBaselineDaily` is undisturbed by this slice** —
    splitting the schedule is an explicit, elevated, maintainer-only step (register `RadarCollectDaily -Mode
    collect` and `RadarScoreDaily -Mode score`, then retire the combined task; the header of
    `setup-baseline-task.ps1` carries the exact commands).
  - **No scoring change**: no new fingerprint input, no `_formula.Version` bump, no `RuleSetVersion` bump; the
    then-current pins (`radar-scoring-fp-2ce20f8fc497` / `radar-scoring-fp-3457da53489d`) do not move *by this
    slice* — spec 148 later moved both.
- **"Can collect" and "is a known collector" are different capabilities; a `score` pass gets the second only
  (spec 147).** 144 and 146 were each correct and did not compose: 144 registers **zero** collectors in
  `score` mode (correct — construction is what opens the HttpClients), and `SignalSourceDescriptor` derived
  everything from the injected `IEnumerable<IEvidenceCollector>`, reading only `CollectorName`. So in score
  mode ⛔ **every snapshot recorded false provenance** (`collectors=;` over evidence seven collectors had
  genuinely gathered — live, and it hit **v8** strategies too, not just v9), a v9 collector-channel strategy
  could not start at all, and the ran-vs-quiet split inverted. Rules:
  - **`EnabledCollectorVocabulary` (in `Radar.Application.Collectors`) is THE ordered-distinct-Ordinal
    projection** — moved out of the descriptor's ctor, handed out behind a read-only wrapper, `FromNames` /
    `FromCollectors` / `Empty`. It **holds strings**: it cannot collect and references nothing that can, so
    144's asserted "a score pass constructs and invokes no collector" is untouched
    (`Assert.Empty(GetServices<IEvidenceCollector>())` in score mode still holds and is non-negotiable). No
    `IConfiguration` in Application. `SignalSourceDescriptor` consumes the vocabulary and no longer reaches
    into `IEvidenceCollector` at all.
  - **ONE kind→collector table in `RadarWorkerServices`**, each entry naming the collector class's own
    `public const string Name` (re-exported publicly as `RadarCollectorNames.*`, since the collector classes
    are `internal`) — so the registration path and the vocabulary path resolve `Radar:Collectors` through the
    same resolver and **cannot drift**. A drifting vocabulary would be *worse* than the failure it replaces:
    the spec-146 guard would pass on a collector that cannot run. Pinned by an anti-drift test that builds a
    provider per kind and compares the vocabulary against the actually-registered `CollectorName`s, plus one
    asserting the fail-fast messages' kind list is rendered FROM the table.
  - **The list is now read in EVERY mode.** Case-insensitive matching, defensive de-dupe, blank entry ⇒ fail
    fast, unknown kind ⇒ fail fast — identical everywhere, and the message text is byte-unchanged. The ONE
    mode-dependent rule is `requireAtLeastOne`, false for `score` only. **Behaviour change:** a blank/unknown
    `Radar:Collectors` entry now fails startup in score mode too (it used to be ignored) — deliberate, because
    that list IS the recorded vocabulary.
  - **Provenance representation (the spec's option B): `collectors={csv};` for a pass that collected —
    byte-identical to pre-147, never a second segment — and `collectors={csv};collection=none-this-pass;` for
    a `score` pass.** Non-empty even with an empty vocabulary (`collectors=;collection=none-this-pass;`), so
    it is unmistakable from the "no collectors configured" form `collectors=;`. Carried by a
    `CollectionPassOptions { CollectionPassKind Kind }` singleton (`TryAddSingleton` default `Collected`, so
    every existing composition is unchanged); the Worker registers `NoCollectionThisPass` for
    `RadarRunMode.Score` **only**.
  - **Replay is deliberately NOT re-stamped.** It registers real collectors and 139's `replay ⊆ forward`
    compares snapshots FIELD FOR FIELD; marking replay would break that invariant. Asserted.
  - **The typo guard is unweakened in every mode** — it now validates against the same vocabulary everywhere,
    and its `(none)` branch survives for the genuinely-no-collectors-configured case. Asserted in score mode
    for an unknown AND a mis-cased name.
  - **§4, stated plainly because it is weaker than it looks: `CollectorsNotRun` is STRUCTURALLY EMPTY in any
    composed run — in every mode, not just `score`.** `ScoringStrategyFactory` validates channel collectors
    against the very list `ScoringEngine` then hands the formula as `ScoringInput.EnabledCollectors`, so once
    startup succeeds nothing can be missing. A channel 0 therefore always means "this window holds no signals
    whose evidence that collector retrieved" — **never an outage**, and it never was (a registered collector
    that failed every fetch is indistinguishable here). Collection HEALTH lives in the collection summary and
    the run record. 147 did not weaken this; it un-inverted it.
  - **No fingerprint move**: `CollectionProvenance` (marker included) is hashed into **nothing**, no
    `_formula.Version` bump, no `RuleSetVersion` bump; the then-current pins (`radar-scoring-fp-2ce20f8fc497` /
    `radar-scoring-fp-3457da53489d`, both since moved by spec 148) do not move *by this slice* and
    `ScoringConfigFingerprintTests` is untouched. Asserted:
    a full-mode and a score-mode graph over the same config stamp the SAME fingerprint and DIFFERENT
    provenance.
  - **Out of scope, recorded not built**: spec 140's strategy-vs-price comparison (which this unblocks), the
    `ScoringWeights.TrajectoryCorroborationK` fingerprint gap (moves both pins — its own spec), and
    backfilling `CollectionProvenance` on existing snapshots (append-only, AD-8: fix forward).
- **Strategies are RANKED against price, downstream of scoring, with the hold-out built into the harness
  (spec 140).** The payoff of the 136–147 arc. `Radar.Application/Efficacy/Comparison/` reads each configured
  strategy's persisted score series, relates each score to SUBSEQUENT price movement, and writes ONE leaderboard
  pair at `data/efficacy/strategy-leaderboard.{csv,md}`. It ranks; it does not act (no auto-promotion — a human
  decides). Rules:
  - **The spec's premise was wrong and the correction is load-bearing: 101/108 emit NO numeric efficacy
    metric.** `EfficacyDatasetBuilder` produces a per-company JOIN of snapshots to the price bar
    *at-or-before* the score date (correct for a chart; a future bar there would be an artefact), rendered as
    an SVG + CSV. There is no correlation and no aggregate number anywhere. So "reuse the 101/108 metric
    definition" means **reuse the join and its inputs** (`ICompanyRepository` /
    `IScoreSnapshotFileStore.ReadAllForCompanyAsync` / `IPriceHistoryStore`) — which this slice does, via a
    single additive `BuildAsync(scoreStore, ct)` overload the existing no-arg one now delegates to, so there is
    exactly ONE join. The forward-horizon **metric is DEFINED here**, because none existed.
  - **`ForwardReturn` is the causality primitive and its guarantee is STRUCTURAL.** Score at D vs
    `(D, D+h]`: entry = earliest bar strictly after D, exit = latest bar within the horizon; fewer than two
    distinct bars, or a non-positive entry price, drops the observation **with a named reason and a count**.
    The only place the bar list is touched is one admission filter whose predicate is `bar.Date > asOf` —
    nothing downstream can reach a bar at or before D. This is the mirror of spec 136's hindsight leak on the
    price side, and it is tested with **poison** at-or-before bars whose two wildly different variants must
    produce byte-identical answers. Price = `AdjClose` (what the SVG plots), falling back to `Close` only when
    the adjusted value is unusable.
  - **The anchor is `WindowEndUtc`, not `CreatedAtUtc`.** `EfficacyPoint` gained a trailing, nullable
    `AsOfDate`; the existing CSV/SVG renderers do not read it and their output is **asserted byte-unchanged**.
    It matters because a spec-139 replay snapshot's `CreatedAtUtc` is the replay process's wall clock (identical
    for every point) while `WindowEndUtc` is the simulated as-of that actually bounded what the score could see.
  - **Metric: Spearman ρ with average ranks, plus a closed-form Fisher-z interval — never a bootstrap.**
    Randomness is forbidden (AD-3), and a resampled interval would make two runs over identical data disagree.
    Every degeneracy is NAMED rather than producing NaN: n < 4 (the interval's floor, `se = 1/sqrt(n−3)`), a
    constant vector on either side, and |ρ| = 1 (a zero-width interval would read as certainty). The score is
    `OpportunityScore` — what the weekly report ranks by, what the efficacy chart plots, and where a v9 channel
    composite lands. **Stated honestly in the rendered output:** observations are pooled across companies and
    dates and are NOT independent, so the interval is optimistically narrow — dispersion, not significance.
  - **Hold-out and honest N are properties of the API, not of the reader's discipline.** ONE chronological
    index partition of the sorted distinct as-of dates across ALL strategies (so every strategy is judged on the
    same calendar, and a date belongs to exactly one side by construction); ranking is computed **inside** the
    harness on the in-sample window only, and the caller receives an already-ordered list — an
    out-of-sample-ranked leaderboard is not expressible. `StrategiesCompared` (the count actually ranked) and
    `DroppedStrategies` (name + machine-readable reason + the counts that triggered it) are **fields on the
    result**, not log lines, and both rendered artifacts state them. Proven by a fixture where the rank-1
    strategy is deliberately the WORSE one out-of-sample.
  - **AD-14 is asserted on the TYPE GRAPH, not on prose.** `EfficacyReadOnlyGuardrailTests` walks the
    transitive closure of `Radar.Application.Scoring` (base types, interfaces, private fields, signatures, every
    generic argument) and fails if any `Radar.Application.Prices`/`Efficacy` type is reachable — mutation-proven
    to catch a `List<PriceBar>` hidden in a private field. A positive control asserts the comparison module DOES
    reach price, so the guardrail cannot pass vacuously, and a third test pins the comparison's scoring
    dependencies to an allow-list of OUTPUT types (`IScoreSnapshotFileStore`, `ScoringStrategyDefinition`,
    `ScoringStrategySet`).
  - **Wiring**: `Radar:Efficacy:Comparison` (`Enabled` default **true**, but only INSIDE the already-opt-in
    `Radar:Efficacy` gate), horizon/hold-out/minimum validated at the config boundary and crossing into
    Application already resolved. It runs in `Worker` right after `IEfficacyReportGenerator` — outside
    `IRadarPipeline`, and skipped entirely by a replay run. With too little history it writes an honest
    "No strategy could be ranked" leaderboard rather than failing. `ReplayLabel` (blank ⇒ the live forward
    series) points it at one spec-139 replay run's per-strategy output instead; because a replay run REPLACES
    the pipeline run and never renders efficacy, that is a deliberate two-process workflow.
  - **Reuse over copy**: `EfficacyCsvRenderer`'s inline CSV escape was **extracted** into the shared
    `CsvField` and both exports route through it.
  - **No fingerprint move, no scoring change**: not one file under `Scoring/`, `Domain/` or `Pipeline/` was
    touched; the then-current pins (`radar-scoring-fp-2ce20f8fc497` / `radar-scoring-fp-3457da53489d`) stand
    *through this slice* — spec 148 moved both afterwards, for reasons of its own.
  - **Out of scope, recorded not built**: auto-promoting the winner, a new price collector, live/streaming
    comparison, and any portfolio/return simulation or trading P&L.
  - ⚠ **AMENDED BY SPEC 152 — a PARTIAL forward window is now its own outcome, and every number the leaderboard
    had printed was mislabelled.** `ForwardReturn` picked the latest bar inside `(D, D+h]` and never checked how
    far it got, so four days of price in a 21-day window produced a four-day return **reported as a 21-day
    forward return** and pooled with complete ones; `observationsWithoutForwardPrice` caught only the
    fully-missing case. Now: `ForwardReturnUnavailableReason.PartialWindow` (appended last) when
    `exit.Date < D.AddDays(h − exitToleranceDays)`, checked after `SingleForwardBar` and **before** the
    price check (coverage is the more informative classification); `TryCompute`'s tolerance parameter is
    **required, with no default**, because a silent default is how this slipped through once. The default is
    **4 calendar days, measured not guessed** over `data/prices/` (43 tickers, 11,153 bars, 2025-07-03→2026-07-27):
    max gap between bars 4 days, max shortfall **3** days over **15,334** genuinely-complete 21-day windows,
    discarding **0.000 %** of them (a tolerance of 1 would discard 16.284 %), worst admitted case still covering
    17/21 ≈ 81 % of the horizon. `ObservationsWithPartialWindow` is rendered as its **own** CSV and markdown
    column and `ObservationsWithoutForwardPrice` keeps its **exact** pre-152 definition — "no price at all" and
    "some price but not the horizon" are different facts. The entry rule `bar.Date > asOf` is untouched and
    asserted with poison bars, including on the `PartialWindow` branch. **Honest consequence, and it IS the
    deliverable:** with ~1 month of price history almost every observation becomes `PartialWindow`, so the
    leaderboard correctly reports "No strategy could be ranked" at h=21 until roughly 2026-08-17. That is the
    right answer, not a regression.
- **The fingerprint is COMPLETE, and replay records the provenance it writes (spec 148).** Two closures, one
  slice, from the `radar-architecture-reviewer` sweep of `main` @ `b9b3f65`. **The CURRENT pins are AI-OFF
  `radar-scoring-fp-0c46e07b94db` and AI-ON `radar-scoring-fp-28226897f97b`** — every "the pins do not move"
  above was true of its own slice and stays true of it; this slice moved them, deliberately, once. Rules:
  - ⚠ **THE PIN IS NO LONGER THE LIVE STAMP, and that equivalence break is this slice's doing.** Every pin
    quoted anywhere above doubled as the value a live baseline run writes, because every hashed input was a
    code default. The window is not: the pins are computed at the `ScoringOptions` **code default of 30 days**,
    which the Worker never uses, while the baseline runs at `Radar:ScoringWindowDays` **= 60**
    (`RadarWorkerOptions`/`appsettings.json`; `default.json` does not override it) and therefore stamps AI-OFF
    **`radar-scoring-fp-4eb2fe5d3cdf`** / AI-ON **`radar-scoring-fp-4da4b5ff6ec9`**. `-Profile long-window`
    (120 days) stamps `radar-scoring-fp-0a7058d94582` / `radar-scoring-fp-81e9fab711f8`. Both sets are correct
    at their own window — do NOT "reconcile" them onto one value. When matching a stamp against
    `data/scoring-configs/strategies/{name}.json` or an accrued snapshot, use the pair for the window that run
    actually used. The operator-facing record lives in `scripts/run-profiles/default.json`'s comment; the pins
    in `ScoringConfigFingerprintTests` are the unit-level change-detector.
  - **Two output-affecting inputs were hashed into NOTHING, and both are now folded.** `ScoringOptions.Window`
    (bound from `Radar:ScoringWindowDays`) bounds the current *and* the previous/velocity window, so a 14-day
    and a 30-day run produce materially different Trajectory/SignalVelocity/Attention — and stamped the same
    `ScoringConfigVersion`. `ScoringWeights.TrajectoryCorroborationK` is v8's Trajectory denominator and,
    since spec 146, v9's channel-direction denominator; it was the ONLY `ScoringWeights` field the fold had
    ever missed. **This was worse after spec 141**: a window edit is an in-place edit to a NAMED strategy,
    exactly the category `StrategyIdentityGuard` promises to catch and structurally could not see, while
    `ScoreSeriesKey` kept both cohorts in one `default` series.
  - **The window is hashed as TICKS, not days.** Ticks is injective over every `TimeSpan` (AD-3); whole-days
    is not, so a 36-hour and a 24-hour window would collide and two genuinely different scorings would share
    one stamp — the precise failure the field exists to prevent. Asserted down to a single tick.
  - **`EffectiveScoringConfig.Window` is trailing and NULLABLE on purpose.** A config file written pre-148 has
    no window field, and reading that absence as `TimeSpan.Zero` would be a FALSE record of a zero-length
    window. `null` means "written pre-148; not recorded". Every new write populates it, so the store's
    descriptor↔fingerprint self-verification still holds.
  - **Completeness is now a REFLECTION GUARD, not a review habit.** `ScoringConfigFingerprintTests` perturbs
    every public `ScoringWeights` property in turn and asserts the fingerprint moves, and pins
    `ScoringOptions`' property set to exactly `{ Window }` — so the NEXT unfolded knob fails the day it is
    added rather than seven slices later.
  - **Scores are byte-identical, MEASURED not argued.** `ScoringOutputStabilityTests` pins one fixture's whole
    output under the real v8 (five components + explanation + `ComponentJson` + the ordered link chain); that
    file was compiled and run against pre-148 `origin/main` and passes there too. No `_formula.Version` bump,
    no `RuleSetVersion` bump, no weight edit, no formula file touched.
  - **Replay had the weakest provenance in the system, on the path Radar is meant to choose a strategy from.**
    `ReplayRunner` took neither `IScoringConfigStore` nor the tripwire, so a replay-only run in a fresh data
    root emitted snapshots whose stamp dereferenced to nothing. It now runs `StrategyIdentityGuard.VerifyAsync`
    as the FIRST statement of `RunAsync` (mirroring all three forward runners — a misconfiguration costs no
    scoring and no snapshot lands under a name whose meaning changed) and `WriteIfNewAsync` once per strategy
    in the outer loop. **Writing the scoring-config store is a PROVENANCE RECORD, not a scoring mutation:**
    replay still mutates no signal/evidence store, still never writes the live scores directory, and
    `replay ⊆ forward` still holds field for field. Asserted, not assumed — the read-only test now names the
    config store as the ONE sanctioned outside write and pins its exact two files.
  - **Same-label overwrite WARNS LOUDLY, aggregated per strategy.** As-of-keyed file names are what make a
    re-replay idempotent and equally what makes it replace an already-ranked series. Decided: warn (failing
    would break the legitimate "re-replay after fixing data" workflow; silence is how a comparison quietly
    becomes wrong). ONE `LogWarning` per (label, strategy) with the count and what it means, following spec
    145's aggregation precedent. Detected where the target path is known — `FileScoreSnapshotStore`, via an
    optional `OnSnapshotOverwritten` probe that the live/forward path never wires — and surfaced through
    `IReplayScoreSnapshotFileStoreFactory.OverwrittenCount`, which is monotonic so the runner takes a
    difference. A NEW label warns nothing, so the recommended remedy demonstrably works.
  - **Part B moved no fingerprint input**: nothing in it touches `Compute`, `EffectiveScoringConfig`'s hashed
    content, or any descriptor. Both pin moves are Part A's, and they happened exactly once.
  - **Out of scope, recorded not built**: M3 (v9 copied v8's EvidenceConfidence/SignalVelocity blocks instead
    of extracting them — real, guarded by a pinning test, its own slice), M4 (documenting the score-store
    boundary), the `StrategyIdentityGuard`-vs-routine-`RuleSetVersion`-bump operating procedure, and the
    stale-doc cleanups L1–L4.
- **Every strategy gets its own plain ranked table in the weekly report — and nothing is combined (spec
  150).** Spec 137 made the primary "the series the weekly report renders", so the first live 3-strategy run
  scored 43 companies under `default`/`filings-led`/`narrative-led` and only `default` appeared anywhere; the
  other two existed solely as JSON under `data/scores/strategies/{name}/`. `WeeklyReportModel` gains one
  trailing, defaulted, nullable `Strategies` list of `StrategyReportSection`, rendered after ALL existing
  content. Rules:
  - **Gated on `Runtimes.Count > 1`, and a single strategy passes `null` (never an empty list)** — so every
    deployment that never configured `Radar:Strategies` renders a report that is **byte-identical** to
    pre-150. Asserted as a **full-string pin** (`MarkdownWeeklyReportStrategySectionTests.PreSpec150Golden`),
    captured by running the shared golden model through the *unmodified* renderer — a real before/after, not
    a restatement of current behaviour.
  - **`IScoringStrategyFactory` + `IScoreRepositoryFactory` are REQUIRED ctor dependencies of
    `WeeklyReportBuilder`**, never optional-nullable: a silently-null optional dependency means a production
    wiring mistake renders no sections while every test stays green (the class of bug spec 146's review
    caught). Both were already registered, so DI resolves them; a composition that renders a report must
    therefore also register `ISignalFileStore` (the Worker always does).
  - **Same read path, reused rules.** Snapshots come from `_scoreRepositoryFactory.ForStrategy(...)` — the
    very repository the scoring stage wrote through, no second route to the strategy score files. The
    candidate rule (latest snapshot in `(periodStart, periodEnd]`, a company with none **omitted**, never
    invented), the ordering (`OpportunityScore` desc, then `CompanyId` asc — AD-3) and the spec-53
    zero-evidence-link exclusion are the primary walk's existing rules verbatim.
  - **`MaxItems` applies PER SECTION, independently, and truncation is stated.** Decided, documented on
    `BuildStrategySectionsAsync` and tested: one strategy can never crowd out another, and when the cap bites
    the header appends `· showing top N` rather than silently shortening (the spec-125 failure). The section
    carries `CompaniesScored`, `CompaniesWithLinkedEvidence` and `Rows`, all three rendered — the spec-53
    exclusion is visible arithmetic instead of a silent drop — with `Truncated` derived so it cannot disagree
    with the numbers beside it. Links are fetched for every candidate (not just up to the cap) precisely
    because that middle number is rendered.
  - **Scores only, and NOTHING is composed.** No labels (`WeeklyReportActionPolicyV1` is asserted to be
    consulted once per surfaced *primary* entry, never per strategy row — a company `Watch` under one
    strategy and `Ignore` under another would read as Radar equivocating), no evidence blocks, no "why
    noticed", no advice vocabulary. **Explicitly out of scope, recorded not built: every form of
    cross-strategy composition** — disagreement metrics, merged rankings, composite scores, "consensus"
    columns — because a computed disagreement number over a few days of accrued history would rank noise and
    invite trusting it. One honesty line under the FIRST section says these are independent scorings of the
    SAME collection pass, that absolute scores are not comparable when formulas differ, and that ranking
    strategies against price is spec 140's `data/efficacy/strategy-leaderboard.md` — otherwise the
    multiple-comparisons trap simply arrives via the reader.
  - **Provenance holds**: a row carries the whole `CompanyScoreSnapshot`, so every printed number is read off
    the stored snapshot, and the renderer applies the same snapshot-id/company-id guard it applies to
    narrative entries. `|` is escaped in names/tickers (an unescaped pipe would silently add columns); a
    missing ticker renders `—`; a null/blank fingerprint renders `(unstamped)`.
  - **Read-only: no scoring change, no new fingerprint input, no pin move.** `ScoringConfigVersion` is
    DISPLAYED (from `runtime.Engine.EffectiveConfig`), never computed here; nothing under `Scoring/` or
    `Domain/` was touched.
- **v9 got the notedness discount it never had, and a strategy can now be tuned INLINE (spec 149).** Found by
  running it: the first live 3-strategy run (2026-07-27) had the two v9 strategies nearly *inverting* the v8
  primary at the extremes (CAT 43rd of 43 under `default`, **1st** under `filings-led`), because
  `RadarScoreFormulaV8` referenced the following-tier/notedness discount in 13 places and
  `RadarScoreFormulaV9` in **zero** — so a v9 strategy ranked on raw channel activity, largely a size proxy
  and close to the inverse of Radar's purpose. A gap in spec 146, not a defect in it. Rules:
  - **One definition of notedness, EXTRACTED not copied.** `ScoreSignalMath.NotednessDiscount` (+
    `TierDiscount`) now owns the clamped
    `1 − attention/OpportunityAttentionDivisor·OpportunityAttentionDiscountWeight −
    TierDiscount(tier)·FollowingTierDiscountWeight` expression, and **both** formulas route through it, over
    the same `ScoringWeights` knobs and the same clamped-int `AttentionScore`. The two formulas differ in
    **composition** — where the discount lands — never in what notedness *means*. v8's expression shape and
    accumulation order are preserved verbatim (the clamp was already a separate sub-expression), so v8 is
    byte-identical: `ScoringOutputStabilityTests` (spec 148) is untouched, still passes, and its fixture
    genuinely exercises the discount.
  - **v9 applies it ONCE, to the COMPOSED score**, `Clamp0To100(100·composite·discount)` — notedness is a
    property of the COMPANY, not of a source, so per-channel application would compound it with however many
    channels a strategy happens to declare. Attention consequently enters v9 **twice with opposite signs**:
    as budgetable positive breadth (spec 146's direction correction, kept) and as the fame that damps whatever
    was found. Per-channel `WeightedContribution`s still sum to the *undiscounted* composite — the discount is
    not smuggled into per-channel provenance.
  - **The opt-out is exact, and that is the compatibility proof.** With `OpportunityAttentionDiscountWeight` =
    0 **and** `FollowingTierDiscountWeight` = 0 the discount is **exactly `1.0`** (both subtracted terms are a
    finite value × 0; the default floor 0.05 ≤ 1), and ×1.0 is the IEEE-754 identity. Measured, not argued: the
    pinned fixture was run against pre-149 `origin/main` @ `230948f` and reproduces its components,
    explanation and contribution chain field-for-field.
  - **Scope of "byte-identical", stated so it cannot be misread**: identical = the five `ScoreComponents`, the
    explanation, the composite, every contribution. Changed = `ComponentJson` gains **one additive property,
    `Discount`** — same backward-compatibility argument spec 146 made for `Formula`/`Composite`/`Channels`
    (the five `ScoreComponents` properties still come first and by name). It is recorded because the discount
    is a multiplicative transform on the headline number and the curated `FollowingTier` appears nowhere else
    in a v9 snapshot. The **explanation names the discount only when it is ≠ 1.0** — i.e. iff it moved the
    number: "Opportunity 33 (composite 0.412 = …)" without the transform between them reads as an arithmetic
    error, and a score Radar cannot explain is not a score; when it is inert, `Opportunity = composite·100` is
    literally true and mentioning it would be noise.
  - ⚠ **AD-6, answered explicitly, and the answer is uncomfortable.** Adding a multiplicative discount changes
    v9's COMPOSITION, not merely its inputs: **at default weights a v9 strategy scores differently after this
    slice than before it.** Spec 149 put `radar-formula-v10` out of scope, so `_formula.Version` stays
    `radar-formula-v9` and the default `ScoringWeights` are unchanged — therefore **a v9 strategy's
    `ScoringConfigVersion` does NOT move even though its behaviour did**. v9 snapshots from before and after
    are falsely comparable and `StrategyIdentityGuard` will not trip. That is precisely the failure spec 148
    exists to prevent, accepted here only because v9 is opt-in, shipped days earlier, and has **one** live run
    of history. The remedy for anyone who cares is spec 141's immutable-by-convention rule: give the retuned
    strategy a NEW NAME (`patents-led` → `patents-led-v2`), which re-keys the series via `ScoreSeriesKey`
    without the stamp having to move. **A future structural change to v9 must bump to `radar-formula-v10`
    rather than repeat this.**
  - **Inline per-strategy weights: `Radar:Strategies[i].Weights`, merge order defaults → named
    `ScoringProfile` → inline, last wins.** Parsing stays in the composition root
    (`ApplyInlineWeightOverrides`); `ScoringStrategyDefinition` needed **no** new property (it already carries
    resolved `Weights`) and `IConfiguration` still never reaches Application. **An unknown key FAILS FAST
    naming the strategy and the key** — `ConfigurationBinder` silently ignores unmatched keys, and a typo'd
    override would leave a strategy stamped, scored and *ranked* as tuned while being nothing of the sort
    (the fail-open shape spec 138 already had to close once). Key matching is **case-INSENSITIVE,
    deliberately**: the binder matches case-insensitively, so a case-sensitive validator would reject keys
    that bind fine and, worse, would stop answering the binder's question. `ScoringWeights.Validate()` runs on
    the **merged** result (so a cross-field invariant like the monotone tier ordering is enforced too), a
    scalar `"Weights": "x"` is rejected like the `SignalTypes`/`Channels` shape guards, and an omitted
    `Weights` returns the profile's instance unchanged. A **known** key that carries no number is rejected
    too — every `ScoringWeights` field is a plain number, so an object (`{ "Value": 0.0 }`) would be silently
    ignored and an explicit `null` binds to **0** (measured), i.e. a silently *disabled* discount on a
    strategy that reads as tuned. **Every** inline-`Weights` failure names the strategy, bind failures
    included: `ConfigurationBinder`'s own message carries the *indexed* path
    (`Radar:Strategies:3:Weights:RecencyFloor`) but no name, so a non-numeric or empty value is rethrown
    named, with the binder exception kept as `InnerException` — same treatment as the merged-`Validate()`
    failure, so the contract holds for the whole method rather than most of it.
  - **Identity verified, not assumed**: resolved weights are hashed into `ScoringConfigVersion` **by value**,
    so two strategies differing only in one inline weight get **different** fingerprints (asserted through the
    real `AddRadarScoringStrategies` → `ScoringStrategyFactory` path) while the strategy that declared nothing
    keeps the untouched default stamp.
  - **NO PIN MOVE.** No `ScoringWeights` property added, no `ScoringOptions` change, no `_formula.Version`
    bump, no `RuleSetVersion` bump. The spec-148 pins hold at every window: 30d (unit pins)
    `0c46e07b94db`/`28226897f97b`, **60d (live baseline)** `4eb2fe5d3cdf`/**`4da4b5ff6ec9`**, 120d
    (`-Profile long-window`) `0a7058d94582`/`81e9fab711f8` — all four recomputed on this branch.
  - **M3 (spec 148's deferred item) was NOT done here** — v9 still holds its own copy of v8's
    EvidenceConfidence/SignalVelocity blocks. It does not fall out of this slice naturally: those blocks are
    multi-line accumulations whose extraction would have to preserve v8's arithmetic shape under a much larger
    surface than a single clamped expression, and spec 149 explicitly said not to let it grow the slice.
  - **Out of scope, recorded not built**: `radar-formula-v10`, per-strategy report tables / cross-strategy
    comparison rendering (spec 150 — deliberately after this one, since comparing across a formula that
    ignores notedness would compare the wrong thing), and auto-tuning weights against price (humans declare;
    spec 140 judges; price is never an input, AD-14).
- **Collector attribution is RECOVERABLE for legacy evidence, opt-in, and never mistakable for a recorded fact
  (spec 151).** Spec 146 began recording the producing collector on evidence; **6,047 of 6,388 accrued raw
  files (94.7 %) predate it**, so replaying a v9 collector-channel strategy over the accrued window scored
  every channel against ~5 % attribution — worse than no series, because it would populate spec 140's
  leaderboard with numbers measuring the missing attribution. The attribution was deterministic at collection
  time and simply was not persisted, so re-deriving it is *recovery*, not fabrication — but it is still an
  inference. Rules:
  - **`ICollectorAttributionResolver` is the ONE seam.** `RadarScoreFormulaV9` no longer reads the metadata key
    inline; it asks the resolver and keeps the whole `CollectorAttribution`
    (`{ string? CollectorName, CollectorAttributionSource Source }`, `Unattributed`/`Recorded`/`Inferred`).
    **Inferred ≠ recorded STRUCTURALLY, not by convention**: the invariant "`CollectorName` is non-null iff
    `Source != Unattributed`" is enforced by private-ctor factories, and `Unattributed = 0` so even
    `default(CollectorAttribution)` satisfies it. `ScoringChannel.Consumes` is unchanged (exact ordinal match,
    false for null) — **what a v9 channel MEANS is untouched**; only which signals carry a name changes.
  - **Default OFF, and the default is the compatibility proof.** `Radar:Scoring:InferLegacyCollectorAttribution`
    (bool, default `false`) → `RecordedOnlyCollectorAttributionResolver`, which is *behaviourally identical* to
    the pre-151 inline read. So scoring output, provenance strings and every fingerprint are byte-identical,
    `replay ⊆ forward` is untouched, and no already-produced score can move. An unparseable value **fails
    fast** (reading `"yes"` as off would emit a full near-zero series that looks like data).
  - **The table lives in Infrastructure and reuses the spec-147 vocabulary.**
    `LegacyCollectorAttributionInference` keys on `EvidenceSourceType` + each collector's **own exclusive
    metadata marker key**, now a `MetadataMarkerKey` const on the collector itself and *referenced* by the
    table (and `RadarCollectorNames.*` for the names) — so neither can drift. **The marker rule is not the
    obvious rule and that matters**: `sourceType ⇒ newssearch` would have MISATTRIBUTED the 5 live GDELT
    records (both emit `NewsArticle`); `metadata.secFeedUrl` does not discriminate (all three SEC collectors
    write the same submissions-JSON shape); and `metadata.form` is **config-dependent** (it separates only
    while `Radar:Sec:Forms` excludes `4`/`SC 13*`). `sec-edgar` writes no exclusive key, so it is the ONE
    elimination rule over a **closed** three-collector `Filing` set — pinned by a test, as is "every shipped
    collector is covered".
  - **Recorded ALWAYS wins; ambiguity stays unattributed.** Not "when they agree" — always, which is what makes
    the inference strictly additive over the attributed cohort. Two contradictory markers, an unknown source
    type, or no marker with no elimination rule ⇒ `Unattributed`. Radar never guesses.
  - **Validated vs merely REASONED, stated because the split is uncomfortable.** Against the 341 records that
    do carry recorded attribution, ignoring their recorded value: **341/341 agree, 0 disagreements, 0 of 6,388
    ambiguous.** But that cohort is 337 `newssearch` / 2 `sec-form4` / 2 `RssPressReleaseCollector`.
    **`sec-edgar` (1,160), `sec-13dg` (850), `usaspending` (21), GDELT `news` (5) and the five zero-record
    collectors are REASONED, not ground-truth validated** — and `filings-led`'s two channels are exactly
    `sec-form4`/`sec-13dg`, so the least-validated mappings carry the experiment.
  - **Nothing is persisted; no evidence file is rewritten; the spec's side index was considered and REJECTED.**
    Attribution here is a pure function of `SourceType` + the metadata bag — fields already in memory on the
    object being scored. A `contentHash`-keyed side index would be a materialized cache of that function: it
    adds a file to keep in sync with an append-only store, a regeneration step, and a staleness mode where the
    index silently wins. Deriving on read persists no new state, cannot drift from the store, is reversible by
    deleting one class, and needs no backfill — satisfying AD-8/AD-1 more strongly than an index would.
  - **Every artifact can say so.** `CollectionProvenance` gains a trailing `attribution=inferred-legacy;`
    segment (spec 147's precedent; composes with `collection=none-this-pass;`); each v9 `ChannelBreakdown`
    gains additive `RecordedSignals`/`InferredSignals`/`UnattributedSignals`; each affected contribution reason
    gains `(collector attribution inferred)`. **`UnattributedSignals` is structurally 0 for a COLLECTOR
    channel** (`Consumes(null)` is false) and informative only for the breadth channel.
  - **No fingerprint move**: the attribution mode is hashed into **nothing** — asserted, engine-level and in
    the composed Worker graph, that two graphs differing only in the flag stamp the SAME `ScoringConfigVersion`
    and DIFFERENT `CollectionProvenance` with byte-identical scores. All four spec-148 pins stand;
    `ScoringConfigFingerprintTests` and `ScoringOutputStabilityTests` are untouched.
  - ⚠ **It must never become a silent fallback (spec §4).** Forward collection records the real collector; if
    it ever stops, that is a defect that must surface as unattributed evidence rather than be papered over.
    Hence opt-in, marked everywhere, and documented as a research affordance for a bounded historical gap.
  - **Out of scope, recorded not built**: backfilling missing *evidence* (spec 142's 89.5 % unresolvable
    cohort — a different problem, still healed forward only), auto-running the replay or promoting its output,
    and changing what a v9 channel means.
- **`radar-formula-v10`: neutral evidence establishes COVERAGE but contributes no DIRECTIONAL opportunity
  (spec 153).** v9's collector channel scored `saturation × (0.5 + 0.5·preponderance)`, so a channel with no
  directional mass sat at exactly **0.5**. The code called that "neither rewarded nor punished" — true against
  a *mixed* channel, false against an **inactive** one, which contributes 0: an all-Neutral channel scored
  `saturation × 0.5`, **rising with activity**, so volume alone produced score. Measured on the live store,
  **87.6 % of 49,793 signals are Neutral** (Positive 8.1 %, Negative 4.3 %), and it landed hardest on exactly
  the strategies built to test the thesis — `filings-led`'s `sec-form4` channel sees routine Form 4s extracted
  as Neutral `InsiderBuying`, and its `sec-13dg` channel sees passive 13Gs that spec 99 made Neutral **by
  design**, so that strategy was substantially ranking **filing volume** (larger companies file more).
  Corroborating symptom: five deliberately-different strategies backtested 2026-07-28 came in at in-sample
  Spearman ρ −0.0849 / −0.0969 / −0.0999 / −0.1000 / −0.1009 — a spread of **0.016**, which is what one common
  factor dominating all five looks like. Rules:
  - **The composition, in symbols: `channelScore = saturation × max(0, preponderance)`.** Range `[0,1)`, so
    the composite range contract, the `[0,1]` clamp and the **NEVER-RENORMALISE** rule are all untouched.
  - **All-neutral vs balanced: DECIDED, and they score the SAME.** No directional mass at all ⇒ preponderance
    exactly 0 ⇒ **exactly 0**; balanced positive/negative mass ⇒ preponderance exactly 0 ⇒ **also exactly 0**.
    Both mean "no net evidence that this trajectory is improving", and Opportunity answers exactly that
    question. **They differ in the EVIDENCE TRAIL, not in the score**: each v10 channel breakdown records the
    preponderance, the total directional mass and a `DirectionState` token (`none` / `balanced` / `positive` /
    `negative`) — provenance only, never a score input. Net-**negative** mass also **floors at 0**: v10's
    Opportunity measures improvement, deterioration is reported by the (v8-meaning) `TrajectoryScore` v10
    keeps, and a negative channel share would *subtract* from other channels' genuine findings and break the
    `[0,1]` share semantics.
  - **Neutral evidence is NOT discarded — this removes a directional CONTRIBUTION, not the evidence.** A
    Neutral signal still counts as activity in its channel's saturation (so **neutral coverage AMPLIFIES a
    genuine directional read** — asserted), still counts in EvidenceConfidence and SignalVelocity, still
    counts in `SignalCount`, still keeps the channel out of `Dark`, and still emits its own contribution
    naming the channel. An all-neutral channel (`Score 0`, `Dark false`, `SignalCount > 0`) is therefore
    distinguishable from an absent one (`Score 0`, `Dark true`, `SignalCount 0`) — same score, different
    record. `Dark` is more load-bearing in v10 than it was in v9, precisely because the score no longer
    separates them.
  - **The breadth channel is UNCHANGED, and the tension is recorded rather than hidden.** Still
    `reach/(reach + S_c)`, with the spec-149 notedness discount applied exactly as v9 applies it — once, to
    the composed score, via `ScoreSignalMath.NotednessDiscount`. **Honest caveat, documented on the class:** a
    breadth channel still earns share from pure coverage, which is *adjacent* to the "volume alone produces
    score" problem this formula exists to fix. Kept deliberately because breadth is an explicitly
    strategy-**budgeted** measure of NOTICE (not of improvement — a strategy that does not want to pay for
    notice simply does not declare it, unlike v9's un-opt-out-able 0.5 floor) and is already damped by the
    notedness discount; spec 153's *measured* target is the directional factor on collector channels.
    Re-tuning or removing breadth needs its own evidence.
  - ⚠ **v10 SCORES ARE ON A LOWER ABSOLUTE SCALE than v9's** — removing a 0.5 floor from every collector
    channel lowers essentially everything. **v9 and v10 absolute scores are NOT comparable; only rankings
    are.** Same fixture, same budget: Opportunity **22** under v9, **9** under v10 (the all-Neutral channel
    0.353 → 0.000; the mixed channel 0.438 → 0.128). That is the intended consequence, not a calibration
    defect — re-tuning weights/saturations to compensate is explicitly out of scope (measure first).
  - **v8 is untouched. v9 is BYTE-IDENTICAL, and that is measured.** Both stay available as the controls that
    make the change measurable, exactly as v8 remained when v9 shipped. Asserted by two golden pins: the
    existing `ScoringOutputStabilityTests` (v8) and the NEW `RadarScoreFormulaV9OutputStabilityTests`, whose
    values were **captured from the pre-153 sources before any production file was touched** and which both
    pass **unmodified** afterwards. `ScoringConfigFingerprintTests` is untouched and all four spec-148 pins
    stand. Recorded but deliberately NOT fixed (spec §3): v8's all-neutral company lands at
    `TrajectoryNeutral = 50` rather than 0 — the same class of property, but v8 is the established baseline,
    so fixing it is a separate decision with its own `radar-formula-vN`.
  - **`CompositionRevision` closes the hole spec 149 exposed.** `IScoreFormula` gains an additive **default
    interface member** `string CompositionRevision => string.Empty`; `FormulaIdentity.Of` is the ONE
    definition of the composed identity (`Version` when blank, else `{Version}@{CompositionRevision}`), and
    **all three** of `ScoringEngine`'s uses route through it — the hashed `formulaVersion` field,
    `EffectiveScoringConfig.FormulaVersion`, and the `ScoringVersion` stamp. Storing the *composed* value is
    what keeps the scoring-config store's recompute-from-stored self-verification true (it rehashes the
    persisted `FormulaVersion`). v8 and v9 do not override it, so their stamps, persisted records and every
    pinned fingerprint are byte-identical. `RadarScoreFormulaV10.CompositionRevision` is a const (`"rev1"`)
    declared next to the composition with its obligation stated, and
    `RadarScoreFormulaV10CompositionGuardTests` pins the revision, v10's full output and the
    `ScoringConfigVersion` a v10 strategy stamps at the code-default weights and 30-day window over that
    file's own 3-channel budget (`radar-scoring-fp-d89b8bc81815` — budget-dependent, like every channel
    strategy's stamp) **together in one file**: change v10's composition and it fails, and the
    only green fixes are revert, or bump the revision and update all three pins — which re-stamps and
    therefore trips `StrategyIdentityGuard` on the next run. **Relationship to AD-6:** a genuinely NEW
    structure still earns `radar-formula-v11`; the revision only makes a spec-149-style in-place adjustment
    impossible to make invisibly.
  - **Reuse over copy, and it was the bulk of the slice.** v9 held VERBATIM copies of v8's Trajectory /
    Attention / EvidenceConfidence / SignalVelocity blocks (the architecture audit's **M3**, deferred by spec
    148) and a third copy was not acceptable: all four moved into `ScoreSignalMath` (plus v9's private
    `Saturate`), with v8 AND v9 routed through them and every expression shape and accumulation order
    preserved verbatim — `100·reach/(reach+S)` is deliberately **not** expressed via `Saturate`, because
    `(100·reach)/(reach+S) ≠ 100·(reach/(reach+S))` in IEEE-754. The whole channel loop (selection,
    collector-attribution resolution + tally, ran/not-run split, activity→saturation→preponderance, per-signal
    channel attribution, composite sum, the contribution builder and the explanation's channel summary) moved
    into **`ScoringChannelComposition`**, parameterised by a `CollectorChannelScore
    (saturation, preponderance) -> channelScore` delegate — **the ONLY behavioural difference between v9 and
    v10**. Each formula still projects the shared per-channel result into its OWN `ComponentJson` record
    (v9's stays byte-identical at 13 channel properties; v10's appends `Preponderance` / `DirectionalMass` /
    `DirectionState`, which are `null` for a breadth channel because breadth never consults direction) and
    writes its own explanation naming its own version.
  - **Wiring**: `ScoreFormulaVersions.V10` appended to `All`; `RadarScoreFormulaFactory` dispatches it with
    the same ctor args v9 gets; and `ScoringStrategySet`'s two channel rules were generalised off a hard-coded
    `V9` onto **one predicate, `ScoreFormulaVersions.ConsumesChannels`**, which the factory dispatch reads too,
    so the validator and the dispatch cannot drift. A v10 strategy with no channels fails fast exactly as a v9
    one does; `ScoringStrategyFactory`'s registered-collector guard keys off `Channels`, so it was already
    formula-agnostic (confirmed by test, not assumed); and `Radar:Strategies[i].Formula = "radar-formula-v10"`
    binds, validates and starts identically to v9.
  - **Out of scope, recorded not built**: changing v8; re-tuning channel weights/saturations to compensate for
    the lower scale; the efficacy horizon / outcome variable (spec 152 and the open question after it); and
    migrating any existing strategy onto v10 — it is opt-in, and a strategy that changes formula should get a
    NEW NAME (spec 141's immutable-by-convention rule).
- **Pooled price outcomes are BENCHMARK-ADJUSTED against a frozen universe; the paired path is deliberately
  not (spec 183).** AD-16's "must be benchmark-adjusted" is now implemented: excess = raw forward return −
  the equal-weight mean forward return of the OTHER resolved members of **`benchmark-universe-v1`**
  (`data/efficacy/benchmark-universe-v1.json` — committed, self-contained, 74 members frozen 2026-08-23,
  content hash `97e31fde67655453e4bdee8f69eef07785db6f2c80124220176a5637829561fc`), self-excluded, members
  resolving through the SAME spec-152 `ForwardReturn` rules. Rules: the artifact is the ONLY membership /
  price-series-key input (never `companies.json` — a seed edit moves nothing; expansion = a prospective
  `benchmark-universe-v2`, never an edit — the reader refuses a content-hash mismatch); the computation is
  CENTRAL (`UniverseBenchmark`, cached per (universe, D, horizon, tolerance), shared via
  `IUniverseBenchmarkProvider` by the spec-140 leaderboard and the spec-179 news-risk evaluator); the
  excess definition and coverage rule are CODE CONSTANTS (`required = max(40, ceil(0.90 × eligiblePeers))`,
  integer ceiling so ×10 counts don't FP-round up; unresolved members stay in the denominator with
  reasons); below the bound the pooled observation is excluded as **`BenchmarkUnavailable`** — named and
  counted, never a silent raw fallback — and a post-freeze company is **`NotInBenchmarkUniverse`**. The
  leaderboard now ranks by excess (`excess-vs-universe-v1` columns, CSV schema `strategy-leaderboard-v2`;
  the pre-183 raw artifacts are preserved once as `strategy-leaderboard-raw-v1.{md,csv}` and declared
  incomparable; pre-freeze dates carry a retrospective label). **The AD-15 paired path has NO benchmark
  gate, structurally**: `StrategyObservation` carries `RawForwardReturn` + nullable `ExcessForwardReturn`;
  the paired harness consumes ONLY raw-return ranks (self-excluded excess is a positive affine per-date
  transform — `excessᵢ = N/(N−1) × (rᵢ − mean(all))` — so every per-date rank/ρ/delta is identical), and
  its outputs are byte-identical with or without a benchmark (asserted). News-risk rows carry raw AND
  excess, both descriptive; max-adverse keeps its raw basis. AD-15/AD-16 amendments dated 2026-08-23. No
  scoring change, no fingerprint move; the attention screen is untouched.
- **Facts stay uncertain; the CALL gets made — evidence status + operating calls (spec 184).** Two layers,
  deliberately separate, both living in `Radar.Application.Lifecycle` OUTSIDE the scoring closure
  (`StrategyLifecycleBoundaryTests` asserts neither `Radar.Application.Scoring` nor `…Pipeline` can reach a
  lifecycle type — which is why calls/statuses ride `WeeklyReportModel.Lifecycle`, the renderer-facing model,
  and are deliberately NOT on `StrategyReportSection`, which travels into the pipeline result and the
  spec-179 news-risk nomination input). Rules:
  - **Evidence status** (`Accruing | Ranked | GatePending | GatePassed | GateFailed`) is COMPUTED each run
    from artifacts that already exist — the spec-140/183 leaderboard CSV + the spec-155/170 paired AD-15
    composite-gate CSV, read by `FileStrategyEvidenceFactsSource` (degrades, never throws) and mapped by the
    pure `StrategyEvidenceStatusCalculator`. Descriptive, never a verdict: `Ranked` structurally cannot
    render without its numbers, a CI spanning zero renders the SENTENCE "no evidence of discrimination yet",
    unreadable artifacts render "Accruing (evidence unavailable)" (the arm is never hidden), and `GateFailed`
    requires every gate reason to be a MERIT code (`median-paired-delta-not-positive` /
    `interval-lower-bound-not-positive`) — any accrual/prerequisite reason is `GatePending`, so noise is
    never converted into pass/fail ahead of the precommitted gate.
  - **The operating call** (`Lead | Trial | DoNotLead | Stop`, global `StopAll`) is a declared, journaled,
    falsifiable DECISION: `data/strategy-operating-calls.json` (committed, strict reader
    `FileOperatingCallSource` — unknown token/property/schema fails naming the file and rule; ABSENT file =
    the stated "no call declared" condition, prominence stays with the storage primary by default) is the
    ONLY runtime input; `docs/strategy-lifecycle.md` is the append-only audit journal, never parsed. ONE
    deterministic, order-independent reducer (`OperatingCallReducer`): a persisted gate verdict wins unless
    the call carries `overridesGate: true` AND ~~post-dates it~~ **binds to it by `overridesVerdictId`
    (⚠ AMENDED BY SPEC 186 §3 — timestamp precedence is DELETED; see the spec-186 bullet)**
    (`GatePassed → Lead`, `GateFailed → Stop`);
    otherwise the file call verbatim; an uncalled Research arm is an implicit Trial; after reduction exactly
    one Lead or StopAll — **zero Leads (the Lead arm gate-failed) resolves to the PREDECLARED fallback
    StopAll**, and two reduced Leads throw rather than pick silently. Validation (unknown strategy, call on a
    Comparator, duplicate, multiple/zero Leads, Lead beside StopAll, resolution without its immutable
    `resolutionRule`) fails AT STARTUP via `OperatingCallStartupValidator` — the Worker's first statement,
    before seeding, so a bad file costs no collection.
  - **Lead governs ALL user-facing narrative and action prominence**: the weekly report's narrative walk
    (highest opportunity, movement, labels, "why noticed", evidence blocks) reads the LEAD arm's repositories
    (`IScoreRepositoryFactory`/`IScoreSnapshotFileStoreFactory` — the same write paths, no second route);
    spec 150's "labels are primary-only" is amended to **labels are Lead-only** (still exactly one labelled
    strategy). `Radar:PrimaryStrategy` remains ONLY storage/series identity, untouched. Live-leaders orders
    Lead (bannered with call/basis/asOf/reviewBy/resolutionRule) → Trials → DoNotLead-with-basis; Stop arms
    move to a "Stopped arms — diagnostic appendix", never hidden; StopAll renders the diagnostic view under
    an explicit "no lead — StopAll" banner with NO narrative entries and no labels.
  - **Nothing else moves**: no fingerprint input, no snapshot field, no formula/rule-set bump — the pins do
    not move; scores/sections/news-risk nomination asserted byte-identical across call fixtures; and with a
    SINGLE configured strategy the layer is structurally inert (the sources are never even consulted —
    asserted with throwing stubs) and the report is byte-identical.
  - **The initial calls (2026-08-23, actor human, review by 2026-09-05T00:00:00Z)**: `disclosure-led-v11`
    Lead (resolved by the AD-15 composite gate EVENT, not a calendar date), `default` DoNotLead (oos ρ −0.05,
    CI spans zero at call time), the four other research arms Trial (resolved by supersession); comparators
    carry no call, ever. Radar records wrong calls rather than avoiding falsifiable decisions.
- **News event typing — stage 1 of the two-stage read: facts and event types, NO directional question (spec
  181).** A new `NewsTyping` slice (Application + Infrastructure) types spec-177 archived observations
  against the closed **`news-event-taxonomy-v1`** (14 members incl. `MarketReaction`; hash
  `078f53452ac8bf28526f29704f5d06a345bfae3b7bcbbf54661a2a8193555f5c`, pinned by test and declared in
  `docs/cohorts/news-event-taxonomy-v1.md` — immutable by convention, change ⇒ v2, cohorts never pool across
  versions; the §3 ≥200-observation human audit runs against FIRST typings, tooling shipped here). Rules:
  facts are the typed unit (one headline carries several events); each validated fact carries event types,
  a preserved statement, temporal scope, closed attribution/assertion-status vocabularies, confidence and
  EXACT-substring citations (spec-179-style fail-closed validation; unlike 179 an invalid citation is
  dropped individually and the fact survives on verified remainder — the omission-bias guard);
  `DerivedPrimaryType` is DERIVED (greatest summed confidence, taxonomy-order tie-break), never authored;
  the wire schema and prompt contain NO direction/severity/materiality member (reflection-guarded).
  Cohort key = provider:model|prompt|schema|**taxonomy**; capture-mode cohorts stay separate in every
  output; no merged verdict anywhere. The generator runs post-run beside the 179 shadow (gate:
  `Radar:NewsResearch:Typing:Enabled` && Full && unfiltered; **default OFF** — enable via the `news-typing`
  run-profile overlay, hosted DeepSeek reader only per §1's measured 0%-vs-19.2% citation-drop gap),
  bounded by `MaxNewTypingsPerRun` PER READER (window observations newest-first, then backlog oldest-first
  — the bounded backlog phase IS the 13k-article catch-up mechanism; no new RunMode), cached by
  (cohort, observation, payloadHash) on completed typings (Typed/InsufficientContent only, so failures
  retry). **`fact-family-v1`** is a deterministic post-extraction checkpoint pass (never a model call):
  company + capture mode + overlapping event types + token-set-Jaccard ≥ 0.6 over versioned normalization +
  7-day window, contradictions (differing number multisets, negation XOR) never merge, family id = builder
  version + company + capture mode + earliest member's normalized statement (never the member list),
  snapshots append-only per cohort. ⚠ **SUPERSEDED BY `fact-family-v2` (spec 186 §4)** — MEMBERSHIP is
  byte-compatible, but identity gained a temporal anchor and the build split into segmentation + projection;
  see the spec-186 bullet. Output: `data/news-typing/live/attention-decomposition-{date}.md|.json`
  — per company per reader×capture-mode cohort, type distribution + publisher breadth + family count beside
  raw count + honest incompleteness marking, carrying the §5 caveat verbatim. Read-side and shadow: no
  score, label, strategy, fingerprint, snapshot field or report rank moves; the pins do not move. Stage 2
  (the direction judge consuming ONLY this fact layer) is spec 185 — SHIPPED, next bullet.
- **Stage-2 direction judge — facts-only, challenge-only, and the leaders finally say what the judge saw
  (spec 185).** `Radar.Application.NewsRisk.Judgment` (deliberately OUTSIDE `Radar.Application.NewsTyping`,
  whose guard pins that fact types carry no direction member). The judge receives ONLY canonical fact
  families (representative fact's typed content + `MemberCount`/`DistinctPublisherCount` as corroboration
  of REPORTING — one claim however syndicated; the request type structurally carries no raw prose, headline,
  score, rank, label or price, reflection-guarded) against the FIXED rubric verbatim ("the company's recent
  business trajectory"). v1 findings are CHALLENGE-ONLY (reusing spec-179's `NewsRiskCategory`/`Severity`
  and `AdviceLanguageGuard`, never copied); `BusinessTrajectory ∈ {Improving,Deteriorating,Mixed,Unknown}`
  with zero findings IS the supportive read; all-invalid findings ⇒ `ValidationFailed`, NEVER no-challenge;
  the attribution-caveat rule (every supporting fact below `reported` ⇒ a missing/blank caveat DROPS the
  finding) makes attribution demonstrably change judgments, and it is a prompt rule too. Cohort key =
  `{judge}|prompt|schema|stage1={full stage-1 cohort key}|families={FactFamilyBuilder.IdentityString}` — a
  stage-1/taxonomy/builder change forks stage 2 by construction; cache identity = (cohort, company, ordered
  family-set hash); completed = `Judged|InsufficientFacts` only (failures retry, spec-181 rule). Records
  persist insert-only at `{news-risk root}/judgments/{judge-policy-segment}/{companyId}/…` carrying ALL FIVE
  completeness dimensions (spec-182's capture/search/supply verbatim + `NewsTypingCompleteness
  {Failed=0,Backlog,Complete}` + `NewsJudgmentFamilyBundle {Capped=0,Complete}`). Orchestration: Worker runs
  typing FIRST (now returns `NewsTypingRunResult` — the pass's own families/facts/completeness join, no disk
  re-read), then the judge (spec-179 candidate selector REUSED — same candidates as the single-call read),
  then the shadow (additive param embeds the judgment sections + A/B category display in the v3 live
  artifact — `news-risk-live-v3`, cohorts never pool, no merged verdict). The leaders marker: EVERY
  live-leaders row (research/stopped/comparator alike) carries a MANDATORY `semantic read` column — `⚠
  challenged (top-finding)` / `· no challenge found in supplied facts` (+` (typing incomplete)` when typing
  ≠ Complete; never worded clean) / `? unassessed (reason)` from a closed 8-token vocabulary — derived ONLY
  by `NewsJudgmentMarkerPolicy` from the PROSPECTIVELY designated presentation cohort
  (`Radar:NewsResearch:Judgment:PresentationCohort {Judge,Extractor}`, referentially validated at startup);
  the model never chooses presentation, an absent marker is unrepresentable
  (`NewsJudgmentMarkerReportModel.MarkerCellFor` is total), and only same-run judgments qualify (`stale`
  otherwise). Because the report renders inside the pipeline and the judge runs after it, the first render
  says `judgment-pending` and the Worker re-renders the SAME captured model via
  `IWeeklyReportJudgmentRerenderer` (registered ONLY with the judgment step; its PRESENCE is what makes the
  builder render pending — absent ⇒ the honest `no-judgment` stands) overwriting the same report file; row
  numbers are byte-identical apart from the marker cell (asserted), `PreSpec150Golden` is untouched, and
  labels/ranks/scores/snapshots move NOWHERE. Config: `Radar:NewsResearch:Judgment` (default OFF, strict
  key allowlist, requires `Typing:Enabled` unconditionally naming both keys, requires `GenerateReport`,
  Full-mode unfiltered only; judges reuse the spec-179 reader shape); `-Profile news-judgment` enables
  typing+judgment hosted-DeepSeek-only. Guards extend, never weaken: Scoring/Pipeline cannot reach
  Judgment, the judge subsystem cannot reach Prices (positive control kept). First live output is
  EXPLORATORY (no audited stage-1 sample exists — the dispatch note); the artifact caveat says so. No
  fingerprint input, no snapshot field, no formula/rule-set bump; the pins do not move.
- **Judgment/typing hardening — four confirmed external-review defects, all read/display-side (spec 186).**
  Nothing here is hashed into any scoring identity; the spec-148/160 pins stand and
  `ScoringConfigFingerprintTests` is untouched. Four bounded fixes:
  - **A `Deteriorating` trajectory can no longer render the reassuring dot (§1).** `NewsJudgmentMarkerPolicy`
    mapped EVERY `Judged`-plus-zero-findings record to `NoChallengeFound` without consulting
    `BusinessTrajectory` — an ABSENCE claim rendered beside contrary presence evidence, the omission-bias
    failure reborn one seam past where spec 185 killed it, and LIVE from the first baseline run. Now
    `Judged` + 0 findings + `Deteriorating` ⇒ **`Challenged`** with the deterministic summary token
    `business-trajectory-deteriorating` (no finding is invented — the summary names the trajectory AXIS).
    The marker STATE vocabulary stays the closed 3-state set and **the validator is UNTOUCHED**: a
    zero-findings `Deteriorating` read is legitimate model output, because the spec-179 challenge taxonomy
    has no bucket for gradual decline — which is exactly why the trajectory axis exists. EVERY `Judged`
    marker now appends `· trajectory <token>` in BOTH judged states, uniformly, so the display is
    state-complete and the dot can never silently imply health; `Mixed`/`Unknown` + 0 findings therefore
    stay `NoChallengeFound` defensibly. A `Judged` record with a **NULL** persisted trajectory is an INVALID
    state, not an unknown one (the validator requires the token to parse) — it renders
    `? unassessed (invalid-record)`, never a dot. **The provenance claim was made TRUE, not asserted**: a
    new `### Judgment provenance — diagnostic appendix` names each judged row's `JudgmentId` with the
    judgments-store root stated ONCE — rendered only when a marker carries an id, so a null model, the
    pending placeholder and every pre-186 composition are byte-identical. **Enum-zero sub-fix taken, not
    deferred**: `NewsJudgmentTrajectory` had `Improving = 0`, making the BEST state the default value and
    inverting the spec-182 house rule; reordered to `Unknown = 0` after VERIFYING token-only persistence
    (`RadarFileStoreJson` uses `JsonStringEnumConverter(allowIntegerValues: false)` — integers are rejected
    on read — and the wire type is a `string?` parsed by `NewsTypingTokens`, which rejects all-digit tokens;
    no `(int)` cast, ordering or `Enum.GetValues` dependency exists). Pinned by test.
  - **Typing retries are BOUNDED and FAIR, and the bound is on HOSTED CALLS (§2).** The completed-only cache
    meant provider/parse/validation failures re-entered selection newest-first EVERY run forever; ~200
    persistently failing records would pin the whole `MaxNewTypingsPerRun` cap and starve the 13k backlog
    permanently. Attempt counts are **DERIVED** per `(cohortKey, observationId, payloadHash)` from the
    records the insert-only store already holds and the generator already loads — no new store, no side
    index. Two identity rules, both deliberate, because the OLD identity folded `runId` and mapped every
    null-run invocation onto one `"standalone"` id (so re-invocation called the model while the store
    deduplicated the record and the count never advanced): (a) **same-run idempotency** — within one `runId`
    an observation with a persisted attempt for this cohort is SKIPPED, no model call; (b) **every
    standalone (null-run) invocation mints a distinct persisted attempt identity** — attempt 1 keeps the
    literal `"standalone"` (every id already on disk is unchanged), attempt N > 1 is `"standalone#N"`,
    resolved once per pass from the pre-pass store snapshot (deterministic, clock-free, AD-3). **Invariant,
    asserted on the counting fake extractor's CALL COUNT, not on stored records: hosted calls for one
    (cohort, observation, payload) can never exceed `MaxTypingAttempts` under any mix of re-runs and
    standalone invocations.** `Radar:NewsResearch:Typing:MaxTypingAttempts` (default **3**, ≥ 1) and
    `MaxRetryTypingsPerRun` (default **25**, **≥ 1** — zero would re-permit total retry starvation and is
    REJECTED — and < `MaxNewTypingsPerRun`, the cross-field rule enforced at the config boundary) join the
    strict key allowlist. The **FIFO retry lane** reserves `min(MaxRetryTypingsPerRun, pendingRetries)`
    slots ordered by **oldest last-attempt instant first, then observation id** — NOT fewest-attempts-first,
    which still starved LATER attempts against a replenishing attempt-1 population — so
    `ceil(pendingRetries / MaxRetryTypingsPerRun)` runs-to-reach holds for every record in a pending
    snapshot (mutation-proven); unused lane capacity returns to first attempts, which fill the remainder
    window-newest-first then backlog-oldest-first as before. **Exhaustion is visible, never silent**: a
    per-cohort `RetryExhausted` count on the run result and the decomposition artifact, one aggregated
    Warning per cohort (the spec-145 precedent), and an in-window exhausted observation degrades its
    company's typing completeness to **`Failed`** (doc widened rather than a new token — `Backlog` literally
    means "deferred by the cap", which is a FALSE statement about an exhausted observation, and `Failed` is
    the zero/degraded value every consumer already handles). Both schema moves are NAMED:
    `NewsTypingLimitsRecord` gains the two limits **trailing + nullable** (pre-186 records hydrate as "not
    recorded", never a fabricated limit), and `news-typing-decomposition-v1 → v2` for the additive
    `RetryExhausted` (by-name readers unaffected — asserted). `NewsTypingRecord.CurrentSchemaVersion` stays
    `news-typing-v1` (the repo's trailing-nullable precedent: spec 142 `EvidenceQuality`, spec 148
    `EffectiveScoringConfig.Window`). **Behaviour change beyond retries**: re-invoking the same `runId` now
    skips already-attempted observations.
  - **Filesystem metadata — and TIMESTAMPS — leave the verdict path entirely (§3).** The paired-gate verdict
    instant was `File.GetLastWriteTimeUtc`, and the efficacy artifacts are rewritten every run, so a valid
    `overridesGate: true` call silently expired after ONE run (and a copy/restore did the same, and the
    instant was machine-dependent — the spec-184 reviewer's note, now closed). Time-comparing an override
    against a verdict is the wrong primitive; **identity-binding is the right one.** `GateVerdictIdentity`
    computes a **`gateVerdictId`** content hash over, in fixed canonical order: the gate CONTRACT identity
    (predeclared primary + `PrimaryWasPredeclared` + declared boundary + the new
    `Ad15GateReasonCodes.VocabularyVersion`), the **ADMITTED purged outcome blocks** (dates and per-block
    inputs — the evidence the verdict rests on), the price-gate verdict + ordered reason codes, and the
    AD-16 prerequisite identity + outcome + composite reasons. Deliberately EXCLUDED: every wall-clock
    instant, path/mtime/size, machine name, run id, and dropped/candidate dates that never entered the
    claim. `VerdictExists` mirrors `StrategyEvidenceStatusCalculator`'s GatePassed/GateFailed condition over
    the SHARED `Ad15GateReasonCodes.MeritFailureCodes`/`NonMeritCodes` (moved there, one definition, two
    consumers), so "an id is present" ⟺ "the reducer sees a verdict" cannot drift; no verdict ⇒ the column
    is EMPTY. Carried as **one additive run-level CSV column** (the paired CSV carries no schema tag —
    confirmed by test; the 33 pre-186 column names are pinned at their original indices) plus one markdown
    line so a maintainer can read the id without opening the CSV. `StrategyGateVerdict.VerdictAtUtc` →
    `VerdictId`; `PairedGateFact.ArtifactWrittenAtUtc` → `GateVerdictId`; the `File.GetLastWriteTimeUtc`
    read is DELETED and a repo-wide sweep found it was the only filesystem-metadata consumer on the path.
    **`OperatingCallReducer`'s timestamp comparison is DELETED**: an override applies iff its
    `overridesVerdictId` equals the artifact's current `gateVerdictId` (ordinal; an empty/absent id can
    never match). **`strategy-operating-calls-v2`**, named as such because the conditionally-required
    `overridesVerdictId` semantically REPLACES timestamp precedence: v1 stays readable and behaves exactly
    as today WITHOUT overrides, but cannot express one — a v1 file with `overridesGate: true` fails naming
    the remedy, and `overridesVerdictId` in a v1 file is an unknown property; the committed
    `data/strategy-operating-calls.json` is migrated (token only — no call carries an override). **A stale
    override is REPORTED, never silently dropped**: a `### Stale gate override` block names the arm, the id
    it bound to and the current id, and the gate default re-arms — new evidence SHOULD re-open the call. A
    **pre-186 artifact** (no column) ⇒ identity unknown ⇒ no override can match ⇒ gate default wins with ONE
    warning naming the artifact and the remedy; AD-8 preserved, unknown never fabricates an id. Reuse over
    copy: the hand-copied canonical-string→SHA-256 idiom was **extracted** into
    `Radar.Application.Identity.CanonicalHash` (the sibling of `DeterministicGuid`) and both the new
    identity and `NewsJudgmentInput.ComputeFamilySetHash` route through it; the remaining copies
    (`BenchmarkUniverse`, `NewsObservationIdentity`, `NewsRiskInputBundle`, `NewsEventTaxonomy`,
    `ScoringConfigFingerprint`) are hash-pinned identities left ALONE deliberately — a follow-up sweep.
  - **`fact-family-v2` — the id gained a DURABLE temporal anchor, and identity split from projection (§4).**
    v1 split same-claim facts >7 days apart into separate families but derived the id WITHOUT a temporal
    component, so recurring corporate news (quarterly dividend/buyback headlines, near-identical normalized
    statements months apart) produced separate episodes with COLLIDING ids and corrupted judgment
    provenance. Per spec 181 §4's own rule an identity-input change is a NEW builder version, never an edit.
    **TWO STAGES, because durable identity and the window representative are DIFFERENT jobs.** Stage 1
    SEGMENTS over ALL qualifying validated facts in the store — **preserving v1's membership algorithm
    VERBATIM** (representative-relative similarity within the 7-day proximity rule, greedy first fit over
    (instant, factId); NOT exact-canonical-key grouping, NOT transitive chaining — **membership semantics do
    not change in this spec, only identity and projection do**, and a parity fixture is the proof) — and
    yields each episode's durable anchor: the **first-ever member's `FirstObservedAtUtc` UTC date + that
    member's sorted `EventTypes`** (two same-statement episodes with DISJOINT types are different families),
    both immutable under window expiry because facts are append-only. Stage 2 PROJECTS each episode with
    ≥ 1 in-window member into the snapshot carrying the durable `FamilyId`, while representative, members,
    counts, publishers, event types, statement, claim key and earliest instant come from the **IN-WINDOW
    members ALONE**. **Do not collapse the two stages**: with one stage the first-ever member doubles as
    `RepresentativeFactId`, and `NewsJudgmentInputBuilder` DROPS any family whose representative is absent
    from the current-window fact index — so once the anchor aged out, a family carrying FRESH news would
    silently vanish from judgment, the exact opposite of the fix (pinned end-to-end through the judge). Id =
    `radar:fact-family:{BuilderVersion}:{companyId:D}:{captureMode}:{anchorDate:yyyy-MM-dd}:{sortedEventTypes}:{anchorCanonicalClaimKey}`;
    the segmentation scope, anchor rule and projection rule all enter `IdentityString`. Stage 1's candidate
    scan is bucketed by `(CompanyId, CaptureMode)` with episodes pruned once >7 days behind the fact being
    placed — an EXACT-equivalence transform (both conditions `CanJoin` already rejects, with creation order
    preserved so first fit is identical), without which a history-wide checkpoint would go quadratic.
    `FactFamilySnapshot.CurrentSchemaVersion` is **NOT** bumped (no field added/removed/re-meant; the
    builder change is recorded in `BuilderIdentity`, which spec 181 §4 already made the cohort
    discriminator) and `FactsConsidered`/`FactsWithoutCompany` keep their **WINDOW** basis (a snapshot is a
    statement about a window). **The ONLY id-shift case left**, recorded on the builder's doc comment: a
    late-arriving member temporally EARLIER than every member the episode ever observed shifts the anchor.
    v1 checkpoints, typing records and judgments on disk are untouched (AD-8); **expected one-time cost:**
    every family id changes on the first post-186 run, so the stage-2 cohort key (which embeds
    `families={FactFamilyBuilder.IdentityString}`) forks and every candidate company re-judges ONCE, draining
    under `MaxCompaniesPerRun`.
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
   still code. **Spec 153 added `radar-formula-v10` to `ScoreFormulaVersions.All`** (now
   `{ v8, v9, v10 }`, in version order) and to `RadarScoreFormulaFactory`'s dispatch; whether a formula takes
   a `Channels` budget is answered by the single predicate `ScoreFormulaVersions.ConsumesChannels`
   (`v9`, `v10`), which both `ScoringStrategySet`'s rules and the factory read. **And an in-place change to
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
