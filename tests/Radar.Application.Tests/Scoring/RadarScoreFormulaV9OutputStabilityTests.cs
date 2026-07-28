using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 153's "<c>radar-formula-v9</c> is byte-identical after this slice" criterion, made checkable instead
/// of argued — the v9 twin of <see cref="ScoringOutputStabilityTests"/> (which pins v8), deliberately built
/// from the same idioms and the same fixed-Guid discipline rather than as a third mechanism.
/// <para>
/// Spec 153 adds <c>radar-formula-v10</c> and, to avoid a THIRD copy of v8's per-signal blocks, EXTRACTS the
/// shared machinery v9 had copied: the v8-meaning Trajectory / EvidenceConfidence / SignalVelocity /
/// Attention component blocks into <see cref="ScoreSignalMath"/>, and the whole channel-composition loop into
/// <see cref="ScoringChannelComposition"/>, which v9 and v10 now share and differ in only by their collector
/// direction factor. An extraction that moved a number would be a silent rescoring of a live series, so this
/// file pins v9's WHOLE output on one fixed fixture — all five components, the explanation, the entire
/// <c>ComponentJson</c> string, and the ordered evidence-link chain — at the code-default weights and window.
/// </para>
/// <para>
/// THE VALUES WERE CAPTURED BEFORE THE EXTRACTION, from the pre-153 sources, and this file was NOT touched
/// afterwards. If a future change moves a number here, it moved a v9 score, and no refactoring argument can
/// explain that away: fix the code, not the pin. (A deliberate v9 change would have to update these pins
/// consciously — and, per spec 149's uncomfortable lesson, would also owe the series a new strategy NAME,
/// because v9's <c>ScoringConfigVersion</c> does not move when only its composition does.)
/// </para>
/// <para>
/// The fixture exercises exactly what the extraction touches: TWO collector channels — one carrying MIXED
/// positive/negative directional mass (so the preponderance/direction factor is live, not saturated) and one
/// carrying only Neutral signals (the v9 behaviour spec 153 changes in v10, pinned here as the control) —
/// plus a breadth channel with genuine third-party publishers, so Attention is non-zero and the spec-149
/// notedness discount actually bites (a Large-tier company makes the tier term bite too).
/// </para>
/// </summary>
public sealed class RadarScoreFormulaV9OutputStabilityTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution ORDER (observed instant, then signal id) is stable across
    // runs and machines — otherwise the pinned link chain below would be a coin toss (AD-3).
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

    /// <summary>
    /// The pinned strategy budget: a mixed-direction collector channel, an all-Neutral collector channel and
    /// a breadth channel. Weights sum to 1.0 and each channel carries its own saturation, as
    /// <see cref="ScoringChannelSet.Create"/> requires.
    /// </summary>
    private static ScoringChannelSet Channels() => ScoringChannelSet.Create(
        [
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.4, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.3, 2),
            ScoringChannel.Breadth("attention", 0.3, 3),
        ],
        "v9-stability");

    [Fact]
    public async Task DefaultConfig_V9OutputIsUnchangedByTheSpec153Extraction()
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
            new RadarScoreFormulaV9(weights, attention, Channels()),
            weights,
            attention,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            // The CODE DEFAULT window (30 days) — the same one the fingerprint pins are computed at.
            new ScoringOptions(),
            NullLogger<ScoringEngine>.Instance,
            strategyName: "v9-stability",
            channels: Channels());

        await companies.AddAsync(
            new CompanyBuilder().WithId(CompanyId).WithFollowingTier(FollowingTier.Large).Build(),
            CancellationToken.None);

        // ---- the "filings" channel: MIXED directional mass, so the direction factor is genuinely exercised
        await SeedAsync(
            signals, evidence, FilingSignalId, FilingEvidenceId, "sec-edgar", SignalType.GuidanceChange,
            SignalDirection.Positive, strength: 8, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-3));
        await SeedAsync(
            signals, evidence, PressSignalId, PressEvidenceId, "sec-edgar", SignalType.CustomerWin,
            SignalDirection.Negative, strength: 5, EvidenceSourceType.PressRelease, "Acme Newsroom",
            EvidenceQuality.High, WindowEnd.AddDays(-10));

        // ---- the "insider" channel: ALL NEUTRAL — the routine-Form-4 shape spec 153 is about. Under v9 this
        //      still earns half its saturated share; that is precisely what these pins record as the control.
        await SeedAsync(
            signals, evidence, InsiderOneSignalId, InsiderOneEvidenceId, "sec-form4", SignalType.InsiderBuying,
            SignalDirection.Neutral, strength: 4, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-6));
        await SeedAsync(
            signals, evidence, InsiderTwoSignalId, InsiderTwoEvidenceId, "sec-form4", SignalType.InsiderBuying,
            SignalDirection.Neutral, strength: 3, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-12));

        // ---- the breadth channel: two genuine third-party outlets, 5 days apart so the spec-109 same-event
        //      collapse (3-day window) leaves both standing. Non-zero Attention ⇒ the notedness discount bites.
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

        // ---- the five components -------------------------------------------------------------------
        Assert.Equal(PinnedTrajectory, snapshot.TrajectoryScore);
        Assert.Equal(PinnedOpportunity, snapshot.OpportunityScore);
        Assert.Equal(PinnedAttention, snapshot.AttentionScore);
        Assert.Equal(PinnedEvidenceConfidence, snapshot.EvidenceConfidenceScore);
        Assert.Equal(PinnedSignalVelocity, snapshot.SignalVelocityScore);

        // ---- the narrative + the machine-readable breakdown ------------------------------------------
        Assert.Equal(PinnedExplanation, snapshot.Explanation);
        Assert.Equal(PinnedComponentJson, snapshot.ComponentJson);

        // ---- the provenance chain, in order (observed instant, then signal id) -----------------------
        Assert.Equal(
            PinnedLinks,
            result.Links
                .Select(l => (l.SignalId, l.EvidenceId, l.ContributionReason, l.ContributionWeight))
                .ToArray());

        // ---- and the composed formula identity, which spec 153 routes through FormulaIdentity ---------
        // v9 declares no CompositionRevision, so its stamp is the bare version token exactly as before.
        Assert.Equal("mvp-engine-v1+radar-formula-v9", snapshot.ScoringVersion);
    }

    // ---------------------------------------------------------------------------------------------------
    // THE PINS. Captured from the pre-153 sources; do not "update" them to make a refactor pass.
    // ---------------------------------------------------------------------------------------------------

    private const int PinnedTrajectory = 57;
    private const int PinnedOpportunity = 22;
    private const int PinnedAttention = 42;
    private const int PinnedEvidenceConfidence = 80;
    private const int PinnedSignalVelocity = 100;

    private const string PinnedExplanation =
        "radar-formula-v9: 6 signal(s) over 30d across 3 channel(s) → Opportunity 22 (composite 0.408 = "
            + "attention 0.423×0.30, filings 0.438×0.40, insider 0.353×0.30; × notedness 0.532); "
            + "Trajectory 57, Attention 42, Confidence 80, Velocity 100.";

    private const string PinnedComponentJson =
        "{\"TrajectoryScore\":57,\"OpportunityScore\":22,\"AttentionScore\":42,"
            + "\"EvidenceConfidenceScore\":80,\"SignalVelocityScore\":100,\"Formula\":\"radar-formula-v9\","
            + "\"Composite\":0.40812828306380944,\"Discount\":0.532,\"Channels\":["
            + "{\"Name\":\"attention\",\"Kind\":\"breadth\",\"Weight\":0.3,\"Saturation\":3,"
            + "\"Score\":0.4230769230769231,\"WeightedContribution\":0.12692307692307692,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[],\"CollectorsRan\":[],"
            + "\"CollectorsNotRun\":[],\"RecordedSignals\":2,\"InferredSignals\":0,"
            + "\"UnattributedSignals\":0},"
            + "{\"Name\":\"filings\",\"Kind\":\"collector\",\"Weight\":0.4,\"Saturation\":3,"
            + "\"Score\":0.43830713299889007,\"WeightedContribution\":0.17532285319955604,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[\"sec-edgar\"],"
            + "\"CollectorsRan\":[\"sec-edgar\"],\"CollectorsNotRun\":[],\"RecordedSignals\":2,"
            + "\"InferredSignals\":0,\"UnattributedSignals\":0},"
            + "{\"Name\":\"insider\",\"Kind\":\"collector\",\"Weight\":0.3,\"Saturation\":2,"
            + "\"Score\":0.35294117647058826,\"WeightedContribution\":0.10588235294117647,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[\"sec-form4\"],\"CollectorsRan\":[],"
            + "\"CollectorsNotRun\":[\"sec-form4\"],\"RecordedSignals\":2,\"InferredSignals\":0,"
            + "\"UnattributedSignals\":0}]}";

    private static (Guid SignalId, Guid EvidenceId, string Reason, int Weight)[] PinnedLinks =>
    [
        (ReutersSignalId, ReutersEvidenceId,
            "MediaAttention (Neutral), strength 3, confidence 0.80 — channel attention", 0),
        (BloombergSignalId, BloombergEvidenceId,
            "MediaAttention (Neutral), strength 2, confidence 0.80 — channel attention", 0),
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
        // The spec-146 recorded collector: what a v9 collector channel selects on.
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
        public Task<string> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult("written/signal.json");

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    /// <summary>
    /// A fixed identity/provenance descriptor. Neither is a scoring input — but the enabled-collector
    /// vocabulary IS recorded per channel, so it is deliberately asymmetric here (<c>sec-edgar</c> enabled,
    /// <c>sec-form4</c> not) to pin the ran/not-run split as well.
    /// </summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v6;";

        public string CollectionProvenance() => "collectors=sec-edgar;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec-edgar"];
    }
}
