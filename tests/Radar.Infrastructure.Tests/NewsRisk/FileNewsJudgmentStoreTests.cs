using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsRisk;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// Spec 185 §5: the durable judgment store — insert-only writes under
/// <c>{root}/judgments/{judge-policy-segment}/{companyId}/…</c>, lossless round-trip through a FRESH
/// instance (hydration), completed-only cache reads (a provider/parse/validation failure is persisted but
/// never reused), and a malformed file logged and skipped.
/// </summary>
public sealed class FileNewsJudgmentStoreTests : IDisposable
{
    private static readonly Guid Company = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunA = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid RunB = new("dddddddd-0000-0000-0000-000000000002");

    private const string CohortKey =
        "openai:judge-model|news-judgment-prompt-v1|news-judgment-schema-v1|stage1=s1|families=fact-family-v1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-newsjudgment-tests-" + Guid.NewGuid().ToString("N"));

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

    private FileNewsJudgmentStore NewStore() => new(
        new FileNewsJudgmentStoreOptions { RootDirectory = _root },
        NullLogger<FileNewsJudgmentStore>.Instance);

    private static NewsJudgmentRecord Record(
        Guid? runId = null,
        NewsJudgmentStatus status = NewsJudgmentStatus.Judged,
        string familySetHash = "fsh-1")
    {
        var run = runId ?? RunA;
        return new NewsJudgmentRecord(
            SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
            JudgmentId: NewsJudgmentRecord.IdentityFor(CohortKey, Company, familySetHash, run),
            RunId: run,
            CompanyId: Company,
            CompanyName: "Eos Energy",
            Ticker: "EOSE",
            JudgeName: "judge",
            Provider: "openai",
            ModelId: "judge-model",
            PromptVersion: NewsJudgmentContract.PromptVersion,
            ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
            Stage1CohortKey: "s1",
            TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
            TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
            FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
            CohortKey: CohortKey,
            FamilySetHash: familySetHash,
            Families: [new NewsJudgmentFamilyRef(Guid.NewGuid(), Guid.NewGuid(), 3, 2)],
            ArchiveCapture: NewsRiskArchiveCapture.Proven,
            SearchEnumeration: NewsRiskSearchEnumeration.Complete,
            ObservationSupply: NewsRiskAssessmentBundle.Complete,
            TypingCompleteness: NewsTypingCompleteness.Complete,
            FamilyBundle: NewsJudgmentFamilyBundle.Complete,
            CoverageIssues: [],
            Status: status,
            BusinessTrajectory: status == NewsJudgmentStatus.Judged ? NewsJudgmentTrajectory.Mixed : null,
            ChallengeStrength: null,
            Findings:
            [
                new NewsJudgmentValidatedFinding(
                    NewsRiskCategory.RegulatoryOrLegalSetback,
                    NewsRiskSeverity.High,
                    0.8,
                    [Guid.NewGuid()],
                    "Based solely on a plaintiff-firm solicitation."),
            ],
            Rationale: "Factual read.",
            FindingsTotal: 1,
            FindingsAccepted: 1,
            FindingsDropped: 0,
            FindingDropReasons: [],
            RawResponseHash: "raw",
            FailureDetail: null,
            Limits: new NewsJudgmentLimitsRecord(30, 50),
            ReusedFromJudgmentId: null,
            CreatedAtUtc: new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task WriteAndHydrate_RoundTripsThroughAFreshInstance()
    {
        var record = Record();
        Assert.True(await NewStore().WriteAsync(record, CancellationToken.None));

        var reloaded = Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));
        Assert.Equal(record.JudgmentId, reloaded.JudgmentId);
        Assert.Equal(record.CohortKey, reloaded.CohortKey);
        Assert.Equal(record.FamilySetHash, reloaded.FamilySetHash);
        Assert.Equal(record.Stage1CohortKey, reloaded.Stage1CohortKey);
        Assert.Equal(record.TypingCompleteness, reloaded.TypingCompleteness);
        Assert.Equal(record.FamilyBundle, reloaded.FamilyBundle);
        Assert.Equal(NewsJudgmentTrajectory.Mixed, reloaded.BusinessTrajectory);
        var finding = Assert.Single(reloaded.Findings);
        Assert.Equal(NewsRiskCategory.RegulatoryOrLegalSetback, finding.Category);
        Assert.Equal(record.Findings[0].FactIds[0], Assert.Single(finding.FactIds));
    }

    [Fact]
    public async Task Layout_IsJudgePolicySegmentThenCompanyId()
    {
        await NewStore().WriteAsync(Record(), CancellationToken.None);

        var file = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_root, "judgments"), "*.json", SearchOption.AllDirectories));
        Assert.Contains(
            Path.Combine("judgments", "openai-judge-model", Company.ToString("D")),
            file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameDeterministicId_IsInsertOnlyDeduped_NeverOverwritten()
    {
        var store = NewStore();
        Assert.True(await store.WriteAsync(Record(), CancellationToken.None));
        Assert.True(await store.WriteAsync(Record(), CancellationToken.None));

        Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindCompleted_ReturnsJudgedAndInsufficientFacts_NeverFailures()
    {
        var store = NewStore();
        await store.WriteAsync(
            Record(status: NewsJudgmentStatus.ProviderFailure, familySetHash: "fsh-pf"),
            CancellationToken.None);
        await store.WriteAsync(
            Record(status: NewsJudgmentStatus.ValidationFailed, familySetHash: "fsh-vf"),
            CancellationToken.None);
        await store.WriteAsync(
            Record(status: NewsJudgmentStatus.InsufficientFacts, familySetHash: "fsh-if"),
            CancellationToken.None);
        await store.WriteAsync(Record(familySetHash: "fsh-ok"), CancellationToken.None);

        var fresh = NewStore();
        Assert.Null(await fresh.FindCompletedAsync(CohortKey, Company, "fsh-pf", CancellationToken.None));
        Assert.Null(await fresh.FindCompletedAsync(CohortKey, Company, "fsh-vf", CancellationToken.None));
        Assert.NotNull(await fresh.FindCompletedAsync(CohortKey, Company, "fsh-if", CancellationToken.None));
        Assert.NotNull(await fresh.FindCompletedAsync(CohortKey, Company, "fsh-ok", CancellationToken.None));
    }

    [Fact]
    public async Task FindCompleted_MissesOnADifferentCohortOrFamilySet()
    {
        var store = NewStore();
        await store.WriteAsync(Record(), CancellationToken.None);

        Assert.Null(await store.FindCompletedAsync(
            CohortKey + "|other", Company, "fsh-1", CancellationToken.None));
        Assert.Null(await store.FindCompletedAsync(
            CohortKey, Company, "different-hash", CancellationToken.None));
        Assert.NotNull(await store.FindCompletedAsync(
            CohortKey, Company, "fsh-1", CancellationToken.None));
    }

    [Fact]
    public async Task ADifferentRun_MintsADifferentId_SoRetriesNeverCollide()
    {
        var store = NewStore();
        await store.WriteAsync(
            Record(RunA, NewsJudgmentStatus.ProviderFailure), CancellationToken.None);
        await store.WriteAsync(Record(RunB), CancellationToken.None);

        Assert.Equal(2, (await NewStore().GetAllAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task MalformedFile_IsSkippedOnHydration_NeverThrown()
    {
        await NewStore().WriteAsync(Record(), CancellationToken.None);
        var dir = Path.Combine(_root, "judgments", "openai-judge-model", Company.ToString("D"));
        await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), "{ not json");

        Assert.Single(await NewStore().GetAllAsync(CancellationToken.None));
    }
}
