namespace Radar.Application.Reporting;

using Radar.Application.Collectors;

/// <summary>The complete weekly report as data; the renderer formats it deterministically.</summary>
public sealed record WeeklyReportModel(
    string Title,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<WeeklyReportEntry> Entries,
    IReadOnlyList<NeedsReviewSignalRef> SignalsNeedingReview,
    CollectionSummary? Collection = null,
    IReadOnlyList<RecentRunSummary>? RecentRuns = null);
