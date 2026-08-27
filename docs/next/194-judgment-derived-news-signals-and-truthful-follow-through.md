# Task: Make news direction follow the judgment it came from

## ⚠ STATUS — PART 1 IS SHIPPED; THIS SPEC NOW COVERS THE REMAINDER ONLY

**Shipped 2026-08-27 as PR #198, merged `85c6f5f`** (branch `feature/judgment-derived-news-signals`):

- **§1.1 — DONE.** The `NewsArticle` branch is back to the pre-191 Neutral `MediaAttention` event (proven
  character-identical against the pre-191 source). `INewsDirectionalReadSource`, `NewsDirectionalRead`,
  `NewsDirectionalReadSource` and `NewsDirectionalReadOptions` are deleted, with a reflection guard asserting
  none survives dormant. `NewsObservationEvidenceJoin`, `NewsTrajectorySignalRules` and
  `NewsDirectionalSignalMetadata` are RETAINED for the sections below.
  `KeywordSignalExtractor.RuleSetVersion` is now **`radar-keyword-rules-v8`**.
- **§3 — DONE.** Retryable typing failure is scoped to in-window `(ObservationId, PayloadHash)` membership.
- Live pins are now **60d AI-OFF `radar-scoring-fp-06e4781f86bb` / AI-ON `radar-scoring-fp-7a4cd9d409ed`**
  (30d `023b1af1e3d4`/`ef9104b7b2b9`; 120d `5cb9dc71f309`/`759835b624ca`). The identity records under
  `data/scoring-configs/strategies/` were cleared on 2026-08-27 and re-record at the v8 values.

**REMAINING WORK — this is what a dispatch of this spec must implement: §1.2, §1.3, §1.4, §1.5 and §2.**
Do NOT re-do §1.1 or §3; verify they are present and move on.

**Recommended order, highest value first.** §1.4 is the priority: 16 accrued v7 directional signals sit
inside the live 60-day window and CONTINUE to be scored with an inherited direction, and `AddIfNewAsync`
rejects already-seen evidence so they can never be re-extracted as Neutral. §1.4 is the only thing that
stops that, and it is independent of the materializer. Then §1.2 + §1.3 (news regains direction, properly
grounded), then §2 (closes the AD-10 hole), then §1.5.

If a dispatch cannot complete all of it, ship §1.4 alone as its own reviewed PR rather than leaving a
half-built materializer uncommitted.

## Overview

Specs 177–193 finally gave Radar a grounded company-level `BusinessTrajectory` and then wired it into
scoring. The first code review of the merged path found that spec 191 connected the right two systems at the
wrong lifecycle seam.

The live order is:

1. collect and archive this run's news;
2. extract/store signals and score;
3. type the archived observations;
4. judge the typed facts; and
5. re-render the report marker.

`NewsDirectionalReadSource` runs at step 2. It joins a newly collected article to its observation, then takes
the latest admitted judgment **by company only**. The judgment necessarily predates the new article because
the current run's judge has not run yet. Therefore the article that actually caused Monday's
`Deteriorating` judgment was already stored Neutral on Monday and is never re-extracted; a new, potentially
positive article collected on Tuesday inherits Monday's negative call before Tuesday's judge reads it. The
persisted provenance names Tuesday's observation beside a judgment whose cited facts came from Monday. This
is not merely a one-run lag — it attaches a real judgment to evidence it did not read and multiplies that old
call by however many new headlines arrive.

Two coupled follow-through defects belong in the same correction:

- judgment enablement/model/presentation cohort and the news-trajectory strength rules change signals but
  contribute nothing to `ScoringConfigVersion`;
- a pass-wide stage-1 failure can label an otherwise-complete in-window company `RetryableFailure`, which
  spec 191 now turns into a lower signal strength.

This slice corrects those three scoring defects together. The load-bearing rule is: **a
judgment produces its own grounded signal after the judgment exists; a later article never borrows an older
company verdict.** The scoring identity and completeness input must move with that signal, so they stay in
the same spec. The three independent operational/diagnostic findings from specs 190/193 are split into spec
195 so this correctness fix does not wait on artifact rendering or log plumbing.

### The scoring decision, stated up front

Do **not** patch `NewsDirectionalReadSource` with an observation-membership predicate. Under the current
stage order that would make the cited article Neutral on its first pass, then leave it Neutral forever because
evidence is insert-only and never re-extracted. The correct seam is after stage 2 judgment:

- ordinary news extraction returns to the pre-191 Neutral `MediaAttention` event;
- one validated presentation-cohort judgment may materialize **one judgment-derived directional
  `MediaAttention` signal**, grounded in the facts/observations/evidence that judgment actually cited;
