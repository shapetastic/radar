using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>Shared deterministic builders for the spec-185 direction-judge tests.</summary>
internal static class NewsJudgmentTestData
{
    public static readonly DateTimeOffset ObservedAt = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    public static NewsJudgmentInputFamily Family(
        Guid? factId = null,
        NewsFactAssertionStatus assertionStatus = NewsFactAssertionStatus.Reported,
        NewsFactAttribution attribution = NewsFactAttribution.Publisher,
        string statement = "The company reported that quarterly revenue rose 12%.",
        int memberCount = 1,
        int distinctPublisherCount = 1) => new(
        FamilyId: Guid.NewGuid(),
        RepresentativeFactId: factId ?? Guid.NewGuid(),
        EventTypes: [NewsEventType.EarningsOrGuidance],
        Statement: statement,
        TemporalScope: "Q2 2026",
        Attribution: attribution,
        AssertionStatus: assertionStatus,
        Confidence: 0.9,
        Citations: ["quarterly revenue rose 12%"],
        MemberCount: memberCount,
        DistinctPublisherCount: distinctPublisherCount);

    public static NewsJudgmentModelFinding Finding(
        Guid factId,
        string category = "RegulatoryOrLegalSetback",
        string severity = "High",
        double? confidence = 0.8,
        string? caveat = null) => new(
        Category: category,
        Severity: severity,
        Confidence: confidence,
        FactIds: [factId.ToString("D")],
        AttributionCaveat: caveat);

    public static NewsJudgmentModelResponse Response(
        string? trajectory = "Deteriorating",
        int? strength = 60,
        IReadOnlyList<NewsJudgmentModelFinding>? findings = null,
        string? rationale = "Legal scrutiny challenges the trajectory.") => new(
        BusinessTrajectory: trajectory,
        ChallengeStrength: strength,
        Findings: findings,
        Rationale: rationale);

    public static FactFamilyRecord FamilyRecord(
        Guid companyId,
        Guid representativeFactId,
        string statement,
        int memberCount = 1,
        int distinctPublisherCount = 1,
        IReadOnlyList<Guid>? memberFactIds = null) => new(
        FamilyId: Guid.NewGuid(),
        CompanyId: companyId,
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        RepresentativeFactId: representativeFactId,
        RepresentativeStatement: statement,
        CanonicalClaimKey: FactFamilyBuilder.NormalizeStatement(statement),
        EventTypes: [NewsEventType.RegulatoryOrLegal],
        MemberFactIds: memberFactIds ?? [representativeFactId],
        MemberCount: memberCount,
        DistinctPublisherCount: distinctPublisherCount,
        EarliestObservedAtUtc: ObservedAt);

    public static NewsTypingFactRef FactRef(
        Guid companyId,
        Guid factId,
        string statement,
        NewsFactAssertionStatus assertionStatus = NewsFactAssertionStatus.Alleged,
        NewsFactAttribution attribution = NewsFactAttribution.PlaintiffFirm) => new(
        Fact: new NewsTypingValidatedFact(
            FactId: factId,
            EventTypes: [NewsEventType.RegulatoryOrLegal],
            Statement: statement,
            TemporalScope: null,
            Attribution: attribution,
            AssertionStatus: assertionStatus,
            Confidence: 0.85,
            Citations: [statement]),
        ObservationId: Guid.NewGuid(),
        CompanyId: companyId,
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss);
}
