using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.SignalExtraction;
using Radar.Application.Storage;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

public sealed class DailyNewsReportRendererTests
{
    private static readonly NewsJudgmentSignalMaterializationSummary EmptyAccounting =
        NewsJudgmentSignalMaterializationSummary.Empty;

    private static DailyNewsReportRow Row(
        string company, SignalDirection direction, int strength, string? trajectory = "improving",
        string headline = "Acme reports quarterly results", Guid? signalId = null) =>
        new(
            SignalId: signalId ?? Guid.NewGuid(),
            EvidenceId: Guid.NewGuid(),
            JudgmentId: Guid.NewGuid(),
            CompanyId: Guid.NewGuid(),
            CompanyName: company,
            Direction: direction,
            Strength: strength,
            Confidence: 0.5m,
            JudgedTrajectory: trajectory,
            Headline: headline);

    [Fact]
    public void OrdersPositivesByStrengthThenNegatives_AndEscapesTableCells()
    {
        var report = new DailyNewsReport(
            RunId: Guid.NewGuid(),
            GeneratedAtUtc: new DateTimeOffset(2026, 9, 2, 21, 46, 0, TimeSpan.Zero),
            Rows:
            [
                Row("Monro", SignalDirection.Negative, 5, "deteriorating"),
                Row("Cass Information Systems", SignalDirection.Positive, 8),
                Row("Liberty | Energy", SignalDirection.Positive, 7, headline: "3 GW | buildout\ncontinues"),
            ],
            Accounting: EmptyAccounting,
            MaterializedNotResolved: 0);

        var markdown = DailyNewsReportRenderer.Render(report);

        var cass = markdown.IndexOf("Cass Information Systems", StringComparison.Ordinal);
        var liberty = markdown.IndexOf("Liberty \\| Energy", StringComparison.Ordinal);
        var monro = markdown.IndexOf("Monro", StringComparison.Ordinal);
        Assert.True(cass >= 0 && liberty >= 0 && monro >= 0, markdown);
        Assert.True(cass < liberty, "strongest positive first");
        Assert.True(liberty < monro, "negatives after positives");

        // Cell content is one escaped line: the pipe survives escaped and the newline is collapsed.
        Assert.Contains("3 GW \\| buildout continues", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be resolved", markdown, StringComparison.Ordinal); // no unresolved line at 0
    }

    [Fact]
    public void EmptyDayRendersHonestMessage_AndNullsRenderAsNotRecorded()
    {
        var report = new DailyNewsReport(
            RunId: null,
            GeneratedAtUtc: new DateTimeOffset(2026, 9, 2, 21, 46, 0, TimeSpan.Zero),
            Rows: [],
            Accounting: EmptyAccounting,
            MaterializedNotResolved: 0);

        var markdown = DailyNewsReportRenderer.Render(report);

        Assert.Contains("No judged directional news was minted by this run", markdown, StringComparison.Ordinal);
        Assert.Contains("Run: not recorded", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedCountIsSurfaced_NeverSilentlyNarrowed()
    {
        var report = new DailyNewsReport(
            RunId: Guid.NewGuid(),
            GeneratedAtUtc: new DateTimeOffset(2026, 9, 2, 21, 46, 0, TimeSpan.Zero),
            Rows: [Row("Acme", SignalDirection.Positive, 5)],
            Accounting: EmptyAccounting,
            MaterializedNotResolved: 2);

        var markdown = DailyNewsReportRenderer.Render(report);

        Assert.Contains("2 materialized signal(s) could not be resolved", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverEmitsForbiddenAdviceLanguage()
    {
        var report = new DailyNewsReport(
            RunId: Guid.NewGuid(),
            GeneratedAtUtc: new DateTimeOffset(2026, 9, 2, 21, 46, 0, TimeSpan.Zero),
            Rows: [Row("Acme", SignalDirection.Positive, 5)],
            Accounting: EmptyAccounting,
            MaterializedNotResolved: 1);

        var markdown = DailyNewsReportRenderer.Render(report).ToLowerInvariant();

        // The hard output-language rule. "buy"/"sell" are checked as words so e.g. "sell-side" in a cited
        // headline would be the CITATION's word, not Radar's — the template itself must never introduce them.
        Assert.DoesNotContain("guaranteed upside", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("safe bet", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(" buy ", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(" sell ", markdown, StringComparison.Ordinal);
        Assert.Contains("Not financial advice", DailyNewsReportRenderer.Render(report), StringComparison.Ordinal);
    }
}

public sealed class DailyNewsReportStepTests
{
    private sealed class FakeSignalRepository : ISignalRepository
    {
        public Dictionary<Guid, List<Signal>> ByCompany { get; } = [];
        public bool Throw { get; set; }

        public Task AddAsync(Signal signal, CancellationToken ct) => throw new NotSupportedException();

        public Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct)
        {
            if (Throw)
            {
                throw new IOException("store unavailable");
            }

            return Task.FromResult<IReadOnlyList<Signal>>(
                ByCompany.TryGetValue(companyId, out var list) ? list : []);
        }

        public Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
            DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWriter : IDailyNewsReportWriter
    {
        public string? Markdown { get; private set; }
        public int Writes { get; private set; }
        public bool FailWrite { get; set; }

        public Task<DurableWriteResult> WriteAsync(
            DateTimeOffset generatedAtUtc, string markdown, CancellationToken ct)
        {
            Writes++;
            Markdown = markdown;
            return Task.FromResult(DurableWriteResult.From("daily/test.md", !FailWrite));
        }
    }

    private static readonly Guid CompanyA = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid CompanyB = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private static NewsJudgmentRecord Judgment(Guid judgmentId, Guid companyId, string companyName) =>
        new(
            SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
            JudgmentId: judgmentId,
            RunId: Guid.NewGuid(),
            CompanyId: companyId,
            CompanyName: companyName,
            Ticker: "TICK",
            JudgeName: "judge",
            Provider: "openai",
            ModelId: "judge-model",
            PromptVersion: NewsJudgmentContract.PromptVersion,
            ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
            Stage1CohortKey: "s1",
            TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
            TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
            FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
            CohortKey: "cohort",
            FamilySetHash: "hash",
            Families: [new NewsJudgmentFamilyRef(Guid.NewGuid(), Guid.NewGuid(), 3, 2)],
            ArchiveCapture: NewsRiskArchiveCapture.Proven,
            SearchEnumeration: NewsRiskSearchEnumeration.Complete,
            ObservationSupply: NewsRiskAssessmentBundle.Complete,
            TypingCompleteness: NewsTypingCompleteness.Complete,
            FamilyBundle: NewsJudgmentFamilyBundle.Complete,
            CoverageIssues: [],
            Status: NewsJudgmentStatus.Judged,
            BusinessTrajectory: NewsJudgmentTrajectory.Improving,
            ChallengeStrength: null,
            Findings: [],
            Rationale: "Factual read.",
            FindingsTotal: 0,
            FindingsAccepted: 0,
            FindingsDropped: 0,
            FindingDropReasons: [],
            RawResponseHash: "raw",
            FailureDetail: null,
            Limits: new NewsJudgmentLimitsRecord(30, 50, 3),
            ReusedFromJudgmentId: null,
            CreatedAtUtc: new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            TrajectoryFactIds: []);

    private static NewsJudgmentRunResult RunResult(
        NewsJudgmentSignalMaterializationSummary? materialization, params NewsJudgmentRecord[] judgments) =>
        new(
            Judgments: judgments,
            Markers: null,
            Stage1FactsDroppedByCohort: new Dictionary<string, int>(),
            SignalMaterialization: materialization);

    private static NewsJudgmentSignalMaterializationSummary Accounting(
        int materialized, int alreadyMaterialized = 0) =>
        NewsJudgmentSignalMaterializationSummary.Empty with
        {
            JudgmentsConsidered = materialized,
            Eligible = materialized,
            Materialized = materialized,
            AlreadyMaterialized = alreadyMaterialized,
        };

    private static Signal JudgmentDerivedSignal(Guid companyId, Guid judgmentId, string headline) =>
        new SignalBuilder()
            .WithCompanyId(companyId)
            .WithType(SignalType.MediaAttention)
            .WithDirection(SignalDirection.Positive)
            .WithStrength(7)
            .WithSupportingExcerpt(headline)
            .WithMetadataJson(NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
                judgmentId, "cohort", "improving", [Guid.NewGuid()], [Guid.NewGuid()], [Guid.NewGuid()]))
            .Build();

    private static DailyNewsReportStep Step(FakeSignalRepository repo, FakeWriter writer) =>
        new(repo, writer, TimeProvider.System, NullLogger<DailyNewsReportStep>.Instance);

    [Fact]
    public async Task NoJudgment_OrMaterializationNotAttempted_WritesNothing()
    {
        var repo = new FakeSignalRepository();
        var writer = new FakeWriter();
        var step = Step(repo, writer);

        await step.RunAsync(Guid.NewGuid(), judgment: null, CancellationToken.None);
        await step.RunAsync(
            Guid.NewGuid(),
            RunResult(materialization: null, Judgment(Guid.NewGuid(), CompanyA, "Acme")),
            CancellationToken.None);

        Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task ResolvesOnlyThisRunsJudgmentSignals_AndCountsWhatItCannotResolve()
    {
        var thisRunJudgment = Guid.NewGuid();
        var earlierRunJudgment = Guid.NewGuid();
        var repo = new FakeSignalRepository();
        repo.ByCompany[CompanyA] =
        [
            JudgmentDerivedSignal(CompanyA, thisRunJudgment, "Acme doubles quarterly profit"),
            // Ordinary attention signal with no envelope: valid, but not judged directional news.
            new SignalBuilder().WithCompanyId(CompanyA).WithType(SignalType.MediaAttention)
                .WithDirection(SignalDirection.Neutral).Build(),
        ];
        // Company B's only judgment-derived signal belongs to an EARLIER run's judgment.
        repo.ByCompany[CompanyB] =
            [JudgmentDerivedSignal(CompanyB, earlierRunJudgment, "Old Beta news")];
        var writer = new FakeWriter();
        var step = Step(repo, writer);

        // The materializer claims two signals for this pass; only one resolves under this run's ids.
        await step.RunAsync(
            Guid.NewGuid(),
            RunResult(
                Accounting(materialized: 2),
                Judgment(thisRunJudgment, CompanyA, "Acme Corp"),
                Judgment(Guid.NewGuid(), CompanyB, "Beta Corp")),
            CancellationToken.None);

        Assert.Equal(1, writer.Writes);
        var markdown = writer.Markdown!;
        Assert.Contains("Acme doubles quarterly profit", markdown, StringComparison.Ordinal);
        Assert.Contains("Acme Corp", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Old Beta news", markdown, StringComparison.Ordinal);
        Assert.Contains("1 materialized signal(s) could not be resolved", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreFailure_IsSwallowedAndWritesNothing_TheRunIsUnaffected()
    {
        var repo = new FakeSignalRepository { Throw = true };
        var writer = new FakeWriter();
        var step = Step(repo, writer);

        await step.RunAsync(
            Guid.NewGuid(),
            RunResult(Accounting(materialized: 1), Judgment(Guid.NewGuid(), CompanyA, "Acme Corp")),
            CancellationToken.None);

        Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task FailedReportWrite_DoesNotThrow()
    {
        var thisRunJudgment = Guid.NewGuid();
        var repo = new FakeSignalRepository();
        repo.ByCompany[CompanyA] = [JudgmentDerivedSignal(CompanyA, thisRunJudgment, "Acme headline")];
        var writer = new FakeWriter { FailWrite = true };
        var step = Step(repo, writer);

        await step.RunAsync(
            Guid.NewGuid(),
            RunResult(Accounting(materialized: 1), Judgment(thisRunJudgment, CompanyA, "Acme Corp")),
            CancellationToken.None);

        Assert.Equal(1, writer.Writes);
    }
}
