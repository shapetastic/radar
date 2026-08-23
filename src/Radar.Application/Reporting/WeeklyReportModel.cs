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
    CollectionHealthReport? Health = null,
    // One plain ranked table per configured scoring strategy (spec 150), primary first. NULL when the run
    // has a single strategy — which is every deployment that never configured Radar:Strategies — so the
    // rendered report stays BYTE-IDENTICAL to the pre-150 output. Trailing and defaulted so every existing
    // construction site keeps compiling. Scores only: no labels, no evidence, no "why noticed".
    IReadOnlyList<StrategyReportSection>? Strategies = null,
    // The spec-184 operating-call layer + per-strategy evidence statuses. NULL in a single-strategy run
    // (the call layer is inert there, spec 184 §4) and for direct-model callers that predate it — the
    // renderer then behaves byte-identically to pre-184. Trailing and defaulted so every existing
    // construction site keeps compiling.
    StrategyLifecycleReportModel? Lifecycle = null,
    // The spec-185 semantic-read marker source for the live-leaders section. Only consulted when the model
    // carries strategy sections (a single-strategy report has no leaders section and stays byte-identical);
    // NULL there means "no judgment step is registered" and every leader row renders the honest
    // `? unassessed (no-judgment)`. Trailing and defaulted so every existing construction site keeps
    // compiling. Display metadata only — never a score, rank, ordering, label or snapshot change.
    NewsJudgmentMarkerReportModel? NewsJudgment = null);
