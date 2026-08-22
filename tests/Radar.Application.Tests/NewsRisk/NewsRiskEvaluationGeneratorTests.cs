using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Evaluation;
using Radar.Application.Prices;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §9: the read-only evaluator — entry anchored at the ASSESSMENT cutoff (never selection),
/// partial forward windows failing closed through the reused spec-152 primitive, development examples
/// visible but excluded from the clean prospective table, legacy/retrospective content in separate
/// development tables, and reader cohorts never pooling.
/// </summary>
public sealed class NewsRiskEvaluationGeneratorTests
{
    private static readonly DateTimeOffset SelectionAsOf = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boundary = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeAssessmentStore(IReadOnlyList<NewsRiskAssessmentRecord> records)
        : INewsRiskAssessmentStore
    {
        public Task<bool> WriteAsync(NewsRiskAssessmentRecord record, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<NewsRiskAssessmentRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(records);

        public Task<NewsRiskAssessmentRecord?> FindCompletedAsync(
            string cohortKey, string inputBundleHash, CancellationToken ct) =>
            Task.FromResult<NewsRiskAssessmentRecord?>(null);
    }

    private sealed class FakeDevSource(IReadOnlyList<NewsRiskDevelopmentExample>? examples)
        : INewsRiskDevelopmentExampleSource
    {
        public Task<IReadOnlyList<NewsRiskDevelopmentExample>?> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(examples);
    }

    private sealed class FakeBoundaryReader(NewsObservationBoundary? boundary)
        : INewsProspectiveBoundaryReader
    {
        public Task<NewsObservationBoundary?> ReadBoundaryAsync(CancellationToken ct) =>
            Task.FromResult(boundary);
    }

    private sealed class FakePriceStore(Dictionary<string, PriceHistory> histories) : IPriceHistoryStore
    {
        public Task<string> WriteAsync(PriceHistory history, CancellationToken ct) =>
            Task.FromResult("(unused)");

        public Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct) =>
            Task.FromResult(histories.GetValueOrDefault(ticker));
    }

    private sealed class CapturingArtifactStore : INewsRiskArtifactStore
    {
        public string? Markdown { get; private set; }
        public string? Csv { get; private set; }

        public Task WriteLiveAsync(
            string asOfDateToken, string markdown, NewsRiskLiveDocument document, CancellationToken ct) =>
            Task.CompletedTask;

        public Task WriteFailedAsync(string asOfDateToken, string reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task WriteEvaluationAsync(string markdown, string csv, CancellationToken ct)
        {
            Markdown = markdown;
            Csv = csv;
            return Task.CompletedTask;
        }
    }

    private static NewsRiskAssessmentRecord Assessment(
        string ticker,
        DateTimeOffset? assessmentCutoffUtc = null,
        NewsRiskAssessmentStatus status = NewsRiskAssessmentStatus.ThesisChallenged,
        int? riskScore = 60,
        bool coverageComplete = true,
        NewsObservationCaptureMode captureMode = NewsObservationCaptureMode.ProspectiveRss,
        string readerName = "ambient",
        string model = "model-a") => new(
        SchemaVersion: NewsRiskAssessmentRecord.CurrentSchemaVersion,
        AssessmentId: Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        SelectionAsOfUtc: SelectionAsOf,
        AssessmentCutoffUtc: assessmentCutoffUtc ?? SelectionAsOf,
        CompanyId: Guid.NewGuid(),
        CompanyName: ticker + " Co",
        Ticker: ticker,
        Selections: [new NewsRiskCandidateSelection("default", 1, Guid.NewGuid())],
        ReaderName: readerName,
        Provider: "test-provider",
        ModelId: model,
        PromptVersion: NewsRiskAnalysisContract.PromptVersion,
        ResultSchemaVersion: NewsRiskAnalysisContract.SchemaVersion,
        CohortKey: NewsRiskAnalysisContract.CohortKey("test-provider", model),
        InputBundleHash: "bundle-" + ticker,
        Observations:
        [
            new NewsRiskInputObservationRef(
                Guid.NewGuid(), "ph", DescriptionSupplied: true, BodySupplied: false,
                BodyContentHash: null, BodyRetrievedAtUtc: null, BodyExtractorVersion: null,
                BodyRetrievalPolicy: null, CaptureMode: captureMode),
        ],
        CoverageComplete: coverageComplete,
        CoverageIssues: [],
        Status: status,
        RiskScore: status == NewsRiskAssessmentStatus.ThesisChallenged ? riskScore : null,
        Categories: status == NewsRiskAssessmentStatus.ThesisChallenged
            ? [NewsRiskCategory.LiquidityOrGoingConcern]
            : [],
        Claims: [],
        Rationale: null,
        ClaimsTotal: 1,
        ClaimsAccepted: 1,
        ClaimsDropped: 0,
        ClaimDropReasons: [],
        RawResponseHash: "raw",
        FailureDetail: null,
        Limits: new NewsRiskShadowLimitsRecord(30, 30, 12, 3),
        ReusedFromAssessmentId: null,
        CreatedAtUtc: SelectionAsOf);

