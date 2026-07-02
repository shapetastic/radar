using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class ScoringEngineTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>
    /// In-test formula stub: returns a fixed, in-range computation and echoes exactly one
    /// provenance-carrying contribution per input signal. Keeps orchestration tests decoupled from any
    /// real formula's internals — assertions are about windowing/traceability/range, never weights.
    /// </summary>
    private sealed class StubScoreFormula : IScoreFormula
    {
        public string Version => "stub-formula-vX";

        public ScoreComputation Compute(ScoringInput input)
        {
            var contributions = input.Signals
                .Select(s => new ScoreContribution(
                    SignalId: s.Signal.Id,
                    EvidenceId: s.Evidence.Id,
                    ContributionReason: $"stub:{s.Signal.Id}",
                    ContributionWeight: 5))
                .ToList();

            var components = new ScoreComponents(
                TrajectoryScore: 50,
                OpportunityScore: 50,
                AttentionScore: 50,
                EvidenceConfidenceScore: 50,
                SignalVelocityScore: 50);

            return new ScoreComputation(
                components,
                Explanation: $"stub explanation: {contributions.Count} contribution(s).",
                ComponentJson: "{\"stub\":true}",
                Contributions: contributions);
        }
    }

    /// <summary>
    /// In-test formula that records the last <see cref="ScoringInput"/> it received so windowing-input
    /// tests can assert exactly what the engine handed the formula. Returns a valid all-zero
    /// computation with no contributions (provenance is asserted elsewhere).
    /// </summary>
    private sealed class CapturingScoreFormula : IScoreFormula
    {
        public ScoringInput? LastInput { get; private set; }

        public string Version => "capturing-formula-vX";

        public ScoreComputation Compute(ScoringInput input)
        {
            LastInput = input;

            var components = new ScoreComponents(
                TrajectoryScore: 0,
                OpportunityScore: 0,
                AttentionScore: 0,
                EvidenceConfidenceScore: 0,
                SignalVelocityScore: 0);

            return new ScoreComputation(
                components,
                Explanation: "capturing formula: zero.",
                ComponentJson: "{}",
                Contributions: new List<ScoreContribution>());
        }
    }

    private sealed class Harness
    {
        public InMemorySignalRepository Signals { get; } = new();
        public InMemoryEvidenceRepository Evidence { get; } = new();
        public InMemoryScoreRepository Scores { get; } = new();
        public ScoringEngine Engine { get; }

        public Harness(IScoreFormula? formula = null)
        {
            Engine = new ScoringEngine(
                Signals,
                Evidence,
                Scores,
                formula ?? new StubScoreFormula(),
                new ScoringOptions { Window = Window },
                NullLogger<ScoringEngine>.Instance);
        }

        public async Task<(Signal signal, EvidenceItem evidence)> SeedPairAsync(
            Guid companyId,
            DateTimeOffset observedAt,
            SignalReviewStatus status = SignalReviewStatus.Approved,
            bool storeEvidence = true)
        {
            var evidence = new EvidenceBuilder()
                .WithId(Guid.NewGuid())
                .WithContentHash(Guid.NewGuid().ToString("N"))
                .Build();

            var signal = new SignalBuilder()
                .WithId(Guid.NewGuid())
                .WithEvidenceId(evidence.Id)
                .WithCompanyId(companyId)
                .WithReviewStatus(status)
                .WithObservedAtUtc(observedAt)
                .Build();

            if (storeEvidence)
            {
                await Evidence.AddIfNewAsync(evidence, CancellationToken.None);
            }

            await Signals.AddAsync(signal, CancellationToken.None);
            return (signal, evidence);
        }
    }

    [Fact]
    public async Task WindowFilter_ExcludesSignalsOutsideTheWindow()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var inside = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-5));
        // Before the window (at or before exclusive start) and after the inclusive end.
        await harness.SeedPairAsync(companyId, WindowEnd - Window); // exactly at exclusive start -> excluded
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(1)); // after end -> excluded

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(inside.signal.Id, result.Links[0].SignalId);
    }

    [Fact]
    public async Task WindowFilter_IncludesSignalAtInclusiveEnd()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var atEnd = await harness.SeedPairAsync(companyId, WindowEnd);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(atEnd.signal.Id, result.Links[0].SignalId);
    }

    [Theory]
    [InlineData(SignalReviewStatus.Pending)]
    [InlineData(SignalReviewStatus.NeedsHumanReview)]
    [InlineData(SignalReviewStatus.Rejected)]
    public async Task ReviewFilter_ExcludesNonApprovedSignals(SignalReviewStatus status)
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var approved = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-2));
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-2), status);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(approved.signal.Id, result.Links[0].SignalId);
    }

    [Fact]
    public async Task MissingEvidence_IsExcluded_AndEngineSucceeds()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var withEvidence = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));
        var missing = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3), storeEvidence: false);

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Single(result.Links);
        Assert.Equal(withEvidence.signal.Id, result.Links[0].SignalId);
        Assert.DoesNotContain(result.Links, l => l.SignalId == missing.signal.Id);
    }

    [Fact]
    public async Task Traceability_OneLinkPerContribution_WithMatchingProvenance()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();

        var a = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-10));
        var b = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-5));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(2, result.Links.Count);
        Assert.All(result.Links, l => Assert.Equal(result.Snapshot.Id, l.ScoreSnapshotId));

        var seeded = new[]
        {
            (a.signal.Id, a.evidence.Id),
            (b.signal.Id, b.evidence.Id),
        };
        foreach (var link in result.Links)
        {
            Assert.Contains((link.SignalId, link.EvidenceId), seeded);
        }
    }

    [Fact]
    public async Task ComponentScores_AreWithinInclusiveRange()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var s = result.Snapshot;

        foreach (var score in new[]
        {
            s.TrajectoryScore, s.OpportunityScore, s.AttentionScore,
            s.EvidenceConfidenceScore, s.SignalVelocityScore,
        })
        {
            Assert.InRange(score, 0, 100);
        }
    }

    [Fact]
    public async Task Versioning_RecordsBothEngineAndFormulaVersions()
    {
        var formula = new StubScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Contains("mvp-engine-v1", result.Snapshot.ScoringVersion);
        Assert.Contains(formula.Version, result.Snapshot.ScoringVersion);
    }

    [Fact]
    public async Task Versioning_StampsScoringConfigVersion()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Every new snapshot is stamped with the current scoring-generation constant (non-null), so the
        // report can gate cross-run comparability on it.
        Assert.Equal("radar-scoring-config-v2", result.Snapshot.ScoringConfigVersion);
    }

    [Fact]
    public async Task Versioning_ScoringConfigVersion_IsNonNullAndNonEmpty()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        // Guard the presence of the stamp, independent of its exact value: Versioning_StampsScoringConfigVersion
        // asserts the current version string (and is intentionally updated on each AD-10 bump), while this test
        // stays decoupled from that value so it survives bumps and only fails if a freshly-produced snapshot ever
        // silently regresses to null — which would disable the report's cross-run comparability gate (spec 69).
        Assert.False(string.IsNullOrEmpty(result.Snapshot.ScoringConfigVersion));
    }

    [Fact]
    public async Task WindowAndTimestamps_CreatedAtEqualsWindowEnd()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(WindowEnd - Window, result.Snapshot.WindowStartUtc);
        Assert.Equal(WindowEnd, result.Snapshot.WindowEndUtc);
        // CreatedAtUtc must track the run instant (windowEndUtc), NOT a separate clock read — so a
        // freshly-created snapshot is included by the report's inclusive (start, end] window (spec 49).
        Assert.Equal(WindowEnd, result.Snapshot.CreatedAtUtc);
    }

    [Fact]
    public async Task Persistence_SnapshotAndLinksAreRetrievable()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-2));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var snapshots = await harness.Scores.GetSnapshotsForCompanyAsync(companyId, CancellationToken.None);
        Assert.Contains(snapshots, s => s.Id == result.Snapshot.Id);

        var links = await harness.Scores.GetLinksForSnapshotAsync(result.Snapshot.Id, CancellationToken.None);
        Assert.Equal(result.Links.Count, links.Count);
        Assert.All(links, l => Assert.Equal(result.Snapshot.Id, l.ScoreSnapshotId));
    }

    [Fact]
    public async Task EmptyWindow_ProducesValidSnapshotWithZeroLinks()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        // No qualifying signals seeded.

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Empty(result.Links);
        Assert.InRange(result.Snapshot.TrajectoryScore, 0, 100);

        var snapshots = await harness.Scores.GetSnapshotsForCompanyAsync(companyId, CancellationToken.None);
        Assert.Contains(snapshots, s => s.Id == result.Snapshot.Id);
    }

    [Fact]
    public async Task Reproducibility_SameStateAndClock_YieldsEquivalentScores()
    {
        var harness = new Harness();
        var companyId = Guid.NewGuid();
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-7));
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));

        var first = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);
        var second = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.Equal(first.Snapshot.TrajectoryScore, second.Snapshot.TrajectoryScore);
        Assert.Equal(first.Snapshot.OpportunityScore, second.Snapshot.OpportunityScore);
        Assert.Equal(first.Snapshot.AttentionScore, second.Snapshot.AttentionScore);
        Assert.Equal(first.Snapshot.EvidenceConfidenceScore, second.Snapshot.EvidenceConfidenceScore);
        Assert.Equal(first.Snapshot.SignalVelocityScore, second.Snapshot.SignalVelocityScore);
        Assert.Equal(first.Snapshot.ComponentJson, second.Snapshot.ComponentJson);
        Assert.Equal(first.Snapshot.ScoringVersion, second.Snapshot.ScoringVersion);

        // Equal set of contribution tuples, ignoring freshly-generated snapshot/link Ids.
        static HashSet<(Guid, Guid, int, string)> Tuples(CompanyScoreResult r) =>
            r.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionWeight, l.ContributionReason)).ToHashSet();

        Assert.True(Tuples(first).SetEquals(Tuples(second)));
    }

    [Fact]
    public async Task DiWiring_ResolvesEngine_AndScoresWithRealFormula()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();

        using var provider = services.BuildServiceProvider();

        var signals = provider.GetRequiredService<ISignalRepository>();
        var evidence = provider.GetRequiredService<IEvidenceRepository>();
        var engine = provider.GetRequiredService<IScoringEngine>();

        var companyId = Guid.NewGuid();
        var ev = new EvidenceBuilder().WithId(Guid.NewGuid()).WithContentHash("wiring-hash").Build();
        var sig = new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(ev.Id)
            .WithCompanyId(companyId)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-1))
            .Build();

        await evidence.AddIfNewAsync(ev, CancellationToken.None);
        await signals.AddAsync(sig, CancellationToken.None);

        var result = await engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        Assert.InRange(result.Snapshot.TrajectoryScore, 0, 100);
        Assert.Contains("mvp-engine-v1", result.Snapshot.ScoringVersion);
    }

    [Fact]
    public async Task PreviousWindow_IsSlicedAndPassed_SeparateFromCurrentAndOlder()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        // Current window (WindowStart, WindowEnd].
        var windowStart = WindowEnd - Window;
        var current = await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-3));
        // Previous window (WindowStart - Window, WindowStart].
        var previous = await harness.SeedPairAsync(companyId, windowStart.AddDays(-3));
        // Older than the previous window -> excluded from both.
        await harness.SeedPairAsync(companyId, windowStart - Window - TimeSpan.FromDays(1));

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.Equal(new[] { current.signal.Id }, input.Signals.Select(s => s.Signal.Id).ToArray());
        Assert.Equal(new[] { previous.signal.Id }, input.PreviousSignals.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task PreviousWindow_BoundaryAtWindowStart_BelongsToPrevious()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        var windowStart = WindowEnd - Window;
        var atStart = await harness.SeedPairAsync(companyId, windowStart);

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.DoesNotContain(input.Signals, s => s.Signal.Id == atStart.signal.Id);
        Assert.Contains(input.PreviousSignals, s => s.Id == atStart.signal.Id);
    }

    [Fact]
    public async Task PreviousWindow_ReviewFilter_ExcludesNonApproved()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        var windowStart = WindowEnd - Window;
        var approved = await harness.SeedPairAsync(companyId, windowStart.AddDays(-5));
        var pending = await harness.SeedPairAsync(
            companyId, windowStart.AddDays(-5), SignalReviewStatus.Pending);

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.Contains(input.PreviousSignals, s => s.Id == approved.signal.Id);
        Assert.DoesNotContain(input.PreviousSignals, s => s.Id == pending.signal.Id);
    }

    [Fact]
    public async Task PreviousWindow_DoesNotRequireEvidence_ButCurrentStillDoes()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        var windowStart = WindowEnd - Window;
        // Previous-window signal with missing evidence -> still carried.
        var previousNoEvidence = await harness.SeedPairAsync(
            companyId, windowStart.AddDays(-2), storeEvidence: false);
        // Current-window signal with missing evidence -> still dropped.
        var currentNoEvidence = await harness.SeedPairAsync(
            companyId, WindowEnd.AddDays(-2), storeEvidence: false);

        await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.Contains(input.PreviousSignals, s => s.Id == previousNoEvidence.signal.Id);
        Assert.DoesNotContain(input.Signals, s => s.Signal.Id == currentNoEvidence.signal.Id);
    }

    [Fact]
    public async Task PreviousWindow_Empty_WhenNoSignalsBeforeWindowStart()
    {
        var formula = new CapturingScoreFormula();
        var harness = new Harness(formula);
        var companyId = Guid.NewGuid();

        // Only a current-window signal; nothing at or before windowStart.
        await harness.SeedPairAsync(companyId, WindowEnd.AddDays(-1));

        var result = await harness.Engine.ScoreCompanyAsync(companyId, WindowEnd, CancellationToken.None);

        var input = Assert.IsType<ScoringInput>(formula.LastInput);

        Assert.NotNull(input.PreviousSignals);
        Assert.Empty(input.PreviousSignals);
        Assert.InRange(result.Snapshot.TrajectoryScore, 0, 100);
    }
}
