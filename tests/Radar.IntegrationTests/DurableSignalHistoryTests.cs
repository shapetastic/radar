using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Signals;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.TestSupport;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 142's central invariant, end to end:
/// <para>
/// <b>Scoring a window against the HYDRATED DURABLE store must produce the byte-identical score that
/// scoring the SAME signals held in memory produces.</b>
/// </para>
/// Mirrors spec 139's <c>replay ⊆ forward</c>: the comparison is field-for-field, excluding only the
/// per-call minted snapshot/link <see cref="Guid"/>s (a forward run mints those too). A field silently
/// lost on the way to disk is the failure mode that would make every downstream measurement a lie, so the
/// fixtures deliberately carry values — a non-<see cref="EvidenceQuality.Unknown"/> quality above all —
/// that would visibly move a component if they were dropped.
/// </summary>
public sealed class DurableSignalHistoryTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid NorthwindId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Northwind = "Northwind Robotics";

    // Inside the 30-day scoring window ending at FixedNow.
    private static readonly DateTimeOffset InWindow = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);

    private static ServiceProvider BuildProvider(TempPipelineFixtures fx, bool durable)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedNow));
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCollector(fx.EvidenceDir);
        services.AddLocalFileCompanySeed(fx.SeedFilePath);
        services.AddFileRawEvidenceStore(fx.RawEvidenceDir);
        services.AddFileSignalStore(fx.SignalsDir);
        if (durable)
        {
            services.AddDurableRadarSignalHistory();
        }

        services.AddFileScoreStore(Path.Combine(fx.RootDir, "scores"));
        services.AddFileReportWriter(Path.Combine(fx.RootDir, "reports"));
        services.AddFilePipelineRunStore(Path.Combine(fx.RootDir, "runs"));
        services.AddFileScoringConfigStore(Path.Combine(fx.RootDir, "scoring-configs"));
        services.AddRadarPipeline();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Everything a snapshot carries except its freshly-minted <see cref="CompanyScoreSnapshot.Id"/>.
    /// Comparing the projection (rather than listing assertions) means a NEW snapshot field is covered the
    /// moment it is added, instead of quietly escaping the invariant.
    /// </summary>
    private static object SnapshotFields(CompanyScoreSnapshot s) => new
    {
        s.CompanyId,
        s.ScoringVersion,
        s.TrajectoryScore,
        s.OpportunityScore,
        s.AttentionScore,
        s.EvidenceConfidenceScore,
        s.SignalVelocityScore,
        s.Explanation,
        s.ComponentJson,
        s.WindowStartUtc,
        s.WindowEndUtc,
        s.CreatedAtUtc,
        s.ScoringConfigVersion,
        s.StrategyName,
        s.CollectionProvenance,
    };

    private static object[] LinkFields(IEnumerable<ScoreEvidenceLink> links) =>
    [
        .. links
            .OrderBy(l => l.SignalId)
            .ThenBy(l => l.EvidenceId)
            .Select(object (l) => new { l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight })
    ];

    /// <summary>
    /// A fixture evidence item. <paramref name="alsoDeclareQualityInMetadata"/> defaults to <c>false</c> ON
    /// PURPOSE: a real collector puts <c>quality</c> in the metadata bag too, so leaving it there would let
    /// the legacy RECOVERY path silently cover for a lost top-level <c>quality</c> field and the round-trip
    /// invariant would stay green even with the new field deleted. Omitting it makes the explicit field the
    /// only carrier, which is what turns this into a real regression guard.
    /// </summary>
    private static EvidenceItem BuildEvidence(
        string hash, EvidenceQuality quality, bool alsoDeclareQualityInMetadata = false)
    {
        var metadata = new Dictionary<string, string> { ["sourceFile"] = hash + ".json" };
        if (alsoDeclareQualityInMetadata)
        {
            metadata["quality"] = quality.ToString();
        }

        return new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithSourceName(Northwind)
            .WithSourceUrl("https://example.com/" + hash)
            .WithTitle("Northwind update " + hash)
            .WithSummary(null)
            .WithRawText("Northwind Robotics signs a multi-year deal with a partner.")
            .WithContentHash(hash)
            .WithPublishedAtUtc(InWindow)
            .WithCollectedAtUtc(InWindow.AddHours(1))
            .WithQuality(quality)
            .WithMetadataJson(EvidenceMetadata.Compose(metadata, [Northwind]))
            .Build();
    }

    private static Signal BuildSignal(EvidenceItem evidence, string idSeed) => new SignalBuilder()
        .WithId(Guid.Parse(idSeed))
        .WithEvidenceId(evidence.Id)
        .WithCompanyId(NorthwindId)
        .WithCompanyMention(Northwind)
        .WithType(SignalType.CustomerWin)
        .WithDirection(SignalDirection.Positive)
        .WithSupportingExcerpt("signs a multi-year deal")
        .WithReviewStatus(SignalReviewStatus.Approved)
        .WithObservedAtUtc(InWindow)
        .WithCreatedAtUtc(InWindow.AddHours(1))
        .Build();

    private static SignalReview ReviewFor(Signal s) => new(
        Id: Guid.NewGuid(),
        SignalId: s.Id,
        ReviewerName: "deterministic-reviewer-v1",
        Decision: SignalReviewDecision.Approve,
        Summary: "Approve: excerpt found in evidence.",
        IssuesJson: null,
        ReviewedAtUtc: s.CreatedAtUtc);

    /// <summary>
    /// Persists the fixture through the SAME seams the pipeline uses (evidence repository + raw evidence
    /// store; signal repository + signal file store), so the on-disk bytes are exactly what a real run
    /// would leave behind.
    /// </summary>
    private static async Task SeedAsync(ServiceProvider sp, IReadOnlyList<(EvidenceItem E, Signal S)> pairs)
    {
        await sp.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);

        var evidenceRepo = sp.GetRequiredService<IEvidenceRepository>();
        var rawStore = sp.GetRequiredService<IRawEvidenceStore>();
        var signalRepo = sp.GetRequiredService<ISignalRepository>();
        var signalStore = sp.GetRequiredService<ISignalFileStore>();

        foreach (var (evidence, signal) in pairs)
        {
            Assert.True(await evidenceRepo.AddIfNewAsync(evidence, default));
            await rawStore.WriteIfNewAsync(evidence, default);
            await signalRepo.AddAsync(signal, default);
            await signalStore.WriteAsync(signal, ReviewFor(signal), default);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // 1. The invariant.
    // -------------------------------------------------------------------------------------------------

    [Theory]
    // quality carried ONLY by the new top-level field (deleting it must break the invariant)…
    [InlineData(EvidenceQuality.PrimarySource, false)]
    [InlineData(EvidenceQuality.High, false)]
    [InlineData(EvidenceQuality.Medium, false)]
    [InlineData(EvidenceQuality.Unknown, false)]
    // …and the production shape, where the collector also declares it in the metadata bag.
    [InlineData(EvidenceQuality.PrimarySource, true)]
    [InlineData(EvidenceQuality.Medium, true)]
    public async Task HydratedDurableScoring_IsFieldForFieldIdenticalToInMemoryScoring(
        EvidenceQuality quality, bool alsoDeclareQualityInMetadata)
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

        var e1 = BuildEvidence("hash-alpha", quality, alsoDeclareQualityInMetadata);
        var e2 = BuildEvidence("hash-beta", quality, alsoDeclareQualityInMetadata);
        var pairs = new[]
        {
            (e1, BuildSignal(e1, "aaaa0000-0000-0000-0000-000000000001")),
            (e2, BuildSignal(e2, "aaaa0000-0000-0000-0000-000000000002")),
        };

        // Pass 1: score with the signals held IN MEMORY (this is also what writes the fixture to disk).
        await using var inMemory = BuildProvider(fx, durable: false);
        await SeedAsync(inMemory, pairs);
        var expected = await inMemory.GetRequiredService<IScoringEngine>()
            .ScoreCompanyAsync(NorthwindId, FixedNow, default);

        // Pass 2: a FRESH container over the SAME directories, holding nothing in memory. Everything it
        // scores has to come back off disk.
        await using var durable = BuildProvider(fx, durable: true);
        await durable.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        var actual = await durable.GetRequiredService<IScoringEngine>()
            .ScoreCompanyAsync(NorthwindId, FixedNow, default);

        Assert.Equal(SnapshotFields(expected.Snapshot), SnapshotFields(actual.Snapshot));
        Assert.Equal(LinkFields(expected.Links), LinkFields(actual.Links));

        // Provenance actually survived the round trip (a zero-link snapshot would trivially "match").
        Assert.NotEmpty(actual.Links);
        Assert.Equal(2, actual.Links.Count);
    }

    [Fact]
    public async Task EvidenceQualityIsNotLostOnDisk_ItStillMovesEvidenceConfidence()
    {
        // The guard that makes the invariant above meaningful: if EvidenceQuality were dropped in
        // serialization, both of these would hydrate as Unknown and score identically.
        var primary = await ScoreFromDiskAsync(EvidenceQuality.PrimarySource);
        var unknown = await ScoreFromDiskAsync(EvidenceQuality.Unknown);

        Assert.True(
            primary > unknown,
            $"PrimarySource-backed EC ({primary}) must exceed Unknown-backed EC ({unknown}) after hydration.");

        static async Task<int> ScoreFromDiskAsync(EvidenceQuality quality)
        {
            using var fx = new TempPipelineFixtures();
            fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

            var e = BuildEvidence("hash-" + quality, quality);
            await using var writer = BuildProvider(fx, durable: false);
            await SeedAsync(writer, [(e, BuildSignal(e, "aaaa0000-0000-0000-0000-00000000000a"))]);

            await using var reader = BuildProvider(fx, durable: true);
            await reader.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
            var result = await reader.GetRequiredService<IScoringEngine>()
                .ScoreCompanyAsync(NorthwindId, FixedNow, default);
            return result.Snapshot.EvidenceConfidenceScore;
        }
    }

    // -------------------------------------------------------------------------------------------------
    // 2. Spec 136's known-at predicate against a HYDRATED durable store.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task KnownAsOf_HydratedStore_ExcludesASignalCreatedAfterTheAsOfInstant()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

        var early = BuildEvidence("hash-early", EvidenceQuality.High);
        var late = BuildEvidence("hash-late", EvidenceQuality.High);

        var earlySignal = BuildSignal(early, "bbbb0000-0000-0000-0000-000000000001");
        // Observed in-window, but only KNOWN after the as-of instant below.
        var lateSignal = BuildSignal(late, "bbbb0000-0000-0000-0000-000000000002")
            with { CreatedAtUtc = FixedNow.AddDays(1) };

        await using var writer = BuildProvider(fx, durable: false);
        await SeedAsync(writer, [(early, earlySignal), (late, lateSignal)]);

        await using var reader = BuildProvider(fx, durable: true);
        await reader.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        var result = await reader.GetRequiredService<IScoringEngine>()
            .ScoreCompanyAsync(NorthwindId, FixedNow, default);

        // Only the signal Radar knew about by FixedNow contributes.
        Assert.Equal([earlySignal.Id], result.Links.Select(l => l.SignalId).Distinct().ToArray());
    }

    [Fact]
    public async Task KnownAsOf_EqualityBoundary_IsANoOpForAForwardRun_OnAHydratedStore()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

        var e = BuildEvidence("hash-boundary", EvidenceQuality.High);
        // AD-7: this run's signals carry CreatedAtUtc == asOfUtc == windowEndUtc EXACTLY.
        var s = BuildSignal(e, "cccc0000-0000-0000-0000-000000000001") with { CreatedAtUtc = FixedNow };

        await using var writer = BuildProvider(fx, durable: false);
        await SeedAsync(writer, [(e, s)]);

        await using var reader = BuildProvider(fx, durable: true);
        await reader.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        var result = await reader.GetRequiredService<IScoringEngine>()
            .ScoreCompanyAsync(NorthwindId, FixedNow, default);

        Assert.Equal([s.Id], result.Links.Select(l => l.SignalId).Distinct().ToArray());
    }

    // -------------------------------------------------------------------------------------------------
    // 3. Idempotent re-collection across PROCESSES (the acceptance criterion the in-memory repo faked).
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SecondRunInAFreshProcess_ReExtractsNothing_AndScoresIdentically()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics launches a new platform and signs a multi-year deal with a partner.",
            "2026-06-24T00:00:00Z", quality: "High");

        // Run 1.
        await using var run1 = BuildProvider(fx, durable: true);
        await run1.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        var first = await run1.GetRequiredService<IRadarPipeline>().RunAsync(default);
        Assert.True(first.EvidenceNew >= 1);
        Assert.True(first.SignalsExtracted >= 1);

        var evidenceFilesAfterFirst = FileSnapshot(fx.RawEvidenceDir);
        var signalFilesAfterFirst = FileSnapshot(fx.SignalsDir);
        var firstScore = await run1.GetRequiredService<IScoringEngine>()
            .ScoreCompanyAsync(NorthwindId, FixedNow, default);

        // Run 2 in a FRESH container over the same directories — a new process, nothing in memory.
        await using var run2 = BuildProvider(fx, durable: true);
        await run2.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        var second = await run2.GetRequiredService<IRadarPipeline>().RunAsync(default);

        // The evidence was already accrued, so nothing is re-extracted and no signal is re-minted.
        Assert.Equal(0, second.EvidenceNew);
        Assert.Equal(0, second.SignalsExtracted);

        // Append-only (AD-8): no file added, removed, or rewritten.
        Assert.Equal(evidenceFilesAfterFirst, FileSnapshot(fx.RawEvidenceDir));
        Assert.Equal(signalFilesAfterFirst, FileSnapshot(fx.SignalsDir));

        // …and the second run's scoring is unchanged.
        var secondScore = await run2.GetRequiredService<IScoringEngine>()
            .ScoreCompanyAsync(NorthwindId, FixedNow, default);
        Assert.Equal(SnapshotFields(firstScore.Snapshot), SnapshotFields(secondScore.Snapshot));
        Assert.Equal(LinkFields(firstScore.Links), LinkFields(secondScore.Links));
        Assert.NotEmpty(secondScore.Links);
    }

    // -------------------------------------------------------------------------------------------------
    // 4. The weekly report stays PERIOD-filtered now that the repositories return full accrued history.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WeeklyReport_NeedsReviewSection_StaysBoundedToTheReportPeriod()
    {
        // WeeklyReportBuilder reads needs-review signals through ISignalRepository.GetObservedBetweenAsync,
        // which is period-filtered — so pointing that interface at accrued history must NOT widen the
        // report. A signal observed long before the period must stay out of it.
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

        var recent = BuildEvidence("hash-recent", EvidenceQuality.High);
        var ancient = BuildEvidence("hash-ancient", EvidenceQuality.High) with
        {
            PublishedAtUtc = FixedNow.AddYears(-5),
        };

        var recentSignal = BuildSignal(recent, "dddd0000-0000-0000-0000-000000000001") with
        {
            ReviewStatus = SignalReviewStatus.NeedsHumanReview,
            CompanyMention = "Recent Mention",
        };
        var ancientSignal = BuildSignal(ancient, "dddd0000-0000-0000-0000-000000000002") with
        {
            ReviewStatus = SignalReviewStatus.NeedsHumanReview,
            CompanyMention = "Ancient Mention",
            ObservedAtUtc = FixedNow.AddYears(-5),
            CreatedAtUtc = FixedNow.AddYears(-5),
        };

        await using var writer = BuildProvider(fx, durable: false);
        await SeedAsync(writer, [(recent, recentSignal), (ancient, ancientSignal)]);

        await using var reader = BuildProvider(fx, durable: true);
        await reader.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);

        // Sanity: the durable repository really does hold BOTH, so the report's filter is what excludes one.
        var all = await reader.GetRequiredService<ISignalRepository>()
            .GetByCompanyAsync(NorthwindId, default);
        Assert.Equal(2, all.Count);

        var report = await reader.GetRequiredService<IWeeklyReportBuilder>()
            .GenerateAsync(FixedNow, CollectionSummary.Empty, health: null, default);

        Assert.Contains("Recent Mention", report.Report.MarkdownContent);
        Assert.DoesNotContain("Ancient Mention", report.Report.MarkdownContent);
    }

    /// <summary>Relative path → (length, content hash-ish) for every file under a root, for an exact "unchanged" compare.</summary>
    private static Dictionary<string, string> FileSnapshot(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                File.ReadAllText,
                StringComparer.Ordinal);
    }
}
