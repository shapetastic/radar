using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.News;
using Radar.Application.Pipeline;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.News;

/// <summary>
/// Spec 196 §3 at the collection seam: the per-run attention publisher coverage summary rides the news
/// observation batch, partitions every ATTEMPTED candidate, and names the unclassified tail so the curated
/// map can be maintained against real volume.
/// <para>
/// It is a CAPTURE-FLOW diagnostic. These tests deliberately assert the partition property and the
/// unclassified ordering — never that the numbers here equal anything the scoring window consumes, because
/// they measure different populations.
/// </para>
/// </summary>
public sealed class CollectionPassAttentionCoverageTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static NewsObservationCandidate Candidate(string publisher, string url) =>
        new(
            CompanyId: CompanyId,
            Ticker: "RKLB",
            Collector: "newssearch",
            QueryPhrase: "Rocket Lab",
            FeedId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            FeedName: "Rocket Lab — News",
            GoogleLandingUrl: url,
            Publisher: publisher,
            PublisherSiteUrl: null,
            Headline: $"Rocket Lab wins new launch contract - {publisher}",
            DescriptionRaw: "<a>Rocket Lab wins new launch contract</a>",
            DescriptionText: "Rocket Lab wins new launch contract",
            DescriptionTruncated: false,
            PublishedAtUtc: FixedNow.AddHours(-3),
            RetrievedAtUtc: FixedNow.AddMinutes(-5));

    private static CollectedEvidence Evidence(string url) =>
        new(
            SourceType: EvidenceSourceType.NewsArticle,
            SourceName: "SpaceNews",
            SourceUrl: url,
            Title: "Rocket Lab wins new launch contract - SpaceNews",
            RawText: $"Rocket Lab wins new launch contract. Source: {url}",
            PublishedAt: FixedNow.AddHours(-3),
            CollectedAt: FixedNow.AddMinutes(-5),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed class FakeCollector(string name, CollectionResult result) : IEvidenceCollector
    {
        public string CollectorName => name;

        public EvidenceSourceType SourceType => EvidenceSourceType.NewsArticle;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class EmptyExtractor : ISignalExtractor
    {
        public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(new ExtractSignalsOutput([], "none"));
    }

    private sealed class NullSignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("(null)"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    private sealed class NullRawStore : IRawEvidenceStore
    {
        // Spec 206 §3: Written — every item is newly durable, so admission flows exactly as before.
        public Task<Radar.Application.Storage.DurableWriteResult> WriteIfNewAsync(
            EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(Radar.Application.Storage.DurableWriteResult.Succeeded("(null-raw-store)"));
    }

    private sealed class CleanHealthValidator : ICollectionHealthValidator
    {
        public Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(CollectionHealthReport.Empty);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (CollectionPass Pass, InMemoryNewsObservationArchive Archive) CreatePass(
        IReadOnlyList<NewsObservationCandidate> candidates,
        bool failWrites = false,
        ILogger<CollectionPass>? logger = null)
    {
        var companies = new InMemoryCompanyRepository();
        var archive = new InMemoryNewsObservationArchive { FailWrites = failWrites };
        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult(
                [Evidence("https://news.google.com/rss/articles/AAA")],
                CollectionSummary.Empty,
                null,
                candidates));

        var pass = new CollectionPass(
            [collector],
            new CollectedEvidenceMapper(new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance),
            new InMemoryEvidenceRepository(),
            new NullRawStore(),
            new EmptyExtractor(),
            new CompanyResolver(companies, NullLogger<CompanyResolver>.Instance),
            new DeterministicSignalReviewer(
                new FixedTime(FixedNow), NullLogger<DeterministicSignalReviewer>.Instance),
            new InMemorySignalRepository(),
            new InMemorySignalReviewRepository(),
            new NullSignalFileStore(),
            companies,
            new CleanHealthValidator(),
            new FixedTime(FixedNow),
            logger ?? NullLogger<CollectionPass>.Instance,
            new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default),
            newsObservationArchive: archive);

        return (pass, archive);
    }

    [Fact]
    public async Task Coverage_TierCounts_SumToObservationsAttempted()
    {
        // The §3 population rule, asserted: every candidate ATTEMPTED is in exactly one tier row, the
        // unclassified sentinel included. Two of these candidates are cross-run duplicates of each other,
        // so written + deduped < attempted — which is precisely why the partition must key off attempted.
        var candidates = new[]
        {
            Candidate("Reuters", "https://news.google.com/rss/articles/A"),
            Candidate("PR Newswire", "https://news.google.com/rss/articles/B"),
            Candidate("Yahoo Finance", "https://news.google.com/rss/articles/C"),
            Candidate("Seeking Alpha", "https://news.google.com/rss/articles/D"),
            Candidate("Some Outlet Nobody Audited", "https://news.google.com/rss/articles/E"),
            Candidate("Some Outlet Nobody Audited", "https://news.google.com/rss/articles/E"),
        };
        var (pass, archive) = CreatePass(candidates);

        await pass.RunAsync(CancellationToken.None);

        var batch = Assert.Single(archive.Batches);
        var coverage = Assert.IsType<AttentionPublisherCoverageSummary>(batch.AttentionPublisherCoverage);

        Assert.Equal(AttentionPublisherCoverageSummary.CurrentVersion, coverage.Version);
        Assert.Equal(candidates.Length, batch.ObservationsAttempted);
        Assert.Equal(batch.ObservationsAttempted, coverage.ObservationsAttempted);
        Assert.Equal(batch.ObservationsAttempted, coverage.Tiers.Sum(t => t.Observations));
        // The duplicate really was deduped, so this is not accidentally the written count.
        Assert.True(batch.ObservationsWritten < batch.ObservationsAttempted);

        Assert.Equal(1, TierCount(coverage, "Genuine"));
        Assert.Equal(1, TierCount(coverage, "Wire"));
        Assert.Equal(1, TierCount(coverage, "Mill"));
        Assert.Equal(1, TierCount(coverage, "Platform"));
        Assert.Equal(2, TierCount(coverage, AttentionSourceResolution.UnclassifiedTierName));
    }

    [Fact]
    public async Task Coverage_CountsFailedCandidates_SoAFailedRunIsNotSilentlyClean()
    {
        // "Written, cross-run deduped and FAILED alike": a run whose archive writes all fail still reports
        // what it attempted to capture, rather than an empty — and falsely reassuring — coverage picture.
        var candidates = new[]
        {
            Candidate("Reuters", "https://news.google.com/rss/articles/A"),
            Candidate("Some Outlet Nobody Audited", "https://news.google.com/rss/articles/B"),
        };
        var (pass, archive) = CreatePass(candidates, failWrites: true);

        await pass.RunAsync(CancellationToken.None);

        var batch = Assert.Single(archive.Batches);
        var coverage = batch.AttentionPublisherCoverage!;

        Assert.False(batch.CaptureProven);
        Assert.Equal(2, batch.ObservationsFailed);
        Assert.Equal(0, batch.ObservationsWritten);
        Assert.Equal(2, coverage.ObservationsAttempted);
        Assert.Equal(2, coverage.Tiers.Sum(t => t.Observations));
    }

    [Fact]
    public async Task Coverage_NamesUnclassifiedPublishers_ByDescendingVolume()
    {
        // The curation worklist: largest first, with a deterministic ordinal tie-break (AD-3). An audited
        // Mill publisher must NOT appear here despite sharing the unclassified weight — that is the whole
        // reason the diagnostic consumes Resolve rather than WeightFor.
        var candidates = new List<NewsObservationCandidate>();
        void Add(string publisher, int times)
        {
            for (var i = 0; i < times; i++)
            {
                candidates.Add(Candidate(publisher, $"https://news.google.com/rss/articles/{publisher}-{i}"));
            }
        }

        Add("Bravo Ledger", 3);
        Add("Alpha Chronicle", 5);
        Add("Charlie Gazette", 3);
        Add("Yahoo Finance", 9);   // audited Mill — must never be listed as unclassified
        Add("", 2);                // blank publisher — named, never folded into a real outlet

        var (pass, archive) = CreatePass(candidates);

        await pass.RunAsync(CancellationToken.None);

        var coverage = Assert.Single(archive.Batches).AttentionPublisherCoverage!;

        Assert.Equal(
            new[]
            {
                ("Alpha Chronicle", 5),
                ("Bravo Ledger", 3),
                ("Charlie Gazette", 3),
                (UnclassifiedPublisherCoverage.Unattributed, 2),
            },
            coverage.TopUnclassifiedPublishers.Select(p => (p.Publisher, p.Observations)));

        Assert.Equal(4, coverage.DistinctUnclassifiedPublishers);
        Assert.DoesNotContain(coverage.TopUnclassifiedPublishers, p => p.Publisher == "Yahoo Finance");
        Assert.Equal(9, TierCount(coverage, "Mill"));
        Assert.Equal(13, TierCount(coverage, AttentionSourceResolution.UnclassifiedTierName));
        Assert.Equal(coverage.ObservationsAttempted, coverage.Tiers.Sum(t => t.Observations));
    }

    [Theory]
    [InlineData("ZED Outlet", "Zed Outlet")]
    [InlineData("Zed Outlet", "ZED Outlet")]
    public async Task Coverage_UnclassifiedCasingVariants_AreOneRow_WithAnEncounterOrderIndependentName(
        string first, string second)
    {
        // One outlet whose feed capitalised its own name differently must be ONE curation worklist row, and
        // the rendered spelling must not depend on which variant the collector happened to yield first
        // (AD-3): the ordinally-smallest spelling wins in either order.
        var candidates = new List<NewsObservationCandidate>
        {
            Candidate(first, "https://news.google.com/rss/articles/A"),
            Candidate(second, "https://news.google.com/rss/articles/B"),
            Candidate(second, "https://news.google.com/rss/articles/C"),
        };

        var (pass, archive) = CreatePass(candidates);

        await pass.RunAsync(CancellationToken.None);

        var coverage = Assert.Single(archive.Batches).AttentionPublisherCoverage!;

        var row = Assert.Single(coverage.TopUnclassifiedPublishers);
        Assert.Equal("ZED Outlet", row.Publisher);
        Assert.Equal(3, row.Observations);
        Assert.Equal(1, coverage.DistinctUnclassifiedPublishers);
        Assert.Equal(3, TierCount(coverage, AttentionSourceResolution.UnclassifiedTierName));
        Assert.Equal(coverage.ObservationsAttempted, coverage.Tiers.Sum(t => t.Observations));
    }

    [Fact]
    public async Task Coverage_TopUnclassifiedList_IsCapped_ButTheDistinctCountIsNot()
    {
        // The head is bounded so one log line stays one log line; the SIZE of the tail is still reported,
        // so a capped list can never read as "that is all of them".
        var candidates = Enumerable.Range(0, 25)
            .Select(i => Candidate(
                $"Unlisted Outlet {i:D2}", $"https://news.google.com/rss/articles/{i:D2}"))
            .ToArray();
        var (pass, archive) = CreatePass(candidates);

        await pass.RunAsync(CancellationToken.None);

        var coverage = Assert.Single(archive.Batches).AttentionPublisherCoverage!;

        Assert.Equal(
            AttentionPublisherCoverageSummary.TopUnclassifiedPublisherLimit,
            coverage.TopUnclassifiedPublishers.Count);
        Assert.Equal(25, coverage.DistinctUnclassifiedPublishers);
        Assert.Equal(25, coverage.ObservationsAttempted);
    }

    [Fact]
    public async Task Batch_SchemaVersion_IsUnchanged_BecauseItIsSharedWithEveryObservationRecord()
    {
        // ⚠ The summary carries its OWN token. NewsObservationBatch.SchemaVersion is stamped with
        // NewsObservationRecord.CurrentSchemaVersion — the same const every individual observation record
        // carries — so bumping it would churn every observation record for an unrelated reason.
        var (pass, archive) = CreatePass(
            [Candidate("Reuters", "https://news.google.com/rss/articles/A")]);

        await pass.RunAsync(CancellationToken.None);

        var batch = Assert.Single(archive.Batches);

        Assert.Equal(NewsObservationRecord.CurrentSchemaVersion, batch.SchemaVersion);
        Assert.Equal("attention-publisher-coverage-v1", batch.AttentionPublisherCoverage!.Version);
        Assert.NotEqual(batch.SchemaVersion, batch.AttentionPublisherCoverage.Version);
    }

    [Fact]
    public void Batch_WithoutTheSummary_HydratesAsNull_MeaningNotRecorded()
    {
        // A pre-196 batch record carries no summary. The trailing member defaults to null = NOT RECORDED,
        // never an all-zero summary that would read as "this run captured nothing from any tier".
        var legacy = new NewsObservationBatch(
            BatchId: Guid.NewGuid(),
            RunAsOfUtc: FixedNow,
            SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
            FullUniverse: true,
            ObservationsAttempted: 7,
            ObservationsWritten: 7,
            ObservationsCrossRunDeduped: 0,
            ObservationsFailed: 0,
            CaptureProven: true,
            Collectors: []);

        Assert.Null(legacy.AttentionPublisherCoverage);
    }

    [Fact]
    public async Task Coverage_IsLogged_AsExactlyOneAggregatedLine_NeverOnePerPublisher()
    {
        // §3's aggregation rule (the spec-145 precedent), asserted rather than assumed: ONE Information line
        // per run for the WHOLE summary. This pass sees five distinct unclassified publishers over eleven
        // unclassified candidates, so a call moved inside the per-publisher (or per-candidate) loop would
        // emit five (or eleven) matching lines and fail the count — the mutation this test exists to catch.
        var candidates = new List<NewsObservationCandidate>();
        void Add(string publisher, int times)
        {
            for (var i = 0; i < times; i++)
            {
                candidates.Add(Candidate(publisher, $"https://news.google.com/rss/articles/{publisher}-{i}"));
            }
        }

        Add("Alpha Chronicle", 4);
        Add("Bravo Ledger", 3);
        Add("Charlie Gazette", 2);
        Add("Delta Dispatch", 1);
        Add("Echo Herald", 1);
        Add("Reuters", 2);         // classified — present in the tier shares, absent from the worklist
        Add("Yahoo Finance", 3);

        var logger = new CapturingLogger();
        var (pass, archive) = CreatePass(candidates, logger: logger);

        await pass.RunAsync(CancellationToken.None);

        var batch = Assert.Single(archive.Batches);
        var coverage = batch.AttentionPublisherCoverage!;
        Assert.Equal(5, coverage.DistinctUnclassifiedPublishers);

        var coverageLines = logger.Entries
            .Where(e => e.Message.Contains("Attention publisher coverage for batch", StringComparison.Ordinal))
            .ToList();

        var line = Assert.Single(coverageLines);
        Assert.Equal(LogLevel.Information, line.Level);

        // One line carrying the WHOLE summary: the batch it describes, every tier share including
        // unclassified, the size of the tail, and every publisher on the (uncapped, here) worklist. A
        // per-publisher line could name at most one of these.
        Assert.Contains(batch.BatchId.ToString(), line.Message, StringComparison.Ordinal);
        Assert.Contains(coverage.Version, line.Message, StringComparison.Ordinal);
        foreach (var tier in coverage.Tiers)
        {
            Assert.Contains(tier.TierName, line.Message, StringComparison.Ordinal);
        }

        foreach (var publisher in coverage.TopUnclassifiedPublishers)
        {
            Assert.Contains(publisher.Publisher, line.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Reuters", line.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<CollectionPass>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static int TierCount(AttentionPublisherCoverageSummary coverage, string tier) =>
        coverage.Tiers.SingleOrDefault(t => t.TierName == tier)?.Observations ?? 0;
}