- that signal is available to scoring from the next run onward; and
- deterministic scoring assembly replaces the cited article's Neutral attention signal with the grounded
  directional companion, so activity is not double-counted.

The one-run score lag is explicit and honest: the semantic marker can show the judgment in the run that made
it, while the score can consume it only after the signal has been durably created. Do not backdate the
signal to conceal that lag.

### Live exposure and the fix-forward decision

Spec 191 is already merged and active in the baseline. Until this correction is implemented, **every live
full run can persist inherited directions and score them**. In particular, the scheduled **2026-08-26 22:30
baseline** will produce snapshots under the defective v7 semantics if it starts before spec 194 lands: one
previously admitted company judgment can be attached to every newly collected article until another judgment
replaces it, multiplying one call into N units of directional mass. Preponderance therefore becomes partly a
function of subsequent news volume — the size proxy spec 191 intended to remove, reintroduced through stale
direction.

The decision is fix-forward, not delete/backfill:

- let a pre-194 scheduled run complete rather than mutating code/data as it starts;
- preserve every v7 signal and snapshot as an immutable record of what Radar actually did;
- mark the spec-191 scoring regime as **known-defective and non-comparable** in the lineage; and
- do not use v7-era directional-news snapshots as evidence for post-194 model efficacy or pool them across
  the post-194 boundary when interpreting results. Post-194 evaluation starts at the first snapshot carrying
  the new fingerprint.

This slice does not delete or rewrite the existing efficacy artifacts. The fingerprint and dated lineage
boundary are the durable separator; reports may retain the historical line for audit, but the spec-191
segment is not a valid control cohort for the corrected signal semantics.

## Assignment

Worktree: any

Dependencies: specs 187–193 merged. Every existing evidence, observation, typing, family, judgment, signal,
score and efficacy artifact stays immutable.

Estimated time: ~2–3 days. Do not trim the provenance, identity or legacy-suppression tests to fit the
estimate.

## 1. Replace article inheritance with a judgment-derived signal

### 1.1 Retire the pre-judgment read from ordinary extraction

Remove the `INewsDirectionalReadSource` dependency from `KeywordSignalExtractor` and `CollectionPass`, and
remove the `NewsDirectionalReadSource` registration/implementation once no production caller remains.
`NewsObservationEvidenceJoin` stays: the materializer below reuses it to resolve cited observations back to
evidence.

The `NewsArticle` branch once again emits exactly the pre-191 Neutral `MediaAttention` signal for every new
article: same direction, strength, novelty, confidence, excerpt and ordinary reason. It must never consult a
company-level judgment while extracting an article. Bump `KeywordSignalExtractor.RuleSetVersion`
`radar-keyword-rules-v7` → `radar-keyword-rules-v8`; this is a scoring-rule correction, not a silent rollback.

Keep the spec-191 signal metadata reader long enough to identify accrued v7 directional records (§1.4).
Do not delete or rewrite those files.

### 1.2 Materialize only the judgment that actually owns the provenance

Add one application service, `INewsJudgmentSignalMaterializer`, invoked by `Worker` immediately after
`RunNewsJudgmentAsync` and before the news-risk live artifact is built. It receives the current
`NewsJudgmentRunResult` and the exact `NewsTypingRunResult` instance the judge consumed. It performs no model
call and never re-ranks candidates.

Only a record satisfying **all** of these rules is eligible:

- exact designated presentation cohort, resolved structurally rather than by rendered-name parsing;
- `Status == Judged`;
- `BusinessTrajectory` is `Improving` or `Deteriorating` (`Mixed` and `Unknown` are honest non-directions and
  materialize no directional signal);
- `TrajectoryFactIds` is non-null/non-empty; and
- every trajectory fact id resolves in the matching stage-1 cohort's `FactsById` to its source observation,
  and every distinct source observation resolves through `NewsObservationEvidenceJoin` to exactly one news
  evidence item for the same company.

The last rule is deliberately all-or-nothing. A partially resolvable citation set is not full provenance;
record the named skip and create no signal. Reuse the existing exact-single-match join and its headline
normalization. Do not add a second fuzzy join or persist a side index.

For one eligible judgment, create **one** signal, not one per citation and not one per later article:

- `Id = DeterministicGuid.FromCanonicalString(`
  `"radar:news-judgment-signal:news-judgment-signal-v1:{JudgmentId:D}"` `)`;
- `EvidenceId` is the deterministic primary anchor: the resolved cited evidence with the latest
  `ObservedAtUtc`, then lowest `EvidenceId` on a tie;