    /// <summary>Daily bars covering [start, end], strictly rising so returns are deterministic.</summary>
    private static PriceHistory History(string ticker, DateOnly start, DateOnly end)
    {
        var bars = new List<PriceBar>();
        var price = 100m;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            bars.Add(new PriceBar(d, price, price, price, price, price, 1000));
            price += 1m;
        }

        return new PriceHistory(ticker, "test", SelectionAsOf, bars);
    }

    private static async Task<(string Markdown, string Csv)> RunAsync(
        IReadOnlyList<NewsRiskAssessmentRecord> assessments,
        IReadOnlyList<NewsRiskDevelopmentExample>? examples,
        NewsObservationBoundary? boundary,
        Dictionary<string, PriceHistory> prices)
    {
        var artifacts = new CapturingArtifactStore();
        var generator = new NewsRiskEvaluationGenerator(
            new FakeAssessmentStore(assessments),
            new FakeDevSource(examples),
            new FakeBoundaryReader(boundary),
            new FakePriceStore(prices),
            artifacts,
            NullLogger<NewsRiskEvaluationGenerator>.Instance);
        await generator.GenerateAsync(CancellationToken.None);
        Assert.NotNull(artifacts.Markdown);
        Assert.NotNull(artifacts.Csv);
        return (artifacts.Markdown!, artifacts.Csv!);
    }

    private static NewsObservationBoundary EstablishedBoundary() =>
        new(NewsObservationRecord.CurrentSchemaVersion, Boundary, Guid.NewGuid());

    private static string CsvLineFor(string csv, NewsRiskAssessmentRecord record) =>
        Assert.Single(
            csv.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            l => l.Contains(record.AssessmentId.ToString("D"), StringComparison.Ordinal));

    [Fact]
    public async Task EntryAnchorsAtTheAssessmentCutoff_NeverSelectionTime()
    {
        // The cutoff sits FIVE days after selection (a fetched body arrived late). The entry bar must be
        // the first bar strictly after the CUTOFF date, not after the selection date.
        var cutoff = SelectionAsOf.AddDays(5);
        var record = Assessment("AAA", assessmentCutoffUtc: cutoff);
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(cutoff.UtcDateTime).AddDays(25);

        var (_, csv) = await RunAsync(
            [record], [], EstablishedBoundary(), new() { ["AAA"] = History("AAA", start, end) });

        var line = CsvLineFor(csv, record);
        var expectedEntry = DateOnly.FromDateTime(cutoff.UtcDateTime).AddDays(1);
        Assert.Contains(expectedEntry.ToString("yyyy-MM-dd"), line);
        Assert.Contains(",CleanProspective,", line);
    }

    [Fact]
    public async Task PartialForwardWindow_FailsClosed_ThroughTheReusedSpec152Primitive()
    {
        var record = Assessment("BBB");
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        // Only five days of price after the cutoff: a 21-day window cannot resolve.
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(5);

        var (_, csv) = await RunAsync(
            [record], [], EstablishedBoundary(), new() { ["BBB"] = History("BBB", start, end) });

        var line = CsvLineFor(csv, record);
        Assert.Contains(",Excluded,", line);
        Assert.Contains("forward-window-PartialWindow", line);
    }

    [Fact]
    public async Task MissingPriceHistory_IsANamedExclusion()
    {
        var record = Assessment("CCC");

        var (_, csv) = await RunAsync([record], [], EstablishedBoundary(), new());

        Assert.Contains("no-price-history", CsvLineFor(csv, record));
    }

    [Fact]
    public async Task KnownDevelopmentExamples_StayVisible_ButNeverEnterTheCleanTable()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var eose = Assessment("EOSE");
        var clean = Assessment("DDD");
        var examples = new[] { new NewsRiskDevelopmentExample("EOSE", "2026-08-22", "motivating case") };

