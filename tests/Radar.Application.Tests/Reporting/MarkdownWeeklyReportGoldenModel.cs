using Radar.Application.Collectors;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// One fixed, fully-populated <see cref="WeeklyReportModel"/> shared by the spec-150 byte-identity pin.
/// Every id/instant/score is a literal so the rendered markdown is a pure function of this file — the pin
/// in <c>MarkdownWeeklyReportStrategySectionTests</c> compares the WHOLE rendered string against a
/// character-for-character copy of the pre-spec-150 output, so any change to the single-strategy report
/// (a new heading, a moved line, a stray blank line) fails loudly rather than being argued about.
/// </summary>
internal static class MarkdownWeeklyReportGoldenModel
{
    private static readonly DateTimeOffset PeriodStart = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 6, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 6, 8, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AcmeSnapshotId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BorealisId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BorealisSnapshotId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid EvidenceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SignalId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ReviewSignalId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid ReviewEvidenceId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static CompanyScoreSnapshot Snapshot(
        Guid id, Guid companyId, int opportunity, int trajectory, int attention) =>
        new(
            Id: id,
            CompanyId: companyId,
            ScoringVersion: "radar-formula-v8",
            TrajectoryScore: trajectory,
            OpportunityScore: opportunity,
            AttentionScore: attention,
            EvidenceConfidenceScore: 80,
            SignalVelocityScore: 55,
            Explanation: "Deterministic explanation.",
            ComponentJson: "{}",
            WindowStartUtc: PeriodStart,
            WindowEndUtc: PeriodEnd,
            CreatedAtUtc: PeriodEnd,
            ScoringConfigVersion: "radar-scoring-fp-000000000000",
            StrategyName: "default");

    public static WeeklyReportModel Create(
        IReadOnlyList<StrategyReportSection>? strategies = null) =>
        new(
            Title: "Radar Weekly — 2026-06-01 to 2026-06-08",
            PeriodStartUtc: PeriodStart,
            PeriodEndUtc: PeriodEnd,
            GeneratedAtUtc: GeneratedAt,
            Entries:
            [
                new WeeklyReportEntry(
                    CompanyId: AcmeId,
                    CompanyName: "Acme Dynamics",
                    Ticker: "ACME",
                    ScoreSnapshotId: AcmeSnapshotId,
                    Snapshot: Snapshot(AcmeSnapshotId, AcmeId, 71, 64, 22),
                    Action: RadarReportAction.Investigate,
                    Rationale: "Trajectory improving on corroborated evidence.",
                    Rank: 1,
                    Evidence:
                    [
                        new ReportEvidenceRef(
                            EvidenceId: EvidenceId,
                            SignalId: SignalId,
                            SourceName: "Acme Feed",
                            SourceUrl: "https://acme.example/news",
                            Title: "Acme lands major customer",
                            ContributionReason: "Customer win raised trajectory."),
                    ],
                    Signals:
                    [
                        new ReportSignalRef(
                            SignalId, SignalType.CustomerWin, SignalDirection.Positive,
                            "Multi-year agreement announced."),
                    ],
                    PreviousOpportunityScore: 60,
                    PreviousTrajectoryScore: 64,
                    PreviousScoringChanged: false,
                    FollowingTier: FollowingTier.Small),
                new WeeklyReportEntry(
                    CompanyId: BorealisId,
                    CompanyName: "Borealis Systems",
                    Ticker: null,
                    ScoreSnapshotId: BorealisSnapshotId,
                    Snapshot: Snapshot(BorealisSnapshotId, BorealisId, 40, 30, 70),
                    Action: RadarReportAction.Watch,
                    Rationale: "Thin corroboration; keep observing.",
                    Rank: 2,
                    Evidence: [],
                    Signals: [],
                    PreviousOpportunityScore: null,
                    PreviousTrajectoryScore: null,
                    PreviousScoringChanged: false,
                    FollowingTier: FollowingTier.Mega),
            ],
            SignalsNeedingReview:
            [
                new NeedsReviewSignalRef(
                    SignalId: ReviewSignalId,
                    EvidenceId: ReviewEvidenceId,
                    CompanyMention: "Northwind Robotics",
                    Summary: "Unverified expansion claim.",
                    ReviewReason: "EscalateToHuman: low-quality source."),
            ],
            Collection: new CollectionSummary(
                SourcesChecked: 4,
                SourcesSucceeded: 3,
                SourcesFailed: 1,
                ItemsCollected: 9,
                Failures: [new SourceFailure("Acme Feed", "https://acme.example/rss", "HTTP 503")]),
            RecentRuns:
            [
                new RecentRunSummary(
                    CreatedAtUtc: new DateTimeOffset(2026, 6, 7, 14, 0, 0, TimeSpan.Zero),
                    Collectors: ["rss", "sec"],
                    EvidenceNew: 12,
                    SignalsApproved: 7,
                    CompaniesScored: 43,
                    SourcesChecked: 4,
                    SourcesFailed: 1),
            ],
            Health: new CollectionHealthReport(
            [
                new CollectionHealthWarning(
                    Code: "feed-shrinkage",
                    Severity: CollectionHealthSeverity.Warning,
                    FeedType: "rss",
                    DeclaredInSeed: 10,
                    ReachedCollectors: 8,
                    Message: "Two declared feeds never reached a collector."),
            ]),
            Strategies: strategies);
}
