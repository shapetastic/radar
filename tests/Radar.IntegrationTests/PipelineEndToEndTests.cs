using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.IntegrationTests;

/// <summary>
/// Black-box end-to-end tests over the real wired DI graph (real LocalFile sources, real keyword
/// extractor, all stages). Only the clock (a fixed <see cref="FakeTimeProvider"/>) and the file
/// inputs (temp-dir JSON fixtures) are faked. Assertions are behavioural/structural — relative
/// magnitudes, ordering, label-in-allowed-set, provenance consistency, substrings — never formula
/// magic numbers or generated Guids.
/// </summary>
public sealed class PipelineEndToEndTests
{
    // Fixed run instant; both the 30-day scoring window and 7-day report period end here.
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    // A published date a few days before FixedNow → inside both the 30-day and 7-day windows.
    private const string InPeriodPublished = "2026-06-24T00:00:00Z";

    // Fixed seed-company Guids for stable tiebreaks. Plain multi-word names with NO trailing
    // company-suffix token so resolution is an exact normalized name match against the evidence
    // sourceName (which the keyword extractor sets as the CompanyMention).
    private static readonly Guid NorthwindId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AcmeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BorealisId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string Northwind = "Northwind Robotics";
    private const string Acme = "Acme Dynamics";
    private const string Borealis = "Borealis Systems";

    // The six allowed display labels the markdown may render.
    private static readonly string[] AllowedDisplayLabels =
    [
        "Investigate", "Watch", "Ignore", "Needs more evidence", "Thesis improving", "Thesis deteriorating",
    ];

    private static ServiceProvider BuildProvider(
        TempPipelineFixtures fixtures, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        // FIRST: wins over System. Defaults to a constant clock; tests may inject an auto-advancing
        // FakeTimeProvider to reproduce production wall-clock skew (spec 49 regression guard).
        services.AddSingleton<TimeProvider>(timeProvider ?? new FakeTimeProvider(FixedNow));
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCollector(fixtures.EvidenceDir);
        services.AddLocalFileCompanySeed(fixtures.SeedFilePath);
        services.AddFileRawEvidenceStore(fixtures.RawEvidenceDir);
        services.AddFileSignalStore(fixtures.SignalsDir);
        services.AddFileScoreStore(Path.Combine(fixtures.RootDir, "scores"));
        services.AddFileReportWriter(Path.Combine(fixtures.RootDir, "reports"));
        services.AddRadarPipeline();
        return services.BuildServiceProvider();
    }