- `CompanyId` and company mention come from the judgment record, never a fresh resolver guess;
- `Type = MediaAttention`;
- `Improving → Positive`, `Deteriorating → Negative`;
- strength uses the existing `NewsTrajectorySignalRules` finding-count/typing-completeness mapping;
- novelty/confidence retain spec 191's declared values; and
- the supporting excerpt is the first citation, in the validated fact's persisted order, that passes the
  existing excerpt-in-primary-evidence guard. If none does, record `excerpt-not-in-evidence` and create no
  signal.

The signal's `ObservedAtUtc` is the primary evidence's real publication/collection instant.
`CreatedAtUtc` is the **materialization instant now**, even when the judgment was reused from an earlier run.
Never copy `judgment.CreatedAtUtc` into `signal.CreatedAtUtc`: a reused old judgment did not create a durable
signal in the past, and backdating it would let replay see a signal Radar did not yet have.

Compose metadata through the existing `EvidenceMetadata` envelope, with one versioned definition containing:

- `newsJudgmentSignalVersion = news-judgment-signal-v1`;
- `newsJudgmentId`;
- `newsJudgmentCohortKey`;
- `newsTrajectory`;
- the ordered distinct trajectory fact ids;
- the ordered distinct source observation ids; and
- the ordered distinct resolved evidence ids.

GUID lists use lowercase `D` format, ordinally ordered, with one documented delimiter. Extend the shared
`NewsDirectionalSignalMetadata` helper or replace it with one shared version-aware helper; do not create a
second metadata parser.

Run the candidate through the existing signal validation and deterministic review path, then add the reviewed
signal/review to the same repositories and `FileSignalStore` used by collection. Before review/write, check
the deterministic signal id: an existing signal is `AlreadyMaterialized`, never reviewed or overwritten.
A failed durable write follows spec 193's truthful outcome rules and is counted, not reported as materialized.
No retry queue is added; the next process may safely retry because no durable signal with that id exists.

Return a typed `NewsJudgmentSignalMaterializationSummary` carrying at least eligible, materialized,
already-materialized, validation-rejected, write-failed and the named provenance-skip counts. Attach it as a
trailing nullable member of `NewsJudgmentRunResult` (null = pre-194/not attempted), log one summary, and render
it in the live news-risk artifact. One unexpected company failure must not prevent the remaining judgments
from materializing; cancellation still propagates.

### 1.3 Replace the Neutral signal, do not count the cited article twice

Add a pure `NewsJudgmentSignalSupersede`, following `GuidanceChangeSupersede`'s established shape, and apply it
to **both** current-window `ScoringSignal` pairs and previous-window plain signals before formula/filter
consumption.

For `MediaAttention` signals sharing the same `EvidenceId`:

- a structurally valid `news-judgment-signal-v1` signal beats the ordinary Neutral article signal and any
  accrued spec-191 v7 directional article signal;
- if more than one materialized signal shares the anchor, latest `CreatedAtUtc` wins, then lowest `Signal.Id`;
  and
- without a valid materialized signal, no ordinary signals are removed by this supersede.

Return survivors plus per-winner superseded counts and surface the count beside the existing media-collapse
and guidance-supersede accounting. On the healthy path the materialized signal replaces one attention event
with one grounded attention event; current and previous activity counts do not grow merely because judgment
was added.

### 1.4 Fail closed over already-persisted spec-191 article directions

The accrued v7 signals cannot be deleted or rewritten, but they must not continue asserting that an old
company judgment read their matched article. Add one pure, versioned admission transform before the
supersede/collapse steps:

- a directional `MediaAttention` signal carrying spec-191 judgment metadata but **not**
  `newsJudgmentSignalVersion = news-judgment-signal-v1` is an accrued legacy-inheritance signal;
- unless a v1 materialized companion supersedes it, score it with the exact pre-191 Neutral media-attention
  direction/strength; and
- count suppressed legacy directions per company and state the suppression in contribution/debug provenance,
  so a score never silently uses a different direction from the persisted record.

Do not suppress unrelated future directional signal families by testing only `Direction != Neutral`. Match
the exact legacy metadata shape. A malformed v1 envelope also fails closed to Neutral and is counted
separately.

The transform is read-side only. The original signal, review and file remain byte-identical.

### 1.5 A grounded direction must survive same-event collapse

Bump `MediaAttentionCollapse.Version` `media-collapse-v1` → `media-collapse-v2`. Keep the existing greedy
event-window boundaries unchanged, but choose the representative inside each completed bucket by:

