using Radar.Application.Lifecycle;

namespace Radar.Application.Reporting;

/// <summary>
/// The spec-184 lifecycle view the renderer draws the operating-call layer and the per-strategy evidence
/// statuses from. Built by the builder ONLY in a multi-strategy composition; <c>null</c> on the model in a
/// single-strategy one, which is what keeps that report byte-identical (spec 184 §4).
/// <para>
/// Deliberately a MODEL-level record rather than fields on <see cref="StrategyReportSection"/>: the section
/// list travels on <see cref="WeeklyReportResult"/> into the pipeline result (and the news-risk shadow
/// step), and the architecture guard requires that neither scoring nor the pipeline can reach a lifecycle
/// type. Keeping calls/statuses here — reachable from the model the renderer sees, not from the result the
/// pipeline sees — makes that boundary structural.
/// </para>
/// </summary>
/// <param name="Calls">The reduced operating calls (possibly the "none declared" resolution).</param>
/// <param name="Statuses">One computed evidence status per configured strategy, in configured order.</param>
public sealed record StrategyLifecycleReportModel(
    ResolvedOperatingCalls Calls,
    IReadOnlyList<StrategyLifecycleStatusLine> Statuses)
{
    /// <summary>The computed status for one strategy, or null when none was computed for it.</summary>
    public StrategyEvidenceStatus? StatusFor(string strategyName) =>
        Statuses.FirstOrDefault(s =>
            string.Equals(s.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase))?.Status;
}

/// <summary>One strategy's computed evidence status, keyed by its configured name.</summary>
public sealed record StrategyLifecycleStatusLine(string StrategyName, StrategyEvidenceStatus Status);
