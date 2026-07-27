namespace Radar.Application.Reporting;

using Radar.Domain.Scoring;

/// <summary>
/// One row of one strategy's plain ranked table (spec 150). Deliberately carries the whole
/// <see cref="CompanyScoreSnapshot"/> rather than a handful of copied integers: every number the renderer
/// prints is read straight off the snapshot the strategy actually wrote, so a row can never show a score
/// that no stored snapshot produced (provenance is sacred), and <see cref="ScoreSnapshotId"/> lets a reader
/// walk report → snapshot → signals/evidence exactly as a <see cref="WeeklyReportEntry"/> does.
/// <para>
/// It carries NO label, NO evidence refs and NO "why noticed": those stay primary-only (spec 150 §2). A
/// company labelled <c>Watch</c> under one strategy and <c>Ignore</c> under another would read as Radar
/// equivocating, which the output-language rules do not contemplate.
/// </para>
/// </summary>
/// <param name="Rank">1-based position within this strategy's surfaced rows.</param>
/// <param name="CompanyId">The scored company (equals <c>Snapshot.CompanyId</c>).</param>
/// <param name="CompanyName">Display name, straight from the company record.</param>
/// <param name="Ticker">Display ticker, or null when the company record has none.</param>
/// <param name="ScoreSnapshotId">The cited snapshot (equals <c>Snapshot.Id</c>).</param>
/// <param name="Snapshot">The snapshot every rendered score is read from.</param>
public sealed record StrategyReportRow(
    int Rank,
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    Guid ScoreSnapshotId,
    CompanyScoreSnapshot Snapshot);
