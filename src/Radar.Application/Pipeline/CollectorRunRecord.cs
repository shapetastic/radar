using Radar.Application.Collectors;

namespace Radar.Application.Pipeline;

/// <summary>
/// What ONE collector did on ONE run (spec 169): its stable provenance name, its own <b>unmerged</b>
/// collection summary, and — for the collectors that record it — per-company coverage.
/// <para>
/// <b>Why this exists.</b> AD-16 §5 originally claimed the attention-coverage test was satisfiable from the
/// run record's existing aggregate fields. The 2026-08-03 amendment corrects that: <c>SourcesFailed</c> is a
/// sum across every collector, so it cannot separate two failed RSS feeds from one failed <c>newssearch</c>
/// feed; it carries no company; and it cannot reveal that a <i>successful</i> query hit its result limit —
/// the most dangerous case, because a truncated window looks exactly like a complete one and silently
/// undercounts publishers.
/// </para>
/// <para>
/// The rows are built <b>before</b> <see cref="CollectionResultMerger.Merge"/>, because after the merge
/// collector identity no longer exists — the same reason spec 146 stamps collection provenance where it
/// does. Observational only (AD-14 discipline): never an evidence, signal, score, fingerprint or
/// strategy-comparability input.
/// </para>
/// </summary>
/// <param name="CollectorName">The collector's stable provenance name (<see cref="IEvidenceCollector.CollectorName"/>).</param>
/// <param name="SourcesChecked">Sources this collector checked.</param>
/// <param name="SourcesSucceeded">Sources this collector read successfully.</param>
/// <param name="SourcesFailed">Sources this collector could not read, parse or validate.</param>
/// <param name="ItemsCollected">Raw evidence items this collector produced.</param>
/// <param name="Failures">This collector's own source failures, in its stable source-processing order.</param>
/// <param name="CompanyCoverage">
/// Optional per-company coverage, ordered by <see cref="CollectorCompanyCoverage.CompanyId"/>. Trailing +
/// optional so a collector that records none simply omits it. <c>null</c> means UNPROVEN, never success.
/// </param>
public sealed record CollectorRunRecord(
    string CollectorName,
    int SourcesChecked,
    int SourcesSucceeded,
    int SourcesFailed,
    int ItemsCollected,
    IReadOnlyList<SourceFailure> Failures,
    IReadOnlyList<CollectorCompanyCoverage>? CompanyCoverage = null);
