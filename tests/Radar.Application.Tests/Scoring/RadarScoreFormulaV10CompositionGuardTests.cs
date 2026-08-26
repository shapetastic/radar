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
/// THE <c>radar-formula-v10</c> COMPOSITION TRIPWIRE (spec 153). It pins, deliberately in ONE place so they
/// can only move together:
/// <list type="number">
/// <item>the composition revision token (<see cref="PinnedRevision"/>),</item>
/// <item>v10's FULL output on a fixed fixture — all five components, the explanation, the whole
/// <c>ComponentJson</c> and the ordered contribution/link chain,</item>
/// <item>the <c>ScoringConfigVersion</c> a v10 strategy stamps at the code-default weights and window.</item>
/// </list>
///
/// <para><b>THE RULE, stated explicitly because it is the point of the file.</b> If you change how
/// <see cref="RadarScoreFormulaV10"/> COMPOSES its score, this test fails. There are exactly TWO green fixes:
/// <list type="bullet">
/// <item><b>revert</b> the change; or</item>
/// <item><b>bump <c>CompositionRevision</c> and update all three pins together</b>. Bumping the revision
/// re-stamps every v10 strategy's <c>ScoringConfigVersion</c> (it is folded in via
/// <see cref="FormulaIdentity"/>), which trips <c>StrategyIdentityGuard</c> on the next run and forces a
/// conscious decision about the discontinuity.</item>
/// </list>
/// Updating the output pins WITHOUT bumping the revision is not a fix — it reproduces exactly the spec-149
/// failure this mechanism exists to prevent, where v9's composition changed while its stamp did not and pre-
/// and post-change scores were silently mixed in one series.</para>
///
/// <para><b>Relationship to AD-6.</b> A genuinely NEW structure still earns <c>radar-formula-v11</c>: a new
/// class, a new token, and both predecessors left intact as controls. The revision is narrower — it exists so
/// an in-place ADJUSTMENT to v10's existing structure cannot happen invisibly. It is not a licence to keep
/// amending one formula indefinitely.</para>
///
/// <para>The fixture is the same shape as <c>RadarScoreFormulaV9OutputStabilityTests</c>' on purpose (a
/// mixed-direction collector channel, an ALL-NEUTRAL collector channel and a breadth channel, over a
/// Large-tier company so the notedness discount bites), so the two files can be read side by side to see
/// exactly what spec 153 changed: the same fixture scores Opportunity 22 under v9 and
/// <see cref="PinnedOpportunity"/> under v10, because the all-Neutral <c>insider</c> channel drops from a
/// positive half-share to exactly 0.</para>
/// </summary>
public sealed class RadarScoreFormulaV10CompositionGuardTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Fixed ids so the deterministic contribution ORDER (observed instant, then signal id) is stable across
    // runs and machines (AD-3) — the same discipline the v8/v9 stability pins use.
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
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.4, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.3, 2),
            ScoringChannel.Breadth("attention", 0.3, 3),
        ],
        "v10-guard");

    [Fact]
    public void CompositionRevision_IsPinned_AndComposesIntoTheFormulaIdentity()
    {
        var formula = new RadarScoreFormulaV10(
            new ScoringWeights(),
            new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default),
            Channels());

        Assert.Equal(PinnedRevision, formula.CompositionRevision);
        Assert.Equal(ScoreFormulaVersions.V10, formula.Version);
        Assert.Equal($"{ScoreFormulaVersions.V10}@{PinnedRevision}", FormulaIdentity.Of(formula));

        // …and the formulas that declare NO revision compose to their bare token, which is what keeps every
        // v8/v9 stamp and every pinned fingerprint byte-identical.
        var attention = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);
        Assert.Equal(
            ScoreFormulaVersions.V8,
            FormulaIdentity.Of(new RadarScoreFormulaV8(new ScoringWeights(), attention)));
        Assert.Equal(
            ScoreFormulaVersions.V9,
            FormulaIdentity.Of(new RadarScoreFormulaV9(new ScoringWeights(), attention, Channels())));
    }

    [Fact]
    public async Task V10Composition_AndTheStampItProduces_ArePinnedTogether()
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
            new RadarScoreFormulaV10(weights, attention, Channels()),
            weights,
            attention,
            new StubSourceDescriptor(),
            new InsiderMaterialityWeights(),
            new MediaAttentionCollapse(new MediaCollapseOptions()),
            // The CODE DEFAULT window (30 days) — the same one ScoringConfigFingerprintTests' pins use, so
            // the stamp below is directly comparable with the documented AI-OFF/AI-ON default pins.
            new ScoringOptions(),
            NullLogger<ScoringEngine>.Instance,
            strategyName: "v10-guard",
            channels: Channels());

        await companies.AddAsync(
            new CompanyBuilder().WithId(CompanyId).WithFollowingTier(FollowingTier.Large).Build(),
            CancellationToken.None);

        // "filings": MIXED directional mass, so the direction factor is genuinely exercised.
        await SeedAsync(
            signals, evidence, FilingSignalId, FilingEvidenceId, "sec-edgar", SignalType.GuidanceChange,
            SignalDirection.Positive, strength: 8, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-3));
        await SeedAsync(
            signals, evidence, PressSignalId, PressEvidenceId, "sec-edgar", SignalType.CustomerWin,
            SignalDirection.Negative, strength: 5, EvidenceSourceType.PressRelease, "Acme Newsroom",
            EvidenceQuality.High, WindowEnd.AddDays(-10));

        // "insider": ALL NEUTRAL — the routine-Form-4 shape. This is what v10 zeroes and v9 did not.
        await SeedAsync(
            signals, evidence, InsiderOneSignalId, InsiderOneEvidenceId, "sec-form4", SignalType.InsiderBuying,
            SignalDirection.Neutral, strength: 4, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-6));
        await SeedAsync(
            signals, evidence, InsiderTwoSignalId, InsiderTwoEvidenceId, "sec-form4", SignalType.InsiderBuying,
            SignalDirection.Neutral, strength: 3, EvidenceSourceType.Filing, "SEC EDGAR",
            EvidenceQuality.PrimarySource, WindowEnd.AddDays(-12));

        // "attention": two genuine third-party outlets, 5 days apart so the spec-109 same-event collapse
        // (3-day window) leaves both standing and Attention is non-zero.
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

        // ---- PIN 3: the identity a v10 strategy stamps ------------------------------------------------
        Assert.Equal($"mvp-engine-v1+{ScoreFormulaVersions.V10}@{PinnedRevision}", snapshot.ScoringVersion);
        Assert.Equal(PinnedScoringConfigVersion, snapshot.ScoringConfigVersion);
        Assert.Equal(
            $"{ScoreFormulaVersions.V10}@{PinnedRevision}", engine.EffectiveConfig.FormulaVersion);
    }

    // ---------------------------------------------------------------------------------------------------
    // THE PINS. Moving any of them without a CompositionRevision bump is the failure this file prevents.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>PIN 1 — the composition revision.</summary>
    private const string PinnedRevision = "rev1";

    // NOTE, and it is the whole point of the slice: this fixture is IDENTICAL to
    // RadarScoreFormulaV9OutputStabilityTests', which pins Opportunity 22. Under v10 it is 9 — because the
    // all-Neutral `insider` channel drops from 0.353 to exactly 0.000, and the mixed `filings` channel drops
    // from 0.438 to 0.128 (its preponderance is 0.172, and v10 uses that directly instead of 0.5 + 0.5·0.172).
    // v10 scores are on a LOWER ABSOLUTE SCALE than v9's; only rankings are comparable between them.
    private const int PinnedTrajectory = 57;
    private const int PinnedOpportunity = 9;
    private const int PinnedAttention = 42;
    private const int PinnedEvidenceConfidence = 80;
    private const int PinnedSignalVelocity = 100;

    private const string PinnedExplanation =
        "radar-formula-v10: 6 signal(s) over 30d across 3 channel(s) → Opportunity 9 (composite 0.178 = "
            + "attention 0.423×0.30, filings 0.128×0.40, insider 0.000×0.30; × notedness 0.532); "
            + "Trajectory 57, Attention 42, Confidence 80, Velocity 100.";

    private const string PinnedComponentJson =
        "{\"TrajectoryScore\":57,\"OpportunityScore\":9,\"AttentionScore\":42,"
            + "\"EvidenceConfidenceScore\":80,\"SignalVelocityScore\":100,"
            + "\"Formula\":\"radar-formula-v10\",\"Revision\":\"rev1\","
            + "\"Composite\":0.17829625953931263,\"Discount\":0.532,\"Channels\":["
            + "{\"Name\":\"attention\",\"Kind\":\"breadth\",\"Weight\":0.3,\"Saturation\":3,"
            + "\"Score\":0.4230769230769231,\"WeightedContribution\":0.12692307692307692,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[],\"CollectorsRan\":[],"
            + "\"CollectorsNotRun\":[],\"RecordedSignals\":2,\"InferredSignals\":0,"
            + "\"UnattributedSignals\":0,\"Preponderance\":null,\"DirectionalMass\":null,"
            + "\"DirectionState\":null},"
            + "{\"Name\":\"filings\",\"Kind\":\"collector\",\"Weight\":0.4,\"Saturation\":3,"
            + "\"Score\":0.1284329565405893,\"WeightedContribution\":0.051373182616235726,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[\"sec-edgar\"],"
            + "\"CollectorsRan\":[\"sec-edgar\"],\"CollectorsNotRun\":[],\"RecordedSignals\":2,"
            + "\"InferredSignals\":0,\"UnattributedSignals\":0,"
            + "\"Preponderance\":0.17166020444131122,\"DirectionalMass\":8.913333333333334,"
            + "\"DirectionState\":\"positive\"},"
            + "{\"Name\":\"insider\",\"Kind\":\"collector\",\"Weight\":0.3,\"Saturation\":2,"
            + "\"Score\":0,\"WeightedContribution\":0,"
            + "\"SignalCount\":2,\"Dark\":false,\"Collectors\":[\"sec-form4\"],\"CollectorsRan\":[],"
            + "\"CollectorsNotRun\":[\"sec-form4\"],\"RecordedSignals\":2,\"InferredSignals\":0,"
            + "\"UnattributedSignals\":0,\"Preponderance\":0,\"DirectionalMass\":0,"
            + "\"DirectionState\":\"none\"}]}";

    /// <summary>
    /// PIN 3 — the stamp a default-weights, default-window (30d) v10 strategy with THIS channel budget
    /// produces. It moves if the composition revision moves, if a hashed weight moves, or if the budget
    /// moves — which is exactly the coupling the revision mechanism exists to create.
    /// </summary>
    private const string PinnedScoringConfigVersion = "radar-scoring-fp-d89b8bc81815";

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
    /// A FIXED AI-OFF signal-source identity descriptor, with an asymmetric enabled-collector vocabulary so
    /// the ran/not-run split is pinned too. Neither is a scoring input.
    /// <para>
    /// The literal is deliberately FROZEN at <c>radar-keyword-rules-v6</c> and is NOT read from
    /// <c>KeywordSignalExtractor.RuleSetVersion</c>: this file pins a FORMULA COMPOSITION, so an unrelated
    /// extractor rule-set bump (spec 191 moved the shipped value to <c>v7</c>) must not move the fingerprint
    /// pinned below. <c>ScoringConfigFingerprintTests</c> is where the SHIPPED rule-set identity is pinned.
    /// </para>
    /// </summary>
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "rules=radar-keyword-rules-v6;";

        public string CollectionProvenance() => "collectors=sec-edgar;";

        public IReadOnlyList<string> EnabledCollectors() => ["sec-edgar"];
    }
}
