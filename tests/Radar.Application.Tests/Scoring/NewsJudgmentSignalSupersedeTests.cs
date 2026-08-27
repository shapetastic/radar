using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 194 §1.3 — the judgment-derived news signal REPLACES the ordinary attention event over the article
/// it was grounded in, so forming a judgment never inflates a company's news activity.
/// <para>
/// The gap: §1.2 materializes one grounded <c>MediaAttention</c> signal anchored to the evidence a judgment
/// actually cited, but that article's ordinary Neutral signal is already on disk. Without this transform the
/// cited article contributes TWO attention signals over ONE evidence id — the volume inflation in miniature
/// that the whole correction exists to remove.
/// </para>
/// <para>
/// <b>MUTATION PROOFS.</b> Delete the <c>NewsJudgmentSignalSupersede.Apply</c> calls from
/// <c>ScoringEngine</c> and <see cref="NewsJudgmentSignalSupersedeScoringTests"/>' activity-count and
/// contribution-reason tests turn red. Widen the winner map to include ordinary media signals (i.e. drop the
/// <c>IsJudgmentDerived</c> gate in pass 1) and
/// <see cref="NoMaterializedSignal_RemovesNothing_AndReturnsTheInputInstance"/> plus
/// <see cref="OrdinaryAndLegacySignals_OverOneEvidence_AreAllUntouchedWithoutAWinner"/> turn red. Flip the
/// <c>Beats</c> tie-break to earliest <c>CreatedAtUtc</c> and
/// <see cref="TwoMaterializedSignals_LatestCreatedWins"/> turns red.
/// </para>
/// </summary>
public sealed class NewsJudgmentSignalSupersedeTests
{
    private static readonly DateTimeOffset Base = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so every ordering and tie-break assertion is stable across machines (AD-3).
    private static readonly Guid ArticleEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherEvidenceId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid JudgmentId = new("9c8f7e6d-3333-4c33-9333-cccccccccccc");

