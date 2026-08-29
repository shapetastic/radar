namespace Radar.Application.Pipeline;

/// <summary>
/// Immutable, durable record of one completed pipeline run: the run instant, which collectors ran, which
/// scoring strategies scored it, the run's observational counts, and the generated report id (if any). It is
/// a run-observability projection
/// of <see cref="RadarPipelineResult"/> — NOT a Domain aggregate — persisted once per run to build a
/// run history for week-over-week comparison. The counts are observational only; provenance still lives
/// in the persisted evidence/signals/snapshots/report, not here. All temporal fields are UTC; the run is
/// stamped with the run's single <c>asOfUtc</c> instant (one run, one instant, AD-7).
/// </summary>
public sealed record PipelineRunRecord(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Collectors,
    int EvidenceCollected,
    int EvidenceNew,
    int SignalsExtracted,
    int SignalsValid,
    int SignalsApproved,
    int SignalsNeedingReview,
    int CompaniesScored,
    int SourcesChecked,
    int SourcesFailed,
    Guid? ReportId,
    // Observational collection-health findings for this run (spec 98): reconciliation warnings for
    // feed types declared in the seed that did not reach the collectors. Trailing + optional so old
    // on-disk run JSON (written before this slice) still deserializes (null == no findings recorded);
    // never evidence/signal/scoring input, and RecentRunSummary does not read it.
    IReadOnlyList<CollectionHealthWarning>? CollectionWarnings = null,
    // The scoring strategies that scored this run's companies, in run order, alongside which of them was
    // primary (spec 137 — one collection pass, N independently-stamped scorings). Trailing + optional so old
    // on-disk run JSON (written before this slice) still deserializes (null == single-strategy/unrecorded);
    // observational only, never evidence/signal/scoring input, and RecentRunSummary does not read it.
    IReadOnlyList<string>? Strategies = null,
    string? PrimaryStrategy = null,
    // The resolved, canonicalised Radar:Companies ticker filter this run collected for (spec 161), or null
    // when the run covered the whole watch universe. Trailing + optional so every existing on-disk run JSON
    // still deserializes and reads correctly as UNFILTERED. Run provenance only — a partial pass must never
    // be mistakable for a full one — and never an evidence/signal/scoring input: the company universe is not
    // a fingerprint input (AD-10), and the filter is collect-only by guard, so a scored run always carries
    // null here.
    IReadOnlyList<string>? CompanyFilter = null,
    // Per-COLLECTOR run provenance (spec 169 / AD-16's 2026-08-03 amendment): one row per collector that ran,
    // in stable collector order, carrying that collector's own UNMERGED summary plus — for the collectors
    // that record it — per-company coverage. Trailing + optional so every existing on-disk run JSON still
    // deserializes and reads correctly as "not recorded". Observational only: never an evidence, signal,
    // score, fingerprint or strategy-comparability input, and RecentRunSummary does not read it.
    //
    // FOR COVERAGE PURPOSES NULL MEANS UNPROVEN, NEVER SUCCESS, and it is never inferred or backfilled for
    // records written before this contract existed (heal forward — specs 142/145). The AD-16 evaluator reads
    // these rows as the proof that an attention observation window was actually observed; treating an absent
    // record as a clean one would let a missed collection read as a valid publisher count of zero.
    IReadOnlyList<CollectorRunRecord>? CollectorRuns = null,
    // The spec-177 news-observation batch manifest this run's collection pass wrote (the EXPLICIT
    // association the spec demands — never a nearest-time join), or null when capture was disabled, no
    // collector emitted observations, or the record predates the archive. Trailing + optional so every
    // existing on-disk run JSON still deserializes; observational only, read by no scoring/report path.
    Guid? NewsObservationBatchId = null,
    // Spec 193 §1: how many signals / score snapshots this run held in memory but could NOT durably persist
    // (the file store's write degraded gracefully). Trailing + NULLABLE, and null means NOT RECORDED — a run
    // record written before this contract existed, or a pass that did not do that kind of work at all. Never
    // a fabricated 0: "this run persisted everything" and "nobody was counting" are different facts, and the
    // first is a claim the second cannot make (the spec-190 CollectorCompanyCoverage precedent).
    //
    // WHICH RUNNER RECORDS WHICH: a value is written only where the pass genuinely observed it. The combined
    // run records both. A `collect` pass records SignalsNotPersisted and leaves ScoreSnapshotsNotPersisted
    // null — it wrote no snapshot, so 0 would claim a clean snapshot write that never happened. A `score`
    // pass is the mirror: ScoreSnapshotsNotPersisted recorded, SignalsNotPersisted null.
    //
    // Observational only — never an evidence, signal, score, fingerprint or strategy-comparability input,
    // and RecentRunSummary does not read it. It is not backfilled onto existing records (heal forward,
    // AD-8).
    int? SignalsNotPersisted = null,
    int? ScoreSnapshotsNotPersisted = null,
    // Spec 201 §1: the two remaining durable writes a pipeline run performs whose outcome had been discarded
    // — the weekly report markdown and the per-strategy effective scoring-config file (content-addressed,
    // insert-if-new). Same contract as the two counters above: trailing + NULLABLE, null means NOT RECORDED
    // ("this pass did not do that kind of work" — a `collect` pass writes neither, a run with GenerateReport
    // off writes no report), never a fabricated 0. ReportId stays populated on a failed report write: the
    // report WAS generated (its in-memory model may still be re-rendered to the same path by the judgment
    // re-renderer), so the id identifies what exists and this counter says the FILE did not land.
    int? ReportsNotPersisted = null,
    int? ScoringConfigsNotPersisted = null);
