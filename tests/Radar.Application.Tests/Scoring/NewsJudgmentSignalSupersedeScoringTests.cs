using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
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
/// SPEC 194 §1.3, end to end through the real <see cref="ScoringEngine"/> — the CONSTRUCTED pre/post pin.
/// <para>
/// One company, one news article, one evidence id. The PRE set is what the store holds today: the ordinary
/// Neutral <c>MediaAttention</c> signal the extractor wrote. The POST set adds the spec-194 §1.2
/// judgment-derived signal over that SAME evidence — the grounded read of the very article the judgment
/// cited. Every fixture here is constructed in this file; nothing is copied from the live store, so the pin
/// cannot go green by inheriting whatever the data happens to say this week.
/// </para>
/// <para>
/// The two claims being pinned are the ones the section exists for:
/// <list type="number">
///   <item>the grounded signal REPLACES the ordinary one — in the CURRENT window and in the
///     PREVIOUS/velocity window — so activity does not grow merely because judgment was added; and</item>
///   <item>removing the materialized signal restores the ordinary Neutral result exactly, and adding it
///     changes ONLY the trajectory/media contribution and the linked provenance.</item>
/// </list>
/// </para>
/// <para>
/// <b>MUTATION PROOF.</b> Delete the two <c>NewsJudgmentSignalSupersede.Apply</c> calls from
/// <c>ScoringEngine</c> and <see cref="MaterializedSignal_ReplacesTheOrdinarySignal_InBothWindows"/>,
/// <see cref="MaterializedSignal_NamesTheSupersedeOnTheContributionReason"/> and
/// <see cref="MaterializedSignal_SurfacesTheSupersedeInTheAggregatedLog"/> turn red: the article
/// double-counts as two attention events over one evidence id.
/// </para>
/// </summary>
public sealed class NewsJudgmentSignalSupersedeScoringTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution order (observed instant, then signal id) is stable across
    // machines (AD-3).
    private static readonly Guid CompanyId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ArticleEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OrdinarySignalId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MaterializedSignalId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid JudgmentId = new("9c8f7e6d-3333-4c33-9333-cccccccccccc");

    /// <summary>The exact note the engine appends; asserted by value so the provenance text cannot drift.</summary>
    private const string SupersedeNote =
        " (superseded 1 ordinary media attention signal(s) for this evidence: the judgment-derived direction "
        + "replaces the attention event)";

    // ---------------------------------------------------------------------------------------------
    // Claim 1: one attention event in, one out — in BOTH windows.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MaterializedSignal_ReplacesTheOrdinarySignal_InBothWindows()
    {
        var ordinaryOnly = await ScoreAsync(withMaterializedSignal: false);
        var withJudgment = await ScoreAsync(withMaterializedSignal: true);

        // CURRENT window: exactly one contribution over the article evidence, before and after. The scored
        // signal count is the number the engine logs, and it is IDENTICAL — the article contributes one
        // attention event whether or not Radar formed a judgment about it.
        Assert.Equal(1, ScoredSignalCount(ordinaryOnly.Logger));
        Assert.Equal(1, ScoredSignalCount(withJudgment.Logger));

        var before = Assert.Single(ordinaryOnly.Result.Links);
        var after = Assert.Single(withJudgment.Result.Links);
        Assert.Equal(ArticleEvidenceId, before.EvidenceId);
        Assert.Equal(ArticleEvidenceId, after.EvidenceId);

        // The GROUNDED signal is the one that carries the contribution; the ordinary one is gone from the
        // score (and still on disk, untouched — this transform writes nothing).
        Assert.Equal(OrdinarySignalId, before.SignalId);
        Assert.Equal(MaterializedSignalId, after.SignalId);

        // PREVIOUS/velocity window: the previous window carries BOTH signals over the same evidence too, so
        // if the supersede did not run there the company would read as decelerating purely because a
        // judgment existed in the earlier window. SignalVelocity is identical across the two runs, which is
        // exactly the "activity does not grow" claim measured where velocity can see it.
        Assert.Equal(
            ordinaryOnly.Result.Snapshot.SignalVelocityScore,
            withJudgment.Result.Snapshot.SignalVelocityScore);
    }

    [Fact]
    public async Task MaterializedSignal_ChangesOnlyTheTrajectoryContributionAndItsProvenance()
    {
        // The materialized signal carries the SAME strength/novelty/confidence the ordinary article event
        // does (spec 194 §1.2: base strength 4, novelty 4, confidence 0.5 — what a supportive read with zero
        // challenge findings legitimately produces), so the ONLY difference between the two runs is the
        // direction and the provenance envelope. Any component that moves beyond trajectory would therefore
        // be the supersede miscounting activity.
        var ordinaryOnly = await ScoreAsync(withMaterializedSignal: false);
        var withJudgment = await ScoreAsync(withMaterializedSignal: true);

        var before = ordinaryOnly.Result.Snapshot;
        var after = withJudgment.Result.Snapshot;

        Assert.Equal(before.AttentionScore, after.AttentionScore);
        Assert.Equal(before.EvidenceConfidenceScore, after.EvidenceConfidenceScore);
        Assert.Equal(before.SignalVelocityScore, after.SignalVelocityScore);

        // The trajectory DOES move: a Neutral attention event carries no directional mass, a grounded
        // Negative read does. That is the whole point of the correction — and it arrives without the
        // article being counted twice.
        Assert.NotEqual(before.TrajectoryScore, after.TrajectoryScore);
        Assert.True(
            after.TrajectoryScore < before.TrajectoryScore,
            "a grounded Deteriorating read must lower trajectory, not raise it");
    }

    // ---------------------------------------------------------------------------------------------
    // Claim 2: the removal is stated, never silent.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MaterializedSignal_NamesTheSupersedeOnTheContributionReason()
    {
        var withJudgment = await ScoreAsync(withMaterializedSignal: true);

        var link = Assert.Single(withJudgment.Result.Links);
        Assert.EndsWith(SupersedeNote, link.ContributionReason, StringComparison.Ordinal);

        // The note is APPENDED to the formula's own reason, never a replacement for it: the persisted link
        // still says what the signal was before it says what it replaced.
        Assert.StartsWith("MediaAttention (Negative)", link.ContributionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryOnly_CarriesNoSupersedeNote_AndNoSupersedeLogLine()
    {
        var ordinaryOnly = await ScoreAsync(withMaterializedSignal: false);

        var link = Assert.Single(ordinaryOnly.Result.Links);
        Assert.DoesNotContain("superseded", link.ContributionReason, StringComparison.OrdinalIgnoreCase);

        // A run with nothing to supersede logs nothing new at all — the healthy path's log is byte-identical
        // to the pre-194 one.
        Assert.DoesNotContain(
            ordinaryOnly.Logger.Entries,
            e => e.Message.Contains("ordinary media attention signal(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaterializedSignal_SurfacesTheSupersedeInTheAggregatedLog()
    {
        var withJudgment = await ScoreAsync(withMaterializedSignal: true);

        // ONE aggregated line per company (the spec-145 precedent), naming BOTH windows — the current
        // window's removal is the one the evidence link reflects, the previous window's is activity-only.
        var line = Assert.Single(
            withJudgment.Logger.Entries,
            e => e.Message.Contains("ordinary media attention signal(s)", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("Superseded 1 ordinary media attention signal(s)", line.Message, StringComparison.Ordinal);
        Assert.Contains("and 1 in the previous/velocity window", line.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // The fixture.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One company, one article, one evidence id. The current window always holds the ordinary Neutral
    /// article signal; <paramref name="withMaterializedSignal"/> adds the §1.2 judgment-derived companion
    /// over the SAME evidence. The previous/velocity window is fed the SAME set through the signal file
    /// store, so both windows exercise the supersede.
    /// </summary>
    private static async Task<(CompanyScoreResult Result, CapturingLogger Logger)> ScoreAsync(
        bool withMaterializedSignal)
    {
        var signals = new InMemorySignalRepository();
        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();
        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        var logger = new CapturingLogger();

        var currentWindow = new List<Signal> { OrdinarySignal(WindowEnd.AddDays(-3)) };
        if (withMaterializedSignal)
        {
            currentWindow.Add(MaterializedSignal(WindowEnd.AddDays(-3)));
        }

        // The previous window mirrors the current one, one full window earlier (the engine reads it with the
        // default 30-day window, so day -40 sits inside (windowEnd-60, windowEnd-30]).
        var previousWindow = new List<Signal> { OrdinarySignal(WindowEnd.AddDays(-40)) with { Id = Guid.NewGuid() } };
        if (withMaterializedSignal)
        {
            previousWindow.Add(MaterializedSignal(WindowEnd.AddDays(-40)) with { Id = Guid.NewGuid() });
        }

        var engine = new ScoringEngine(
            signals,
            new StubSignalFileStore(previousWindow),
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

        await evidence.AddIfNewAsync(
            new EvidenceBuilder()
                .WithId(ArticleEvidenceId)
                .WithContentHash(ArticleEvidenceId.ToString("N"))
                .WithSourceType(EvidenceSourceType.NewsArticle)
                .WithSourceName("Example Wire")
                .WithQuality(EvidenceQuality.Medium)
                .WithPublishedAtUtc(WindowEnd.AddDays(-3))
                .WithCollectedAtUtc(WindowEnd.AddDays(-3))
                .Build(),
            CancellationToken.None);

        foreach (var signal in currentWindow)
        {
            await signals.AddAsync(signal, CancellationToken.None);
        }

        var result = await engine.ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);
        return (result, logger);
    }

    /// <summary>Exactly what the pre-191 (and post-§1.1) extractor writes for a news article.</summary>
    private static Signal OrdinarySignal(DateTimeOffset observedAt) =>
        NewsSignalBuilder(OrdinarySignalId, observedAt)
            .WithDirection(SignalDirection.Neutral)
            .WithMetadataJson(null)
            .Build();

    /// <summary>
    /// The spec-194 §1.2 signal, with its envelope composed through the SHARED writer the materializer
    /// itself uses — so this fixture cannot drift from what the producer actually persists.
    /// </summary>
    private static Signal MaterializedSignal(DateTimeOffset observedAt) =>
        NewsSignalBuilder(MaterializedSignalId, observedAt)
            .WithDirection(SignalDirection.Negative)
            .WithMetadataJson(NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
                JudgmentId,
                "deepseek|p2|s2|stage1|families",
                "Deteriorating",
                [new Guid("aaaaaaaa-0000-4000-8000-000000000001")],
                [new Guid("bbbbbbbb-0000-4000-8000-000000000001")],
                [ArticleEvidenceId]))
            .Build();

    private static SignalBuilder NewsSignalBuilder(Guid id, DateTimeOffset observedAt) =>
        new SignalBuilder()
            .WithId(id)
            .WithEvidenceId(ArticleEvidenceId)
            .WithCompanyId(CompanyId)
            .WithType(SignalType.MediaAttention)
            // Identical magnitudes on both signals: the ONLY difference the pin admits is direction and
            // provenance (§1.2 base strength 4 / novelty 4 / confidence 0.5).
            .WithStrength(4)
            .WithNovelty(4)
            .WithConfidence(0.5m)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAt)
            .WithCreatedAtUtc(observedAt);

    private static int ScoredSignalCount(CapturingLogger logger)
    {
        var line = logger.Entries.Single(e => e.Message.StartsWith("Scored company", StringComparison.Ordinal));
        var from = line.Message.IndexOf("from ", StringComparison.Ordinal) + "from ".Length;
        var to = line.Message.IndexOf(" signal(s)", from, StringComparison.Ordinal);
        return int.Parse(line.Message[from..to], System.Globalization.CultureInfo.InvariantCulture);
    }

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

    /// <summary>Serves a fixed previous/velocity window, so the supersede is exercised on both windows.</summary>
    private sealed class StubSignalFileStore(IReadOnlyList<Signal> previousWindow) : ISignalFileStore
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
            Task.FromResult<IReadOnlyList<Signal>>(
                [.. previousWindow.Where(
                    s => s.ObservedAtUtc > startExclusiveUtc && s.ObservedAtUtc <= endInclusiveUtc)]);
    }

    /// <summary>A fixed identity/provenance descriptor: neither is a scoring input.</summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v8;";

        public string CollectionProvenance() => "collectors=newssearch;";

        public IReadOnlyList<string> EnabledCollectors() => ["newssearch"];
    }
}