1. valid `news-judgment-signal-v1` direction over an ordinary Neutral media signal;
2. among materialized signals, latest `CreatedAtUtc`, then lowest id; and
3. when no materialized signal exists, the exact v1 earliest-observed/lowest-id rule.

The collapsed count remains exact and the representative remains a real persisted signal. This closes spec
191's recorded direction-blind-collapse gap without widening or shrinking event buckets.

## 2. Put the judgment read in the scoring identity

The static v8 rule bump identifies this implementation, but it cannot distinguish judgment off/on, DeepSeek
from a later Claude cohort, or one presentation cohort from another. Add one pure
`NewsJudgmentScoringIdentity` (name flexible) and fold it into `SignalSourceDescriptor.CanonicalDescriptor()`.

It must be constructible in every scoring-capable mode (`full`, `score`, `replay`) from validated configuration
**without constructing a provider client or issuing a request**. Score-only mode must stamp the same identity
as a full run with the same config.

Canonical identity distinguishes:

- judgment disabled versus enabled;
- the exact resolved presentation cohort key (therefore provider/model + judge prompt/schema and stage-1
  cohort identity);
- `news-judgment-signal-v1` materializer identity;
- the Improving/Deteriorating mapping and every strength constant;
- the legacy-inheritance-neutralization rule version; and
- the judgment-signal supersede version.

Use `DescriptorEscaping`; do not hand-roll delimiter escaping. `media-collapse-v2` is already folded through
`MediaAttentionCollapse.CanonicalDescriptor()` and must not be duplicated inside the news segment.

Assert all of the following:

- judgment off/on produces different `ScoringConfigVersion`;
- changing only model/presentation cohort produces a different version;
- changing a strength constant (through a constructed identity fixture, not by making constants configurable)
  produces a different version;
- full/score/replay with identical effective judgment configuration produce the same version; and
- changing reader API keys, call budgets, retry caps or other cost controls does **not** move it.

Recompute every spec-148 fingerprint pin for the 30/60/120-day AI-off/AI-on fixtures. Update the pin comments,
the operator-facing profile comment and the spec-191 lineage entry in `CLAUDE.md`. The new fingerprint will
trip every existing per-strategy identity record; state the operator action accurately: those ignored live
records must be consciously deleted/re-recorded before the first post-194 baseline. Do not fabricate or
commit them.

This is the **second intentional scoring-identity move in the same week**: spec 191 moved keyword rules
v6 → v7 and the current pins, and spec 194 moves v7 → v8 while adding the missing judgment descriptor and
`media-collapse-v2`. The history therefore has three distinct semantic regimes — pre-191 Neutral news,
spec-191 inherited direction (known defective), and post-194 grounded judgment signals — with two close
discontinuities. Say that plainly in the lineage and profile comment.

Operator order is load-bearing:

1. do not touch the ignored identity records while a pre-194 baseline is running;
2. after spec 194 is merged and before the first post-194 baseline, delete/re-record every configured
   `data/scoring-configs/strategies/{name}.json` identity consciously; and
3. verify the first run reports the expected new fingerprint before treating subsequent snapshots as the
   corrected series.

If step 2 is missed, `StrategyIdentityGuard` will halt the run before collection. That halt is correct and
must not be bypassed.

## 3. Scope retryable typing failure to the checkpoint window

`NewsTypingGenerator.BuildCohortRunResult` currently tests pass-wide `FailedCompanyIds` while deriving a
30-day company checkpoint. Replace that test with observation-key membership:

- a company is `RetryableFailure` only when at least one **in-window** `(ObservationId, PayloadHash)` is in
  this pass's retryable-failure set;
- exhausted in-window keys still win as `RetryExhausted`;
- an out-of-window backlog failure remains visible in the pass-wide reader summary/lane accounting but cannot
  alter the in-window company token; and
- correct the comments and any rendered message that still says legacy `Failed` or claims the impact is only
  presentational.

This is now score-relevant: a false non-Complete value suppresses `NewsTrajectorySignalRules`' complete-typing
bonus on the judgment-derived signal. Add a regression fixture with a fully typed in-window company plus one
failing out-of-window backlog observation; completeness remains `Complete`, and the materialized signal gets
the Complete strength. A real in-window failure still produces `RetryableFailure` and no bonus.

## 4. Tests and mutation proofs

At minimum add these regressions:

1. **The Monday/Tuesday failure shape:** Monday's cited deterioration materializes one Negative judgment
   signal anchored to Monday's evidence. Tuesday's newly extracted positive-looking article remains ordinary
   Neutral and never receives Monday's judgment id. Reverting to company-only inheritance turns the test red.
