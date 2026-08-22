using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.News;

public sealed class NewsObservationMigrationTests
{
    private static readonly DateTimeOffset OriginalCollectedAt =
        new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset FetchNow =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid RklbId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static EvidenceItem NewsEvidence(
        string url = "https://news.google.com/rss/articles/AAA",
        string title = "Rocket Lab wins new launch contract - SpaceNews",
        string contentHash = "hash-news-1") =>
        new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithSourceName("SpaceNews")
            .WithSourceUrl(url)
            .WithTitle(title)
            .WithRawText($"{title} — SpaceNews. Source: {url}")
            .WithContentHash(contentHash)
            .WithPublishedAtUtc(OriginalCollectedAt.AddHours(-3))
            .WithCollectedAtUtc(OriginalCollectedAt)
            .WithMetadataJson(
                """{"metadata":{"publisher":"SpaceNews","feedName":"Rocket Lab — News","url":""" +
                $"\"{url}\"" + """},"companyHints":["RKLB"]}""")
            .Build();

    private static async Task<(NewsObservationMigration Migration, InMemoryNewsObservationArchive Archive)>
        CreateAsync(
            IEnumerable<EvidenceItem> evidence,
            bool retrospective = false,
            INewsArticleContentReader? reader = null)
    {
        var evidenceRepository = new InMemoryEvidenceRepository();
        foreach (var item in evidence)
        {
            await evidenceRepository.AddIfNewAsync(item, CancellationToken.None);
        }

        var companyRepository = new InMemoryCompanyRepository();
        await companyRepository.AddAsync(
            new CompanyBuilder().WithId(RklbId).WithName("Rocket Lab").WithTicker("RKLB").Build(),
            CancellationToken.None);

        var archive = new InMemoryNewsObservationArchive();
        var migration = new NewsObservationMigration(
            evidenceRepository,
            companyRepository,
            archive,
            new NewsObservationMigrationOptions { RetrospectiveFetch = retrospective },
            NullLogger<NewsObservationMigration>.Instance,
            reader);
        return (migration, archive);
    }

    [Fact]
    public async Task RunAsync_MigratesNewsEvidence_IntoHonestLegacyHeadlineOnlyObservations()
    {
        var (migration, archive) = await CreateAsync([NewsEvidence()]);

        var result = await migration.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.EvidenceScanned);
        Assert.Equal(1, result.LegacyWritten);
        var record = Assert.Single(archive.Records.Values);

        Assert.Equal(NewsObservationCaptureMode.LegacyHeadlineOnly, record.CaptureMode);
        Assert.Equal("Rocket Lab wins new launch contract - SpaceNews", record.Headline);
        Assert.Equal("SpaceNews", record.Publisher);
        Assert.Equal("https://news.google.com/rss/articles/AAA", record.GoogleLandingUrl);
        Assert.Equal(RklbId, record.CompanyId);
        Assert.Equal("RKLB", record.Ticker);
        Assert.Equal("Rocket Lab — News", record.FeedName);
        // FirstObservedAtUtc is the ORIGINAL CollectedAtUtc — that headline/URL really was persisted then.
        Assert.Equal(OriginalCollectedAt, record.FirstObservedAtUtc);
        Assert.Equal(OriginalCollectedAt, record.RetrievedAtUtc);
        Assert.Equal(OriginalCollectedAt.AddHours(-3), record.PublishedAtUtc);
        // Description/body stay null forever — they were discarded before spec 177 and cannot be honestly
        // reconstructed.
        Assert.Null(record.DescriptionRaw);
        Assert.Null(record.DescriptionText);
        Assert.Null(record.ArticleFetch);
        // Legacy evidence predates spec 146's collector stamp ⇒ collector honestly null, never invented.
        Assert.Null(record.Collector);
        Assert.Null(record.QueryPhrase);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_ASecondRunWritesNothingNew()
    {
        var (migration, archive) = await CreateAsync([NewsEvidence()]);

        var first = await migration.RunAsync(CancellationToken.None);
        var second = await migration.RunAsync(CancellationToken.None);

        Assert.Equal(1, first.LegacyWritten);
        Assert.Equal(0, second.LegacyWritten);
        Assert.Equal(1, second.LegacyDeduped);
        Assert.Single(archive.Records);
    }

