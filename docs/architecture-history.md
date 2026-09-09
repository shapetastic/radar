# Radar architecture history — per-spec decision bullets

Moved VERBATIM from CLAUDE.md on 2026-08-31 (the file had outgrown the instruction budget).
These bullets are the authoritative per-spec record of architecture decisions, measurements,
reversals and fingerprint lineage for specs 137→199. CLAUDE.md keeps the standing rules and a
distilled current-facts section that points here.

Rules of this file (inherited from CLAUDE.md, unchanged by the move):

- **A REVERSAL is recorded where the original claim lives**: amend the superseded bullet IN
  PLACE; never append a second, contradicting bullet beside it. Two bullets that disagree are
  worse than one that is stale.
- **Never duplicate a value that code defines** — cite its owner (`ScoringConfigFingerprintTests`,
  `ScoreFormulaVersions.cs`, `default.json`). A pin quoted in any bullet older than the last
  pin-moving bullet is HISTORY, not today's stamp.
- **New spec bullets are APPENDED here** when a slice ships; CLAUDE.md gains a bullet only for
  a genuinely standing rule.
- **Planners and reviewers MUST consult the bullets for any subsystem a slice touches**
  (CLAUDE.md Step 1 / the review loop point here).

---

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
  - **`ISignalSourceDescriptor` has THREE members** (⚠ this bullet originally said two; `EnabledCollectors()` was added by spec 146, and the identity string is now `rules=…;[ai=…;]news=…;[newsquery=…;]` — the `news=`/`newsquery=` segments moved the pins in specs 197 and 198): `CanonicalDescriptor()` = strategy identity
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
    definition of the stable identity — since spec 205 `(CompanyId, EvidenceId, Type, Direction,
    FilingReadOutcomeRecorded)` (spec 85's key, extracted here, plus one boolean: true exactly for a
    `GuidanceChange` whose metadata carries the spec-204 `filingReadOutcome` envelope, via the shared
    `FilingReadSignalMetadata.IsFilingReadSignal`; false for every pre-204/non-read signal, whose identity
    is therefore unchanged). The fifth field exists because a keyword Neutral and an AI-read Neutral over
    the same filing otherwise shared one key and this collapse erased the read before
    `GuidanceChangeSupersede` — the rule built to prefer it — ever saw it; it is a provenance-class
    discriminator required by that downstream winner rule, **not** an invitation to hash extractor metadata
    into identity (it is deliberately not the outcome token, confidence, rationale, model or signal id, so
    repeated copies of the SAME read still collapse). The key is
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
    "some price but not the horizon" are different facts. ⚠ **AMENDED AGAIN BY SPEC 217 — the admission rule
    set is now VERSIONED (`ObservationEligibility.Version`) and has a FOURTH exclusion axis,
    `CorporateActionInWindow`.** An observation at as-of D is excluded, before any price is read, when a
    recognised acquisition of that company was announced on or before `D + h` (one predicate covering both
    halves of the rule: the announcement inside `(D, D+h]`, and D on/after the announcement). It is an
    outcome NO STRATEGY COULD HAVE EARNED — MarineMax's +46.1 % gap on 2026-08-10 sat inside the 21-day
    window of every score from 2026-07-20, and since then its price has been pinned at the $53.00 bid — so
    counting it as skill or as a miss measures the deal, not the scoring. It is evaluated FIRST so the four
    axes stay DISJOINT (an excluded company-day is never also "no forward price"), and de-duped on the same
    `(company, as-of)` key. It is rendered as its own column on `strategy-leaderboard.{md,csv}` (both
    halves) and, on the PAIRED artifact, in the markdown support table beside the two exclusion tallies it
    joins — the paired CSV carries the two RULE IDENTITIES per row but not the marginal count, because that
    CSV's row unit is the per-baseline pairing and it has never carried a per-strategy marginal exclusion
    column: `ObservationsWithoutForwardPrice` and `ObservationsWithPartialWindow` live only in that same
    markdown table (verified in `PairedComparisonRenderer`). Putting the new count there and nowhere else
    keeps all three exclusion tallies in one place and at one unit. The entry rule `bar.Date > asOf` is untouched and
    asserted with poison bars, including on the `PartialWindow` branch. **Honest consequence, and it IS the
    deliverable:** with ~1 month of price history almost every observation becomes `PartialWindow`, so the
    leaderboard correctly reports "No strategy could be ranked" at h=21 until roughly 2026-08-17. That is the
    right answer, not a regression.
- **The fingerprint is COMPLETE, and replay records the provenance it writes (spec 148).** Two closures, one
  slice, from the `radar-architecture-reviewer` sweep of `main` @ `b9b3f65`. **The pins THIS SLICE set were
  AI-OFF `radar-scoring-fp-0c46e07b94db` and AI-ON `radar-scoring-fp-28226897f97b`** — every "the pins do not
  move" above was true of its own slice and stays true of it; this slice moved them, deliberately, once.
  ⚠ **Both have since moved again — the AI-ON side by spec 160 (`… → radar-scoring-fp-ebd7d11a58d0`) and
  then BOTH by spec 191 (the `radar-keyword-rules-v6 → v7` bump). For the CURRENT values at all three
  windows, read the spec-198 bullet (the LAST pin-moving bullet) or — authoritatively —
  `ScoringConfigFingerprintTests`; every value quoted in THIS bullet is historical lineage, not today's
  stamp. ⚠ The pins have moved SIX times (191, 194, 196, 197, 198); do not trust a pin quoted in any bullet
  older than the last one.** Rules:
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
    in the outer loop. ⚠ Amended by spec 206 §1: until then a FAILED config write still warned and wrote the
    strategy's snapshots beneath the undereferenceable stamp — the write existed, the precondition did not.
    Replay now applies the spec-202 §1 durability precondition exactly as the forward `ScoringPass`: a
    `Failed` strategy's whole as-of × company loop is skipped before its store is resolved, named on
    `ReplayResult.StrategiesSkippedForUnpersistedConfig` (with `ReplayResult.Strategies` deliberately
    re-meant to count strategies that EXECUTED), and reported in one aggregated Warning per invocation.
    **Writing the scoring-config store is a PROVENANCE RECORD, not a scoring mutation:**
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
    structure still earns a NEW `radar-formula-vN`; the revision only makes a spec-149-style in-place adjustment
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
  content hash `97e31fde67655453e4bdee8f69eef07785db6f2c80124220176a5637829561fc`; ⚠ spec 199 later took the
  SEED to 94 while this artifact stays frozen at 74, so the 20 post-199 additions correctly report
  `NotInBenchmarkUniverse` until a prospective `benchmark-universe-v2` is declared), self-excluded, members
  resolving through the SAME spec-152 `ForwardReturn` rules. Rules: the artifact is the ONLY membership /
  price-series-key input (never `companies.json` — a seed edit moves nothing; expansion = a prospective
  `benchmark-universe-v2`, never an edit — the reader refuses a content-hash mismatch); the computation is
  CENTRAL (`UniverseBenchmark`, cached per (universe, D, horizon, tolerance), shared via
  `IUniverseBenchmarkProvider` by the spec-140 leaderboard and the spec-179 news-risk evaluator); the
  excess definition and coverage rule are CODE CONSTANTS (`required = max(40, ceil(0.90 × eligiblePeers))`,
  integer ceiling so ×10 counts don't FP-round up; unresolved members stay in the denominator with
  reasons); below the bound the pooled observation is excluded as **`BenchmarkUnavailable`** — named and
  counted, never a silent raw fallback — and a post-freeze company is **`NotInBenchmarkUniverse`**. The
  leaderboard now ranks by excess (⚠ **`excess-vs-universe-v2` since spec 217** — the v1 columns and CSV
  schema `strategy-leaderboard-v2` are HISTORY; the shipped values are `UniverseBenchmark.ExcessRuleVersion`
  and `StrategyLeaderboardRenderer.CsvSchemaVersion`. **The frozen `benchmark-universe-v1` MEMBERSHIP is
  UNCHANGED** — v2 is a rule about which members may represent the pond on a given date, not a re-selection
  of it: a member under a recognised pending acquisition leaves the equal-weight peer mean **and the
  coverage denominator** from its announcement date onward, because a member pinned at a take-out bid biases
  every OTHER company's excess downward. The removal is counted per as-of date on the artifact's coverage
  line, distinct from `UnresolvedMembers` — an excluded member's price resolves perfectly, which is exactly
  why it had to go. Declared PROSPECTIVELY (the 2026-09-29 AD-15 boundary is untouched);
  the pre-183 raw artifacts are preserved once as `strategy-leaderboard-raw-v1.{md,csv}` and the pre-217
  excess-v1 series once as `strategy-leaderboard-excess-v1.{md,csv}`, both declared incomparable, through
  ONE parameterised preservation step in `FileEfficacyArtifactStore` rather than a second copy of it;
  pre-freeze dates carry a retrospective label). **The AD-15 paired path has NO benchmark
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
    ⚠ **SUPERSEDED IN PART 2026-08-29 (PR #206, `d6244ac`):** the three `filings-led-*` arms are now **Stop**
    (oos ρ −0.159/−0.149/−0.139, every 95 % CI excluding zero; the notedness ablation pair inert — the
    filings-channel hypothesis fails, not the discount; scoring continues, prominence stops; re-called Trial
    if ≥ 24 oos dates show the CI upper bound > 0), `narrative-led-v2` stays Trial, and a new research arm
    **`default-noattn`** (`default` with both notedness discounts at 0, implicit Trial) tests whether the
    inverse-attention discount subtracts the component `baseline-media-only` (+0.137, the only clearly
    positive oos arm) predicts on. `disclosure-led-v11` ≈ its v10 control (Δρ 0.0035) — Lead unchanged,
    precommitted. The live strategy count is **11**. Journal: `docs/strategy-lifecycle.md`.
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
  ≠ Complete; never worded clean) / `? unassessed (reason)` from a closed vocabulary (8 at spec 185; **11 today** — `invalid-record` added by 186, `retries-exhausted` by 187, plus `not-persisted`) — derived ONLY
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
    index. ⚠ **BOTH OF THOSE CLAIMS ARE SUPERSEDED BY SPEC 187 §3 for TYPING**: an outcome record is written
    AFTER the call, so an outcome-derived count cannot bound CALLS (a crash, a cancellation or a `false`
    from `WriteAsync` spent a call and advanced the count by nothing). The "no new store, no side index"
    constraint is explicitly lifted — typing now takes a durable PRE-CALL reservation and the derived
    counter survives only as the legacy-occupancy migration read. The rule below still stands VERBATIM for
    stage-2 JUDGMENT, deliberately (see the spec-187 bullet's asymmetry note). Two identity rules, both
    deliberate, because the OLD identity folded `runId` and mapped every
    null-run invocation onto one `"standalone"` id (so re-invocation called the model while the store
    deduplicated the record and the count never advanced): (a) **same-run idempotency** — within one `runId`
    an observation with a persisted attempt for this cohort is SKIPPED, no model call; (b) **every
    standalone (null-run) invocation mints a distinct persisted attempt identity** — attempt 1 keeps the
    literal `"standalone"` (every id already on disk is unchanged), attempt N > 1 is `"standalone#N"`,
    resolved once per pass from the pre-pass store snapshot (deterministic, clock-free, AD-3). **Invariant,
    asserted on the counting fake extractor's CALL COUNT, not on stored records: hosted calls for one
    (cohort, observation, payload) can never exceed `MaxTypingAttempts` under any mix of re-runs and
    standalone invocations.** (Since spec 187 §3 that invariant is enforced by the reservation ledger and
    holds across crashes, failed outcome writes and concurrent processes as well.)
    `Radar:NewsResearch:Typing:MaxTypingAttempts` (default **3**, ≥ 1) and
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
- **The judge must CITE what made the call, and every hosted call is paid for before it is made (spec 187).**
  Written from the FIRST live typing+judgment run (`976d0f20`, 2026-08-24, 1h03) plus a post-run code
  review. The run proved the surface works and that a structurally complete judgment is not yet a sound one:
  every judgment ran at `TypingCompleteness = Backlog`, EOSE had 31 archived observations and 2 typings so
  the headlines that motivated the arc were invisible to the judge, and **MNRO's own persisted rationale
  said the supplied fact was neutral and then labelled the trajectory `Deteriorating` because the
  instruction demanded a direction** — a v1 prompt-CONTRACT defect, not a bad model day (CASS inferred
  decline from absence, WDFC `Improving` from absence, YORW read a 52-week price low as business execution).
  Nothing here moves a score, rank, label, strategy, snapshot, scoring fingerprint or AD-15/AD-16 claim; the
  pins do not move and `ScoringConfigFingerprintTests` is untouched. Rules:
  - **`news-judgment-v2` — a directional call must name its evidence (§1).** `news-judgment-prompt-v2` +
    `news-judgment-schema-v2` fork a new stage-2 cohort (v1 records stay readable, never rewritten). The
    response carries `TrajectoryFactIds`, and the validator makes them load-bearing: every id must parse, be
    distinct and be a SUPPLIED representative fact; `Improving`/`Deteriorating`/`Mixed` require **at least
    one**; **`Unknown` requires NONE** (it means no supplied fact established a balance, not that provenance
    was omitted); at least one cited fact must sit **at-or-above `reported`** — the SAME boundary the
    spec-185 attribution-caveat rule already used, so the two cannot drift; and the cited set may not be
    made ENTIRELY of `NewsJudgmentContextOnlyEventTypes` (price/analyst/ownership/promotional context is not
    business direction — the YORW shape). A `Judged` response additionally requires a non-blank factual
    rationale ~~≤ 1,000 chars~~ (⚠ **AMENDED BY SPEC 192 §1 — the 1,000-character bound no longer FAILS
    anything**: it is a recorded SOFT flag, the rationale is persisted in full, and only the new
    4,000-character HARD ceiling rejects the response — checked AFTER the findings loop. The NON-BLANK
    requirement, and the advice-language rule beside it, are untouched; see the spec-192 bullet), and a
    finding standing only on context-only evidence is dropped individually as
    `non-business-context-only`. **Deliberately NOT built: a prose polarity scanner over the rationale.**
    Grepping "declined"/"improved" would be a second, weaker judge with no provenance — the fix is a CITED
    contract at the structured seam, not string matching. It does not make the model infallible: a v2 call
    can still be wrong, it just cannot be unattributable.
  - **Bounded judgment failures, and the asymmetry with typing is stated rather than hidden (§1).** Strict
    validation makes a persistent `ValidationFailed` likelier, so each (stage-2 cohort, company, family set)
    gets `MaxJudgmentAttempts` (default **3**) CALL-PRODUCING attempts — `Judged`/`ValidationFailed`/
    `ProviderFailure`/`ParseFailure`, never `InsufficientFacts`, never the bound marker, never a cache
    reuse — DERIVED from the insert-only store read once per pass, plus same-run idempotency and the
    spec-186 `standalone#N` null-run identity. At the bound a **no-call `AttemptsExhausted` record** is
    persisted under its OWN identity namespace (`radar:news-judgment-exhausted:…`, run-scoped) so it can
    never be mistaken for a spent call and the row renders **`? unassessed (retries-exhausted)`** rather
    than a fabricated verdict. **The asymmetry is deliberate and documented on the class:** judgment gets
    NO pre-call reservation ledger, so a process killed between call and write can spend one unrecorded
    call — accepted because judgment is one serial call per company per run while typing spends hundreds.
    The budget is keyed on the **family-set hash**, so a materially changed fact set earns a fresh budget:
    the bound constrains repeated calls over the SAME input, never the evaluation of new evidence.
  - **Type the companies you are about to judge (§2).** The live run spent its whole 200-call budget on the
    global queue and then judged 18 companies whose motivating headlines were still untyped.
    `MaxCandidateTypingsPerRun` (default **100**) buys a third selection lane between the FIFO retry lane
    and the general queue, filled **ROUND-ROBIN** over the candidate plan (candidate-at-a-time would
    reproduce EOSE-style starvation inside the lane). The cross-field rule is **three-way** —
    `MaxCandidateTypingsPerRun + MaxRetryTypingsPerRun < MaxNewTypingsPerRun` when judgment is enabled
    (100 + 25 < 200 leaves 75) — so a general first-attempt slot is ALWAYS reserved and candidate priority
    can never stop the legacy backlog draining. There is **ONE shared candidate plan**
    (`INewsJudgmentCandidatePlanner` over the existing spec-179 selector, computed once per run and
    CONSUMED by both stages), which is what makes "typing-prioritized == judged" true by construction
    rather than by two agreeing copies of a selection rule. `news-typing-decomposition-v3` reports the
    per-lane counts.
  - **Every hosted typing call wins a DURABLE PRE-CALL reservation (§3) — and this SUPERSEDES spec 186 §2's
    "no new store, no side index".** `INewsTypingAttemptLedger` +
    `NewsTypingAttemptReservation` are keyed on `(cohortKey, observationId, payloadHash, attemptOrdinal)`
    and deliberately **NOT** on the run id: two processes racing for the same attempt must collide on the
    same file name and exactly one must win (`FileMode.CreateNew` is the atomic primitive). The protocol at
    the ONE site that calls the provider: (1) skip completed, (2) skip exhausted, (3) atomically claim the
    next ordinal, (4) only the winner calls, (5) persist the outcome LINKED to the reservation, (6) let only
    a durable outcome count. `WriteAsync`'s boolean is now CHECKED — an unpersisted outcome never enters the
    completed map, never contributes facts or families, and never reaches the judge. Occupancy is the union
    of reserved ordinals and LEGACY (pre-187, unlinked) outcome records, so 186's derived counter survives
    ONLY as the legacy-occupancy migration read and every accrued `standalone`/`standalone#N` id is
    byte-unchanged. `ReservedWithoutOutcome` counts reservations holding no linked outcome (crash,
    cancellation, failed write): the budget can be spent EARLY but never OVERSPENT, and that trade is
    reported per cohort rather than assumed.
  - **A final failed attempt is exhausted in the SAME run, and exhaustion is disjoint from backlog (§4).**
    Exhaustion was computed pre-pass, so a failure on the last permitted attempt was reported a run late and
    `BuildCompany` counted the same observation as BOTH typing backlog and retry-exhausted. One local rule
    now marks exhaustion pre-pass AND during the pass; `UntypedRemaining` means STILL ELIGIBLE (exhausted
    observations excluded, unpersisted outcomes included, because nothing durable was produced for them), so
    "the queue a later run can drain" and "work that has permanently left selection" are different numbers
    that reconcile. `news-typing-decomposition-v1 → v2 → **v3**` for the additive `ReservedWithoutOutcome`
    and — the reason a bump was owed rather than tidy — the CORRECTED meaning of `UntypedRemaining`.
  - **The structured gate decision outranks rendered reason text (§5).** `StrategyEvidenceStatusCalculator`
    still substring-searched the rendered `GateReasons` while the artifact already carried spec 186's
    semantic `gateVerdictId`, so a baseline NAME containing a reason-code token could make the status
    disagree with the very verdict identity `GateVerdicts(...)` carried for the same artifact. Now: a
    **non-empty `GateVerdictId` IS the writer's statement that a verdict exists** (the merit/non-merit split
    already ran writer-side over the STRUCTURED reasons), so `Qualifies` alone selects
    `GatePassed`/`GateFailed` and the reasons are DISPLAY DETAIL. A pre-186 artifact with no id falls
    through to an isolated legacy path that parses reason CODES and fails **CLOSED** (any accrual reason, or
    a blank/unparseable list, ⇒ `GatePending`). Id and status therefore cannot disagree BY CONSTRUCTION, and
    `OperatingCallReducer` is **untouched** — 186 §3's override binding is unchanged.
  - **The `_comment*` flattener repair is committed and the REAL failure boundary is tested (§6).** The
    2026-08-23 scheduled baseline crashed at startup (0xE0434352) because `run-radar.ps1` skipped only the
    exact key `_comment`, so the promotion's `Radar:NewsResearch:_comment2` reached the strict allowlist.
    The fix (skip every `_comment*`) is committed, and the test boundary now reaches the real failure across
    THREE places: `RunProfileMirror` (in `Radar.TestSupport`) is the ONE flatten mirror of the PowerShell
    rule — the anti-drift point, so a second copy can never mirror a stale rule —
    `RunProfileGuardCompatibilityTests` mirrors the `_comment*` prefix behaviour through it,
    `RunProfileNewsResearchGuardTests` (`Radar.Worker.Tests`) binds the FULL NewsResearch strict guards over
    the flattened real profile, and `RunRadarScriptWhatIfTests` (`Radar.Worker.Tests`) runs a
    Windows-conditional `run-radar.ps1 -Profile default -WhatIf` smoke test over the REAL script — the
    complete suite passing while clean HEAD crashed before doing useful work is the failure mode being
    closed.
  - **Provider-call timing: observability, not policy (§7).** Each typing/judgment provider invocation is
    bracketed by the injected `TimeProvider`'s MONOTONIC APIs (`GetTimestamp`/`GetElapsedTime` — never
    `DateTimeOffset` subtraction, never `Stopwatch`, never a wall-clock sleep in a test) and persisted as a
    TRAILING NULLABLE `ProviderDurationMs` on both attempt records; `null` means NO CALL (cache reuse,
    `NoContent`, `InsufficientFacts`, `AttemptsExhausted`), a failure that reached the provider RETAINS its
    duration, and neither schema tag moves for it. Bounded Information progress every **25** typing calls
    per reader / **5** judgment calls per judge×stage-1 cohort, plus the final partial batch, carrying
    attempted/selected, persisted successes, provider/parse/validation failures, stage elapsed, rolling mean
    and current max. Each stage then logs calls + **p50/p95/max/total** from ONE shared
    `ProviderCallTimings` helper (not two copies) over the CURRENT pass's in-memory durations, with the
    percentile definition stated and pinned: **sort ascending, rank = ceil(p/100 × n), 1-based, clamped to
    [1, n]** — nearest-rank, no interpolation. A zero-call pass renders **"0 provider call(s); no call
    latency measured this pass"** and OMITS the percentiles, because a measured zero and an unmeasured zero
    are different facts. Hashed into nothing, read by nothing: asserted that identical inputs with wildly
    different latencies produce byte-identical ids, cohort keys, family ids and selection order (AD-3), and
    that no log line carries model text, an API key or an environment-variable value. Calls stay SERIAL —
    no timeout, no concurrency change, no automatic fallback, and a 429 follows the existing named
    failure/retry path where the progress counters can see it.
  - **Baseline provider posture: one hosted reader, Ollama retained but unscheduled (§8).** `ollama-local`
    is removed from `Radar:NewsResearch:Shadow:Readers` in `default.json`; the DeepSeek entry STAYS, because
    a non-empty list REPLACES the ambient reader and deleting the list would take the hosted cohort with it.
    Baseline is now shadow = 1 hosted DeepInfra DeepSeek reader, typing = 1, judgment = 1. This is a
    SCHEDULING decision: the Ollama provider, option binding, manual-profile capability and provider tests
    all remain, the accrued `ollama:llama3.1` cohort data is untouched historical provenance (cohorts never
    pool), Ollama is NOT substituted into typing or judgment, and **no Claude CLI/provider/wrapper/cohort/
    fallback exists anywhere** — Claude may be evaluated later under its own explicit provider identity.
  - **Migration: NONE (§9).** No operator deletion or reset. The first post-187 run naturally creates the
    v2 judgment cohort and re-judges the current candidates once; **stage-1 typing stays in its EXISTING
    cohort** because selection priority and attempt accounting change no extractor prompt, schema or
    taxonomy; and existing facts, families, typings, judgments and assessments remain immutable (AD-8).
  - **Out of scope, recorded not built**: a prose polarity scanner over rationales, a durable pre-call
    reservation ledger for JUDGMENT, parallel provider calls / wall-clock stage cutoffs / dynamic
    throttling / automatic reader substitution, any Claude adapter or cohort, removing Ollama from the code,
    and feeding typing, trajectory, findings or markers into any score/rank/label/fingerprint calculation.
- **Two spec-187 claims were wrong at the seam, and spec 188 corrects them at the source.** Read-side and
  display-side only: no prompt, schema, cohort key, persisted record schema, score, rank, label, strategy,
  marker or AD-15/AD-16 rule moves, and the pins do not move.
  - **Durable call PROVENANCE is not current-pass ACTIVITY (§1).** `NewsJudgmentGenerator` inferred "this
    pass called the provider" from the persisted `NewsJudgmentRecord.ProviderDurationMs`, but a same-run
    reused attempt correctly carries the duration AND the failure status of its ORIGINAL call — so a re-run
    replayed old latency as current latency, counted a call that never happened, replayed an old
    provider/parse/validation failure into current totals, and let ANY later no-call candidate (same-run
    reuse, cross-run cache reuse, `InsufficientFacts`, `AttemptsExhausted`) re-emit the same `5/…`
    boundary. `JudgeOneAsync` now returns a private pass-local `JudgmentPassOutcome
    { Record, TimeSpan? ProviderCallDurationThisPass }` — transient orchestration state, never persisted,
    never a wire contract, never an identity input — set ONLY around an analyzer invocation this
    invocation made. Every spec-187 §7 metric reads it: attempted calls, latency samples, the three failure
    counters, `persistedJudged` (a current call producing `Judged` AND a durable `WriteAsync`), and the
    five-call boundary, which is now evaluated only immediately after a current call. `ProviderDurationMs`
    is UNCHANGED and a reused record keeps its original value in the store and in the run result — the
    in-memory copy must not disagree with the insert-only record on disk. An all-reuse pass logs the
    zero-call summary, emits no progress line and contributes no old failure. **Attempt counting and
    idempotency are untouched**: `JudgmentAttemptHistory`, the 3-attempt default, `standalone#N`, the
    exhaustion identity, cache identity and insert-only semantics all stand exactly as spec 187 shipped
    them. This fixes OBSERVATION of those decisions, not the decisions.
  - **"Partly understood" is not a verdict (§2).** The non-empty-`GateVerdictId` structured path is
    unchanged (spec 187 §5: `Qualifies` alone decides, reasons are display detail). Only the empty-id
    fallback changed: `ParseRenderedReasonCodes` silently DISCARDED unrecognised segments, so a list of one
    recognised merit failure plus one malformed/future segment collapsed to merit-only and became
    `GateFailed`. It now returns a `RenderedReasonParse { Codes, EverySegmentRecognised }`, and
    `GateFailed` requires a nonblank list, ≥ 1 segment, EVERY segment parsed and recognised against the
    closed `Ad15GateReasonCodes.All`, and every parsed code being a merit code — the writer-side
    `GateVerdictIdentity.VerdictExists` test verbatim, with completeness added. Anything else (empty list,
    blank segment, malformed baseline syntax, unrecognised/future code, prose, any non-merit code, any
    mixture) is `GatePending` with NO fabricated verdict id. The spec-187 baseline-name and free-form-detail
    spoof protections are retained. **The fallback is LIVE, not historical** — the method is now named
    `NoVerdictIdStatusFromRenderedReasons` because it serves pre-186 artifacts AND every current artifact
    whose gate has reached no verdict (every row in today's paired-comparison CSV); it is fail-closed
    because no structured verdict identity exists there, not because nothing reaches it. No known live
    mis-verdict existed: a well-formed current merit-only result carries an id and takes the structured
    path.
  - **The operational record (§3).** `scripts/run-profiles/default.json` no longer says judgment attempts
    are bounded "the same way" as typing's. Typing's `MaxTypingAttempts 3` bounds PROVIDER CALLS because
    every call wins a durable pre-call reservation; judgment's `MaxJudgmentAttempts 3` is separately derived
    from durably recorded call-producing outcomes plus same-run idempotency and deliberately has NO pre-call
    ledger, so a crash or a failed outcome write between call and persistence can spend an unrecorded
    judgment call.
- **Typing capacity is a DECLARED, falsifiable posture — 350/150/25 — and typing incompleteness finally has
  honest names (spec 189).** Written from the first post-187 baseline (`a180298d`, 2026-08-24, 58m10s, clean).
  Its headline result was semantic and good: MNRO moved from v1's marker-forced `Deteriorating` to a grounded
  v2 `Unknown` while EOSE stayed `Deteriorating` with two cited findings — Radar made a call when facts
  supported one and declined when they did not. The same run exposed the next limiting layer. Read/display
  side only: no NewsSearch reader/collector path, `MaxRecordsPerCompany`, evidence, score, rank, label,
  strategy, scoring fingerprint, snapshot field, marker policy or AD-15/AD-16 rule moves; the pins do not move
  and `ScoringConfigFingerprintTests` is untouched. Nothing accrued is deleted or rewritten, and the 30-day
  window is NOT narrowed. Rules:
  - **The capacity call, and its measured basis (§1).** `MaxNewTypingsPerRun` **350**,
    `MaxCandidateTypingsPerRun` **150**, `MaxRetryTypingsPerRun` **25**, `MaxTypingAttempts` **3**,
    `LookbackDays` **30** — declared EXPLICITLY in `default.json` **and** in both the `news-typing` /
    `news-judgment` overlays. That redundancy IS the fix: the overlays previously redeclared only the budget
    and the window, so the two lane widths came from the code defaults and selecting an experiment overlay
    could silently restore the pre-189 200/100 posture. Measured on `a180298d`: the 30-day window held
    **2,411** observations — 377 `Typed`, 17 `InsufficientContent`, **2,017 still eligible/untyped** (15.6 %
    fully typed) — the run CAPTURED **252** new observation files against a **200**-call cap (inflow exceeded
    capacity), and the 200 calls cost **508.6 s** of serial provider time (mean 2.54 s, p95 6.32 s, max
    32.32 s), so another 150 calls is ≈ **6m21s** — material but bounded beside a 58-minute baseline, and now
    measurable through spec 187/188's pass-truthful telemetry. **The hypothesis is explicit and falsifiable:**
    at ~252 captured per run and 350 durable completed outcomes, capacity exceeds inflow by roughly **98
    observations per run**, so the 2,017 backlog clears in about **21 runs** before retries, validation
    failures and inflow changes. That is a PREDICTION, not a promise. The **ambient code defaults stay
    200/100** (`NewsTypingWorkerOptions` / `NewsTypingOptions.DefaultMaxCandidateTypingsPerRun`): the increase
    is a measured operating decision for the checked-in scheduled profile, not permission for any caller that
    merely enables typing to spend 75 % more. The three-way cross-field rule is unweakened — 150 + 25 < 350
    reserves **≥175** general first-attempt slots — and nothing auto-tunes: no feedback controller, no
    queue-depth/latency/failure-driven budget, no silent config mutation.
  - **`Failed` split into `RetryableFailure` vs `RetryExhausted` (§2), ordinals frozen.** `RetryableFailure`
    = a provider/parse/validation failure, a refused attempt reservation or an unpersisted outcome THIS pass,
    with no in-window observation exhausted — degraded today, still eligible. `RetryExhausted` = at least one
    IN-WINDOW observation has spent all permitted attempts: a permanent hole for that
    `(cohort, observation, payload)`. Both are APPENDED, so `Failed = 0` / `Backlog = 1` / `Complete = 2` keep
    their values and the zero value stays the degraded one; persistence is token-based
    (`JsonStringEnumConverter(allowIntegerValues: false)` REJECTS integers on read), verified before relying
    on it. `Failed` stays READABLE for accrued records and defensive hydration but is **never newly computed**,
    and an old `Failed` judgment is **never** retro-classified into a guessed state (AD-8). Precedence is
    total and conservative: exhaustion → retryable failure → backlog → complete. One observation may sit in
    `UntypedRemaining` (the disjoint population partition, "work still eligible") while ALSO explaining a
    company-level `RetryableFailure` (current-pass provenance, "why this read degraded today") — different
    questions, deliberately not one number. **Recorded asymmetry, decided not overlooked:** the FAILURE set is
    pass-wide (a legacy-backlog failure degrades the company too, exactly as pre-189 `Failed` did) while
    EXHAUSTION stays window-scoped (spec 186 §2); narrowing failures to the window in the same slice that
    splits the token would have silently UPGRADED companies to `Complete`, and degrading is the safe
    direction. **Its one reachable edge is recorded, not fixed** (narrowing is its own decision): an
    OUT-OF-WINDOW observation spending its FINAL attempt this pass marks the company failed (pass-wide)
    but not exhausted (window-scoped), so that company's token reads `RetryableFailure` for an
    observation that is really exhausted — TOKEN-only, since the artifact row projects retryable
    failures through the exhaustion-excluding rule (its eligible-backlog count stays 0) and the marker
    policy treats every non-`Complete` value identically. `NewsJudgmentRecord.CurrentSchemaVersion` →
    **`news-judgment-v3`** for the widened persisted vocabulary — and ONLY that:
    `NewsJudgmentContract.PromptVersion`/`SchemaVersion`, the stage-2 cohort key and the model request
    are asserted unchanged (typing completeness is run provenance the judge never sees),
    so completed cached verdicts stay reusable and carry the **current** run's token. Marker state and wording
    are untouched: every value other than `Complete` still makes a zero-finding dot say "(typing incomplete)",
    and the exact token is visible in the judgment appendix rather than turned into a fabricated company
    challenge.
  - **`news-typing-decomposition-v4` shows inflow, retries, CALLS and retryable failures (§3).** Additive and
    trailing throughout; a v1 **or v3** by-name consumer reads a v4 document unchanged (asserted against the
    production writer), and existing artifacts stay immutable. Document level: `NewsObservationBatchId` and
    `ObservationsCapturedThisRun` (the batch's durable `ObservationsWritten` — **never** a timestamp-derived
    estimate; `null` when the batch is unresolvable), plus one AUTHORITATIVE pass-wide reader summary per
    extractor cohort (all three lane selections, provider calls attempted, completed outcomes, provider/parse/
    validation failures, reservation refusals, failed outcome writes, `RetryExhausted`,
    `ReservedWithoutOutcome`, `UntypedRemaining`). Per company × capture mode: `RetrySelected`,
    `ProviderCallsAttempted`, `RetryableFailuresThisRun`. **`RetrySelected` had been missing and that is why
    the live run read wrong**: 100 candidate + 99 general looked like an unused slot against a 200-call budget
    when it was 100 + 99 + **1 retry** (AXGN attempt 2). **SELECTIONS and CALLS are deliberately different
    numbers** — a refused reservation is a selection that never became a call — and both project from ONE
    per-observation record on the pass, so the rows and the totals cannot disagree. **The pass-wide summary is
    AUTHORITATIVE for the budget and may legitimately exceed the sum of the in-window company rows** (a
    selected legacy-backlog observation is outside the window; a company-less observation is in no company
    section); the artifact SAYS so in rendered text rather than silently claiming equality. The partition is
    unchanged: `Typed + InsufficientContent + UntypedRemaining + RetryExhausted` = eligible in-window
    observations — retry selections, calls and retryable failures are diagnostics, never extra buckets.
    Retryable failures render their OWN named line ("typing retryable failure this run: N observation(s) …;
    they remain in the eligible backlog"), separate from backlog and from exhaustion's permanent-hole wording.
    The `a180298d` shape is a regression fixture built from CONSTRUCTED records (never a copy of a mutable
    live file): 100 + 99 + 1 = 200 calls, five stage-1 validation failures over four judgment candidates,
    `RetryExhausted` 0.
  - **The moving candidate denominator, stated because it will otherwise be misread (§1/§4).** The live
    candidate set held 626 in-window observations (468 untyped) and the candidate lane only rises 100→150.
    Companies enter and leave the nominated set every run, so newly admitted untyped histories refill the lane
    and can keep `Complete` rare even while the global backlog drains. **Continuing candidate incompleteness
    ALONE is not evidence that the budget is too low** — the review must split RETAINED candidates from
    entrants and exits and compare their coverage separately.
  - **The three-run review method (§4), which has NOT happened yet.** After three successful post-189 nightly
    runs, REVIEW rather than auto-tune: inflow versus actual typing calls and the change in
    `UntypedRemaining`; candidate completed-typing coverage split retained/entering/exiting; candidate-set
    churn; retryable failures and exhaustion; typing p50/p95/max/total and provider-failure rate; and the
    observed net backlog movement against the predicted ~98/run drain — **naming the reason for any material
    miss rather than silently revising the baseline**. That review may justify another explicit decision; this
    spec adds no controller.
  - **Migration: NONE.** No deletion, reset, replay or cohort migration. Existing observations, evidence,
    signals, scores, typings, reservations, families, judgments and efficacy artifacts remain immutable; stage-1
    typing stays in its existing cohort (no prompt/schema/taxonomy change), and the first post-189 run simply
    starts writing v3 judgment records and v4 decomposition artifacts.
  - **Out of scope, recorded not built**: raising `Radar:News:MaxRecordsPerCompany` or admitting more
    NewsArticle evidence (spec 190's NewsSearch local-limit audit is a separate slice), narrowing the window
    or ageing data early to improve a percentage, changing typing prompt/schema/taxonomy, fact-family identity
    or judgment prompt/result-schema/cohort, changing marker state or treating incomplete typing as a company
    challenge, parallel calls / dynamic throttling / automatic fallback, and rewriting old `Failed` judgments.
- **The NewsSearch limit is RADAR'S OWN, and the audit measures it without admitting one extra article
  (spec 190).** The first post-187 baseline reported `ResultLimitReached` for every judged company and every
  NewsSearch capture row — which proved nothing, because the baseline configures
  `Radar:News:MaxRecordsPerCompany = 25` and `HttpNewsSearchReader` simply stopped retaining there. "The
  response held exactly 25 valid items" and "the response held more and Radar stopped reading" were
  indistinguishable, and the durable aggregate was even named `AnyFeedHitProviderCap` while the stored fact
  was only that Radar reached its own configured limit. **Diagnostic-only, read-side: not one additional
  evidence item, observation candidate or scoring input is admitted**, and no score, rank, label, strategy,
  scoring fingerprint, snapshot, marker policy or AD-15/AD-16 rule moves — the pins do not move and
  `ScoringConfigFingerprintTests` is untouched. Rules:
  - **The retained PREFIX is byte-identical; the tail is the SAME already-fetched body.** `Parse` no longer
    `break`s at the requested limit: it keeps scanning the already-loaded `XDocument` under the UNCHANGED
    absolute ceiling (100 valid items, which now bounds prefix + tail TOGETHER, deliberately not raised),
    counting structurally valid link-bearing items under the same "no `<link>` ⇒ skip" rule and collecting
    the beyond-prefix ones into `NewsSearchReadResult.DiagnosticTail`. Prefix and tail go through ONE
    extracted `BuildItem` path, so a tail item is exactly what the prefix would have held — the audit
    compares like with like. **No extra request, page, article fetch or pacing change** (asserted on a
    counting handler: one call per feed, search endpoint only). `ObservedValidItemBeyondLocalLimit` is
    DERIVED (`ValidItemsObserved > Items.Count`) so it cannot drift; a failure carries no diagnostics; and
    the legacy `Success(items)` factory still works, recording "no item observed beyond the limit" — which
    is exactly what an unscanned response can honestly claim.
  - **The collector maps NOTHING new.** The evidence + observation loop runs over exactly the same retained
    prefix; `MapToEvidence`/`MapToObservation` are never called for a tail item. A separate diagnostic pass
    applies the EXISTING `IsRelevant` rule and dedupes tail URLs against **every retained-prefix URL** — all
    of `result.Items`, **not** the evidence loop's `seenUrls`, which is incomplete because that loop breaks
    once the per-feed cap is met — and against earlier tail items, in its own set. The output is one count:
    additional unique company-relevant items observed and deliberately not admitted.
  - **Three honest states, none of them a provider fact**: *possible truncation* (`HitEffectiveResultLimit`
    — the prefix filled Radar's own limit), *confirmed local truncation* (a valid item really was observed
    beyond it) and *below limit*. **`HitEffectiveResultLimit` and the closed `ResultLimitReached` token keep
    their EXACT fail-closed semantics** — nothing upgrades or gates on the new confirmed fact, because
    observing no tail still cannot prove the provider had no further results, so AD-16 / news-risk coverage
    cannot silently upgrade.
  - **Provenance, correctly named, trailing and nullable.** `CollectorCompanyCoverage` gains
    `EffectiveResultLimit` / `MaxValidItemsObserved` / `ConfirmedLocalTruncation` /
    `UnadmittedRelevantTailItemCount`; on an accrued row **`null` means NOT RECORDED, never `false`/`0`**
    (pinned by a legacy-JSON hydration test through `FilePipelineRunStore`). `NewsObservationCollectorCapture`
    gains `AnyFeedHitEffectiveResultLimit` + `AnyFeedConfirmedLocalTruncation` (both nullable, `null` when no
    row recorded the diagnostic), while **`AnyFeedHitProviderCap` stays a readable non-nullable HISTORICAL
    MISNOMER** that new captures keep MIRRORING for old readers — **the mirror is not evidence about provider
    behaviour**, and new code reads the new fields and treats the old member only as a legacy fallback. The
    `c with { Issues = ... }` health amend preserves the new fields (asserted, not assumed). No historical
    batch, artifact, observation, evidence, signal, score, typing or judgment is rewritten.
  - **One aggregated first-run audit line** (Information, deterministic, advice-free): companies at the
    effective LOCAL limit, companies with a confirmed tail beyond it, additional unique company-relevant tail
    items not admitted, max + median observed valid response size, and the UNCHANGED admitted evidence /
    observation-candidate totals. The median is a small documented private helper (mean of the two central
    values on an even count) and it is NOT reused from `AttentionArrivalScreenEvaluator.Median` — that one is
    `internal` to `Radar.Application`, which Infrastructure cannot reach, and is defined over the efficacy
    screen's doubles; the two agree on the even-count convention on purpose. A pass with no successful feed
    renders `max n/a, median n/a` rather than printing an unmeasured zero as a measured one.
  - **The two similarly named keys stay separate, and both stay 25.** Only `Radar:News:MaxRecordsPerCompany`
    governs this path; `Radar:Gdelt:MaxRecordsPerCompany` belongs to the GDELT collector and is out of scope
    *by configuration path and reader type*, not by current enablement. Pinned by binding the two to
    DELIBERATELY DIFFERENT values and asserting the newssearch collector reads the `Radar:News` one, plus a
    test holding both shipped values at 25 (code defaults and `appsettings.json`).
  - **Out of scope, recorded not built**: raising either limit, admitting a tail item as evidence / an
    observation candidate / a scoring input, changing request count, query construction, pagination, pacing,
    article fetching or provider choice, upgrading `ResultLimitReached` to complete enumeration, and any
    sidecar-only expansion. **Any later proposal to raise `Radar:News:MaxRecordsPerCompany` is its own spec**
    and must state how the extra `NewsArticle` evidence affects scoring/fingerprints and how the extra
    observation inflow will be typed against spec 189's budget.
- **An over-long rationale must not discard a judgment's findings — the length gate returned BEFORE the
  findings loop (spec 192).** `NewsJudgmentValidator.Validate` rejected the WHOLE response when the
  rationale exceeded `MaxRationaleLength` (1,000), with the `return` placed ahead of the findings loop, so
  the findings were not judged invalid — they were **never examined at all**: no citation check, no
  attribution-caveat rule, no context-only gate. Measured on the live store: **4 of 18 judgments failed
  validation on 2026-08-25 (22 %), three for length alone**, over rationales clustered at **1,095–1,228**
  characters — CVLT lost **3** findings, LBRT **2** — and the text was NULLED rather than persisted, so only
  `rawResponseHash` survived. Those rows rendered `? unassessed (validation-failed)`, which reads as "Radar
  has nothing on this company" when it had produced specific findings: the omission-bias shape one seam past
  where spec 186 closed it, a FORMATTING gate suppressing PRESENCE claims. It also suppressed the input spec
  191 wires into scoring, which is why this slice went first. Read/display side only: no prompt version,
  result schema, stage-2 cohort key, fact-family identity, marker state/vocabulary/policy, score, rank,
  label, strategy, snapshot field or scoring fingerprint moves; the pins do not move and
  `ScoringConfigFingerprintTests` is untouched. Rules:
  - **The soft bound FLAGS; it never discards (§1).** `MaxRationaleLength` (1,000) stays as the named
    constant and is still what the judge prompt asks for — the prompt is UNCHANGED — but exceeding it now
    records `RationaleOverSoftLimit` and nothing else. The rationale is persisted **IN FULL and deliberately
    never truncated**: a shortened rationale is a FABRICATED explanation, and spec 187 §1's "a judgment Radar
    cannot explain is not a judgment" requires the real one. **ABSENCE of an explanation justifies discarding
    a response; VERBOSITY of one does not**, and the pre-192 validator treated the two identically.
  - **A hard ceiling still rejects genuine malformation — AFTER the findings are validated and counted
    (§1).** `MaxRationaleHardLimit` **4,000** with its own reason code **`rationale-exceeds-hard-limit`**,
    whose text names the ACTUAL length. It is checked after the findings loop on purpose, so the accumulated
    per-finding drop reasons and `FindingsTotal` are still reported, and the over-long rationale is still
    carried onto the FAILED result rather than nulled — unrecoverable text is precisely the complaint.
  - **The ordering bug is fixed, and it was its own defect (§1).** The advice-language scrub ran AFTER the
    length check, so an over-long rationale was returned unscrubbed — the rationale most in need of the house
    rule was the one exempt from it. Order is now trim → `AdviceLanguageGuard` scrub → blank check → measure.
  - **`rationale-missing` and the advice-language rule are UNCHANGED and still fail the whole response.**
    The reason string is pinned BYTE-IDENTICALLY by test; a scrubbed-to-empty rationale still fails as
    `rationale-missing`, never as a clean-looking zero-finding read. Spec 185's fail-closed
    all-findings-invalid ⇒ `ValidationFailed` rule is untouched: findings failing on their OWN merits are
    never rendered as "no challenge found". Only the LENGTH rule moved.
  - **`RationaleLength` / `RationaleOverSoftLimit` are TRAILING and NULLABLE, and the tag does NOT bump
    (§2).** `null` means NOT RECORDED — a pre-192 record, or an attempt that never produced a validated
    response (provider/parse failure) — never a fabricated `false`/`0`. `RationaleLength` is the length of
    the rationale **as persisted** (trimmed and advice-scrubbed), so it can never disagree with the text
    beside it. `NewsJudgmentRecord.CurrentSchemaVersion` stays **`news-judgment-v3`** on the same test v3
    itself was granted on: no field is removed or re-meant and no persisted VOCABULARY changes (spec 189
    bumped because the completeness vocabulary widened; nothing comparable happens here) — the
    trailing-nullable precedent of spec 142's `EvidenceQuality` and spec 148's
    `EffectiveScoringConfig.Window`. A reused verdict carries the CACHED values, so a replayed judgment
    never reads as "not recorded" beside the very rationale it carries forward.
  - **The bound becomes a MEASURED signal instead of a silent destroyer (§2).** One aggregated per-cohort
    **Information** line (the spec-145 precedent — not one line per judgment), rendered only for a cohort
    with a non-zero count, saying the full rationale is persisted and the findings were validated on their
    own merits. Information, not Warning: a long rationale is a prompt-tuning fact, not a fault. It counts
    ONLY judgments this pass actually called the provider for (**spec 188 §1** pass-truthfulness) — a reused
    verdict legitimately carries the ORIGINAL call's rationale length, and replaying it would report old
    prose as current activity on exactly the re-run path that telemetry exists to explain.
  - **Previously-failed judgments retry NATURALLY; nothing is rewritten.** `ValidationFailed` is not a
    completed status (spec 181), so CVLT, LBRT, CASS and GTY re-enter selection and are re-judged under the
    corrected validator, bounded by spec 187's `MaxJudgmentAttempts = 3`. Existing records stay exactly as
    they are (insert-only, AD-8) — no backfill, no migration, no re-judge of the whole candidate set (the
    cohort key is `judge|prompt|schema|stage1|families`; validator RULES are not one of its inputs). The
    four lost rationales are unrecoverable: only their response hashes were kept. **Intended effect: more
    judgments reach `Judged`, so more leaders rows carry a real marker instead of
    `? unassessed (validation-failed)`.**
  - **Mutation-proven, not asserted.** Restoring the pre-192 ordering turns the CVLT-shaped fixture (a
    1,228-character rationale with three valid findings) red — it reports `ValidationFailed` with zero
    findings and a null rationale — along with 7 of the other 9 spec-192 tests; the ordering fix has its own
    proof (advice language inside a long rationale is scrubbed FIRST, then fails as `rationale-missing`).
  - **Out of scope, recorded not built**: truncating or summarising a rationale, changing the judge prompt /
    result schema / taxonomy / fact-family identity / any cohort key, re-judging or rewriting historical
    records, reviving the four lost rationales, changing the marker vocabulary or policy, and spec 191's
    wiring of the judgment into scoring — this slice only stops suppressing its input.
- **News is DIRECTIONAL in the signal layer (spec 191) — ⚠ THE EXTRACTION-TIME ARTICLE-INHERITANCE SEAM IS
  SUPERSEDED AND DELETED BY SPEC 194 §1.1.** The DIAGNOSIS stands and is unchanged: `KeywordSignalExtractor`
  turned EVERY news article into exactly one **Neutral `MediaAttention`** signal and never read the headline
  for meaning. Measured over a 4,000-signal sample of 2026/08 signals: **98.4 % Neutral, 96.75 %
  `MediaAttention`** — so scoring consumed news as **VOLUME**, close to a size proxy, while specs 177–190
  built a two-stage read producing exactly the missing fact (cited typed facts + a grounded
  `BusinessTrajectory`) that reached nothing but one marker column. An earlier draft proposed an eleventh
  strategy arm consuming the judgment; that was rejected as preserving ten measurements of a broken input.
  Spec 191's FIX is what did not survive contact: it did not ground the direction in the article, so it
  reproduced the volume proxy it was written to remove. Rules:
  - ⚠ **WHY the 191 read was withdrawn (spec 194 §1.1), stated first because everything below depends on
    it.** `NewsDirectionalReadSource` ran at **EXTRACTION** — i.e. **before the current run's judge had
    produced anything** — so it paired THIS article's `ObservationId` with the company's **LATEST** admitted
    `JudgmentId` and **never checked that the judgment had cited this article**. The admitted judgment
    necessarily rested on EARLIER articles. One verdict was therefore **inherited by every later headline the
    company collected**, multiplying a single judged call into **N units of directional mass**, N being the
    company's news volume — **reintroducing the news-volume size proxy spec 191 set out to remove**, now
    carrying a direction and a provenance envelope that made it read as grounded. Withdrawn, not patched: the
    extractor's news branch emits the Neutral `MediaAttention` signal again, and
    `INewsDirectionalReadSource`, `NewsDirectionalRead`, `NewsDirectionalReadSource`,
    `NewsDirectionalReadOptions`, the `CollectionPass` per-run prepare call and the DI registration are
    **DELETED** — as are `NewsDirectionalReadBoundaryTests`, `NewsDirectionalReadSourceTests`,
    `CollectionPassNewsDirectionalPrepareTests`, `NewsDirectionalProvenanceChainTests` and
    `KeywordSignalExtractorNewsDirectionTests`. The replacement — one judgment-DERIVED signal anchored to the
    evidence the judgment actually cited — is spec 194 §1.2, and it is **SHIPPED** (see its own bullet
    below).
  - **The join is DERIVED ON READ, company-scoped and FAIL-CLOSED — and NOTHING is persisted. RETAINED:
    `NewsObservationEvidenceJoin` survives 194 §1.1 intact** (it is consumed by `NewsTypingGenerator`, not
    only by the deleted read) and is the observation↔evidence primitive spec 194 §1.2 builds the
    judgment-derived signal on. An observation record carries `companyId`/`headline` but no evidence id, and
    spec 145 made evidence identity the normalized **title+body** hash, so a title-only join is a heuristic,
    not an identity.
    `NewsObservationEvidenceJoin` keys on `NewsTextNormalization.Normalize(headline)` vs
    `Normalize(evidence.Title)` — the fact layer's OWN normalization, **EXTRACTED and shared, never a second
    normalizer** (`FactFamilyBuilder` routes through it and its `IdentityString` is pinned byte-identical, so
    no family re-keys and the stage-2 cohort does not fork). A blank key never joins; a null-company
    observation never joins; a key joins iff **exactly one** news evidence item carries it **AND exactly one
    distinct company claims it** (two-or-more evidence ⇒ ambiguous; two-or-more companies ⇒ ambiguous — which
    is what makes "a same-headline article belonging to a DIFFERENT company never joins" TRUE rather than
    likely). Several observations of one article report the **lowest ordinal `ObservationId`** (AD-3). Counts
    partition **OBSERVATIONS** (joined / unjoined-no-match / unjoined-ambiguous) and are reported in ONE
    aggregated `Information` line per index build — the spec-145 aggregation precedent. **No side index**
    (spec 151's recorded precedent: a derived-on-read function beats a materialized cache that can drift).
  - **Admission (§3), every condition required, latest-wins — the RULE is kept, its 191 IMPLEMENTATION is
    deleted.** These conditions are correct as far as they go and spec 194 §1.2 reuses them; what 191 lacked
    was the one condition that mattered — that the judgment CITED the evidence being signalled — which is why
    "latest-wins **per company**" was the inheritance bug rather than a tie-break detail. The judgment must
    come from the **prospectively designated** presentation cohort (`Radar:NewsResearch:Judgment:PresentationCohort`,
    composed at wiring from the SAME `NewsJudgmentReaderIdentity.CohortKeyFor` /
    `NewsTypingReaderIdentity.CohortKey` the leaders marker resolves, so the SCORED cohort and the DISPLAYED
    cohort cannot drift), its status must be `Judged`, it must carry a non-null trajectory, and it must
    satisfy spec 136's `CreatedAtUtc <= asOfUtc`. `ValidationFailed` / `InsufficientFacts` /
    `ProviderFailure` / `ParseFailure` / `AttemptsExhausted` are **not directions**. Latest per company wins,
    ties on the **lowest `JudgmentId`**.
  - **Mapping, and what it does NOT touch. RETAINED: `NewsTrajectorySignalRules` survives 194 §1.1**, no
    longer as the article-inheritance rule but as the mapping the §1.2 judgment-derived signal will carry;
    the magnitudes are unchanged and it is currently reachable from no production path. `Improving →
    Positive`, `Deteriorating → Negative`, `Mixed`/`Unknown` → **Neutral** (genuine both-ways evidence is not
    a direction; a judge that declined has not called). Strength = `4 + min(findings, 3) + (typing Complete ?
    1 : 0)`, range **4–8** — the base IS the Neutral strength, so a directional read is never weaker than the
    attention event it replaces, and a supportive `Improving` read legitimately carries ZERO findings (spec
    185 findings are challenge-only) and lands at base. `Novelty` (4), `Confidence` (0.5), `CompanyMention`,
    the excerpt and the output summary are UNCHANGED on both paths. **`SignalType` stays `MediaAttention`** —
    a new type would silently fall outside every declared `SignalTypes` filter and every v9/v10/v11 channel
    budget.
  - **Provenance is MANDATORY, and it is enforced by construction. RETAINED, and now READ before it is
    written.** The 191 directional signal recorded `newsJudgmentId`, `newsJudgmentCohortKey`,
    `newsObservationId` (+ the trajectory token) through the SHARED `EvidenceMetadata.Compose` envelope.
    `NewsDirectionalSignalMetadata` keeps those key definitions after 194 §1.1 — the signals 191 wrote are on
    disk and are append-only, so the keys are the SHAPE §1.4's legacy-inheritance transform must match on and
    the shape §1.2's versioned envelope extends. Its `Compose` overload went with the deleted producer.
    `ExtractedSignal` and `Radar.Domain.Signals.Signal` keep their **trailing, nullable** `MetadataJson`
    (mirroring `EvidenceItem.MetadataJson`), persisted by `FileSignalStore` as a trailing property **omitted
    when null** — so every already-written file and every metadata-free signal is byte-unchanged, and an
    absent property hydrates as `null` = NOT RECORDED.
  - **Neutral is once again the ONLY news case.** 191 framed Neutral as the honest fallback beneath a
    directional read; after 194 §1.1 there is no read above it, and `KeywordSignalExtractorNewsNeutralityTests`
    pins that the extractor's news branch is unconditionally Neutral with no judgment dependency of any kind.
  - **The architecture guards were NOT weakened — and after 194 §1.1 there is no seam left to guard.** Spec
    177's acquisition-only guard and spec 179 §10's transitive guard keep their exact namespace lists; the
    extraction-side boundary test 191 added was deleted with the seam it described, because a guard over a
    type that no longer exists is a claim about nothing. `NewsObservationArchitectureGuardTests` and
    `NewsRiskArchitectureGuardTests` still hold the standing claims.
  - ✅ **§1.5 IS SHIPPED — `media-collapse-v2`: a grounded direction now survives the same-event collapse.**
    The gap spec 191 recorded as DORMANT went live the moment §1.2 minted a directional news signal: the
    spec-109 collapse buckets `MediaAttention` signals by observation-time proximity and v1 kept the
    **earliest-observed** representative, so a bucket holding the grounded signal and an earlier unread
    Neutral duplicate kept the Neutral one — Radar would hold a validated read and score the company's news
    as plain attention. `MediaAttentionCollapse.Version` is now **`media-collapse-v2`** and only the choice
    of representative INSIDE each completed bucket changed: (1) a structurally valid
    `news-judgment-signal-v1` signal beats an ordinary media signal; (2) among materialized signals the
    latest `CreatedAtUtc`, then lowest `Id` (the SAME rule §1.3 applies, so the two steps can never disagree
    about which grounded read is current); (3) with no materialized signal in the bucket, the EXACT v1
    earliest-observed/lowest-id rule — pinned instance-for-instance, so an all-ordinary bucket is
    byte-identical to v1. **The greedy event-window BOUNDARIES are unchanged and asserted, not assumed:** the
    window is still measured from each bucket's EARLIEST member, never from the chosen representative —
    otherwise the existence of a judgment could widen or shrink a bucket and silently move the collapsed
    counts (mutation-proven: anchoring the window on the representative turns the boundary test red). The
    collapsed count stays exact (every other member of the bucket) and is keyed by the representative; the
    representative is always a REAL persisted signal, never a synthesized composite.
  - **Wiring was gated on judgment; after 194 §1.1 there is nothing to wire.** 191 registered the seam inside
    `AddRadarNewsJudgment`, under the unfiltered-full-mode + typing-enabled + resolvable-judge gate; that
    registration is deleted, so `KeywordSignalExtractor` has no news-read dependency in ANY composition.
    `scripts/run-profiles/default.json` still has Typing and Judgment `Enabled: true` — the judge still runs
    and still feeds the leaders marker; it just no longer reaches the signal layer.
  - ✅ **§2 IS SHIPPED — the AD-10 news-read hole is CLOSED.** `NewsJudgmentScoringIdentity`
    (`Radar.Application.Scoring`) renders one canonical **`news=…;` segment**, appended by
    `SignalSourceDescriptor.CanonicalDescriptor()` **AFTER** the existing `rules=` and optional `ai=`
    segments so the pre-194 prefix stays byte-stable and a pin move is unambiguously attributable. It
    distinguishes: judgment **disabled vs enabled**; the exact **resolved presentation cohort key** (hence
    provider + exact judge model + judge prompt/schema + the whole stage-1 extractor cohort identity + the
    fact-family builder identity); the **`news-judgment-signal-v1`** materializer identity; the
    **trajectory→direction mapping and every strength constant** (`BaseStrength` 4, `MaxFindingContribution`
    3, `CompleteTypingBonus` 1, `Novelty` 4, `Confidence` 0.5 — encoded by value, in fixed order, invariant
    culture); and the **`legacy-news-inheritance-v1`** (§1.4) + **`news-judgment-supersede-v1`** (§1.3) rule
    versions. Rules:
    - **It holds STRINGS AND NUMBERS — the spec-147 `EnabledCollectorVocabulary` posture, and it is
      structural.** It cannot call a judge, cannot construct a provider client and references nothing that
      can, so a spec-144 `score` pass and a spec-139 replay compose the SAME identity a `full` run composes
      from the same configuration **without registering the judgment step at all** (asserted through the real
      composed Worker graphs, in the style of spec 147's full-vs-score fingerprint test). This deliberately
      does NOT repeat spec 144's shape, where the whole AI seam had to be registered in score mode because
      `ScoringDescriptor()` could only be obtained from the live source. It is also what keeps the
      spec-177/179 architecture guards intact: `Radar.Application.Scoring` carries **no reference of any
      kind** to `Radar.Application.News` or `Radar.Application.NewsRisk` — **source or type-graph, asserted
      both ways** (`NewsObservationArchitectureGuardTests`: the reflection walk over every Scoring type's
      shape PLUS, since spec 201 §3, a source scan of `src/Radar.Application/Scoring/**` for any `using` or
      fully-qualified reference; the one `const` read that had hidden from the type-graph walk now lives in
      `Radar.Application.SignalExtraction.NewsTrajectorySignalConstants`) — so the mapping and the constants
      are composed on the far side by `NewsJudgmentScoringIdentityFactory` (`Radar.Application.News`) and
      arrive here already rendered. **Do not invert that by taking the trajectory enum or the rules type as a
      parameter.**
    - **The segment is UNCONDITIONAL; disabled renders `news=disabled:…;`.** Rendering nothing when disabled
      would be byte-identical to a pre-194 composition, so "judgment off" and "a Radar that predates the
      judgment read" would share a stamp — the ambiguity spec 147 removed from `collectors=;`. Consequence,
      accepted: the AI-OFF pins move too. A composition that never configured the judgment resolves to
      `Disabled`, which is exactly what it scores as.
    - **Cost controls are deliberately NOT in it** — reader API keys (the value *and* the env-var name),
      call budgets and retry caps: `MaxCompaniesPerRun`, `MaxFamiliesPerJudgment`, `MaxJudgmentAttempts` and
      the typing budgets are each asserted NOT to move the stamp. They change what Radar spends discovering
      a judgment, never what a judgment means; folding them in would re-stamp a whole series for a throttle
      change (spec 141: a fingerprint records identity, not operational posture). Reader NAMES are likewise
      out (the spec-179 rule) while provider and model are in.
    - **`EscapeNested`, not `Escape`** — the segment has internal `:`/`|` structure and the cohort key
      legitimately contains `:`, `|` and `=`, so escaping only the outer delimiters would let a cohort key
      impersonate a field; widening the shared `Escape` instead would move the AI-ON pin for the wrong
      reason (the spec-146 argument verbatim).
    - **ONE cohort-key composition.** `NewsJudgmentPresentationCohort.ComposeCohortKey` was EXTRACTED from
      the run-time `TryResolve` and is called by both it and the new config-time resolution, so the
      CONFIGURED cohort and the PRODUCED cohort cannot drift. `media-collapse-v2` is folded through
      `MediaAttentionCollapse.CanonicalDescriptor()` and is **asserted to appear exactly once** across the
      hashed inputs — never duplicated inside the news segment.
    - **Behaviour change, deliberate, the spec-147 precedent:** when judgment is enabled the judge/typing
      reader lists and the presentation-cohort designation are now read and validated in **every** run mode,
      because that designation IS the recorded identity. A misconfigured cohort therefore fails startup in
      `score`/`replay` too, and a judge reader's API-key environment variable must be set in those modes —
      the same posture spec 144 established for `Radar:Ai`.
  - ⚠ **THE PINS MOVED TWICE IN ONE WEEK, AND THE SCORE SERIES TAKES TWO DISCONTINUITIES.**
    `KeywordSignalExtractor.RuleSetVersion` went **`radar-keyword-rules-v6` → `radar-keyword-rules-v7`
    (spec 191)** and then **`radar-keyword-rules-v7` → `radar-keyword-rules-v8` (spec 194 §1.1)** — both
    rule-STRUCTURE changes under CLAUDE.md checklist item 7, both folded into `ScoringConfigVersion` via
    `SignalSourceDescriptor`. Unlike specs 127/129/130 — opt-in-OFF rule groups whose scoring math was
    byte-identical — **both of these change scores**. **Spec 194 then moved every pin TWICE MORE on its own
    branch**: once for `MediaAttentionCollapse.Version` **`media-collapse-v1` → `media-collapse-v2`** (§1.5,
    a hashed field in its own right — `mediaCollapseDescriptor`) and once for the **news-judgment identity
    segment** (§2). Neither needed a `RuleSetVersion` or `_formula.Version` change.
    **post-194 values ⚠ SUPERSEDED (moved again by specs 196, 197 and 198 — see the spec-198 bullet or `ScoringConfigFingerprintTests` for today's) — (`radar-keyword-rules-v8` + `media-collapse-v2` + `news=…;`),
    independently recomputed and confirmed twice — once through `ScoringConfigFingerprint.Compute` over a
    REAL `SignalSourceDescriptor`, and once by re-deriving the canonical string and its SHA-256 outside
    .NET:**
    30d code-default (the unit pins) AI-OFF **`radar-scoring-fp-5036d7f73af3`** / AI-ON
    **`radar-scoring-fp-5ef6508adc5d`**;
    **60d LIVE baseline** AI-OFF **`radar-scoring-fp-2cbbd056ffe5`** / AI-ON
    **`radar-scoring-fp-b9543f441717`**;
    120d `-Profile long-window` AI-OFF **`radar-scoring-fp-f68e6481b136`** / AI-ON
    **`radar-scoring-fp-901129153cd1`**.
    On the AI-ON side the news segment carries the LIVE **enabled** cohort (`default.json` designates the
    DeepInfra DeepSeek reader as both presentation judge and stage-1 extractor); on the AI-OFF side it
    carries the **disabled** form — AI-OFF has always meant "the code-default composition, nothing optional
    registered". Both AI-OFF live-window values are now **asserted** rather than only recorded in prose.
    **The three composition-guard pins did NOT move with them** (`RadarScoreFormulaV10CompositionGuardTests`
    `412ba7a0b6f5`, `V11` `173e8e705e77`, `RadarBaselineActivityFormulaV1` `a8bda5680a46`, all set by §1.5):
    those files substitute a deliberately FROZEN `StubSourceDescriptor`, so an identity move cannot disturb a
    FORMULA-COMPOSITION pin. That isolation is the feature — do not "fix" it by pointing the stub at the
    real descriptor.
    **HISTORY — superseded values, recorded for reconciling accrued snapshots ONLY:** the spec-194 §1.5
    (v8 + `media-collapse-v2`, no news segment) pairs 30d `a47076995bf5` / `fce77b299c76`, 60d
    `61891b37e429` / `162df0f4c62b`, 120d `f160ee8faaa6` / `b8ce14dea17a`; the spec-194 §1.1 v8 +
    `media-collapse-v1` pairs 30d `023b1af1e3d4` / `ef9104b7b2b9`, 60d `06e4781f86bb` / `7a4cd9d409ed`, 120d
    `5cb9dc71f309` / `759835b624ca`; the spec-191 v7 pairs 30d `be417df3b731` / `4d1cd1a1528c`, 60d
    `58c289cd0113` / `3670cdb74652`, 120d `5d89d6ce1668` / `c9fe86a19073`; and the spec-148/160 v6 values
    30d `0c46e07b94db` / `ebd7d11a58d0`, 60d `4eb2fe5d3cdf` / `5ffa8c9e25f0`, 120d `0a7058d94582` /
    `19fecdb64e3a`. The window-dependence rule stands unchanged — **the three pairs are three correct
    answers at three windows; do not reconcile them onto one value**, and match an accrued stamp against the
    pair for the window that run actually used.
    **THREE SEMANTIC REGIMES, two close discontinuities:** pre-191 Neutral news; spec-191 inherited
    direction (**known DEFECTIVE — NOT a valid control cohort; do not pool it across the boundary or use it
    as evidence for post-194 efficacy**); and post-194 grounded judgment signals. No `_formula.Version`
    bump, no weight edit, no new strategy, no arm renamed, no Lead change. **History is deliberately NOT
    regenerated, rewritten or backfilled (AD-8/AD-1)**: snapshots on either side of each bump mean different
    things, exactly as spec 148 took its discontinuity.
  - ⚠ **OPERATOR ACTION — REQUIRED BEFORE THE FIRST POST-194 BASELINE RUN, AND THE ORDER IS LOAD-BEARING.**
    `StrategyIdentityGuard` compares each strategy's computed fingerprint against
    `data/scoring-configs/strategies/{name}.json` as the FIRST statement of the run, and **will throw**,
    naming the strategy and both fingerprints — the value it now computes is the FINAL post-194 pair
    **`radar-scoring-fp-2cbbd056ffe5` (AI-OFF) / `radar-scoring-fp-b9543f441717` (AI-ON)** at the live
    60-day window, not `61891b37e429` / `162df0f4c62b` (spec 194 §1.5), `06e4781f86bb` / `7a4cd9d409ed`
    (§1.1) or `58c289cd0113` / `3670cdb74652` (spec 191) that a pre-194 record holds. The order:
    **(1)** do NOT touch the ignored identity records while a pre-194 baseline is running; **(2)** after
    merge and BEFORE the first post-194 baseline, consciously **delete or re-record every configured
    `data/scoring-configs/strategies/{name}.json`**; **(3)** verify the first run reports the expected new
    fingerprint before treating subsequent snapshots as the corrected series. That path is **git-ignored**,
    so those records can never be updated by a PR and cannot ride along in this change — **fabricating them
    would be worse than the guard's own message**. If step 2 is missed the run halts before collection:
    **that halt is CORRECT and must not be bypassed.** The move is recorded in
    `ScoringConfigFingerprintTests`' pin comments and in `scripts/run-profiles/default.json`'s
    operator-facing `_comment`.
  - **`replay ⊆ forward` still holds field-for-field.** Replay never re-extracts — it reads the signals the
    forward pass persisted — so the 24 accrued v7 directional `MediaAttention` signals stay on disk exactly
    as they were written, inherited direction and all. Since §1.4 (below) both the forward pass and replay
    admit them through the SAME `ScoringEngine` transform, so both see them Neutral and the invariant is
    untouched. Nothing in the replay path can reach a judgment at all, and after 194 §1.1 nothing in the
    FORWARD path can either.
  - ✅ **§1.4 IS SHIPPED — the accrued v7 directions now fail CLOSED to Neutral on read.**
    `LegacyNewsInheritanceNeutralization` (`Radar.Application.Scoring`, versioned
    **`legacy-news-inheritance-v1`**) is a pure, deterministic (AD-3), read-side admission transform applied
    in `ScoringEngine.ScoreCompanyAsync` **before** the spec-113 supersede and the spec-109 media collapse,
    to the current-window `ScoringSignal` pairs **and** the previous/velocity-window `Signal` list. Rules:
    - **The match is the exact legacy metadata SHAPE, never `Direction != Neutral`.** A directional
      `MediaAttention` signal qualifies iff its `MetadataJson` parses through the shared `EvidenceMetadata`
      reader (never a second parser), carries spec 191's `newsJudgmentId` + `newsJudgmentCohortKey` +
      `newsObservationId`, and does NOT carry `newsJudgmentSignalVersion = news-judgment-signal-v1` (both
      key and value now declared as consts on `NewsDirectionalSignalMetadata`; §1.2 will WRITE them).
      Matching on direction alone would swallow the §1.2 judgment-derived signal this correction exists to
      make room for — mutation-proven, not asserted.
    - **A malformed v1 envelope fails closed TOO, on its own count axis.** Unreadable JSON, or a
      `news-judgment-signal-v1` claim missing judgment id / cohort key / trajectory, is a direction whose
      grounding cannot be verified. Counted separately from the legacy residue, because a CURRENT writer
      producing unverifiable provenance is a different and more urgent fact.
    - **The substitution is the exact pre-191 Neutral media-attention event**: `Direction = Neutral`,
      `Strength = NewsTrajectorySignalRules.BaseStrength` (4 — sourced from there so the two cannot drift).
      Everything else (id, evidence id, novelty, confidence, excerpt, reason, both instants, the metadata
      envelope itself) is carried through, so the signal is still walkable back to the record on disk.
    - **Read-side only, and nothing is written**: the persisted signal, its review and its file stay
      byte-identical (append-only, AD-8). Nothing is deleted, rewritten or backfilled.
    - **Never silent**: each current-window suppression is named on that signal's `ScoreEvidenceLink`
      contribution reason (the spec-109/193 precedent), and one aggregated per-company **Warning** reports
      both axes for both windows (the spec-145 aggregation precedent). A run with nothing to suppress logs
      nothing new and is byte-identical to before — the untouched fast path returns the INPUT INSTANCE.
    - **No pin moved.** `legacy-news-inheritance-v1` is declared public precisely so **§2** can fold it into
      `SignalSourceDescriptor.CanonicalDescriptor()` in its own pass; it is hashed into nothing today, and
      `ScoringConfigFingerprintTests`, `KeywordSignalExtractor.RuleSetVersion` and
      `MediaAttentionCollapse.Version` are untouched.
    - ⚠ **The accrued cohort is STILL NOT a valid control cohort** — its scores before this change used an
      ungrounded direction, and it is now scored as plain attention volume. Do not pool across the boundary.
  - ✅ **§1.2 IS SHIPPED — the JUDGMENT now creates its own grounded signal, and no later article borrows
    it.** `INewsJudgmentSignalMaterializer` / `NewsJudgmentSignalMaterializer` (`Radar.Application.News`),
    invoked by the Worker immediately AFTER `RunNewsJudgmentAsync` and BEFORE `RunNewsRiskShadowAsync`, over
    the current `NewsJudgmentRunResult` and the EXACT `NewsTypingRunResult` instance the judge consumed. It
    makes **no model call** and re-ranks no candidates. Rules:
    - **Eligibility is ALL-OR-NOTHING**: the designated presentation cohort (resolved STRUCTURALLY through
      the new shared `NewsJudgmentPresentationCohort`, which `NewsJudgmentGenerator.BuildPresentationMarkers`
      was routed onto rather than copied — so the cohort whose direction is SCORED and the cohort whose
      marker is DISPLAYED cannot drift), `Status == Judged`, an `Improving`/`Deteriorating` trajectory
      (`Mixed`/`Unknown` are honest non-directions via `NewsTrajectorySignalRules.DirectionFor`; and, since
      spec 214, a `LevelOnly` trajectory basis — every cited fact a stated level or unquantified — mints
      nothing under its own `LevelOnlyTrajectory` skip, with `TrajectoryBasisNotRecorded` for a pre-214
      directional record and `TrajectoryBasisNotAllowlisted` for a defined-but-unallowlisted basis: the
      basis gate is an ALLOWLIST of `Supported` alone), a non-empty
      `TrajectoryFactIds`, and **every** cited fact resolving through the stage-1 cohort's `FactsById` to a
      source observation that resolves through `NewsObservationEvidenceJoin` to exactly one news evidence
      item **for the same company**. A partially resolvable citation set records a NAMED skip and creates no
      signal: scoring the resolvable part would rest a company-level verdict on a subset of the evidence that
      produced it, invisibly.
    - **The join gained a REVERSE lookup, not a second join.** `TryMatchByObservation` maps **every**
      observation on a joined key (not just the lowest-id representative) onto that key's single match, so a
      cited fact whose observation is not the representative resolves. The forward `TryMatch`, the
      representative rule and the three observation counts are asserted byte-unchanged. No fuzzy second join,
      no side index (spec 151's precedent).
    - **ONE signal per judgment** — not one per citation, not one per later article.
      `Id = DeterministicGuid.FromCanonicalString("radar:news-judgment-signal:news-judgment-signal-v1:{judgmentId:D}")`;
      `EvidenceId` = the deterministic primary anchor (latest cited `PublishedAtUtc ?? CollectedAtUtc`, then
      lowest evidence id); `CompanyId`/`CompanyMention` from the JUDGMENT record (no resolver call);
      `Type = MediaAttention` (a new `SignalType` would fall outside every declared `SignalTypes` filter and
      every channel budget); strength `NewsTrajectorySignalRules.StrengthFor(findings, typingComplete)`;
      Novelty **4** / Confidence **0.5** retained from spec 191 and now declared as consts beside the rules.
    - **`CreatedAtUtc` is the MATERIALIZATION instant, never the judgment's** — even for a reused judgment.
      Backdating would let a spec-136 replay see a signal Radar did not have; the one-run score lag is the
      honest cost, and is asserted against `ISignalFileStore.ReadApprovedInWindowAsync`'s
      `CreatedAtUtc <= knownAsOfUtc` predicate. `ObservedAtUtc` is the anchor evidence's own instant.
    - **The excerpt is a CITATION** — the first, in the record's persisted `TrajectoryFactIds` order and each
      fact's own citation order, that passes the mapper's excerpt-in-evidence guard against the ANCHOR
      evidence. That guard was EXTRACTED as `ExtractedSignalMapper.IsExcerptSupportedByEvidence` and
      `ToSignal` routes through it, so the pre-check and the verdict cannot disagree. None passing ⇒ the
      named `excerpt-not-in-evidence` skip and no signal.
    - **One versioned envelope, one parser.** `NewsDirectionalSignalMetadata.ComposeJudgmentSignal` writes
      `newsJudgmentSignalVersion` + `newsJudgmentId` + `newsJudgmentCohortKey` + the trajectory + the ordered
      distinct fact / observation / evidence id lists, through the SHARED `EvidenceMetadata.Compose`. GUID
      lists are lowercase `D`, distinct, ordinally ordered, joined by the declared `GuidListDelimiter` (`,`),
      with `ParseGuidList` as the matching read side. ⚠ **Deliberate deviation from the spec's §1.2 prose:**
      the trajectory rides the EXISTING `newsBusinessTrajectory` key, not a new `newsTrajectory` — the key was
      already declared and is already read by §1.4's validity gate, and a second spelling would be exactly
      the duplicate-parser failure the same section forbids two sentences later.
    - **The creation path is the collection pass's, not a second one**: map → (deterministic id set BEFORE
      review, so `review.SignalId == signal.Id` and the file store's provenance guard holds) → review →
      `ISignalRepository` + `ISignalReviewRepository` + `ISignalFileStore`. An existing deterministic id is
      `AlreadyMaterialized` — **never reviewed again and never overwritten**. A failed durable write follows
      spec 193: COUNTED, not reported as materialized, no retry queue (the next process may safely retry,
      because no durable signal with that id exists).
    - **`NewsJudgmentSignalMaterializationSummary`** (eligible / materialized / already-materialized /
      validation-rejected / write-failed + counts keyed by the CLOSED `NewsJudgmentSignalSkipReason`
      vocabulary) rides `NewsJudgmentRunResult.SignalMaterialization` — **trailing and nullable, `null` =
      NOT ATTEMPTED**, never an all-zero summary — is logged ONCE per pass (spec-145 aggregation) and renders
      as an additive trailing section of the live news-risk artifact (a `null` summary renders a
      byte-identical document; the schema tag does not move). One unexpected company failure is counted on
      its own `UnexpectedFailure` axis and the rest still materialize; cancellation propagates.
    - **No pin moved.** Nothing on the scoring-identity side was touched: no `SignalSourceDescriptor` change,
      no `RuleSetVersion` bump, no `MediaAttentionCollapse.Version` bump, `ScoringConfigFingerprintTests`
      untouched. The materializer is registered WITH judgment, so a judgment-disabled graph is unchanged.
  - ✅ **§1.3 IS SHIPPED — the grounded signal REPLACES the ordinary article event; the cited article is not
    counted twice.** `NewsJudgmentSignalSupersede` (`Radar.Application.Scoring`, versioned
    **`news-judgment-supersede-v1`**) follows `GuidanceChangeSupersede`'s established shape exactly (generic
    core + two overloads, an untouched fast path returning the **INPUT INSTANCE**, survivors + per-winner
    counts in `NewsJudgmentSupersedeResult<T>`), and is applied in `ScoringEngine.ScoreCompanyAsync` to
    **both** the current-window `ScoringSignal` pairs and the previous/velocity-window `Signal` list, before
    formula/filter consumption. Rules:
    - **The rule**: among `MediaAttention` signals sharing one `EvidenceId`, a structurally valid
      `news-judgment-signal-v1` signal beats the ordinary Neutral article signal **and** any accrued
      spec-191 v7 directional article signal; among materialized signals the **latest `CreatedAtUtc`** wins,
      then the **lowest `Signal.Id`** (deliberately the opposite of the guidance supersede's
      earliest-observed tie-break — `ObservedAtUtc` on a materialized signal is the ARTICLE's instant and is
      identical across re-materializations, so only the creation instant distinguishes two grounded reads).
      **Without a valid materialized signal NOTHING is removed** — an evidence id carrying only
      ordinary/legacy media signals is completely untouched, because de-noising ordinary news volume is the
      same-event collapse's job and widening a supersede into a second collapse would drop attention events
      no judgment ever replaced. That is true BY CONSTRUCTION: the winner map is built from the valid v1
      signals alone, so an evidence id with no materialized signal simply has no entry.
    - **ASSEMBLY ORDER, and why.** `LegacyNewsInheritanceNeutralization` (§1.4) → `GuidanceChangeSupersede`
      → `NewsJudgmentSignalSupersede` (§1.3) → `MediaAttentionCollapse` (§1.5). It runs **after** §1.4
      because it reads §1.4's output: a neutralized legacy signal must arrive as a LOSING ordinary signal,
      never as a rival direction. It runs **before** the collapse so the collapse buckets the post-supersede
      media set (the removed duplicate must not first be counted as a collapsed same-event item) and so the
      surviving grounded signal is what v2 then prefers as its bucket representative. Its order relative to
      `GuidanceChangeSupersede` is behaviourally **irrelevant, and that is a checked fact, not an
      assumption**: the two operate on disjoint `SignalType`s (`GuidanceChange` vs `MediaAttention`), so
      neither can see the other's removals. `ScoringInput.PreCollapseSignals` was repointed at the
      post-supersede set, so the spec-122 collapsed-breadth credit cannot re-admit the replaced signal's
      publisher through the back door.
    - **The ONE valid-v1 predicate, three call sites.** The "structurally valid `news-judgment-signal-v1`"
      question was EXTRACTED out of §1.4's private classifier into
      `NewsDirectionalSignalMetadata.ClassifyProvenance` / `IsJudgmentDerived` (total, deterministic, one
      parse through the shared `EvidenceMetadata.TryRead`), and §1.3, §1.4 and §1.5 all route through it.
      A second copy would let a signal be "valid enough to supersede" while "malformed enough to
      neutralize" — a score that both keeps and disowns the same direction. A malformed or unreadable v1
      envelope therefore loses the supersede as well as failing closed to Neutral.
    - **Never silent, and beside the existing accounting.** The winning contribution's `Reason` gains
      `(superseded N ordinary media attention signal(s) for this evidence: the judgment-derived direction
      replaces the attention event)` in the same block that carries the spec-109 collapse note and the
      spec-193 GuidanceChange note, and ONE aggregated per-company **Information** line (the spec-145
      precedent) reports both windows. A run with nothing to supersede logs nothing new and is byte-identical
      to before.
    - **The healthy path replaces one attention event with one grounded attention event — asserted through
      the real engine on a CONSTRUCTED pre/post pin** (no fixture copies live data): removing the
      materialized signal restores the ordinary Neutral result exactly, adding it leaves `AttentionScore`,
      `EvidenceConfidenceScore` and `SignalVelocityScore` unchanged in both windows and moves only the
      trajectory contribution and the linked provenance.
  - ✅ **SPEC 194 IS COMPLETE — §1.1, §1.2, §1.3, §1.4, §1.5, §2 and §3 have all shipped.** The AD-10
    news-read honesty gap that stood open through the whole 177–193 arc — "the filing seam carries
    `ScoringDescriptor()` and the news read has no analogue, so judgment on/off, the judge MODEL, the
    designated `PresentationCohort` and `NewsTrajectorySignalRules`' strength constants are hashed into
    NOTHING" — is **CLOSED** by the `news=…;` segment described above. The three rule versions this arc
    introduced (`legacy-news-inheritance-v1`, `news-judgment-supersede-v1`, `news-judgment-signal-v1`) are
    public precisely so §2 could fold them in, and it does. `media-collapse-v2` stays folded through
    `MediaAttentionCollapse.CanonicalDescriptor()` alone and is asserted NOT to be duplicated inside the news
    segment. **Snapshots across a judgment on/off or judge-model change are now stamped differently, so
    `StrategyIdentityGuard` trips and `ScoreSeriesKey` no longer pools them.**
  - **Out of scope, recorded not built**: backfilling/regenerating/rewriting any historical signal, snapshot
    or efficacy artifact; giving typed facts a per-fact direction (spec 181's reflection-guarded rule stands
    — the COMPANY-level trajectory is the input); persisting the observation↔evidence join as a side index;
    changing the judge prompt, result schema, taxonomy, fact-family identity or any cohort key; making the
    news-trajectory strength constants configurable (§2 hashes them by value; a test perturbs a CONSTRUCTED
    identity fixture instead); folding call budgets or retry caps into the identity; and retiring
    v8/v9/v10/v11 or changing which arm is Lead.
- **Nothing may be discarded without being counted — three sites closed (spec 193).** Accounting only: no
  scoring behaviour, formula, rule set, cohort key or cache key changed and **no pin moved**. Three findings
  from a silent-discard sweep, all of the same family the architecture rule above now forbids:
  - **A failed durable write read as SUCCESS — the only one that could lose real data.**
    `GracefulFileWriter.TryWriteAllTextAsync` caught IO/permission errors, logged one Warning and returned
    `false`, and **every caller used that return only to gate a success log**. `FileSignalStore.WriteAsync`
    then unconditionally ran `_byId[signal.Id] = signal;` and returned the path, so a signal that never
    reached disk sat in the in-process index, reported a path, and was counted as stored —
    `CollectionPass` even carried a comment sanctioning it. The next run's `ReadApprovedInWindowAsync` simply
    would not see it and **nothing recorded that it should have**. Reachable on Windows via an antivirus file
    lock. Now: a typed `DurableWriteResult` shaped on the existing `NewsObservationWriteOutcome` (not a second
    invention), trailing-nullable `SignalsNotPersisted`/`ScoreSnapshotsNotPersisted` on the run record, and
    ONE aggregated Warning per store per run. **The graceful degradation, the in-memory copy and the catch
    set are untouched — only the CLAIM changed.**
  - **`GuidanceChangeSupersede` removed signals from BOTH scoring windows with no count, log or contribution
    reason** — the only signal-removal step in `ScoringEngine` with no trace, sitting between two that
    account (the dropped-evidence aggregate above it, `MediaAttentionCollapse` below it). It matters because
    spec 173 measured **4 of the top 10 companies by Opportunity resting on a results-only
    `GuidanceChange`**. Now returns counts in the `MediaCollapseResult` shape, each charged to the survivor of
    its own `EvidenceId`; **which signals are removed is pinned byte-identical.**
  - **Duplicate-headline collapse discarded syndication volume**, so one story carried by 40 outlets was
    indistinguishable from one carried by 1 — and the code comment claiming publisher diversity survived was
    false. Now counted; neither count is a `BundleHash` input, so the assessment cache key does not move.
  - ⚠ **The fix reproduced the defect once, in its own warning line**: `signalsNotPersisted ?? 0` rendered a
    null ("this pass did no signal writes") as a measured `0`. Caught by Copilot, not by the reviewer. The
    line now renders only the axes a pass actually observed, while a *measured* zero beside a non-zero
    sibling still renders. **The rule applies to log lines and rendered text, not only to persisted records.**
- **The discard accounting made real, and one near-miss worth remembering (spec 195).** The three
  operational findings split out of spec 194 so the correctness fix would not wait on them. No pin moved.
  - **Spec 193's aggregation did not actually exist in the composed system**: `GracefulFileWriter` still
    logged a Warning per failed file *in addition to* the new pass-level aggregate. Fixed with a typed
    **per-instance** failure-log mode — `Immediate` (default, every existing caller unchanged) vs
    `CallerAggregates` (the two pipeline batch paths only).
  - ⚠ **The reviewer caught that a CLASS-WIDE flip would have silently muted spec-139 REPLAY write
    failures.** `ReplayRunner` discards its `DurableWriteResult`, so an unwritable replay directory would
    have looked like a successful run. This is why the mode is per-instance rather than global — a fix for a
    silent-discard defect nearly introduced a worse one.
  - **Spec 193's syndication counts were captured but rendered nowhere.** Now carried as trailing-nullable
    fields on `news-risk-live-v4`, taken from the FRESH bundle rather than a cached assessment (the surviving
    articles and therefore `BundleHash` can be unchanged while breadth changes, so cache reuse would display
    a previous run's breadth as current). The artifact total is labelled a company-publisher **incidence
    sum**, never "globally distinct".
  - **Spec 190's diagnostic tail is deduped company-wide** instead of per-feed-then-summed, so the same tail
    URL in two feeds counts once and feed iteration order cannot change the answer.
- **Attention measured aggregator COVERAGE, not notice — the default is inverted and the volume is
  classified (spec 196).** `OpportunityScore` applies attention as an inverse discount, so a company Radar
  believes is already noticed is marked down; the intent is right, the measurement was not. Measured over the
  live corpus at the pinned instant `2026-08-27T21:42:45.4943606Z` (news observations whose `publishedAtUtc`
  falls in the preceding 60 days, company in the 74-company universe, resolved through the production
  `Normalize`): **2,865 observations, of which 50.1 % were UNCLASSIFIED and therefore weighted `0.25` — two
  and a half times a `Mill` publisher — while `Genuine` notice was 15 observations, 0.5 %.** Output
  consequence: attention mean **73.4** with **53 of 75** score directories between 70 and 89, i.e. a
  near-uniform tax rather than a discriminator, and `OpportunityScore` compressed to mean 19.2 / max 38. The
  worked example: **DGII scored attention 75** with 38 of its 45 in-window observations from algorithmic
  aggregators and Yahoo Finance (10) as its single largest source, unclassified. Rules:
  - **The default is INVERTED: `UnknownWeight` 0.25 → 0.1, the `Mill` weight.** An explicit entry is now
    required to count as **notice**, not to be **discounted** — genuine outlets are a short enumerable list
    while mills and syndication are an unbounded long tail (~300 unclassified publishers and counting), so
    enumerating the tail is unwinnable. It stays deliberately **non-zero** (real coverage is never silently
    zeroed; a zero would make an unclassified genuine outlet invisible rather than merely quiet) and stays
    **configurable**, so the inversion is a declared default rather than a hard-coded belief.
  - **The POLICY was defined before any publisher was assigned, and it lives in code**: `Wire` **0.05** (paid
    or company-originated distribution — visibility the company controls the existence of, not independent
    notice), `Mill` **0.1** (automated/templated/republished with no demonstrated independent *selection* —
    does the outlet decide which companies to cover, or publish on every ticker by construction?), `Platform`
    **0.3** (contributor investor content: a human chose *this* company, the outlet gatekeeps little) and
    `Genuine` **1.0** (independent reporting or editorial selection). **Seeking Alpha and The Motley Fool are
    both `Platform`** — a tenfold split between two comparable investor-content platforms was exactly the
    unprincipled curation this slice removes.
  - **MEMBERSHIP is the committed audit's; the WEIGHTS are the spec's.** `docs/cohorts/attention-publisher-audit-v1.md`
    records each classified publisher's in-corpus volume, company spread, verdict and the sampled items it
    rests on (sampling: the most-recent in-corpus item per company by `PublishedAtUtc` then `ObservationId`,
    up to ten companies). **Audit overrode reputation** where they disagreed: MarketWatch is `Mill` because
    all ten of its in-corpus items were the automated market-wrap template, and The Globe and Mail is `Mill`
    because its in-corpus feed was entirely syndicated. Two findings the audit recorded and deliberately did
    NOT fix in the tier map: `Valley News Live`/`Perham Focus`/`The Mighty 790 KFGO` are real Otter Tail
    County newsrooms matched to OTTR **by company-name collision** (a resolution defect, not a publisher one),
    and issuer names appear as publishers (`Aehr Test Systems`, `Chevron`) — both fall to the inverted default,
    which is the right practical outcome, and both are separate specs. Post-audit the live corpus is Wire 4.4 %
    / Mill 78.1 % / Platform 3.7 % / Genuine 1.0 % / unclassified **12.8 %** (367 observations over 256
    publishers, 194 of them singletons).
  - **Matching: one real fix, one retraction.** `marketscreener.com` was **never** broken — `Normalize`
    already strips the trailing TLD — and a test now GUARDS that rather than "fixing" it. The real gap was
    `Investing.com Nigeria` (`investingcomnigeria`), which shares no key with the listed `Investing.com`
    (`investing`); it and the other regional Investing.com/Yahoo Finance editions are plain **alias entries**
    in the tier lists. `Normalize` is deliberately **not broadened** — a prefix rule could silently collapse
    unrelated outlets.
  - **`IAttentionSourceWeights.Resolve` is the ONE matching implementation; `WeightFor` is a projection of it
    (a default interface member).** This is a structural consequence of the inversion, not tidiness: once
    unknown == `Mill` == 0.1, a `double` cannot distinguish an audited mill from an unclassified publisher, so
    a diagnostic built on `WeightFor` would either duplicate the matching rules (they drift) or report every
    mill as unclassified — lying about the very gap it exists to expose. `AttentionSourceResolution` carries
    `TierName` / `Weight` / `IsExplicitlyMapped` / `NormalizedPublisher`, with the invariant "`IsExplicitlyMapped`
    ⟺ a real tier" enforced by a private ctor plus two factories. **An ambiguous publisher — one normalized key
    in two tiers — now THROWS at construction naming the publisher, the key and BOTH tiers**, instead of
    resolving by ordinal last-wins, which made both the score and the diagnostic depend on tier-NAME ordering.
    A duplicate within one tier is idempotent.
  - **The per-run CAPTURE-FLOW diagnostic, which is NOT the `AttentionScore` input.** `AttentionPublisherCoverageSummary`
    (primitive-only — it carries no `Radar.Application.Scoring` type; ⚠ **the broader claim this bullet
    originally made, "`Radar.Application.News` still takes no dependency on `Radar.Application.Scoring`",
    was FALSE when written and is withdrawn (spec 201 §3):** `NewsJudgmentScoringIdentityFactory`
    (`Radar.Application.News`) and `NewsRiskCandidateSelector` (`Radar.Application.NewsRisk`) DO reference
    Scoring types, deliberately — the
    ban runs the OTHER way, Scoring → News/NewsRisk, and that is the one the guards enforce) rides
    `NewsObservationBatch` as **one trailing nullable member** carrying its OWN token
    `attention-publisher-coverage-v1`; **`NewsObservationBatch.SchemaVersion` is UNCHANGED** because it is
    stamped with `NewsObservationRecord.CurrentSchemaVersion`, the same const every observation record carries,
    and bumping it would churn every record for an unrelated reason. `null` on a pre-196 batch = NOT RECORDED,
    never zero. It counts **every candidate ATTEMPTED** — written, cross-run deduped and failed alike — so its
    tier counts **sum to `ObservationsAttempted`** (asserted), and it is emitted as ONE aggregated Information
    line per run (the spec-145 precedent) with the tier shares and the top-10 unclassified publishers by
    descending volume. The two populations genuinely differ: this is candidate volume in one collection pass,
    whereas scoring consumes tier-weighted **distinct publishers per company** over the scoring window. Nothing
    is auto-classified from it — the tier map stays curated policy (AD-5). `CollectionPass` takes
    `IAttentionSourceWeights` as a **required** ctor dependency (spec 150's precedent — a silently-null optional
    dependency means a wiring mistake renders no diagnostic while every test stays green).
  - ⚠ **ALL SIX PINS MOVED — THE THIRD SCORING-IDENTITY MOVE IN AS MANY WEEKS**, because the tier map is the
    `attnDesc` hashed field (AD-10). **CURRENT values: 30d code-default** AI-OFF **`radar-scoring-fp-54e845330f96`**
    / AI-ON **`radar-scoring-fp-420b31ba0753`**; **60d LIVE baseline** AI-OFF **`radar-scoring-fp-8daa662a57a6`**
    / AI-ON **`radar-scoring-fp-65eb592d0354`**; **120d `-Profile long-window`** AI-OFF
    **`radar-scoring-fp-f610244e23c6`** / AI-ON **`radar-scoring-fp-a89b6d9ad0a5`**. The three pairs are three
    correct answers at three windows — **do not reconcile them onto one value**; match an accrued stamp against
    the pair for the window that run actually used. The spec-194 values (`5036d7f73af3`/`5ef6508adc5d`,
    `2cbbd056ffe5`/`b9543f441717`, `f68e6481b136`/`901129153cd1`) are now history. **Verified twice, and the
    second check is the useful one:** each value was re-derived outside .NET by rebuilding the canonical string
    and hashing it with a different SHA-256 implementation (the spec-194 precedent), AND substituting a
    reconstructed pre-196 tier map into the same code reproduces every pre-196 pin exactly — so the tier map is
    provably the SOLE cause of the move. The three **composition-guard** pins moved with them and only with
    them (`RadarScoreFormulaV10` `412ba7a0b6f5 → 70e32b77f1c4`, `V11` `173e8e705e77 → 32a50355b568`,
    `RadarBaselineActivityFormulaV1` `a8bda5680a46 → 7af921a7ae84`): those files substitute a frozen
    `StubSourceDescriptor` but DO consume the real `AttentionSourceTierOptions.Default`, and the same
    substitution control reproduces their previous values. No `_formula.Version` bump, no `RuleSetVersion` bump
    (still `radar-keyword-rules-v8`), no `MediaAttentionCollapse.Version` bump (still `media-collapse-v2`), no
    weight edit. **`CanonicalDescriptor()`'s SHAPE is unchanged and tier NAMES are deliberately NOT hashed** —
    a rename with identical weights and membership scores identically and must not re-stamp.
  - ⚠ **THE ATTENTION REGIME BEFORE THIS BOUNDARY IS NOT COMPARABLE WITH THE ONE AFTER IT.** Accrued snapshots
    keep their old attention values (history is deliberately NOT regenerated, rewritten or backfilled —
    AD-8/AD-1, the spec-148 precedent); they were computed against a map under which half the observed volume
    outranked a content mill.
  - ⚠ **OPERATOR ACTION — REQUIRED BEFORE THE FIRST POST-196 BASELINE, AND THE ORDER IS LOAD-BEARING.**
    `data/scoring-configs/` is git-ignored, so the identity records cannot ride in a PR and **must never be
    fabricated**: **(1)** do not touch them while a pre-196 baseline is running; **(2)** after merge and before
    the first post-196 baseline, consciously **delete or re-record every configured
    `data/scoring-configs/strategies/{name}.json`**; **(3)** verify the first run reports
    `radar-scoring-fp-65eb592d0354` (the shipped profile is AI-ON at 60 days) before treating later snapshots
    as the corrected series. If step 2 is missed, `StrategyIdentityGuard` halts the run before collection —
    **that halt is CORRECT and must not be bypassed.**
  - **The fix is proven by a READ-ONLY PAIRED COUNTERFACTUAL, not by consecutive runs** (§7), because two
    nightly runs change the policy AND the evidence and would show their sum. `AttentionPolicyCounterfactualTests`
    (env-gated on `RADAR_ATTENTION_COUNTERFACTUAL_DATA_ROOT`, skipped with a NAMED reason otherwise) hydrates
    the durable signal + raw-evidence stores read-only (spec 142), scores the 74-company universe for the
    primary `default` strategy at ONE fixed as-of instant through the REAL `ScoringEngine`, and varies ONLY the
    `IAttentionSourceWeights` instance. **Nothing is persisted** — no snapshot, no run record, no identity
    record; the composition registers no score file store, no run store, no scoring-config store and no
    collector. It reports BOTH populations: raw observation coverage per tier (map maintenance) AND the
    tier-weighted **distinct publisher/company breadth `AttentionReach` actually consumes**, counting survivors
    and collapsed-only publishers alike, plus the `AttentionScore` and `OpportunityScore` distributions.
  - ✅ **THE MEASURED COUNTERFACTUAL RESULT — recorded HERE because a PR body is not a durable record**
    (CLAUDE.md's "no measure ships without its live distribution" rule cuts both ways: the harness is not the
    measurement). Old policy → new policy, ONE paired read-only run at the pinned as-of
    `2026-08-27T21:42:45.4943606Z`, 60-day window, `default` strategy, 74 companies, nothing persisted.
    **(1) Raw observation coverage** over the 2,865 in-window observations: unclassified 1,435 (50.1 %) →
    **367 (12.8 %)**; `Mill` 1,415 (49.4 %) → 2,238 (78.1 %); `Platform` 0 → 105 (3.7 %); `Wire` 0 → 127
    (4.4 %); `Genuine` 15 (0.5 %) → 28 (1.0 %); unclassified publishers 302 → 256, **194 of them singletons**.
    Largest remaining: `Valley News Live` 8, `Aehr Test Systems` 7, `Perham Focus` 7, `The Mighty 790 KFGO` 6
    — exactly the company-name-collision and issuer-as-publisher cases the audit recorded and deliberately did
    NOT tier. **The old-policy arm reproduces the spec's own corpus measurement exactly (2,865 / 1,435 / 1,415
    / 15), which is what validates the harness** rather than merely running it.
    **(2) Breadth actually consumed by `AttentionReach`** — the *scoring* unit, 3,107 company-publisher pairs
    over 833 distinct publishers, survivors AND collapsed-only: per company min 2.350 → 1.500, mean
    **9.165 → 4.949**, max 58.900 → 25.700. Distinct publishers by tier: unclassified **808 → 756**, `Mill`
    19 → 55, `Platform` 0 → 6, `Wire` 0 → 7, `Genuine` 6 → 9. **The point this makes, and it is the
    load-bearing one: the audit classified the VOLUME, not the breadth TAIL** — 756 of 833 distinct publishers
    remain unclassified even though only 12.8 % of *observations* are, because the tail is singletons —
    **which is precisely why the inverted default, and not further enumeration, was the right lever.**
    **(3) `AttentionScore`** (n = 74): min 52 → 44, mean **74.4 → 64.7**, max 95 → 90, spread 43 → **46**,
    populated decades 5 → 6. Decades old → new: 40–49 `0→2`, 50–59 `2→17`, 60–69 `16→40`, 70–79 `43→11`,
    80–89 `10→3`, 90–99 `3→1`. (The old arm's 74.4 and the 73.4 quoted at the top of this bullet are the
    SAME regime read two ways — 73.4 is the accrued snapshots across 75 score directories, 74.4 is the
    74-company universe re-scored at the pinned instant. Neither is a correction of the other.)
    **(4) `OpportunityScore`** (n = 74): min 3 → 3, mean 19.0 → **20.2**, max 38 → **41**, spread 35 → **38**,
    populated decades 4 → 5. Decades old → new: 0–9 `6→6`, 10–19 `35→33`, 20–29 `28→25`, 30–39 `5→8`,
    40–49 `0→2`.
  - ⚠ **THE VERDICT IS PARTIAL AND MUST NOT BE READ AS A CLEAN WIN.** The criterion — attention DISCRIMINATES
    rather than taxes — is only **PARTIALLY** met. What improved is real: the mass moved out of the 70s
    (43 → 11) into the 60s, a sixth decade opened, and the spread widened 43 → **46**. What did not: **40 of
    74 companies (54 %) still sit in ONE decade (60–69)**, so attention remains substantially a broad discount
    rather than a strong discriminator, and the corrected measure did not by itself make it one. Per §5/§7
    that is not a failure of this slice but its **recorded evidence for a separate discount-weight-tuning
    slice** (`OpportunityAttentionDivisor` / `OpportunityAttentionDiscountWeight` /
    `FollowingTierDiscountWeight`) with its own measurement — **no weight was tuned here to manufacture a
    better-looking spread**, and none was touched.
  - **§5 — what this deliberately does NOT do, recorded so the next reader does not assume more was decided
    than was.** It does **not re-tune the discount weights**: `OpportunityAttentionDivisor`,
    `OpportunityAttentionDiscountWeight` and `FollowingTierDiscountWeight` are untouched, because the hypothesis
    is that attention measured the *wrong thing*, not that the discount was too strong — **if attention still
    looks like a near-uniform tax after this, THAT is the evidence for re-tuning, in a separate spec with its
    own measurement.** It does not touch the curated per-company `FollowingTier`. It does not change collection
    (no feed, cap, collector or admission rule; no evidence or observation added, removed or re-mapped). It does
    not rewrite history. No new strategy, arm, formula class, signal type, label or Lead change.
- **The judgment-to-score path is COMPLETE — exact article identity, recoverable citations and a pass-level
  diagnostic (spec 197).** Spec 194 made the news direction HONEST (only the judgment that cited an
  article's facts can create a direction); the first full post-194 baseline
  (`0b48b865-76b8-4485-996c-9b9139b694aa`, 2026-08-27) proved it was honest and, at two mechanical seams,
  so fail-closed that most legitimate calls never reached scoring: the materializer considered 19
  judgments, **9 were eligible and directional and only 2 materialized** (7 failed observation→evidence
  resolution, EOSE among them despite five grounded trajectory facts); all five stage-2 `ValidationFailed`
  responses cited **eight-character prefixes** (`11e52ee0`) instead of the supplied 36-character FactIds;
  and the otherwise-green run emitted ~**462 Warnings** (397 unresolved-evidence + 63 spec-191
  neutralization lines) that buried two genuine RSS transport failures. §1 and §2 were specified together
  deliberately — each changes which judgments produce a scoring input, so separating them would have cost
  two identity moves and two operator resets. Rules:
  - **§1 — the join is a deterministic FAIL-CLOSED LADDER, not a title guess.** `NewsObservationEvidenceJoin`
    stays derived-on-read and pure (no side index, no store mutation, no fuzzy matching — spec 151's
    precedent) and its single normalized-title key becomes three ordered tiers: **`ExactArticleInstant`**
    (non-blank `GoogleLandingUrl == SourceUrl` under `StringComparer.Ordinal` + equal non-blank normalized
    headline + both publication instants present and equal in UTC), **`ExactArticleUrl`** (same URL +
    headline when one side carries no usable instant) and **`UniqueHeadlineFallback`** (the pre-197 rule).
    Each tier requires exactly ONE evidence item and ONE distinct company. **Precedence is load-bearing:
    zero candidates may fall through, AMBIGUITY MAY NOT** — a stronger tier finding two evidence items or
    two companies stops there, because falling through to a weaker key makes the ambiguity disappear rather
    than resolve it. The URL bytes are used as persisted: **no canonicalization, tracking-parameter
    stripping, redirect following, casefolding or timestamp tolerance** (§6 non-goals — each widens identity
    and needs its own measured evidence).
  - **§1.2 — every observation has ONE typed disposition and the partition conserves exactly.**
    `NewsObservationEvidenceDisposition` is total (`NoMatch = 0`, the degraded zero; `Ambiguous`; plus the
    three joined routes) and `NewsObservationEvidenceJoinCounts` DERIVES `Joined` from the three route
    counts rather than storing it beside them, so
    `ExactArticleInstant + ExactArticleUrl + UniqueHeadlineFallback + UnjoinedNoMatch + UnjoinedAmbiguous ==
    Observations` is structurally unbreakable instead of being an invariant a future edit could violate
    silently. `NewsObservationEvidenceResolution` enforces "`Match` non-null ⟺ a joined route" by private
    ctor + factories (the spec-196 `AttentionSourceResolution` precedent). The materializer's generic
    `UnresolvedObservation` skip **splits** into `ObservationNoMatch` (Radar holds no evidence for that
    article — a coverage gap) and `ObservationAmbiguous` (Radar REFUSED the identity — a policy decision),
    with the old token retained read-only so a pre-197 artifact still deserializes (the spec-189 `Failed`
    precedent) and `JoinedEvidenceMissing` kept as its own defence-in-depth axis. Counts ride
    `NewsJudgmentSignalMaterializationSummary.JoinCounts` **trailing + nullable** (`null` = the join was NOT
    ATTEMPTED; a measured zero stays zero) and render in the live artifact, which advances
    **`news-risk-live-v4 → v5`**. Current-run diagnostic provenance only: it enters no bundle hash, cache
    key, cohort, judgment, signal or score. **The all-or-nothing citation rule is UNCHANGED** — one
    `NoMatch` or `Ambiguous` trajectory observation still creates no signal. This slice improves identity;
    it does not weaken provenance.
  - **§1.3 — `news-judgment-signal-v1 → v2`, with prior-version occupancy COUNTED rather than hidden.**
    Because the ladder changes which judgments produce scoring inputs, it is not a silent fix under v1: the
    materializer/metadata token advances, v2 ids derive from the v2 token + `JudgmentId` (one signal per
    judgment, existing idempotency preserved), and v2 folds into `NewsJudgmentScoringIdentity`. **Accrued v1
    signals remain valid, immutable grounded judgment signals** — the ONE shared
    `NewsDirectionalSignalMetadata` classifier accepts well-formed v1 AND v2, while a present but
    blank/unsupported `newsJudgmentSignalVersion` is a malformed envelope and fails closed (never falling
    through as an unrelated metadata bag); supersede, media collapse and legacy neutralization all keep
    routing through that one classifier, never three copied version checks. Before reviewing or writing v2
    the materializer checks BOTH ids: an existing v2 is the ordinary `AlreadyMaterialized` path; an existing
    structurally valid v1 is **`PriorVersionOccupied`** — its own summary axis, because it measures a
    one-time migration that must DRAIN rather than persist — and a missing or malformed record at the v1 id
    is **not** occupancy and never suppresses an honest v2 retry. No v1 file is overwritten or deleted, and
    the one-run knowledge-time rule stands: v2 is never backdated to the judgment instant.
  - ✅ **THE LIVE AUDIT — measured read-only over the current stores, no mutation and no model call**
    ("no measure ships without its live distribution"). **The premise FIRST, because it was §1.1's
    highest-risk assumption:** all **3,194 of 3,194** observations (100 %) have an exact ordinal
    `GoogleLandingUrl == SourceUrl` twin among the **14,574** news evidence records — equal to the
    title-twin rate and MORE discriminating (**2,138** unique URL twins vs **2,095** unique title twins), so
    tiers 1–2 are universally eligible rather than dead-fire branches. **Ladder over the live store:**
    ExactArticleInstant **3,185** / ExactArticleUrl **0** / UniqueHeadlineFallback **0** / NoMatch **0** /
    Ambiguous **9**, against the pre-197 title-only Joined **2,095** / NoMatch **0** / Ambiguous **1,099**.
    **Judgment replay of baseline `0b48b865-76b8-4485-996c-9b9139b694aa`:** 9 eligible directional, **2
    materializable before, 9 after, 0 remaining unresolved**. **EOSE explicitly:** its cited observation
    `6f2625a2-4f1a-a2d3-5880-c08b33d75cc0` (published `2026-06-30T07:00:00Z`) has 2 evidence in its title
    bucket AND 2 in its (url, title) bucket but exactly **1** in its (url, title, instant) bucket, resolving
    to evidence `9c885176-ccd5-4597-b438-f4a4b61258ef` — the concrete shape the whole ladder was written
    for. **Stated honestly: `ExactArticleUrl` and `UniqueHeadlineFallback` measured ZERO on today's
    corpus.** They are not dead code and their zero is a MEASURED zero, not an assumed one: they are the
    fail-closed fallbacks for records lacking a usable instant, and today's corpus has none.
  - **§2 — prompt/schema v3 and ONE shared fail-closed citation resolver.** The fixed system instruction and
    the per-request family preamble now say plainly: copy the **complete 36-character hyphenated FactId**,
    never abbreviate/truncate/paraphrase/invent, for `TrajectoryFactIds` **and** every finding's `FactIds`.
    Wording alone is not a recovery mechanism, so `NewsJudgmentCitationResolver` — used by BOTH trajectory
    and finding validation, one implementation and never two — accepts a parseable GUID only when it is in
    the SUPPLIED representative-fact set, and otherwise recovers a token only when it is **8–31 ASCII hex
    characters with no hyphens** and an ordinal-ignore-case **prefix of the canonical 32-character `N`
    rendering of exactly ONE supplied fact**. Zero matches, two-or-more matches, a prefix under eight
    characters, a suffix/substring or any other malformed token fails with a **distinct named reason**
    (`Malformed = 0` as the degraded zero, `NotSupplied`, `PrefixTooShort`, `PrefixUnmatched`,
    `PrefixAmbiguous`), and **distinctness is checked AFTER expansion**, so a full GUID and its own prefix
    in one list are a duplicate rather than two citations. **This is not fuzzy inference:** the scoped
    supplied set has one deterministic referent or the response fails — no prefix matched against the global
    fact store, no first-collision pick, and no relaxation of the supplied-set, assertion-strength,
    context-only, finding-category, attribution or rationale gates.
  - **§2.2 — the contract FORKS, and the recovery pressure becomes MEASURABLE instead of silently repairing
    the provider forever.** `news-judgment-prompt-v2 → v3` and `news-judgment-schema-v2 → v3`: the JSON
    property shape is unchanged, but FactId's accepted GRAMMAR is part of the result schema, and both
    versions enter the stage-2 cohort key, so the new contract earns a fresh retry budget and no v2 attempt
    is reused. `FactIdPrefixExpansionCount` is **trailing + nullable** on `NewsJudgmentRecord`
    (**`news-judgment-v3 → v4`**) with three distinct meanings: **`null`** = no validated response was
    examined under this contract, or a pre-197 record; **`0`** = a response WAS examined and every accepted
    citation was already complete; **positive** = raw citation occurrences deterministically expanded across
    trajectory plus findings, *including* expansions observed before a different validation error failed the
    response. It is carried through `NewsJudgmentValidationResult`, persisted on both `Judged` and
    `ValidationFailed` call-producing records, aggregated once per cohort at **Information** —
    **pass-truthfully (spec 188 §1): a cache/same-run reuse retains its original durable count and is never
    reported as a new current-pass normalization** — and rendered on `NewsRiskLiveJudgment` in the v5
    artifact with measured zero, positive and not-recorded distinguishable. **Measured basis:** all **44**
    distinct 8+-hex tokens across the five live `ValidationFailed` rationales (IOSP `f160ab52`, LBRT
    `2f4bd2fd`, CAT `97c73714`, CASS `11e52ee0`, WDFC `252d42b5`) expand to exactly ONE supplied fact
    against 24–35-fact supplied sets: **0 unmatched, 0 ambiguous**.
  - ⚠ **THE ONE-TIME RE-JUDGE, stated as specs 186 and 194 stated theirs.** Forking the stage-2 cohort key
    means **every candidate company is re-judged ONCE** on the first post-197 run — roughly **19 hosted
    judge calls** at the current candidate count — and the five accrued `ValidationFailed` attempts are
    **not reused**. That is the intended effect: they earn fresh attempts under a contract that can accept
    their citations. It drains within the configured `MaxCompaniesPerRun`; **no provider, budget or
    retry-count change was requested** (§6).
  - **§3 — the two repeated engine Warnings move to the boundary that can see the whole operation, and
    NOTHING is silenced.** `ScoringEngine` is ONE strategy, so its "one Warning per company" (spec 145's
    unresolved evidence, spec 194 §1.4's neutralization) was really one per strategy × company — the ~462
    lines above. A transient `ScoreAssemblyDiagnostics` now rides `CompanyScoreResult` carrying the
    unresolved-evidence signal count + that evaluation's distinct-evidence count and the four neutralization
    axes (current/previous window × accrued-legacy/malformed-envelope, **never pooled**, because a CURRENT
    writer producing unverifiable provenance must not disappear inside the expected spec-191 residue). It is
    never persisted, never a wire contract, never a cache/cohort key, never an identity input and **hashed
    into nothing**. The engine emits **no Warning** for these two categories (a bounded Debug line only) and
    its filtering, neutralization, supersede, collapse, contribution-reason, evidence-link, snapshot and
    persistence behaviour is untouched. Both production callers aggregate through the ONE shared
    `ScoreAssemblyDiagnosticsAggregator` — `ScoringPass` emits at most one Warning per category across the
    combined/standalone pass, `ReplayRunner` the same bounded pair across the complete replay invocation, so
    moving the line out of the shared engine cannot make replay silent. **The population is labelled
    HONESTLY:** a count summed across engines is signal-evaluation **INCIDENCES**, not globally distinct
    signals, so every line carries affected strategy-company evaluations, distinct companies, distinct
    strategies (and distinct as-of instants for replay), and the per-evaluation distinct-evidence counts
    render as an explicit sum, never as a global distinct total. An unaffected operation logs nothing at
    all. **§3 alone moves no score and no pin.** Other engine Warnings/Information lines are out of scope —
    no real provider, RSS, file-write or snapshot-write failure is suppressed, and spec 195's file-writer
    logging modes are undisturbed.
  - ⚠ **THE PINS MOVED ON THE AI-ON SIDE ONLY, AND THE THREE AI-OFF VALUES ARE PROVEN UNCHANGED — that
    non-move is an ASSERTED DELIVERABLE, not an omission.** §4 predicted the split in advance: the AI-ON
    `news=enabled:…` segment carries the resolved presentation cohort (hence prompt/schema v3) and the
    materializer identity (hence `news-judgment-signal-v2`), while the AI-OFF segment
    `news=disabled:legacy-news-inheritance-v1:news-judgment-supersede-v1;` carries **neither**, so neither
    cause can reach it — a disabled pin moving here would indicate **scope leakage**, not a deliverable.
    **CURRENT values: 30d code-default (the unit pins)** AI-OFF **`radar-scoring-fp-54e845330f96`
    (UNCHANGED)** / AI-ON `radar-scoring-fp-420b31ba0753` → **`radar-scoring-fp-e7317fd038ac`**; **60d LIVE
    baseline** AI-OFF **`radar-scoring-fp-8daa662a57a6` (UNCHANGED)** / AI-ON
    `radar-scoring-fp-65eb592d0354` → **`radar-scoring-fp-81a397434756`**; **120d `-Profile long-window`**
    AI-OFF **`radar-scoring-fp-f610244e23c6` (UNCHANGED)** / AI-ON `radar-scoring-fp-a89b6d9ad0a5` →
    **`radar-scoring-fp-e9d9819a2b41`**. **Verified TWICE, and the second check is the useful one:** each
    value was computed through `ScoringConfigFingerprint.Compute` over the real descriptors AND re-derived
    outside .NET by rebuilding the canonical string and hashing it with a different SHA-256 implementation
    (the spec-194/196 precedent). The standing rule holds: **the three window pairs are three correct
    answers at three windows — do NOT reconcile them onto one value**; match an accrued stamp against the
    pair for the window that run actually used. No `_formula.Version` bump, no `RuleSetVersion` bump (still
    `radar-keyword-rules-v8`), no `MediaAttentionCollapse.Version` bump (still `media-collapse-v2`), no
    supersede/neutralization rule-version bump, no attention-tier edit, no weight edit. The three
    **composition-guard** pins did NOT move (`RadarScoreFormulaV10` `70e32b77f1c4`, `V11` `32a50355b568`,
    `RadarBaselineActivityFormulaV1` `7af921a7ae84`): those files substitute a frozen
    `StubSourceDescriptor`, which is exactly why an identity move cannot disturb a formula-COMPOSITION pin.
  - ⚠ **OPERATOR ACTION — REQUIRED BEFORE THE FIRST POST-197 BASELINE, AND THE ORDER IS LOAD-BEARING. This
    is the THIRD close identity boundary in a row (194, 196, 197).** `data/scoring-configs/` is git-ignored,
    so the identity records cannot ride in a PR and **must NEVER be fabricated**: **(1)** do not touch them
    while a pre-197 baseline is running; **(2)** after merge and before the first post-197 baseline,
    consciously **delete or re-record every configured `data/scoring-configs/strategies/{name}.json`**;
    **(3)** verify the first run reports **`radar-scoring-fp-81a397434756`** ⚠ **SUPERSEDED BY SPEC 198 — the only correct verify-target today is `radar-scoring-fp-11240da5aeb0` (60d AI-ON); AI-OFF is `radar-scoring-fp-0ff442a14c1b`.** (the shipped profile is AI-ON
    at 60 days) before treating later snapshots as the corrected series. If step 2 is missed,
    `StrategyIdentityGuard` runs as the FIRST statement of the run and halts before collection — **that halt
    is CORRECT and must not be bypassed.**
  - ⚠ **THE DISCONTINUITY, stated precisely.** Post-194/v1 scores fail closed **correctly** but
    **materially UNDER-ADMIT grounded judgments**, because the title-only join rejected stronger exact
    identity (2 of 9 eligible directional judgments admitted on the measured baseline). Post-197/v2 scores
    admit only citations resolved by the stronger deterministic ladder. History is preserved and **never
    regenerated, rewritten or backfilled** (AD-8/AD-1) — and **the pre-197 sparse-join segment must NOT be
    presented as equivalent judgment coverage** when interpreting news-direction efficacy.
  - **Out of scope, recorded not built (§6)**: fuzzy headline similarity, URL canonicalization, redirect
    resolution and timestamp tolerance; partial citation materialization and any "best available evidence"
    guess; relaxing the business-trajectory, assertion-status, context-only, finding or advice-language
    gates; any additional provider, judge, call budget or retry count (the v3 cohort receives the existing
    configured budget); same-run score backdating (a newly materialized signal stays score-visible only from
    a later run); deleting or rewriting v1 signals, v2 judgments or pre-197 snapshots; and any formula,
    weight, attention-tier, strategy-arm, Lead, marker-vocabulary or collection change — spec 195's
    file-write diagnostics and spec 196's attention calibration are untouched.
- **The news feed is filtered by RECENCY, and the query is finally a hashed input (spec 198).** Radar queried
  Google News per company with **no time filter**, retained the first 25 items in document order and only
  *then* deduped. Measured against the live endpoint on 2026-08-28 for one company phrase: the unfiltered
  response held **100 items, median age 71 days**, 63 of them over a month old and only 3 within a day; the
  2026-08-28 baseline retained 1,604 items → **234 new, 1,370 cross-run deduped**, i.e. ~85 % of the
  collection budget spent re-reading known articles, leaving ~3–4 slots per company per night for anything
  new. It is a **capacity** defect, not a "scoring on stale news" one — what Radar captured *was* fresh (159
  of the 234 were ≤1 day old). **This also retires spec 190's open question**: the "4,312 additional unique
  company-relevant tail items observed but not admitted" reads like missed coverage but is overwhelmingly
  OLD, so selectively admitting it by publisher tier — an earlier draft's plan — was abandoned as carefully
  importing more stale articles while leaving the waste untouched. Rules:
  - **§1 — `Radar:News:RecencyWindowDays` (default 7), applied as a `when:{n}d` term on the search PHRASE.**
    Google News RSS has no recency PARAMETER; the operator is part of the search expression, so it is
    appended to the trimmed phrase **before** `Uri.EscapeDataString` and encodes as `%20when%3A7d`.
    **Verified against the live endpoint on 2026-08-29** for `Caterpillar Inc` (the spec asked for this
    because the operator is undocumented): unfiltered → **100 items, oldest 24 Jun 2026**; `when:7d` → **66
    items, oldest 23 Aug 2026**. So it demonstrably BOUNDS the response and does not silently degrade to
    unfiltered. **`0` reproduces the pre-198 URL BYTE-FOR-BYTE** — pinned against the literal, and that
    equality is the compatibility proof — while a negative value fails startup naming the key (zero is a
    legitimate *disabled*; negative is nonsense that would read as disabled). **Failure posture is "no
    improvement", never "no results"**: a provider that ignored the term returns more/older items and the
    retained prefix, relevance filter, URL dedupe, per-feed cap, diagnostic tail, evidence mapping and
    observation capture all behave exactly as today (asserted by feeding an identical body to both arms).
    **Seven, not one or two**: the baseline runs daily, so a 1–2 day window has no margin — the 2026-08-26
    run fired 23 minutes late and a missed night would open a *permanent* gap, because a skipped article
    never reappears in a narrower window. The window is **fixed and declared**, never adaptive, never derived
    from the last successful run and never per company (AD-3).
  - **§2 — a company's FIRST collection stays UNFILTERED, decided from PERSISTED STATE and never from a
    clock.** The unfiltered query is the only way a newly seeded company acquires back history, so
    `INewsObservationCompanyHistory` (new, in `Radar.Application.News`) answers "which companies already hold
    at least one archived observation?" and `FileNewsObservationArchive` **additionally implements it off the
    SAME lazily-hydrated `_byId` index** — spec 142's "the repository IS the file store" precedent, and spec
    151's recorded rejection of a materialized side index that can drift. Resolved **ONCE per collection
    pass**, not per feed. Records carrying no company contribute nothing. The seam is an **optional** ctor
    dependency of `NewsAttentionCollector`, so a composition that never registers it narrows nothing and is
    byte-identical to pre-198 — **fail closed to "no improvement"**. Proven the way the spec demanded:
    advancing the collector's `TimeProvider` by a year leaves the issued queries identical.
  - **The mode used is RECORDED, never inferred.** `CollectorCompanyCoverage` gains two **trailing nullable**
    fields — `RecencyWindowDays` (the CONFIGURED window) and `UnfilteredFirstCollectionFeedCount` (how many of
    that company's feeds took the §2 exemption) — following the spec-190 convention verbatim: **`null` means
    NOT RECORDED, never `0`/`false`**, and the news collector records them on EVERY row it writes, including
    MissingFeed and failed rows where the count is honestly zero. The count is deliberately **0 when the
    window is disabled** (every query was unfiltered for an unrelated reason, and the recorded `0` window
    already says so) — counting those would report an exemption that never applied. Both survive the run
    record's `c with { Issues = … }` health amend and the durable round-trip (asserted, not assumed), and a
    legacy row hydrates both as `null`. One **aggregated Information line per RUN** (never per company or
    feed) reports the configured window, the windowed/unfiltered feed split and how many companies were on
    their first collection.
  - **§3 — `NewsQueryScoringIdentity` (`Radar.Application.Scoring`) makes the query a hashed
    `ScoringConfigVersion` input.** The feed query decides WHICH evidence exists, so it changes
    `AttentionReach`, `OpportunityScore` and every rank — and it was hashed into NOTHING, the same
    comparability hole spec 194 §2 closed for the judgment read. It **holds an int** (the spec-147
    `EnabledCollectorVocabulary` / spec-194 `NewsJudgmentScoringIdentity` posture, asserted on the TYPE
    GRAPH): it references no News/NewsRisk/Infrastructure type, cannot issue a request, and therefore a
    spec-144 `score` pass and a spec-139 replay compose the SAME identity a `full` run composes **without
    registering the newssearch collector** (asserted through the real composed Worker graphs).
    `DefaultRecencyWindowDays = 7` is **THE one definition** — `NewsCollectorOptions.RecencyWindowDays` and
    `NewsWorkerOptions.RecencyWindowDays` both default off it, so the value a run SENDS and the value the
    fingerprint HASHES cannot drift (pinned by test). The `newsquery={n}d;` segment is appended **LAST**, after
    the spec-194 `news=` segment, so the whole post-197 prefix stays byte-stable.
  - ⚠ **THE SEGMENT IS CONDITIONAL, UNLIKE SPEC 194's — and that is the additivity proof.** A window of `0`
    renders the **EMPTY** string, so a disabled configuration reproduces the same descriptor without the
    newsquery segment byte-for-byte; `Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins` (named
    `…ReproducesPost197Pins` until spec 214) asserts the six no-newsquery halves of the CURRENT pins are
    reproduced EXACTLY at all three windows — AI-off unchanged since 198, AI-on moved by 214 and again by 215 and 216 (⚠ this sentence read "moved by 214" until spec 216; the halves are re-pinned by each of those slices, and the test — never this line — is the authority for the values). Spec 194 chose
    the opposite because "judgment off"
    and "a Radar that predates the judgment read" are different facts; here the input is a plain magnitude with
    a code default, and rendering `newsquery=0d;` would have re-stamped every composition for a filter that does
    nothing. The **null fallback in `SignalSourceDescriptor` is the DEFAULT, not `None`** — unlike the opt-in
    judgment step the window applies whenever the newssearch collector runs, so falling back to "no filter"
    would stamp a claim the run cannot support; a composition that genuinely wants none configures `0`.
    Cost/posture knobs are deliberately NOT folded in: the retention limit, the 100-item parse ceiling, the
    pacing delay and the locale flag (spec 141 — a fingerprint records identity, not throttle).
  - ⚠ **ALL SIX PINS MOVED — THE SIXTH SCORING-IDENTITY BOUNDARY IN THREE WEEKS** (191, 194 §1.5, 194 §2, 196,
    197, 198). **CURRENT values: 30d code-default (the unit pins)** AI-OFF **`radar-scoring-fp-56c8e882beed`** /
    AI-ON **`radar-scoring-fp-7d2b0cf537c4`**; **60d LIVE baseline** AI-OFF **`radar-scoring-fp-0ff442a14c1b`** /
    AI-ON **`radar-scoring-fp-11240da5aeb0`**; **120d `-Profile long-window`** AI-OFF
    **`radar-scoring-fp-adf455313d35`** / AI-ON **`radar-scoring-fp-7eece22968a4`**. **Unlike spec 197 this
    moves BOTH sides, and that is the deliverable**: the segment is not judgment-gated, so an unchanged AI-OFF
    pin would mean the window is not actually hashed. Verified TWICE — through
    `ScoringConfigFingerprint.Compute` over the real descriptors AND re-derived outside .NET by rebuilding the
    canonical string, writing it with no trailing newline and hashing with `sha256sum` (the spec-194/196/197
    practice). The standing rule holds: **the three window pairs are three correct answers at three windows — do
    NOT reconcile them onto one value.** The spec-197 values (`54e845330f96`/`e7317fd038ac`,
    `8daa662a57a6`/`81a397434756`, `f610244e23c6`/`e9d9819a2b41`) are now history. No `_formula.Version` bump,
    no `RuleSetVersion` bump (still `radar-keyword-rules-v8`), no `MediaAttentionCollapse.Version` bump (still
    `media-collapse-v2`), no supersede/neutralization rule bump, no attention-tier edit, no weight edit. The
    three **composition-guard** pins did NOT move (`RadarScoreFormulaV10` `70e32b77f1c4`, `V11` `32a50355b568`,
    `RadarBaselineActivityFormulaV1` `7af921a7ae84`): those files substitute a frozen `StubSourceDescriptor`,
    which is exactly why an identity move cannot disturb a formula-COMPOSITION pin.
  - ⚠ **OPERATOR ACTION — REQUIRED BEFORE THE FIRST POST-198 BASELINE, AND THE ORDER IS LOAD-BEARING.**
    `data/scoring-configs/` is git-ignored, so the identity records cannot ride in a PR and **must NEVER be
    fabricated**: **(1)** do not touch them while a pre-198 baseline is running; **(2)** after merge and before
    the first post-198 baseline, consciously **delete or re-record every configured
    `data/scoring-configs/strategies/{name}.json`**; **(3)** verify the first run reports
    **`radar-scoring-fp-11240da5aeb0`** (the shipped profile is AI-ON at 60 days) before treating later
    snapshots as the corrected series. If step 2 is missed, `StrategyIdentityGuard` halts before collection —
    **that halt is CORRECT and must not be bypassed.**
  - ⚠ **THE COLLECTION REGIME BEFORE THIS BOUNDARY IS NOT COMPARABLE WITH THE ONE AFTER IT.** Pre-198 runs read
    an unfiltered feed whose median item was ten weeks old; post-198 runs read a 7-day window. History is
    deliberately **NOT** regenerated, rewritten or backfilled (AD-8/AD-1, the spec-148 precedent), and nothing
    is re-collected — the change is forward-only.
  - **§4 — the live distribution, measured by two read-only env-gated harnesses (CLAUDE.md's "no measure ships
    without its live distribution").** `NewsRecencyWindowLiveMeasurementTests`
    (`RADAR_NEWS_RECENCY_LIVE_DATA_ROOT`) issues BOTH arms for every configured `newssearch` feed through the
    PRODUCTION `HttpNewsSearchReader` (hence the attribute-only `InternalsVisibleTo` addition — measuring a
    reimplementation would measure the wrong thing), paced by the collector's own `InterRequestDelay`, and
    reports item counts, **age distributions**, projected retained-slot usage and the projected
    new-vs-deduped split against the 234 / 1,370 baseline, plus the companies the windowed arm returns ZERO
    items for (**expected, not a fault** — nothing was published about them this week).
    `NewsRecencyWindowCounterfactualTests` (`RADAR_NEWS_RECENCY_COUNTERFACTUAL_DATA_ROOT`) is the paired
    read-only projection at ONE pinned as-of instant over the 74-company universe and the primary `default`
    strategy at the 60-day scoring window, through the REAL `ScoringEngine`, with the arms differing ONLY in an
    evidence-ADMISSION decorator over `ISignalRepository`/`ISignalFileStore` (no scoring arithmetic
    duplicated). Neither admits, maps or persists anything; both skip with a NAMED reason when unset; the
    shared `BuildReadOnlyProvider` and the frozen `ReadOnlyHarnessSourceDescriptor` are REUSED from the spec-196
    §7 harness rather than copied. ⚠ **The counterfactual is a PROJECTION over accrued evidence, not a replay
    of a different collection history**: cross-run dedupe means most excluded items contributed no new evidence
    on the night they were re-read (so the loss is OVERSTATED) and the freed budget is not modelled (so the gain
    is UNDERSTATED) — a bound on the downside, not a forecast. Both artifacts render those caveats.
  - **MEASURED, 2026-08-29, live endpoint over the full 74-company feed set. THE COVERAGE CRITERION IS MET.**
    Full response, unfiltered → windowed: median items **100 → 19**; median age **32.2 d → 2.9 d**; items over
    30 days old **3,855 → 0**; oldest item **7,465 days → 7.0 days**. Projected over the retained 25 slots:
    recent (≤7 d) relevant items admitted **683 → 789**, i.e. coverage **RISES ~15.5 %** and does not fall —
    which is the ship/no-ship criterion; projected NEW **152 → 210**; projected cross-run DEDUPED
    **1,453 → 579**. So the change admits ~38 % more new material while cutting redundant re-reads ~60 %.
  - ⚠ **Read the two "new" numbers carefully — they are calibrated differently and do NOT disagree.** The
    unfiltered projected-new of **152** sits below the 2026-08-28 baseline's ACTUAL **234** because the
    observation archive has since accrued another night of URLs, so a re-projection over today's archive
    dedupes more than that night did. The projection is therefore calibrated on the DEDUPE side and
    **conservative on the new side**; treat 789/210 as a lower bound on the gain, not a forecast.
  - **PAIRED COUNTERFACTUAL — RAN 2026-08-29 over the live store (23.6 min, read-only, nothing
    persisted). The predicted direction HELD.** Unfiltered → windowed, 74 companies, `default` strategy,
    one as-of instant, only the recency window varying: tier-weighted publisher **breadth mean 4.949 →
    4.649**; **`AttentionScore` mean 64.662 → 63.284** (min 44 → 42, max 90 both); **`OpportunityScore`
    mean 20.23 → 20.50** (min 3, max 41 both). Attention falls and opportunity rises, exactly as
    predicted — the claim was falsifiable and was not quietly reinterpreted.
  - **The effect is SMALL and that is the informative part: ~1.4 points of attention.** All news evidence
    12,180 vs 14,824 — the window removes **2,644** items — while **genuinely recent evidence is
    IDENTICAL at 11,383 in both arms**. So the removed material was overwhelmingly stale duplicates from
    publishers ALREADY counted, and since breadth counts DISTINCT PUBLISHERS rather than articles,
    removing them barely moves the number.
  - ⚠ **This run CANNOT validate the coverage criterion, and the harness says so itself.** Coverage being
    equal here is true BY CONSTRUCTION: the exclusion predicate only ever removes items older than the
    window, so the windowed arm can never admit fewer recent items. The meaningful coverage check is the
    §4 LIVE measurement, where the two arms query the provider independently and coverage ROSE 683 → 789.
    the ship.
  - **§5 — explicit non-goals, enforced**: no change to `Radar:News:MaxRecordsPerCompany` (stays 25), the
    absolute 100-item parse ceiling, the request count, the pacing or the number of requests per company; no
    selective tail admission by publisher tier and no tail item admitted at all; no change to the relevance
    rule, URL dedupe, evidence identity (spec 145) or the spec-197 join; no typing-budget change; no attention
    weight re-tuning (spec 196 left that open on its own evidence); no backfill and no re-collection; no new
    collector, feed, provider or query beyond the appended term; `Radar:Gdelt:*` untouched (a DIFFERENT
    collector's similarly named knob).
- **The universe is 94, the selection variable was COVERAGE not market cap, and nothing in the scoring
  identity moved (spec 199).** `data/companies.json` goes **74 → 94**,
  **additions only**: every existing company entry is byte-identical (asserted — the diff is a pure
  insertion, 710 added lines, 0 deleted), no company was removed, renamed or re-tiered, and no evidence,
  observation, signal, snapshot or efficacy artifact is rewritten. The universe had sat at 74 since spec 166
  while the Lead arm's evidence status read "no evidence of discrimination yet" and a media-count baseline
  out-performed every research arm out of sample — **a universe too small or too tame to contain a genuine
  early-stage improver cannot demonstrate the method works, however carefully it is measured**, so waiting
  for validation before expanding was circular. Rules:
  - **Selection was on being UNDER-COVERED; small cap is the PRIOR, not the test.** `followingTier` is
    curated from following/coverage evidence and is never derived from price or market cap (AD-14), and the
    live tier↔attention overlap is why cap would have optimised the wrong variable: `small` n=35 mean
    **62.0** (46–79), `mid` n=32 mean **66.0** (56–90), `large` n=2 mean 74.5 (67–82), `mega` n=5 mean
    **71.6** (58–90) — **four points of mean separation and near-total overlap**, a `small` reaching 79
    while a `mega` sits at 58. The spec permitted up to a quarter of the batch as `mid` with an argued
    obscurity case; **none was needed — all 20 are `followingTier: small`**, taking the universe from 35/74
    small to **55/94**, so the majority of it is finally the under-covered thing Radar exists to look at.
  - **Sector spread was a hard constraint, because a sector-correlated batch would confound the efficacy
    read with a sector bet.** Two additions each across nine sectors, plus one Industrials and one
    Technology — all eleven sectors gained at least one. Final:
    Industrials 11, Technology 10, Healthcare 10, Consumer Cyclical 10, Financial Services 9, Communication
    Services 9, Consumer Defensive 8, Basic Materials 7, Energy 7, Real Estate 7, Utilities 6 = 94. Themes
    are spread rather than drawn from one story.
  - **Every CIK was live-verified 2026-08-29** against `https://data.sec.gov/submissions/CIK{cik}.json`
    (HTTP 200; entity name, ticker and exchange matched; filings within the last month; Form 4 and SC 13
    present), and is **pinned by test** with all three EDGAR feeds (`sec`/`secform4`/`sec13dg`) asserted to
    resolve to that one submissions document — the guard that a later edit cannot silently re-point a
    company's filings at a different registrant, whose evidence would then be scored under the wrong name
    and is never backfilled. **Exactly ONE addition carries an `rss` press-release feed** (GHM,
    `ir.grahamcorp.com/…/rss`, verified 200 + valid RSS 2.0, 10 items); every other IR feed candidate failed
    verification and was **omitted rather than guessed** — spec 199 §2's rule that a broken feed is worse
    than an absent one, the PSTL/BKE precedent.
  - **SIX tickers carry NO `ticker=` token (four at spec 199, ESQ added by spec 200, OUST added by spec 207
    — see the spec-207 bullet), and one phrase deliberately contradicts its own legal name.**
    `NewsAttentionCollector.IsRelevant` is an unanchored case-insensitive `Contains` over `phrase OR
    ticker`, so `ITIC` ("cr**itic**", "pol**itic**al"), `GEOS` ("**geos**patial", "**geos**cience"), `CTO`
    ("dire**cto**r", "se**cto**r", "fa**cto**r", "do**cto**r"), `UTL` ("o**utl**ook", "o**utl**et",
    "o**utl**ine") and — **spec 200** — `ESQ` ("**Esq**uire", an ordinary word AND a publisher name) are
    phrase-only (the V/FR/CARS treatment). ⚠ **Spec 200 §1 corrected three phrases at the seed BEFORE their
    first collection** (§2 found zero history for all three ids): UTMD `query=Utah Medical Products&ticker=UTMD`
    (was `Utah Medical`, which admitted "University of Utah Medical …"), ITIC `query=Investors Title Company`
    (was `Investors Title`), ESQ `query=Esquire Financial` (was `…&ticker=ESQ`). Exact urls pinned by
    `ProductionCompanySeedTests`; six adversarial accept/reject headlines pinned through the collector's public
    surface in `NewsAttentionCollectorTests`. `IsRelevant` itself is UNCHANGED (a global predicate change needs
    its own corpus-wide audit). **JBSS is the spec-159 `&` trap again**:
    `TwoKeyFeedToken.TrySplit` splits on the FIRST `&`, so the url is exactly
    `query=John B. Sanfilippo&ticker=JBSS` — **do not "restore" the ampersand for consistency with
    `John B. Sanfilippo & Son`**, which would eat the ticker token. **NWPX carries TWO newssearch phrases**
    (`NWPX Infrastructure` + the legacy `Northwest Pipe`), the CARS precedent: `IsRelevant` consults only
    the feed's own phrase and never the aliases, so a renamed company with one phrase silently drops every
    legacy-brand headline. All four rules are pinned by test.
  - **`benchmark-universe-v1` is UNTOUCHED and no v2 was created.** Spec 183's rule is explicit — expansion
    is a **prospective** `benchmark-universe-v2`, never an edit — so the artifact keeps its 74 frozen
    members and content hash, and the 20 additions correctly report **`NotInBenchmarkUniverse`** on the
    pooled path rather than being silently admitted (which would retroactively insert later-selected members
    into every historical excess number). Asserted both ways: none of the 20 ids or tickers is a v1 member,
    and no `benchmark-universe-v2.json` exists.
  - ✅ **NO SCORING CHANGE, NO FINGERPRINT INPUT, ALL SIX PINS UNCHANGED, AND NO OPERATOR
    IDENTITY-RECORD CLEAR IS REQUIRED FOR THIS SLICE.** The watch universe is not a hashed input:
    no formula, weight, tier map, rule set, strategy, channel budget or config default changed, and
    `ScoringConfigFingerprintTests` is **untouched** (verified by `git diff`). The current values stand
    exactly as spec 198 set them — 30d `radar-scoring-fp-56c8e882beed`/`radar-scoring-fp-7d2b0cf537c4`, **60d
    live** `radar-scoring-fp-0ff442a14c1b`/**`radar-scoring-fp-11240da5aeb0`**, 120d
    `radar-scoring-fp-adf455313d35`/`radar-scoring-fp-7eece22968a4`. **199 adds no operator step of its
    own**, and the spec-198 one was UNCHANGED by 199 and was CLOSED by run 1 on 2026-08-29 (spec 200 §5
    record): `data/scoring-configs/strategies/` was verified empty beforehand and the first post-199 run
    recorded the first identity for `default` as **`radar-scoring-fp-11240da5aeb0`**
    (`logs/baseline-20260829T213002Z.log`). The step as it stood: before the first post-198 baseline,
    delete or re-record every configured `data/scoring-configs/strategies/{name}.json` (git-ignored, so
    it can never ride in a PR and must never be fabricated) and verify the first run reports
    **`radar-scoring-fp-11240da5aeb0`**.
  - ⚠ **THE SELECTION IS A HYPOTHESIS AND IS RECORDED AS ONE.** Coverage cannot be measured for a company
    that is not yet in the universe — it has no observations — so seed-time selection is a **prediction**.
    The predicted attention bands (low < 55 / mid 55–70 / high > 70), committed here so they can be judged:
    **low (9)** UTMD, FLXS, ITIC, SGA, SENEA, GEOS, OLP, UTL, RGCO; **mid (11)** GHM, CLMB, MLAB, JOUT, ESQ,
    OOMA, JBSS, NWPX, KOP, EPM, CTO; **high (0)**. The DURABLE record — one row per company carrying its
    CIK, sector, predicted band and the one-line obscurity REASON it was selected on — is
    `docs/cohorts/under-covered-2026-08.md` (the spec-166 `event-enriched-2026-07.json` precedent;
    human-read at the retrospective, deliberately wired into NO code path). This bullet must not be the
    only copy, and the two must not disagree. After **three** post-199 runs, compare predicted against
    measured `AttentionScore` and report the hit rate. **If the additions cluster ABOVE 70 the under-covered
    heuristic FAILED and that must be reported as a failed heuristic, not quietly absorbed** — it would mean
    seed-time judgement cannot identify under-covered names, which is worth knowing before any further
    expansion. ⚠ **Cold-start caveat (spec 200 §4):** `AttentionScore` is a 60-day window and three daily runs
    hold only a few days of capture, so the three-run read tests query relevance, capture shape and early
    calibration — it is NOT proof of durable under-coverage, and no company may be removed, re-tiered or
    feed-tuned from it; the mature read is the first successful run whose 60-day window starts no earlier
    than the first post-199 collection instant — recorded by spec 200 Phase B on 2026-09-03 as the first
    successful run whose `WindowEndUtc` ≥ 2026-10-28T21:44:52Z (first post-199 collection instant
    2026-08-29T21:44:52Z + 60 days; descriptive only, no gate).
  - **The capacity premise, MEASURED — and the spec's own projection was wrong in BOTH directions.**
    ⚠ **No post-198 baseline existed yet at spec 199** (the latest run was `fa50b516`, 2026-08-28T21:40Z,
    which is PRE-198), so §5's "measure against the first post-198 run" is measured here from the last three
    PRE-198 runs plus spec 198's own live measurement, and **the post-198 check was still OWED on the first
    post-198 baseline — since MEASURED by spec 200 §5 (2026-09-03, DRAINING)**. Measured typing drain, `untypedRemaining` per run: 2026-08-26 **2,028** → 08-27 **1,927** →
    08-28 **1,821**, i.e. **101 and 106 per run** — so the spec's premise of "~58/run today" **understated**
    current capacity. Per run: 350 provider calls attempted, ~337–342 completed outcomes persisted, 0
    provider/parse failures, 10 validation failures (08-28). Observations captured: 08-26 **255**, 08-27
    **236**, 08-28 **234 new / 1,370 cross-run deduped**. Of the 234 captured on 08-28, **194 were ≤7 days
    old** (08-27: 199 of 236; 08-26: 195 of 255), so under spec 198's 7-day window those ~195 still arrive,
    and with 198's measured **+15.5 %** recent-coverage gain (683 → 789 admitted) post-198 inflow projects
    to **~225/run** — close to today's 234 rather than the spec's assumed drop to 210. Projected post-198
    drain at 74 companies is therefore **≈115/run** (not the spec's 140), and at 94 companies inflow is
    **≈286/run worst case** (pro-rata; under-covered additions should produce LESS than the average, so it
    is an **upper bound**) giving a projected drain of **≈54/run**. ~~**Still clearly draining, so the spec's
    ship condition is met and the FULL 20 ships**; the 1,821 backlog clears in ~34 runs at that rate.~~
    ⚠ **SUPERSEDED BY SPEC 200: every post-198/post-199 figure in this bullet is PROJECTED, NOT MEASURED.**
    The ship condition was a live-measurement condition and was never measured on the code that shipped — the
    batch shipped on a projection while the post-198 measurement was still owed; the pre-expansion post-198
    baseline no longer exists and must not be manufactured. The live measurement is **spec 200 §5** (first
    three successful post-199 full runs, run 1 reported separately as the one-time seed burst; verdict
    DRAINING / NOT DRAINING / UNRESOLVED, missing data never read as zero). **MEASURED 2026-09-03: DRAINING**
    (2,172 → 1,941 → 1,816; steady-state drain 125–231/run against the projected ~54; run 3's typing
    accounting from its log because the date-keyed artifact was overwritten by the same-day run). No
    typing or judgment budget was changed — a budget change is a separate decision.
  - **Expected operational consequences, recorded NOT discovered, so the first post-199 run is read
    correctly and nothing reads as a regression**: per-run scorings rise **740 → ≈940** (94 companies × 10
    strategies); **82 additional feeds**, taking the seed from 359 to **441** (4 per company = 80, plus
    GHM's rss and NWPX's second newssearch), so
    collection wall-clock rises above the 2026-08-28 **1h49m** baseline and must be confirmed to stay inside
    the scheduled window; **price history backfills one year per new ticker on the first run**, a one-off
    cost outside the pipeline (AD-14 — price is validation only, never a scoring input); and the new
    companies have **NO accrued evidence**, so their first scores are thin and their attention is low.
    ⚠ **They will therefore look artificially attractive under the inverse-attention discount before their
    evidence accrues — an early high rank for a new company MUST NOT be read as a finding**, and the report
    period's interpretation should say so. **Efficacy is a THREE-WAY boundary (spec 200 §4), not "they
    enter the efficacy series"**: (1) live strategy/report scoring is immediate for all 94 as soon as
    evidence exists; (2) raw forward-return diagnostics appear only after a company's horizon resolves (~21+
    days, initially spec-152 `PartialWindow`) and grant NO benchmark membership; (3) the official
    benchmark-v1 leaderboard and the paired AD-15 claim exclude all 20 as `NotInBenchmarkUniverse` until a
    prospective `benchmark-universe-v2` — none created, the 2026-09-29 AD-15 boundary unmoved.
  - **Out of scope, recorded not built**: creating `benchmark-universe-v2`; changing any existing company's
    tier, feeds or identity; any new collector, feed kind or provider; any typing/judgment budget change;
    and backfilling evidence, prices or scores for the additions (they accrue forward, like every other
    company did).
- **The expansion is VALIDATED on live runs — capacity DRAINING, the cold-start attention read recorded
  honestly, and three feed identities repaired before they could accrue history (spec 200, two phases:
  Phase A 2026-08-29, Phase B 2026-09-03).** Spec 199's record carried three defects: three `newssearch`
  phrases loose enough for `NewsAttentionCollector.IsRelevant`'s unanchored substring rule to admit the
  wrong company, a capacity ship condition declared "met" from a projection, and "they enter the efficacy
  series" wording that conflated three products. Spec 200 repaired all three and then measured the
  expansion on the first three successful post-199 full runs.
  - **Phase A (2026-08-29, PR #205, merged BEFORE the first post-199 collection — the boundary was clean).**
    §2 inspected the durable stores read-only (per-company `data/scores`/`signals`/`prices`/`news-typing`
    directories, `companyId` in `data/news-observations`, the run records; no recursive flat grep) and found
    **ZERO history** for all three ids against the latest durable run `fa50b516` (2026-08-28, 74 companies,
    pre-199), so the corrections landed at the seed with no migration, no contamination label and nothing
    rewritten. The three repairs, exact: UTMD `query=Utah Medical&ticker=UTMD` → `query=Utah Medical
    Products&ticker=UTMD` (the old phrase admitted "University of Utah Medical …"); ITIC `query=Investors
    Title` → `query=Investors Title Company`; ESQ `query=Esquire Financial&ticker=ESQ` → `query=Esquire
    Financial` — **ESQ joins the colliding-ticker allowlist** ("Esquire" is an ordinary word AND a publisher
    name), making FIVE phrase-only additions. The exact urls and six adversarial accept/reject headlines are
    pinned through the collector's public surface (`ProductionCompanySeedTests`,
    `NewsAttentionCollectorTests`). **Deliberately NO global `IsRelevant` change**: a boundary-aware ticker
    predicate would re-decide accepted history for every ticker-bearing feed and needs its own corpus-wide
    audit; it is recorded as deferred, not smuggled in. Spec 199's capacity claim was relabelled
    **PROJECTED, NOT MEASURED** in place (its "ship condition met" struck through as superseded — the batch
    shipped on a projection while the post-198 measurement was owed, and the pre-expansion post-198 baseline
    no longer exists and must not be manufactured), and its efficacy wording was replaced by the explicit
    **three-way boundary**: (1) live strategy/report scoring is immediate for all 94; (2) raw forward-return
    diagnostics appear once a horizon resolves and grant no benchmark membership; (3) the official
    benchmark-v1 leaderboard and the paired AD-15 claim exclude the 20 as `NotInBenchmarkUniverse` until a
    prospective `benchmark-universe-v2` — none created, the 2026-09-29 AD-15 boundary unmoved. Phase A did
    NOT promote the spec; it stayed in `docs/next/` until Phase B.
  - **Phase B (2026-09-03) — §5 capacity verdict: DRAINING.** The three successful post-199 full runs
    (default profile, exit 0, 94 scored, 11 strategies, `collectionWarnings` empty, every `*NotPersisted`
    counter 0): run 1 `70f256e3` (as-of 2026-08-29T21:44:52Z, the ONE-TIME SEED BURST), run 2 `b6d52f64`
    (2026-08-30T21:46:25Z), run 3 `7d4dbce3` (2026-09-01T02:50:16Z). `untypedRemaining` from the typing
    reader `deepinfra-deepseek` (cohort `openai:deepseek-ai/DeepSeek-V4-Flash|news-typing-prompt-v1|
    news-typing-schema-v1|news-event-taxonomy-v1`, budget 350/run unchanged): 1,821 (pre-199, 2026-08-28) →
    **2,172** (run 1: the seed burst RAISED the backlog **+351**, 454 unfiltered first-collection observations
    for the 20 additions, reported separately) → **1,941** (run 2, **−231**) → **1,816** (run 3, **−125**;
    aggregate run3 − run1 **−356**). Sources: `data/news-typing/live/attention-decomposition-2026-08-28.json`,
    `…-2026-08-29.json` (`runId` 70f256e3), `…-2026-08-30.json` (`runId` b6d52f64), and for run 3
    **`logs/baseline-20260901T021014Z.log`** ("1816 untyped observation(s) remain") — the same quantity from
    the same code path, proven identical to the artifact value on runs 1 and 2. Steady-state drain measured
    **125–231/run against the PROJECTED ~54/run** (the projection was pessimistic); steady-state admitted
    inflow 114–215/run (batch `observationsWritten` 693 seed / 114 / 215) against the projected ~286 upper
    bound. Supplementary, NOT part of the verdict: runs 4 `35b57cfd` and 5 `fd2575b7` have DURABLE artifacts
    and continue the drain, **1,688** and **1,585**. Consequence: universe expansion is NOT frozen; spec 207's
    gate may read this verdict. Run 3's 06:30:46 wall-clock is not steady-state comparable (the host was
    suspended mid-run: one 87-minute provider call, `hydrationElapsed` 00:36:22 vs 00:05:15 on run 2) but the
    run is complete and counts. The spec-198 operator precondition CLOSED on run 1: the directory was
    verified empty beforehand and the first identity recorded for `default` was
    **`radar-scoring-fp-11240da5aeb0`** (`logs/baseline-20260829T213002Z.log`); every `default` snapshot of
    runs 1–5 carries it.
  - **Phase B — §6 cold-start attention read, all 20 rows retained, no prediction revised.** Fixed snapshot:
    run 3's `default` snapshot, `WindowEndUtc` 2026-09-01T02:50:16.5514898Z. **0 of 20 above 70** (the
    FAILED-heuristic condition is not triggered); **low 8/9** (SENEA the miss at 57, the only row to reach
    mid — it saturated the 25-item retained prefix on runs 2 and 3); **mid 0/11** (every mid prediction
    measured low, 33–46); overall 8/20; **EPM, the declared RISK CASE, 40 low** — the precommitted
    investor-platform/dividend coverage risk did NOT materialise, the miss is in the opposite direction;
    0 unresolved, 0 contaminated (§2's zero-history finding). The mechanical reason for the whole mid band
    measuring low is cold start, not the companies: three days of capture inside a 60-day window against
    incumbents carrying 60 days (`small` mean 62.0 at spec 199), so the cohort is depressed as a whole
    (26–57) and within-cohort separation cannot be tested yet. The read tested query relevance (every one of
    the 20 companies returned relevant items on run 1), capture shape and early calibration; it does NOT
    validate the under-coverage thesis and **no company is removed, re-tiered or feed-tuned**. Full table in
    `docs/cohorts/under-covered-2026-08.md` and the spec's §6 record. **Mature read date (§4):** the first
    successful run whose `WindowEndUtc` ≥ **2026-10-28T21:44:52Z** (first post-199 collection instant
    2026-08-29T21:44:52Z + 60 days) — descriptive only, not a gate.
  - **Defect FOUND, recorded, NOT fixed by spec 200 — FIXED by spec 208 (2026-09-03, run-scoped
    `attention-decomposition-{instant}-{runId}` name; see the spec-208 bullet).**
    `FileNewsTypingArtifactStore` keyed the decomposition artifact by as-of DATE
    (`{root}/live/attention-decomposition-{asOfDate}.md|.json`); two
    successful full runs on 2026-09-01 (run 3 at 02:50Z, run 4 at 21:46Z) meant run 4 silently OVERWROTE run
    3's artifact (`attention-decomposition-2026-09-01.json` carries `runId` 35b57cfd), so run 3's typing
    accounting survives only in its log — a "nothing may be discarded without being counted" violation. Had
    the log not existed the §5 verdict would have been UNRESOLVED.
  - **Nothing moved.** Phase B changed no code, test, seed, config or data; Phase A's seed-only query edits
    are not a hashed input. **Spec 200 moved nothing**: no formula, weight, strategy, channel, prompt, typing
    budget, recency window or fingerprint pin; `benchmark-universe-v1` byte-identical, no v2; the 2026-09-29
    AD-15 boundary unmoved. The spec was promoted from `docs/next/` to `docs/` by the Phase B PR.
- **The AI-robotics theme enters the universe — 94 → 102, additions only, gated on spec 200's DRAINING
  verdict (spec 207, 2026-09-03).** The universe carried no robotics or AI-robotics name (nearest neighbours
  MRCY, KLIC, DGII, HLIO); the maintainer asked for the theme (2026-09-02). Spec 207's HARD entry condition
  was spec 200 §5's capacity verdict — NOT DRAINING or UNRESOLVED would have frozen expansion — and the
  recorded verdict is **DRAINING** (`untypedRemaining` 2,172 → 1,941 → 1,816), so the batch proceeded.
  - **Eight US-listed names, selected for fit with the under-covered small/mid thesis, not fame.** Small:
    PDYN (Palladyne AI, CIK 0001826681), STXS (Stereotaxis, 0001289340), CMCO (Columbus McKinnon,
    0001005229), ALNT (Allient, 0000046129). Mid: OUST (Ouster, 0001816581), PRCT (PROCEPT BioRobotics,
    0001588978), NOVT (Novanta, 0001076930), AMBA (Ambarella, 0001280263). `small` 55 → 59. Every CIK was
    resolved from `https://www.sec.gov/files/company_tickers.json` and live-verified against
    `data.sec.gov/submissions` on 2026-09-03 (HTTP 200, name + ticker matched); none was dropped.
    `ProductionCompanySeedTests.Spec207Ciks` OWNS those values. The heavily covered theme names (Serve
    Robotics, Ondas, SoundHound, BigBear.ai, Symbotic) were excluded as covered or too large; Richtech
    Robotics because its ticker `RR` is unusable as a news token (Rolls-Royce); FARO (acquired) and iRobot
    (Chapter 11) because neither is an independent US-listed company any longer. Each row carries exactly
    the four standard feeds (sec / secform4 / sec13dg / newssearch) — **no rss/IR press feed** for this
    batch; per-company IR feeds stay a separate, measured decision.
  - **Exact newssearch identities, chosen against the unanchored substring predicate at the seed — the
    spec-198/200 lesson applied BEFORE first collection.** `query=Palladyne AI&ticker=PDYN`,
    `query=Stereotaxis&ticker=STXS`, `query=Columbus McKinnon&ticker=CMCO` (never shorten to the bare
    "Columbus" — the city), `query=Allient&ticker=ALNT`, `query=Ouster Inc` (**no ticker token**),
    `query=PROCEPT BioRobotics&ticker=PRCT`, `query=Novanta&ticker=NOVT`, `query=Ambarella&ticker=AMBA`;
    all eight pinned byte-exact by `ProductionSeed_Spec207Additions_NewsSearchUrlIsExactly`. **OUST is the
    SIXTH phrase-only ticker** (after ITIC/GEOS/CTO/UTL at spec 199 and ESQ at spec 200): "ouster" is a
    common English noun ("the CEO's ouster"), colliding as BOTH the ticker and the bare company name, so the
    phrase carries `Inc` for precision. The adversarial pairs are pinned through the PUBLIC `CollectAsync`
    surface (`IsRelevant` stays private and unchanged): OUST rejects "Shareholders demand the CEO's ouster
    after proxy fight" and accepts "Ouster Inc. reports quarterly results"; CMCO rejects "Columbus city
    council approves transit plan" and accepts "Columbus McKinnon expands automation line".
  - **OUST is the declared identity RISK CASE, in both directions, recorded before any collection.** The
    `Ouster Inc` phrase trades recall for precision: a headline that writes only "Ouster (OUST)" is missed
    (pinned as a known miss by `CollectAsync_Spec207OustFeed_TickerOnlyHeadline_IsMissedByDesign`), so a
    LOW measured Attention for OUST may be about feed identity, not coverage; loosening the phrase would
    admit ouster-the-noun headlines and inflate Attention. Neither direction may be "fixed" by tuning inside
    the spec — a starving feed is a measured follow-up spec against the relevance predicate, never a quiet
    query edit.
  - **Predeclared, falsifiable attention predictions: 3 low / 5 mid / 0 high** (bands low <55 / mid 55–70 /
    high >70 against the stored 60-day `AttentionScore`): PDYN mid, STXS low, CMCO low, ALNT low, OUST mid,
    PRCT mid, NOVT mid, AMBA mid — committed in `docs/cohorts/ai-robotics-2026-09.md`, a human-read record
    wired into no code path. **The three-run retrospective is OWED there**, entry condition three successful
    post-207 full runs, cold-start caveats identical to spec 200 §4 (a few days of capture under a 60-day
    window tests query relevance and capture shape, not the coverage thesis; no removal, re-tier or feed
    tuning from the read), together with the per-run `untypedRemaining` deltas — the eight unfiltered first
    collections are a deliberate seed spike and the post-spike drain check reuses spec 200 §5's arithmetic.
    Every forward-looking number in the spec is PROJECTED until those runs exist. Spec 207 is single-phase:
    it promoted to `docs/` on implementation because the owed measurement lives in the cohort doc.
  - **Spec 207 moved nothing.** No formula, weight, strategy, channel, prompt, typing budget, recency window
    or fingerprint pin (the seed is not a hashed input; `ScoringConfigFingerprintTests` unchanged);
    `benchmark-universe-v1` byte-identical, no v2 — the eight report `NotInBenchmarkUniverse`; no AD-15/AD-16
    boundary movement; no global change to the relevance predicate (OUST's allowlist entry is the whole
    predicate change).
- **The typing decomposition artifact is run-scoped — as-of instant + run id, never the as-of date alone
  (spec 208, 2026-09-03).** `FileNewsTypingArtifactStore` named the pair
  `attention-decomposition-{yyyy-MM-dd}.md|.json` (and `…-FAILED.md`), so two full runs on one UTC date wrote
  the same path and the second silently destroyed the first — measured, not hypothetical: on 2026-09-01 the
  21:46Z scheduled run overwrote the 02:50Z run's artifact (run 3 of spec 200 §5), whose `untypedRemaining`
  checkpoint then survived only in the scheduled-run wrapper log. The identity is now
  `attention-decomposition-{yyyyMMdd'T'HHmmss'Z'}-{runId:D}` (FAILED: `…-{instant}-{runId}-FAILED.md`), owned by
  ONE pure helper, `NewsTypingArtifactNames` (Application, beside `INewsTypingArtifactStore`), so the live
  pair, the FAILED variant and the tests cannot drift; the interface takes `(DateTimeOffset asOfUtc, Guid?
  runId)` explicitly and `NewsTypingGenerator` threads the run id and the run record's `CreatedAtUtc` through
  both call sites (the failure path stays anchored on the NOW instant, as before, and is run-scoped too). An
  absent run id — unreachable today, typing runs only in unfiltered full mode which always mints one — writes
  the instant-only name and logs ONE Warning; nothing throws and no GUID is fabricated. Mutation-proven in
  `FileNewsTypingArtifactStoreTests`: same instant + two run ids → two surviving pairs with the first intact;
  the 2026-09-01 shape (02:50Z then 21:46Z) coexists; the pinned name
  `attention-decomposition-20260901T025000Z-0f8fad5b-d9cb-469f-a165-70867728950e.md` is asserted byte-exact.
  - **Accrued date-keyed artifacts heal forward only.** Existing `attention-decomposition-{yyyy-MM-dd}.*`
    files are not renamed, migrated, rewritten or reconstructed (a legacy pair on disk is proven
    byte-for-byte untouched by a same-date run-scoped write); the 02:50Z 2026-09-01 artifact is permanently
    lost and spec 200 §5's wrapper-log alternate source stands. Nothing in `src/` reads these artifacts and
    spec 208 adds no reader. CLAUDE.md owed follow-up (ii) is amended in place to DONE.
  - **Spec 208 moved nothing.** No scoring, formula, weight, strategy, channel, prompt, typing budget,
    recency window, report writer (the weekly/daily same-day overwrite is correct for derived views and is
    out of scope) or fingerprint pin; `ScoringConfigFingerprintTests` unchanged.
- **The insider channel tells the truth in aggregate and in its label — one shared metadata contract, a
  presentation-only relabel over BOTH render paths, and a structured window summary that says "not captured"
  (spec 209, 2026-09-05).** Two 2026-09-04 skeptic reviews found the channel misfiring in opposite directions
  with every per-filing rule behaving as designed: NWPX's eleven weekly 10b5-1 plan filings each rendered
  as `InsiderBuying (Neutral)` (a directional label over filings whose direction the store never captured —
  wording amended by spec 211 — and no per-filing rule can see a six-week cadence), while AGX's externally reported ~$119M H1 sales reached the store as one $3.3M
  `discretionary-sale`. The §1 audit (`docs/cohorts/insider-flow-audit-2026-09.md`, persisted data only) is
  the measurement; the code changes are three:
  - **The classification tokens and metadata keys moved to Application.** The five spec-156 tokens
    (`plan-10b5-1`, `discretionary-buy`, `discretionary-sale`, `mixed-buy-sell`,
    `no-discretionary-transactions`) lived in Infrastructure's `SecForm4ClassificationReasons` while the
    report builder that must read them is Application — so the reader either duplicated magic strings or
    inverted the layering. They now live verbatim (byte-exact pinned; they are persisted data) in
    `InsiderActivityMetadata` (`Radar.Application.Collectors`, beside `EvidenceMetadata`) together with the
    key consts (`insiderClassificationReason`, `insiderNetValue`, `insiderCluster`, `insiderDirection`,
    `form`/`4`, `filingDate`) and a never-throwing `TryRead(EvidenceItem) → InsiderActivityRead?` (null for
    a non-Form-4; every member null means "not captured", never 0). `HttpSecForm4Reader`,
    `SecForm4Collector` (whose `MetadataMarkerKey` is now `= InsiderActivityMetadata.DirectionKey`) and
    `KeywordSignalExtractor` write/read through it; the Infrastructure class is DELETED. Zero behaviour
    change on the write side or in extraction (existing tests unchanged).
  - **`InsiderBuying` is rendered as `InsiderActivity` on BOTH paths the token reaches the reader.** The
    stored `SignalType.InsiderBuying` member is untouched (accrued signals deserialize unchanged; no enum
    rename, no JSON rewrite). `MarkdownWeeklyReportRenderer.DisplaySignalType` maps it at the
    renderer-owned type site, AND `DisplayProvenanceText` (a compiled whole-token `\bInsiderBuying\b`
    regex) rewrites the exact token inside stored evidence-link contribution reasons and signal reasons at
    render time. **This partially supersedes spec 167's stance that the display mapping "must NEVER be
    applied to stored provenance text"** — amended in place in the renderer comment — for that ONE exact
    token only, because a legend cannot un-invert a label a reader sees eleven times; GuidanceChange's
    stored text still renders byte-verbatim with its legend, and every other byte of stored text is
    unchanged (pinned: `NotInsiderBuyingX` is not rewritten). One legend line is added after the
    GuidanceChange one; it deliberately does not name the stored token because the report-language tests
    forbid the substrings "buy"/"sell", so all new wording uses "purchase value" / "sale value" /
    "10b5-1 plan filing" (spec 211 amended this from "planned disposition") / "mixed purchase-and-sale".
  - **`WeeklyReportEntry.InsiderActivity` (an `InsiderActivitySummary?`) — report-side, numerically inert,
    not persisted, no scoring input, no fingerprint input.** Built by `InsiderActivitySummary.From` (pure,
    static, unit-tested alone) over the DISTINCT evidence items (by id) behind the snapshot's links — the
    builder now loads each distinct evidence id ONCE (`LoadLinkedEvidenceAsync`) and both the evidence-ref
    block and the summary read that one load; nothing calls `GetAllAsync`. Buckets mirror the persisted
    taxonomy exactly: filing count; 10b5-1 plan-filing count with first/last filing date and a span in
    elapsed days (`(last − first).Days`, stated ONLY when ≥ 2 plan filings and ALL are dated — an undated
    one is counted in `Plan10b51UndatedCount` and the span renders "not established"); discretionary
    purchase count + summed captured value (null when none captured, never 0) + not-captured count; the same
    for sales; a **mixed count with NO value member at all** (the persisted magnitude is
    `Math.Max(purchase, sale)`, neither net nor total, and is never summed into any value column);
    no-discretionary count; unknown (legacy, no token) count; and an unrecognised-token count (a stored
    token outside the closed set is counted AND named in ONE aggregated warning per company). The window
    rule is the scoring convention, exclusive-start inclusive-end `(WindowStartUtc, WindowEndUtc]` on
    `PublishedAtUtc ?? CollectedAtUtc`; anything outside lands in `OutsideWindowCount` — excluded from every
    other bucket, never silently dropped. Null on the entry means no Form 4 evidence is linked at all and
    the renderer prints nothing (never a fabricated "0 filings"). The rendered line is ONE line in a fixed
    clause order; the NWPX shape is pinned byte-exact:
    `- Insider activity (Form 4, this window): 11 filings; 11 10b5-1 plan filings across 29 days; transaction value not captured`.
    **Spec 211 corrected this bucket's name in place** (`PlannedDisposition*` → `Plan10b51*`, rendered
    "planned-disposition" → "10b5-1 plan filing") because the reader forces every plan transaction Neutral
    BEFORE reading its codes, so a plan filing's transaction direction is never captured and the original
    name stated an inference the store does not carry.
  - **§4 forward transaction-code capture is DEFERRED** to its own slice: per-filing code tallies and gross
    purchase/sale values for NEW filings (even when the plan flag forces Neutral) are not implemented here,
    not partially. Historical plan filings' codes and values are UNKNOWN and stay unknown (no backfill, no
    re-fetch of accrued filings) — every aggregate renders what was captured and says "not captured" for
    the rest.
  - **Spec 209 moved nothing.** No scoring, formula, weight, strategy, channel, tier, cluster boost,
    collection window, fetch depth, `KeywordSignalExtractor.RuleSetVersion`, phrase table or fingerprint pin
    (`ScoringConfigFingerprintTests` unchanged); `SecForm4TransactionCode.Classify` and the
    10b5-1-forces-Neutral rule untouched.
- **The Watch floor names what it counted — every counted type's distinct (source class, observed date,
  judgment-derived) support tuples, or an honest range summary; labels, count and threshold byte-identical
  (spec 210, 2026-09-05).** ⚠ **AMENDED IN PLACE BY SPEC 212 (2026-09-07):** "threshold byte-identical"
  was true of spec 210 only. From 2026-08-23 (the Lead taking the narrative, spec 184) until spec 212 the
  floor had been the ONLY source of `Watch` labels — every one of the 307 accrued `Watch` labels in that
  span, 18 of 18 on the 2026-09-06 report — because the number it floors against was a v8-tuned constant
  (40) that the v11 Lead never reached (max 21 over 3,471 snapshots). That number is now the labelled
  arm's per-Lead Watch line (`Radar:Strategies[i].Labels`, spec 212); the floor's rule, count, tiers and
  rationale contract are unchanged. The 2026-09-04 NWPX skeptic review raised the hypothesis that one real-world
  announcement can wear two extractors' clothes — a keyword-typed filing signal plus a judgment-derived
  `MediaAttention` from the same event's coverage — and satisfy `WeeklyReportActionPolicyV1`'s
  `>= MinCorroboratingSignalTypes` distinct-positive-TYPES floor without independent corroboration. The
  pre-spec review reframed it as a hypothesis to MEASURE (NWPX's live floor rests on a 2026-07-29 8-K plus
  September news — separate dates), so this slice makes the shape visible and pins it synthetically; no
  cross-source event identity is built. Three changes:
  - **`ReportSignalRef` gained three TRAILING, DEFAULTED, NULLABLE members** — `ObservedAtUtc`,
    `SourceType` (the CANONICAL `EvidenceSourceType`, never an informal class) and `IsJudgmentDerived`
    (from the ONE parser, `NewsDirectionalSignalMetadata.IsJudgmentDerived`). `null` = not recorded, never
    a silent false: a missing member renders `source unknown` / `date unknown` / `judgment unknown`. Every
    pre-210 positional construction site compiles unchanged.
  - **`WeeklyReportBuilder` loads evidence BEFORE `Decide`, and the single spec-209 load
    (`LoadLinkedEvidenceAsync`, one lookup per distinct evidence id) now feeds THREE consumers**: the
    contributing-signal refs (source type via `signal.EvidenceId`, which is the link's id), the
    evidence-ref block and the insider-activity aggregate. No second per-company evidence pass (the spec-203
    lesson). A signal whose cited evidence is not in the load — an id outside the links, or a linked item the
    store did not return — keeps `SourceType == null` and is counted in ONE aggregated warning per snapshot
    (both causes named), never one line per signal and never a fabricated default.
  - **`WeeklyReportActionPolicyV1.Version` is `weekly-report-action-v2 → v3`** because the rationale
    CONTRACT changed while labels did not. The floor's count logic is byte-identical (`GroupBy(Type).Count`
    ≡ the old `Distinct().Count()`), the rationale keeps its v2 prefix verbatim and appends `: ` plus, per
    counted type in enum order joined by ` + `, `Type (tuple; tuple; …)` — tuples DISTINCT on (source class,
    date, judgment flag), ordered date (unknown last) → source class → flag, rendered `{source} {yyyy-MM-dd}`
    with `, judgment` when judgment-derived. Above `MaxRenderedSupportTuplesPerType = 3` the type renders
    `{k} distinct dates {earliest}–{latest} (+{u} date unknown), {n} support tuples` (singular form for one
    date; parenthetical only when u > 0) — nothing picks a "first" or "latest" tuple silently, because an
    arbitrary choice could manufacture or hide an echo. The source-class display is ONE internal `switch`
    (`DescribeSourceClass`) with an explicit `source unknown` fallback for `null`/undefined values, and a
    test proves every defined `EvidenceSourceType` member maps to a named class. **v3 rendered the STORED
    `SignalType` token** (`GuidanceChange`, `InsiderBuying`), reasoning from spec 167's pin that the display
    token never leaks into a policy rationale — **SUPERSEDED by spec 211 (v4)**: that bypassed the report's
    presentation relabels (`GuidanceChange` on 15 of 19 live floored lines; the forbidden `InsiderBuying`
    substring on LBRT's), so the printed name now goes through the shared `SignalTypeDisplay` seam and the
    same synthetic same-day fixture is pinned byte-exact at policy level as
    `EarningsTrajectory (filing 2026-09-02) + MediaAttention (news 2026-09-02, judgment)` (the builder-level
    fixture in `WeeklyReportBuilderTests.CorroborationProvenance` pins the same shape dated 2026-02-05);
    grouping, ordering and the count still run on the stored enum.
  - **Spec 210 moved nothing else.** No label outcome (a full-report fixture and a representative-matrix sweep
    prove null-vs-populated provenance decides identically on every tier — only the floored rationale differs),
    no count, threshold, scoring, signal, formula, weight or fingerprint pin (`ScoringConfigFingerprintTests`
    unchanged); the daily news report is untouched. The §3 live audit (raw floor firings vs deduplicated
    support episodes; whether a genuine same-day cross-extractor echo occurs) is recorded in
    `docs/cohorts/watch-floor-episodes-2026-09.md` (measured 2026-09-05): 288 raw firings over 41 weekly
    reports collapse to 55 support episodes; 9 of 55 match the same-day filing-typed + judgment-derived
    `MediaAttention` shape, and title inspection confirms a GENUINE live echo — Ooma, 2026-08-26, whose floor in
    `radar-weekly-2026-08-30` rested entirely on one earnings 8-K and same-day coverage of it — now pinned beside
    the synthetic fixture as `Floor_Rationale_Makes_Live_Ooma_2026_08_26_Same_Day_Echo_Visible`. Whether that
    changes what gets floored is the maintainer's call; spec 210 changed no label.
- **Two display-truth corrections left by specs 209/210 — a 10b5-1 plan filing has no known direction, and
  the Watch-floor rationale prints the report's labels through ONE shared seam (spec 211, 2026-09-06).** The
  2026-09-05 post-merge review found both live in `radar-weekly-2026-09-05.md`: 17 insider lines said
  "planned-disposition" although `HttpSecForm4Reader` forces every plan transaction Neutral BEFORE reading
  its codes (the store cannot say acquisition or disposition), and 15 of 19 floored `- Why:` lines printed
  `GuidanceChange` — plus `InsiderBuying`, the forbidden substring, on LBRT's — because the renderer's two
  relabels were private to the renderer and the policy never saw them. Presentation only; three changes:
  - **`InsiderActivitySummary.PlannedDisposition*` → `Plan10b51*`** (named for the persisted token
    `InsiderActivityMetadata.Plan10b51`); the doc-comment states the direction is not captured; the rendered
    clause is `N 10b5-1 plan filing(s)` through the existing `Plural` helper, with NWPX re-pinned byte-exact:
    `- Insider activity (Form 4, this window): 11 filings; 11 10b5-1 plan filings across 29 days; transaction value not captured`.
    The renderer, `HttpSecForm4Reader` and `SecForm4TransactionCode` comments that called a plan filing a
    disposition/sale were amended in place; no reader behaviour changed.
  - **`SignalTypeDisplay` (`Radar.Application.Reporting`, references Domain only) is THE owner of the
    presentation mapping**: `Label(SignalType)` (`GuidanceChange → EarningsTrajectory`, `InsiderBuying →
    InsiderActivity`, every other member its own name) and `RewriteStoredProvenance` (the spec-209 whole-token
    `\bInsiderBuying\b` rewrite, semantics unchanged — `NotInsiderBuyingX` untouched, stored `GuidanceChange`
    verbatim). The renderer's private `DisplaySignalType` / `StoredInsiderTypeToken` / `DisplayProvenanceText`
    are DELETED (not kept as a second copy) and its three sites route through the seam; the spec-167/209
    "why" comment block moved onto the type. `WeeklyReportActionPolicyV1` names each floor-support group with
    `SignalTypeDisplay.Label(g.Key)` while `GroupBy(Type).OrderBy(Key)` and the count stay on the stored
    enum, and its `Version` is `weekly-report-action-v3 → v4` (rationale contract changed; every label
    byte-identical, proven by a matrix × tier sweep that reconstructs the v3 text and diffs it by exactly the
    two names). `SignalTypeDisplayGuardrailTests` scans the renderer and the policy source (comments
    stripped) and fails on any `SignalType`-to-string site other than the seam — a `SignalType.X =>` arm, the
    label literals, the stored token in code, a `{g.Key}` / `{x.Type}` interpolation — with a self-test that
    the patterns catch the exact shapes that shipped the defect. The LBRT shape (`InsiderBuying` +
    `StrategicPartnership`, both filings) and the `GuidanceChange` + `MediaAttention` shape are crossed into
    the policy's forbidden-language sweep, so the substring that slipped through now fails at test time.
  - **Spec 211 moved nothing.** No signal, score, weight, strategy, channel, stored JSON or accrued file;
    nothing hashes `WeeklyReportActionPolicyV1.Version` into `ScoringConfigVersion` (grep-verified in the
    PR), so no fingerprint pin moved (`ScoringConfigFingerprintTests` unchanged). Docs amended IN PLACE (spec
    209's wording, the spec-209/210 bullets above, `docs/reading-radar-output.md` → v4 with the
    presentation-label note); both 2026-09-05 cohort audits keep their measured rows verbatim under a
    wording-changed note. **§4 MEASURED (first post-merge full run, 2026-09-07 21:30Z, report
    `radar-weekly-2026-09-07.md`; before = the 2026-09-05 report):** floored `- Why:` lines containing
    `GuidanceChange` 15 of 19 → **0**; containing `InsiderBuying` 1 (LBRT) → **0**; insider lines saying
    `planned-disposition` 17 → **0**; saying `10b5-1 plan filing` 0 → **18**. Descriptive, no gate.
- **The Investigate / Watch lines are per-Lead config, explicit, fixed, REQUIRED for every declared Lead
  and stated on the report — no longer two constants tuned for one formula (spec 212, 2026-09-07).**
  `WeeklyReportActionPolicyV1` labelled `Investigate` at Opportunity ≥ 60 and `Watch` at ≥ 40 from two
  private constants set when `radar-formula-v8`'s multi-channel composite was the only formula. Since spec
  184 the labels follow the LEAD (`disclosure-led-v11`), whose Opportunity is `100 × S × P × notedness`
  over the FILINGS channel alone — both saturating terms tuned for all-channel mass, so on one channel the
  composite collapses (Opportunity 40 needs mass ≈ 10.6 undiscounted / ≈ 17 at a 0.75 discount; 60 needs
  ≈ 21.6 / ≈ 54; EDGAR supplies 2–4 directional reads per window and the best company carries mass ≈ 6).
  **Measured 2026-09-07, read-only over every accrued snapshot under `data/scores/` +
  `data/scores/strategies/` (as-of dates 2026-07-29 → 2026-09-06; the spec's own pre-implementation
  measurement — `scripts/audit-label-thresholds.ps1 -FromDate 2026-07-29` re-measures it at implementation
  time and reports `default` ≥ 40 as 75 of 3,545, see the re-measurement paragraph below):**

  | arm | formula | n | dates | max | p99 | p90 | p50 | ≥ 60 | ≥ 40 | share ≥ 40 |
  | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
  | disclosure-led-v11 (**Lead**) | v11 | 3,471 | 35 | 21 | 18 | 12 | 0 | 0 | 0 | 0.0% |
  | disclosure-led-v10-control | v10 | 3,471 | 35 | 21 | 18 | 13 | 0 | 0 | 0 | 0.0% |
  | filings-led-v2 / -halfnoted / -nonoted | v9 | ≈3,650 | 37–38 | 28 / 31 / 39 | 25 / 30 / 37 | 22 / 27 / 32 | 12 / 17 / 21 | 0 | 0 | 0.0% |
  | narrative-led-v2 | v9 | 3,557 | 36 | 39 | 27 | 21 | 17 | 0 | 0 | 0.0% |
  | default (storage primary) | v8 | 3,545 | 36 | 44 | 42 | 31 | 18 | 0 | 74 | 2.1% |
  | default-noattn | v8, no discount | 878 | 7 | 76 | 71 | 52 | 30 | 21 | 174 | 19.8% |
  | baseline-earnings-only | comparator | 3,471 | 35 | 52 | 50 | 42 | 15 | 0 | 602 | 17.3% |
  | baseline-media-only | comparator | 3,471 | 35 | 50 | 43 | 39 | 31 | 0 | 203 | 5.8% |
  | baseline-activity-only | comparator | 3,471 | 35 | 41 | 33 | 24 | 17 | 0 | 5 | 0.1% |

  The scales are not comparable — `default-noattn` (v8 without the discount) puts 19.8% of snapshots at
  ≥ 40 against `default`'s 2.1%, so a line is a property of the ARM, not the formula version. From the 60
  accrued reports: `Investigate` rendered 4 times ever (all single-strategy era); the last score-based
  `Watch` is in the 2026-08-14 report; since the Lead took the narrative (2026-08-23) zero labels came from
  a score — the CLAUDE.md live-distribution defect exactly (a near-constant against a fixed threshold,
  provably correct, discriminating nothing). What shipped:
  - **`LabelThresholds(Investigate, Watch)` in `Radar.Application.Scoring`** beside `StrategyPurpose` (so
    Scoring never depends back on Reporting), invariant `0 < Watch < Investigate ≤ 100` in the constructor,
    `Default = (60, 40)` the ONLY definition of those numbers; the policy's constants are deleted.
    `ScoringStrategyDefinition.Labels` is **nullable** — omitted ≠ an explicit `{ 60, 40 }`. Bound from
    `Radar:Strategies[i].Labels` (both keys required when present, unknown child keys / scalar / non-integer
    / invariant failures all name `Radar:Strategies:{i}:Labels`; `"Labels"` joined `StrategyEntryKeys`).
    **NOT a fingerprint input** — `ScoringConfigFingerprint`, `FormulaIdentity`, `StrategyIdentityGuard`
    and the strategy-config files are untouched (AD-10 as amended: the fingerprint stamps what changes a
    SCORE); `ScoringConfigFingerprintTests` passed unchanged.
  - **A Lead REQUIRES explicit lines, at runtime.** `OperatingCallReducer.Validate` fails a declared Lead
    whose `Labels` is null (beside every other rule about a valid Lead, so the Worker halts at startup
    before collection), and `Reduce` re-checks the FINAL effective Lead immediately before `WithLead` —
    gate-promoted (`GateDefault`) and override-held Leads are covered by the same rule in the same type.
    The failure names the arm, its provenance (declared / overridden / gate-promoted) and
    `Radar:Strategies:{i}:Labels`. Mirrors `StrategyIdentityGuard`'s stance: a halt with a named remedy is
    correct; a report labelled on lines nobody chose is not.
  - **Three states, pinned** (builder tests with pre-212 byte captures): no operating-calls file ⇒ the
    storage primary's `Labels ?? Default`, banner "defaults (no operating calls declared)", differing from
    the pre-212 bytes by the one banner line; effective Lead ⇒ the Lead's explicit lines reach the policy
    (`ReportActionContext.Thresholds`, trailing/defaulted; `null` ⇒ `Default`, byte-identical to v4);
    **StopAll (declared, or the zero-Lead fallback) ⇒ no narrative, no labels, NO banner, byte-identical
    to pre-212.** The banner is rendered ONCE from `WeeklyReportModel.Labels` (`ReportLabelLines`: arm,
    lines, explicit/defaulted, Lead/primary-by-default, policy version), never from a constant, directly
    under the legend lines. Policy `weekly-report-action-v4 → v5` (the CONTRACT changed: the lines are
    inputs); rationales interpolate the applied line (`Opportunity 16 (>= 15)`); the corroboration floor
    floors against the arm's Watch line and never above it; `NeutralTrajectory` / `EvidenceConfidenceFloor`
    / `ThesisDelta` / `MinCorroboratingSignalTypes` stay constants (computed on the same scale by the shared
    `ScoreSignalMath` for every formula — only Opportunity changed scale). Nothing hashes the policy version.
  - **Live profile values, PINNED by the spec (`scripts/run-profiles/default.json` is the owner):**
    `disclosure-led-v11` `Labels { Investigate 20, Watch 15 }`; NO other arm sets `Labels` (`default` is
    its own 60/40 by definition; comparators cannot lead — dead config; the v9 arms and `default-noattn`
    are "no lines set — a Lead call must add them"). **Principle: prevalence-match once, then FIX.** A
    line's operational meaning is the share of snapshots it puts in front of a reader; Watch 15 was chosen
    to reproduce the v8 primary's ≈ 2% ≥ 40 prevalence on v11's accrued distribution, Investigate 20 is the
    top ≈ 0.3% (max 21) — a stated workload JUDGEMENT, since v8 has no ≥ 60 prevalence to match. These are
    fixed operating (triage) thresholds connected to no outcome and are labelled as such everywhere they
    render; they say nothing about whether a value is a strong opportunity, and a universal cross-arm
    score is a stated non-goal.
    Implementation-time re-measurement (`scripts/audit-label-thresholds.ps1 -Strategy disclosure-led-v11
    -MatchPrevalenceOf default`, 2026-09-07): over the spec's window (`-FromDate 2026-07-29`) `default`'s
    ≥ 40 share is 2.1% (75/3,545) and the matched v11 value is **15** exactly (k = 73rd largest); over the
    whole store (3,760 `default` snapshots, 2.0%) it is **16** (k = 69th; share ≥ 16: 2.1%, 72 snapshots) —
    both within the spec's ±1 tolerance, so 15 stood; at 15 the v11 share is 3.8% (131 snapshots). On the
    2026-09-06 report those lines would have labelled AGX `Investigate` (20) and ESQ/JOUT/DGII `Watch` by
    score (17/16/15) — noted, not a justification.
  - **Spec 212 moved nothing.** No score, weight, formula, channel, snapshot field, stored JSON, accrued
    file or fingerprint pin; the formula constants (`3`, `10`) are untouched — retuning them is a
    composition change (v12 / `CompositionRevision`) and a different, later decision. **MEASURED (first
    post-merge full run, 2026-09-07 21:30Z, report `radar-weekly-2026-09-07.md`, Lead lines 20/15):**
    `Investigate` **1** (AGX, Opportunity 20 — the first score-based label on any research arm since
    2026-08-14 and the first `Investigate` since the single-strategy era); `Watch` 18 = **3 by score**
    (ESQ 17, JOUT 15, DGII 15) + **15 by floor**; the banner rendered once with the arm,
    lines, "explicit", v5 and the "operating thresholds, not validated evidence" sentence. Before
    (2026-09-06 report): 0 / 0 / 18 by floor. Descriptive, no gate.
- **The judge stops reading a level as a trend — a deterministic comparison-basis on every supplied fact,
  prompt rule 11, and a fail-closed materializer ALLOWLIST (spec 214, 2026-09-08).** On the 2026-09-07 run
  the stage-2 judge read Argan (AGX) as `Improving` on "Backlog reached $2.5B, indicating strong future
  demand" — a LEVEL; the backlog was $2.93B on 2026-01-31 and had fallen 14% over the year, and nothing
  Radar supplied could have said so (the typing store's only AGX backlog fact IS that statement). Rule 3
  covers the absence of facts; nothing covered the presence of a number that carries no direction.
  - **`StatementComparisonClassifier` (`comparison-basis-v1`, `Radar.Application.NewsRisk.Judgment`)** —
    pure, static, closed-table, PRECEDENCE-ordered over `(Statement, EventTypes)`, applied at judge-INPUT
    time in `NewsJudgmentInputBuilder` (never at typing time — the stage-1 cohort is untouched, so no
    re-typing). (1) `StatedComparison`: a comparison phrase (`record`, `up from`, `versus`/`vs`, `grew`,
    `fell`, `declined`, `year-over-year`, `beat`, `missed`, …), `up`/`down` immediately followed by a
    figure, or `from X to Y`; no number required; a bare `%` NEVER qualifies. (2) `LevelOnly`: a figure
    within 6 tokens of a stock/flow metric noun (backlog, cash, debt, headcount, revenue, margin, EPS, …),
    or any figure on an `EarningsOrGuidance` statement — and a level OUTRANKS an event term ("wins a $50M
    contract; backlog now $2.5B" is a level). (3) `Event`: an event term (order, award, contract, won,
    approved, cleared, launched, acquired, financing, listing, recall, lawsuit, filed, settled, …) AND at
    least one of the five event-bearing types — **both required; the type is the tie-breaker** ("the
    company filed its quarterly report" under `EarningsOrGuidance` is not an event). (4) `NotQuantified`.
    Whole words/phrases only, case-insensitive, bounded by a character that is neither a word character
    nor a hyphen (a hyphenated compound is ONE token — `flat` never hits "flat-panel", `record` never
    "record-breaking"; the hyphenated comparisons `record-breaking`/`record-setting`/`above-average`/
    `below-average`/`year-over-year`/`all-time` are listed explicitly): `vs` never hits "investors",
    `record` never "recorded", `cut` never "cutting-edge", `rose` never "Rosetta" — pinned. Two entries
    are figure/noun-SCOPED rather than bare words, decided from the review-round-1 live sample: `growth`
    counts only beside a figure ("growth of 12%", "12% growth"; "growth strategy outlined" does not) and
    `lift(s)/lifted` only immediately before a metric noun ("lifts revenue and profit" — the Middlesex
    Water statement the spec Overview names as genuinely directional; "Power projects lift Argan" does
    not, which is what keeps the AGX backlog statement a level). `crushes`/`crushed` joined the beat/miss
    family ("Crushes Q4 Profit Estimates by 31.6%"). The figure grammar takes thousands GROUPS only, never
    a trailing comma ("In 2026, …" is a year, not a figure — a review-round-1 defect). Enum values start at
    1 so a defaulted zero is undefined. The tables + window + boundary rule ARE the identity: a change is
    `comparison-basis-v2`.
  - **Judge input and contract.** `NewsJudgmentInputFamily.ComparisonBasis` (required); the user message
    renders one `ComparisonBasis: …` line per family (`LevelOnly — a stated level, not a trend`);
    `NewsJudgmentContract.PromptVersion` → `news-judgment-prompt-v4` (rule 11: a level establishes no
    direction; only a StatedComparison or Event fact may be cited in `TrajectoryFactIds`; a LevelOnly fact
    is finding context only; Unknown ONLY when no supplied fact is StatedComparison or Event; name the
    set-aside levels in the rationale); `SchemaVersion` stays `news-judgment-schema-v3`; the cohort key
    gains `|comparison=comparison-basis-v1` after `families=` (an input the model sees). The family-set
    hash is deliberately NOT changed (the basis is a pure function of two already-hashed fields).
  - **Record `news-judgment-v4 → v5`** (`NewsJudgmentRecord.CurrentSchemaVersion`): trailing nullable
    `TrajectoryBasis` (`NewsTrajectoryBasis { Supported = 1, LevelOnly = 2 }`; spec 215 adds
    `ReferenceSupported`) and per-family `NewsJudgmentFamilyRef.ComparisonBasis` (nullable = pre-214,
    never defaulted). `null` TrajectoryBasis means NOT APPLICABLE — Mixed/Unknown/any failure — OR
    pre-214; the two are distinguishable by the materializer's gate ORDER (status and direction before
    basis). `NewsJudgmentValidator.TrajectoryBasisFor` computes it ONLY for a Judged Improving/Deteriorating
    result over the RESOLVED cited facts: `Supported` iff ≥ 1 cited family is StatedComparison or Event,
    else `LevelOnly` — NOT a validation failure (the call is persisted verbatim and marked). The cache-reuse
    path carries the cached verdict's own basis. `NewsJudgmentGenerator` logs ONE line per cohort:
    "{LevelOnly} of {Directional} directional judgment(s) called this pass rest ONLY on level/unquantified
    facts" (never per item).
  - **Materializer `news-judgment-signal-v2 → v3` — an ALLOWLIST, not a denylist.**
    `NewsJudgmentSignalMaterializer.AllowlistedTrajectoryBases = { Supported }` (spec 215 widened it to
    `{ Supported, ReferenceSupported }` under the same `news-judgment-signal-v3`); after the status,
    direction and cited-facts gates: `null` → `TrajectoryBasisNotRecorded` (a pre-214 directional record —
    counted, never assumed Supported), `LevelOnly` → `LevelOnlyTrajectory`, defined-but-unallowlisted →
    `TrajectoryBasisNotAllowlisted`; an unknown token ON DISK never reaches the materializer (the strict
    enum converter fails the record as unreadable, counted on the judgment store's unreadable axis —
    pinned by test). All three are per-record gate reasons in the summary identity, render through
    `DescribeSkips` in the daily news report's accounting line and the live artifact, and
    `NewsDirectionalSignalMetadata.SupportedJudgmentSignalVersions = { v1, v2, v3 }` (v2 declared as
    `JudgmentSignalVersionV2`). There is deliberately NO v2 occupancy lookup: a v2 signal exists only for a
    pre-214 judgment id, which the null-basis gate stops first; `RetiredV2SignalIdFor` exists for
    measurement only. The prompt fork gives every re-judged company a NEW judgment id, so v3 signals may
    coexist with accrued v2 ones and the latest-judgment supersede resolves same-evidence pairs — the
    first post-214 run REPORTS per company: v2 signals in window, v3 minted, same-evidence pairs
    superseded, different-evidence coexistences (owed in the PR body's follow-up; descriptive, no gate).
    Nothing accrued is rewritten (AD-8).
  - **Report.** `NewsJudgmentLeaderMarker.TrajectoryBasis` (a string token, set by `NewsJudgmentMarkerPolicy`
    only for a judged DIRECTIONAL marker: `Supported` / `LevelOnly` / `(pre-214)`; Mixed/Unknown carry
    none) renders on the weekly report's judgment provenance appendix as ` · basis: …` — never in the
    leaders cell. `docs/reading-radar-output.md` item 11: "A level is not a trend."
  - **MEASURED (2026-09-08, read-only over the live store via `ComparisonBasisLiveMeasurementTests`,
    env `RADAR_COMPARISON_BASIS_LIVE_DATA_ROOT`; the shipped tables, after two inspection passes):**
    5,263 typed facts (5,150 typing records) — StatedComparison **895 (17.0%)**, LevelOnly **105 (2.0%)**,
    Event **335 (6.4%)**, NotQuantified **3,928 (74.6%)**; of the 1,335 quantified facts, LevelOnly is
    7.9% (bound > 30%: holds) and StatedComparison 67.0% (bound < 5%: holds). The harness prints the
    first 12 statements of each class, and the two inspection passes each fixed defects ON MERIT, never to
    a bound: pass 1 — a day-of-month figure ("to report results on August 6") read as a level on an
    earnings statement; `slumps`/`swings to`/`reduced` missing ("Revenue Slumps 57%" was a level);
    `divestiture`/`sale of` missing from the event table; pass 2 (review round 1) — a year followed by a
    comma escaped the year exclusion; bare `growth` over-included ("Growth strategy outlined"); a hyphen
    as a boundary let `flat`/`above` hit inside compounds ("Flat-panel display maker"); and `lift(s)`
    before a metric noun / `crushes` were missing; review round 2 then added the hyphenated
    `record-high`/`record-low` and the six `-than-expected` forms that the hyphen rule had made false
    negatives (live impact: one statement, GEOS "Wider-than-Expected Loss", NotQuantified →
    StatedComparison; no judgment moved) and let `lift(s)` take up to two qualifiers ("lifts its
    full-year outlook"). Net of all passes against the first draft: StatedComparison −127 (the
    `growth`/hyphen over-inclusion outweighed the additions), LevelOnly −28, Event +6, NotQuantified +149. 303 judgment records, 269 Judged, **182 directional**: would-be
    Supported **163 (89.6%)**, would-be LevelOnly **10 (5.5%)** (bound above the crude 14.8%: holds; the
    crude regex over-counted because it flagged event facts), no TrajectoryFactIds (pre-187 v1) 9,
    unresolved citation 0. Of the 10 would-be LevelOnly judgments (WDFC ×3, SHEN, LBRT ×4, HWKN ×2
    Deteriorating), **5 hold a materialized `news-judgment-signal-v2` signal** (0 hold v1) — those five
    are the directions v3 would have refused. The scoping moved judgments BOTH ways, which is the point:
    both MSEX judgments left the set once "lifts revenue and profit" counted, and one LBRT judgment
    (`c18447d3`, 2026-09-01) entered it once bare `growth` stopped counting. **Seen and left for
    `comparison-basis-v2`:** `Top 5 Analyst Questions` and `17% Undervalued` on `EarningsOrGuidance`
    statements classify LevelOnly under the spec's bare-figure-on-earnings rule (harmless to the gate —
    LevelOnly and NotQuantified are treated identically by validator and materializer, only the rendered
    line differs); institutional-holding statements typed `MergerAcquisitionOrStake` by stage 1 ("30,456
    Shares … Acquired by …") classify Event under the tie-breaker — a stage-1 typing matter, and prompt
    rule 5 already tells the judge holdings are context. **AGX `928eb9f8-…` (Improving, v4, basis null):** its
    four cited facts classify as StatedComparison ("Reports Record $384 Million Revenue…"),
    StatedComparison ("Q2 Revenue $384.0M, vs. FactSet Est of $300.5M"), **LevelOnly ("Power projects
    lift Argan … as backlog hits $2.5B")**, StatedComparison ("Beats Expectations By $1.12 EPS") — so
    under v1 the judgment's would-be basis is **Supported** (three record/beat facts carry it; the backlog
    level is the one fact the rule sets aside), and it holds a v2 signal. The classifier does what it
    claims — it marks the backlog fact as a level the judge may not cite — and the AGX call itself was
    not level-ONLY; the re-judgment under prompt v4 is recorded in the PR body, not predicted. Descriptive;
    no table was tuned to any bound.
  - **Identity.** The AI-ON pins moved (30d unit, 60d live, 120d long-window, and the three
    no-newsquery additivity halves); the AI-OFF pins did NOT (asserted). Values are CITED, never
    transcribed: `ScoringConfigFingerprintTests.Compute_AiOnDefault_MatchesPinnedFingerprint`,
    `Compute_LiveWindowAiOnStamps_ArePinned`, `Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins`
    (AI-ON halves) — history: 60d live AI-ON `radar-scoring-fp-11240da5aeb0` was the value stamped
    2026-08-29 through 2026-09-07. `ChatNewsJudgmentAnalyzerTests` re-pinned the instruction hash. The
    operator step (delete/re-record `data/scoring-configs/strategies/{name}.json`, verify the first run's
    stamp against the test) was taken ONCE for specs 214 and 215, which merged back-to-back (⚠ AMENDED BY SPEC 216: that step WAS performed, on 2026-09-08, and the post-214/215 composition then stamped `radar-scoring-fp-8590412af27c` across 102 companies — strategy `default`, earliest score `2026-09-08T21:46:22.311Z`. Spec 214's own 60d value was never stamped by a live run, but 215's was. Spec 216 moved the AI-ON pins AGAIN and therefore owes a SECOND, separate operator step — not a shared one); every candidate company is
    re-judged once (~19 calls). No formula, weight, rule-set, collapse, supersede, neutralization,
    attention-tier or news-query change; spec 214 moved no AI-OFF value.
- **Spec 215 — company-reported reference values: the filing read keeps what it reads, and the judge is
  handed the company's own prior figure for any metric a news fact quotes (2026-09-08).** Spec 214 stopped
  the judge treating a level as a trend; it could not make the judge RIGHT about the trend, because Radar
  held no prior value to compare against — the EX-99.1 body `ChatFilingAnalyzer` read live was never
  persisted, and the three backlog figures the skeptic compared were numbers Argan itself had reported in
  filings Radar READ and then forgot. No earlier bullet claimed the body was "read and discarded", so
  nothing here needed reversal; the seam line in `docs/radar-full-pipeline-spec.md` was amended in place.
  - **The filing read returns the metrics the release STATES (`Radar.Application.Filings`).**
    `IFilingAnalyzer.AnalyzeAsync` now returns `FilingRead(Sentiment, VerifiedReportedMetrics?)` — the
    `FilingSentiment` half is byte-unchanged (the pre-215 directional instruction is pinned as
    `ChatFilingAnalyzer.SentimentInstruction`, and the seam test asserts it is the exact spec-164 text);
    `ReportedMetrics` is `null` when NO extraction was examined (extraction disabled via
    `FilingAnalyzerOptions.ExtractReportedMetrics` — the reported-metrics paragraph is omitted and nothing
    is examined or returned; the structured-output schema derived from the DTO still advertises the list —
    no model call, an untrusted response) and non-null — a verified list plus FOUR measured counts
    (`DroppedUnrecognised` / `DroppedUnverified` / `DroppedDuplicate` / `PriorPairsDroppedIncomplete`) —
    when the response was examined. The typed model response is a NEW all-strings
    wire DTO (`FilingReadModelResponse` + `ReportedMetricWire`, the spec-179 rule); `direction` is a string
    parsed by the digit-rejecting shared token parser and an out-of-vocabulary token degrades the WHOLE
    read to Unknown with nothing examined (the pre-215 behaviour, where enum deserialization rejected the
    payload); `confidence` stays a JSON number because that is the shape every accrued live response has
    carried and the schema the typed extension emits derives from the DTO. The closed `ReportedMetric`
    enum (Revenue, NetIncome, DilutedEps, GrossMargin, OperatingIncome, Backlog, CashAndInvestments,
    TotalDebt, FreeCashFlow — guidance deliberately absent; values start at 1) is spelled as a
    compile-time constant in `ReportedMetricsInstruction` so `FilingAnalyzerPrompt.DefaultSystemInstruction`
    stays a `const` alias, and a test pins the constant to the enum.
    `AnalyzedFilingRecord.CurrentCacheVersion` was NOT bumped: the record gained trailing nullable
    `ReportedMetricsPolicy` (null = written pre-215 or extraction disabled = HIT, the spec-160 `cmpscan`
    null-policy precedent; a non-null value differing from `ReportedMetricsPolicy.Version` is a bounded
    MISS in `DirectionalFilingSignalSource` pass 1), and the stamp is written ONLY when an extraction was
    made, so a null on disk always means "not extracted", never "extracted nothing".
  - **Verification is code, not the model's word (`ReportedMetricVerifier`, Infrastructure, pure).** It
    lives beside `EarningsComparabilityScan` (the spec-160 shape) rather than in Application, because it
    reuses the shared `FeedTargetRelevance.NormalizeWhitespace` collapser (Infrastructure) and runs over
    the TRUNCATED body the analyzer actually sent (`FilingAnalyzerPrompt.Truncate`, same cap) — a pure
    Application verifier would have needed a pasted second copy of the collapser. Rules, in order: a null
    entry or a blank metric/value/period/quote is `DroppedUnverified`; a non-blank metric outside the
    closed set is `DroppedUnrecognised`; the value, the prior value and prior period (each when present —
    the prior period joined the check in the PR #222 Copilot fix pass) and the unit (when
    non-blank) must appear verbatim (ordinal, whitespace-collapsed) inside the quote and the quote inside
    the truncated body, else `DroppedUnverified` (so a quote past the `MaxInputLength` cap is a COUNTED
    gap, never a silent zero); a repeated (metric, period) pair is `DroppedDuplicate` (the third count is
    additive to the spec's two — one ledger record per pair, the repeat counted rather than collapsed). A
    prior VALUE is kept only when its prior PERIOD was stated too (half a comparison cannot be placed in
    time); an incomplete pair — a verified prior value without a period, or a period without a value — is
    nulled on the record (the current value itself verified and is kept) and COUNTED as
    `PriorPairsDroppedIncomplete`, the fourth measure, so a model that habitually returns half a comparison
    shows up in the aggregated line rather than as silently prior-less records. Persisted strings are the trimmed, whitespace-collapsed
    forms the check compared, so a record can never disagree with the scan that admitted it. No
    arithmetic, no unit conversion, no number parsing anywhere.
  - **The ledger (`IReportedMetricStore` → `FileReportedMetricStore`).
    ⚠ SPEC 216 AMENDED THIS BULLET IN PLACE — path, write mechanism, identity and ORDERING all moved.**
    The path is `{ReportedMetricsDirectory}/{companyId:D}/{sanitizedAccession}.{policy}.json` (§5: the
    POLICY is an explicit `WriteIfNewAsync` argument and part of the file name, so a re-analysis under a
    later policy writes a NEW file beside the old one instead of colliding with it forever — the v1 layout's
    documented heal-forward path was a promise the code could not keep). One file per (company, accession,
    policy) holding that release's `ReportedMetricRecord` LIST (id, company, accession, evidence id,
    filing date = `PublishedAtUtc ?? CollectedAtUtc`, form, metric, value/unit/period as stated, prior
    pair, quote, reader identity, `Verification = Verbatim`, `Policy` = the CURRENT
    `ReportedMetricsPolicy.Version`); ids are content-derived over
    `radar:reported-metric:{policy}:{accession}:{metric}:{period}`, so a re-read is a durable no-op and a
    re-analysis under a later policy is a distinct record. The write goes through the shared
    `AtomicFileWriter` (temp file + no-overwrite rename; §4) — the rename is the commit point, so a partial
    file can never become the record — and an EXISTING file is `AlreadyAvailable` ONLY when it reads back as
    a complete record list; an unparseable one is `Failed` with reason `corrupt-existing`, logged once per
    path, never `AlreadyOnDisk` (v1 reported any `IOException` over an existing file as a concurrent
    writer's success, so a half-written file it had created itself became the record). A disk failure is a
    typed `Failed`, never a throw; the read returns `FilingDateUtc` desc, metric, period, id, ACROSS every
    policy's files (filtering to the current one is the projector's job). An EMPTY list still claims the
    file (a release that verified nothing is a recorded fact). Written by `CollectionPass`, in its existing
    directional loop, AFTER `MapResolveReviewStoreAsync` resolved the signal — the record is filed under
    the RESOLVED company id (`SignalStoreResult` now carries it, and since spec 216 the mention it resolved
    FROM; resolution stays in one place), through an optional `IReportedMetricStore?` (null ⇒
    byte-identical) — but only ever FROM AN OUTBOX ENVELOPE (see the spec-216 bullet). The extraction rides
    `DirectionalFilingSignal.ReportedMetrics` (`ReportedMetricExtraction`: accession, form, reader,
    metrics) and is null on a cache replay — "not extracted this pass", never an empty list. ONE aggregated
    Information line per run: fresh reads that extracted / files written / already on disk / not persisted
    / no resolved company / no ledger registered / records written / dropped unverified / unrecognised /
    duplicate / prior pairs dropped incomplete. The counts were NOT added to `CollectionPassResult`/`PipelineRunRecord` (the log line is the
    surface; a record field is owed only if a consumer needs it). Worker: `Radar:ReportedMetricsDirectory`
    (default `data/reported-metrics`, overridden by `run-radar.ps1`) and `Radar:Ai:ReportedMetrics:Enabled`
    (default true, declared in `default.json` with a one-line reason), registered inside the same AI gate as
    the analyzed-filing cache; the analyzer's `ExtractReportedMetrics` follows the same flag so a disabled
    ledger never leaves an extraction with nowhere to go.
  - **The judge is handed reference values (`ReferenceValueProjector`, `reference-projection-v1`,
    Application, pure).** ⚠ SPEC 216 §1 MOVED IT TO `reference-projection-v2` AND ADDED THE ELIGIBILITY
    RULES THIS BULLET LACKED: the newest accession per (company, metric) is the CURRENT value and is NEVER
    a reference (v1 had no temporal or structural check at all, and the collection pass writes the newest
    release's metrics BEFORE the judge runs in the same pass, so a fact quoting "backlog $2.5B" was handed
    the same $2.5B from the same release as its "reference"); a VERIFIED prior pair on that newest record
    projects separately as kind `StatedPrior` with its own ReferenceId; a reference filed LATER than the
    observation instant of every supplied family naming its metric is excluded and counted; only the
    CURRENT policy's ledger files are read, superseded ones counted. Prompt → `news-judgment-prompt-v6`
    and each rendered line carries `prior` / `stated-prior` after the metric. A closed table maps each metric to the statement phrases that NAME it (Backlog:
    backlog, order book; Revenue: revenue(s), sales; NetIncome: net income, net loss, net profit, profit,
    earnings; DilutedEps: eps, earnings per share; GrossMargin: gross margin, margin(s); OperatingIncome:
    operating income/profit; CashAndInvestments: cash, cash and (cash) equivalents, cash and investments;
    TotalDebt: debt, net debt; FreeCashFlow: free cash flow, cash flow), matched under the SAME whole-word
    boundary rule as `StatementComparisonClassifier` (its `WholeWordAlternation` became internal and is
    reused, not copied). `NewsJudgmentInputBuilder.Build` takes the company's ledger, projects every record
    whose metric is named in ANY supplied statement, most recent first, at most 4 per metric and 16 per
    judgment, the remainder COUNTED as `ReferenceValuesOmitted`, and carries the ordered
    `NewsJudgmentReferenceValue` list (ReferenceId = the ledger record id) on the bundle and the request.
    **The family-set hash DOES fold the projected reference ids in — and only when there are any**: unlike
    `ComparisonBasis` (a pure function of already-hashed fields, deliberately not folded by spec 214), a
    reference value is EXTERNAL input the model sees, so the same families beside a grown ledger must be a
    new cache entry (a re-judgment), while every accrued hash and every reference-free judgment stays
    byte-identical (asserted). The user message renders, AFTER the families and only when non-empty,
    `Company-reported reference values (from SEC filings Radar read; cite by ReferenceId):` then one
    `ReferenceId: {id} · {Metric} · {value} {unit} · {period} · stated in {form} filed {yyyy-MM-dd} · "{quote}"`
    line per value (the prior pair appended as `(prior …)` when stated). Prompt rule (12) verbatim from the
    spec: a reference value is a comparison basis, not news — read the direction from the comparison, cite
    BOTH the fact and the ReferenceId, never cite a ReferenceId as a trajectory fact on its own.
    `NewsJudgmentContract.PromptVersion` → `news-judgment-prompt-v5` (⚠ spec 216 §1 → `v6`),
    `SchemaVersion` → `news-judgment-schema-v4` (`TrajectoryReferenceIds`, per-finding `ReferenceIds`;
    UNCHANGED by 216 — the response shape did not move), and `CohortKey` appends
    `|references=reference-projection-v1` (⚠ spec 216 §1 → `v2`).
  - **Validation, basis, record.** `NewsJudgmentValidator.Validate` takes the projected references and
    resolves every reference citation through a SECOND `NewsJudgmentCitationResolver` scoped to the
    projected ids (same prefix grammar; a FactId cited as a reference is `not-supplied`, the two sets never
    cross; reference expansions are NOT folded into `FactIdPrefixExpansionCount`, whose spec-197 definition
    is fact citations only). An unsupplied/malformed/duplicate reference fails the trajectory
    (`trajectory-reference-…`) or drops the finding (`finding[i] reference-…`) exactly as FactIds do; a
    reference is never REQUIRED, and by construction a reference alone can never carry a trajectory (the
    fact gate already demands ≥ 1 cited fact). `NewsTrajectoryBasis` gains `ReferenceSupported = 3`, and
    `TrajectoryBasisFor` has a fixed precedence: `Supported` (≥ 1 cited StatedComparison/Event —
    unchanged) → `ReferenceSupported` (≥ 1 cited LevelOnly fact whose statement NAMES the metric of ≥ 1
    cited reference, under the projector's one table) → `LevelOnly`. ⚠ Spec 216 §1 renamed the
    not-in-the-projected-set reason to `reference-not-projected` (the projection now EXCLUDES the current
    value and any later filing, so "not projected" is a different fact from "not supplied") and moved the
    record tag to `news-judgment-v7`. `NewsJudgmentRecord` →
    `news-judgment-v6` with trailing nullable `ReferenceIds` (the PROJECTED set, ids only; empty = projected
    none, recorded on every attempt that assembled an input), `ReferenceValuesOmitted` and
    `TrajectoryReferenceIds`; `NewsJudgmentValidatedFinding.ReferenceIds` (null pre-215, empty on a v6
    finding with none); the cache-replay branch carries all three from the cached record.
    `NewsJudgmentGenerator` takes an optional `IReportedMetricStore?`, reads each candidate's ledger ONCE
    per pass (a read failure ⇒ empty + one Warning per company + one aggregated Warning, never a silent
    empty), and its spec-214 cohort line gains the `ReferenceSupported` count beside `LevelOnly`, plus a
    new line "{n} of {N} judgment(s) called this pass were handed at least one company-reported reference
    value". **Materializer: `AllowlistedTrajectoryBases = { Supported, ReferenceSupported }` under the SAME
    `news-judgment-signal-v3` identity** — the allowlist grew, the rule ("mint only an allowlisted basis")
    did not, so no fourth version; `LevelOnly` still mints nothing (asserted). Report: the marker gains a
    string `ReferenceIds` token (comma-joined cited trajectory reference ids, null when none) rendered on
    the judgment provenance appendix as ` · references: …` after `basis: ReferenceSupported`; the evidence
    line gains ` — reported: revenue 384.0 million (Q2 FY27), backlog 2.518 billion (as of 2026-07-31)`
    (`ReportEvidenceRef.ReportedMetrics`, null = no ledger records for that evidence; `WeeklyReportBuilder`
    takes an optional `IReportedMetricStore?`, reads the company ledger once per surfaced entry and joins by
    `EvidenceId`; metric display names come from ONE `ReportedMetricDisplay.NameOf`, no direction word by
    construction). `docs/reading-radar-output.md` item 12.
  - **Identity.** Prompt v5, schema v4 and `reference-projection-v1` enter the `news=` segment (⚠ SPEC 216 SUPERSEDED TWO OF THE THREE — **AFTER they had stamped a live run**: prompt v5 → `news-judgment-prompt-v6` and `reference-projection-v1` → `v2`; schema v4 stands. The 214+215 operator step WAS performed on 2026-09-08, and this composition — `comparison-basis-v1`, prompt v5, schema v4, `reference-projection-v1`, with the reported-metrics ledger DISABLED by `766c925` so ZERO references were projected — stamped `radar-scoring-fp-8590412af27c` across 102 companies, strategy `default`, earliest score `2026-09-08T21:46:22.311Z`. That ONE-RUN COHORT is real accrued history: an analyst grepping that stamp finds it, so do not read 216 as having superseded a value that never reached disk. Spec 216 owes a SECOND operator step of its own. 216 also appends `rm=<policy>` to the DIRECTIONAL-FILING `ai=` descriptor, which is NOT this segment): the six
    AI-ON pins moved (30d unit, 60d live, 120d long-window, and the three no-newsquery additivity halves)
    and the three AI-OFF pins did NOT (asserted). Values are CITED, never transcribed:
    `ScoringConfigFingerprintTests.Compute_AiOnDefault_MatchesPinnedFingerprint`,
    `Compute_LiveWindowAiOnStamps_ArePinned`, `Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins`
    — history: the spec-214 60d value `radar-scoring-fp-241097438af8` was never stamped by a live run
    (214 and 215 merge back-to-back), and `radar-scoring-fp-11240da5aeb0` remains the value stamped
    2026-08-29 through 2026-09-07. `ChatNewsJudgmentAnalyzerTests` re-pinned the instruction hash;
    `FilingAnalyzerPromptSeamTests` re-pinned the filing instruction (the directional half asserted
    byte-identical to the spec-164 pin). ⚠ **REVERSED BY SPEC 216 §5:** this bullet said the
    directional-filing scoring descriptor (`str/nov/minconf/model/cmpscan/cmpcap`) is UNCHANGED because
    "the filing prompt change is not a fingerprint input". It IS one. Enabling metric extraction changes
    the FILING-ANALYSIS PROMPT ITSELF — the model is asked for the release's stated metrics as well as its
    direction — and the VERIFICATION policy decides which values exist at all, which is spec 119's
    reading-model argument applied to the read's other prompt-shaping input. The descriptor therefore gains
    a trailing `rm=disabled|<policy>` field, hashed by value. The operator step (delete/re-record
    `data/scoring-configs/strategies/{name}.json`, verify the first run's stamp against the test) is owed a
    SECOND time, separately: the 214+215 step was ALREADY PERFORMED on 2026-09-08 and its composition
    stamped a live run (102 companies under `radar-scoring-fp-8590412af27c`, ledger off ⇒ zero references
    projected), so 216 needs its own step before the first post-216 baseline. Two steps, not one shared.
  - **MEASURED (2026-09-08, read-only over the live store via `ReferenceValueLiveMeasurementTests`, env
    `RADAR_REFERENCE_VALUE_LIVE_DATA_ROOT`):** the ledger is heal-forward and therefore EMPTY at
    implementation time (0 companies / 0 records); what was measured is the ground it will fill. Analyzed-
    filing cache: **275** records readable under production rules (242 `DirectionalSignalProduced`, 33
    `NoDirectionalSignal`; a further 225 files are stale-version misses — the spec-204 v2 no-signal
    records — and are correctly NOT counted), **275 (100.0 %)** with a null `reportedMetricsPolicy`, 0
    under `reported-metrics-v1`. Forward accrual rate, from the spec-115 debug store grouped by `asOfUtc`
    (LAST attempt per accession, so a re-read filing is counted under the later run only): 2026-09-03 **48**
    reads (15 directional / 33 no-directional-read), 2026-09-02 1, 2026-08-29 **50** (29 / 1 below-
    confidence / 20), 2026-08-27 1, 2026-08-24 1 — i.e. the seed-burst runs read ~50 (the `MaxFilingsPerRun`
    cap) and a steady-state run reads ≈ 1, so the ledger fills at the earnings calendar's pace. Projection
    would-be hit rate on TODAY's facts: **250 of 269 Judged judgments (92.9 %)** have at least one supplied
    family naming a ledger metric; **1,656 of 8,519 supplied families (19.4 %)** name one; 0 judgments had a
    supplied family missing from the typing store. Per metric (families / judgments): Revenue 465 / 202,
    NetIncome **1,173 / 242** (the `earnings` and `profit` phrases carry it — "Announces Earnings Results"
    names NetIncome under v1; a narrower table would be a further projection version — ⚠ `v2` was TAKEN by
    spec 216 §1's eligibility rules, so a table narrowing now earns `reference-projection-v3`; recorded
    here, not tuned),
    DilutedEps 197 / 103, GrossMargin 116 / 79, Backlog 37 / 32, CashAndInvestments 75 / 52, FreeCashFlow
    30 / 21, OperatingIncome 0 / 0, TotalDebt 0 / 0. **AGX `928eb9f8-…` (Judged, Improving, v4, basis null,
    referenceIds null):** its four cited facts name Revenue + NetIncome (StatedComparison), Revenue
    (StatedComparison), **Backlog (LevelOnly — the one that could become `ReferenceSupported`)** and
    NetIncome + DilutedEps (StatedComparison), so once ONE Argan release has been read all four would
    receive a reference block and the backlog level would be the fact whose direction the reference
    decides. The 50 % verification sanity bound of spec 215 §3 ("a ledger that verifies < 50 % of what the
    model returns is a prompt/verification defect") is UNMEASURED until the first post-merge run and is
    owed in that PR-body follow-up, together with ledger records written, metrics dropped unverified /
    unrecognised / duplicate, judgments handed ≥ 1 reference value and the `ReferenceSupported` count.
    Descriptive; no table was tuned to any number.
- **Spec 213 — `default.json`'s `_comment` becomes standing facts that cite their owners; its per-spec history
  moves here verbatim; a guard stops it regrowing; the report cap fails loudly (2026-09-08).** Three comments
  moved: the top-level `_comment` (~75.6k characters carrying 125 `radar-scoring-fp-` literals, per-spec operator
  procedures and "verify the first … run reports" imperatives for boundaries long since crossed — a second
  architecture-history file whose shape was "state a fact as current, then append a superseding paragraph at
  the end", the exact REVERSAL failure CLAUDE.md names), `Radar:News:_comment` (two pin literals) and
  `Radar:NewsResearch:_comment2` (over the length limit). All three are reproduced byte-for-byte in the
  "default.json _comment history" section at the end of this file, in FILE order (not chronological — the
  order is evidence of how the drift happened), and each was rewritten as standing facts that cite an owner
  (`ScoringConfigFingerprintTests`, `ScoreFormulaVersions.All`, `KeywordSignalExtractor.RuleSetVersion`, the
  profile's own `Strategies` array, `data/companies.json`, `data/strategy-operating-calls.json`) and quote no
  pin; `Radar:NewsResearch:_comment`, `Radar:Efficacy:Comparison:_comment` and `Radar:Ai:ReportedMetrics:_comment`
  already passed and are byte-identical, as are the four overlay profiles. The guard:
  `RunProfileCommentGuardTests` walks every `scripts/run-profiles/*.json` (asserting the directory walk-up found
  at least five, so a broken path cannot pass vacuously) and every `_comment*` string anywhere in the JSON tree,
  failing on a `radar-scoring-fp-[0-9a-f]{12}` match, a case-insensitive `verify the first .* run reports`
  match, or a .NET string length above 4,000 (`default.json`) / 6,000 (an overlay), naming the profile, the
  key's JSON path, the spec and where history goes — with positive controls proving each predicate bites. The
  cap rule (§4): `WorkerRunOptions` now carries `ReportMaxItems` (copied from `RadarWorkerOptions` by the
  composition root) and `Worker.ExecuteAsync` throws `InvalidOperationException` immediately after seeding when
  `Radar:ReportMaxItems < seeded` — every run mode, filtered or not; the seeded count is whatever this run
  seeded — naming the key and both numbers in the `StrategyIdentityGuard` register (a misconfiguration costs no
  collection). The cap is deliberately NOT derived from the universe (a cap that tracks the universe is no cap),
  so a universe expansion must raise it in the same change; CLAUDE.md's universe bullet carries that clause and
  the "showing top N of M" report line stays the counted truth. **Spec 213 moved nothing**: no strategy,
  weight, formula, fingerprint, pin or configured value changed; `ReportMaxItems` stays 120 from `fb1335d`.

- **Spec 216 — a reference value must be PRIOR, DURABLE and VERIFIED before it can support a judgment: the
  five post-merge findings on spec 215 (2026-09-09).** All five were confirmed against the code; the ledger
  had been switched off (`766c925`) so nothing was polluted (`data/reported-metrics/` held ZERO files, and
  the 500-record analyzed-filing cache held ZERO `reportedMetricsPolicy` stamps of any kind — both measured
  2026-09-08). Re-enabled here. The 215 COMPOSITION did nonetheless reach live: its operator step was
  taken 2026-09-08 and one full run accrued under it with the ledger OFF, so that cohort projected ZERO
  references — named, with its stamp, in the §5 identity bullet below.
  - **§1 — a reference is PRIOR to the fact it supports, STRUCTURALLY (`reference-projection-v1 → v2`).**
    v1 selected every ledger row whose metric a supplied statement named, with NO temporal check, while
    `CollectionPass` writes the newest release's metrics BEFORE the judge runs in the same pass — so a news
    fact "backlog $2.5B" was handed the same $2.5B from the same release as its reference and could grade
    `ReferenceSupported` on its own value. A TIMESTAMP rule does not fix it (a release routinely precedes
    its own coverage by a day) and a same-accession exclusion is impossible (stage-1 facts carry observation
    ids, never the SEC accession). The rule is on the LEDGER'S OWN ORDER: per (company, metric), records are
    ordered by filing date, then ACCESSION (ordinal — chronological within a filer's sequence), then id, and
    every record of the LATEST accession is the CURRENT value and is EXCLUDED
    (`referencesExcludedNewest`). A metric reported ONCE is therefore never a reference — the honest state
    of a young ledger. Separately, a VERIFIED prior pair on the newest record projects as kind
    `StatedPrior` with its own id (`ReportedMetricRecord.StatedPriorIdentity`, derived from the record's
    own unique `Id` so it is INJECTIVE — two rows of one release stating the same metric for two periods
    against the SAME prior period are two references, not two figures collapsed onto one id that the
    id-keyed lookups then throw on), carrying the PRIOR figure as its value; a half-stated pair projects
    nothing. A secondary guard on top: a reference filed LATER than
    the observation instant of every supplied family naming its metric is excluded and counted
    (`referencesExcludedLaterThanFact`); a family whose instant is NOT RECORDED excludes nothing.
    `FactFamilyRecord.EarliestObservedAtUtc` is threaded into `NewsJudgmentInputFamily.ObservedAtUtc`
    (deliberately NOT folded into the family-set hash — the judge never sees it, and everything it can
    change is already hashed through the projected reference ids) and persisted per consumed family.
    Validation: an unprojected TRAJECTORY reference FAILS the whole judgment with the named reason
    `reference-not-projected`; on a FINDING it drops that finding only. Prompt →
    `news-judgment-prompt-v6` (rule 12 restated: a reference is the company's EARLIER statement and is never
    the figure a supplied fact itself quotes; each is labelled `prior` or `stated-prior`, and the rendered
    line carries the label).
  - **§2 — a REAL outbox: `IReportedMetricOutbox` (Application) / `FileReportedMetricOutbox`
    (`{ledger root}/outbox/{pending|unresolved|acknowledged}/`).** v1's cache record was stamped
    `reportedMetricsPolicy` at ANALYSIS time while the ledger write happened later in `CollectionPass`, so
    a `NotPersisted` write — or a company that could not be resolved — left the cache saying "extraction
    done" and the metric was lost permanently (counted, but lost). Re-analysing is not the fix: it re-fetches
    from SEC and re-runs the model, which can return a DIFFERENT direction. The envelope is the COMPLETE
    ready-to-write payload (content-derived `OutboxId` over policy+accession, policy, nullable company,
    the MENTION + hints resolution was attempted from, evidence id, accession, form, filing date, reader
    identity, the verified readings, every drop count, `Attempts`, `CreatedAtUtc`, `LastAttemptAtUtc`), so
    a replay is one `WriteIfNewAsync` — no fetch, no model call (asserted by a pass whose directional source
    is not registered at all and which collects no evidence, so a re-read is not merely unused but
    unreachable). THE ORDERING: analysis → company resolved → **envelope durable** → only then
    the cache record stamped → ledger write → acknowledge. THE STAMP IS VERIFIED, NOT ASSUMED (review
    round 4): `IAnalyzedFilingCache.PutAsync` returns a bare `Task` and `FileAnalyzedFilingCache` discards
    `GracefulFileWriter`'s `bool`, so counting a stamp on the strength of the call returning would report
    `cache stamps written 1 / not written 0` over a disk that stamped nothing — the discarded-`bool` defect
    specs 192/193 closed elsewhere. The pass RE-READS the record (the cache has no in-memory layer) and
    counts `written` only when the policy is actually present; otherwise `not written`. Cost: one extra
    cache read per fresh extraction, bounded by `MaxFilingsPerRun`. Same class, same round: a failed
    `AcknowledgeAsync` is counted on its own `not acknowledged` axis beside the pending count at ALL THREE
    acknowledge sites (the unresolved copy's included — it was the one that was counted nowhere); the two
    axes are NOT disjoint, and the RENDERED LINE discloses that — `pending N (of which not acknowledged
    M)` — so a log reader can never read them as N+M envelopes (a containment stated only in a code
    comment reaches nobody who reads the log). And the
    no-outbox branch of the policy-stamp check counts `policy stamp … not checked` instead of returning
    silently, even though the shipped composition cannot reach it. Die before the envelope and the record is
    unstamped and the filing re-analyzes (nothing was persisted to lose); die after and it replays. Pending
    envelopes are enumerated and replayed BEFORE each pass's fresh reads; acknowledged ones are MOVED, never
    deleted. An unresolved company is ROUTABLE, not a loss: the envelope sits under `outbox/unresolved/`
    and is re-run through `ICompanyResolver` on every replay, re-enqueued under the company once resolution
    succeeds. The PERSISTED `Attempts` drives exactly one Warning per accession per run at 3. A cache record
    carrying a policy stamp with NO envelope behind it (the 215-era shape) FAILS CLOSED: counted
    (`policyStampWithoutEnvelope`), named once, never acknowledged as an empty ledger and never
    auto-re-analyzed — recovery is a conscious maintainer action. `DirectionalFilingSignal` gained trailing
    `Accession` + `CachedReportedMetricsPolicy` so the pass can route and audit without re-parsing evidence.
  - **§3 — verification verifies the metric, the period and the WHOLE token (`reported-metrics-v1 → v2`).**
    v1 trusted the metric LABEL (a cash figure labelled Backlog passed), required only a non-blank period
    (an invented period passed) and used `quote.Contains(value)` (so "384" verified inside "384.0") — and
    metric and period drive projection matching, so all three reached the judge as facts. v2 adds: the quote
    must NAME the labelled metric under the closed `ReportedMetricSynonyms` table (longest phrase wins
    across the WHOLE table through the shared `StatementComparisonClassifier.WholeWordAlternation`, with
    EXCLUSION phrases that consume what they contain — `cash flow` ≠ CashAndInvestments, `cost of sales` ≠
    Revenue, `net debt` ≠ TotalDebt, `gross profit` ≠ GrossMargin); the PERIOD verbatim in the quote; value,
    unit and prior value as WHOLE tokens (a numeric edge may not touch a digit or a digit-bearing separator;
    a symbolic edge constrains nothing, so the unit `$` still verifies inside `$2.5`); and ASSOCIATION, not
    co-presence — metric synonym, value+unit and period inside ONE bounded fragment with the value the
    NEAREST verified-shape number to the synonym, so "cash was $100m and backlog was $2.5bn" cannot verify a
    Backlog entry of 100. Fragment boundaries apply in ORDER OF PREFERENCE (newline, `|`, `;`, a run of 2+
    spaces, a SENTENCE-ENDING period = `.` before whitespace-then-uppercase or end-of-text): exactly one
    kind is used per quote — the highest-preference one present — so a table splits on its rows while
    `$0.79` and `384.0` stay in one sentence. Four new counters, each its own axis on the aggregated line:
    `metricNotInQuote` / `periodNotInQuote` / `fragment` / `notAssociated`, plus `Examined`/`DroppedTotal`
    so spec 216 §6's >50% bound has ONE denominator.
  - **§4 — the store writes atomically.** The shared `AtomicFileWriter` (temp file in the same directory,
    flush, no-overwrite rename) now serves the ledger and the outbox; an existing file is
    `AlreadyAvailable` ONLY when it reads back as a complete record list, otherwise `Failed` with reason
    `corrupt-existing`, logged once per path. `GracefulFileWriter`'s other callers are out of scope (spec
    201 owns that seam) — the precedent is noted, not applied.
  - **§5 — identity.** `rm=disabled|<policy>` is appended LAST to the DIRECTIONAL-FILING `ai=` descriptor,
    NOT to `news=`: enabling extraction changes the filing-analysis PROMPT itself even with the judgment
    disabled, and the verification policy decides which values exist. `news=` gains only
    `references=reference-projection-v2` (through the cohort key). The four-case matrix is pinned by
    `ScoringConfigFingerprintTests.Compute_ReportedMetricsIdentityMatrix_IsPinned_AiOnMovesAiOffCannot`:
    judgment OFF + flag toggled ⇒ changes; judgment ON + toggled ⇒ changes; AI read OFF + toggled ⇒
    UNCHANGED; policy v2 → a fake v3 ⇒ changes. The six AI-ON pins moved (30d unit, 60d live, 120d
    long-window and the three no-newsquery halves) and the three AI-OFF pins did NOT — asserted, and for
    TWO independent reasons now (no `ai=` segment to carry `rm=`, no `news=enabled:` segment to carry the
    projection version). Values are CITED, never transcribed:
    `Compute_AiOnDefault_MatchesPinnedFingerprint`, `Compute_LiveWindowAiOnStamps_ArePinned`,
    `Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins`. Durable judgment record →
    `news-judgment-v7`, persisting per consumed family `ObservedAtUtc` and per judgment `ReferencePolicy`,
    `ReferenceKinds`, `TrajectoryReferenceKinds` and the three exclusion counts (v6 records stay readable;
    new fields null = not recorded). **The regime boundary is 214–216** — treated as ONE
    comparability boundary spanning the three slices, with the precommitted **2026-09-29 claim date
    UNCHANGED** (the boundary describes comparability, not the claim). ⚠ That is a deliberate CALL, and its
    basis is NOT that the three share an operator step — they do not. It spans TWO identity discontinuities
    with a one-run cohort between them: the 214+215 operator step was performed on 2026-09-08 and that
    composition (`comparison-basis-v1`, prompt v5, schema v4, `reference-projection-v1`, ledger DISABLED by
    `766c925` ⇒ ZERO references projected) stamped `radar-scoring-fp-8590412af27c` across 102 companies,
    strategy `default`, earliest score `2026-09-08T21:46:22.311Z`. Pooling 214–216 pools that one-run
    cohort in KNOWINGLY; naming it here keeps its snapshots reconcilable. Spec 216 owes a SECOND operator
    step. ⚠ The spec's own line 43 ("the operator step (spec 214 §5) is performed once after merge") was
    written before that run and is stale for the same reason — corrected here; the spec file is left as
    written.
  - **§6 — re-enable.** `Radar:Ai:ReportedMetrics:Enabled` back to `true` in `default.json`, its `_comment`
    amended in place (the DISABLED sentence dropped; it now cites `ReportedMetricVerifier`,
    `ReportedMetricsPolicy.Version`, `ReferenceValueProjector` and `IReportedMetricOutbox`, and states that
    the flag IS a fingerprint input). The unit-level verifier distribution is NOT a live distribution: the
    LIVE one is owed from the first post-merge run with the flag ON (records written, dropped per class,
    acknowledged/unacknowledged, judgments handed ≥ 1 reference by KIND, `ReferenceSupported`,
    `referencesExcludedNewest` / `referencesExcludedLaterThanFact`, pending/acknowledged/replayed), against
    spec 215 §3's kept bound: **a verifier that drops > 50 % of what the model returns is a
    prompt/table defect to investigate, not a finding**.

- **Spec 217 — a pending acquisition is a CLOSED THESIS: recognised deterministically from the 8-K, said
  plainly on the report, and kept out of the forward efficacy series with every exclusion counted.**
  - **The reconstruction that motivated it** (from `data/evidence/raw`, `data/reports/weekly`,
    `data/prices/hzo.json`, read 2026-09-08). On 2026-08-10 Safe Harbor Marinas agreed to buy MarineMax
    (HZO) for **$53.00 a share in cash** (~$1.5B). Radar collected the 8-K (items 1.01/7.01/9.01, accession
    `0001193125-26-341302`) and ~50 articles that day; `KeywordSignalExtractor`'s "material definitive
    agreement" rule minted `StrategicPartnership (Positive)` at strength 4, trajectory rose **56 → 62**, and
    the 2026-08-10 weekly report labelled MarineMax **Thesis improving** at rank 43 — an all-cash sale of the
    whole company, read as a partnership. Nothing predictive existed before the announcement and Radar is
    not meant to front-run a private negotiation; the defect is entirely in how the ANNOUNCEMENT was read.
  - **`acqscan-v1` — deterministic, pure, fail-closed, no AI.** Two legs, both verbatim in the filing's own
    text (primary 8-K document + EX-99.1, fetched once per filing through the existing
    `SecEdgarUrls`/`SecHttpFetch` seam under the shared global SEC pacer): (a) the SUBJECT company is the
    TARGET — its own name in the target POSITION relative to "acquired by" / "merge with and into" /
    "acquisition of", with an ACQUIRER-SIDE VETO evaluated first; (b) a stated per-share cash and/or stock
    consideration. Position matters because an acquirer-side 8-K contains every merger word, so an unordered
    co-occurrence test would close the wrong company's thesis. **"Definitive agreement" alone is not a merger
    phrase** — it is the boilerplate title of every item-1.01 8-K, and the store holds **178** of them
    (measured 2026-09-08) — so it counts only beside an explicit target sentence. Every way a filing falls
    out is its OWN counted `AcquisitionScanOutcome` (`no-merger-agreement`, `company-not-target`,
    `company-is-acquirer`, `acquirer-not-named`, `no-stated-consideration`, `empty-body`).
  - **Both HZO item-1.01 filings are fixture-pinned**, because they are the two shapes that matter:
    `0001193125-26-290439` (2026-06-30, a CREDIT agreement — item 1.01 plus the company's own name
    throughout, the exact shape a title-only rule fires on) must NOT be recognised, and
    `0001193125-26-341302` (2026-08-10) is the ONE expected recognition and the pinned regression for
    "an 8-K 1.01 merger ⇒ `Ignore`, not `Thesis improving`". The fixtures are REPRESENTATIVE 8-K wording
    written from the public facts, not verbatim SEC documents, and nothing claims otherwise — the verbatim
    behaviour over the real filings is the env-gated live harness's job (`AcquisitionRecognitionLiveMeasurementTests`,
    §1 OWED: the distribution over all 178 accrued item-1.01 filings, expected 1 recognised / 177 not; any
    second recognition is investigated by hand and named, because a false positive closes a live thesis).
  - **The state is DERIVED, never curated.** `CompanyStatus.PendingAcquisition` is added to the Domain enum
    but is unreachable from `data/companies.json` (`LocalFileCompanySeedSource` always writes `Active`): the
    Worker resolves it per run from the append-only acquisitions store and stamps it on every snapshot from
    the announcement onward as `CompanyScoreSnapshot.CompanyStatusAtScoring` — **recorded, hashed into
    nothing**, `null` meaning NOT RECORDED and never `Active`. Retiring a closed deal to `Delisted` stays a
    conscious, journaled maintainer step.
  - **Scoring continues; the keyword rule is UNCHANGED; the READ is corrected at assembly.** `acq-supersede-v1`
    rewrites the extractor's `StrategicPartnership` over the ONE recognised evidence id as a Neutral
    `SignalType.CorporateAction` at strength 0 — SAME signal id, so the persisted `ScoreEvidenceLink` still
    walks report → snapshot → signal → evidence and the rewrite is visible as a CHANGE rather than as a
    disappearance. It runs in both windows (velocity too), touches no other type or evidence, and is counted
    on one aggregated per-company line plus the contribution reason. `KeywordSignalExtractor.RuleSetVersion`
    is deliberately NOT bumped (still the value that file owns): the rule is a scoring input, and changing
    the table is a different slice.
  - **⚠ BOTH PIN FAMILIES MOVED ONCE — the first time in the 197→216 arc that the AI-OFF side moved, and
    that is the deliverable.** The cause is a trailing, UNCONDITIONAL `acq=acqscan-v1;supersede=acq-supersede-v1;`
    segment appended to `SignalSourceDescriptor.CanonicalDescriptor()` after spec 198's `newsquery=`. It is
    not AI-gated and not judgment-gated because the supersede is pure assembly code that runs in every
    composition — only the DATA varies — so an unchanged AI-OFF pin would have meant the rule was not
    actually hashed. `ScoringConfigFingerprintTests` is the only authority for the values; the six it
    replaced are quoted there as history. **The operator step is OWED and is a THIRD, SEPARATE one** in this
    arc, not shared with 214/215 (whose step was taken 2026-09-08) or with 216 (which owes its own): delete
    or re-record every configured `data/scoring-configs/strategies/{name}.json` before the first post-217
    baseline. Deliberately EXCLUDED from the hash: `Radar:Acquisitions:MaxFetchesPerRun` and `Enabled`,
    which bound how many filings are READ, never whether a read filing is recognised (the spec-105 rule).
  - **The report says it in three places, and each removal is counted.** Policy `weekly-report-action-v5 →
    v6` gains RULE 0, ahead of thin evidence and ahead of the improving/deteriorating delta, so
    `Thesis improving` structurally cannot fire for a company being bought; the label is `Ignore` (**no new
    label — the six AD-9 labels are unchanged**) and the STATE is the rationale. The entry carries a
    one-line `⏸ Acquisition pending — …` banner under the label; a new `## Acquisitions pending` section
    sits after `## Ignore / Low signal` with company/acquirer/consideration/announced/days-pending/accession;
    and each strategy's ranked table drops the company with a one-line footer naming the count and pointing
    at that section. `StrategyReportSection.Truncated` discounts the exclusion, so the MaxItems cap is never
    blamed for it. **An absent acquisitions store renders NOTHING** (`PendingAcquisitions.RecognitionAvailable`
    is false): "nothing is pending" and "we did not look" are different facts, so only a real store read may
    print the empty section's measured zero.
  - **Efficacy — `observation-eligibility-v2` + `excess-vs-universe-v2`, declared PROSPECTIVELY.** See the
    spec-183 and spec-140/152 bullets above, amended in place. The **2026-09-29 precommitted AD-15 claim
    date is UNCHANGED** and the paired claim interval starts after it, so no outcome that has entered the
    claim family is re-scored. Both artifacts stamp both rule identities.
  - **§3 measured, not asserted** (`AcquisitionLeaderboardCounterfactualTests`, a read-only offline paired
    recomputation over the accrued store on 2026-09-09: ONE store, ONE price side, ONE frozen benchmark, and
    the projection as the ONLY difference). Per arm, out-of-sample observations fell 1036 → 1022 (or
    1110 → 1095 / 1089 → 1074), with **37–45 company-days excluded as `CorporateActionInWindow`** per arm
    across 48 as-of dates, and **32 of 48 as-of dates had the pinned member removed from the peer mean**.
    Every |Δρ| is ≤ 0.025. **THE ORDERING DID CHANGE, and it is stated plainly rather than buried:** the two
    adjacent pairs `baseline-earnings-only` / `baseline-activity-only` (in-sample ρ 0.0045 / −0.0178 →
    −0.0051 / 0.0065) and `filings-led-halfnoted` / `filings-led-nonoted` (−0.1324 / −0.1350 → −0.1471 /
    −0.1455) swapped ranks — ranks 6/7 and 8/9 respectively. Both swaps are between arms already separated by
    less than the width of their own confidence intervals: the second pair's in-sample ρ differ by 0.0026
    before and 0.0016 after, the first pair's by 0.0223 before and 0.0116 after, over 33 in-sample and 15
    out-of-sample as-of dates. The top five (baseline-media-only > disclosure-led-v10-control >
    disclosure-led-v11 > default > narrative-led-v2) are unchanged, and the same three arms
    (`default-noattn`, `filings-led`, `narrative-led`) drop for thin data before and after. **This is a
    finding about how much ONE takeover was worth in a small sample, not a result** — and it is precisely why
    the exclusion is declared prospectively rather than applied to a claim already made.
  - **Review round 2 found a LIVE REGRESSION the whole suite had stayed green through, and it is the
    lesson worth keeping.** `FileStrategyEvidenceFactsSource` gates on the leaderboard CSV's
    `schemaVersion` and refuses anything it does not know, degrading the WHOLE evidence layer to
    "unavailable"; spec 217 bumped `StrategyLeaderboardRenderer.CsvSchemaVersion` to
    `strategy-leaderboard-v3` and did not move that reader's supported list — so the next live report would
    have printed **"Accruing (evidence unavailable)" for every arm**. Nothing failed, because the reader's
    tests build their header from a COPY of the renderer's, and one of them even used
    `strategy-leaderboard-v3` as its example of an *unknown* schema and kept passing for an unrelated reason.
    Fixed three ways: the reader accepts v2 (the shape live deployments have on disk) and v3; a new test
    asserts the supported list CONTAINS the shipped `CsvSchemaVersion` constant rather than a copy of it; and
    the stale "byte-for-byte the renderer's CsvHeader" comment is corrected in place rather than left
    standing. **A copied schema literal is a fact with no owner** — the same rule CLAUDE.md states for pins,
    reaching a place nobody had applied it.
  - **The Lead-call evidence lines cite the rule identities OFF THE ARTIFACT** (§3's second stamping
    requirement, initially only half met — the artifacts were stamped, the operating-call table was not).
    `RankedEvidence` carries `ObservationEligibilityVersion` / `ExcessRuleVersion` read from the leaderboard
    CSV's own per-row columns, and the "Calls and evidence status" table renders
    `[eligibility …; benchmark …]` on each ranked line. They are read from the FILE, never from a code
    constant, because the artifact was written by a PREVIOUS run: a constant would assert today's rules over
    yesterday's numbers. A pre-217 artifact carries neither column and the line prints
    `not stated (pre-217 artifact)` — a stale artifact declares itself instead of being silently relabelled.
  - **Two accounting corrections from the same round.** (i) A store root that cannot be enumerated — or that
    is a FILE rather than a directory — now returns `AcquisitionStoreReadResult.Unavailable`, which forces
    `PendingAcquisitions.RecognitionAvailable` false, so the report stays SILENT instead of printing
    "no company is under a recognised pending acquisition" over a store it could not open. `Readable`
    (there is no answer) and `Unreadable > 0` (some files in a successful read did not parse) are kept as
    separate facts. (ii) `acqscan-v1`'s final verbatim re-check no longer borrows a leg's bucket: it used to
    return `NoStatedConsideration` even when the leg-(a) TARGET quote failed, mis-attributing a leg-(a)
    failure in the very tally whose purpose is that split. The two checks are now separate branches
    returning a dedicated `VerbatimCheckFailed`, which is its own bucket because a failure there is a DEFECT
    IN THE SCAN (every quote is a slice of the body being checked), not a fact about the filing — a non-zero
    count in the live distribution is a finding to investigate.
  - **Round 3 — the v3 CSV said v1 in its own column NAMES, and that is the same defect class.** The rho
    columns were still `inSampleRhoExcessVsUniverseV1` / `outOfSampleRhoExcessVsUniverseV1` while carrying
    `excess-vs-universe-v2` VALUES, with the adjacent `excessRuleVersion` cell correctly saying v2 — two
    cells in one row disagreeing, which is worse than one stale cell because a reader acts on whichever they
    find first, and a consumer parsing by column name would have read a v2 number as v1. §3 declares v2 ON
    the artifacts; shipping v2 values under v1 names does not do that. The v3 names are now
    **`inSampleRhoExcess` / `outOfSampleRhoExcess`** — deliberately carrying NO version, because baking one
    into a column name duplicates a value that code defines (the pins rule, reaching a new place): the rule
    version has exactly ONE owner on the artifact, the per-row `excessRuleVersion` column sourced from
    `UniverseBenchmark.ExcessRuleVersion`, and a version-free name can never go stale. The v2 names survive
    as `StrategyLeaderboardRenderer.LegacyInSampleRhoColumnV2` / `LegacyOutOfSampleRhoColumnV2` purely so
    the evidence-facts reader still understands a pre-217 artifact a live deployment has on disk; that
    reader tries the v3 name first and falls back, and splices BOTH from the renderer's constants rather
    than holding literals of its own. A new test asserts the rendered header line against the REAL renderer
    (an empty leaderboard renders exactly its header), the same anti-drift move as the `CsvSchemaVersion`
    assertion — the copies are what let both of these go stale silently.
  - **The rest of the v1 strings were AUDITED and are correct history, not stale claims**:
    `strategy-leaderboard-raw-v1.{md,csv}` and `strategy-leaderboard-excess-v1.{md,csv}` are preserved FILE
    names; `benchmark-universe-v1` is the frozen UNIVERSE version, a different thing from the excess rule
    and genuinely still v1; and the markdown table header, metric line and `## Benchmark (…)` heading all
    render `excess-vs-universe-v2` (the heading interpolates the constant). `PairedComparisonRenderer`
    contains no v1 naming at all.
  - **Round 4 — the benchmark has TWO consumers, and the second one was missed by rounds 1–3.**
    `NewsRiskEvaluationGenerator` takes the same singleton `IUniverseBenchmarkProvider` the leaderboard does
    (registered one line apart in the composition root), so **its excess values silently became
    `excess-vs-universe-v2` the moment spec 217 shipped** while the artifact still said v1 in three places:
    the per-row `excessForwardReturn21dBasis` token (`excess-vs-benchmark-universe-v1`), the markdown column
    label (`Excess fwd 21d vs universe-v1`) and the prose, which stated the **v1 rule verbatim** ("the other
    resolved frozen-universe members, self-excluded") — false the moment any member is pinned, which is the
    spec's own worked example. All three now name no version or cite
    `UniverseBenchmark.ExcessRuleVersion`; this artifact carries no schema-version column of its own, so the
    per-row basis token IS its outcome-definition stamp and it moves with the rule automatically. The
    pinned-member accounting the leaderboard gained is now on this artifact too (peer-mean removals per
    as-of date, a MEASURED zero when zero, and an explicit "NOT a measured zero" line when the universe
    could not be loaded). The pinned test assertion is against the CONSTANT — the literal it replaced is
    precisely how the defect survived CI, the third instance in this slice of a copied token going stale.
  - **DECISION, made and stated: the news-risk artifact MARKS a pinned row; it does not EXCLUDE it.** The
    news-risk path applies the peer-mean exclusion (it shares the benchmark) but NOT
    `ObservationEligibility`, so a row for a company that is ITSELF pinned still gets an excess computed off
    its own take-out pop. That asymmetry is deliberate and is now on the artifact rather than silent. The
    efficacy series EXCLUDES the equivalent observation because it **ranks**: an outcome no strategy could
    have earned would be scored there as skill or as a miss. The news-risk evaluation **describes** one
    frozen assessment at a time and declares itself non-claim-bearing, so dropping rows would shrink the
    coverage of the very records it exists to show and would DISCARD an assessment rather than count one.
    The forward return is a true fact about the company; what would be false is reading it as an ordinary
    business outcome — so the row carries a per-row `corporateActionInWindow21d` column, the count and the
    reasoning are rendered in the markdown ("Read a marked row's return as the deal, not as the business"),
    and the marker is BLANK rather than `false` when no forward return was computed at all. A silent
    asymmetry between two artifacts computed off one benchmark is the thing that bites later; this one is
    written down in the code, on the artifact and here.
  - **The §3 numbers above were RE-MEASURED after these fixes and are byte-identical**, and no fingerprint
    pin moved (asserted by `ScoringConfigFingerprintTests`): none of the four touches the observation
    builder, the benchmark, the descriptor or any hashed input.

- **Spec 219 (2026-09-09) — judgment coverage went UNIVERSAL: read this date before reading the efficacy
  chart.** Until this date Radar's only source of "is this news good or bad" was the stage-2 judge, and the
  judge's only candidate source was `NewsRiskCandidateSelector` — the spec-179 §3 top-five-rows-per-Research-
  section traversal, built for a RISK AUDIT of the names about to be shown to a human and never re-derived
  when spec 194 made the same verdict the source of directional `MediaAttention` for SCORING. ~19 of 102
  companies were read per run; the rest reached scoring as a count of articles with no direction, which
  `RadarScoreFormulaV8` then used to DISCOUNT them, which pushed them further out of the top five. From this
  date `news-judgment-coverage-v2` (`NewsJudgmentCoveragePolicy.Version`) enumerates the company universe —
  ticker order, no rank, no consensus, no notion of "better" — and the BUDGET moved from coverage to depth
  (`Radar:NewsResearch:Judgment:MaxFamiliesPerBreadthJudgment`, shipped default owned by
  `NewsJudgmentOptions.DefaultMaxFamiliesPerBreadthJudgment`), with the spec-179 depth cohort retained
  unchanged at the full budget. **A step change in the efficacy series across this date is Radar GAINING A
  SENSE ORGAN, not Radar getting better** — before it, most companies' news was volume; after it, most
  companies' news has a judged direction. That is why the date is recorded: the two sides are not a
  before/after of the same measurement. Spec 219 moved the three AI-ON fingerprint pins (and their three
  no-newsquery halves) through the enabled-only coverage-policy field on the `news=` segment and moved NO
  AI-OFF pin; the values are owned by `ScoringConfigFingerprintTests` and are not quoted here. The record
  tag moved to `news-judgment-v8` (`NewsJudgmentRecord.CurrentSchemaVersion`); the prompt, the response
  schema and the stage-2 cohort key did NOT move, so accrued verdicts stay cacheable. Nothing was
  backfilled: accrued history heals forward only (AD-8/AD-1), and every pre-219 judgment record hydrates its
  new coverage fields as `null` = NOT RECORDED rather than as a fabricated full-depth read.

## default.json _comment history (moved verbatim by spec 213, 2026-09-07)

This is HISTORY, not current state. The text below is `scripts/run-profiles/default.json`'s pre-213 top-level
`_comment`, `Radar:News:_comment` and `Radar:NewsResearch:_comment2`, moved here verbatim and unedited (including
the in-place amendments `fb1335d` made, which are part of the record), split into the notes the top-level comment
already consisted of and kept in the ORDER they appeared in the file — which is NOT chronological. The order is
itself evidence of how the drift happened (state a fact as current, then append a superseding note at the end):
do not reorder it. Any value quoted below — fingerprint pins, formula and rule-set versions, strategy indices,
universe/scoring/entry counts, dates — is OWNED by `ScoringConfigFingerprintTests`, `ScoreFormulaVersions.cs`,
`KeywordSignalExtractor.RuleSetVersion` and the profile itself; read the owner, never a number here. A claim
below may already have been stale when it was moved, and nothing was corrected, trimmed or tidied AT THE MOVE.
That is a statement about spec 213, not a freeze: a LATER slice that supersedes an identity/pin claim living
here amends THAT sentence in place under the REVERSAL rule (specs 214, 215 and 216 each did, to the spec-198
pin paragraph) rather than appending a contradicting sibling, because a reader greps `news=` or a pin phrase
and acts on whichever sentence they find first. An amendment REPLACES the stale claim; it never deletes the
record — a superseded pin that actually stamped a live run stays quoted beside the new one AS HISTORY (both
`radar-scoring-fp-11240da5aeb0` and `radar-scoring-fp-8590412af27c` do), because accrued snapshots carry it
and an analyst must still be able to reconcile them.

### Top-level `_comment` (moved verbatim by spec 213)
- BASELINE Radar live-run profile ('default') — the canonical record of how we run the Worker for a live measurement. run-radar.ps1 always loads this as the base; other profiles overlay small deltas on top. Machine-specific bits are NOT in here: the output directories and the SEC User-Agent (a real contact email) are supplied by run-radar.ps1 at runtime, and SECRETS are never in here at all — the baseline earnings read (spec 119) is DeepSeek-V4-Flash on DeepInfra via the OpenAI-compatible provider (spec 118), whose API key is read at RUNTIME from the environment variable NAMED by Radar:Ai:OpenAi:ApiKeyEnvVar (DEEPINFRA_API_KEY); the key VALUE is never written here, never logged, and a missing/empty key fails the run loudly (same precedent as the SEC User-Agent). It replaced local ollama/llama3.1 after the 2026-07-21 A/B: DeepSeek read EOSE's reported -70% gross margin as Mixed 0.85 where llama3.1 read Improving 0.90, and caught AEHR's deteriorating reported quarter (revenue $59M->$50M, GAAP net loss $(3.9)M->$(7.1)M). Because the reading model changes signal DIRECTION it is folded into the AI-ON scoring fingerprint BY VALUE via the spec-106 directional-filing descriptor (now str/nov/minconf/model, model LAST, escaped) — a comparability input like the spec-95 collector set, with NO _formula.Version / RuleSetVersion bump. Ollama remains fully supported: a -Profile overlay can point Radar:Ai back at it. Scoring is intentionally omitted so it uses the code defaults (radar-formula-v8; MediaReachWeight 0.10 post-spec-94; the spec-112 recalibration raised the AI directional-filing default Strength 6->8 — a confident full-text guidance read now outweighs the keyword extractor max of 6 so it can materially move the thesis, applied symmetrically to raised/cut guidance, MinConfidence unchanged at 0.6, a config-magnitude change with NO _formula.Version / RuleSetVersion bump; the directional Strength is folded into the fingerprint ONLY when the AI directional path is registered, and the LIVE AI-ON default fingerprint this profile actually produces — an AI provider IS configured below, so the directional descriptor IS folded in — was radar-scoring-fp-5ffa8c9e25f0 AT THIS BASELINE'S Radar:ScoringWindowDays=60 as of spec 160 — SUPERSEDED six times since (191, 194 §1.5, 194 §2, 196, 197, 198): the CURRENT value is asserted by ScoringConfigFingerprintTests, never by this comment, and the last value recorded here is the SPEC 198 note's (verified live 2026-09-06) — re-stamped from radar-scoring-fp-4da4b5ff6ec9 by SPEC 160 (the comparability-cap cmpscan/cmpcap descriptor fields — see the SPEC 160 note at the very end of this comment), which had itself re-stamped from radar-scoring-fp-3457da53489d by spec 148 (the scoring WINDOW and TrajectoryCorroborationK are folded into the hash, so the live stamp is WINDOW-DEPENDENT now and the unit-test pin radar-scoring-fp-28226897f97b — computed at the ScoringOptions CODE default of 30 days, which the Worker never uses — is NOT the value this profile stamps; see the SPEC 148 note at the very end of this comment), which had itself re-stamped from radar-scoring-fp-57356123e09b by the spec-141 split of collection provenance out of strategy identity (see the SPEC 141 note at the very end of this comment), which had itself re-stamped from radar-scoring-fp-74c5e077f728 by the spec-133 promotion of the openFDA collector 'fda' INTO the Collectors array above — a COLLECTOR-SET change (6 -> 7 collectors), which AD-10 re-stamps AUTOMATICALLY with NO RuleSetVersion bump (radar-keyword-rules-v6 stands), NO _formula.Version bump (radar-formula-v8 stands) and scoring math byte-identical — which had itself re-stamped from radar-scoring-fp-2be98e738684 by the spec-130 RuleSetVersion radar-keyword-rules-v5 -> v6 bump (the new opt-in-OFF TrademarkActivity rule group; scoring math byte-identical), which had itself re-stamped from radar-scoring-fp-63c096e531ec by the spec-129 RuleSetVersion radar-keyword-rules-v4 -> v5 bump (the new opt-in-OFF RegulatoryApproval rule group; scoring math byte-identical), which had itself re-stamped from radar-scoring-fp-c908f03a554a by the spec-127 RuleSetVersion radar-keyword-rules-v3 -> v4 bump (the new opt-in-OFF PatentActivity rule group; scoring math byte-identical), which had itself re-stamped from radar-scoring-fp-2ef5ef96cce2 by the spec-122 radar-formula-v8 structure bump described below, which had itself re-stamped from radar-scoring-fp-4c06fd2d2d8c by spec 119 (the earnings-read model identity openai:deepseek-ai/DeepSeek-V4-Flash joined the directional descriptor, and the pinned value is now built through the real ai=-segment escaping so it is what a live run actually stamps), which had itself re-stamped from the spec-112 AI-ON stamp radar-scoring-fp-454984785732 by the spec-117 radar-formula-v7 structure bump; the spec-117 notedness-aware Opportunity discount — followingDiscount = 1 - (Attention/OpportunityAttentionDivisor)*OpportunityAttentionDiscountWeight - TierDiscount(followingTier)*FollowingTierDiscountWeight, clamped to [OpportunityDiscountFloor 0.05, 1], where followingTier is the CURATED per-company seed tier in data/companies.json (mega/large/mid/small; AD-14: never price/market-cap/volume-derived) and the tier magnitudes (mega 0.45 / large 0.30 / mid 0.15 / small 0.0) plus the two term weights and the floor are new ScoringWeights config knobs hashed by value — is a formula STRUCTURE change, so _formula.Version advanced radar-formula-v6 -> radar-formula-v7 and the AI-OFF fingerprint re-stamped radar-scoring-fp-c45fb79092ea -> radar-scoring-fp-8f4b59efd288; the spec-122 breadth-preserving collapse — the Attention reach term becomes reach = breadthSurvivors + CollapsedBreadthCredit*breadthCollapsedExtra + MediaReachWeight*mediaSignalCount, where breadthCollapsedExtra is the TIER-WEIGHTED sum over third-party publishers that the spec-109 same-event media collapse dropped (so 15 distinct genuine outlets covering ONE event now read as breadth 15, not 1, while 15 mill re-posts still add only ~1.5) and mediaSignalCount stays POST-collapse so no volume/velocity term is re-admitted (AD-14 clean) — is likewise a formula STRUCTURE change, so _formula.Version advanced radar-formula-v7 -> radar-formula-v8, the new ScoringWeights magnitude CollapsedBreadthCredit (default 1.0, range [0,1]; at 0.0 v8 is byte-identical to v7) is hashed by value, and BOTH defaults re-stamped: AI-OFF radar-scoring-fp-8f4b59efd288 -> radar-scoring-fp-cb80a5809882 and AI-ON radar-scoring-fp-2ef5ef96cce2 -> radar-scoring-fp-c908f03a554a; the spec-127 PatentActivity rule group (opt-in-OFF collector 'patents') then bumped RuleSetVersion radar-keyword-rules-v3 -> v4, re-stamping BOTH defaults again with scoring math byte-identical: AI-OFF radar-scoring-fp-cb80a5809882 -> radar-scoring-fp-b4a040144f66 and AI-ON radar-scoring-fp-c908f03a554a -> radar-scoring-fp-63c096e531ec; the spec-129 RegulatoryApproval rule group (opt-in-OFF openFDA collector 'fda') then bumped RuleSetVersion radar-keyword-rules-v4 -> v5, re-stamping BOTH defaults again with scoring math byte-identical: AI-OFF radar-scoring-fp-b4a040144f66 -> radar-scoring-fp-1251d4e0373e and AI-ON radar-scoring-fp-63c096e531ec -> radar-scoring-fp-2be98e738684; the spec-130 TrademarkActivity rule group (opt-in-OFF USPTO trademark collector 'trademarks') then bumped RuleSetVersion radar-keyword-rules-v5 -> v6, re-stamping BOTH defaults again with scoring math byte-identical: AI-OFF radar-scoring-fp-1251d4e0373e -> radar-scoring-fp-c1e126884b7c and AI-ON radar-scoring-fp-2be98e738684 -> radar-scoring-fp-74c5e077f728; spec 133 then promoted the openFDA collector 'fda' INTO the Collectors array above (see the promotion note below), which is a COLLECTOR-SET change rather than a rules/formula change and therefore re-stamps BOTH defaults again AUTOMATICALLY under AD-10 with NO RuleSetVersion bump, NO _formula.Version bump and scoring math byte-identical: AI-OFF radar-scoring-fp-c1e126884b7c -> radar-scoring-fp-6b2f468041b9 and AI-ON radar-scoring-fp-74c5e077f728 -> radar-scoring-fp-57356123e09b; for the spec-117 v7 following discount (carried into v8 unchanged), a Small-tier company at default weights is byte-identical to v6 (AEHR unchanged) while a mega-cap like JNJ (Attention 21) drops its discount multiplier 0.916 -> 0.466 so a v6 Opp 45 lands ~23 — a graded lean via the floor, never a hard exclusion; AI-OFF fingerprint radar-scoring-fp-4eb2fe5d3cdf for this 7-collector baseline incl. sec13dg and fda AT Radar:ScoringWindowDays=60, re-stamped from radar-scoring-fp-2ce20f8fc497 by spec 148 (the scoring WINDOW and TrajectoryCorroborationK fold; the 30-day CODE-DEFAULT unit-test pin is the different value radar-scoring-fp-0c46e07b94db — see the SPEC 148 note at the very end of this comment), which had itself re-stamped from radar-scoring-fp-6b2f468041b9 by spec 141 (it now re-stamps automatically when the rules / weights / formula-structure change, but NOT when a collector is switched on or off — spec 141 removed the collector set from the hash; the spec-111 corroboration-aware Trajectory — the current-window directional signals now split into a positive mass and a negative mass combined as T_raw = 10*(Mpos-Mneg)/(Mpos+Mneg+k) so a corroborated majority is rewarded and a lone dissenter is damped-but-not-zeroed; only the Trajectory component changed, every other component is byte-identical to v5 — is a formula STRUCTURE change, so _formula.Version advanced radar-formula-v5 -> radar-formula-v6 and the fingerprint re-stamped from radar-scoring-fp-abbdf9fab44f; the corroboration-smoothing constant k is the new config knob Radar:Scoring:Profiles:{name}:TrajectoryCorroborationK — a ScoringWeights magnitude bound via AddRadarScoringWeights from the profile selected by Radar:Scoring:Profile (default profile 'default'); default 10, tuning it is a config edit, no formula bump; the spec-110 recalibration of the default insider SellTiers to a materiality-scaled asymmetric curve — buy>>sell, e.g. sell 50000000:8,25000000:7,10000000:6,2500000:5,1000000:4,250000:3,MinValue:2 so a ~$1.6M lone discretionary sale maps to strength 4 not 7 while a >=$50M sale still reaches 8; BuyTiers/ClusterBoost unchanged — was folded into the fingerprint BY VALUE via the spec-96 insider descriptor, re-stamping it from radar-scoring-fp-525e552874eb with scoring math on non-insider-sell signals UNCHANGED and no _formula.Version / RuleSetVersion bump; the earlier spec-109 same-event media-attention collapse descriptor — media-collapse-v1;window=3 — had re-stamped it from radar-scoring-fp-c9e609ed53e9 with scoring math BYTE-IDENTICAL on a non-media signal set — only the MediaAttention input set is de-noised; the earlier spec-103 radar-keyword-rules-v3 bump — the new opt-in-OFF HiringActivity rule group — had re-stamped it from radar-scoring-fp-8d638b90d4aa), which, SINCE SPEC 141, NO LONGER folds the enabled collector set — that set is instead RECORDED per snapshot as CollectionProvenance, by concrete IEvidenceCollector.CollectorName, e.g. rss's is "RssPressReleaseCollector" and sec's is "sec-edgar", not the "kind" tokens in the Collectors list above, and is hashed into NOTHING — but still folds the extractor rule-set identity (radar-keyword-rules-v6 when written, v8 since spec 194 — KeywordSignalExtractor.RuleSetVersion is the owner; incl. the spec-99 InstitutionalOwnership 13D/13G rule group, the spec-103 HiringActivity group, the spec-127 PatentActivity group, the spec-129 RegulatoryApproval group, and the spec-130 TrademarkActivity group; the 'hiringats', 'patents', and 'trademarks' collectors themselves stay opt-in OFF — NOT in Collectors above; 'fda' was opt-in OFF too until spec 133 promoted it into Collectors above) plus the config-tunable insider buy/sell materiality tiers + cluster boost (InsiderMaterialityWeights; BuyTiers/ClusterBoost default == spec 93, SellTiers default == the spec-110 asymmetric materiality curve) plus the same-event media-attention collapse window (MediaCollapseOptions, default 3 days; Radar:Scoring:MediaCollapse:EventWindowDays) into the fingerprint). 'secform4' (spec 93 SEC Form 4 insider-transaction collector) was promoted into the baseline on 2026-07-05 after its live re-measure validated directional insider buy/sell signals. 'sec13dg' (spec 99/100 SEC 13D/13G institutional-ownership collector) was promoted on 2026-07-06 after its live run validated the direction split (2 activist-13D Positive / 138 passive-13G+amendment Neutral); 13G is Neutral by design (dominated by passive index filers) so it never misfires bullish. 'fda' (spec 129 openFDA 510(k)/PMA device clearance collector) was promoted on 2026-07-25 by spec 133 — it is Radar's first DIRECTIONAL non-filing collector (its fixed phrase maps to a Positive, routine-strength RegulatoryApproval signal via the radar-keyword-rules-v6 group that spec 129 added but which had never fired, because no ENABLED collector produced the phrase), it runs against the KEYLESS api.fda.gov so it introduces no secret, no env var and no key gate, and data/companies.json declared an 'fda' feed for only 2 of 43 companies when written (TMDX applicant=TransMedics, AXGN applicant=Axogen) and still 2 — of 102 — on 2026-09-07 (specs 199/207 added 59 companies with no fda feed) — thin coverage is expected and accepted (usaspending: 3-of-43 then, 3-of-102 now), and widening the seed set is a separate evidence-led task since a company only earns a feed once its applicant token is verified to return results. This promotion is a COLLECTOR-SET change ONLY: per AD-10 (as amended) SignalSourceDescriptor folds the enabled collector set into the ScoringConfigVersion by concrete CollectorName ('fda'), so BOTH default fingerprints re-stamp AUTOMATICALLY — AI-OFF radar-scoring-fp-c1e126884b7c -> radar-scoring-fp-6b2f468041b9 and AI-ON radar-scoring-fp-74c5e077f728 -> radar-scoring-fp-57356123e09b — with NO KeywordSignalExtractor.RuleSetVersion bump (radar-keyword-rules-v6 unchanged), NO _formula.Version bump (radar-formula-v8 unchanged), no ScoringWeights / attention-tier / insider-materiality edit and BYTE-IDENTICAL scoring math; the 41 companies with no 'fda' feed keep an identical evidence set, identical signals and identical component scores, and only their stamped ScoringConfigVersion differs. Because the stamp moves, the spec-101/108 score-vs-price efficacy overlay opens a fresh segment here — expected and correct (that is exactly what spec 108's continuity-aware segmentation exists to mark), so the next few runs show a short score series. Prices.Enabled=true turns ON the spec-92 daily price-history reference acquisition (AD-14: validation/reference data only, NEVER a scoring input) so score-vs-price history accrues for a future backtest chart; run-radar.ps1 supplies Radar:PricesDirectory (-> <outRoot>/prices). Efficacy.Enabled=true turns ON the spec-101 read-only score-vs-price render (AD-14 read SIDE — never a scoring input): each run writes per-company SVG+CSV to <outRoot>/efficacy (run-radar.ps1 supplies Radar:EfficacyDirectory). The score series is segmented by the STRATEGY NAME since spec 141 (ScoreSeriesKey; the ScoringConfigVersion fingerprint is still drawn as a dashed boundary tick but no longer breaks the line), so one strategy renders as one continuous series and switching a collector on no longer fragments it — run on a cadence to let the score line accrue. ReportMaxItems=90 raises the weekly-report entry cap (RadarWorkerOptions.ReportMaxItems, default 25) because the 2026-07-22 run scored 29 companies and the report silently rendered only 25 — TR/WTRG/CVX/CAT were scored and then dropped on the floor, invisible to the reader; with the spec-125 universe expansion to 43 it would have dropped 18. Spec 125 set it to 60 — deliberate headroom over 43 so the next universe expansion did not silently truncate; SPEC 159 (2026-07-29) then expanded the universe 43 -> 66 (23 verified companies added to data/companies.json), at which 60 would have silently dropped up to 6 scored companies — the exact failure this cap exists to prevent — so it was raised 60 -> 90, again with deliberate headroom (90, not 66) so the NEXT expansion does not silently truncate either. THAT DID NOT HOLD: spec 199 (74 -> 94) and spec 207 (94 -> 102) both passed 90 without raising it, and every strategy section from 2026-09-03 to 2026-09-06 rendered 'showing top 90' of 102 scored — counted, not silent, but 12 scored companies per arm never reached the reader. Raised 90 -> 120 on 2026-09-07 with the same headroom logic; the durable fix (a cap derived from the universe size, or a startup failure when universe > cap) is NOT done and a universe expansion must re-check this line. It is an OPERATIONAL DISPLAY parameter, not a scoring weight: it is not a ScoringWeights knob, is not hashed into the ScoringConfigVersion fingerprint, and needs no _formula.Version / RuleSetVersion bump (no fingerprint moved for it; the pins this sentence used to quote are history — current values live in ScoringConfigFingerprintTests). Ai:MaxFilingsPerRun=50 overrides DirectionalFilingSignalOptions' default of 5 (also RadarWorkerOptions.Ai.MaxFilingsPerRun): that low default predated the global SecRequestPacer (spec ~110), which now owns SEC fair-access burst protection process-wide, so a small cap is a stale pre-pacer cost limiter, NOT a safety mechanism. With spec 126's post-cache semantics the cap bounds only NEW AI analyses per run (cache hits replay unbounded), so 50 lets the spec-125 43-company universe read its uncached backlog in one pass while cached directional reads keep contributing regardless. Like ReportMaxItems it is a cost/operational knob, not a ScoringWeights magnitude: it is NOT hashed into the ScoringConfigVersion fingerprint and needs no _formula.Version / RuleSetVersion bump (no fingerprint moved for it; the pins this sentence used to quote are history — current values live in ScoringConfigFingerprintTests). Code defaults remain 5 so a fresh empty-cache environment still drains its backlog cheaply over successive runs.
-  SPEC 141 (2026-07-26) — STRATEGY IDENTITY vs COLLECTION PROVENANCE: the enabled-collector set left the ScoringConfigVersion hash entirely. SignalSourceDescriptor now exposes TWO strings — CanonicalDescriptor() (identity: rules=...;[ai=...;], the fingerprint input) and CollectionProvenance() (collectors=<csv>;, stamped verbatim on every snapshot and hashed into nothing) — so switching a collector on or off in the Collectors array above records WHAT WAS COLLECTED without re-stamping any strategy, and the spec-133 style of re-stamp can never happen again. The score series is keyed by StrategyName instead (null/blank => 'default', so all accrued history stays in the primary series), a strategy is IMMUTABLE BY CONVENTION (to change one, add a new name such as momentum -> momentum-v2), and the fingerprint is demoted to a STARTUP TRIPWIRE: StrategyIdentityGuard compares each strategy's computed fingerprint against the per-name record at <outRoot>/scoring-configs/strategies/{name}.json before Stage 1 and fails the run fast if a NAME was edited in place. Both baseline fingerprints therefore moved ONCE, DELIBERATELY — AI-OFF radar-scoring-fp-6b2f468041b9 -> radar-scoring-fp-2ce20f8fc497 and AI-ON radar-scoring-fp-57356123e09b -> radar-scoring-fp-3457da53489d — with NO KeywordSignalExtractor.RuleSetVersion bump (radar-keyword-rules-v6 stands), NO _formula.Version bump (radar-formula-v8 stands), no ScoringWeights / attention-tier / insider-materiality edit and BYTE-IDENTICAL scoring math: every component, weight and gate is unchanged and only the stamps differ. Accrued history was NOT regenerated or rewritten (append-only, AD-8), so the pre-141 fragments stay exactly as they are.
-  SPEC 140 (2026-07-27) — STRATEGY-vs-PRICE COMPARISON: because Efficacy.Enabled is true above, each run now ALSO writes a single strategy leaderboard pair to <outRoot>/efficacy/strategy-leaderboard.{csv,md} (Radar:Efficacy:Comparison, enabled by default INSIDE the Radar:Efficacy gate). It runs the SAME no-look-ahead join once per configured strategy over that strategy's own persisted score store, relates each score at D to price movement over (D, D+h] (h = Radar:Efficacy:Comparison:ForwardHorizonDays, default 21 calendar days; price at or before D is never read), and ranks the strategies by Spearman rank correlation with a chronological hold-out (Radar:Efficacy:Comparison:HoldOutFraction, default 0.30) — the ranking is computed IN-SAMPLE and the headline number is OUT-OF-SAMPLE. It reports the honest N and NAMES every strategy dropped for thin data (Radar:Efficacy:Comparison:MinimumObservations, default 20 per window). Price stays validation-only and strictly downstream of scoring (AD-14) — asserted architecturally over the scoring type graph — and NOTHING here is hashed: no new fingerprint input, no _formula.Version bump, no RuleSetVersion bump, so both stamps stood through spec 140 (AI-OFF radar-scoring-fp-2ce20f8fc497 / AI-ON radar-scoring-fp-3457da53489d; spec 148 has since moved both — see the SPEC 148 note at the very end of this comment) and every existing per-company efficacy SVG/CSV is byte-unchanged. With the current single 'default' strategy and a short accrued series this is expected to render an honest 'No strategy could be ranked' leaderboard until enough joined history exists — that is a result, not an error. Set Radar:Efficacy:Comparison:ReplayLabel to compare a spec-139 replay run's per-strategy output under <outRoot>/replays/{label}/ instead of the live forward series (run the replay first; a replay run replaces the pipeline run and never renders efficacy). Radar:Efficacy:Comparison:Enabled=false turns the whole thing off and leaves the per-company render exactly as it was.
-  SPEC 148 (2026-07-27) — FINGERPRINT COMPLETENESS: two genuinely output-affecting inputs had been hashed into NOTHING and are now folded by value, so BOTH baseline fingerprints moved ONCE, DELIBERATELY — the LIVE stamps this profile writes went AI-OFF radar-scoring-fp-2ce20f8fc497 -> radar-scoring-fp-4eb2fe5d3cdf and AI-ON radar-scoring-fp-3457da53489d -> radar-scoring-fp-4da4b5ff6ec9. ⚠ READ THIS BEFORE RECONCILING A STAMP: spec 148 BROKE the long-standing equivalence between the values pinned in ScoringConfigFingerprintTests and the values a live run stamps. Every hashed input used to be a code default, so the two coincided; the WINDOW is not a code default. The unit-test pins are computed at the ScoringOptions CODE default of 30 days (AI-OFF radar-scoring-fp-0c46e07b94db / AI-ON radar-scoring-fp-28226897f97b) — a value the Worker NEVER uses — whereas this baseline runs at Radar:ScoringWindowDays=60 (RadarWorkerOptions.ScoringWindowDays=60 and src/Radar.Worker/appsettings.json; this profile deliberately does NOT override it) and therefore stamps the 60-day pair above. For reference the -Profile long-window overlay (120 days) stamps AI-OFF radar-scoring-fp-0a7058d94582 / AI-ON radar-scoring-fp-19fecdb64e3a (the AI-ON side re-stamped from radar-scoring-fp-81e9fab711f8 by spec 160). The stamps to reconcile against data/scoring-configs/strategies/{name}.json and against any accrued snapshot are ALWAYS the ones for the window that run actually used. (1) The recent-signal WINDOW (ScoringOptions.Window, bound from Radar:ScoringWindowDays — 60 on this baseline) bounds both the current window and the previous/velocity window, so a 14-day and a 60-day run produce materially different Trajectory/SignalVelocity/Attention — and used to stamp the SAME ScoringConfigVersion. That is also why scripts/run-profiles/long-window.json's long-standing claim that widening the window re-fingerprints the effective config is only TRUE as of this slice; before it, the long-window experiment was silently sharing the baseline's stamp. It is hashed as TICKS (injective; whole-days would let a 36h and a 24h window collide) and is now carried verbatim on the persisted EffectiveScoringConfig as a trailing NULLABLE field (null = written before spec 148, i.e. not recorded — never a false claim of a zero-length window). (2) ScoringWeights.TrajectoryCorroborationK, the k in T_raw = 10*(Mpos-Mneg)/(Mpos+Mneg+k) and, since spec 146, the same denominator in radar-formula-v9's per-channel direction factor — the ONE ScoringWeights field the fold had ever missed. NO KeywordSignalExtractor.RuleSetVersion bump (radar-keyword-rules-v6 stands), NO _formula.Version bump (radar-formula-v8 stands), no weight/tier/attention edit, and scoring math is BYTE-IDENTICAL — proven by compiling and running a whole-output pin (five components + explanation + componentJson + the ordered evidence-link chain) against the pre-148 sources, where it also passes. Accrued history was NOT regenerated (append-only, AD-8), so the next run will trip StrategyIdentityGuard once per strategy NAME with the old stamp recorded at <outRoot>/scoring-configs/strategies/{name}.json — that is the correct, visible consequence of a deliberate identity change: delete that per-name record to acknowledge it, or add a new strategy name if the pre- and post-148 cohorts must stay separable. Spec 148 also gave the spec-139 REPLAY runner the provenance every forward pass already had: it now writes each strategy's effective config (so a replayed snapshot's stamp dereferences to the weights that produced it instead of to nothing) and runs StrategyIdentityGuard first; that is a provenance record, not a scoring mutation — replay still mutates no signal/evidence store, never writes the live scores directory, and replay-subset-of-forward still holds field for field. A re-replay under an ALREADY-USED label now WARNS loudly (once per label+strategy, with the count of as-of points it replaced) instead of silently overwriting a possibly-already-ranked series; use a new Radar:Replay:Label to keep both. STRATEGIES + NOTEDNESS CURVE (2026-07-27, after spec 149) - this profile declares five COMPOSITE strategies scored over ONE collection pass (spec 137) - plus, since spec 154, the three 'baseline-' controls described at the end of this comment, eight entries in total when written — ELEVEN since 2026-08-29 (spec 157 added disclosure-led-v11 + disclosure-led-v10-control; the 2026-08-29 data calls added default-noattn); the Strategies array below is the owner of the count. 'default' is the primary and is declared EXPLICITLY because naming any strategy stops the single-strategy synthesis; it is the same {ScoringProfile: default, radar-formula-v8, all types, no channels} as the synthesised one, so it must re-derive the live 60-day AI-ON stamp exactly or StrategyIdentityGuard fails the run at startup rather than silently forking the primary series. THE FINDING THAT SHAPED THE REST: the first live 3-strategy run showed radar-formula-v9 had NO notedness discount at all (v8 references the following-tier/notedness discount in 13 places, v9 in ZERO), so the v9 strategies nearly INVERTED the v8 primary at the extremes - CAT ranked 43rd of 43 under 'default' and 1st under 'filings-led', CVX 42nd vs 2nd, while GTY (v8's top pick, small and under-followed) fell to 18th/33rd. A v9 strategy was ranking on raw channel activity, roughly a size proxy and close to the inverse of Radar's purpose; a gap in spec 146, not a defect in it. Spec 149 gave v9 the discount through the SAME ScoringWeights knobs v8 uses and added inline per-strategy 'Weights' overrides (merged code defaults -> ScoringProfile -> inline, last wins; unknown keys fail fast at startup naming the strategy; two strategies differing only in one inline weight get different fingerprints automatically). That turns the discount from an assumption into an EXPERIMENT, which is why three strategies here hold their channels IDENTICAL and vary only notedness: 'filings-led' (full discount, the spec-149 default), 'filings-led-halfnoted' (both discount weights 0.5) and 'filings-led-nonoted' (both 0.0 - deliberately the pre-149 behaviour, kept as the CONTROL). One variable, three points, so any difference in the spec-140 leaderboard is attributable rather than guessed. This is the question the arc was built to answer: does Radar's core thesis - that under-followed companies are the interesting ones - actually predict anything, or is it costing signal? Nothing else in the system answers it. Deliberately only three points and five composite arms: at 43 companies a wider sweep manufactures a winner by chance, which is why spec 140 ranks IN-sample and reports OUT-of-sample with an honest N. 'narrative-led' stays as the deliberate opposite of filings-led and the control for 'is Radar just tracking press coverage?'. Channel Collectors name the concrete IEvidenceCollector.CollectorName, NOT the 'kind' tokens in the Collectors array above - rss's is 'RssPressReleaseCollector', sec's is 'sec-edgar' - and spec 147 fails startup on an unknown or mis-cased name in every RunMode. Saturation values are UNCALIBRATED first guesses. ACCRUAL LAG: the spec-140 metric judges a score at D against price over (D, D+21], so a strategy added today produces its first rankable number about 21 days later - add strategies early, not when you want the answer. Each strategy scores every company in data/companies.json per run (43 when written, 102 since spec 207), writes its own identity record under scoring-configs/strategies/{name}.json, gets its own strategies/{name}/ score path (spec 137) and its own plain ranked table in the weekly report (spec 150). Under spec 141's immutability convention, retuning one of these is a NEW NAME (filings-led-halfnoted-v2), never an edit in place - editing in place trips StrategyIdentityGuard at startup, by design. RENAMED 2026-07-27 (filings-led -> filings-led-v2, narrative-led -> narrative-led-v2): spec 149 changed how radar-formula-v9 COMPOSES a score (it gained the notedness discount) but did NOT move the v9 fingerprints - filings-led stayed radar-scoring-fp-7ef38390f90b across the change. That is the accepted AD-6 hole spec 149 documented, and its consequence is that the ORIGINAL filings-led/narrative-led series would mix pre- and post-discount scores under ONE identity with nothing in the data distinguishing them - exactly the 'silently continue one series while measuring something else' failure spec 148 closed for the scoring window. The contamination was only ONE run deep (2026-07-27 19:01Z), so renaming was cheap and buys a clean series from the next run; in three weeks it would not have been. The orphaned pre-rename records and snapshots are left in place untouched (append-only, AD-8) - they are simply no longer configured, so the guard ignores them and the weekly report does not render them. NOTE the three filings-led* points start one day apart: -halfnoted/-nonoted were created post-149 and already have one as-of date from the 2026-07-27 score-mode smoke test, while filings-led-v2 starts fresh - negligible over a multi-week series, but it is a real offset.
-  SPEC 154 (2026-07-28) — DUMB BASELINE STRATEGIES: three CONTROLS now join the five strategies described above (they are entries 5-7; the notedness-curve experiment above is untouched by them). Radar:PrimaryStrategy is 'default' and the FIRST entry, { "Name": "default", "ScoringProfile": "default" }, is the strategy this profile has always run — it is asserted (BaselineStrategyWiringTests) to resolve BYTE-IDENTICALLY to the one Radar SYNTHESISES when Radar:Strategies is absent: same profile, same weights, same all-types SignalTypeFilter, same radar-formula-v8, no channels, and above all the SAME ScoringConfigVersion (the 60-day AI-ON stamp — radar-scoring-fp-4da4b5ff6ec9 when spec 154 shipped; it has moved at every identity boundary since, the SPEC 198 note holds the last one recorded here and ScoringConfigFingerprintTests the current one). So writing the list out moves NO stamp, splits NO series and does not trip StrategyIdentityGuard on the primary; the live 'default' series continues uninterrupted. The three trailing entries are CONTROLS, prefixed 'baseline-' so nobody reads one as a candidate strategy in a report or a leaderboard. They exist to be BEATEN — AD-15: a composite strategy may only be described as adding value if it beats EVERY baseline OUT-OF-SAMPLE, on an honest N, by more than the spread between the baselines themselves. WHAT EACH ONE ASKS. (1) 'baseline-earnings-only' — does the latest guidance read ALONE track price? Config-only: the shipped radar-formula-v8 over SignalTypes ['GuidanceChange'], no new code at all. (2) 'baseline-activity-only' — is Radar's score just 'something happened'? ONE collector channel over ALL SEVEN enabled collectors at Weight 1.00, scored by the new radar-baseline-activity-v1 as the saturated plain COUNT of in-window signals: no direction, no notedness, no quality weighting, no recency, no strength, no confidence. (3) 'baseline-media-only' — is Radar just tracking press coverage? The same formula over the press/news collectors only. NOTE the collector names in Channels are the concrete IEvidenceCollector.CollectorName values ('RssPressReleaseCollector', 'sec-edgar', 'sec-form4', 'sec-13dg', 'newssearch', 'usaspending', 'fda'), NOT the Radar:Collectors KIND tokens ('rss', 'sec', 'secform4', ...); matching is EXACT and ordinal, and a typo fails the run at startup before any collection (DefaultRunProfileTests checks every name here against the Worker's kind->name table). WHY radar-baseline-activity-v1 IS NOT radar-formula-v11: the radar-formula-vN sequence is the lineage of Radar's COMPOSITE, each version a considered evolution of the last (AD-6). A control is not an evolution of anything — it is the thing the composite has to beat — and numbering it into that lineage would make it read as the newest and best formula in every leaderboard, fingerprint record and ComponentJson it appears in. It is still a first-class shippable formula (in ScoreFormulaVersions.All, dispatched by RadarScoreFormulaFactory, ConsumesChannels true). THE TWO SATURATION CONSTANTS ARE THE ONLY JUDGEMENT CALL in an otherwise assumption-free control, so they are recorded here rather than left in code. 'activity' Saturation=60: the last measured live 30-day scored window held 2,618 signals across 43 companies (~61 per company), so ~60 puts a typical company near the responsive middle of the x/(x+S) curve instead of pinning everyone at ~1.0 (S=5 would) or flooring everyone near 0 (S=1000 would); at this baseline's Radar:ScoringWindowDays=60 the counts roughly double, landing a typical company around 0.67, still on the responsive part. 'media' Saturation=20: that channel sees a strict SUBSET of the same signals, so reusing 60 would push nearly every company to the bottom of the curve where the baseline cannot discriminate at all; 20 is one third of it, chosen so a company drawing roughly a third of its activity from press/news sits at the same point on its own curve as it does on the whole-activity one. The one-third ratio is a JUDGEMENT, declared as such. Re-tuning either number is a fingerprint-moving change to a NAMED strategy, so per spec 141 (immutable by convention) give the retuned control a NEW NAME — 'baseline-activity-only' -> 'baseline-activity-only-v2' — rather than editing it in place. PER-RUN COST: strategies go from 5 to 8, and scoring is per company per strategy, so a 43-company run went from 215 to 344 scorings (scorings = strategies × companies: 11 × 102 since spec 207) and the weekly report gains three more ranked tables (spec 150 renders one section per strategy once Runtimes.Count > 1). Collection, the AI directional read, extraction, resolution and review still run EXACTLY ONCE (spec 137) — no extra SEC/GDELT/news traffic and no extra AI spend. Keep the control group SMALL and deliberate: every extra arm makes a chance winner more likely, which is the exact trap spec 140's out-of-sample hold-out exists to resist. A BASELINE WINNING IS A FINDING ABOUT RADAR, NOT A RECOMMENDATION — and none of these numbers mean anything until enough price history has accrued for spec 152's partial-window rule to stop (correctly) reporting 'No strategy could be ranked' at the 21-day horizon. DEFERRED, DELIBERATELY NOT SHIPPED: 'baseline-following-tier' (is the score just 'small company'?). A tier-only score traces back to NO contributing evidence — the curated FollowingTier is a company attribute, not evidence — which violates Radar's sacred provenance invariant that a score without evidence is invalid; such snapshots would carry zero score-evidence links and be excluded from the weekly report by the spec-53 rule anyway. The same question is answerable READ-SIDE by relating the existing strategies' ranks to the curated tier, without minting evidence-less snapshots. NOTHING here is hashed beyond each strategy's own identity: no new fingerprint input, no _formula.Version bump, no RuleSetVersion bump, no ScoringWeights change — the four spec-148 pins all stand and the 'default' stamp is unmoved. ⚠ OPERATOR OBLIGATION — READ THIS BEFORE WRITING OR RE-RUNNING AN OVERLAY PROFILE. It was created when Radar:Strategies was FIRST declared explicitly (commit 168125b, the notedness curve above), NOT by spec 154; spec 154 merely widened it from five entries to eight. Now that Radar:Strategies is declared EXPLICITLY above, ANY overlay profile under scripts/run-profiles/ that changes scoring WEIGHTS must ALSO re-point the primary's ScoringProfile, i.e. carry "Strategies": [ { "ScoringProfile": "<its-profile-name>" } ] alongside its Radar:Scoring:Profile / Profiles block. Overlaying Radar:Scoring:Profile ALONE is no longer enough and — this is the dangerous part — it does not fail, it FAILS OPEN. Before Radar:Strategies was declared at all, this profile relied on Radar SYNTHESISING a single primary that inherited the AMBIENT Radar:Scoring:Profile, which is the very key an overlay sets; the experiment's weights therefore reached the primary for free. With the list written out, each entry resolves its OWN ScoringProfile, so entry 0's "default" wins, finds no section under Radar:Scoring:Profiles:default and falls through to the CODE DEFAULTS — while the run still writes to data/experiments/<profile>/, still logs 'run profile: <profile>', still stamps a ScoringConfigVersion (the SAME one as the baseline) and still produces a full set of plausible numbers. An experiment that silently did not run is worse than one that crashed, and it is the same fail-open shape specs 138 and 149 each had to close. NOTE the two things that do NOT need the extra delta: (a) an overlay that changes a GLOBAL pipeline knob rather than a weight — long-window.json sets only Radar:ScoringWindowDays, which is a ScoringOptions value reaching every strategy through the pipeline, so it is correct as written and is pinned as such; and (b) the three 'baseline-' CONTROLS (indices 5-7 when written; 6-8 since default-noattn was inserted at index 1 on 2026-08-29 — count the array, never trust an index quoted in prose), which an experiment should DELIBERATELY leave alone — a control must be the SAME control in the baseline run and in the experiment or the comparison AD-15 rests on is meaningless. run-radar.ps1 merges an overlay ONE FLAT KEY AT A TIME (Radar:A:B:0:C), so a single-field { "ScoringProfile": ... } entry overrides Radar:Strategies:0:ScoringProfile and nothing else — the primary keeps its Name from here and every other index is untouched. low-media.json is the worked example, and DefaultRunProfileTests binds default.json + each overlay exactly as run-radar.ps1 does and asserts the primary's magnitude actually moved (the test that would have caught this).
-  SPEC 157 (2026-07-28) — DIRECTIONAL-ONLY SCORING radar-formula-v11 + THE MATCHED LIVE PAIR: two arms join at indices 8-9 (9-10 since 2026-08-29), under NEW names (spec 141 — never an edit; every earlier arm is mid-accrual and untouched). 'disclosure-led-v11' runs the new radar-formula-v11, whose ONE change from v10 is that a collector channel's SATURATION is computed over DIRECTIONAL-ONLY activity (ScoreSignalMath.DirectionalActivityMass — Positive+Negative mass only) instead of all-signal activity, so adding Neutral signals changes a collector channel's score by EXACTLY zero where v10's saturation rose with neutral volume (AD-16: neutral volume must never amplify a directional read; 87.6% of live signals are Neutral). v11 also REJECTS a breadth channel at startup — spec 158 measured positive-only breadth as structurally ZERO for all 43 companies (Var(positive reach)=0: spec 70 makes every news signal Neutral and first-party RSS is not a third-party publisher), and unfiltered breadth would let a Neutral news item raise OpportunityScore — see docs/158-channel-feasibility-findings.md. 'disclosure-led-v10-control' runs radar-formula-v10 over the IDENTICAL budget, so any ranking difference between the pair is attributable to directional-only vs all-signal collector saturation and to NOTHING else (neither arm declares breadth, so the breadth rejection cannot contribute). THE BUDGET IS PREDECLARED BY SPEC 157 §7 (as amended after spec 158 and the post-merge observability review), not chosen here: ONE collector channel 'filings' = sec-edgar, Weight 1.00, Saturation 3 — spec 158's measured option A, adopted because sec-edgar is configured for 43/43 companies (RSS only 26/43, so option B's 0.40 press share would conflate missing source configuration with valid quiet) and it was the only predeclared budget with any in-window variance (13/43 non-zero, 9 distinct integers; the withdrawn sec-form4/.50+sec-13dg/.30+breadth/.20 budget scored a constant 0 for all 43, which AD-16 §7's degeneracy rule excludes outright). The weak point, recorded: sec-edgar's LEGACY collector attribution is spec-151 inferred-by-elimination and reasoned rather than ground-truth validated; the live arm accrues on forward RECORDED attribution. AD-16's precommitted outcome (AMENDMENT 2026-07-28 in docs/architecture-decisions.md) governs the evaluation: forward attention FLOW over (D, D+21] — distinct third-party publishers with a resolving MediaAttention signal, never a difference of AttentionScore stocks — first eligible primary-screen as-of date 2026-09-26 as first computed — since PINNED to 2026-09-29 (Radar:Efficacy:Comparison below, appsettings.json and the paired-comparison artifact all state it), and the AttentionScore diagnostic keeps its full-set v8 meaning (it is the secondary comparator, so v11 deliberately does NOT narrow it). PER-RUN COST: strategies go from 8 to 10, so a 43-company run went from 344 to 430 scorings (11 × 102 = 1,122 since spec 207); collection, the AI directional read, extraction, resolution and review still run EXACTLY ONCE (spec 137). NOTHING existing is re-stamped: v11 is a new formula class (with its own CompositionRevision rev1, pinned by RadarScoreFormulaV11CompositionGuardTests), v8/v9/v10 are byte-identical (their golden pins pass unmodified), the four spec-148 pins stand, and both new arms mint their own fingerprints at their own strategies/{name}/ paths. v10 and v11 ABSOLUTE scores are not comparable (removing neutral mass from saturation lowers essentially everything); only rankings are — the same caveat v10 carries against v9.
-  SPEC 160 (2026-07-29) — COMPARABILITY-AWARE CONFIDENCE CAP ON THE AI FILING READ: the deterministic EarningsComparabilityScan (cmpscan-v1) now scans every full stripped EX-99.1 body the analyzer reads (BEFORE MaxInputLength truncation); when the release ITSELF declares comparability breaks ('discontinued operations', 'divestiture', 'impairment', 'litigation settlement', 'one-time', 'gain on sale', 'securities loss', 'bad debt recovery', ...) the persisted confidence of the directional GuidanceChange read becomes min(readConfidence, Radar:Ai:ComparabilityConfidenceCap) — default 0.65, a scoring-affecting magnitude living BESIDE MinConfidence/Strength/Novelty (NEVER under the diagnostics-only Radar:Ai:Filings block), 1.0 is the exact off-switch — applied BEFORE the MinConfidence gate, with the capped signal's Reason naming the cap-triggering markers (diagnostic-only matches like 'continuing operations'/'sale of its' are recorded in the cache/debug records but never cap). Motivated by the 2026-07-29 CASS misread: DeepSeek read the Q2-2026 8-K Positive 0.90 on a headline GAAP doubling the release itself declared dirty (prior-year $3.6M securities loss, a $1.8M bad-debt recovery that is a litigation-settlement payment, the TEM divestiture) — the same failure class as the llama3.1 EOSE misread that motivated spec 119, but it survives the model swap, so the fix is deterministic, not a better prompt. The scan structure identity (cmpscan=cmpscan-v1) and the cap magnitude (cmpcap, G29 by value) are folded into the directional descriptor AFTER model=, so EVERY AI-ON fingerprint moved ONCE, DELIBERATELY: this baseline's 60-day LIVE stamp radar-scoring-fp-4da4b5ff6ec9 -> radar-scoring-fp-5ffa8c9e25f0, the 120-day long-window stamp radar-scoring-fp-81e9fab711f8 -> radar-scoring-fp-19fecdb64e3a, and the 30-day code-default unit pin radar-scoring-fp-28226897f97b -> radar-scoring-fp-ebd7d11a58d0 (all three recomputed on the branch; the two live-window values are now ASSERTED by ScoringConfigFingerprintTests.Compute_LiveWindowAiOnStamps_ArePinned rather than transcribed). AI-OFF stamps did NOT move — the descriptor folds only when the AI source is registered — so AI-OFF stays radar-scoring-fp-4eb2fe5d3cdf (60d) / radar-scoring-fp-0a7058d94582 (120d) / radar-scoring-fp-0c46e07b94db (30d code default). NO _formula.Version bump, NO KeywordSignalExtractor.RuleSetVersion bump: cmpscan-v1 is its own parallel rule-STRUCTURE token (change either phrase table => bump it, which is also a cache-policy change). ⚠ OPERATOR STEP ON THE FIRST POST-160 RUN: StrategyIdentityGuard will trip once per strategy NAME (all 10 strategies share the ai= segment) against the old stamp recorded at <outRoot>/scoring-configs/strategies/{name}.json — the guard doing its job on a deliberate identity change (the spec-148 precedent). Delete each per-name record under data/scoring-configs/strategies/ to acknowledge, or give a strategy a NEW name if the pre/post-160 cohorts must stay separable; ScoreSeriesKey keys on the NAME (spec 141), so no score series forks and the efficacy chart just draws its fingerprint-boundary tick. CACHE POLICY: AnalyzedFilingRecord now records the scan policy ('cmpscan-v1;cap=0.65') plus both matched-marker groups. A record with a NULL policy (written pre-160) is a HIT — heal forward, the accrued cache is never mass-invalidated, and legacy uncapped reads keep replaying until their evidence ages out of the 60-day scoring window (fully converged by ≈ first post-160 baseline run + 60 days; per AD-16's 2026-07-29 amendment the disclosure-led-v11 primary screen's first eligible as-of date is therefore the LATER of 2026-09-26 and that convergence date — RECORDED: the boundary was pinned to 2026-09-29, see the Radar:Efficacy:Comparison block below). A record whose non-null policy differs from the current one (the cap was tuned, or cmpscan bumped) is a bounded automatic MISS re-analyzed under the current policy (MaxFilingsPerRun + the 429 breaker bound it like any miss); NO CurrentCacheVersion bump — the null-policy hit rule IS the migration story. What this deliberately does NOT do: correct the accrued CASS 0.90 signal — deleting its cache entry would do nothing (the 8-K evidence is durable, so AddIfNewAsync keeps it out of the candidate list forever, and the spec-142 earliest-created cross-run dedupe would keep the 0.90 copy against any capped twin anyway); it stands and ages out of the 60-day window after as-of ≈ 2026-09-21 (heal forward, specs 142/145). Ai:Filings:PersistReadDebug=true is now ON in this profile (diagnostics-only, gitignored data/ai-debug/, NOT a fingerprint input; one small JSON per analysis attempt) so the per-read hit rate of BOTH marker groups accrues from live runs and any cmpscan-v2 promotion of a diagnostic phrase is argued from hit-rate data instead of intuitions.
-  SPEC 176 (2026-08-22) — EXPLICIT STRATEGY PURPOSE: four arms now declare "Purpose": "Comparator" (baseline-earnings-only, baseline-activity-only, baseline-media-only, disclosure-led-v10-control); every other arm is Research by default and does not write the value redundantly. Purpose is REPORT METADATA ONLY — never a fingerprint input, never a series key — so this edit moves NO stamp, forks NO series and does not trip StrategyIdentityGuard; it drives only the weekly report's 'Live strategy leaders' grouping (Research arms vs Comparators — diagnostic only). A Comparator primary is rejected at startup, and every Radar:Strategies entry is now guarded by a case-insensitive allowlist (StrategyEntryKeys in InfrastructureServiceCollectionExtensions is the owner — seven keys when written, eight since spec 212 added Labels) so an unknown sibling key fails fast instead of being silently ignored.
-  SPEC 197 (2026-08-28) — superseded as THE CURRENT PINS by SPEC 198 (see the SPEC 198 note at the very end of this comment); the values it names below are the PRE-198 pins, kept for reconciling accrued snapshots. It was the third close identity boundary in a row (194, 196, 197). It moves the AI-ON side ONLY, for TWO causes folded into ONE recomputation — both arriving through the already-hashed spec-194 §2 news= segment. (a) §1.3 news-judgment-signal-v1 -> news-judgment-signal-v2: the observation->evidence join replaced its title-only key with a fail-closed match ladder (exact ordinal article URL + normalized headline + equal publication instant, then exact URL + headline, then the pre-197 unique-headline rule; ambiguity STOPS and never falls through to a weaker key), which changes WHICH judgments can produce a scoring input at all — on the 2026-08-27 baseline 0b48b865-76b8-4485-996c-9b9139b694aa the 9 eligible directional judgments went from 2 materializable to 9, with 0 unresolved remainder — so it is not a silent fix under the v1 token. Accrued v1 signals stay valid, immutable and recognized by the ONE shared classifier; an existing valid v1 id is prior-version occupancy and mints no v2 duplicate. (b) §2.2 news-judgment-prompt-v2/news-judgment-schema-v2 -> v3: the accepted FactId GRAMMAR is part of the result schema (a unique 8-31-character hex prefix of exactly one SUPPLIED representative fact now expands deterministically; everything else fails by named reason), and both versions enter the resolved PRESENTATION COHORT KEY this segment carries. LIVE 60d STAMPS ARE NOW AI-OFF radar-scoring-fp-8daa662a57a6 (UNCHANGED from spec 196) / AI-ON radar-scoring-fp-81a397434756 (120d -Profile long-window: radar-scoring-fp-f610244e23c6 UNCHANGED / radar-scoring-fp-e9d9819a2b41; 30d code-default unit pins radar-scoring-fp-54e845330f96 UNCHANGED / radar-scoring-fp-e7317fd038ac). The three pairs are three CORRECT answers at three windows — do not reconcile them onto one value; match an accrued stamp against the pair for the window that run actually used. THE THREE AI-OFF PINS ARE PROVEN UNCHANGED AND THAT NON-MOVE IS A DELIVERABLE: the AI-OFF descriptor renders news=disabled:legacy-news-inheritance-v1:news-judgment-supersede-v1;, which carries NEITHER the presentation cohort NOR the materializer version, so neither cause can reach it — a disabled pin moving here would indicate scope leakage, not progress. No _formula.Version bump, no KeywordSignalExtractor.RuleSetVersion bump (still radar-keyword-rules-v8), no MediaAttentionCollapse.Version bump (still media-collapse-v2), no supersede/neutralization rule bump, no attention tier edit, no weight edit, no strategy/arm/label/Lead change, and no config VALUE in this profile changed. Spec 197 §3 (moving the two repeated per-strategy-company engine Warnings — unresolved evidence and spec-191 neutralization, ~462 lines on the live baseline — to at most one aggregated Warning per category per scoring pass and per replay invocation, with honestly labelled incidence/company/strategy axes) is transient diagnostic state hashed into NOTHING and would move no pin on its own. EXPECTED ONE-TIME COST: forking the stage-2 cohort key means every candidate company is RE-JUDGED ONCE on the first post-197 run (~19 hosted judge calls at the current candidate count) and the five accrued ValidationFailed attempts (IOSP, LBRT, CAT, CASS, WDFC) are NOT reused — that is the intended effect, and NO budget or retry-count change is requested. OPERATOR ACTION, AND THE ORDER IS LOAD-BEARING: (1) do NOT touch the ignored identity records while a pre-197 baseline is running; (2) after merge and BEFORE the first post-197 baseline, consciously delete or re-record every configured data/scoring-configs/strategies/{name}.json — that path is git-ignored, so it cannot be updated by a PR and must NEVER be fabricated; (3) verify the first post-197 run reports radar-scoring-fp-81a397434756 (this profile is AI-ON at 60 days) before treating later snapshots as the corrected series — SUPERSEDED by spec 198 (done). If step 2 is missed, StrategyIdentityGuard runs as the FIRST statement of the run and WILL THROW, naming the strategy and both fingerprints — that halt is CORRECT and must not be bypassed. THE DISCONTINUITY, STATED PRECISELY: post-194/v1 scores fail closed correctly but MATERIALLY UNDER-ADMIT grounded judgments because the title-only join rejected stronger exact identity; post-197/v2 scores admit only citations resolved by the stronger deterministic ladder; history is preserved and never regenerated, rewritten or backfilled (AD-8/AD-1), and the pre-197 sparse-join segment must NOT be presented as equivalent judgment coverage when interpreting news-direction efficacy.
-  SPEC 196 (2026-08-28) — THE THIRD SCORING-IDENTITY MOVE IN AS MANY WEEKS. Its AI-OFF pins are still the current ones; its AI-ON pins were superseded by SPEC 197 above (the AI-OFF side did not move again, deliberately). The attention publisher TIER MAP (the attnDesc hashed field) changed twice over in one field: UnknownWeight was INVERTED 0.25 -> 0.1 (the Mill weight — an explicit entry is now required to count as NOTICE rather than to be DISCOUNTED), and the map gained the four-tier policy Wire 0.05 / Mill 0.1 / Platform 0.3 / Genuine 1.0 with ~50 publishers classified by the sampled audit committed at docs/cohorts/attention-publisher-audit-v1.md (membership only; the weights are the spec's decision). Measured cause: over the live 60-day corpus at the pinned instant 2026-08-27T21:42:45.4943606Z, 50.1% of 2,865 observations were UNCLASSIFIED and therefore weighted 0.25 — two and a half times a Mill publisher — while GENUINE notice was 0.5%, so Attention was measuring aggregator database coverage rather than market notice (mean 73.4, 53 of 75 companies in 70-89: a near-uniform tax, not a discriminator). IT SET LIVE 60d AI-OFF radar-scoring-fp-8daa662a57a6 / AI-ON radar-scoring-fp-65eb592d0354 (120d -Profile long-window: radar-scoring-fp-f610244e23c6 / radar-scoring-fp-a89b6d9ad0a5; 30d code-default unit pins radar-scoring-fp-54e845330f96 / radar-scoring-fp-420b31ba0753). The three AI-OFF values still stand; the three AI-ON values are HISTORY as of spec 197 — see the SPEC 197 entry above for the current ones. The three pairs are three CORRECT answers at three windows — do not reconcile them onto one value; match an accrued stamp against the pair for the window that run actually used. No _formula.Version bump, no KeywordSignalExtractor.RuleSetVersion bump (still radar-keyword-rules-v8), no MediaAttentionCollapse.Version bump (still media-collapse-v2), no weight edit, no strategy/arm/label/Lead change, and the CanonicalDescriptor SHAPE is unchanged — tier NAMES are deliberately NOT hashed, so a rename with identical weights and membership re-stamps nothing. THE ATTENTION REGIME BEFORE THIS PIN IS NOT COMPARABLE WITH THE ONE AFTER IT: accrued snapshots keep their old attention values (history is NOT regenerated, rewritten or backfilled — AD-8/AD-1, the spec-148 precedent), and they were computed against a map under which half the observed volume outranked a content mill. OPERATOR ACTION, AND THE ORDER IS LOAD-BEARING: (1) do NOT touch the ignored identity records while a pre-196 baseline is running; (2) after merge and BEFORE the first post-196 baseline, consciously delete or re-record every configured data/scoring-configs/strategies/{name}.json — that path is git-ignored, so it cannot be updated by a PR and must never be fabricated; (3) verify the first post-196 run reports radar-scoring-fp-65eb592d0354 (this profile is AI-ON at 60 days) before treating later snapshots as the corrected series — SUPERSEDED: after spec 197 the value to verify is radar-scoring-fp-81a397434756; the ordered procedure itself is unchanged and must be repeated for the 197 boundary. If step 2 is missed, StrategyIdentityGuard runs as the FIRST statement of the run and WILL THROW, naming the strategy and both fingerprints — that halt is CORRECT and must not be bypassed. Spec 196 also adds a per-run CAPTURE-FLOW diagnostic (attention-publisher-coverage-v1) on the news-observation batch record and one aggregated log line per run reporting tier shares and the top-10 unclassified publishers; it is for curating the map and is NEVER the AttentionScore input, and NewsObservationBatch.SchemaVersion is unchanged. Explicitly NOT done by spec 196: the discount weights (OpportunityAttentionDivisor, OpportunityAttentionDiscountWeight, FollowingTierDiscountWeight) and the curated per-company FollowingTier are untouched, collection is untouched, and no history is rewritten. SPEC 194 §2 (2026-08-27) — THE FINAL POST-194 PINS. §2 folds the NEWS-READ IDENTITY into SignalSourceDescriptor.CanonicalDescriptor() as a news=...; segment appended AFTER rules= and the optional ai=, closing the recorded AD-10 hole: judgment off/on, the judge MODEL, the prospectively designated presentation cohort, the news-judgment-signal-v1 materializer identity, the trajectory->direction mapping with every strength constant, the legacy-inheritance neutralization version and the judgment-signal supersede version were ALL hashed into nothing, so a judge-model change and a judgment on/off change were invisible to StrategyIdentityGuard and ScoreSeriesKey pooled both cohorts into one series. Cost controls (API keys, call budgets, retry caps) are deliberately NOT folded in. The segment is UNCONDITIONAL — a disabled judgment renders news=disabled:...; rather than nothing — so the AI-OFF pins move too. LIVE 60d STAMPS ARE NOW AI-OFF radar-scoring-fp-2cbbd056ffe5 / AI-ON radar-scoring-fp-b9543f441717 (120d: radar-scoring-fp-f68e6481b136 / radar-scoring-fp-901129153cd1; 30d code-default unit pins radar-scoring-fp-5036d7f73af3 / radar-scoring-fp-5ef6508adc5d). SPEC 194 MOVED THE PINS TWICE ON ITS OWN BRANCH — once for media-collapse-v2 (§1.5) and once for this segment (§2) — so the §1.5 values below are already history. TWO INTENTIONAL SCORING-IDENTITY MOVES IN THE SAME WEEK (spec 191 v6->v7, then spec 194 v7->v8 + media-collapse-v2 + the news segment), and therefore THREE semantic regimes with two close discontinuities: pre-191 Neutral news, spec-191 inherited direction (KNOWN DEFECTIVE — not a valid control cohort, do not pool it across the boundary), and post-194 grounded judgment signals. History is NOT regenerated, rewritten or backfilled. OPERATOR ACTION, AND THE ORDER IS LOAD-BEARING: (1) do NOT touch the ignored identity records while a pre-194 baseline is running; (2) after merge and BEFORE the first post-194 baseline, consciously delete or re-record every configured data/scoring-configs/strategies/{name}.json — that path is git-ignored, so it cannot be updated by a PR and must never be fabricated; (3) verify the first post-194 run reports radar-scoring-fp-b9543f441717 (this profile is AI-ON at 60 days) before treating later snapshots as the corrected series — SUPERSEDED (done; the current value is the SPEC 198 note's). If step 2 is missed, StrategyIdentityGuard runs as the FIRST statement of the run and WILL THROW, naming the strategy and both fingerprints — that halt is CORRECT and must not be bypassed. NOTE for a score/replay pass: since §2 the judge/typing reader lists and the presentation-cohort designation are read and validated in EVERY run mode (the spec-147 precedent — that designation IS the recorded identity), so a misconfigured cohort now fails startup there too. SPEC 194 §1.3/§1.5 (2026-08-27) — the pins moved for MediaAttentionCollapse.Version media-collapse-v1 -> media-collapse-v2; its 60d pair AI-OFF radar-scoring-fp-61891b37e429 / AI-ON radar-scoring-fp-162df0f4c62b (120d radar-scoring-fp-f160ee8faaa6 / radar-scoring-fp-b8ce14dea17a; 30d radar-scoring-fp-a47076995bf5 / radar-scoring-fp-fce77b299c76) is HISTORY, superseded by §2 above. WHAT CHANGED: §1.5's media-collapse-v2 keeps v1's greedy event-window BOUNDARIES byte-for-byte and changes only which real member of a completed bucket represents it — a structurally valid news-judgment-signal-v1 direction now outranks an earlier ordinary Neutral member, so a grounded read can no longer be de-noised away by an unread duplicate (an all-ordinary bucket still yields v1's exact result). §1.3's NewsJudgmentSignalSupersede (news-judgment-supersede-v1) makes the judgment-derived signal REPLACE the ordinary attention event over the same article evidence in BOTH the current and the previous/velocity window, so activity does not grow merely because a judgment exists. EVERY VALUE QUOTED BELOW IS HISTORY, NOT A LIVE STAMP — including the spec-194 §1.1 pair AI-OFF radar-scoring-fp-06e4781f86bb / AI-ON radar-scoring-fp-7a4cd9d409ed (120d radar-scoring-fp-5cb9dc71f309 / radar-scoring-fp-759835b624ca; 30d radar-scoring-fp-023b1af1e3d4 / radar-scoring-fp-ef9104b7b2b9). SPEC 194 §1.2/§1.4 (2026-08-27) — WHAT REPLACED THE WITHDRAWN READ, AND WHAT HAPPENS TO WHAT IT WROTE. §1.2: news direction is now minted by ONE GROUNDED judgment-DERIVED signal (news-judgment-signal-v1) per ELIGIBLE presentation-cohort judgment — Judged, directional trajectory (Improving -> Positive, Deteriorating -> Negative; Mixed/Unknown mint nothing, which is the system working), citing at least one fact whose source observation joins exactly one news evidence item for the SAME company, with a cited excerpt verified verbatim in that evidence — and it is anchored to the evidence the judgment ACTUALLY CITED, never to the company's latest article, which is the whole correction. Its id is a pure function of the materializer version and the judgment id, so one judgment can never mint a second signal (idempotent across re-runs and crashes); a failed durable write is COUNTED, never reported as materialized. OPERATIONAL CONSEQUENCE, DELIBERATE AND NEVER BACKDATED: materialization runs AFTER the judgment, which runs after the pipeline, so a judgment produced by THIS run becomes score-visible only on a LATER run — a ONE-RUN LAG. The signal's knowledge time is when Radar actually had the judgment, which is what keeps spec 136's point-in-time predicate and spec 139's replay honest; do not expect the first post-194 run's news direction to appear in that same run's scores. §1.4: the accrued spec-191 directional signals are neutralized on read — see MEASURED CAVEAT below. SPEC 194 PART 1 (2026-08-26) — SPEC 191'S DIRECTIONAL NEWS READ IS WITHDRAWN AND THE PINS MOVED AGAIN (rules v7 -> v8). EVERY v7 VALUE QUOTED BELOW IS HISTORY, NOT A LIVE STAMP. Spec 191 wired the read at the WRONG lifecycle seam: NewsDirectionalReadSource ran at extraction, before the current run's judge, so it paired THIS article's observation with the company's LATEST judgment without ever checking that the judgment cited the article — one verdict was inherited by every later headline, multiplying one call into N units of directional mass. The NewsArticle branch is therefore back to the pre-191 Neutral MediaAttention event, so THE EXTRACTOR mints no directional news signal at all — since §1.2 (above) the ONLY producer of a directional news signal is the judgment-derived materializer. MEASURED CAVEAT: the 24 v7 directional signals already written to data/signals/ (16 of them inside the live 60d window, from the 2026-08-26 22:53 baseline) stay untouched ON DISK (AD-8) and, since spec 194 §1.4 SHIPPED, are FAILED CLOSED ON READ by the legacy-inheritance neutralization (legacy-news-inheritance-v1): at scoring-assembly time each one is admitted with the exact pre-191 Neutral media-attention direction and strength, in BOTH the current and the previous/velocity window, counted per kind, with the substitution stated on the contribution reason so a score never silently disagrees with the persisted record it cites. Nothing is rewritten, deleted or backfilled — the persisted signals keep their inherited direction as history, and AddIfNewAsync rejects already-seen evidence, so those articles are never re-extracted at all. A malformed judgment-signal envelope fails closed the same way, counted on its OWN axis (a broken writer and a known-defective retired one are different facts). One verdict inherited by several headlines is visible in that cohort (one company holds 5 Negative from a single judgment). It is NOT a valid control cohort. Accrued v7 directional signals stay on disk untouched (AD-8) and are NOT a valid control cohort. Historical spec-191 note follows.
-  SPEC 191 (2026-08-26) — superseded, v6 -> v7: KeywordSignalExtractor.RuleSetVersion bumps radar-keyword-rules-v6 -> v7. The NewsArticle branch is no longer unconditionally Neutral MediaAttention — when Radar has actually READ the article (its evidence joins an archived news observation whose company carries a Judged stage-2 news judgment from the PROSPECTIVELY DESIGNATED presentation cohort, created at or before the run instant) the signal carries that judgment's DIRECTION (Improving -> Positive, Deteriorating -> Negative; Mixed/Unknown/unjoined/unread stay Neutral) and a Strength scaled 4..8 by the judge's finding count and typing completeness, plus mandatory provenance metadata (newsJudgmentId, newsJudgmentCohortKey, newsObservationId). Measured motivation: over a 4,000-signal sample of 2026/08 signals news was 98.4% Neutral and 96.75% MediaAttention, i.e. scored as VOLUME. Unlike the specs 127/129/130 rule-group bumps this one CHANGES SCORES on this baseline (Typing and Judgment are both Enabled below), so the score series takes a DISCONTINUITY: history is deliberately NOT regenerated, rewritten or backfilled (AD-8/AD-1). Every LIVE stamp this profile can produce moved: AI-ON 60d (what this baseline stamps) radar-scoring-fp-5ffa8c9e25f0 -> radar-scoring-fp-3670cdb74652; AI-OFF 60d radar-scoring-fp-4eb2fe5d3cdf -> radar-scoring-fp-58c289cd0113; -Profile long-window 120d AI-ON radar-scoring-fp-19fecdb64e3a -> radar-scoring-fp-c9fe86a19073 and AI-OFF radar-scoring-fp-0a7058d94582 -> radar-scoring-fp-5d89d6ce1668; the 30-day CODE-DEFAULT unit pins (a window the Worker never uses) AI-ON radar-scoring-fp-ebd7d11a58d0 -> radar-scoring-fp-4d1cd1a1528c and AI-OFF radar-scoring-fp-0c46e07b94db -> radar-scoring-fp-be417df3b731. All six recomputed on the branch; the two live-window AI-ON values stay ASSERTED by ScoringConfigFingerprintTests.Compute_LiveWindowAiOnStamps_ArePinned. NO _formula.Version bump (radar-formula-v8 stands), no weight edit, no strategy renamed, no arm added, no Lead change. OPERATOR ACTION REQUIRED BEFORE THE NEXT RUN: StrategyIdentityGuard compares each strategy's computed fingerprint against data/scoring-configs/strategies/{name}.json and will THROW naming the strategy and both fingerprints — that path is gitignored, so the records cannot ship in the branch. Delete (or re-record) data/scoring-configs/strategies/*.json for every configured strategy, consciously, so the guard passes for the INTENDED reason rather than being bypassed. The observation<->evidence join behind all this is derived on read and NEVER persisted (spec 151's precedent): keyed on the shared fact-layer statement normalization over (company, headline), fail-closed on a blank key, a null company, zero matches, two-or-more matching evidence items and a headline claimed by two companies, with joined / unjoined-no-match / unjoined-ambiguous counted and logged once per run.
-  SPEC 198 (2026-08-29) — THE CURRENT PINS, AND THE SIXTH SCORING-IDENTITY BOUNDARY IN THREE WEEKS (191, 194 §1.5, 194 §2, 196, 197, 198). Cause: the NEWS FEED QUERY's recency window is now a hashed ScoringConfigVersion input. Radar:News:RecencyWindowDays (declared explicitly below, default 7) appends a 'when:{n}d' term to the Google News RSS search PHRASE, and SignalSourceDescriptor.CanonicalDescriptor() now carries it as a trailing 'newsquery=7d;' segment AFTER the spec-194 §2 news= segment. WHY IT IS HASHED: the query decides WHICH evidence exists at all, so it changes AttentionReach, OpportunityScore and every rank; it was hashed into NOTHING, so narrowing it would have moved every score while StrategyIdentityGuard stayed silent and ScoreSeriesKey drew both cohorts as one continuous line — the same comparability hole spec 194 §2 closed for the judgment read. MEASURED BASIS, verified against the LIVE endpoint on 2026-08-29 for the phrase 'Caterpillar Inc': the unfiltered query returned 100 items with the oldest dated 24 Jun 2026, while the same query carrying when:7d returned 66 with the oldest dated 23 Aug 2026 — so the undocumented operator demonstrably BOUNDS the response and does not silently degrade to unfiltered. WHY 7 AND NOT 1 OR 2: the baseline runs daily, so a 1-2 day window has no margin and one missed night would open a permanent gap (a skipped article never reappears in a narrower window); seven tolerates several consecutive failures while still cutting a 100-item response to a handful, and the redundancy is free because cross-run dedupe already discards it. A company's FIRST collection stays UNFILTERED (§2), decided from PERSISTED STATE (does the observation archive already hold a record for it?) and never from a clock, so seeding still acquires back history; the window used and the unfiltered-first-collection feed count are recorded on every CollectorCompanyCoverage row. LIVE 60d STAMPS AFTER SPEC 198 WERE AI-OFF radar-scoring-fp-0ff442a14c1b / AI-ON radar-scoring-fp-11240da5aeb0 (120d -Profile long-window: radar-scoring-fp-adf455313d35 / radar-scoring-fp-7eece22968a4; 30d code-default unit pins radar-scoring-fp-56c8e882beed / radar-scoring-fp-7d2b0cf537c4). SPECS 214-216 (214+215 merged back-to-back 2026-09-08 AND TOOK THEIR OPERATOR STEP THAT DAY; 216 is a SECOND, SEPARATE identity move owing a SECOND operator step) MOVED THE THREE AI-ON VALUES AGAIN, AI-OFF UNCHANGED — AND THE CAUSES DO NOT ALL RIDE news=. Inside the news= segment (through the judgment cohort key and the materializer identity): comparison-basis-v1 and news-judgment-signal-v3 (spec 214), news-judgment-schema-v4 (spec 215), and news-judgment-prompt-v6 + references=reference-projection-v2 (spec 216 §1, superseding 214's prompt v4, 215's prompt v5 and 215's reference-projection-v1 IN PLACE — and ONE of those intermediate compositions DID stamp a live run: the 214-only 60d value radar-scoring-fp-241097438af8 never did, because 214 and 215 merged back-to-back, but the 214+215 operator step WAS performed on 2026-09-08 and the post-215 composition (comparison-basis-v1, prompt v5, schema v4, reference-projection-v1, with the reported-metrics ledger DISABLED by 766c925 so ZERO references were projected) stamped radar-scoring-fp-8590412af27c across 102 companies, strategy 'default', earliest score 2026-09-08T21:46:22.311Z. That HISTORICAL one-run cohort is accrued and reconcilable — quoted here as history, exactly as 11240da5aeb0 is, never as a current value). OUTSIDE it: spec 216 §5 appends rm=reported-metrics-v2 to the DIRECTIONAL-FILING ai= descriptor, NOT to news= — enabling metric extraction changes the FILING-ANALYSIS prompt and the verification policy, not the news read (the spec-216 §5 bullet in this file is the authority for that split; do not read this paragraph as putting rm= in news=). AI-OFF cannot move for two independent reasons: with the AI read off there is no ai= segment to carry rm=, and no news=enabled: fields to carry the cohort key. The CURRENT AI-ON values are NOT transcribed here — read them from ScoringConfigFingerprintTests (Compute_LiveWindowAiOnStamps_ArePinned for 60d/120d, Compute_AiOnDefault_MatchesPinnedFingerprint for 30d); the AI-OFF values above still stand and are asserted by Compute_LiveWindowAiOffStamps_ArePinned. Take the operator step AGAIN after 216 merges — a SECOND step, not a shared one; the 214+215 step was already performed on 2026-09-08. UNLIKE SPEC 197 THIS MOVES BOTH SIDES, and that is the point: the segment is not judgment-gated, so an unchanged AI-OFF pin would mean the window is not actually hashed. The three pairs are three CORRECT answers at three windows — do not reconcile them onto one value; match an accrued stamp against the pair for the window that run actually used. ADDITIVITY IS PROVEN, NOT ASSUMED: a window of 0 renders the EMPTY segment, and ScoringConfigFingerprintTests.Compute_NewsQueryWindowDisabled_ReproducesNoNewsQueryPins (named …ReproducesPost197Pins until spec 214) asserted that all six post-197 values (54e845330f96/e7317fd038ac, 8daa662a57a6/81a397434756, f610244e23c6/e9d9819a2b41) are then reproduced EXACTLY — so this move is attributable to the window and to nothing else. Each of the six new values was derived TWICE: through ScoringConfigFingerprint.Compute over the real descriptors, and outside .NET by rebuilding the canonical string with no trailing newline and hashing it with sha256sum. No _formula.Version bump, no KeywordSignalExtractor.RuleSetVersion bump (still radar-keyword-rules-v8), no MediaAttentionCollapse bump (still media-collapse-v2), no supersede/neutralization rule bump, no attention tier edit, no weight edit, no strategy/arm/label/Lead change. Radar:News:MaxRecordsPerCompany stays 25, the absolute 100-item parse ceiling stays, the request count and pacing are unchanged and no response-tail item is admitted (§5). THE COLLECTION REGIME BEFORE THIS BOUNDARY IS NOT COMPARABLE WITH THE ONE AFTER IT: pre-198 runs read an unfiltered feed whose median item was weeks old and spent most of the 25-slot budget re-reading known articles; history is deliberately NOT regenerated, rewritten or backfilled (AD-8/AD-1). OPERATOR ACTION REQUIRED BEFORE THE FIRST POST-198 BASELINE, AND THE ORDER IS LOAD-BEARING: (1) do not touch the ignored identity records while a pre-198 baseline is running; (2) after merge and BEFORE the first post-198 baseline, consciously delete or re-record every configured data/scoring-configs/strategies/{name}.json — that path is gitignored, so those records cannot ride in a PR and MUST NEVER be fabricated; (3) verify the first run reports radar-scoring-fp-11240da5aeb0 before treating later snapshots as the corrected series — DONE 2026-08-29 (run 1 stamped it) and the value stamped live through 2026-09-07; specs 199-213 moved no pin; SPECS 214-216 ARE the next comparability boundary (AI-ON only), but that is a deliberate comparability CALL over TWO identity discontinuities, not one shared move: the 214+215 step was performed 2026-09-08 and radar-scoring-fp-8590412af27c stamped one live run (102 companies, ledger off so zero references were projected), and SPEC 216 OWES A SECOND ordered operator step before the first post-216 baseline, verifying against ScoringConfigFingerprintTests rather than any value quoted here. If step 2 is missed, StrategyIdentityGuard halts the run before collection — that halt is CORRECT and must not be bypassed. LIVE DISTRIBUTION: the two read-only env-gated harnesses are NewsRecencyWindowLiveMeasurementTests (RADAR_NEWS_RECENCY_LIVE_DATA_ROOT — both arms against the live endpoint: item counts, AGE distributions, projected slot usage and the new-vs-deduped split against the 2026-08-28 baseline's 234 new / 1,370 cross-run deduped) and NewsRecencyWindowCounterfactualTests (RADAR_NEWS_RECENCY_COUNTERFACTUAL_DATA_ROOT — the paired read-only projection at one pinned as-of instant: publisher breadth plus the AttentionScore/OpportunityScore distributions before and after). Neither admits, maps or persists anything.
-  SPEC 212 (2026-09-07) LABEL LINES: the Lead arm disclosure-led-v11 carries Labels { Investigate 20, Watch 15 } below, PINNED by that spec — Watch 15 reproduces the v8 primary's historical 2.1% >= 40 prevalence on v11's accrued distribution (measured 15 by scripts/audit-label-thresholds.ps1 -Strategy disclosure-led-v11 -MatchPrevalenceOf default); Investigate 20 is the top ~0.3% of that distribution (observed max 21) — v8 has no >= 60 prevalence to match, so 20 is a stated workload JUDGEMENT about how many names a morning should escalate. These are fixed operating (triage) thresholds chosen by prevalence, connected to no outcome and saying nothing about whether a value is a strong opportunity; NOT a fingerprint input (report layer only; no pin moved). No other arm sets Labels: default is its own 60/40 by definition (LabelThresholds.Default applies only when no operating call is declared), comparators cannot lead, and a Lead call on any other arm must add that arm's Labels in the same change — the Worker refuses to build the report otherwise.

### Radar:News:_comment (moved verbatim by spec 213)

SPEC 198 — the Google News RSS news-attention collector's recency window, declared EXPLICITLY here (matching the code default and appsettings.json) so this profile RECORDS the posture it runs rather than inheriting it, and so an experiment overlay that redeclares Radar:News cannot silently restore the pre-198 unfiltered feed. RecencyWindowDays appends a 'when:{n}d' term to the search phrase: measured live on 2026-08-29 for 'Caterpillar Inc', the unfiltered query returned 100 items (oldest 24 Jun 2026) and when:7d returned 66 (oldest 23 Aug 2026). 0 disables it and reproduces the pre-198 URL byte-for-byte; a negative value fails startup naming this key. A company's FIRST collection stays unfiltered — decided from the persisted observation archive, never from a clock — so seeding still acquires back history. IT IS A HASHED ScoringConfigVersion INPUT (NewsQueryScoringIdentity): at this baseline's Radar:ScoringWindowDays=60 the AI-ON stamp was radar-scoring-fp-11240da5aeb0 through spec 213 (AI-OFF radar-scoring-fp-0ff442a14c1b, unchanged by spec 214); spec 214 moved the AI-ON value — see ScoringConfigFingerprintTests.Compute_LiveWindowAiOnStamps_ArePinned for the current one. BEFORE THE FIRST POST-198 RUN, delete or re-record every configured data/scoring-configs/strategies/{name}.json (that path is gitignored) or StrategyIdentityGuard halts the run before collection — correctly. MaxRecordsPerCompany (25), the absolute 100-item parse ceiling, the request count and the pacing are deliberately UNCHANGED (§5), and Radar:Gdelt:MaxRecordsPerCompany is a DIFFERENT collector's knob.

### Radar:NewsResearch:_comment2 (moved verbatim by spec 213)

SPECS 181+185 PROMOTED INTO THE BASELINE (maintainer decision, 2026-08-23): the stage-1 news event-typing pass and the stage-2 direction judge now run on every unfiltered full baseline run, moved up from the news-typing/news-judgment experiment overlays (which remain as overlays and are now redundant against this baseline). Typing: hosted DeepSeek ONLY, per the spec-181 §1 gate measurement (hosted 0.0% citation-drop vs llama3.1 19.2% with near-zero cross-reader agreement — the local model is not a viable solo typing reader; since spec 187 §8 the news-risk Shadow above no longer schedules the local reader either, so the baseline runs ONE hosted DeepSeek cohort per stage — the accrued ollama:llama3.1 shadow cohort stays on disk as historical provenance and is never pooled with it). Judgment: reads ONLY canonical fact families, challenge-only findings + BusinessTrajectory axis, judgments persisted beside the spec-179 shadow assessments — side by side, never pooled, no merged verdict; the weekly report's leaders rows gain the mandatory 3-state semantic-read marker fed by the PROSPECTIVELY designated presentation cohort below (judge deepinfra-deepseek over extractor deepinfra-deepseek — never switched after seeing results). NO score, label, strategy, fingerprint, snapshot field or report RANK changes; the marker is display metadata; nothing here is hashed. THE SPEC-181 §3 HUMAN AUDIT IS WAIVED (maintainer decision, 2026-08-23, applying the decide-dont-fence-sit rule): taxonomy v1 stands as declared and the output is LIVE, not exploratory — the revision mechanism (news-event-taxonomy-v2, cohorts never pool across versions) plus the per-run MECHANICAL validation numbers the pipeline already records (citation-drop rate and reasons, ValidationFailed counts, per-cohort completeness) are the quality control; revise to v2 if those numbers say so, do not wait for a row-by-row human pass. SPEC 187 (2026-08-24) — what the FIRST live typing+judgment run (976d0f20, 2026-08-24) changed here. The 200 hosted calls/reader/run are now split across three lanes rather than one queue: bounded FIFO retries (MaxRetryTypingsPerRun 25, oldest last-attempt first), then up to MaxCandidateTypingsPerRun 100 first attempts on the companies THIS run is about to judge (round-robin over the ONE shared candidate plan, so typing-prioritized == judged by construction — the live run judged 18 companies whose motivating headlines were still untyped), then every remaining slot (≥75 by the enforced cross-field rule 100+25<200 — the THEN-CURRENT posture, SUPERSEDED: see the SPEC 189 paragraph below for today's 150+25<350 ⇒ ≥175) on the global queue, window-new-first then backlog oldest-first, so the legacy backlog keeps draining. Each hosted typing call now wins a DURABLE PRE-CALL reservation (§3), so a crash or a failed outcome write costs an attempt instead of costing nothing, and MaxTypingAttempts 3 is a bound on CALLS rather than on persisted outcomes. SPEC 188 (2026-08-25) CORRECTS THE PREVIOUS WORDING HERE: the two bounds are NOT the same mechanism. Typing's MaxTypingAttempts 3 is a bound on PROVIDER CALLS precisely because every call wins that durable pre-call reservation. Judgment's MaxJudgmentAttempts 3 is separately DERIVED from durably recorded call-producing outcomes plus same-run idempotency, and deliberately has NO pre-call ledger — so a crash or a failed outcome write between the call and its persistence can spend an unrecorded judgment call. That asymmetry is accepted honestly: judgment is one serial call per company per run, while typing can spend hundreds. Either way, an exhausted company renders '? unassessed (retries-exhausted)' rather than a fabricated verdict. MIGRATION: NONE REQUIRED — no operator deletion or reset. The first post-187 run naturally creates the news-judgment-v2 cohort (prompt-v2 + schema-v2 fork the stage-2 cohort key) and re-judges the current candidates once; stage-1 typing stays in its EXISTING cohort because selection priority and attempt accounting do not touch the extractor prompt, schema or taxonomy; and every existing fact, family, typing, judgment and assessment on disk stays immutable (AD-8). SPEC 189 (2026-08-25) RAISES THE LIVE TYPING BUDGET TO 350 CALLS / 150 CANDIDATE / 25 RETRY, and DECLARES ALL FIVE TYPING LIMITS EXPLICITLY HERE (MaxNewTypingsPerRun 350, LookbackDays 30, MaxTypingAttempts 3, MaxRetryTypingsPerRun 25, MaxCandidateTypingsPerRun 150) — previously only the first two were written down and the two lane widths were inherited from the code defaults, so an experiment overlay could silently restore the old 200/100 posture. THE MEASURED BASIS is the 2026-08-24 baseline run a180298d: the 30-day window held 2,411 observations of which only 377 were typed (15.6%) and 2,017 were still eligible/untyped, the run CAPTURED 252 new observations against a 200-call cap (inflow exceeded capacity), and the 200 calls cost 508.6s of serial provider time (mean 2.54s, p95 6.32s, max 32.32s) inside a 58-minute baseline — so another 150 calls is about 6m21s at the observed mean, material but bounded. THE HYPOTHESIS IS EXPLICIT AND FALSIFIABLE: at ~252 observations captured per run and 350 durable completed outcomes, capacity exceeds inflow by roughly 98 observations per run, so a 2,017-observation backlog clears in about 21 runs before retries, validation failures and inflow changes. That is a PREDICTION, not a promise — the three-run review (spec 189 §4) compares actual completed outcomes, inflow and UntypedRemaining against it and names the reason for any material miss. The window is NOT narrowed to flatter the percentage, and nothing auto-tunes: this is one prospectively declared posture whose outcome will be observed. CANDIDATE COVERAGE HAS A MOVING DENOMINATOR: the live candidate set held 626 in-window observations (468 untyped) and the candidate lane only rises 100→150, so companies entering and leaving the nominated set each run can refill the lane and keep 'Complete' rare even while the global backlog drains — continuing candidate incompleteness ALONE is not evidence that the budget is too low, and the review compares retained candidates against entrants and exits separately. The cross-field rule still holds with room to spare (150 + 25 < 350 reserves ≥175 general first-attempt slots, so the legacy backlog keeps draining). Spec 189 also SPLITS the single typing-completeness 'Failed' token: RetryableFailure (a provider/parse/validation, reservation-refusal or unpersisted-outcome failure THIS pass, still eligible for a later retry) versus RetryExhausted (attempts spent — a permanent hole), with exhaustion taking precedence; legacy Failed stays readable and is never newly computed. New judgments stamp news-judgment-v3 for the widened vocabulary while the judge's prompt, result schema, model request and stage-2 cohort key do NOT move (typing completeness is run provenance, never a judge input), and the decomposition artifact forks to news-typing-decomposition-v4 to record the batch's new-observation inflow, AUTHORITATIVE pass-wide reader totals (all three lane selections, actual provider calls, completed outcomes, each failure class, exhaustion and reserved-without-outcome) and per-company retry selections, provider calls and retryable failures. NO score, rank, label, strategy, fingerprint, snapshot field or marker policy moves; no historical artifact is deleted or rewritten.
