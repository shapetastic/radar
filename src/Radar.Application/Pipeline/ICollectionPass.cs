using Radar.Application.Collectors;
using Radar.Domain.Companies;

namespace Radar.Application.Pipeline;

/// <summary>
/// Stages 1–5 of the pipeline as an independently invokable unit (spec 144): collect evidence over the watch
/// universe, store the new items, then extract → resolve → review → store their signals. It writes the durable
/// evidence and signal stores and <b>nothing else</b> — it never scores, never reports and never writes a run
/// record (the runner that invoked it owns that).
/// <para>
/// Splitting this out is what makes "collection runs on its own schedule; scoring runs over whatever has
/// accrued" possible without a second copy of either stage. The combined run
/// (<see cref="RadarPipelineRunner"/>) and the standalone <c>collect</c> pass
/// (<see cref="CollectOnlyPipelineRunner"/>) both call THIS, so one-collection-pass semantics (spec 137 —
/// collection, the AI directional read, extraction, resolution and review each run exactly once) are a
/// property of the type rather than of each caller.
/// </para>
/// </summary>
public interface ICollectionPass
{
    Task<CollectionPassResult> RunAsync(CancellationToken ct);
}

/// <summary>
/// Everything stages 1–5 produced that a caller needs: the run instant, the observational counters, the
/// collection summary + health report, the collector names that ran, and the loaded company universe.
/// <para>
/// <see cref="Companies"/> is carried deliberately. The combined run loads the watch universe ONCE (the
/// collection context needs it and the scoring stage reuses it), and handing the list back keeps that single
/// <see cref="Radar.Application.Abstractions.Persistence.ICompanyRepository.GetAllAsync"/> read intact after
/// the split instead of quietly becoming two reads.
/// </para>
/// <para>
/// <see cref="AsOfUtc"/> is captured AFTER collection, for the reason documented in
/// <see cref="CollectionPass"/>: the run instant must not precede the collection that produced this run's
/// evidence, or freshly collected evidence falls outside the scoring window.
/// </para>
/// <para>
/// <see cref="CollectorRuns"/> (spec 169) is the per-COLLECTOR provenance the run record persists. It is
/// built inside the collector loop, BEFORE <see cref="CollectionResultMerger.Merge"/> discards collector
/// identity, and is therefore the only place the fact can be captured at all.
/// </para>
/// </summary>
public sealed record CollectionPassResult(
    DateTimeOffset AsOfUtc,
    int EvidenceCollected,
    int EvidenceNew,
    int SignalsExtracted,
    int SignalsValid,
    int SignalsApproved,
    int SignalsNeedingReview,
    CollectionSummary Collection,
    CollectionHealthReport Health,
    IReadOnlyList<string> Collectors,
    IReadOnlyList<Company> Companies,
    // Per-collector run provenance in the same stable collector order as Collectors (spec 169). Non-null;
    // it can only be empty if no collector ran, which the pass's constructor already forbids.
    IReadOnlyList<CollectorRunRecord> CollectorRuns,
    // The spec-177 news-observation batch this pass wrote, or null when capture is disabled / no collector
    // emitted an observation sidecar. Trailing + defaulted so every existing construction site is
    // unchanged. It is the EXPLICIT manifest↔run association the run record carries — never a time join.
    Guid? NewsObservationBatchId = null,
    // Spec 193 §1: how many of this pass's signals were held in memory but NOT durably persisted (the
    // signal file store's write degraded gracefully). Its own axis, deliberately: those signals were still
    // extracted, validated, reviewed and counted as such — what they are not is in the accrued store, so the
    // next run's history read will not see them. Trailing + defaulted to 0, which is the truthful value for
    // a pass that persisted everything; the "not recorded" distinction lives on the durable
    // PipelineRunRecord, not on this in-process result.
    int SignalsNotPersisted = 0,
    // Spec 206 §3: how many collected items' raw-evidence records did NOT become durable. Unlike a
    // not-persisted signal, a Failed raw item was EXCLUDED from the whole run (no extraction, review or
    // signal — a signal must never cite evidence absent from the accrued store) and left un-admitted so a
    // later collection retries it. Null means this pass attempted no raw write at all (nothing was
    // collected); a measured 0 means at least one write was attempted and every item ended Written or
    // AlreadyAvailable. Since this slice, EvidenceNew means newly DURABLE raw evidence.
    int? RawEvidenceNotPersisted = null);
