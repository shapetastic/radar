namespace Radar.Application.Reporting;

using Radar.Application.Collectors;
using Radar.Application.Pipeline;

/// <summary>The complete weekly report as data; the renderer formats it deterministically.</summary>
public sealed record WeeklyReportModel(
    string Title,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<WeeklyReportEntry> Entries,
    IReadOnlyList<NeedsReviewSignalRef> SignalsNeedingReview,
    CollectionSummary? Collection = null,
    IReadOnlyList<RecentRunSummary>? RecentRuns = null,
    // Diagnostic collection-health findings (spec 98); null/empty renders no section. Observational
    // only — never a label/score/advice, never a scoring input.
    CollectionHealthReport? Health = null);