2. The article that grounded the judgment is the signal's primary/all-citation provenance; every metadata id
   resolves signal → judgment → fact → observation → evidence.
3. `Mixed`, `Unknown`, non-`Judged`, non-presentation and partially unresolved citation sets create no
   directional signal and increment the named result.
4. Re-running the same judgment produces the same deterministic signal id, no second review and no overwrite.
5. A reused pre-194 judgment materialized today has `CreatedAtUtc = today`; replay immediately before today
   cannot see it.
6. A materialized signal replaces the ordinary signal over the same evidence in current and previous windows;
   activity count is unchanged, and the superseded count is surfaced.
7. An accrued spec-191 directional article signal without the v1 materializer token scores Neutral and is
   counted; the persisted record is untouched. A malformed v1 envelope also fails closed.
8. `media-collapse-v2` keeps a materialized directional representative over an earlier Neutral member while
   preserving the v1 bucket boundaries and the exact v1 result for an all-ordinary bucket.
9. The fingerprint on/off/model/cohort/rule/full-vs-score assertions in §2, with all recomputed pins.
10. An out-of-window typing failure cannot alter in-window completeness or materialized strength; an
    in-window failure can.

For the scoring correction, pin a constructed pre/post input set: removing the v1 materialized signal restores
the ordinary Neutral result; adding it changes only the expected trajectory/media contribution and linked
provenance. No test may obtain green by copying mutable live data.

## 5. Lineage, compatibility and explicit non-goals

- **Forward-only.** No existing evidence, observation, typing, family, judgment, signal, snapshot or efficacy
  artifact is deleted, rewritten or backfilled.
- Existing spec-191 directional article signals remain on disk but their ungrounded inherited direction is
  suppressed by the versioned scoring admission rule. This is a deliberate scoring discontinuity.
- A currently reused judgment may materialize forward, but its signal knowledge time is the materialization
  instant, never the old judgment instant.
- `KeywordSignalExtractor` v8, `media-collapse-v2`, the news judgment identity segment and the legacy
  suppression/supersede rules deliberately move the fingerprints. History is not regenerated.
- No new `SignalType`, strategy, arm, formula class, label or Lead change.
- No judge prompt/schema/taxonomy/fact-family change and no new model call.
- No per-fact positive/negative taxonomy. The validated company-level trajectory remains the call; this spec
  corrects which durable signal carries it.
- No evidence expansion, collection-limit increase, retry queue, write transaction or historical migration.
- Do not add Claude/Ollama/provider-selection work. A future model may be configured later; §2 merely ensures
  that such a model change cannot hide inside the old scoring identity.
- Do not fold spec 195's file-warning, syndication-artifact or diagnostic-tail fixes back into this slice.
  They are independent and must not delay the scoring correction.

Update the spec-191 section in `CLAUDE.md` rather than appending a contradictory second story: state that its
article-inheritance seam and direction-blind collapse were superseded by spec 194, name the new materializer
and v8/v2 identities, preserve the historical pin values as history, and add the newly recomputed current
values.

## Acceptance criteria

- [ ] A news article never inherits a company judgment that did not cite it; ordinary news extraction is
      Neutral, and one validated presentation judgment may create one deterministic, fully grounded
      `news-judgment-signal-v1` signal after judgment.
- [ ] The materialized signal records complete judgment/fact/observation/evidence provenance, is idempotent,
      is never backdated, and becomes score-visible only from a later run/as-of instant.
- [ ] Current and previous scoring replace the ordinary signal over the anchor evidence rather than double
      counting it; accrued spec-191 inherited directions fail closed to Neutral; `media-collapse-v2` preserves
      grounded direction.
- [ ] Judgment off/on, model, presentation cohort and material scoring rules are in
      `ScoringConfigVersion` in full/score/replay modes; all pins and lineage notes move consciously and the
      operator action for ignored strategy identity records is stated.
- [ ] Only an in-window retryable typing failure degrades in-window completeness; the complete-typing strength
      bonus is correct.
- [ ] No historical artifact is deleted, rewritten or backfilled; no new strategy/type/formula/model call and
      no evidence expansion.
- [ ] The spec-191 regime is recorded as known-defective/non-comparable, the second same-week fingerprint move
      and three semantic regimes are explicit, and the operator identity-record sequence is documented.
- [ ] `dotnet build Radar.sln -c Release`, the full test suite and `git diff --check` pass; on Windows,
      `scripts/run-radar.ps1 -Profile default -WhatIf` still resolves and shows the same hosted readers plus
      the intentional new scoring identity.
