namespace Radar.Application.Reporting;

using Radar.Application.Collectors;

/// <summary>
/// Stage 7 builder: assembles and persists a weekly RadarReport for the period ending at
/// <paramref name="periodEndUtc"/>, with one item per surfaced company tracing back to its score
/// snapshot and evidence. The run's <paramref name="collection"/> summary is attached for the
/// transparency footer.
/// </summary>
public interface IWeeklyReportBuilder
{
    Task<WeeklyReportResult> GenerateAsync(
        DateTimeOffset periodEndUtc, CollectionSummary collection, CancellationToken ct);
}
