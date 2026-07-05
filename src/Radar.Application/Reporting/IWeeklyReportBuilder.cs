namespace Radar.Application.Reporting;

using Radar.Application.Collectors;
using Radar.Application.Pipeline;

/// <summary>
/// Stage 7 builder: assembles and persists a weekly RadarReport for the period ending at
/// <paramref name="periodEndUtc"/>, with one item per surfaced company tracing back to its score
/// snapshot and evidence. The run's <paramref name="collection"/> summary is attached for the
/// transparency footer. The optional <paramref name="health"/> collection-health report (spec 98) is
/// attached for the diagnostic "Collection health" section — observational only, never scoring input.
/// </summary>
public interface IWeeklyReportBuilder
{
    Task<WeeklyReportResult> GenerateAsync(
        DateTimeOffset periodEndUtc,
        CollectionSummary collection,
        CollectionHealthReport? health,
        CancellationToken ct);
}
