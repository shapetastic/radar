using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Domain.Scoring;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 144 end to end, over the real wired graph and the real on-disk stores: a <c>collect</c> pass in ONE
/// container followed by a <c>score</c> pass in a SEPARATE container — i.e. what actually happens when the
/// two verbs run as two scheduled processes — reproduces the combined run's scores.
/// <para>
/// The second container starts with empty in-memory state, so this only works because spec 142 made the
/// repositories the durable file stores. That is the load-bearing prerequisite, asserted here rather than
/// assumed.
/// </para>
/// </summary>
public sealed class CollectThenScoreEndToEndTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
    private const string InPeriodPublished = "2026-06-24T00:00:00Z";
    private const string LaterInPeriodPublished = "2026-06-25T00:00:00Z";

    private static readonly Guid NorthwindId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Northwind = "Northwind Robotics";

    private enum Mode
    {
        Full,
        Collect,
        Score,
    }

    /// <summary>
    /// Builds the graph for ONE pass. Mirrors the Worker composition's ordering and — decisively — its
    /// <c>AddDurableRadarSignalHistory</c> call, so scoring reads accrued history off disk. A <c>Score</c>
    /// pass registers NO collector at all, exactly as the composition root does in that mode.
    /// </summary>
    private static ServiceProvider BuildProvider(TempPipelineFixtures fixtures, Mode mode)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedNow));
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();

        if (mode != Mode.Score)
        {
            services.AddLocalFileCollector(fixtures.EvidenceDir);
        }

        services.AddLocalFileCompanySeed(fixtures.SeedFilePath);
        services.AddFileRawEvidenceStore(fixtures.RawEvidenceDir);
        services.AddFileSignalStore(fixtures.SignalsDir);
        services.AddDurableRadarSignalHistory();
        services.AddFileScoreStore(Path.Combine(fixtures.RootDir, "scores"));
        services.AddFileReportWriter(Path.Combine(fixtures.RootDir, "reports"));
        services.AddFilePipelineRunStore(Path.Combine(fixtures.RootDir, "runs"));
        services.AddFileScoringConfigStore(Path.Combine(fixtures.RootDir, "scoring-configs"));

        switch (mode)
        {
            case Mode.Collect:
                services.AddRadarCollectOnlyPipeline();
                break;
            case Mode.Score:
                services.AddRadarScoreOnlyPipeline();
                break;
            default:
                services.AddRadarPipeline();
                break;
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Two evidence items on DISTINCT published dates, each yielding one signal. Distinct dates are
    /// deliberate: <c>ScoringEngine</c> orders contributions by <c>ObservedAtUtc</c> and tiebreaks on
    /// <c>Signal.Id</c>, which <c>ExtractedSignalMapper</c> mints fresh on every extraction — so two signals
    /// sharing an instant would order differently between ANY two runs, including two consecutive combined
    /// runs. That nondeterminism predates this slice; the fixture simply avoids depending on it, so a
    /// failure here means the split changed something rather than that a Guid sorted the other way.
    /// </summary>
    private static void WriteFixture(TempPipelineFixtures fx)
    {
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);
        fx.WriteEvidence(
            "northwind-launch.json", Northwind, "Northwind launch",
            "Northwind Robotics launches a new platform for industrial automation.",
            InPeriodPublished, quality: "High");
        fx.WriteEvidence(
            "northwind-win.json", Northwind, "Northwind customer win",
            "Northwind Robotics signs a multi-year deal with a Fortune 100 partner.",
            LaterInPeriodPublished, quality: "High");
    }

    private static async Task<RadarPipelineResult> SeedAndRunAsync(ServiceProvider sp)
    {
        await sp.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        return await sp.GetRequiredService<IRadarPipeline>().RunAsync(default);
    }

    /// <summary>
    /// THE acceptance criterion at the process boundary: two separate containers over the same on-disk
    /// stores (collect, then score) produce the same score as one combined run over an identical fixture.
    /// <para>
    /// The comparison is over the whole snapshot RECORD with the per-call minted <c>Id</c> normalised away
    /// (the spec-139 exclusion) and <c>CollectionProvenance</c> compared SEPARATELY — see the assertions
    /// below: a score pass registers no collector, so it honestly records the empty collector set. That is
    /// recorded provenance, hashed into nothing, and the assertions prove the fingerprint and every
    /// component are unaffected by it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CollectThenScore_InSeparateContainers_ReproducesTheCombinedRunsScore()
    {
        using var combinedFx = new TempPipelineFixtures();
        using var splitFx = new TempPipelineFixtures();
        WriteFixture(combinedFx);
        WriteFixture(splitFx);

        CompanyScoreSnapshot combinedSnapshot;
        IReadOnlyList<ScoreEvidenceLink> combinedLinks;
        await using (var combined = BuildProvider(combinedFx, Mode.Full))
        {
            var result = await SeedAndRunAsync(combined);
            Assert.Equal(1, result.CompaniesScored);

            var scores = combined.GetRequiredService<IScoreRepository>();
            combinedSnapshot = Assert.Single(
                await scores.GetSnapshotsForCompanyAsync(NorthwindId, default));
            combinedLinks = await scores.GetLinksForSnapshotAsync(combinedSnapshot.Id, default);
            Assert.NotEmpty(combinedLinks);
        }

        // --- process 1: collect only ---
        await using (var collect = BuildProvider(splitFx, Mode.Collect))
        {
            var collectResult = await SeedAndRunAsync(collect);
            Assert.True(collectResult.EvidenceNew >= 1);
            Assert.Equal(0, collectResult.CompaniesScored);
            Assert.Null(collectResult.ReportId);

            // Nothing scored: the live scores root does not exist yet.
            Assert.False(Directory.Exists(Path.Combine(splitFx.RootDir, "scores")));
        }

        // --- process 2: score only, over the accrued store, with NO collector registered ---
        CompanyScoreSnapshot splitSnapshot;
        IReadOnlyList<ScoreEvidenceLink> splitLinks;
        await using (var score = BuildProvider(splitFx, Mode.Score))
        {
            Assert.Empty(score.GetServices<IEvidenceCollector>());

            var scoreResult = await SeedAndRunAsync(score);
            Assert.Equal(1, scoreResult.CompaniesScored);
            Assert.Equal(0, scoreResult.EvidenceCollected);

            var scores = score.GetRequiredService<IScoreRepository>();
            splitSnapshot = Assert.Single(await scores.GetSnapshotsForCompanyAsync(NorthwindId, default));
            splitLinks = await scores.GetLinksForSnapshotAsync(splitSnapshot.Id, default);
        }

        // Not vacuous: the split world really did resolve evidence and build the same provenance chain —
        // the same content-derived EvidenceIds (spec 145), the same reasons, the same weights.
        //
        // Compared as a SET (ordered by reason), because the READ order is not the contribution order:
        // InMemoryScoreRepository.GetLinksForSnapshotAsync returns links ordered by their per-call minted
        // Guid, so two runs read the same links back in different orders. That is pre-existing repository
        // behaviour, unrelated to this slice; the engine's own contribution order is deterministic
        // (ObservedAtUtc, then signal id) and the fixture's distinct published dates keep it so.
        Assert.NotEmpty(splitLinks);
        Assert.Equal(2, splitLinks.Count);
        Assert.Equal(
            combinedLinks
                .Select(l => (l.EvidenceId, l.ContributionReason, l.ContributionWeight))
                .OrderBy(l => l.ContributionReason, StringComparer.Ordinal),
            splitLinks
                .Select(l => (l.EvidenceId, l.ContributionReason, l.ContributionWeight))
                .OrderBy(l => l.ContributionReason, StringComparer.Ordinal));

        // Every scoring-relevant field, compared as a record so a field added later is covered by
        // construction. Only the per-call minted Id and the recorded collection provenance are normalised.
        Assert.Equal(Normalize(combinedSnapshot), Normalize(splitSnapshot));

        // The fingerprint did NOT move: dropping the collectors changes recorded provenance and nothing else
        // (spec 141). This is what makes the score pass's snapshots the same series as the combined run's.
        Assert.Equal(combinedSnapshot.ScoringConfigVersion, splitSnapshot.ScoringConfigVersion);
        Assert.Equal(combinedSnapshot.StrategyName, splitSnapshot.StrategyName);

        // …and the one honest difference, recorded rather than hidden: a score pass ran no collector, so it
        // records the empty collector set. Same class of caveat spec 139 records for replay.
        Assert.Equal("collectors=LocalFileEvidenceCollector;", combinedSnapshot.CollectionProvenance);
        Assert.Equal("collectors=;", splitSnapshot.CollectionProvenance);
    }

    /// <summary>
    /// A score pass over an EMPTY store is not an error — it produces the neutral zero-evidence-link
    /// snapshot a zero-signal company already gets — which is the property that lets scoring run on its own
    /// cadence without being coupled to whether collection found anything.
    /// </summary>
    [Fact]
    public async Task ScorePass_WithNothingAccrued_ScoresNeutrally_WithoutCollecting()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

        await using var score = BuildProvider(fx, Mode.Score);
        var result = await SeedAndRunAsync(score);

        Assert.Equal(1, result.CompaniesScored);
        Assert.Equal(0, result.EvidenceCollected);

        var scores = score.GetRequiredService<IScoreRepository>();
        var snapshot = Assert.Single(await scores.GetSnapshotsForCompanyAsync(NorthwindId, default));
        Assert.Empty(await scores.GetLinksForSnapshotAsync(snapshot.Id, default));
    }

    /// <summary>
    /// Re-scoring is cheap and repeatable — the point of the split. A second score pass over an UNCHANGED
    /// store reproduces the first pass's scores exactly, and collects nothing either time.
    /// </summary>
    [Fact]
    public async Task ScorePass_IsRepeatable_OverAnUnchangedStore()
    {
        using var fx = new TempPipelineFixtures();
        WriteFixture(fx);

        await using (var collect = BuildProvider(fx, Mode.Collect))
        {
            await SeedAndRunAsync(collect);
        }

        CompanyScoreSnapshot first;
        await using (var score = BuildProvider(fx, Mode.Score))
        {
            await SeedAndRunAsync(score);
            first = Assert.Single(await score.GetRequiredService<IScoreRepository>()
                .GetSnapshotsForCompanyAsync(NorthwindId, default));
        }

        CompanyScoreSnapshot second;
        await using (var score = BuildProvider(fx, Mode.Score))
        {
            await SeedAndRunAsync(score);
            second = Assert.Single(await score.GetRequiredService<IScoreRepository>()
                .GetSnapshotsForCompanyAsync(NorthwindId, default));
        }

        Assert.Equal(Normalize(first), Normalize(second));
    }

    /// <summary>
    /// Normalises a snapshot for comparison: the per-call minted <c>Id</c> (the spec-139 exclusion — the
    /// engine mints one on EVERY call, so two consecutive forward runs differ in it too) and the recorded
    /// <c>CollectionProvenance</c>, which is asserted separately because it legitimately differs between a
    /// pass that ran collectors and one that registered none.
    /// </summary>
    private static CompanyScoreSnapshot Normalize(CompanyScoreSnapshot snapshot) =>
        snapshot with { Id = Guid.Empty, CollectionProvenance = null };
}
