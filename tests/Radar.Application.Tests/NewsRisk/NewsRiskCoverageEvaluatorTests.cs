using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.NewsRisk;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 182 §2: the search-enumeration mapping is TOTAL over the states the coverage evaluation
/// distinguishes — one test per input state — and multiple states combine through the single documented
/// severity rule (Failed &gt; Unproven &gt; Truncated &gt; Complete). Archive capture is derived
/// independently and never collapses into search enumeration.
/// </summary>
public sealed class NewsRiskCoverageEvaluatorTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private const string Collector = "newssearch";

    private static NewsObservationBatch Batch(
        bool captureProven = true,
        IReadOnlyList<NewsObservationCollectorCapture>? collectors = null) => new(
        BatchId: Guid.NewGuid(),
        RunAsOfUtc: NewsRiskTestData.SelectionAsOf,
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        FullUniverse: true,
        ObservationsAttempted: 1,
        ObservationsWritten: captureProven ? 1 : 0,
        ObservationsCrossRunDeduped: 0,
        ObservationsFailed: captureProven ? 0 : 1,
        CaptureProven: captureProven,
        Collectors: collectors ?? [Capture(Row())]);

    private static NewsObservationCollectorCapture Capture(
        params CollectorCompanyCoverage[] rows) => new(
        CollectorName: Collector,
        CompanyCoverage: rows,
        ProviderFailures: [],
        AnyFeedHitProviderCap: false);

    private static CollectorCompanyCoverage Row(
        int expected = 1,
        int successful = 1,
        bool hitLimit = false,
        IReadOnlyList<string>? issues = null) => new(
        CompanyId: Company,
        ExpectedFeedCount: expected,
        SuccessfulFeedCount: successful,
        HitEffectiveResultLimit: hitLimit,
        Issues: issues ?? []);

    private static NewsRiskCoverageEvaluation Evaluate(NewsObservationBatch? batch) =>
        NewsRiskCoverageEvaluator.Evaluate(batch, Company, Collector);

    [Fact]
    public void NullBatch_IsUnprovenOnBothDimensions()
    {
        var result = Evaluate(null);

        Assert.Equal(NewsRiskArchiveCapture.Unproven, result.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Unproven, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("archive-batch-unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void UnprovenCapture_DegradesArchiveCaptureOnly_SearchEnumerationIsUnaffected()
    {
        var result = Evaluate(Batch(captureProven: false));

        Assert.Equal(NewsRiskArchiveCapture.Unproven, result.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Complete, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("archive-batch-unproven", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingNewssearchCaptureEntry_IsUnproven()
    {
        var result = Evaluate(Batch(collectors: []));

        Assert.Equal(NewsRiskSearchEnumeration.Unproven, result.SearchEnumeration);
        Assert.Equal(NewsRiskArchiveCapture.Proven, result.ArchiveCapture);
        Assert.Contains(result.Issues, i => i.StartsWith("newssearch-capture-not-recorded", StringComparison.Ordinal));
    }

    [Fact]
    public void NullCompanyCoverage_IsUnproven()
    {
        var capture = Capture() with { CompanyCoverage = null };
        var result = Evaluate(Batch(collectors: [capture]));

        Assert.Equal(NewsRiskSearchEnumeration.Unproven, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("newssearch-coverage-not-recorded", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingCompanyRow_IsUnproven()
    {
        var otherCompany = Row() with { CompanyId = Guid.NewGuid() };
        var result = Evaluate(Batch(collectors: [Capture(otherCompany)]));

        Assert.Equal(NewsRiskSearchEnumeration.Unproven, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("company-coverage-missing", StringComparison.Ordinal));
    }

    [Fact]
    public void NoDeclaredFeed_IsFailed()
    {
        var result = Evaluate(Batch(collectors: [Capture(Row(expected: 0, successful: 0))]));

        Assert.Equal(NewsRiskSearchEnumeration.Failed, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("no-newssearch-feed", StringComparison.Ordinal));
    }

    [Fact]
    public void FeedFailures_AreFailed()
    {
        var result = Evaluate(Batch(collectors: [Capture(Row(expected: 2, successful: 1))]));

        Assert.Equal(NewsRiskSearchEnumeration.Failed, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("feed-failures: 1/2", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordedRowIssues_AreFailed()
    {
        var result = Evaluate(Batch(collectors: [Capture(Row(issues: ["health mismatch"]))]));

        Assert.Equal(NewsRiskSearchEnumeration.Failed, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i == "coverage-issue: health mismatch");
    }

    [Fact]
    public void HitEffectiveResultLimit_IsTruncated()
    {
        var result = Evaluate(Batch(collectors: [Capture(Row(hitLimit: true))]));

        Assert.Equal(NewsRiskSearchEnumeration.Truncated, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("result-limit-reached", StringComparison.Ordinal));
    }

    [Fact]
    public void NoneOfTheAbove_IsComplete_WithNoIssues()
    {
        var result = Evaluate(Batch());

        Assert.Equal(NewsRiskArchiveCapture.Proven, result.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Complete, result.SearchEnumeration);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void FeedFailurePlusTruncation_CombinesToFailed_AndKeepsEveryIssueString()
    {
        // The combine rule: a KNOWN failure outranks truncation — but both stay in the detail list.
        var result = Evaluate(Batch(collectors: [Capture(Row(expected: 2, successful: 1, hitLimit: true))]));

        Assert.Equal(NewsRiskSearchEnumeration.Failed, result.SearchEnumeration);
        Assert.Contains(result.Issues, i => i.StartsWith("feed-failures", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.StartsWith("result-limit-reached", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncationUnderAnUnprovenBatch_StaysTruncated_ArchiveCaptureCarriesTheUnproven()
    {
        // The dimensions are INDEPENDENT: an unproven batch never collapses into the search dimension.
        var result = NewsRiskCoverageEvaluator.Evaluate(
            Batch(captureProven: false, collectors: [Capture(Row(hitLimit: true))]), Company, Collector);

        Assert.Equal(NewsRiskArchiveCapture.Unproven, result.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Truncated, result.SearchEnumeration);
    }

    [Theory]
    [InlineData(NewsRiskSearchEnumeration.Failed, NewsRiskSearchEnumeration.Unproven, NewsRiskSearchEnumeration.Failed)]
    [InlineData(NewsRiskSearchEnumeration.Failed, NewsRiskSearchEnumeration.Truncated, NewsRiskSearchEnumeration.Failed)]
    [InlineData(NewsRiskSearchEnumeration.Unproven, NewsRiskSearchEnumeration.Truncated, NewsRiskSearchEnumeration.Unproven)]
    [InlineData(NewsRiskSearchEnumeration.Truncated, NewsRiskSearchEnumeration.Complete, NewsRiskSearchEnumeration.Truncated)]
    [InlineData(NewsRiskSearchEnumeration.Complete, NewsRiskSearchEnumeration.Complete, NewsRiskSearchEnumeration.Complete)]
    public void Worse_FollowsTheSingleDocumentedSeverityOrder(
        NewsRiskSearchEnumeration a, NewsRiskSearchEnumeration b, NewsRiskSearchEnumeration expected)
    {
        Assert.Equal(expected, NewsRiskCoverageEvaluator.Worse(a, b));
        Assert.Equal(expected, NewsRiskCoverageEvaluator.Worse(b, a));
    }

    [Fact]
    public void ZeroEnumValues_AreTheDegradedStates_OnEveryDimension()
    {
        // The v1→v2 migration rule (spec 182): a persisted record with MISSING dimension fields
        // deserializes to each enum's zero value, which must be the degraded state — never best-state.
        Assert.Equal(NewsRiskArchiveCapture.Unproven, default(NewsRiskArchiveCapture));
        Assert.Equal(NewsRiskSearchEnumeration.Unproven, default(NewsRiskSearchEnumeration));
        Assert.Equal(NewsRiskAssessmentBundle.Capped, default(NewsRiskAssessmentBundle));
    }
}