    // ---------------------------------------------------------------------------------------------
    // The untouched fast path — the shape of every company that has no materialized judgment signal.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NoMaterializedSignal_RemovesNothing_AndReturnsTheInputInstance()
    {
        // Two ordinary Neutral article signals over the SAME evidence, which is exactly the shape that must
        // NOT be touched: de-noising ordinary news volume is the same-event media collapse's job, and
        // widening this supersede into a second collapse would drop attention events no judgment replaced.
        IReadOnlyList<Signal> input =
        [
            OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId),
            OrdinaryNewsSignal(SignalId(0xA2), ArticleEvidenceId),
        ];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        // Reference equality: the fast path allocates no list at all.
        Assert.Same(input, result.Signals);
        Assert.Empty(result.SupersededCounts);
        Assert.Equal(0, result.TotalSuperseded);
    }

    [Fact]
    public void NoMaterializedSignal_ScoringSignalOverload_AlsoReturnsTheInputInstance()
    {
        IReadOnlyList<ScoringSignal> input =
        [
            Pair(OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId)),
            Pair(NonNewsSignal(SignalId(0xA3), OtherEvidenceId)),
        ];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Empty(result.SupersededCounts);
    }

    [Fact]
    public void OrdinaryAndLegacySignals_OverOneEvidence_AreAllUntouchedWithoutAWinner()
    {
        // An accrued spec-191 directional signal (already neutralized by §1.4 upstream) beside the ordinary
        // one. Neither is a valid news-judgment-signal-v1 record, so nothing here may be removed.
        IReadOnlyList<Signal> input =
        [
            OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId),
            NewsSignal(SignalId(0xA2), ArticleEvidenceId, SignalDirection.Neutral, LegacyMetadata()),
        ];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Equal(0, result.TotalSuperseded);
    }

    // ---------------------------------------------------------------------------------------------
    // The supersede itself.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void MaterializedSignal_SupersedesTheOrdinaryArticleSignal_OverTheSameEvidence()
    {
        var materialized = MaterializedNewsSignal(SignalId(0xB1), ArticleEvidenceId, SignalDirection.Positive);
        IReadOnlyList<Signal> input =
        [
            OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId),
            materialized,
        ];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        // ONE attention event in, ONE out: the grounded signal replaces the ordinary one rather than
        // joining it.
        var survivor = Assert.Single(result.Signals);
        Assert.Equal(materialized.Id, survivor.Id);
        Assert.Equal(SignalDirection.Positive, survivor.Direction);

        // Charged to the SURVIVOR, which is what lets the engine name the replacement on the persisted
        // ScoreEvidenceLink instead of leaving the removal untraceable.
        Assert.Equal(1, result.SupersededCounts[materialized.Id]);
        Assert.Equal(1, result.TotalSuperseded);
    }

    [Fact]
    public void MaterializedSignal_AlsoSupersedesAnAccruedSpec191DirectionalSignal()
    {
        // The accrued v7 record arrives here already neutralized by §1.4 (Neutral, ordinary strength) but
        // still carrying its legacy envelope. It is an ordinary loser, never a rival direction — which is
        // precisely why §1.4 must run FIRST.
        var materialized = MaterializedNewsSignal(SignalId(0xB1), ArticleEvidenceId, SignalDirection.Negative);
        IReadOnlyList<Signal> input =
        [
            NewsSignal(SignalId(0xA1), ArticleEvidenceId, SignalDirection.Neutral, LegacyMetadata()),
            OrdinaryNewsSignal(SignalId(0xA2), ArticleEvidenceId),
            materialized,
        ];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        var survivor = Assert.Single(result.Signals);
        Assert.Equal(materialized.Id, survivor.Id);
        Assert.Equal(2, result.SupersededCounts[materialized.Id]);
    }

    [Fact]
    public void MalformedV1Envelope_DoesNotWin_AndRemovesNothing()
    {
        // A signal claiming news-judgment-signal-v1 without the provenance that version promises. §1.4 has
        // already failed it closed to Neutral; here it must additionally lose the supersede, so an
        // unverifiable claim can never displace the honest article event.
        var malformed = NewsSignal(
            SignalId(0xB9),
            ArticleEvidenceId,
            SignalDirection.Neutral,
            EvidenceMetadata.Compose(
                new Dictionary<string, string>
                {
                    [NewsDirectionalSignalMetadata.JudgmentSignalVersionKey] =
                        NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
                    [NewsDirectionalSignalMetadata.JudgmentIdKey] = JudgmentId.ToString("D"),
                    // No cohort key: the version's promised provenance is not there.
                    [NewsDirectionalSignalMetadata.TrajectoryKey] = "Improving",
                },
                []));

        IReadOnlyList<Signal> input = [OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId), malformed];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Equal(0, result.TotalSuperseded);
    }

    [Fact]
    public void UnreadableEnvelope_DoesNotWin()
    {
        var unreadable = NewsSignal(SignalId(0xB8), ArticleEvidenceId, SignalDirection.Neutral, "{not json");

        IReadOnlyList<Signal> input = [OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId), unreadable];

        Assert.Same(input, NewsJudgmentSignalSupersede.Apply(input).Signals);
    }

    [Fact]
    public void TwoMaterializedSignals_LatestCreatedWins()
    {
        // Two grounded reads of the SAME article. ObservedAtUtc is the article's instant and is identical
        // across re-materializations, so only the creation instant distinguishes them: the newest grounded
        // read is the current one.
        var older = MaterializedNewsSignal(
            SignalId(0xB1), ArticleEvidenceId, SignalDirection.Positive, createdAt: Base.AddDays(1));
        var newer = MaterializedNewsSignal(
            SignalId(0xB2), ArticleEvidenceId, SignalDirection.Negative, createdAt: Base.AddDays(2));

        var forward = NewsJudgmentSignalSupersede.Apply(new[] { older, newer });
        var reversed = NewsJudgmentSignalSupersede.Apply(new[] { newer, older });

        Assert.Equal(newer.Id, Assert.Single(forward.Signals).Id);
        Assert.Equal(1, forward.SupersededCounts[newer.Id]);

        // Order-independent (AD-3): the winner is chosen by a strict comparison, never by arrival order.
        Assert.Equal(newer.Id, Assert.Single(reversed.Signals).Id);
    }

    [Fact]
    public void TwoMaterializedSignals_SameCreatedInstant_LowestIdWins()
    {
        var lower = MaterializedNewsSignal(
            SignalId(0x01), ArticleEvidenceId, SignalDirection.Positive, createdAt: Base.AddDays(1));
        var higher = MaterializedNewsSignal(
            SignalId(0x02), ArticleEvidenceId, SignalDirection.Negative, createdAt: Base.AddDays(1));

        var result = NewsJudgmentSignalSupersede.Apply(new[] { higher, lower });

        Assert.Equal(lower.Id, Assert.Single(result.Signals).Id);
    }

    [Fact]
    public void DuplicateCopyOfTheWinner_IsRemovedAndCounted()
    {
        // An exact duplicate of the winner (the same signal appearing twice in the assembled set): at most
        // one MediaAttention signal per superseded evidence id survives, mirroring GuidanceChangeSupersede.
        var materialized = MaterializedNewsSignal(SignalId(0xB1), ArticleEvidenceId, SignalDirection.Positive);

        var result = NewsJudgmentSignalSupersede.Apply(new[] { materialized, materialized });

        Assert.Single(result.Signals);
        Assert.Equal(1, result.SupersededCounts[materialized.Id]);
    }

    // ---------------------------------------------------------------------------------------------
    // What it must never touch.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DifferentEvidenceId_AndNonMediaSignals_AreUntouched()
    {
        var materialized = MaterializedNewsSignal(SignalId(0xB1), ArticleEvidenceId, SignalDirection.Positive);
        var ordinarySameEvidence = OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId);

        // A different article's ordinary signal, and a non-news signal that happens to share the SUPERSEDED
        // evidence id — neither is this transform's business.
        var otherArticle = OrdinaryNewsSignal(SignalId(0xA5), OtherEvidenceId);
        var nonNewsSameEvidence = NonNewsSignal(SignalId(0xC1), ArticleEvidenceId);

        var result = NewsJudgmentSignalSupersede.Apply(
            new[] { ordinarySameEvidence, otherArticle, nonNewsSameEvidence, materialized });

        var ids = result.Signals.Select(s => s.Id).ToList();
        Assert.Equal([otherArticle.Id, nonNewsSameEvidence.Id, materialized.Id], ids);
        Assert.Equal(1, result.TotalSuperseded);
    }

    [Fact]
    public void Survivors_KeepTheInputsRelativeOrdering()
    {
        var materialized = MaterializedNewsSignal(SignalId(0xB1), ArticleEvidenceId, SignalDirection.Positive);
        var first = NonNewsSignal(SignalId(0xC1), OtherEvidenceId);
        var last = OrdinaryNewsSignal(SignalId(0xA9), OtherEvidenceId);

        var result = NewsJudgmentSignalSupersede.Apply(
            new[] { first, OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId), materialized, last });

        Assert.Equal([first.Id, materialized.Id, last.Id], result.Signals.Select(s => s.Id).ToList());
    }

    [Fact]
    public void ScoringSignalOverload_SupersedesTheSameWay_AndKeepsEvidencePairing()
    {
        var materialized = MaterializedNewsSignal(SignalId(0xB1), ArticleEvidenceId, SignalDirection.Positive);
        var ordinary = OrdinaryNewsSignal(SignalId(0xA1), ArticleEvidenceId);

        var result = NewsJudgmentSignalSupersede.Apply(new[] { Pair(ordinary), Pair(materialized) });

        var survivor = Assert.Single(result.Signals);
        Assert.Equal(materialized.Id, survivor.Signal.Id);
        Assert.Equal(ArticleEvidenceId, survivor.Evidence.Id);
        Assert.Equal(1, result.SupersededCounts[materialized.Id]);
    }

    [Fact]
    public void EmptyInput_IsANoOp()
    {
        IReadOnlyList<Signal> input = [];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Empty(result.SupersededCounts);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture builders — every one constructed here, never copied from live data.
    // ---------------------------------------------------------------------------------------------

    private static Guid SignalId(byte discriminator) =>
        new([.. Enumerable.Repeat(discriminator, 16)]);

    private static Signal OrdinaryNewsSignal(Guid id, Guid evidenceId) =>
        NewsSignal(id, evidenceId, SignalDirection.Neutral, metadataJson: null);

    private static Signal NewsSignal(
        Guid id, Guid evidenceId, SignalDirection direction, string? metadataJson) =>
        new SignalBuilder()
            .WithId(id)
            .WithEvidenceId(evidenceId)
            .WithType(SignalType.MediaAttention)
            .WithDirection(direction)
            .WithStrength(4)
            .WithNovelty(4)
            .WithConfidence(0.5m)
            .WithObservedAtUtc(Base)
            .WithCreatedAtUtc(Base)
            .WithMetadataJson(metadataJson)
            .Build();

    private static Signal NonNewsSignal(Guid id, Guid evidenceId) =>
        new SignalBuilder()
            .WithId(id)
            .WithEvidenceId(evidenceId)
            .WithType(SignalType.GuidanceChange)
            .WithDirection(SignalDirection.Positive)
            .WithObservedAtUtc(Base)
            .WithCreatedAtUtc(Base)
            .Build();

    /// <summary>
    /// A structurally valid spec-194 §1.2 signal, composed through the SHARED envelope writer the
    /// materializer itself uses — never a hand-rolled second JSON shape, so this fixture cannot drift from
    /// what the producer actually writes.
    /// </summary>
    private static Signal MaterializedNewsSignal(
        Guid id, Guid evidenceId, SignalDirection direction, DateTimeOffset? createdAt = null)
    {
        var metadata = NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
            JudgmentId,
            "deepseek|p2|s2|stage1|families",
            direction == SignalDirection.Positive ? "Improving" : "Deteriorating",
            [new Guid("aaaaaaaa-0000-4000-8000-000000000001")],
            [new Guid("bbbbbbbb-0000-4000-8000-000000000001")],
            [evidenceId]);

        return NewsSignal(id, evidenceId, direction, metadata) with
        {
            CreatedAtUtc = createdAt ?? Base,
        };
    }

    /// <summary>The exact spec-191 envelope: judgment id + cohort key + matched observation, no v1 token.</summary>
    private static string LegacyMetadata() => EvidenceMetadata.Compose(
        new Dictionary<string, string>
        {
            [NewsDirectionalSignalMetadata.JudgmentIdKey] = JudgmentId.ToString("D"),
            [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = "deepseek|p2|s2|stage1|families",
            [NewsDirectionalSignalMetadata.ObservationIdKey] = "1a2b3c4d-4444-4d44-9444-dddddddddddd",
            [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
        },
        []);

    private static ScoringSignal Pair(Signal signal) =>
        new(
            signal,
            new EvidenceBuilder()
                .WithId(signal.EvidenceId)
                .WithContentHash(signal.EvidenceId.ToString("N"))
                .Build());
}
