using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Evaluation;
using Radar.Application.Prices;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §9 as amended by spec 182 §4: the read-only evaluator — entry anchored at the ASSESSMENT
/// cutoff (never selection), partial forward windows failing closed through the reused spec-152 primitive,
/// development examples visible but excluded from the presence/absence-claim tables, legacy/retrospective
/// content in separate development tables, reader cohorts never pooling, presence claims admitted at any
/// completeness (dimension-segmented), and absence claims requiring best-state dimensions.
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
        NewsRiskArchiveCapture archiveCapture = NewsRiskArchiveCapture.Proven,
        NewsRiskSearchEnumeration searchEnumeration = NewsRiskSearchEnumeration.Complete,
        NewsRiskAssessmentBundle assessmentBundle = NewsRiskAssessmentBundle.Complete,
        NewsObservationCaptureMode captureMode = NewsObservationCaptureMode.ProspectiveRss,
        string readerName = "ambient",
        string model = "model-a",
        Guid? companyId = null) => new(
        SchemaVersion: NewsRiskAssessmentRecord.CurrentSchemaVersion,
        AssessmentId: Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        SelectionAsOfUtc: SelectionAsOf,
        AssessmentCutoffUtc: assessmentCutoffUtc ?? SelectionAsOf,
        CompanyId: companyId ?? Guid.NewGuid(),
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
        ArchiveCapture: archiveCapture,
        SearchEnumeration: searchEnumeration,
        AssessmentBundle: assessmentBundle,
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
        Dictionary<string, PriceHistory> prices,
        Radar.Application.Efficacy.Comparison.IUniverseBenchmarkProvider? benchmarkProvider = null)
    {
        var artifacts = new CapturingArtifactStore();
        var generator = new NewsRiskEvaluationGenerator(
            new FakeAssessmentStore(assessments),
            new FakeDevSource(examples),
            new FakeBoundaryReader(boundary),
            new FakePriceStore(prices),
            artifacts,
            benchmarkProvider ?? new Efficacy.Comparison.FixedUniverseBenchmarkProvider(null),
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
        Assert.Contains(",PresenceClaim,", line);
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
        Assert.Contains(",PresenceClaim,", CsvLineFor(csv, clean));
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
    public async Task NoBoundary_MeansNoProspectiveClaimRow()
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
        Assert.DoesNotContain(",PresenceClaim,", csv);
        Assert.DoesNotContain(",AbsenceClaim,", csv);
    }

    [Fact]
    public async Task DegradedDimensions_NeverExcludeAPresenceClaim_AndAggregatesStateTheCombination()
    {
        // Spec 182 §4: a validated risk found over degraded coverage is a presence claim — admitted at ANY
        // completeness, segmented (never silently pooled) by the dimension combination.
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var degraded = Assessment(
            "AAA",
            searchEnumeration: NewsRiskSearchEnumeration.Truncated,
            assessmentBundle: NewsRiskAssessmentBundle.Capped);
        var bestState = Assessment("BBB");

        var (markdown, csv) = await RunAsync(
            [degraded, bestState], [], EstablishedBoundary(),
            new() { ["AAA"] = History("AAA", start, end), ["BBB"] = History("BBB", start, end) });

        Assert.Contains(",PresenceClaim,", CsvLineFor(csv, degraded));
        Assert.Contains(",PresenceClaim,", CsvLineFor(csv, bestState));
        // The dimensions are their own CSV columns.
        Assert.Contains(",Proven,Truncated,Capped,", CsvLineFor(csv, degraded));
        Assert.Contains(",Proven,Complete,Complete,", CsvLineFor(csv, bestState));
        // Degraded and best-state presence rows never pool into one aggregate: each aggregate line states
        // the dimension combination it covers, so two combinations mean two lines.
        Assert.Contains(
            "Flagged (PresenceClaim) [archiveCapture=Proven, searchEnumeration=Truncated, "
                + "assessmentBundle=Capped]",
            markdown);
        Assert.Contains(
            "Flagged (PresenceClaim) [archiveCapture=Proven, searchEnumeration=Complete, "
                + "assessmentBundle=Complete]",
            markdown);
        // Nothing named "clean" exists to admit caveated rows.
        Assert.DoesNotContain("Clean prospective", markdown);
        Assert.DoesNotContain("CleanProspective", csv);
    }

    [Fact]
    public async Task AbsenceClaims_RequireBestStateDimensions_OnEveryDimension()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var admitted = Assessment("AAA", status: NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText);
        var capped = Assessment(
            "BBB",
            status: NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText,
            searchEnumeration: NewsRiskSearchEnumeration.Truncated,
            assessmentBundle: NewsRiskAssessmentBundle.Capped);
        var unproven = Assessment(
            "CCC",
            status: NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText,
            archiveCapture: NewsRiskArchiveCapture.Unproven);

        var (markdown, csv) = await RunAsync(
            [admitted, capped, unproven], [], EstablishedBoundary(),
            new()
            {
                ["AAA"] = History("AAA", start, end),
                ["BBB"] = History("BBB", start, end),
                ["CCC"] = History("CCC", start, end),
            });

        Assert.Contains(",AbsenceClaim,", CsvLineFor(csv, admitted));
        // A degraded "found nothing" was never a claim: Excluded with the degraded dimensions named.
        var cappedLine = CsvLineFor(csv, capped);
        Assert.Contains(",Excluded,", cappedLine);
        Assert.Contains(
            "absence-claim-requires-complete-coverage: searchEnumeration=Truncated,bundle=Capped",
            cappedLine);
        var unprovenLine = CsvLineFor(csv, unproven);
        Assert.Contains(",Excluded,", unprovenLine);
        Assert.Contains(
            "absence-claim-requires-complete-coverage: archiveCapture=Unproven", unprovenLine);
        // The non-flagged descriptive accounting draws ONLY from admitted absence rows: n=1, not 3.
        Assert.Contains("Non-flagged (AbsenceClaim — complete coverage only): n=1", markdown);
    }

    [Fact]
    public async Task V1Records_DegradedByDefault_CanNeverEnterTheAbsenceCohort()
    {
        // A v1 record deserializes with every dimension at its zero (degraded) default — the enum-zero rule
        // is the migration: no legacy "found nothing" can ever read as a complete-coverage absence claim.
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var legacy = Assessment(
            "AAA",
            status: NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText,
            archiveCapture: default,
            searchEnumeration: default,
            assessmentBundle: default) with
        {
            SchemaVersion = "news-risk-assessment-v1",
        };

        // A legacy row still carrying the retired IncompleteCoverage status lands in Excluded through the
        // existing not-completed-validated gate (it was never a completed analysis).
#pragma warning disable CS0618 // deliberately exercising the retired v1 status
        var legacyStatus = Assessment(
            "BBB",
            status: NewsRiskAssessmentStatus.IncompleteCoverage,
            riskScore: null,
            archiveCapture: default,
            searchEnumeration: default,
            assessmentBundle: default) with
        {
            SchemaVersion = "news-risk-assessment-v1",
        };
#pragma warning restore CS0618

        var (_, csv) = await RunAsync(
            [legacy, legacyStatus], [], EstablishedBoundary(),
            new() { ["AAA"] = History("AAA", start, end), ["BBB"] = History("BBB", start, end) });

        var line = CsvLineFor(csv, legacy);
        Assert.Contains(",Excluded,", line);
        Assert.Contains("absence-claim-requires-complete-coverage", line);
        var statusLine = CsvLineFor(csv, legacyStatus);
        Assert.Contains(",Excluded,", statusLine);
        Assert.Contains("assessment-not-completed-validated: IncompleteCoverage", statusLine);
    }

    [Fact]
    public async Task NonCompletedStatuses_StayExcluded_WithTheNamedReason()
    {
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);
        var failed = Assessment("BBB", status: NewsRiskAssessmentStatus.ProviderFailure);

        var (_, csv) = await RunAsync(
            [failed], [], EstablishedBoundary(), new() { ["BBB"] = History("BBB", start, end) });

        var line = CsvLineFor(csv, failed);
        Assert.Contains(",Excluded,", line);
        Assert.Contains("assessment-not-completed-validated", line);
    }

    [Fact]
    public async Task TheEvaluatorCaveat_IsCarriedVerbatim()
    {
        var (markdown, _) = await RunAsync([], [], null, new());

        Assert.Contains(NewsRiskEvaluationGenerator.EvaluatorCaveat, markdown);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 183 §3: rows carry the raw AND the excess forward return, BOTH labelled descriptive; the
    // RiskScore association keeps its raw max-adverse basis; unavailability is named, never a raw fallback.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A full-coverage benchmark over the assessed company: the target plus 43 flat peers and one +5%
    /// peer (45 members ⇒ 44 eligible ⇒ required = max(40, ceil(0.9 × 44) = 40) = 40, all 44 resolving),
    /// so the excess differs from the raw by exactly the peer mean 0.05 / 44.
    /// </summary>
    private static Radar.Application.Efficacy.Comparison.UniverseBenchmark BenchmarkFor(Guid companyId)
    {
        var asOf = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime);
        var start = asOf.AddDays(-5);

        static IReadOnlyList<PriceBar> Series(DateOnly start, decimal startPrice, decimal dailyStep)
        {
            var bars = new List<PriceBar>();
            var price = startPrice;
            for (var d = 0; d < 40; d++)
            {
                var date = start.AddDays(d);
                bars.Add(new PriceBar(date, price, price, price, price, price, 1000));
                price += dailyStep;
            }

            return bars;
        }

        var members = new List<(Guid, string, IReadOnlyList<PriceBar>)>
        {
            (companyId, "AAA", Series(start, 100m, 1m)),
            (new Guid("aaaaaaaa-1111-1111-1111-111111111111"), "UP", Series(start, 100m, 0.25m)),
        };
        for (var p = 0; p < 43; p++)
        {
            members.Add((
                Efficacy.Comparison.BenchmarkTestUniverse.PeerId(p),
                $"FL{p:D2}",
                Series(start, 100m, 0m)));
        }

        return Efficacy.Comparison.BenchmarkTestUniverse.Of(
            "benchmark-universe-v1",
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            members);
    }

    [Fact]
    public async Task Rows_CarryRawAndExcessForwardReturns_BothLabelledDescriptive_MaxAdverseLabelledRaw()
    {
        var companyId = new Guid("aaaaaaaa-2222-2222-2222-222222222222");
        var record = Assessment("AAA", companyId: companyId);
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);

        var (markdown, csv) = await RunAsync(
            [record],
            [],
            EstablishedBoundary(),
            new() { ["AAA"] = History("AAA", start, end) },
            new Efficacy.Comparison.FixedUniverseBenchmarkProvider(BenchmarkFor(companyId)));

        // Both return columns exist and are labelled: raw as raw, excess against the named frozen universe,
        // max adverse as raw — and everything descriptive (no claim language anywhere in this artifact).
        Assert.Contains("Fwd 21d (raw, descriptive)", markdown);
        Assert.Contains("Excess fwd 21d vs universe-v1 (descriptive)", markdown);
        Assert.Contains("Max adverse 21d (raw)", markdown);
        Assert.Contains("Forward returns are DESCRIPTIVE, in both forms (spec 183)", markdown);
        Assert.Contains("RAW max adverse move", markdown);

        var line = CsvLineFor(csv, record);
        Assert.Contains(",excess-vs-benchmark-universe-v1,", line);
        Assert.Contains(
            "rawForwardReturn21d,excessForwardReturn21d,excessForwardReturn21dBasis,maxAdverseMove21dRaw",
            csv.Split('\n')[0]);

        // The excess value genuinely differs from the raw one (the +5%-peer moves the mean), so the two
        // columns cannot silently be one series.
        var cells = line.Split(',');
        var header = csv.Split('\n')[0].Split(',');
        var rawIndex = Array.IndexOf(header, "rawForwardReturn21d");
        var excessIndex = Array.IndexOf(header, "excessForwardReturn21d");
        Assert.True(rawIndex >= 0 && excessIndex >= 0);
        Assert.NotEqual(cells[rawIndex], cells[excessIndex]);
        Assert.False(string.IsNullOrEmpty(cells[excessIndex]));
    }

    [Fact]
    public async Task UnavailableBenchmark_IsANamedBasis_NeverARawFallback()
    {
        var record = Assessment("AAA");
        var start = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(-5);
        var end = DateOnly.FromDateTime(SelectionAsOf.UtcDateTime).AddDays(25);

        // The default RunAsync provider hands out a null universe: raw resolves, excess cannot.
        var (_, csv) = await RunAsync(
            [record], [], EstablishedBoundary(), new() { ["AAA"] = History("AAA", start, end) });

        var line = CsvLineFor(csv, record);
        var header = csv.Split('\n')[0].Split(',');
        var cells = line.Split(',');
        Assert.Equal("benchmark-unavailable", cells[Array.IndexOf(header, "excessForwardReturn21dBasis")]);
        Assert.Equal(string.Empty, cells[Array.IndexOf(header, "excessForwardReturn21d")]);
        Assert.NotEqual(string.Empty, cells[Array.IndexOf(header, "rawForwardReturn21d")]);
    }
}