    private static async Task<(RadarPipelineResult Result, RadarReport Report, IReadOnlyList<RadarReportItem> Items)>
        SeedAndRunAsync(ServiceProvider sp, CancellationToken ct = default)
    {
        await sp.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);
        var result = await sp.GetRequiredService<IRadarPipeline>().RunAsync(ct);
        var reports = sp.GetRequiredService<IReportRepository>();
        var report = await reports.GetByIdAsync(result.ReportId!.Value, ct);
        var items = await reports.GetItemsAsync(result.ReportId!.Value, ct);
        return (result, report!, items);
    }

    private static string DisplayLabel(RadarReportAction action) => action switch
    {
        RadarReportAction.Investigate => "Investigate",
        RadarReportAction.Watch => "Watch",
        RadarReportAction.Ignore => "Ignore",
        RadarReportAction.NeedsMoreEvidence => "Needs more evidence",
        RadarReportAction.ThesisImproving => "Thesis improving",
        RadarReportAction.ThesisDeteriorating => "Thesis deteriorating",
        _ => action.ToString(),
    };

    /// <summary>
    /// Returns the single snapshot for a company that has been scored exactly once. Asserts there is
    /// exactly one so multi-scored companies (where a constant FakeTimeProvider makes "latest"
    /// ambiguous, all snapshots sharing CreatedAtUtc) are never silently mis-read.
    /// </summary>
    private static async Task<CompanyScoreSnapshot> OnlySnapshotAsync(
        ServiceProvider sp, Guid companyId)
    {
        var scores = sp.GetRequiredService<IScoreRepository>();
        var snapshots = await scores.GetSnapshotsForCompanyAsync(companyId, default);
        return Assert.Single(snapshots);
    }

    /// <summary>Fetches a specific company snapshot by its id (the id a report item cites).</summary>
    private static async Task<CompanyScoreSnapshot> SnapshotByIdAsync(
        ServiceProvider sp, Guid companyId, Guid snapshotId)
    {
        var scores = sp.GetRequiredService<IScoreRepository>();
        var snapshots = await scores.GetSnapshotsForCompanyAsync(companyId, default);
        return snapshots.Single(s => s.Id == snapshotId);
    }

    // ---------------------------------------------------------------------------------------------
    // 1. Golden path.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task GoldenPath_ThreeCompanies_ProducesRankedReportWithProvenance()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies(
        [
            new(NorthwindId, Northwind, "NWR", []),
            new(AcmeId, Acme, "ACM", []),
            new(BorealisId, Borealis, "BOR", []),
        ]);

        // Northwind: multiple trigger phrases in one evidence file.
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics launches a new platform and signs a multi-year deal with a partner.",
            InPeriodPublished, quality: "High");
        fx.WriteEvidence(
            "acme.json", Acme, "Acme update",
            "Acme Dynamics partners with a major integrator this quarter.",
            InPeriodPublished, quality: "High");
        fx.WriteEvidence(
            "borealis.json", Borealis, "Borealis update",
            "Borealis Systems raises $40 million in a new round.",
            InPeriodPublished, quality: "High");

        await using var sp = BuildProvider(fx);
        var (result, report, items) = await SeedAndRunAsync(sp);

        // Exact run-summary counts. Northwind file yields 2 signals (ProductLaunch + CustomerWin),
        // Acme 1 (StrategicPartnership), Borealis 1 (CapitalRaise) → 4 extracted, all valid/approved.
        Assert.Equal(3, result.EvidenceCollected);
        Assert.Equal(3, result.EvidenceNew);
        Assert.Equal(4, result.SignalsExtracted);
        Assert.Equal(4, result.SignalsValid);
        Assert.Equal(4, result.SignalsApproved);
        Assert.Equal(0, result.SignalsNeedingReview);
        Assert.Equal(3, result.CompaniesScored);

        // All three disclaimers present.
        Assert.Contains("> Not financial advice.", report.MarkdownContent);
        Assert.Contains("> For research only.", report.MarkdownContent);
        Assert.Contains("> Human review required.", report.MarkdownContent);

        // Three ranked report items.
        Assert.Equal(3, items.Count);

        // Items ordered by Rank ascending == OpportunityScore descending. Verify against snapshots.
        var ordered = items.OrderBy(i => i.Rank).ToList();
        int? previousOpportunity = null;
        foreach (var item in ordered)
        {
            Assert.NotEqual(Guid.Empty, item.ScoreSnapshotId);
            var snap = await SnapshotByIdAsync(sp, item.CompanyId, item.ScoreSnapshotId);

            if (previousOpportunity is { } prev)
            {
                Assert.True(snap.OpportunityScore <= prev,
                    "Report items must be ranked by descending OpportunityScore.");
            }
            previousOpportunity = snap.OpportunityScore;

            // Each item shows its snapshot id in the markdown and has >=1 evidence link (provenance).
            Assert.Contains(item.ScoreSnapshotId.ToString(), report.MarkdownContent);
            var links = await sp.GetRequiredService<IScoreRepository>()
                .GetLinksForSnapshotAsync(item.ScoreSnapshotId, default);
            Assert.NotEmpty(links);

            // Every emitted label is in the allowed display-label set.
            Assert.Contains(DisplayLabel(item.SuggestedAction), AllowedDisplayLabels);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Quality flows through (Part A end-to-end).
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task QualityFlowsThrough_PrimarySourceBeatsUnknown_AndClearsConfidenceFloor()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies(
        [
            new(NorthwindId, Northwind, "NWR", []),
            new(AcmeId, Acme, "ACM", []),
        ]);

        // Two otherwise-equivalent positive-signal companies: identical trigger phrase + published
        // date; the ONLY difference is the declared evidence quality.
        const string phrase = "signs a multi-year deal with a strategic partner";
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            $"Northwind Robotics {phrase}.", InPeriodPublished, quality: "PrimarySource");
        fx.WriteEvidence(
            "acme.json", Acme, "Acme update",
            $"Acme Dynamics {phrase}.", InPeriodPublished, quality: "Unknown");

        await using var sp = BuildProvider(fx);
        await SeedAndRunAsync(sp);

        var primary = await OnlySnapshotAsync(sp, NorthwindId);
        var unknown = await OnlySnapshotAsync(sp, AcmeId);

        // Part A end-to-end: the PrimarySource-backed company has a strictly higher evidence
        // confidence than the Unknown-backed one (impossible before quality became an input).
        Assert.True(primary.EvidenceConfidenceScore > unknown.EvidenceConfidenceScore,
            $"PrimarySource EC ({primary.EvidenceConfidenceScore}) must exceed Unknown EC " +
            $"({unknown.EvidenceConfidenceScore}).");

        // The Unknown-quality company's evidence confidence stays below the policy floor.
        Assert.True(unknown.EvidenceConfidenceScore < 35);
        // The PrimarySource company clears the floor (quality lifted it).
        Assert.True(primary.EvidenceConfidenceScore >= 35);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Unresolved mention → needs review.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task UnresolvedMention_DoesNotScore_AppearsUnderNeedsReview()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

        // A resolvable company so a report still generates.
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics launches a new product line.", InPeriodPublished, quality: "High");

        // An unseeded source name → its mention cannot resolve.
        const string ghost = "Unlisted Ventures";
        fx.WriteEvidence(
            "ghost.json", ghost, "Ghost update",
            "Unlisted Ventures partners with an undisclosed firm.", InPeriodPublished, quality: "High");

        await using var sp = BuildProvider(fx);
        var (result, report, _) = await SeedAndRunAsync(sp);

        // The unresolved mention is routed to human review, not scored against any company.
        Assert.True(result.SignalsNeedingReview >= 1);
        Assert.Contains("## Signals needing review", report.MarkdownContent);
        Assert.Contains(ghost, report.MarkdownContent);

        // Only the one seeded company has a meaningful (linked) snapshot; the ghost never resolves.
        var scores = sp.GetRequiredService<IScoreRepository>();
        var nwSnap = await OnlySnapshotAsync(sp, NorthwindId);
        var nwLinks = await scores.GetLinksForSnapshotAsync(nwSnap.Id, default);
        Assert.NotEmpty(nwLinks);
    }

    // ---------------------------------------------------------------------------------------------
    // 4. Direction matters.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task DirectionMatters_NegativeRanksBelowPositive_WithLowerTrajectory()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies(
        [
            new(NorthwindId, Northwind, "NWR", []),
            new(AcmeId, Acme, "ACM", []),
        ]);

        // Positive company.
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics signs a multi-year deal with a partner.",
            InPeriodPublished, quality: "High");

        // Negative-only company.
        fx.WriteEvidence(
            "acme.json", Acme, "Acme update",
            "Acme Dynamics cuts guidance for the coming year.",
            InPeriodPublished, quality: "High");

        await using var sp = BuildProvider(fx);
        var (_, _, items) = await SeedAndRunAsync(sp);

        var negative = await OnlySnapshotAsync(sp, AcmeId);
        var positive = await OnlySnapshotAsync(sp, NorthwindId);

        // The negative-only signal pulls trajectory below the neutral midpoint.
        Assert.True(negative.TrajectoryScore < 50);

        // The negative company ranks below the positive one (higher Rank number / lower opportunity).
        var negativeItem = items.Single(i => i.CompanyId == AcmeId);
        var positiveItem = items.Single(i => i.CompanyId == NorthwindId);
        Assert.True(negativeItem.Rank > positiveItem.Rank);
        Assert.True(negative.OpportunityScore <= positive.OpportunityScore);
    }

    // ---------------------------------------------------------------------------------------------
    // 5. Same-run window (Part B end-to-end).
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task SameRunWindow_EvidenceWithNoPublishedAt_StillScores()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);

        // NO publishedAtUtc → ObservedAtUtc falls back to CollectedAtUtc (== the run instant). With
        // the Part B fix (asOfUtc captured after collection), this sits at the inclusive end of the
        // (start, end] scoring window and still scores.
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics signs a multi-year deal with a partner.",
            publishedAtUtc: null, quality: "High");

        await using var sp = BuildProvider(fx);
        var (result, _, _) = await SeedAndRunAsync(sp);

        Assert.True(result.CompaniesScored >= 1);

        // The freshly collected (no-publishedAt) evidence's signal is reflected in the snapshot.
        var scores = sp.GetRequiredService<IScoreRepository>();
        var snap = await OnlySnapshotAsync(sp, NorthwindId);
        var links = await scores.GetLinksForSnapshotAsync(snap.Id, default);
        Assert.NotEmpty(links);
    }

    // ---------------------------------------------------------------------------------------------
    // 6. Idempotent re-run.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task IdempotentRerun_SecondRunAddsNoNewEvidenceOrSignals()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics signs a multi-year deal with a partner.",
            InPeriodPublished, quality: "High");

        await using var sp = BuildProvider(fx);

        // First run over the same in-memory repos.
        await sp.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        var pipeline = sp.GetRequiredService<IRadarPipeline>();
        var first = await pipeline.RunAsync(default);
        Assert.True(first.EvidenceNew >= 1);

        // Second run over the same inputs/state: dedupe by content hash (AD-1).
        var second = await pipeline.RunAsync(default);
        Assert.Equal(0, second.EvidenceNew);
        Assert.Equal(0, second.SignalsExtracted);
        Assert.NotNull(second.ReportId); // still produces a valid report
    }

    // ---------------------------------------------------------------------------------------------
    // 7. Output-language guard.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task OutputLanguageGuard_NoAdviceLanguage_OnlyAllowedLabels()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies(
        [
            new(NorthwindId, Northwind, "NWR", []),
            new(AcmeId, Acme, "ACM", []),
        ]);
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics launches a new platform and signs a multi-year deal.",
            InPeriodPublished, quality: "High");
        fx.WriteEvidence(
            "acme.json", Acme, "Acme update",
            "Acme Dynamics cuts guidance for the year ahead.",
            InPeriodPublished, quality: "High");

        await using var sp = BuildProvider(fx);
        var (_, report, items) = await SeedAndRunAsync(sp);

        var lowered = report.MarkdownContent.ToLowerInvariant();
        Assert.DoesNotContain("buy", lowered);
        Assert.DoesNotContain("sell", lowered);
        Assert.DoesNotContain("guaranteed upside", lowered);
        Assert.DoesNotContain("safe bet", lowered);

        // Only allowed display labels appear.
        foreach (var item in items)
        {
            Assert.Contains(DisplayLabel(item.SuggestedAction), AllowedDisplayLabels);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 8. Regression guard for spec 49 — snapshot CreatedAtUtc must equal the run instant.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ClockSkew_SnapshotCreatedThisRun_StillAppearsInReport()
    {
        using var fx = new TempPipelineFixtures();
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);
        fx.WriteEvidence(
            "northwind.json", Northwind, "Northwind update",
            "Northwind Robotics signs a multi-year deal with a partner.",
            InPeriodPublished, quality: "High");

        // An auto-advancing clock: every GetUtcNow() returns an instant 200ms later than the last,
        // mirroring the observed live wall-clock skew. This makes the pipeline's asOfUtc capture
        // (the scoring windowEndUtc / report periodEndUtc) and any LATER clock read return DIFFERENT
        // instants. Before the spec-49 fix the snapshot stamped CreatedAtUtc from a fresh GetUtcNow()
        // read AFTER asOfUtc, so CreatedAtUtc > periodEndUtc and the report's inclusive (start, end]
        // window EXCLUDED the just-created snapshot → empty report. With the fix CreatedAtUtc ==
        // windowEndUtc == periodEndUtc, so the company surfaces. The 200ms advance is irrelevant to
        // the 30-day scoring window (InPeriodPublished 2026-06-24 vs FixedNow 2026-06-28), so the
        // in-period evidence still scores. THIS TEST FAILS without the
        // `ScoringEngine.CreatedAtUtc = windowEndUtc` fix.
        var skewingClock = new FakeTimeProvider(FixedNow)
        {
            AutoAdvanceAmount = TimeSpan.FromMilliseconds(200),
        };

        await using var sp = BuildProvider(fx, skewingClock);
        var (_, report, items) = await SeedAndRunAsync(sp);

        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.CompanyId == NorthwindId);
        Assert.Contains(Northwind, report.MarkdownContent);
    }
}
