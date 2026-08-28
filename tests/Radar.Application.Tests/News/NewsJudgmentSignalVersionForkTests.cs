using Radar.Application.Collectors;
using Radar.Application.Identity;
using Radar.Application.News;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.News;

/// <summary>
/// SPEC 197 §1.3 — the materializer identity fork to <c>news-judgment-signal-v2</c>.
/// <para>
/// The fork is honest rather than silent because §1.1's match ladder changes WHICH judgments can produce a
/// scoring input. Two properties have to hold together: accrued v1 signals stay valid grounded directions
/// (they are on disk, append-only, and were grounded in the evidence their judgment cited), and every shared
/// scoring transform must answer the version question through the ONE classifier — never three copied
/// checks, which is how a signal becomes "valid enough to supersede" while "malformed enough to neutralize".
/// </para>
/// </summary>
public sealed class NewsJudgmentSignalVersionForkTests
{
    private static readonly Guid JudgmentId = new("9c8f7e6d-3333-4c33-9333-cccccccccccc");
    private static readonly Guid EvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Base = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private const string CohortKey = "deepseek|p2|s2|stage1|families";

    // ---------------------------------------------------------------------------------------------
    // The tokens themselves.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheCurrentTokenIsV2_TheRetiredOneIsStillDeclared_AndBothAreSupported()
    {
        Assert.Equal("news-judgment-signal-v2", NewsDirectionalSignalMetadata.JudgmentSignalVersionValue);
        Assert.Equal("news-judgment-signal-v1", NewsDirectionalSignalMetadata.RetiredJudgmentSignalVersionV1);
        Assert.NotEqual(
            NewsDirectionalSignalMetadata.RetiredJudgmentSignalVersionV1,
            NewsDirectionalSignalMetadata.JudgmentSignalVersionValue);
        Assert.Equal(
            [
                NewsDirectionalSignalMetadata.RetiredJudgmentSignalVersionV1,
                NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
            ],
            NewsDirectionalSignalMetadata.SupportedJudgmentSignalVersions);
    }

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 7 — deterministic ids, and the retired derivation the occupancy check needs.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheV2SignalId_IsDeterministic_AndDerivesFromTheV2Token()
    {
        var expected = DeterministicGuid.FromCanonicalString(
            "radar:news-judgment-signal:news-judgment-signal-v2:" + JudgmentId.ToString("D"));

        Assert.Equal(expected, NewsJudgmentSignalMaterializer.SignalIdFor(JudgmentId));
        Assert.Equal(
            NewsJudgmentSignalMaterializer.SignalIdFor(JudgmentId),
            NewsJudgmentSignalMaterializer.SignalIdFor(JudgmentId));
    }

    [Fact]
    public void TheRetiredV1Id_IsDerivedFromTheV1Token_AndDiffersFromTheV2Id()
    {
        // The whole reason the occupancy check exists: forking the token MOVES the deterministic id, so a
        // judgment already materialized under v1 would otherwise mint a second signal for one verdict.
        var expected = DeterministicGuid.FromCanonicalString(
            "radar:news-judgment-signal:news-judgment-signal-v1:" + JudgmentId.ToString("D"));

        Assert.Equal(expected, NewsJudgmentSignalMaterializer.RetiredV1SignalIdFor(JudgmentId));
        Assert.NotEqual(
            NewsJudgmentSignalMaterializer.SignalIdFor(JudgmentId),
            NewsJudgmentSignalMaterializer.RetiredV1SignalIdFor(JudgmentId));
    }