        var (markdown, csv) = await RunAsync(
            [eose, clean],
            examples,
            EstablishedBoundary(),
            new() { ["EOSE"] = History("EOSE", start, end), ["DDD"] = History("DDD", start, end) });

        Assert.Contains(",KnownDevelopmentExample,", CsvLineFor(csv, eose));
        Assert.Contains(",CleanProspective,", CsvLineFor(csv, clean));
        Assert.Contains("Known development examples", markdown);
    }

    [Fact]
    public async Task LegacyAndRetrospectiveContent_LandInSeparateDevelopmentTables()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var legacy = Assessment("LGC", captureMode: NewsObservationCaptureMode.LegacyHeadlineOnly);
        var retro = Assessment("RTR", captureMode: NewsObservationCaptureMode.RetrospectiveUrlFetch);

        var (_, csv) = await RunAsync(
            [legacy, retro], [], EstablishedBoundary(),
            new() { ["LGC"] = History("LGC", start, end), ["RTR"] = History("RTR", start, end) });

        Assert.Contains(",LegacyHeadlineOnly,", CsvLineFor(csv, legacy));
        Assert.Contains(",RetrospectiveUrlFetch,", CsvLineFor(csv, retro));
    }

    [Fact]
    public async Task ReaderCohorts_NeverPool()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var a = Assessment("AAA", readerName: "reader-a", model: "model-a");
        var b = Assessment("AAA", readerName: "reader-b", model: "model-b");

        var (markdown, _) = await RunAsync(
            [a, b], [], EstablishedBoundary(), new() { ["AAA"] = History("AAA", start, end) });

        // Two cohort sections, side by side; never one pooled table.
        Assert.Contains($"## Cohort `{a.CohortKey}`", markdown);
        Assert.Contains($"## Cohort `{b.CohortKey}`", markdown);
    }

    [Fact]
    public async Task NoBoundary_MeansNoCleanProspectiveRow()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var record = Assessment("AAA");

        var (markdown, csv) = await RunAsync(
            [record], [], boundary: null, new() { ["AAA"] = History("AAA", start, end) });

        Assert.Contains("no-prospective-boundary", CsvLineFor(csv, record));
        Assert.Contains("NOT ESTABLISHED", markdown);
    }

    [Fact]
    public async Task PreBoundaryAssessments_AreExcludedFromClean()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-40);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var record = Assessment("AAA", assessmentCutoffUtc: Boundary.AddDays(-3));

        var (_, csv) = await RunAsync(
            [record], [], EstablishedBoundary(), new() { ["AAA"] = History("AAA", start, end) });

        Assert.Contains("before-prospective-boundary", CsvLineFor(csv, record));
    }

    [Fact]
    public async Task UnavailableDevelopmentDeclarations_SuppressTheCleanTableEntirely()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var record = Assessment("AAA");

        var (markdown, csv) = await RunAsync(
            [record], examples: null, EstablishedBoundary(),
            new() { ["AAA"] = History("AAA", start, end) });

        Assert.Contains("development-declarations-unavailable", CsvLineFor(csv, record));
        Assert.Contains("Development declarations UNAVAILABLE", markdown);
        Assert.DoesNotContain(",CleanProspective,", csv);
    }

    [Fact]
    public async Task IncompleteCoverageOrNonCompletedStatus_NeverEntersTheCleanTable()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var incomplete = Assessment("AAA", coverageComplete: false);
        var failed = Assessment("BBB", status: NewsRiskAssessmentStatus.ProviderFailure);

        var (_, csv) = await RunAsync(
            [incomplete, failed], [], EstablishedBoundary(),
            new() { ["AAA"] = History("AAA", start, end), ["BBB"] = History("BBB", start, end) });

        Assert.Contains("coverage-incomplete", CsvLineFor(csv, incomplete));
        Assert.Contains("assessment-not-completed-validated", CsvLineFor(csv, failed));
        Assert.DoesNotContain(",CleanProspective,", csv);
    }

    [Fact]
    public async Task TheEvaluatorCaveat_IsCarriedVerbatim()
    {
        var (markdown, _) = await RunAsync([], [], null, new());

        Assert.Contains(NewsRiskEvaluationGenerator.EvaluatorCaveat, markdown);
    }
}
