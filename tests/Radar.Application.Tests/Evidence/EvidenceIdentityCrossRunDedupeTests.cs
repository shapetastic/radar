using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Evidence;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Evidence;

/// <summary>
/// The point of spec 145, driven end-to-end through the REAL path — the real
/// <see cref="CollectedEvidenceMapper"/>, the real durable <see cref="FileRawEvidenceStore"/> /
/// <see cref="FileSignalStore"/> (each constructed fresh per "run", which is what a fresh process is), the
/// real <see cref="KeywordSignalExtractor"/> + <see cref="ExtractedSignalMapper"/>, the real
/// <c>SignalCrossRunDedupe</c> inside <see cref="FileSignalStore.GetByCompanyAsync"/>, and the real
/// <see cref="ScoringEngine"/> over <see cref="RadarScoreFormulaV8"/>. No hand-built fakes stand in for any
/// of it, because the defect this slice fixes lived in the seams between those pieces.
///
/// <para>
/// <b>What was broken.</b> The mapper minted <c>Guid.NewGuid()</c> per run while the durable evidence store
/// path-keyed on <c>contentHash</c>. So N re-collections of one article produced N evidence ids, N signals
/// with N distinct spec-85 dedupe keys <c>(CompanyId, EvidenceId, Type, Direction)</c> — a key built on
/// identity can never dedupe identity — and only one persisted evidence file. Measured on the live store
/// (2026-07-26): 49,454 accrued signals collapse under that key to 49,454 (<b>1.000×</b>, a no-op) but
/// collapse by CONTENT to 5,368 (<b>9.213×</b>), and only 10.5 % of signals had resolvable evidence.
/// </para>
/// </summary>
public sealed class EvidenceIdentityCrossRunDedupeTests : IDisposable
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 2, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Three successive "run instants", i.e. three separate pipeline runs over the same source.</summary>
    private static readonly DateTimeOffset[] RunInstants =
    [
        new(2026, 2, 7, 6, 0, 0, TimeSpan.Zero),
        new(2026, 2, 8, 6, 0, 0, TimeSpan.Zero),
        new(2026, 2, 9, 6, 0, 0, TimeSpan.Zero),
    ];

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed record Store(string EvidenceDirectory, string SignalDirectory);

    private Store NewStore()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return new Store(Path.Combine(root, "evidence"), Path.Combine(root, "signals"));
    }

    /// <summary>
    /// The one and only source fact in these fixtures. Identical on every run except
    /// <paramref name="collectedAt"/> — the retrieval timestamp, which is deliberately excluded from
    /// identity, so varying it is part of the assertion rather than a convenience.
    /// </summary>
    private static CollectedEvidence SourceFact(DateTimeOffset collectedAt) => new(
        SourceType: EvidenceSourceType.PressRelease,
        SourceName: "Northwind Newsroom",
        SourceUrl: "https://example.com/nw/pr-1",
        Title: "Northwind Robotics signs multi-year deal",
        RawText: "Northwind Robotics announced a multi-year deal with a major customer today.",
        PublishedAt: PublishedAt,
        CollectedAt: collectedAt,
        Metadata: new Dictionary<string, string> { ["quality"] = "High" })
    {
        CompanyHints = ["NWR"],
    };

    private static CollectedEvidenceMapper Mapper() =>
        new(new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance);

    private static FileRawEvidenceStore EvidenceStore(Store store) =>
        new(
            new FileRawEvidenceStoreOptions { RootDirectory = store.EvidenceDirectory },
            NullLogger<FileRawEvidenceStore>.Instance);

    private static FileSignalStore SignalStore(Store store) =>
        new(
            new FileSignalStoreOptions { RootDirectory = store.SignalDirectory },
            NullLogger<FileSignalStore>.Instance);

    /// <summary>
    /// One pipeline run over one source fact: map → store evidence (durable, insert-only) → extract →
    /// resolve to the company → approve → persist the signal. Every store is constructed FRESH, which is
    /// what a separate process is.
    /// <para>
    /// The signal is persisted on EVERY run regardless of what <c>AddIfNewAsync</c> returned. That is
    /// deliberate: it reproduces the duplication shape the live store actually holds (49,454 signals over
    /// 6,044 distinct evidence hashes), which accrued before spec 142 made re-collection idempotent. If the
    /// fixture skipped re-extraction it would test spec 142's guard, not spec 145's identity.
    /// </para>
    /// </summary>
    private static async Task<EvidenceItem> RunAsync(Store store, Guid companyId, DateTimeOffset runInstant)
    {
        var evidenceStore = EvidenceStore(store);
        var signalStore = SignalStore(store);

        var evidence = Mapper().ToEvidenceItem(SourceFact(runInstant));

        await ((IEvidenceRepository)evidenceStore).AddIfNewAsync(evidence, CancellationToken.None);
        await evidenceStore.WriteIfNewAsync(evidence, CancellationToken.None);

        await PersistSignalAsync(signalStore, evidence, companyId, runInstant);
        return evidence;
    }

    /// <summary>Extraction + resolution + approval + durable write, shared by the real and legacy fixtures.</summary>
    private static async Task<Signal> PersistSignalAsync(
        FileSignalStore signalStore, EvidenceItem evidence, Guid companyId, DateTimeOffset runInstant)
    {
        var extractor = new KeywordSignalExtractor(
            NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights());

        var extracted = await extractor.ExtractAsync(evidence, CancellationToken.None);
        var one = Assert.Single(extracted.Signals);

        var mapped = ExtractedSignalMapper.ToSignal(one, evidence, runInstant);
        Assert.True(mapped.IsValid, string.Join("; ", mapped.Errors));

        // Entity resolution + review, which the pipeline does between extraction and persistence.
        var signal = mapped.Signal! with
        {
            CompanyId = companyId,
            ReviewStatus = SignalReviewStatus.Approved,
        };

        // Fully qualified: the enclosing namespace chain reaches Radar.Application.SignalReview (a
        // namespace), which would otherwise shadow the domain record.
        var review = new Radar.Domain.Signals.SignalReview(
            Id: Guid.NewGuid(),
            SignalId: signal.Id,
            ReviewerName: "deterministic-reviewer",
            Decision: SignalReviewDecision.Approve,
            Summary: "Approved.",
            IssuesJson: null,
            ReviewedAtUtc: runInstant);

        await signalStore.WriteAsync(signal, review, CancellationToken.None);
        return signal;
    }

    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "test-src-desc";

        public string CollectionProvenance() => "collectors=test;";
    }

    /// <summary>
    /// Scores the accrued store from a FRESH set of instances — a fresh process reading only what is on
    /// disk — through the real engine and the real v8 formula.
    /// </summary>
    private static async Task<CompanyScoreResult> ScoreAsync(Store store, Guid companyId)
    {
        var signalStore = SignalStore(store);
        var evidenceStore = EvidenceStore(store);

        var companies = new InMemoryCompanyRepository();
        await companies.AddAsync(
            new CompanyBuilder().WithId(companyId).Build(), CancellationToken.None);

        var weights = new ScoringWeights();
        var sourceWeights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        var engine = new ScoringEngine(
            signalStore,
            signalStore,
            evidenceStore,
            new InMemoryScoreRepository(),
            companies,
            new RadarScoreFormulaV8(weights, sourceWeights),
            weights,
            sourceWeights,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions { Window = Window },
            NullLogger<ScoringEngine>.Instance);

        return await engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
    }

    private static int SignalFileCount(Store store) =>
        Directory.Exists(store.SignalDirectory)
            ? Directory.GetFiles(store.SignalDirectory, "*.json", SearchOption.AllDirectories).Length
            : 0;

    private static int EvidenceFileCount(Store store) =>
        Directory.Exists(store.EvidenceDirectory)
            ? Directory.GetFiles(store.EvidenceDirectory, "*.json", SearchOption.AllDirectories).Length
            : 0;

    // -------------------------------------------------------------------------------------------------
    // 1. N runs over identical source content yield ONE scored signal, not N.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThreeRunsOverIdenticalContent_YieldExactlyOneScoredSignal()
    {
        var store = NewStore();
        var companyId = Guid.NewGuid();

        var evidenceIds = new List<Guid>();
        foreach (var instant in RunInstants)
        {
            evidenceIds.Add((await RunAsync(store, companyId, instant)).Id);
        }

        // Every run derived the SAME evidence id from the same content — the mechanism under test.
        Assert.Single(evidenceIds.Distinct());

        // Nothing was deleted or rewritten: three signal files persist (append-only, AD-8) over ONE
        // insert-only evidence file (AD-1). The collapse happens on the READ, not by destroying history.
        Assert.Equal(3, SignalFileCount(store));
        Assert.Equal(1, EvidenceFileCount(store));

        var result = await ScoreAsync(store, companyId);

        // …and exactly one of those three copies is scored, with its provenance intact.
        var link = Assert.Single(result.Links);
        Assert.Equal(evidenceIds[0], link.EvidenceId);
    }

    // -------------------------------------------------------------------------------------------------
    // 2. The fix must not inflate scores: the duplicated fixture scores EQUAL to the single-copy fixture.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DuplicatedFixture_ScoresIdenticallyToTheSingleCopyFixture()
    {
        var companyId = Guid.NewGuid();

        var single = NewStore();
        await RunAsync(single, companyId, RunInstants[0]);

        var duplicated = NewStore();
        foreach (var instant in RunInstants)
        {
            await RunAsync(duplicated, companyId, instant);
        }

        var expected = await ScoreAsync(single, companyId);
        var actual = await ScoreAsync(duplicated, companyId);

        // EQUAL, not merely "not higher" — a 3× duplicated store must score exactly as its single copy
        // does, component for component. "Lower" would be a different bug.
        Assert.Equal(expected.Snapshot.TrajectoryScore, actual.Snapshot.TrajectoryScore);
        Assert.Equal(expected.Snapshot.OpportunityScore, actual.Snapshot.OpportunityScore);
        Assert.Equal(expected.Snapshot.AttentionScore, actual.Snapshot.AttentionScore);
        Assert.Equal(expected.Snapshot.EvidenceConfidenceScore, actual.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(expected.Snapshot.SignalVelocityScore, actual.Snapshot.SignalVelocityScore);
        Assert.Equal(expected.Snapshot.Explanation, actual.Snapshot.Explanation);
        Assert.Equal(expected.Snapshot.ComponentJson, actual.Snapshot.ComponentJson);

        // The provenance is the same fact in both stores, because identity is content-derived.
        Assert.Equal(expected.Links.Count, actual.Links.Count);
        Assert.Equal(
            Assert.Single(expected.Links).EvidenceId,
            Assert.Single(actual.Links).EvidenceId);

        // The generation stamp is untouched by any of this — no fingerprint input moved (spec 145 changes
        // no weight, no formula shape, no rule-set version, no collector set).
        Assert.Equal(expected.Snapshot.ScoringConfigVersion, actual.Snapshot.ScoringConfigVersion);
        Assert.Equal(expected.Snapshot.ScoringVersion, actual.Snapshot.ScoringVersion);
    }

    // -------------------------------------------------------------------------------------------------
    // 3. The counterfactual: prove the collapse above is real work, not a vacuous assertion.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyPerRunEvidenceIds_DoNotCollapse_AndScoreStrictlyHigher()
    {
        // Reproduces the PRE-145 shape exactly: the same source fact, but each run mints a fresh evidence
        // Guid (and therefore lands under its own contentHash-keyed file). The spec-85 key then sees three
        // distinct (CompanyId, EvidenceId, Type, Direction) tuples and collapses nothing. This is the 9.2×
        // duplication the live store holds — and the score inflation that arrives the moment those
        // duplicate ids become resolvable. If content-derived identity were reverted, test 1 and test 2
        // would produce THIS result instead.
        var companyId = Guid.NewGuid();

        var legacy = NewStore();
        var legacySignalStore = SignalStore(legacy);
        var legacyEvidenceStore = EvidenceStore(legacy);

        for (var run = 0; run < RunInstants.Length; run++)
        {
            var mapped = Mapper().ToEvidenceItem(SourceFact(RunInstants[run]));
            var perRunCopy = mapped with
            {
                Id = Guid.NewGuid(),
                ContentHash = $"{mapped.ContentHash}-run{run}",
            };

            await ((IEvidenceRepository)legacyEvidenceStore)
                .AddIfNewAsync(perRunCopy, CancellationToken.None);
            await legacyEvidenceStore.WriteIfNewAsync(perRunCopy, CancellationToken.None);
            await PersistSignalAsync(legacySignalStore, perRunCopy, companyId, RunInstants[run]);
        }

        var single = NewStore();
        await RunAsync(single, companyId, RunInstants[0]);

        var deduped = await ScoreAsync(single, companyId);
        var inflated = await ScoreAsync(legacy, companyId);

        // Three copies of one fact are scored as three facts…
        Assert.Equal(3, inflated.Links.Count);
        Assert.Single(deduped.Links);

        // …and that is strictly upward pressure on the score. This is the direction spec 145 exists to
        // prevent, so the assertion is deliberately strict rather than "not equal".
        Assert.True(
            inflated.Snapshot.TrajectoryScore > deduped.Snapshot.TrajectoryScore,
            $"expected duplication to inflate Trajectory; got {inflated.Snapshot.TrajectoryScore} " +
            $"vs {deduped.Snapshot.TrajectoryScore}");
    }

    // -------------------------------------------------------------------------------------------------
    // 4. Cross-collector: the same content from two collectors is ONE evidence record.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SameContentFromTwoCollectors_IsOneEvidenceRecord_AndOneScoredSignal()
    {
        // The stated policy (see EvidenceIdentity): identical normalized title+body is ONE fact however
        // many retrieval paths found it. Source name, URL and source type are all excluded from identity,
        // so a press-release feed and a filing feed carrying the same words converge on one record.
        var store = NewStore();
        var companyId = Guid.NewGuid();

        var evidenceStore = EvidenceStore(store);
        var signalStore = SignalStore(store);

        var fromPressRelease = Mapper().ToEvidenceItem(SourceFact(RunInstants[0]));
        var fromFilingFeed = Mapper().ToEvidenceItem(SourceFact(RunInstants[1]) with
        {
            SourceType = EvidenceSourceType.Filing,
            SourceName = "Northwind — SEC filings",
            SourceUrl = "https://data.example.gov/nw/filing?ts=17402",
        });

        Assert.Equal(fromPressRelease.Id, fromFilingFeed.Id);

        foreach (var (evidence, instant) in new[]
                 {
                     (fromPressRelease, RunInstants[0]),
                     (fromFilingFeed, RunInstants[1]),
                 })
        {
            await ((IEvidenceRepository)evidenceStore).AddIfNewAsync(evidence, CancellationToken.None);
            await evidenceStore.WriteIfNewAsync(evidence, CancellationToken.None);
            await PersistSignalAsync(signalStore, evidence, companyId, instant);
        }

        // Provenance retention: BOTH collectors' raw files are on disk, under their own source-type
        // folders. Nothing was deleted; only the identity index collapsed.
        Assert.Equal(2, EvidenceFileCount(store));
        Assert.Equal(2, SignalFileCount(store));

        // Attention breadth/diversity count distinct publishers and source types over the RESOLVED
        // evidence set, so collapsing to one record can only LOWER or hold them — never raise them.
        var result = await ScoreAsync(store, companyId);
        Assert.Single(result.Links);
        Assert.Equal(fromPressRelease.Id, result.Links[0].EvidenceId);
    }

    // -------------------------------------------------------------------------------------------------
    // 5. The attention/breadth claim, asserted rather than argued.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Lower breadth ⇒ lower score" is NOT universally true — <c>OpportunityScore</c> consumes
    /// <c>AttentionScore</c> as an INVERSE discount, so a lower attention would RAISE opportunity. The
    /// reason this slice cannot raise it is structural, not directional: the evidence repository has always
    /// rejected a second item carrying an already-seen content hash, so the breadth contributed by a set of
    /// identical copies was already exactly 1 BEFORE this slice. This test drives the REAL pipeline gate
    /// (honouring <c>AddIfNewAsync</c>, exactly as <c>RadarPipelineRunner</c> does) over third-party news
    /// evidence, where attention is actually non-zero, and pins that both components are UNCHANGED —
    /// neither lowered nor raised — when a second publisher carries byte-identical content.
    /// </summary>
    [Fact]
    public async Task IdenticalContentFromASecondPublisher_LeavesAttentionAndOpportunityUnchanged()
    {
        var companyId = Guid.NewGuid();

        static CollectedEvidence NewsFrom(string publisher, DateTimeOffset collectedAt) => new(
            SourceType: EvidenceSourceType.NewsArticle,
            SourceName: publisher,
            SourceUrl: $"https://{publisher.ToLowerInvariant()}.example/story",
            Title: "Northwind Robotics signs multi-year deal",
            RawText: "Northwind Robotics announced a multi-year deal with a major customer today.",
            PublishedAt: PublishedAt,
            CollectedAt: collectedAt,
            Metadata: new Dictionary<string, string> { ["quality"] = "Medium" });

        var onePublisher = NewStore();
        await CollectRespectingIdempotencyAsync(
            onePublisher, companyId, NewsFrom("OutletA", RunInstants[0]), RunInstants[0]);

        var twoPublishers = NewStore();
        await CollectRespectingIdempotencyAsync(
            twoPublishers, companyId, NewsFrom("OutletA", RunInstants[0]), RunInstants[0]);
        await CollectRespectingIdempotencyAsync(
            twoPublishers, companyId, NewsFrom("OutletB", RunInstants[1]), RunInstants[1]);

        var expected = await ScoreAsync(onePublisher, companyId);
        var actual = await ScoreAsync(twoPublishers, companyId);

        // Attention is genuinely exercised here (third-party news evidence), so an equality of zeroes
        // cannot make this pass vacuously.
        Assert.True(
            expected.Snapshot.AttentionScore > 0,
            $"fixture must exercise Attention; got {expected.Snapshot.AttentionScore}");

        Assert.Equal(expected.Snapshot.AttentionScore, actual.Snapshot.AttentionScore);
        Assert.Equal(expected.Snapshot.OpportunityScore, actual.Snapshot.OpportunityScore);
        Assert.Equal(expected.Snapshot.TrajectoryScore, actual.Snapshot.TrajectoryScore);
        Assert.Equal(expected.Snapshot.EvidenceConfidenceScore, actual.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(expected.Snapshot.SignalVelocityScore, actual.Snapshot.SignalVelocityScore);
    }

    /// <summary>
    /// A collection step that HONOURS the <c>AddIfNewAsync</c> gate, exactly as <c>RadarPipelineRunner</c>
    /// does: already-seen content is not persisted and not re-extracted.
    /// </summary>
    private static async Task CollectRespectingIdempotencyAsync(
        Store store, Guid companyId, CollectedEvidence collected, DateTimeOffset runInstant)
    {
        var evidenceStore = EvidenceStore(store);
        var signalStore = SignalStore(store);

        var evidence = Mapper().ToEvidenceItem(collected);

        if (!await ((IEvidenceRepository)evidenceStore).AddIfNewAsync(evidence, CancellationToken.None))
        {
            return;
        }

        await evidenceStore.WriteIfNewAsync(evidence, CancellationToken.None);
        await PersistSignalAsync(signalStore, evidence, companyId, runInstant);
    }
}
