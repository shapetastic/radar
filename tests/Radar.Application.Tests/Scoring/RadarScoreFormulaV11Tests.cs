using System.Text.Json;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 157 — <c>radar-formula-v11</c>: directional-only collector saturation, so neutral volume can never
/// amplify a directional read (AD-16).
/// <para>
/// Modelled on <see cref="RadarScoreFormulaV10Tests"/>, and METAMORPHIC where the spec demands it: the core
/// assertions perturb an input (add Neutral signals — none, one, many; before, after, interleaved) and assert
/// an exact invariance, rather than pinning examples. The §2 contract is asserted at its three separate
/// levels, because they are three different guarantees: the collector channel score is EXACTLY invariant, the
/// final OpportunityScore may fall (via the notedness discount) but never rise, and the diagnostic components
/// are permitted to change and still count Neutral evidence in full.
/// </para>
/// </summary>
public sealed class RadarScoreFormulaV11Tests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

    private sealed class FuncWeights(Func<string?, double> fn) : IAttentionSourceWeights
    {
        public AttentionSourceResolution Resolve(string? sourceName) =>
            AttentionSourceResolution.Unclassified(fn(sourceName), sourceName ?? string.Empty);
        public string CanonicalDescriptor() => "test-func-weights";
    }

    /// <summary>Every publisher counts as a full genuine outlet, so reach == distinct-publisher count.</summary>
    private static readonly IAttentionSourceWeights AllGenuine = new FuncWeights(_ => 1.0);

    /// <summary>The spec-149 opt-out (both discount weights 0 ⇒ discount exactly 1.0), so a test that is
    /// about the CHANNEL SCORE is not also measuring notedness.</summary>
    private static ScoringWeights Undiscounted() => new()
    {
        OpportunityAttentionDiscountWeight = 0.0,
        FollowingTierDiscountWeight = 0.0,
    };

    private static RadarScoreFormulaV11 V11(params ScoringChannel[] channels) =>
        new(Undiscounted(), AllGenuine, ScoringChannelSet.Create(channels, "test-strategy"));

    private static RadarScoreFormulaV10 V10(params ScoringChannel[] channels) =>
        new(Undiscounted(), AllGenuine, ScoringChannelSet.Create(channels, "test-strategy"));

    private static ScoringSignal BuildSignal(
        string? collector,
        int strength = 6,
        SignalDirection direction = SignalDirection.Positive,
        decimal confidence = 0.8m,
        SignalType type = SignalType.CustomerWin,
        EvidenceQuality quality = EvidenceQuality.High,
        EvidenceSourceType sourceType = EvidenceSourceType.PressRelease,
        string sourceName = "Acme Newsroom",
        DateTimeOffset? observedAt = null)
    {
        var metadata = collector is null
            ? null
            : EvidenceMetadata.Compose(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CollectionProvenanceMetadata.MetadataKey] = collector,
                },
                []);

        var evidence = new EvidenceBuilder()
            .WithQuality(quality)
            .WithSourceType(sourceType)
            .WithSourceName(sourceName)
            .WithMetadataJson(metadata)
            .Build();

        var signal = new SignalBuilder()
            .WithEvidenceId(evidence.Id)
            .WithStrength(strength)
            .WithDirection(direction)
            .WithConfidence(confidence)
            .WithType(type)
            .WithObservedAtUtc(observedAt ?? new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero))
            .Build();

        return new ScoringSignal(signal, evidence);
    }

    private static ScoringInput InputFrom(
        IReadOnlyList<ScoringSignal>? current = null,
        IReadOnlyList<Signal>? previous = null,
        IReadOnlyList<string>? enabledCollectors = null,
        FollowingTier tier = FollowingTier.Small) => new(
        CompanyId: Guid.NewGuid(),
        WindowStartUtc: WindowStart,
        WindowEndUtc: WindowEnd,
        Signals: current ?? Array.Empty<ScoringSignal>(),
        PreviousSignals: previous ?? Array.Empty<Signal>(),
        FollowingTier: tier)
    {
        EnabledCollectors = enabledCollectors ?? Array.Empty<string>(),
    };

    private static JsonElement Breakdown(ScoreComputation result, string channelName) =>
        JsonDocument.Parse(result.ComponentJson).RootElement
            .GetProperty("Channels")
            .EnumerateArray()
            .Single(c => c.GetProperty("Name").GetString() == channelName);

    private static double Composite(ScoreComputation result) =>
        JsonDocument.Parse(result.ComponentJson).RootElement.GetProperty("Composite").GetDouble();

    private static IReadOnlyList<ScoringSignal> Signals(
        int count, SignalDirection direction, string collector = "sec-edgar", int strength = 6) =>
        Enumerable.Range(0, count)
            .Select(i => BuildSignal(
                collector,
                strength: strength,
                direction: direction,
                // Spread the observations, so recency genuinely varies and the invariance below is not an
                // artefact of every signal sharing one recency factor.
                observedAt: new DateTimeOffset(2026, 1, 5 + (i % 20), 0, 0, 0, TimeSpan.Zero)))
            .ToList();

    /// <summary>A directional fixture with something to protect: positive AND negative mass.</summary>
    private static IReadOnlyList<ScoringSignal> DirectionalFixture() =>
    [
        BuildSignal("sec-edgar", strength: 8, direction: SignalDirection.Positive,
            observedAt: new DateTimeOffset(2026, 1, 28, 0, 0, 0, TimeSpan.Zero)),
        BuildSignal("sec-edgar", strength: 6, direction: SignalDirection.Positive,
            observedAt: new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero)),
        BuildSignal("sec-edgar", strength: 4, direction: SignalDirection.Negative,
            observedAt: new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero)),
    ];

    // ---------------------------------------------------------------------------------------------------
    // Identity / construction
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Version_IsRadarFormulaV11_AndAppearsInExplanation()
    {
        var formula = V11(ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3));

        Assert.Equal("radar-formula-v11", formula.Version);
        Assert.Equal(ScoreFormulaVersions.V11, formula.Version);

        var result = formula.Compute(InputFrom([BuildSignal("sec-edgar")]));
        Assert.Contains("radar-formula-v11", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullsAndAnEmptyChannelSet_NamingV11()
    {
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Collector("f", ["sec-edgar"], 1.0, 3)], "s");

        Assert.Throws<ArgumentNullException>(() => new RadarScoreFormulaV11(null!, AllGenuine, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarScoreFormulaV11(new ScoringWeights(), null!, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarScoreFormulaV11(new ScoringWeights(), AllGenuine, null!));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV11(new ScoringWeights(), AllGenuine, ScoringChannelSet.Empty));
        Assert.Contains(ScoreFormulaVersions.V11, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsABreadthChannel_CitingSpec158AndTheFindingsDoc()
    {
        // Spec 157 §3 as amended: no legal v11 configuration may declare breadth. The message must carry the
        // finding (spec 158) and where it is recorded, so the operator is pointed at WHY rather than at a
        // bare rule.
        var channels = ScoringChannelSet.Create(
            [
                ScoringChannel.Collector("filings", ["sec-edgar"], 0.8, 3),
                ScoringChannel.Breadth("attention", 0.2, 3),
            ],
            "breadth-declared");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV11(new ScoringWeights(), AllGenuine, channels));

        Assert.Contains("attention", ex.Message, StringComparison.Ordinal);
        Assert.Contains("spec 158", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            "docs/158-channel-feasibility-findings.md", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.V10, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    // THE slice, level 1 of the §2 contract: the collector channel score is EXACTLY invariant to Neutral
    // additions — metamorphic, not example-based
    // ---------------------------------------------------------------------------------------------------

    public enum NeutralPlacement { Before, After, Interleaved }

    [Theory]
    [InlineData(0, NeutralPlacement.After)]
    [InlineData(1, NeutralPlacement.Before)]
    [InlineData(1, NeutralPlacement.After)]
    [InlineData(1, NeutralPlacement.Interleaved)]
    [InlineData(6, NeutralPlacement.Before)]
    [InlineData(6, NeutralPlacement.After)]
    [InlineData(6, NeutralPlacement.Interleaved)]
    [InlineData(25, NeutralPlacement.Interleaved)]
    public void AddingNeutralSignals_LeavesTheCollectorChannelScore_ExactlyEqual(
        int neutralCount, NeutralPlacement placement)
    {
        // THE AD-16 PROPERTY, asserted bit-for-bit (Assert.Equal on double is exact equality): however many
        // Neutral signals arrive, and wherever they land relative to the directional ones, the channel score,
        // its weighted contribution and the composite do not move by an ULP. This is the exact opposite of
        // v10, whose own pinned test (NeutralCoverage_StillAmplifiesAGenuineDirectionalRead) shows the score
        // RISING on the same perturbation — that test stays untouched, because v10 is the control.
        var channel = ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3);
        var directional = DirectionalFixture();
        var neutrals = Signals(neutralCount, SignalDirection.Neutral);

        IReadOnlyList<ScoringSignal> perturbed = placement switch
        {
            NeutralPlacement.Before => [.. neutrals, .. directional],
            NeutralPlacement.After => [.. directional, .. neutrals],
            _ => [
                .. directional.Take(1), .. neutrals.Take(neutralCount / 2),
                .. directional.Skip(1), .. neutrals.Skip(neutralCount / 2),
            ],
        };

        var bare = V11(channel).Compute(InputFrom(directional));
        var covered = V11(channel).Compute(InputFrom(perturbed));

        Assert.Equal(
            Breakdown(bare, "filings").GetProperty("Score").GetDouble(),
            Breakdown(covered, "filings").GetProperty("Score").GetDouble());
        Assert.Equal(
            Breakdown(bare, "filings").GetProperty("WeightedContribution").GetDouble(),
            Breakdown(covered, "filings").GetProperty("WeightedContribution").GetDouble());
        Assert.Equal(Composite(bare), Composite(covered));

        // …and with the notedness discount opted out (these weights) and no third-party publisher among the
        // neutrals, the whole OpportunityScore is unchanged too.
        Assert.Equal(bare.Components.OpportunityScore, covered.Components.OpportunityScore);

        // The invariance is on the SCORE, not the record: the covered channel visibly consumed more.
        Assert.Equal(
            directional.Count + neutralCount,
            Breakdown(covered, "filings").GetProperty("SignalCount").GetInt32());
    }

    [Fact]
    public void NeutralCoverage_DoesNotAmplifyADirectionalRead_WhereV10Does()
    {
        // The v10/v11 difference asserted SIDE BY SIDE over the same fixture — the shape of spec 153's own
        // "difference, not claim" tests. Same signals, same budget: v10's channel score rises with neutral
        // coverage (all-signal saturation), v11's is bit-identical.
        var channel = ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 8);
        var directional = Signals(2, SignalDirection.Positive);
        var covered = directional.Concat(Signals(6, SignalDirection.Neutral)).ToList();

        var v11Bare = V11(channel).Compute(InputFrom(directional));
        var v11Covered = V11(channel).Compute(InputFrom(covered));
        var v10Bare = V10(channel).Compute(InputFrom(directional));
        var v10Covered = V10(channel).Compute(InputFrom(covered));

        Assert.Equal(
            Breakdown(v11Bare, "filings").GetProperty("Score").GetDouble(),
            Breakdown(v11Covered, "filings").GetProperty("Score").GetDouble());
        Assert.True(
            Breakdown(v10Covered, "filings").GetProperty("Score").GetDouble()
                > Breakdown(v10Bare, "filings").GetProperty("Score").GetDouble(),
            "v10 is the control: its saturation must still rise on neutral coverage");
    }

    // ---------------------------------------------------------------------------------------------------
    // Level 2 of the §2 contract: OpportunityScore never RISES on a Neutral addition — including the case
    // that motivated the breadth rejection, a neutral-only publisher
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NeutralAdditions_NeverIncreaseOpportunityScore_IncludingViaANeutralOnlyPublisher()
    {
        // DEFAULT weights (the notedness discount is live) and a Large-tier company, so the one legitimate
        // path by which Neutral evidence still moves the composite — more attention ⇒ deeper discount — is
        // genuinely exercised. The added signals are Neutral MediaAttention items from a publisher that
        // carries NOTHING ELSE (the neutral-only publisher of spec 157 §3): with breadth rejected, that
        // publisher can only deepen the discount, never earn a share.
        var weights = new ScoringWeights();
        var formula = new RadarScoreFormulaV11(
            weights,
            AllGenuine,
            ScoringChannelSet.Create(
                [ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3)], "disclosure"));

        var directional = DirectionalFixture();
        var neutralOnlyPublisher = Enumerable.Range(0, 4)
            .Select(i => BuildSignal(
                "newssearch",
                direction: SignalDirection.Neutral,
                type: SignalType.MediaAttention,
                sourceType: EvidenceSourceType.NewsArticle,
                sourceName: $"quiet-outlet-{i}",
                observedAt: new DateTimeOffset(2026, 1, 10 + i, 0, 0, 0, TimeSpan.Zero)))
            .ToList();

        var bare = formula.Compute(InputFrom(directional, tier: FollowingTier.Large));
        var covered = formula.Compute(
            InputFrom([.. directional, .. neutralOnlyPublisher], tier: FollowingTier.Large));

        // The channel score is still exactly invariant (level 1 holds under the discounted weights too)…
        Assert.Equal(
            Breakdown(bare, "filings").GetProperty("Score").GetDouble(),
            Breakdown(covered, "filings").GetProperty("Score").GetDouble());
        Assert.Equal(Composite(bare), Composite(covered));

        // …the attention DIAGNOSTIC rose (the neutral-only publisher is genuine third-party attention)…
        Assert.True(
            covered.Components.AttentionScore > bare.Components.AttentionScore,
            "the neutral-only publisher must raise the full-set AttentionScore diagnostic");

        // …so the discount deepened, and Opportunity moved DOWN or stayed put — never up.
        var bareDiscount =
            JsonDocument.Parse(bare.ComponentJson).RootElement.GetProperty("Discount").GetDouble();
        var coveredDiscount =
            JsonDocument.Parse(covered.ComponentJson).RootElement.GetProperty("Discount").GetDouble();
        Assert.True(coveredDiscount <= bareDiscount);
        Assert.True(
            covered.Components.OpportunityScore <= bare.Components.OpportunityScore,
            $"Neutral additions must never increase OpportunityScore "
                + $"({bare.Components.OpportunityScore} → {covered.Components.OpportunityScore})");
    }

    // ---------------------------------------------------------------------------------------------------
    // Level 3 of the §2 contract: the diagnostics may change, and Neutral evidence is NOT discarded
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NeutralEvidence_StillCountsTowardCoverageConfidenceAndVelocity_AndStaysInTheEvidenceTrail()
    {
        var formula = V11(ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3));

        var neutral = Signals(4, SignalDirection.Neutral);
        var result = formula.Compute(InputFrom(neutral));

        var channel = Breakdown(result, "filings");

        Assert.Equal(0.0, channel.GetProperty("Score").GetDouble());
        Assert.Equal(4, channel.GetProperty("SignalCount").GetInt32());
        Assert.False(channel.GetProperty("Dark").GetBoolean());
        Assert.True(
            result.Components.EvidenceConfidenceScore > 0,
            "neutral evidence is still evidence and still raises confidence");
        Assert.True(result.Components.SignalVelocityScore > 0);

        // Every neutral signal still emits its own contribution, naming the channel that consumed it, with
        // its evidence id intact — spec 157 blinds the score to neutral volume, never the evidence trail.
        Assert.Equal(4, result.Contributions.Count);
        Assert.Equal(neutral.Select(s => s.Signal.Id), result.Contributions.Select(c => c.SignalId));
        Assert.Equal(neutral.Select(s => s.Evidence.Id), result.Contributions.Select(c => c.EvidenceId));
        Assert.All(
            result.Contributions,
            c => Assert.Contains("channel filings", c.ContributionReason, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryScoredSignal_NeutralAndUnbudgetedIncluded_KeepsItsEvidenceLinkedContribution()
    {
        // The provenance invariant over a mixed window: directional, Neutral, and a signal no channel
        // budgets for — one contribution each, in input order, evidence ids intact.
        var formula = V11(ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3));

        var signals = new List<ScoringSignal>
        {
            BuildSignal("sec-edgar", direction: SignalDirection.Positive),
            BuildSignal("sec-edgar", direction: SignalDirection.Neutral),
            BuildSignal("usaspending", direction: SignalDirection.Neutral),
        };

        var result = formula.Compute(InputFrom(signals));

        Assert.Equal(signals.Count, result.Contributions.Count);
        Assert.Equal(signals.Select(s => s.Signal.Id), result.Contributions.Select(c => c.SignalId));
        Assert.Equal(signals.Select(s => s.Evidence.Id), result.Contributions.Select(c => c.EvidenceId));
        Assert.Contains(
            "not budgeted by this strategy",
            result.Contributions[2].ContributionReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnAllNeutralChannel_IsDistinguishableFromAnAbsentOne_AtTheSameScore()
    {
        // Same score, DIFFERENT RECORD — and under v11 the score separates them even less than under v10, so
        // Dark + SignalCount (+ DirectionState) carry the whole difference.
        var formula = V11(
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));

        var result = formula.Compute(InputFrom(Signals(4, SignalDirection.Neutral)));

        var neutral = Breakdown(result, "filings");
        var absent = Breakdown(result, "insider");

        Assert.Equal(0.0, neutral.GetProperty("Score").GetDouble());
        Assert.Equal(0.0, absent.GetProperty("Score").GetDouble());

        Assert.False(neutral.GetProperty("Dark").GetBoolean());
        Assert.Equal(4, neutral.GetProperty("SignalCount").GetInt32());
        Assert.Equal(ChannelDirectionState.None, neutral.GetProperty("DirectionState").GetString());

        Assert.True(absent.GetProperty("Dark").GetBoolean());
        Assert.Equal(0, absent.GetProperty("SignalCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------------
    // The diagnostics keep their v8 meanings, byte-identical to v10 over the same signals
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AttentionScore_AndTheOtherThreeDiagnostics_AreByteIdenticalToV10_OverTheSameSignals()
    {
        // The acceptance criterion spec 157 spells out for AttentionScore (AD-16's secondary comparator reads
        // it, so narrowing it would corrupt the comparator) — asserted for all four v8-meaning components,
        // because they share one guarantee: v11 changes the composite's saturation input and NOTHING else.
        var channels = new[]
        {
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.6, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.4, 2),
        };

        var signals = new List<ScoringSignal>
        {
            BuildSignal("sec-edgar", strength: 7, direction: SignalDirection.Positive),
            BuildSignal("sec-edgar", direction: SignalDirection.Neutral),
            BuildSignal("sec-form4", strength: 4, direction: SignalDirection.Neutral,
                sourceType: EvidenceSourceType.Filing, sourceName: "SEC EDGAR"),
            BuildSignal("newssearch", strength: 3, direction: SignalDirection.Neutral,
                type: SignalType.MediaAttention, sourceType: EvidenceSourceType.NewsArticle,
                sourceName: "Reuters"),
            BuildSignal("newssearch", strength: 2, direction: SignalDirection.Neutral,
                type: SignalType.MediaAttention, sourceType: EvidenceSourceType.NewsArticle,
                sourceName: "Bloomberg", observedAt: new DateTimeOffset(2026, 1, 22, 0, 0, 0, TimeSpan.Zero)),
        };
        var previous = new[] { BuildSignal("sec-edgar").Signal };
        var input = InputFrom(signals, previous, tier: FollowingTier.Large);

        var v10 = V10(channels).Compute(input).Components;
        var v11 = V11(channels).Compute(input).Components;

        Assert.Equal(v10.AttentionScore, v11.AttentionScore);
        Assert.Equal(v10.TrajectoryScore, v11.TrajectoryScore);
        Assert.Equal(v10.EvidenceConfidenceScore, v11.EvidenceConfidenceScore);
        Assert.Equal(v10.SignalVelocityScore, v11.SignalVelocityScore);
    }

    // ---------------------------------------------------------------------------------------------------
    // Composition properties v11 keeps from v10
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BalancedMass_ScoresExactlyZero_AndNetNegativeIsFlooredAtZero()
    {
        var formula = V11(ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3));

        var balanced = Signals(3, SignalDirection.Positive)
            .Concat(Signals(3, SignalDirection.Negative))
            .ToList();
        var balancedResult = formula.Compute(InputFrom(balanced));
        Assert.Equal(0.0, Breakdown(balancedResult, "filings").GetProperty("Score").GetDouble());
        Assert.Equal(
            ChannelDirectionState.Balanced,
            Breakdown(balancedResult, "filings").GetProperty("DirectionState").GetString());

        var negative = formula.Compute(InputFrom(Signals(5, SignalDirection.Negative)));
        Assert.Equal(0.0, Breakdown(negative, "filings").GetProperty("Score").GetDouble());
        Assert.Equal(0, negative.Components.OpportunityScore);
        Assert.True(
            negative.Components.TrajectoryScore < new ScoringWeights().TrajectoryNeutral,
            "deterioration is Trajectory's job, not a negative share");
    }

    [Fact]
    public void DarkChannel_ContributesZero_AndTheSurvivingWeightsAreNotRenormalised()
    {
        // DO NOT "FIX" THIS TEST BY RENORMALISING — the same invariant v9/v10 carry, unchanged in v11.
        var formula = V11(
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));

        var result = formula.Compute(InputFrom(
        [
            BuildSignal("sec-form4", direction: SignalDirection.Positive),
            BuildSignal("sec-form4", direction: SignalDirection.Positive),
        ]));

        var insider = Breakdown(result, "insider").GetProperty("Score").GetDouble();
        Assert.Equal(0.0, Breakdown(result, "filings").GetProperty("Score").GetDouble());
        Assert.Equal(0.5 * insider, Composite(result), 12);
        Assert.NotEqual(insider, Composite(result));
    }

    [Fact]
    public void AllComponents_StayInRange_ForAnEmptyWindow()
    {
        var formula = V11(
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.6, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.4, 2));

        var result = formula.Compute(InputFrom());

        Assert.Equal(new ScoreComponents(0, 0, 0, 0, 0), result.Components);
        Assert.NotEmpty(result.Explanation);
        Assert.Empty(result.Contributions);
        Assert.Equal(0.0, Composite(result));
    }

    [Fact]
    public void ComponentJson_StillDeserializesAsScoreComponents_AndCarriesFormulaAndRevision()
    {
        var formula = V11(ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3));

        var result = formula.Compute(InputFrom([BuildSignal("sec-edgar")]));

        Assert.Equal(result.Components, JsonSerializer.Deserialize<ScoreComponents>(result.ComponentJson));
        using var doc = JsonDocument.Parse(result.ComponentJson);
        Assert.Equal(ScoreFormulaVersions.V11, doc.RootElement.GetProperty("Formula").GetString());
        Assert.Equal("rev1", doc.RootElement.GetProperty("Revision").GetString());
    }

    [Fact]
    public void Compute_IsPureAndDeterministic()
    {
        var formula = V11(ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3));

        var input = InputFrom(
            [.. DirectionalFixture(), .. Signals(2, SignalDirection.Neutral)],
            enabledCollectors: ["sec-edgar"]);

        var a = formula.Compute(input);
        var b = formula.Compute(input);

        Assert.Equal(a.Components, b.Components);
        Assert.Equal(a.Explanation, b.Explanation);
        Assert.Equal(a.ComponentJson, b.ComponentJson);
        Assert.Equal(
            a.Contributions.Select(c => (c.SignalId, c.EvidenceId, c.ContributionReason, c.ContributionWeight)),
            b.Contributions.Select(c => (c.SignalId, c.EvidenceId, c.ContributionReason, c.ContributionWeight)));
    }

    [Fact]
    public void DirectionalMassInTheBreakdown_IsTheRawActivityThatFedSaturation()
    {
        // The reason v11's ComponentJson appends NO new field: for a v11 collector channel the recorded
        // DirectionalMass IS the saturation's raw input (DirectionalActivityMass ≡ DirectionalMasses().Total
        // over the same sub-slices), so Score = (m/(m+S)) · max(0, Preponderance) is verifiable by hand.
        var formula = V11(ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3));

        var result = formula.Compute(
            InputFrom([.. DirectionalFixture(), .. Signals(3, SignalDirection.Neutral)]));
        var channel = Breakdown(result, "filings");

        var mass = channel.GetProperty("DirectionalMass").GetDouble();
        var preponderance = channel.GetProperty("Preponderance").GetDouble();
        var saturation = channel.GetProperty("Saturation").GetDouble();

        Assert.Equal(
            (mass / (mass + saturation)) * Math.Max(0.0, preponderance),
            channel.GetProperty("Score").GetDouble());
    }
}