    [Fact]
    public void TheV2Envelope_IsDeterministic_AndStampsTheCurrentToken()
    {
        var first = Envelope(NewsDirectionalSignalMetadata.JudgmentSignalVersionValue);
        var second = Envelope(NewsDirectionalSignalMetadata.JudgmentSignalVersionValue);

        Assert.Equal(first, second);
        Assert.True(EvidenceMetadata.TryRead(first, out var metadata, out _));
        Assert.Equal(
            "news-judgment-signal-v2",
            metadata[NewsDirectionalSignalMetadata.JudgmentSignalVersionKey]);
    }

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 7 — the ONE classifier: v1 and v2 accepted, an unsupported claim fails CLOSED.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("news-judgment-signal-v1")]
    [InlineData("news-judgment-signal-v2")]
    public void AWellFormedEnvelopeOfEitherSupportedVersion_IsJudgmentDerived(string version)
    {
        Assert.Equal(
            NewsJudgmentSignalProvenance.JudgmentDerived,
            NewsDirectionalSignalMetadata.ClassifyProvenance(Envelope(version)));
    }

    [Theory]
    [InlineData("news-judgment-signal-v3")]
    [InlineData("news-judgment-signal-V2")]
    [InlineData("")]
    [InlineData("   ")]
    public void APresentButUnsupportedOrBlankVersion_FailsClosedAsMalformed(string version)
    {
        // It must NOT fall through as an unrelated metadata bag (None) and must NOT be read as the accrued
        // spec-191 shape (LegacyInheritance) — this envelope carries the legacy keys too, so both wrong
        // answers are reachable if the version branch is written as a fall-through. A claim Radar cannot
        // verify is worth less than no claim at all.
        Assert.Equal(
            NewsJudgmentSignalProvenance.MalformedJudgmentEnvelope,
            NewsDirectionalSignalMetadata.ClassifyProvenance(Envelope(version)));
    }

    [Fact]
    public void ASupportedVersionWithoutItsPromisedProvenance_IsStillMalformed()
    {
        var missingCohort = EvidenceMetadata.Compose(
            new Dictionary<string, string>
            {
                [NewsDirectionalSignalMetadata.JudgmentSignalVersionKey] =
                    NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
                [NewsDirectionalSignalMetadata.JudgmentIdKey] = JudgmentId.ToString("D"),
                [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
            },
            []);

        Assert.Equal(
            NewsJudgmentSignalProvenance.MalformedJudgmentEnvelope,
            NewsDirectionalSignalMetadata.ClassifyProvenance(missingCohort));
    }

    [Fact]
    public void TheAccruedSpec191Shape_WithNoVersionTokenAtAll_IsStillLegacyInheritance()
    {
        var legacy = EvidenceMetadata.Compose(
            new Dictionary<string, string>
            {
                [NewsDirectionalSignalMetadata.JudgmentIdKey] = JudgmentId.ToString("D"),
                [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = CohortKey,
                [NewsDirectionalSignalMetadata.ObservationIdKey] =
                    "1a2b3c4d-4444-4d44-9444-dddddddddddd",
                [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
            },
            []);

        Assert.Equal(
            NewsJudgmentSignalProvenance.LegacyInheritance,
            NewsDirectionalSignalMetadata.ClassifyProvenance(legacy));
    }

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 7 — every shared scoring transform accepts BOTH versions, through the one classifier.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("news-judgment-signal-v1")]
    [InlineData("news-judgment-signal-v2")]
    public void LegacyNeutralization_LeavesEitherSupportedVersionsDirectionIntact(string version)
    {
        // The §1.4 legacy matcher keys on the VERSIONED TOKEN, never on `Direction != Neutral` — so a v2
        // signal is not legacy, exactly as a v1 signal is not.
        var signal = JudgmentDerivedSignal(SignalId(0xB1), version);

        var result = LegacyNewsInheritanceNeutralization.Apply(new List<Signal> { signal });

        Assert.Equal(0, result.TotalNeutralized);
        Assert.Same(signal, Assert.Single(result.Signals));
        Assert.Equal(SignalDirection.Negative, result.Signals[0].Direction);
    }

    [Fact]
    public void LegacyNeutralization_StillSuppressesAnUnsupportedVersionClaim_AsMalformed()
    {
        var signal = JudgmentDerivedSignal(SignalId(0xB2), "news-judgment-signal-v3");

        var result = LegacyNewsInheritanceNeutralization.Apply(new List<Signal> { signal });

        Assert.Equal(1, result.TotalNeutralized);
        Assert.Equal(SignalDirection.Neutral, result.Signals[0].Direction);
        // Counted on the MALFORMED axis, never pooled with the accrued spec-191 residue: a current writer
        // emitting unverifiable provenance is a different and more urgent fact.
        Assert.Equal(1, result.MalformedEnvelopeCount);
        Assert.Equal(0, result.LegacyInheritanceCount);
    }

    [Theory]
    [InlineData("news-judgment-signal-v1")]
    [InlineData("news-judgment-signal-v2")]
    public void Supersede_LetsEitherSupportedVersionReplaceTheOrdinaryArticleSignal(string version)
    {
        var grounded = JudgmentDerivedSignal(SignalId(0xC1), version);
        var ordinary = OrdinaryNewsSignal(SignalId(0xC2));

        var result = NewsJudgmentSignalSupersede.Apply(new List<Signal> { ordinary, grounded });

        var survivor = Assert.Single(result.Signals);
        Assert.Equal(grounded.Id, survivor.Id);
        Assert.Equal(1, result.TotalSuperseded);
    }

    [Fact]
    public void Supersede_IgnoresAnUnsupportedVersionClaim_SoNothingIsRemoved()
    {
        var unverifiable = JudgmentDerivedSignal(SignalId(0xC3), "news-judgment-signal-v3");
        IReadOnlyList<Signal> input = [OrdinaryNewsSignal(SignalId(0xC4)), unverifiable];

        var result = NewsJudgmentSignalSupersede.Apply(input);

        Assert.Same(input, result.Signals);
        Assert.Equal(0, result.TotalSuperseded);
    }

    [Theory]
    [InlineData("news-judgment-signal-v1")]
    [InlineData("news-judgment-signal-v2")]
    public void MediaCollapse_PrefersEitherSupportedVersionAsTheBucketRepresentative(string version)
    {
        // media-collapse-v2's representative rule: a structurally valid judgment-derived signal beats an
        // EARLIER ordinary media signal in the same event bucket. Both supported versions must qualify, or a
        // company could hold a validated read and still score its news as plain attention.
        var earlierOrdinary = OrdinaryNewsSignal(SignalId(0xD1)) with
        {
            EvidenceId = new Guid("33333333-3333-3333-3333-333333333333"),
            ObservedAtUtc = Base,
        };
        var grounded = JudgmentDerivedSignal(SignalId(0xD2), version) with
        {
            ObservedAtUtc = Base.AddHours(2),
        };

        var collapse = new MediaAttentionCollapse(new MediaCollapseOptions { EventWindowDays = 3 });
        var result = collapse.Collapse([Pair(earlierOrdinary), Pair(grounded)]);

        var representative = Assert.Single(result.Signals);
        Assert.Equal(grounded.Id, representative.Signal.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures — every envelope composed through the SHARED writer or the shared metadata composer.
    // ---------------------------------------------------------------------------------------------

    private static string Envelope(string version) => EvidenceMetadata.Compose(
        new Dictionary<string, string>
        {
            [NewsDirectionalSignalMetadata.JudgmentSignalVersionKey] = version,
            [NewsDirectionalSignalMetadata.JudgmentIdKey] = JudgmentId.ToString("D"),
            [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = CohortKey,
            [NewsDirectionalSignalMetadata.ObservationIdKey] = "1a2b3c4d-4444-4d44-9444-dddddddddddd",
            [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
        },
        []);

    private static Guid SignalId(byte discriminator) =>
        new([.. Enumerable.Repeat(discriminator, 16)]);

    private static Signal NewsSignal(Guid id, SignalDirection direction, string? metadataJson) =>
        new SignalBuilder()
            .WithId(id)
            .WithEvidenceId(EvidenceId)
            .WithType(SignalType.MediaAttention)
            .WithDirection(direction)
            .WithStrength(4)
            .WithNovelty(4)
            .WithConfidence(0.5m)
            .WithObservedAtUtc(Base)
            .WithCreatedAtUtc(Base)
            .WithMetadataJson(metadataJson)
            .Build();

    private static Signal OrdinaryNewsSignal(Guid id) =>
        NewsSignal(id, SignalDirection.Neutral, metadataJson: null);

    private static Signal JudgmentDerivedSignal(Guid id, string version) =>
        NewsSignal(id, SignalDirection.Negative, Envelope(version));

    private static ScoringSignal Pair(Signal signal) =>
        new(
            signal,
            new EvidenceBuilder()
                .WithId(signal.EvidenceId)
                .WithContentHash(signal.EvidenceId.ToString("N"))
                .Build());
}
