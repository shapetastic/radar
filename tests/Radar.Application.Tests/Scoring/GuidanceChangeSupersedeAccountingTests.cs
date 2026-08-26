using Microsoft.Extensions.Logging;

using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 193 §2 — <c>GuidanceChangeSupersede</c> must account for what it removes, and <b>which signals are
/// removed must not change</b>.
/// <para>
/// The supersede was the only signal-removal step in <c>ScoringEngine.ScoreCompanyAsync</c> with no trace,
/// sitting between two that do account (the dropped-evidence Warning above it and
/// <c>MediaAttentionCollapse</c>'s collapsed count below it). Spec 173 measured that 4 of the top 10
/// companies by Opportunity rest on a results-only <c>GuidanceChange</c>, so silently superseding those
/// removed exactly the signal type the ranking is most sensitive to.
/// </para>
/// <para>
/// THE PINS BELOW WERE CAPTURED FROM THE PRE-193 SOURCES, before any production file in this slice was
/// touched, by running this fixture through the unmodified <c>ScoringEngine</c>/<c>GuidanceChangeSupersede</c>
/// and recording what came out. They are therefore a genuine before/after: if a number here moves, the
/// accounting changed the filter, which this slice forbids. (Same posture as spec 148's
/// <see cref="ScoringOutputStabilityTests"/> and spec 153's
/// <see cref="RadarScoreFormulaV9OutputStabilityTests"/>.)
/// </para>
/// </summary>
public sealed class GuidanceChangeSupersedeAccountingTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution ORDER (observed instant, then signal id) is stable across
    // runs and machines — otherwise the pinned link chain below would be a coin toss (AD-3).
    private static readonly Guid CompanyId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FilingEvidenceId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NeutralGuidanceSignalId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DirectionalGuidanceSignalId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PressSignalId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid PressEvidenceId = new("66666666-6666-6666-6666-666666666666");

    // ---------------------------------------------------------------------------------------------
    // The unit-level accounting contract.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_ChargesEachRemovedSignalToTheSurvivorForItsOwnEvidence()
    {
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);

        var result = GuidanceChangeSupersede.Apply(new[] { neutral, positive });

        // The filter is unchanged: one survivor, and it is the directional one.
        var survivor = Assert.Single(result.Signals);
        Assert.Equal(positive.Id, survivor.Id);

        // The accounting: charged to the survivor of that evidence id (the signal that took its place),
        // never to the removed signal, which contributes no link at all.
        var charge = Assert.Single(result.SupersededCounts);
        Assert.Equal(positive.Id, charge.Key);
        Assert.Equal(1, charge.Value);
        Assert.Equal(1, result.TotalSuperseded);
    }

    [Fact]
    public void Apply_MultipleStaleNeutrals_AllChargeTheOneSurvivor()
    {
        var evidenceId = Guid.NewGuid();
        var neutralA = Guidance(evidenceId, SignalDirection.Neutral);
        var neutralB = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);

        var result = GuidanceChangeSupersede.Apply(new[] { neutralA, positive, neutralB });

        Assert.Single(result.Signals);
        Assert.Equal(2, result.SupersededCounts[positive.Id]);
        Assert.Equal(2, result.TotalSuperseded);
    }

    [Fact]
    public void Apply_TwoEvidenceIds_AreAccountedSeparately()
    {
        var evidenceA = Guid.NewGuid();
        var evidenceB = Guid.NewGuid();
        var neutralA = Guidance(evidenceA, SignalDirection.Neutral);
        var positiveA = Guidance(evidenceA, SignalDirection.Positive);
        var neutralB = Guidance(evidenceB, SignalDirection.Neutral);
        var positiveB = Guidance(evidenceB, SignalDirection.Positive);

        var result = GuidanceChangeSupersede.Apply(
            new[] { neutralA, positiveA, neutralB, positiveB });

        Assert.Equal(2, result.SupersededCounts.Count);
        Assert.Equal(1, result.SupersededCounts[positiveA.Id]);
        Assert.Equal(1, result.SupersededCounts[positiveB.Id]);
        Assert.Equal(2, result.TotalSuperseded);
    }

    [Fact]
    public void Apply_TheHealthyFastPath_ReturnsTheInputInstance_AndCountsNothing()
    {
        var input = new[]
        {
            Guidance(Guid.NewGuid(), SignalDirection.Positive),
            new SignalBuilder().WithType(SignalType.CustomerWin).Build(),
        };

        var result = GuidanceChangeSupersede.Apply(input);

        // Still allocation-free on the healthy spec-78 path: the INPUT INSTANCE comes back.
        Assert.Same(input, result.Signals);
        Assert.Empty(result.SupersededCounts);
        Assert.Equal(0, result.TotalSuperseded);
    }

    [Fact]
    public void Apply_TheCountIsOrderIndependent()
    {
        // AD-3: the survivor is chosen order-independently, so the charge is too.
        var evidenceId = Guid.NewGuid();
        var neutral = Guidance(evidenceId, SignalDirection.Neutral);
        var positive = Guidance(evidenceId, SignalDirection.Positive);
        var other = new SignalBuilder().WithType(SignalType.CustomerWin).Build();

        var forward = GuidanceChangeSupersede.Apply(new[] { neutral, positive, other });
        var reversed = GuidanceChangeSupersede.Apply(new[] { other, positive, neutral });

        Assert.Equal(forward.SupersededCounts[positive.Id], reversed.SupersededCounts[positive.Id]);
        Assert.Equal(forward.TotalSuperseded, reversed.TotalSuperseded);
    }

    // ---------------------------------------------------------------------------------------------
    // The engine-level pin: the accounting changed NOTHING about the score.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealSupersede_ScoresByteIdenticallyToThePre193Behaviour()
    {
        var (result, logger) = await ScoreAsync(includeStaleNeutral: true);
        var snapshot = result.Snapshot;

        // ---- the five components, captured from the PRE-193 sources ---------------------------------
        Assert.Equal(57, snapshot.TrajectoryScore);
        Assert.Equal(41, snapshot.OpportunityScore);
        Assert.Equal(0, snapshot.AttentionScore);
        Assert.Equal(72, snapshot.EvidenceConfidenceScore);
        Assert.Equal(100, snapshot.SignalVelocityScore);

        // ---- the explanation ------------------------------------------------------------------------
        Assert.Equal(
            "radar-formula-v8: 2 signal(s) over 30d → Trajectory 57, Opportunity 41 "
                + "(Attention 0, Confidence 72, Velocity 100).",
            snapshot.Explanation);
        Assert.Equal(
            "{\"TrajectoryScore\":57,\"OpportunityScore\":41,\"AttentionScore\":0,"
                + "\"EvidenceConfidenceScore\":72,\"SignalVelocityScore\":100}",
            snapshot.ComponentJson);

        // ---- the ORDERED evidence-link chain --------------------------------------------------------
        // The stale Neutral is still removed (it has no link), the directional copy still scores, the
        // order and the weights are untouched.
        Assert.Equal(
            [
                (PressSignalId, PressEvidenceId, -3),
                (DirectionalGuidanceSignalId, FilingEvidenceId, 6),
            ],
            result.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionWeight)).ToArray());
        Assert.DoesNotContain(result.Links, l => l.SignalId == NeutralGuidanceSignalId);

        // ---- and the ONLY thing that changed: the removal is now traceable ---------------------------
        var guidanceLink = Assert.Single(result.Links, l => l.SignalId == DirectionalGuidanceSignalId);
        Assert.Equal(
            "GuidanceChange (Positive), strength 8, confidence 0.80 "
                + "(superseded 1 stale GuidanceChange signal(s) for this evidence)",
            guidanceLink.ContributionReason);

        // One aggregated per-company Information line, at the level the scoring log already uses.
        var accounting = Assert.Single(
            logger.Entries,
            e => e.Message.StartsWith("Superseded ", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, accounting.Level);
        Assert.Contains("Superseded 1 stale GuidanceChange signal(s)", accounting.Message);
    }

    [Fact]
    public async Task NoSupersede_EmitsNoCount_AndIsByteUnchanged()
    {
        // The same fixture WITHOUT the stale Neutral. Every number is identical to the supersede case
        // above — which is the point of the supersede — and no count appears anywhere.
        var (result, logger) = await ScoreAsync(includeStaleNeutral: false);
        var snapshot = result.Snapshot;

        Assert.Equal(57, snapshot.TrajectoryScore);
        Assert.Equal(41, snapshot.OpportunityScore);
        Assert.Equal(0, snapshot.AttentionScore);
        Assert.Equal(72, snapshot.EvidenceConfidenceScore);
        Assert.Equal(100, snapshot.SignalVelocityScore);
        Assert.Equal(
            "radar-formula-v8: 2 signal(s) over 30d → Trajectory 57, Opportunity 41 "
                + "(Attention 0, Confidence 72, Velocity 100).",
            snapshot.Explanation);

        Assert.Equal(
            [
                (PressSignalId, PressEvidenceId, -3),
                (DirectionalGuidanceSignalId, FilingEvidenceId, 6),
            ],
            result.Links.Select(l => (l.SignalId, l.EvidenceId, l.ContributionWeight)).ToArray());

        // No "(superseded N …)" suffix on any reason, and no accounting line.
        Assert.All(result.Links, l => Assert.DoesNotContain("superseded", l.ContributionReason));
        Assert.DoesNotContain(
            logger.Entries, e => e.Message.StartsWith("Superseded ", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------

    private static Signal Guidance(Guid evidenceId, SignalDirection direction) =>
        new SignalBuilder()
            .WithId(Guid.NewGuid())
            .WithEvidenceId(evidenceId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(direction)
            .WithObservedAtUtc(WindowEnd.AddDays(-3))
            .Build();

    /// <summary>
    /// The fixture: a filing carrying a directional <c>GuidanceChange</c> (and, optionally, the stale
    /// Neutral copy the spec-113 supersede exists to remove) plus an unrelated negative press release, so
    /// the corroboration-smoothed Trajectory is exercised rather than saturated.
    /// </summary>
    private static async Task<(CompanyScoreResult Result, CapturingLogger Logger)> ScoreAsync(
        bool includeStaleNeutral)
    {
        var signals = new InMemorySignalRepository();
        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();
        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var logger = new CapturingLogger();

        var engine = new ScoringEngine(
            signals,
            new NullSignalFileStore(),
            evidence,
            new InMemoryScoreRepository(),
            companies,
            new RadarScoreFormulaV8(weights, attention),
            weights,
            attention,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            new ScoringOptions(),
            logger);

        await companies.AddAsync(new CompanyBuilder().WithId(CompanyId).Build(), CancellationToken.None);

        var filing = new EvidenceBuilder()
            .WithId(FilingEvidenceId)
            .WithContentHash(FilingEvidenceId.ToString("N"))
            .WithSourceType(EvidenceSourceType.Filing)
            .WithSourceName("SEC EDGAR")
            .WithQuality(EvidenceQuality.PrimarySource)
            .WithPublishedAtUtc(WindowEnd.AddDays(-3))
            .WithCollectedAtUtc(WindowEnd.AddDays(-3))
            .Build();
        await evidence.AddIfNewAsync(filing, CancellationToken.None);

        if (includeStaleNeutral)
        {
            await signals.AddAsync(
                GuidanceOn(NeutralGuidanceSignalId, SignalDirection.Neutral, strength: 5),
                CancellationToken.None);
        }

        await signals.AddAsync(
            GuidanceOn(DirectionalGuidanceSignalId, SignalDirection.Positive, strength: 8),
            CancellationToken.None);

        var press = new EvidenceBuilder()
            .WithId(PressEvidenceId)
            .WithContentHash(PressEvidenceId.ToString("N"))
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithSourceName("Acme Newsroom")
            .WithQuality(EvidenceQuality.High)
            .WithPublishedAtUtc(WindowEnd.AddDays(-10))
            .WithCollectedAtUtc(WindowEnd.AddDays(-10))
            .Build();
        await evidence.AddIfNewAsync(press, CancellationToken.None);
        await signals.AddAsync(
            new SignalBuilder()
                .WithId(PressSignalId)
                .WithEvidenceId(PressEvidenceId)
                .WithCompanyId(CompanyId)
                .WithType(SignalType.CustomerWin)
                .WithDirection(SignalDirection.Negative)
                .WithStrength(5)
                .WithReviewStatus(SignalReviewStatus.Approved)
                .WithObservedAtUtc(WindowEnd.AddDays(-10))
                .WithCreatedAtUtc(WindowEnd.AddDays(-10))
                .Build(),
            CancellationToken.None);

        var result = await engine.ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);
        return (result, logger);
    }

    private static Signal GuidanceOn(Guid signalId, SignalDirection direction, int strength) =>
        new SignalBuilder()
            .WithId(signalId)
            .WithEvidenceId(FilingEvidenceId)
            .WithCompanyId(CompanyId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(WindowEnd.AddDays(-3))
            .WithCreatedAtUtc(WindowEnd.AddDays(-3))
            .Build();

    private sealed class CapturingLogger : ILogger<ScoringEngine>
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

    /// <summary>The previous/velocity window is deliberately empty: this fixture pins the CURRENT window.</summary>
    private sealed class NullSignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("written/signal.json"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    /// <summary>A fixed identity/provenance descriptor: neither is a scoring input.</summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v6;";

        public string CollectionProvenance() => "collectors=sec-edgar;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec-edgar"];
    }
}