    [Fact]
    public async Task RunAsync_NonNewsEvidence_IsIgnored_And_UrlLessNewsEvidence_IsSkippedNotInvented()
    {
        var pressRelease = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("hash-pr")
            .Build();
        var urlLess = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithSourceUrl(null)
            .WithContentHash("hash-nourl")
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var (migration, archive) = await CreateAsync([pressRelease, urlLess]);

        var result = await migration.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.EvidenceScanned); // the press release never even counts as news
        Assert.Equal(1, result.LegacySkipped);
        Assert.Empty(archive.Records);
    }

    [Fact]
    public async Task RunAsync_RetrospectiveFetch_UsesActualRetrievalTime_NeverBackdates()
    {
        var fetchResult = new NewsArticleFetchResult(
            Outcome: NewsArticleFetchOutcome.Fetched,
            RetrievedAtUtc: FetchNow,
            RedirectHops: 1,
            ResolvedUrl: "https://spacenews.com/story",
            HttpStatus: 200,
            ContentType: "text/html",
            Truncated: false,
            ExtractorVersion: "news-text-v1",
            ContentHash: "fetched-hash-a",
            BodyText: "Rocket Lab announced a new launch.",
            RetrievalPolicy: "news-fetch-v1;extractor=news-text-v1;domains=sha256:abc");
        var reader = new StubContentReader(fetchResult);

        var (migration, archive) = await CreateAsync([NewsEvidence()], retrospective: true, reader);

        var result = await migration.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.RetrospectiveAttempted);
        Assert.Equal(1, result.RetrospectiveWritten);

        var retro = Assert.Single(
            archive.Records.Values, r => r.CaptureMode == NewsObservationCaptureMode.RetrospectiveUrlFetch);
        // The knowledge cutoff is the fetch's OWN instant — never the publication or original collection
        // time (the legacy sibling record keeps those).
        Assert.Equal(FetchNow, retro.RetrievedAtUtc);
        Assert.Equal(FetchNow, retro.FirstObservedAtUtc);
        Assert.True(retro.FirstObservedAtUtc > OriginalCollectedAt);
        Assert.Same(fetchResult, retro.ArticleFetch);
        // The three capture modes stay distinguishable: the legacy record still exists beside it.
        Assert.Contains(
            archive.Records.Values, r => r.CaptureMode == NewsObservationCaptureMode.LegacyHeadlineOnly);
    }

    [Fact]
    public async Task RunAsync_RetrospectiveFetch_UnchangedPage_IsIdempotent_FailedFetchIsADurableOutcome()
    {
        var failed = new NewsArticleFetchResult(
            Outcome: NewsArticleFetchOutcome.HttpError,
            RetrievedAtUtc: FetchNow,
            RedirectHops: 0,
            ResolvedUrl: null,
            HttpStatus: 404,
            ContentType: null,
            Truncated: false,
            ExtractorVersion: null,
            ContentHash: null,
            BodyText: null,
            RetrievalPolicy: "news-fetch-v1;extractor=news-text-v1;domains=sha256:abc");
        var reader = new StubContentReader(failed);

        var (migration, archive) = await CreateAsync([NewsEvidence()], retrospective: true, reader);

        var first = await migration.RunAsync(CancellationToken.None);
        Assert.Equal(1, first.RetrospectiveWritten);
        // A disappeared page IS a durable observation (source-availability measurement).
        Assert.Contains(
            archive.Records.Values,
            r => r.CaptureMode == NewsObservationCaptureMode.RetrospectiveUrlFetch
                && r.ArticleFetch!.Outcome == NewsArticleFetchOutcome.HttpError);

        // Same URL, same (absent) content ⇒ same identity ⇒ the second sweep dedupes.
        var second = await migration.RunAsync(CancellationToken.None);
        Assert.Equal(0, second.RetrospectiveWritten);
        Assert.Equal(1, second.RetrospectiveDeduped);
    }

    [Fact]
    public void Ctor_RetrospectiveWithoutAReader_Throws()
    {
        Assert.Throws<ArgumentException>(() => new NewsObservationMigration(
            new InMemoryEvidenceRepository(),
            new InMemoryCompanyRepository(),
            new InMemoryNewsObservationArchive(),
            new NewsObservationMigrationOptions { RetrospectiveFetch = true },
            NullLogger<NewsObservationMigration>.Instance,
            contentReader: null));
    }

    private sealed class StubContentReader(NewsArticleFetchResult result) : INewsArticleContentReader
    {
        public Task<NewsArticleFetchResult> FetchAsync(string url, CancellationToken ct) =>
            Task.FromResult(result);
    }
}

/// <summary>
/// In-memory <see cref="INewsObservationArchive"/> honoring the identity contract (same id + same payload
/// hash ⇒ dedupe; different hash ⇒ conflict), shared by the Application-side spec-177 tests.
/// </summary>
internal sealed class InMemoryNewsObservationArchive : INewsObservationArchive
{
    public Dictionary<Guid, NewsObservationRecord> Records { get; } = [];

    public List<NewsObservationBatch> Batches { get; } = [];

    /// <summary>When set, every observation write reports <see cref="NewsObservationWriteOutcome.Failed"/>.</summary>
    public bool FailWrites { get; set; }

    public Task<NewsObservationWriteOutcome> WriteAsync(NewsObservationRecord record, CancellationToken ct)
    {
        if (FailWrites)
        {
            return Task.FromResult(NewsObservationWriteOutcome.Failed);
        }

        if (Records.TryGetValue(record.ObservationId, out var existing))
        {
            return Task.FromResult(string.Equals(existing.PayloadHash, record.PayloadHash, StringComparison.Ordinal)
                ? NewsObservationWriteOutcome.CrossRunDeduped
                : NewsObservationWriteOutcome.Conflict);
        }

        Records[record.ObservationId] = record;
        return Task.FromResult(NewsObservationWriteOutcome.Written);
    }

    public Task<bool> WriteBatchAsync(NewsObservationBatch batch, CancellationToken ct)
    {
        Batches.Add(batch);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<NewsObservationRecord>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NewsObservationRecord>>(
            [.. Records.Values.OrderBy(o => o.FirstObservedAtUtc).ThenBy(o => o.ObservationId)]);
}
