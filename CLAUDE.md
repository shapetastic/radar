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
    `radar-scoring-fp-57356123e09b → radar-scoring-fp-3457da53489d`. No `_formula.Version` bump, no
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
  byte-identical to before and the pins do NOT move** (AI-OFF `radar-scoring-fp-2ce20f8fc497`, AI-ON
  `radar-scoring-fp-3457da53489d` both unmoved). Rules:
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
    contributes MORE.** v8's inverse attention discount stays in v8; it is deliberately not carried over.
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
  - **Known pre-existing gap, deliberately NOT fixed here**: `ScoringWeights.TrajectoryCorroborationK` is not
    a `ScoringConfigFingerprint` field, so tuning it re-stamps nothing — for v8 *and* now for v9's channel
    direction factor. Adding it would move both pinned fingerprints, which this slice is required not to do.
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
    pins (`radar-scoring-fp-2ce20f8fc497` / `radar-scoring-fp-3457da53489d`) do not move.
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
    `_formula.Version` bump, no `RuleSetVersion` bump; the pins (`radar-scoring-fp-2ce20f8fc497` /
    `radar-scoring-fp-3457da53489d`) do not move and `ScoringConfigFingerprintTests` is untouched. Asserted:
    a full-mode and a score-mode graph over the same config stamp the SAME fingerprint and DIFFERENT
    provenance.
  - **Out of scope, recorded not built**: spec 140's strategy-vs-price comparison (which this unblocks), the
    `ScoringWeights.TrajectoryCorroborationK` fingerprint gap (moves both pins — its own spec), and
    backfilling `CollectionProvenance` on existing snapshots (append-only, AD-8: fix forward).
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
   still code.
7. A scoring-affecting **extractor rule-STRUCTURE** change (the `KeywordSignalExtractor` phrase→direction/strength
   table shape) bumps `KeywordSignalExtractor.RuleSetVersion` (parallel to `_formula.Version`) — it is folded into
   the `ScoringConfigVersion` fingerprint via `SignalSourceDescriptor` (spec 95, AD-10 amended). The
   **enabled-collector set** is captured automatically by that same fingerprint, so enabling/disabling a collector
   needs **no** bump — it re-stamps on its own. The **insider buy/sell materiality tiers + cluster boost** are now
   config too (`Radar:Insider` profiles bound onto `InsiderMaterialityWeights`, default == spec 93) and are hashed
   into that fingerprint **by value** (spec 96, AD-10 amended) — so a tier **magnitude** change is a **config edit**
   needing **no** `RuleSetVersion` bump; only a rule **structure** change bumps `RuleSetVersion`.
