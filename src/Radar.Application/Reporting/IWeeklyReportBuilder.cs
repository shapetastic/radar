namespace Radar.Application.Reporting;

/// <summary>
/// Stage 7 builder: assembles and persists a weekly RadarReport for the period ending at
/// <paramref name="periodEndUtc"/>, with one item per surfaced company tracing back to its score
/// snapshot and evidence.
/// </summary>
public interface IWeeklyReportBuilder
{
    Task<WeeklyReportResult> GenerateAsync(DateTimeOffset periodEndUtc, CancellationToken ct);
}
