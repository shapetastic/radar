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
    IReadOnlyList<Company> Companies);
