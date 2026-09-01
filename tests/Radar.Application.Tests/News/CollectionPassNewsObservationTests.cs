using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.News;
using Radar.Application.Pipeline;
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
/// Spec 177 §§3–5 at the orchestration seam: the collection pass captures observation sidecars per
/// collector, hands them to the archive with identity minted, writes the batch manifest with the EXPLICIT
/// run association, and records failures as unproven capture — all without touching a counter or aborting.
/// </summary>
public sealed class CollectionPassNewsObservationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static NewsObservationCandidate Candidate(string url = "https://news.google.com/rss/articles/AAA") =>
        new(
            CompanyId: CompanyId,
            Ticker: "RKLB",
            Collector: "newssearch",
            QueryPhrase: "Rocket Lab",
            FeedId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            FeedName: "Rocket Lab — News",
            GoogleLandingUrl: url,
            Publisher: "SpaceNews",
            PublisherSiteUrl: "https://spacenews.com",
            Headline: "Rocket Lab wins new launch contract - SpaceNews",
            DescriptionRaw: "<a>Rocket Lab wins new launch contract</a>",
            DescriptionText: "Rocket Lab wins new launch contract",
            DescriptionTruncated: false,
            PublishedAtUtc: FixedNow.AddHours(-3),
            RetrievedAtUtc: FixedNow.AddMinutes(-5));

    private static CollectedEvidence Evidence(string url = "https://news.google.com/rss/articles/AAA") =>
        new(
            SourceType: EvidenceSourceType.NewsArticle,
            SourceName: "SpaceNews",
            SourceUrl: url,
            Title: "Rocket Lab wins new launch contract - SpaceNews",
            RawText: $"Rocket Lab wins new launch contract - SpaceNews — SpaceNews. Source: {url}",
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

    /// <summary>Reports one shrunk-inventory warning for the feed type, so the run record's coverage amend runs.</summary>
    private sealed class ShrunkInventoryHealthValidator(string feedType) : ICollectionHealthValidator
    {
        public Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(new CollectionHealthReport(
            [
                new CollectionHealthWarning(
                    Code: "FeedInventoryShrank",
                    Severity: CollectionHealthSeverity.Warning,
                    FeedType: feedType,
                    DeclaredInSeed: 2,
                    ReachedCollectors: 1,
                    Message: "feed inventory shrank between seed and collectors"),
            ]));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (CollectionPass Pass, InMemoryNewsObservationArchive Archive, InMemoryEvidenceRepository Evidence)
        CreatePass(
            IReadOnlyList<IEvidenceCollector> collectors,
            InMemoryNewsObservationArchive? archive = null,
            NewsObservationCaptureOptions? captureOptions = null,
            InMemoryEvidenceRepository? evidenceRepository = null,
            ICollectionHealthValidator? healthValidator = null)
    {
        var companies = new InMemoryCompanyRepository();
        var evidence = evidenceRepository ?? new InMemoryEvidenceRepository();
        var observationArchive = archive ?? new InMemoryNewsObservationArchive();

        var pass = new CollectionPass(
            collectors,
            new CollectedEvidenceMapper(new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance),
            evidence,
            new NullRawStore(),
            new EmptyExtractor(),
            new CompanyResolver(companies, NullLogger<CompanyResolver>.Instance),
            new DeterministicSignalReviewer(
                new FixedTime(FixedNow), NullLogger<DeterministicSignalReviewer>.Instance),
            new InMemorySignalRepository(),
            new InMemorySignalReviewRepository(),
            new NullSignalFileStore(),
            companies,
            healthValidator ?? new CleanHealthValidator(),
            new FixedTime(FixedNow),
            NullLogger<CollectionPass>.Instance,
            new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default),
            directionalFilingSignals: null,
            newsObservationArchive: observationArchive,
            newsObservationCaptureOptions: captureOptions);

        return (pass, observationArchive, evidence);
    }

    [Fact]
    public async Task RunAsync_ArchivesSidecarObservations_AndWritesBatchWithExplicitRunAssociation()
    {
        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, null, [Candidate()]));
        var (pass, archive, _) = CreatePass([collector]);

        var result = await pass.RunAsync(CancellationToken.None);

        var record = Assert.Single(archive.Records.Values);
        Assert.Equal(NewsObservationCaptureMode.ProspectiveRss, record.CaptureMode);
        Assert.Equal("Rocket Lab wins new launch contract - SpaceNews", record.Headline);
        Assert.Equal(FixedNow.AddMinutes(-5), record.FirstObservedAtUtc);

        var batch = Assert.Single(archive.Batches);
        // The EXPLICIT run association: the batch id lands on the pass result (→ the run record), and the
        // batch's RunAsOfUtc is byte-equal to the run instant — never a nearest-time join.
        Assert.Equal(batch.BatchId, result.NewsObservationBatchId);
        Assert.Equal(result.AsOfUtc, batch.RunAsOfUtc);
        Assert.True(batch.CaptureProven);
        Assert.Equal(1, batch.ObservationsAttempted);
        Assert.Equal(1, batch.ObservationsWritten);
        Assert.True(batch.FullUniverse); // the default capture options claim the whole universe

        var capture = Assert.Single(batch.Collectors);
        Assert.Equal("newssearch", capture.CollectorName);
    }

    [Fact]
    public async Task RunAsync_AccruedEvidenceDuplicate_StillReachesObservationCapture_NoSecondEvidence()
    {
        // Spec 177 §3's load-bearing decoupling: evidence dedupe and observation capture answer different
        // questions. Pre-seed the durable evidence repository with the SAME evidence a previous run stored;
        // the re-collection contributes no new evidence (and hence no signal), but the observation is
        // still captured.
        var mapper = new CollectedEvidenceMapper(
            new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance);
        var accrued = new InMemoryEvidenceRepository();
        await accrued.AddIfNewAsync(mapper.ToEvidenceItem(Evidence()), CancellationToken.None);

        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, null, [Candidate()]));
        var (pass, archive, _) = CreatePass([collector], evidenceRepository: accrued);

        var result = await pass.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.EvidenceCollected);
        Assert.Equal(0, result.EvidenceNew);       // the accrued duplicate stayed deduped …
        Assert.Equal(0, result.SignalsExtracted);  // … and produced no second signal …
        Assert.Single(archive.Records);            // … while the observation was captured anyway.
        Assert.True(Assert.Single(archive.Batches).CaptureProven);
    }

    [Fact]
    public async Task RunAsync_ArchiveWriteFailure_IsRecordedAsUnprovenCapture_AndNeverAbortsTheRun()
    {
        var archive = new InMemoryNewsObservationArchive { FailWrites = true };
        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, null, [Candidate()]));
        var (pass, _, _) = CreatePass([collector], archive);

        var result = await pass.RunAsync(CancellationToken.None);

        // The run completed; the failure is durable on the manifest, never a clean zero.
        Assert.NotNull(result.NewsObservationBatchId);
        var batch = Assert.Single(archive.Batches);
        Assert.False(batch.CaptureProven);
        Assert.Equal(1, batch.ObservationsFailed);
        Assert.Equal(0, batch.ObservationsWritten);
    }

    [Fact]
    public async Task RunAsync_IncompleteCoverage_IsCarriedOntoTheBatch_SoItCannotReadAsCleanCapture()
    {
        // A feed failure + a capped feed: the spec-169 coverage rows and the provider failures ride the
        // batch verbatim, so a later reader can never mistake this run for provably-complete capture.
        var coverage = new CollectorCompanyCoverage(
            CompanyId: CompanyId,
            ExpectedFeedCount: 2,
            SuccessfulFeedCount: 1,
            HitEffectiveResultLimit: true,
            Issues: CollectionCoverageIssues.Canonicalize(
                [CollectionCoverageIssues.SourceFailure, CollectionCoverageIssues.ResultLimitReached]));
        var summary = new CollectionSummary(
            2, 1, 1, 1, [new SourceFailure("Rocket Lab — News", "query=Rocket Lab", "HTTP 429 (rate limited)")]);

        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], summary, [coverage], [Candidate()]));
        var (pass, archive, _) = CreatePass([collector]);

        await pass.RunAsync(CancellationToken.None);

        var capture = Assert.Single(Assert.Single(archive.Batches).Collectors);
        var row = Assert.Single(capture.CompanyCoverage!);
        Assert.Contains(CollectionCoverageIssues.SourceFailure, row.Issues);
        Assert.True(capture.AnyFeedHitProviderCap);
        Assert.Equal("HTTP 429 (rate limited)", Assert.Single(capture.ProviderFailures).Reason);
    }

    [Fact]
    public async Task RunAsync_FilteredPassCaptureOptions_MarkTheBatchAsNotFullUniverse()
    {
        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, null, [Candidate()]));
        var (pass, archive, _) = CreatePass(
            [collector], captureOptions: new NewsObservationCaptureOptions { FullUniverse = false });

        await pass.RunAsync(CancellationToken.None);

        // Observations ARE captured; the batch just cannot establish the whole-universe boundary.
        Assert.Single(archive.Records);
        Assert.False(Assert.Single(archive.Batches).FullUniverse);
    }

    [Fact]
    public async Task RunAsync_NoSidecarEmittingCollector_WritesNoBatch()
    {
        var collector = new FakeCollector(
            "rss-like", new CollectionResult([Evidence()], CollectionSummary.Empty));
        var (pass, archive, _) = CreatePass([collector]);

        var result = await pass.RunAsync(CancellationToken.None);

        Assert.Null(result.NewsObservationBatchId);
        Assert.Empty(archive.Records);
        Assert.Empty(archive.Batches);
    }

    [Fact]
    public async Task RunAsync_NoArchiveRegistered_IsByteForByteThePreSpec177Pass()
    {
        // The optional-dependency default: a composition that never registered the archive captures
        // nothing and reports a null batch id — no behaviour change anywhere else.
        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, null, [Candidate()]));
        var companies = new InMemoryCompanyRepository();

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
            NullLogger<CollectionPass>.Instance,
            new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default));

        var result = await pass.RunAsync(CancellationToken.None);

        Assert.Null(result.NewsObservationBatchId);
        Assert.Equal(1, result.EvidenceNew);
    }
    // -------------------------------------------------------------------------------------------------
    // Spec 190: the capture aggregates are correctly named, and "not recorded" never renders as "false".
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A capped feed that CONFIRMED local truncation: the correctly named nullable aggregates carry the
    /// current provenance, and the historically misnamed non-nullable member is still MIRRORED so a reader
    /// written before the rename keeps working. The mirror is compatibility, not a provider claim.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConfirmedLocalTruncation_SetsTheNewAggregates_AndStillMirrorsTheLegacyMember()
    {
        var coverage = new CollectorCompanyCoverage(
            CompanyId: CompanyId,
            ExpectedFeedCount: 1,
            SuccessfulFeedCount: 1,
            HitEffectiveResultLimit: true,
            Issues: CollectionCoverageIssues.Canonicalize([CollectionCoverageIssues.ResultLimitReached]),
            EffectiveResultLimit: 25,
            MaxValidItemsObserved: 31,
            ConfirmedLocalTruncation: true,
            UnadmittedRelevantTailItemCount: 4);

        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, [coverage], [Candidate()]));
        var (pass, archive, _) = CreatePass([collector]);

        await pass.RunAsync(CancellationToken.None);

        var capture = Assert.Single(Assert.Single(archive.Batches).Collectors);
        Assert.True(capture.AnyFeedHitEffectiveResultLimit);
        Assert.True(capture.AnyFeedConfirmedLocalTruncation);
        Assert.True(capture.AnyFeedHitProviderCap); // legacy mirror, unchanged for old readers
    }

    /// <summary>
    /// At the limit but with NO item observed beyond it: possible truncation stays fail-closed while
    /// confirmed truncation is honestly false — the two facts are distinguishable, which is the whole point.
    /// </summary>
    [Fact]
    public async Task RunAsync_AtLimitWithoutAnObservedTail_SeparatesPossibleFromConfirmedTruncation()
    {
        var coverage = new CollectorCompanyCoverage(
            CompanyId: CompanyId,
            ExpectedFeedCount: 1,
            SuccessfulFeedCount: 1,
            HitEffectiveResultLimit: true,
            Issues: CollectionCoverageIssues.Canonicalize([CollectionCoverageIssues.ResultLimitReached]),
            EffectiveResultLimit: 25,
            MaxValidItemsObserved: 25,
            ConfirmedLocalTruncation: false,
            UnadmittedRelevantTailItemCount: 0);

        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, [coverage], [Candidate()]));
        var (pass, archive, _) = CreatePass([collector]);

        await pass.RunAsync(CancellationToken.None);

        var capture = Assert.Single(Assert.Single(archive.Batches).Collectors);
        Assert.True(capture.AnyFeedHitEffectiveResultLimit);
        Assert.False(capture.AnyFeedConfirmedLocalTruncation);
        Assert.True(capture.AnyFeedHitProviderCap);
    }

    /// <summary>
    /// A coverage row that RECORDS no spec-190 diagnostic (a pre-190 row, or a collector that records none)
    /// must aggregate to <c>null</c> — "not recorded" is a different fact from "no truncation".
    /// </summary>
    [Fact]
    public async Task RunAsync_CoverageWithoutSpec190Diagnostics_AggregatesConfirmedTruncationAsNull()
    {
        var coverage = new CollectorCompanyCoverage(
            CompanyId: CompanyId,
            ExpectedFeedCount: 1,
            SuccessfulFeedCount: 1,
            HitEffectiveResultLimit: false,
            Issues: []);

        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, [coverage], [Candidate()]));
        var (pass, archive, _) = CreatePass([collector]);

        await pass.RunAsync(CancellationToken.None);

        var capture = Assert.Single(Assert.Single(archive.Batches).Collectors);
        Assert.False(capture.AnyFeedHitEffectiveResultLimit);   // recorded: no feed reached the limit
        Assert.Null(capture.AnyFeedConfirmedLocalTruncation);   // NOT recorded — never rendered as false
        Assert.False(capture.AnyFeedHitProviderCap);
    }

    /// <summary>
    /// No coverage rows at all: both new aggregates are <c>null</c> (unproven), while the legacy
    /// non-nullable member keeps its historical <c>false</c>.
    /// </summary>
    [Fact]
    public async Task RunAsync_NoCoverageRows_LeavesBothNewAggregatesNull()
    {
        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, null, [Candidate()]));
        var (pass, archive, _) = CreatePass([collector]);

        await pass.RunAsync(CancellationToken.None);

        var capture = Assert.Single(Assert.Single(archive.Batches).Collectors);
        Assert.Null(capture.AnyFeedHitEffectiveResultLimit);
        Assert.Null(capture.AnyFeedConfirmedLocalTruncation);
        Assert.False(capture.AnyFeedHitProviderCap);
    }

    /// <summary>
    /// The run record's health-mismatch amend rebuilds each coverage row with <c>c with { Issues = ... }</c>.
    /// A positional-record <c>with</c> preserves trailing members by construction — asserted rather than
    /// assumed, because losing them here would silently erase the audit from the durable run log.
    /// </summary>
    [Fact]
    public async Task RunAsync_HealthMismatchAmend_PreservesTheSpec190CoverageDiagnostics()
    {
        var coverage = new CollectorCompanyCoverage(
            CompanyId: CompanyId,
            ExpectedFeedCount: 1,
            SuccessfulFeedCount: 1,
            HitEffectiveResultLimit: true,
            Issues: CollectionCoverageIssues.Canonicalize([CollectionCoverageIssues.ResultLimitReached]),
            EffectiveResultLimit: 25,
            MaxValidItemsObserved: 31,
            ConfirmedLocalTruncation: true,
            UnadmittedRelevantTailItemCount: 4,
            RecencyWindowDays: 7,
            UnfilteredFirstCollectionFeedCount: 1);

        var collector = new FakeCollector(
            "newssearch",
            new CollectionResult([Evidence()], CollectionSummary.Empty, [coverage], [Candidate()]));
        var (pass, _, _) = CreatePass(
            [collector], healthValidator: new ShrunkInventoryHealthValidator("newssearch"));

        var result = await pass.RunAsync(CancellationToken.None);

        var row = Assert.Single(Assert.Single(result.CollectorRuns).CompanyCoverage!);
        Assert.Contains(CollectionCoverageIssues.CollectionHealthMismatch, row.Issues);
        Assert.Equal(25, row.EffectiveResultLimit);
        Assert.Equal(31, row.MaxValidItemsObserved);
        Assert.True(row.ConfirmedLocalTruncation);
        Assert.Equal(4, row.UnadmittedRelevantTailItemCount);
        // Spec 198's two trailing members ride the same `with` and must survive it for the same reason.
        Assert.Equal(7, row.RecencyWindowDays);
        Assert.Equal(1, row.UnfilteredFirstCollectionFeedCount);
    }

}
