using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Infrastructure.NewsRisk;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// Spec 179 §6: the durable assessment store — lossless round-trip through a FRESH instance (hydration),
/// completed-only cache reads, and distinct records per cohort/input identity (an incompatible assessment
/// is never overwritten or reused).
/// </summary>
public sealed class FileNewsRiskAssessmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-newsrisk-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private FileNewsRiskAssessmentStore NewStore() => new(
        new FileNewsRiskAssessmentStoreOptions { RootDirectory = _root },
        NullLogger<FileNewsRiskAssessmentStore>.Instance);

    private static NewsRiskAssessmentRecord Record(
        string cohortKey = "test:model-a|news-risk-prompt-v1|news-risk-schema-v1",
        string bundleHash = "bundle-1",
        Guid? runId = null,
        NewsRiskAssessmentStatus status = NewsRiskAssessmentStatus.ThesisChallenged)
    {
        var run = runId ?? Guid.NewGuid();
        return new NewsRiskAssessmentRecord(
            SchemaVersion: NewsRiskAssessmentRecord.CurrentSchemaVersion,
            AssessmentId: NewsRiskAssessmentRecord.IdentityFor(cohortKey, bundleHash, run, "reader"),
            RunId: run,
            SelectionAsOfUtc: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            AssessmentCutoffUtc: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            CompanyId: Guid.NewGuid(),
            CompanyName: "Test Co",
            Ticker: "TST",
            Selections: [new NewsRiskCandidateSelection("default", 1, Guid.NewGuid())],
            ReaderName: "reader",
            Provider: "test",
            ModelId: "model-a",
            PromptVersion: NewsRiskAnalysisContract.PromptVersion,
            ResultSchemaVersion: NewsRiskAnalysisContract.SchemaVersion,
            CohortKey: cohortKey,
            InputBundleHash: bundleHash,
            Observations:
            [
                new NewsRiskInputObservationRef(
                    Guid.NewGuid(), "ph", DescriptionSupplied: true, BodySupplied: false,
                    BodyContentHash: null, BodyRetrievedAtUtc: null, BodyExtractorVersion: null,
                    BodyRetrievalPolicy: null, CaptureMode: NewsObservationCaptureMode.ProspectiveRss),
            ],
            ArchiveCapture: NewsRiskArchiveCapture.Proven,
            SearchEnumeration: NewsRiskSearchEnumeration.Complete,
            AssessmentBundle: NewsRiskAssessmentBundle.Complete,
            CoverageIssues: [],
            Status: status,
            RiskScore: status == NewsRiskAssessmentStatus.ThesisChallenged ? 66 : null,
            Categories: status == NewsRiskAssessmentStatus.ThesisChallenged
                ? [NewsRiskCategory.LiquidityOrGoingConcern]
                : [],
            Claims:
            [
                new NewsRiskValidatedClaim(
                    NewsRiskCategory.LiquidityOrGoingConcern,
                    NewsRiskSeverity.High,
                    0.8,
                    [Guid.NewGuid()],
                    ["going concern"]),
            ],
            Rationale: "rationale",
            ClaimsTotal: 1,
            ClaimsAccepted: 1,
            ClaimsDropped: 0,
            ClaimDropReasons: [],
            RawResponseHash: "raw",
            FailureDetail: null,
            Limits: new NewsRiskShadowLimitsRecord(30, 30, 12, 3),
            ReusedFromAssessmentId: null,
            CreatedAtUtc: new DateTimeOffset(2026, 8, 20, 12, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Write_RoundTripsLosslessly_ThroughAFreshInstance()
    {
        var record = Record();
        Assert.True(await NewStore().WriteAsync(record, CancellationToken.None));

        var hydrated = await NewStore().GetAllAsync(CancellationToken.None);

        Assert.Equal(record.AssessmentId, Assert.Single(hydrated).AssessmentId);
        var read = hydrated[0];
        Assert.Equal(record.Status, read.Status);
        Assert.Equal(record.RiskScore, read.RiskScore);
        Assert.Equal(record.CohortKey, read.CohortKey);
        Assert.Equal(record.InputBundleHash, read.InputBundleHash);
        Assert.Equal(record.Selections, read.Selections);
        Assert.Equal(record.Categories, read.Categories);
        Assert.Equal(
            NewsObservationCaptureMode.ProspectiveRss,
            Assert.Single(read.Observations).CaptureMode);
        Assert.Equal(record.AssessmentCutoffUtc, read.AssessmentCutoffUtc);
        Assert.Equal(record.Limits, read.Limits);
        // The spec-182 completeness dimensions round-trip losslessly.
        Assert.Equal(NewsRiskArchiveCapture.Proven, read.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Complete, read.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Complete, read.AssessmentBundle);
    }

    [Fact]
    public async Task LegacyV1File_HydratesWithEveryDimensionAtItsDegradedDefault_NeverBestState()
    {
        // A pre-spec-182 file carries `coverageComplete` (now ignored), the retired IncompleteCoverage
        // status, and NO dimension fields. Each dimension enum's zero value is DELIBERATELY the degraded
        // state, so the missing fields deserialize as Unproven/Unproven/Capped — a legacy record can never
        // read as best-state on any dimension.
        var id = Guid.NewGuid();
        var path = Path.Combine(_root, "assessments", "2026", "08", id.ToString("D") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""
            {
              "schemaVersion": "news-risk-assessment-v1",
              "assessmentId": "{{id:D}}",
              "runId": "{{Guid.NewGuid():D}}",
              "selectionAsOfUtc": "2026-08-20T12:00:00+00:00",
              "assessmentCutoffUtc": "2026-08-20T12:00:00+00:00",
              "companyId": "{{Guid.NewGuid():D}}",
              "companyName": "Legacy Co",
              "ticker": "LGC",
              "selections": [],
              "readerName": "reader",
              "provider": "test",
              "modelId": "model-a",
              "promptVersion": "news-risk-prompt-v1",
              "resultSchemaVersion": "news-risk-schema-v1",
              "cohortKey": "test:model-a|news-risk-prompt-v1|news-risk-schema-v1",
              "inputBundleHash": "bundle-legacy",
              "observations": [],
              "coverageComplete": true,
              "coverageIssues": [],
              "status": "IncompleteCoverage",
              "riskScore": null,
              "categories": [],
              "claims": [],
              "rationale": null,
              "claimsTotal": 0,
              "claimsAccepted": 0,
              "claimsDropped": 0,
              "claimDropReasons": [],
              "rawResponseHash": null,
              "failureDetail": null,
              "limits": { "lookbackDays": 30, "maxCompaniesPerRun": 30, "maxArticlesPerCompany": 12, "maxFetchedArticlesPerCompany": 3 },
              "reusedFromAssessmentId": null,
              "createdAtUtc": "2026-08-20T12:05:00+00:00"
            }
            """);

        var hydrated = await NewStore().GetAllAsync(CancellationToken.None);

        var read = Assert.Single(hydrated);
        Assert.Equal(id, read.AssessmentId);
        Assert.Equal("news-risk-assessment-v1", read.SchemaVersion);
        Assert.Equal(NewsRiskArchiveCapture.Unproven, read.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Unproven, read.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Capped, read.AssessmentBundle);
#pragma warning disable CS0618 // the retired status must still deserialize from accrued v1 files
        Assert.Equal(NewsRiskAssessmentStatus.IncompleteCoverage, read.Status);
#pragma warning restore CS0618
        Assert.False(read.IsCompletedAnalysis); // and it is never reusable through the §6 cache
    }

    [Fact]
    public async Task FindCompleted_ReturnsCompletedAnalysesOnly_NeverAProviderFailure()
    {
        var store = NewStore();
        await store.WriteAsync(
            Record(bundleHash: "bundle-x", status: NewsRiskAssessmentStatus.ProviderFailure),
            CancellationToken.None);

        // A provider failure is persisted but never reused — a retry may genuinely succeed.
        Assert.Null(await store.FindCompletedAsync(
            "test:model-a|news-risk-prompt-v1|news-risk-schema-v1", "bundle-x", CancellationToken.None));

        var completed = Record(bundleHash: "bundle-x");
        await store.WriteAsync(completed, CancellationToken.None);

        var found = await store.FindCompletedAsync(
            "test:model-a|news-risk-prompt-v1|news-risk-schema-v1", "bundle-x", CancellationToken.None);
        Assert.Equal(completed.AssessmentId, found?.AssessmentId);
    }

    [Fact]
    public async Task DifferentCohortOrBundle_NeverMatchesTheCache()
    {
        var store = NewStore();
        await store.WriteAsync(Record(), CancellationToken.None);

        Assert.Null(await store.FindCompletedAsync(
            "test:model-B|news-risk-prompt-v1|news-risk-schema-v1", "bundle-1", CancellationToken.None));
        Assert.Null(await store.FindCompletedAsync(
            "test:model-a|news-risk-prompt-v1|news-risk-schema-v1", "bundle-2", CancellationToken.None));
    }

    [Fact]
    public async Task SameDeterministicIdentity_IsInsertOnly_NeverOverwritten()
    {
        var store = NewStore();
        var runId = Guid.NewGuid();
        var original = Record(runId: runId);
        await store.WriteAsync(original, CancellationToken.None);

        // A re-run of the SAME run/reader/input mints the same id; the write dedupes rather than
        // overwriting, and exactly one record survives.
        var duplicate = Record(runId: runId) with { Rationale = "edited" };
        Assert.True(await store.WriteAsync(duplicate, CancellationToken.None));

        var all = await store.GetAllAsync(CancellationToken.None);
        Assert.Equal("rationale", Assert.Single(all).Rationale);
    }
}
