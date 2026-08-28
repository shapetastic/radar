using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// THE <c>radar-baseline-activity-v1</c> COMPOSITION TRIPWIRE (spec 154), modelled on
/// <see cref="RadarScoreFormulaV10CompositionGuardTests"/>. It pins, deliberately in ONE place so they can
/// only move together:
/// <list type="number">
/// <item>the composition revision token (<see cref="PinnedRevision"/>),</item>
/// <item>the formula's FULL output on a fixed fixture — all five components, the explanation, the whole
/// <c>ComponentJson</c> and the ordered contribution/link chain,</item>
/// <item>the <c>ScoringConfigVersion</c> a baseline strategy stamps at the code-default weights and window.</item>
/// </list>
///
/// <para><b>WHY A CONTROL NEEDS THIS MORE THAN A COMPOSITE DOES.</b> Every "strategy X beats the baseline"
/// claim (AD-15) is a claim about a specific, fixed definition of "baseline". If that definition drifted
/// silently — a quality weight creeping into the activity measure, a discount appearing on the composed score
/// — every such claim would be invalidated retroactively and invisibly, and the leaderboard would keep
/// printing numbers as though nothing had happened. There are exactly TWO green fixes when this test fails:
/// <b>revert</b>, or <b>bump <c>CompositionRevision</c> and update all three pins together</b>, which
/// re-stamps every baseline strategy and trips <c>StrategyIdentityGuard</c> on the next run.</para>
///
/// <para>The fixture is deliberately close to <see cref="RadarScoreFormulaV10CompositionGuardTests"/>' — the
/// same six signals over a Large-tier company — with the breadth channel replaced by weight on the two
/// collector channels, because this formula REJECTS a breadth channel (see
/// <see cref="RadarBaselineActivityFormulaV1"/>). Read side by side, the two files show exactly what the
/// control ignores: v10 scores the same six signals at Opportunity 9 after a mixed directional read and a
/// notedness discount; the baseline scores them on count alone.</para>
/// </summary>
public sealed class RadarBaselineActivityFormulaV1CompositionGuardTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution ORDER (observed instant, then signal id) is stable across
    // runs and machines (AD-3) — the same discipline the v8/v9/v10 pins use.
    private static readonly Guid CompanyId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FilingSignalId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FilingEvidenceId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PressSignalId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PressEvidenceId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid InsiderOneSignalId = new("66666666-6666-6666-6666-666666666666");
    private static readonly Guid InsiderOneEvidenceId = new("77777777-7777-7777-7777-777777777777");
    private static readonly Guid InsiderTwoSignalId = new("88888888-8888-8888-8888-888888888888");
    private static readonly Guid InsiderTwoEvidenceId = new("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ReutersSignalId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ReutersEvidenceId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid BloombergSignalId = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid BloombergEvidenceId = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static ScoringChannelSet Channels() => ScoringChannelSet.Create(
        [
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.6, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.4, 2),
        ],
        "baseline-guard");

    [Fact]
    public void CompositionRevision_IsPinned_AndComposesIntoTheFormulaIdentity()
    {
        var formula = new RadarBaselineActivityFormulaV1(
            new ScoringWeights(),
            new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default),
            Channels());

        Assert.Equal(PinnedRevision, formula.CompositionRevision);
        Assert.Equal(ScoreFormulaVersions.BaselineActivityV1, formula.Version);
        Assert.Equal(
            $"{ScoreFormulaVersions.BaselineActivityV1}@{PinnedRevision}", FormulaIdentity.Of(formula));
    }

    [Fact]
    public async Task BaselineComposition_AndTheStampItProduces_ArePinnedTogether()
    {
        var signals = new InMemorySignalRepository();
        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();
        var weights = new ScoringWeights();
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        var engine = new ScoringEngine(
            signals,
            new NullSignalFileStore(),
            evidence,
            new InMemoryScoreRepository(),
            companies,
            new RadarBaselineActivityFormulaV1(weights, attention, Channels()),
            weights,
            attention,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            // The CODE DEFAULT window (30 days) — the same one ScoringConfigFingerprintTests' pins use.
            new ScoringOptions(),
            NullLogger<ScoringEngine>.Instance,
            strategyName: "baseline-guard",
            channels: Channels());

        // Large tier: under v9/v10 this would bite through the notedness discount. Here it must not, and the
        // pinned Opportunity below is the evidence that it does not.
        await companies.AddAsync(
            new CompanyBuilder().WithId(CompanyId).WithFollowingTier(FollowingTier.Large).Build(),
            CancellationToken.None);

        // "filings": MIXED directional mass and two very different strengths/qualities — all of which this
        // formula deliberately ignores, counting 2.
        await SeedAsync(
            signals, evidence, FilingSignalId, FilingEvidenceId, "sec-edgar", SignalType.GuidanceChange,
            SignalDirection.Positive, strength: 8, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-3));
        await SeedAsync(
            signals, evidence, PressSignalId, PressEvidenceId, "sec-edgar", SignalType.CustomerWin,
            SignalDirection.Negative, strength: 5, EvidenceSourceType.PressRelease, "Acme Newsroom",
            EvidenceQuality.High, WindowEnd.AddDays(-10));

        // "insider": ALL NEUTRAL — zero under radar-formula-v10, counted in full here.
        await SeedAsync(
            signals, evidence, InsiderOneSignalId, InsiderOneEvidenceId, "sec-form4", SignalType.InsiderBuying,
            SignalDirection.Neutral, strength: 4, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-6));
        await SeedAsync(
            signals, evidence, InsiderTwoSignalId, InsiderTwoEvidenceId, "sec-form4", SignalType.InsiderBuying,
            SignalDirection.Neutral, strength: 3, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-12));

        // Two genuine third-party outlets, budgeted by NO channel here — so they raise Attention (reported,
        // never consulted) and still emit their own contributions.
        await SeedAsync(
            signals, evidence, ReutersSignalId, ReutersEvidenceId, "newssearch", SignalType.MediaAttention,
            SignalDirection.Neutral, strength: 3, EvidenceSourceType.NewsArticle, "Reuters",
            EvidenceQuality.Medium, WindowEnd.AddDays(-20));
        await SeedAsync(
            signals, evidence, BloombergSignalId, BloombergEvidenceId, "newssearch", SignalType.MediaAttention,
            SignalDirection.Neutral, strength: 2, EvidenceSourceType.NewsArticle, "Bloomberg",
            EvidenceQuality.Medium, WindowEnd.AddDays(-15));

        var result = await engine.ScoreCompanyAsync(CompanyId, WindowEnd, CancellationToken.None);
        var snapshot = result.Snapshot;

        // ---- PIN 2a: the five components ------------------------------------------------------------
        Assert.Equal(PinnedTrajectory, snapshot.TrajectoryScore);
        Assert.Equal(PinnedOpportunity, snapshot.OpportunityScore);
        Assert.Equal(PinnedAttention, snapshot.AttentionScore);
        Assert.Equal(PinnedEvidenceConfidence, snapshot.EvidenceConfidenceScore);
        Assert.Equal(PinnedSignalVelocity, snapshot.SignalVelocityScore);

        // ---- PIN 2b: the narrative + the whole machine-readable breakdown -----------------------------
        Assert.Equal(PinnedExplanation, snapshot.Explanation);
        Assert.Equal(PinnedComponentJson, snapshot.ComponentJson);

        // ---- PIN 2c: the provenance chain, in order ---------------------------------------------------
        Assert.Equal(
            PinnedLinks,
            result.Links
                .Select(l => (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight))
                .ToArray());

        // ---- PIN 3: the identity a baseline strategy stamps -------------------------------------------
        Assert.Equal(
            $"mvp-engine-v1+{ScoreFormulaVersions.BaselineActivityV1}@{PinnedRevision}",
            snapshot.ScoringVersion);
        Assert.Equal(PinnedScoringConfigVersion, snapshot.ScoringConfigVersion);
        Assert.Equal(
            $"{ScoreFormulaVersions.BaselineActivityV1}@{PinnedRevision}",
            engine.EffectiveConfig.FormulaVersion);
    }

    // ---------------------------------------------------------------------------------------------------
    // THE PINS. Moving any of them without a CompositionRevision bump is the failure this file prevents.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>PIN 1 — the composition revision.</summary>
    private const string PinnedRevision = "rev1";

    // The arithmetic, verifiable by hand from the fixture alone — which is the point of a control:
    //   filings  = 2 signals, S = 3 ⇒ 2/(2+3) = 0.4    × weight 0.6 = 0.24
    //   insider  = 2 signals, S = 2 ⇒ 2/(2+2) = 0.5    × weight 0.4 = 0.20
    //   composite = 0.44 ⇒ Opportunity 44, with NO notedness discount applied (v10 would have multiplied by
    //   0.532 for this Large-tier, Attention-42 company).
    private const int PinnedTrajectory = 57;
    private const int PinnedOpportunity = 44;
    private const int PinnedAttention = 42;
    private const int PinnedEvidenceConfidence = 80;
    private const int PinnedSignalVelocity = 100;

    private const string PinnedExplanation =
        "radar-baseline-activity-v1: 6 signal(s) over 30d across 2 channel(s) → Opportunity 44 "
            + "(composite 0.440 = filings 0.400×0.60, insider 0.500×0.40); "
            + "Trajectory 57, Attention 42, Confidence 80, Velocity 100. "
            + "BASELINE CONTROL: signal count only — no direction, no notedness, no quality weighting.";

    private const string PinnedComponentJson =
        "{\"TrajectoryScore\":57,\"OpportunityScore\":44,\"AttentionScore\":42,"
            + "\"EvidenceConfidenceScore\":80,\"SignalVelocityScore\":100,"
            + "\"Formula\":\"radar-baseline-activity-v1\",\"Revision\":\"rev1\","
            + "\"Composite\":0.44,\"Channels\":["
            + "{\"Name\":\"filings\",\"Kind\":\"collector\",\"Weight\":0.6,\"Saturation\":3,"
            + "\"Score\":0.4,\"WeightedContribution\":0.24,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[\"sec-edgar\"],"
            + "\"CollectorsRan\":[\"sec-edgar\"],\"CollectorsNotRun\":[],\"RecordedSignals\":2,"
            + "\"InferredSignals\":0,\"UnattributedSignals\":0},"
            + "{\"Name\":\"insider\",\"Kind\":\"collector\",\"Weight\":0.4,\"Saturation\":2,"
            + "\"Score\":0.5,\"WeightedContribution\":0.2,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[\"sec-form4\"],\"CollectorsRan\":[],"
            + "\"CollectorsNotRun\":[\"sec-form4\"],\"RecordedSignals\":2,\"InferredSignals\":0,"
            + "\"UnattributedSignals\":0}]}";

    /// <summary>
    /// PIN 3 — the stamp a default-weights, default-window (30d) baseline strategy with THIS channel budget
    /// produces. It moves if the composition revision moves, if a hashed weight moves, or if the budget moves.
    /// <para>
    /// It also moves when a hashed field OUTSIDE this file moves, and it just did: SPEC 194 §1.5 bumped
    /// <c>MediaAttentionCollapse.Version</c> <c>media-collapse-v1</c> → <c>media-collapse-v2</c> (a grounded
    /// judgment-derived direction now outranks an earlier ordinary Neutral member as a bucket's
    /// representative), and the media-collapse descriptor is a hashed field, so EVERY strategy re-stamps at
    /// once — radar-scoring-fp-c6139a481f09 → the value below. ⚠ Spec 194 §2 will move it again when the
    /// news-judgment scoring identity is folded in; that pass is deliberately not part of this one.
    /// </para>
    /// <para>
    /// ⚠ THAT FORECAST WAS WRONG, and the reason is worth recording. SPEC 194 §2 folds the news-read
    /// identity into <c>SignalSourceDescriptor.CanonicalDescriptor()</c> — which this file does NOT use: it
    /// substitutes <c>StubSourceDescriptor</c>, whose value is deliberately frozen (see that type's remarks)
    /// so an unrelated identity move cannot disturb a FORMULA-COMPOSITION pin. So §2 moved the six
    /// <c>ScoringConfigFingerprintTests</c> pins and left this one exactly where §1.5 put it. The isolation
    /// is the feature; do not "fix" it by pointing the stub at the real descriptor.
    /// </para>
    /// <para>
    /// SPEC 196 MOVES IT AGAIN, for a hashed field outside this file once more — the attention publisher
    /// TIER MAP (<c>attnDesc</c>). The unknown default was inverted 0.25 → 0.1 and the map gained the
    /// four-tier Wire/Mill/Platform/Genuine policy with its audited membership
    /// (<c>docs/cohorts/attention-publisher-audit-v1.md</c>). These guard fixtures DO consume the real
    /// <c>AttentionSourceTierOptions.Default</c>, so every strategy re-stamps at once. Verified to be the
    /// SOLE cause: substituting a reconstructed pre-196 tier map into this fixture reproduces the previous
    /// pin exactly, with the frozen <c>StubSourceDescriptor</c> and every other input untouched. The
    /// composition, the weights, the budget, the revision and the pinned COMPONENT values below are all
    /// unmoved — only the stamp is.
    /// </para>
    /// </summary>
    private const string PinnedScoringConfigVersion = "radar-scoring-fp-7af921a7ae84";

    private static (Guid SignalId, Guid EvidenceId, string Reason, int Weight)[] PinnedLinks =>
    [
        (ReutersSignalId, ReutersEvidenceId,
            "MediaAttention (Neutral), strength 3, confidence 0.80 — "
                + "no channel (collector newssearch is not budgeted by this strategy)", 0),
        (BloombergSignalId, BloombergEvidenceId,
            "MediaAttention (Neutral), strength 2, confidence 0.80 — "
                + "no channel (collector newssearch is not budgeted by this strategy)", 0),
        (InsiderTwoSignalId, InsiderTwoEvidenceId,
            "InsiderBuying (Neutral), strength 3, confidence 0.80 — channel insider", 0),
        (PressSignalId, PressEvidenceId,
            "CustomerWin (Negative), strength 5, confidence 0.80 — channel filings", -3),
        (InsiderOneSignalId, InsiderOneEvidenceId,
            "InsiderBuying (Neutral), strength 4, confidence 0.80 — channel insider", 0),
        (FilingSignalId, FilingEvidenceId,
            "GuidanceChange (Positive), strength 8, confidence 0.80 — channel filings", 6),
    ];

    private static async Task SeedAsync(
        InMemorySignalRepository signals,
        InMemoryEvidenceRepository evidence,
        Guid signalId,
        Guid evidenceId,
        string collector,
        SignalType type,
        SignalDirection direction,
        int strength,
        EvidenceSourceType sourceType,
        string sourceName,
        EvidenceQuality quality,
        DateTimeOffset observedAtUtc)
    {
        var metadata = EvidenceMetadata.Compose(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CollectionProvenanceMetadata.MetadataKey] = collector,
            },
            []);

        var item = new EvidenceBuilder()
            .WithId(evidenceId)
            .WithContentHash(evidenceId.ToString("N"))
            .WithSourceType(sourceType)
            .WithSourceName(sourceName)
            .WithQuality(quality)
            .WithMetadataJson(metadata)
            .WithPublishedAtUtc(observedAtUtc)
            .WithCollectedAtUtc(observedAtUtc)
            .Build();

        var signal = new SignalBuilder()
            .WithId(signalId)
            .WithEvidenceId(evidenceId)
            .WithCompanyId(CompanyId)
            .WithType(type)
            .WithDirection(direction)
            .WithStrength(strength)
            .WithReviewStatus(SignalReviewStatus.Approved)
            .WithObservedAtUtc(observedAtUtc)
            .WithCreatedAtUtc(observedAtUtc)
            .Build();

        await evidence.AddIfNewAsync(item, CancellationToken.None);
        await signals.AddAsync(signal, CancellationToken.None);
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

    /// <summary>
    /// The default AI-OFF signal-source identity descriptor, with an asymmetric enabled-collector vocabulary so
    /// the ran/not-run split is pinned too. Neither is a scoring input.
    /// </summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v6;";

        public string CollectionProvenance() => "collectors=sec-edgar;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec-edgar"];
    }
}
