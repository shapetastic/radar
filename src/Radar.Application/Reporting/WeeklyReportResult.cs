namespace Radar.Application.Reporting;

using Radar.Domain.Reports;

/// <summary>
/// The persisted report plus the items that trace it to score snapshots.
/// <para>
/// <see cref="StrategySections"/> (spec 179 §2) carries the EXACT spec-150/176 per-strategy section
/// instances the builder already built and rendered — an additive transport so the in-process news-risk
/// shadow step can consume the structured rows without parsing Markdown or reopening/re-ranking the score
/// stores. Trailing and defaulted, so every existing construction site compiles unchanged; <c>null</c> means
/// exactly what it means inside the builder (a single configured strategy builds no sections).
/// </para>
/// </summary>
public sealed record WeeklyReportResult(
    RadarReport Report,
    IReadOnlyList<RadarReportItem> Items,
    IReadOnlyList<StrategyReportSection>? StrategySections = null);
