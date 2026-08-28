using System.Text.Json;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 153 — <c>radar-formula-v10</c>: neutral evidence establishes COVERAGE but contributes no DIRECTIONAL
/// opportunity.
/// <para>
/// Modelled on <see cref="RadarScoreFormulaV9Tests"/> and deliberately asserting the v9-vs-v10 difference
/// SIDE BY SIDE wherever it exists, so the change is the assertion rather than a claim about it. The
/// assertions a future reader will be most tempted to "fix" are the ones proving that neutral evidence is
/// still counted everywhere EXCEPT direction — removing a directional contribution is not the same as
/// discarding the evidence.
/// </para>
/// </summary>
public sealed class RadarScoreFormulaV10Tests
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
    /// about the DIRECTION FACTOR is not also measuring notedness.</summary>
    private static ScoringWeights Undiscounted() => new()
    {
        OpportunityAttentionDiscountWeight = 0.0,
        FollowingTierDiscountWeight = 0.0,
    };

    private static RadarScoreFormulaV10 V10(params ScoringChannel[] channels) =>
        new(Undiscounted(), AllGenuine, ScoringChannelSet.Create(channels, "test-strategy"));

    private static RadarScoreFormulaV9 V9(params ScoringChannel[] channels) =>
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
        int count, SignalDirection direction, string collector = "patents", int strength = 6) =>
        Enumerable.Range(0, count)
            .Select(_ => BuildSignal(collector, strength: strength, direction: direction))
            .ToList();

    // ---------------------------------------------------------------------------------------------------
    // Identity / construction
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Version_IsRadarFormulaV10_AndAppearsInExplanation()
    {
        var formula = V10(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        Assert.Equal("radar-formula-v10", formula.Version);
        Assert.Equal(ScoreFormulaVersions.V10, formula.Version);

        var result = formula.Compute(InputFrom([BuildSignal("patents")]));
        Assert.Contains("radar-formula-v10", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullsAndAnEmptyChannelSet_NamingV10()
    {
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Collector("p", ["patents"], 1.0, 3)], "s");

        Assert.Throws<ArgumentNullException>(() => new RadarScoreFormulaV10(null!, AllGenuine, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarScoreFormulaV10(new ScoringWeights(), null!, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarScoreFormulaV10(new ScoringWeights(), AllGenuine, null!));

        // A v10 strategy with no channels could only ever score 0 — a misconfiguration, not a score. The
        // message names v10 so an operator is not sent looking at the wrong formula.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV10(new ScoringWeights(), AllGenuine, ScoringChannelSet.Empty));
        Assert.Contains(ScoreFormulaVersions.V10, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_InvalidWeight_Throws()
    {
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Collector("p", ["patents"], 1.0, 3)], "s");

        Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV10(
                new ScoringWeights { OpportunityAttentionDivisor = 0 }, AllGenuine, channels));
    }

    // ---------------------------------------------------------------------------------------------------
    // THE slice: no directional mass ⇒ exactly zero
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AllNeutralChannel_ContributesExactlyZeroUnderV10_ButHalfItsSaturatedShareUnderV9()
    {
        // THE SPEC-153 CHANGE, asserted as a difference rather than as a claim. Same fixture, same budget,
        // two formulas: v9 gives an all-Neutral channel saturation·0.5 (rising with activity — volume alone
        // producing score), v10 gives it exactly 0. Do not "fix" the v9 side; it is the control.
        var channel = ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2);
        var neutral = Signals(5, SignalDirection.Neutral, "sec-form4");
        var input = InputFrom(neutral);

        var v10 = V10(channel).Compute(input);
        var v9 = V9(channel).Compute(input);

        Assert.Equal(0.0, Breakdown(v10, "insider").GetProperty("Score").GetDouble());
        Assert.Equal(0.0, Breakdown(v10, "insider").GetProperty("WeightedContribution").GetDouble());
        Assert.Equal(0.0, Composite(v10));
        Assert.Equal(0, v10.Components.OpportunityScore);

        // …while v9's is a strictly positive half-share over the very same signals.
        var v9Score = Breakdown(v9, "insider").GetProperty("Score").GetDouble();
        Assert.True(v9Score > 0, "v9's all-Neutral channel is the control and must be > 0");
        Assert.True(v9.Components.OpportunityScore > 0);
    }

    [Fact]
    public void BalancedPositiveAndNegativeMass_AlsoContributesExactlyZero()
    {
        // The other route to preponderance 0, decided to score the same as "no directional mass at all":
        // both mean "no net evidence that this trajectory is improving". They differ in the evidence TRAIL
        // (DirectionState below), not in the score.
        var formula = V10(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var balanced = Signals(3, SignalDirection.Positive)
            .Concat(Signals(3, SignalDirection.Negative))
            .ToList();

        var result = formula.Compute(InputFrom(balanced));

        Assert.Equal(0.0, Breakdown(result, "patents").GetProperty("Score").GetDouble());
        Assert.Equal(0.0, Composite(result));
        Assert.Equal(0, result.Components.OpportunityScore);
    }

    [Fact]
    public void NetNegativeChannel_IsFlooredAtZero_AndTheCompositeIsNeverNegative()
    {
        // A negative channel share would SUBTRACT from other channels' genuine findings and break the [0,1]
        // share semantics the whole budget rests on. Deterioration is reported by TrajectoryScore, which v10
        // keeps at its v8 meaning — asserted here so the two facts sit together.
        var formula = V10(
            ScoringChannel.Collector("bad", ["patents"], 0.5, 3),
            ScoringChannel.Collector("good", ["sec-form4"], 0.5, 3));

        var signals = Signals(6, SignalDirection.Negative)
            .Concat(Signals(2, SignalDirection.Positive, "sec-form4"))
            .ToList();

        var result = formula.Compute(InputFrom(signals));

        var bad = Breakdown(result, "bad");
        var good = Breakdown(result, "good");

        Assert.Equal(0.0, bad.GetProperty("Score").GetDouble());
        Assert.Equal(0.0, bad.GetProperty("WeightedContribution").GetDouble());
        Assert.True(good.GetProperty("Score").GetDouble() > 0, "the positive channel still earns its share");

        // The negative channel takes nothing away from the positive one.
        Assert.Equal(
            good.GetProperty("WeightedContribution").GetDouble(), Composite(result), 12);
        Assert.True(Composite(result) > 0);
        Assert.InRange(result.Components.OpportunityScore, 0, 100);

        // …and the deterioration IS reported, in the (v8-meaning) Trajectory component.
        Assert.True(
            result.Components.TrajectoryScore < new ScoringWeights().TrajectoryNeutral,
            "net-negative evidence must show up as a below-neutral Trajectory, not as a negative share");
    }

    // ---------------------------------------------------------------------------------------------------
    // …but neutral evidence is NOT discarded
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void NeutralEvidence_StillCountsTowardCoverageAndConfidence_AndStaysInTheEvidenceTrail()
    {
        // THE spec's explicit criterion. This slice removes a DIRECTIONAL contribution, not the evidence: an
        // all-Neutral channel is covered, alive, confident and fully traceable — it simply says nothing about
        // whether the trajectory is improving.
        var formula = V10(ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2));

        var neutral = Signals(4, SignalDirection.Neutral, "sec-form4");
        var result = formula.Compute(InputFrom(neutral));

        var channel = Breakdown(result, "insider");

        Assert.Equal(0.0, channel.GetProperty("Score").GetDouble());
        Assert.Equal(4, channel.GetProperty("SignalCount").GetInt32());
        Assert.False(channel.GetProperty("Dark").GetBoolean());
        Assert.True(
            result.Components.EvidenceConfidenceScore > 0,
            "neutral evidence is still evidence and still raises confidence");
        Assert.True(result.Components.SignalVelocityScore > 0);

        // Every neutral signal still emits its own contribution, naming the channel that consumed it, with
        // its evidence id intact — provenance is unaffected by the directional decision.
        Assert.Equal(4, result.Contributions.Count);
        Assert.Equal(
            neutral.Select(s => s.Signal.Id), result.Contributions.Select(c => c.SignalId));
        Assert.Equal(
            neutral.Select(s => s.Evidence.Id), result.Contributions.Select(c => c.EvidenceId));
        Assert.All(
            result.Contributions,
            c => Assert.Contains("channel insider", c.ContributionReason, StringComparison.Ordinal));
    }

    [Fact]
    public void NeutralCoverage_StillAmplifiesAGenuineDirectionalRead()
    {
        // The distinction between "contributes no direction" and "is discarded". Neutral signals count as
        // ACTIVITY, so they raise the channel's saturation — a positive read with more neutral corroboration
        // around it scores HIGHER. If a future change made neutral signals invisible, this would fail.
        var formula = V10(ScoringChannel.Collector("patents", ["patents"], 1.0, 8));

        var directionalOnly = Signals(2, SignalDirection.Positive);
        var withNeutralCoverage = directionalOnly
            .Concat(Signals(6, SignalDirection.Neutral))
            .ToList();

        var bare = formula.Compute(InputFrom(directionalOnly));
        var covered = formula.Compute(InputFrom(withNeutralCoverage));

        var bareScore = Breakdown(bare, "patents").GetProperty("Score").GetDouble();
        var coveredScore = Breakdown(covered, "patents").GetProperty("Score").GetDouble();

        Assert.True(
            coveredScore > bareScore,
            $"neutral coverage {coveredScore} must amplify the directional read {bareScore}");
    }

    [Fact]
    public void AnAllNeutralChannel_IsDistinguishableFromADarkOne_AtTheSameScore()
    {
        // Same score, DIFFERENT RECORD. Under v9 these two were separable by score; under v10 they are not,
        // which makes Dark + SignalCount the only thing carrying the difference — so it is asserted.
        var formula = V10(
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3));

        var result = formula.Compute(InputFrom(Signals(4, SignalDirection.Neutral, "sec-form4")));

        var neutral = Breakdown(result, "insider");
        var absent = Breakdown(result, "patents");

        Assert.Equal(0.0, neutral.GetProperty("Score").GetDouble());
        Assert.Equal(0.0, absent.GetProperty("Score").GetDouble());

        Assert.False(neutral.GetProperty("Dark").GetBoolean());
        Assert.Equal(4, neutral.GetProperty("SignalCount").GetInt32());

        Assert.True(absent.GetProperty("Dark").GetBoolean());
        Assert.Equal(0, absent.GetProperty("SignalCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------------
    // The directional read recorded in the trail
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, ChannelDirectionState.None)]
    [InlineData(3, 3, ChannelDirectionState.Balanced)]
    [InlineData(4, 1, ChannelDirectionState.Positive)]
    [InlineData(1, 4, ChannelDirectionState.Negative)]
    public void DirectionState_ReportsWhichOfTheFourStatesTheChannelIsIn(
        int positives, int negatives, string expected)
    {
        // Provenance only — it never feeds a score. The `none` vs `balanced` split is the whole reason it
        // exists: both land at preponderance 0 and therefore at the same channel score.
        var formula = V10(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var signals = Signals(positives, SignalDirection.Positive)
            .Concat(Signals(negatives, SignalDirection.Negative))
            // Always some neutral activity, so `none` is genuinely "no DIRECTIONAL mass" and not "no signals".
            .Concat(Signals(2, SignalDirection.Neutral))
            .ToList();

        var channel = Breakdown(formula.Compute(InputFrom(signals)), "patents");

        Assert.Equal(expected, channel.GetProperty("DirectionState").GetString());
        Assert.False(channel.GetProperty("Dark").GetBoolean());

        var mass = channel.GetProperty("DirectionalMass").GetDouble();
        var preponderance = channel.GetProperty("Preponderance").GetDouble();

        if (expected == ChannelDirectionState.None)
        {
            Assert.Equal(0.0, mass);
            Assert.Equal(0.0, preponderance);
        }
        else if (expected == ChannelDirectionState.Balanced)
        {
            // The state that only the MASS can distinguish from `none`: directional mass IS present.
            Assert.True(mass > 0, "balanced means directional mass exists and cancels");
            Assert.Equal(0.0, preponderance);
        }
    }

    [Fact]
    public void BreadthChannel_ReportsNoDirectionalRead_BecauseItNeverTakesOne()
    {
        // A breadth channel's score is reach saturation; direction is never consulted. Recording a measured
        // "none" for it would claim a reading that was never taken, so the three directional properties are
        // explicitly null.
        var formula = V10(ScoringChannel.Breadth("attention", 1.0, 3));

        var result = formula.Compute(InputFrom(
            Enumerable.Range(0, 4)
                .Select(i => BuildSignal(
                    "newssearch",
                    sourceType: EvidenceSourceType.NewsArticle,
                    sourceName: $"outlet-{i}"))
                .ToList()));

        var channel = Breakdown(result, "attention");

        Assert.Equal(JsonValueKind.Null, channel.GetProperty("DirectionState").ValueKind);
        Assert.Equal(JsonValueKind.Null, channel.GetProperty("Preponderance").ValueKind);
        Assert.Equal(JsonValueKind.Null, channel.GetProperty("DirectionalMass").ValueKind);
    }

    // ---------------------------------------------------------------------------------------------------
    // Everything v10 deliberately keeps from v9
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BreadthChannel_IsUnchangedFromV9_ExactlyReachSaturation()
    {
        // Spec 153 changes the COLLECTOR direction factor only. The breadth channel is byte-identical to v9's
        // — a deliberate decision, with its tension recorded on RadarScoreFormulaV10: it still earns share
        // from pure coverage, which is adjacent to the problem this formula exists to fix, but it is an
        // explicitly BUDGETED measure of notice and is already damped by the notedness discount.
        var channel = ScoringChannel.Breadth("attention", 1.0, 3);
        var signals = Enumerable.Range(0, 3)
            .Select(i => BuildSignal(
                "newssearch", sourceType: EvidenceSourceType.NewsArticle, sourceName: $"outlet-{i}"))
            .ToList();
        var input = InputFrom(signals);

        var v10 = V10(channel).Compute(input);
        var v9 = V9(channel).Compute(input);

        // reach 3 (three distinct genuine publishers), saturation 3 ⇒ 3/(3+3) = 0.5, in BOTH formulas.
        Assert.Equal(0.5, Breakdown(v10, "attention").GetProperty("Score").GetDouble(), 12);
        Assert.Equal(
            Breakdown(v9, "attention").GetProperty("Score").GetDouble(),
            Breakdown(v10, "attention").GetProperty("Score").GetDouble());
        Assert.Equal(Composite(v9), Composite(v10));
    }

    [Fact]
    public void NotednessDiscount_IsAppliedOnceToTheComposite_ExactlyAsV9AppliesIt()
    {
        var weights = new ScoringWeights();
        var formula = new RadarScoreFormulaV10(
            weights,
            AllGenuine,
            ScoringChannelSet.Create(
                [
                    ScoringChannel.Collector("news", ["newssearch"], 0.6, 3),
                    ScoringChannel.Breadth("attention", 0.4, 3),
                ],
                "noted"));

        var signals = Enumerable.Range(0, 6)
            .Select(i => BuildSignal(
                "newssearch", sourceType: EvidenceSourceType.NewsArticle, sourceName: $"outlet-{i}"))
            .ToList();

        var result = formula.Compute(InputFrom(signals, tier: FollowingTier.Large));

        using var doc = JsonDocument.Parse(result.ComponentJson);
        var composite = doc.RootElement.GetProperty("Composite").GetDouble();
        var discount = doc.RootElement.GetProperty("Discount").GetDouble();

        Assert.Equal(
            ScoreSignalMath.NotednessDiscount(
                weights, result.Components.AttentionScore, FollowingTier.Large),
            discount);
        Assert.Equal(
            ScoreSignalMath.Clamp0To100(100.0 * composite * discount), result.Components.OpportunityScore);

        // The per-channel contributions still sum to the UNdiscounted composite: the discount is not smuggled
        // into per-channel provenance.
        var summed = doc.RootElement.GetProperty("Channels").EnumerateArray()
            .Sum(c => c.GetProperty("WeightedContribution").GetDouble());
        Assert.Equal(summed, composite, 12);
    }

    [Fact]
    public void DarkChannel_ContributesZero_AndTheSurvivingWeightsAreNotRenormalised()
    {
        // DO NOT "FIX" THIS TEST BY RENORMALISING — the same invariant v9 carries, unchanged in v10.
        var formula = V10(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));

        var result = formula.Compute(InputFrom(Signals(3, SignalDirection.Positive, "sec-form4")));

        var insider = Breakdown(result, "insider").GetProperty("Score").GetDouble();
        Assert.Equal(0.0, Breakdown(result, "patents").GetProperty("Score").GetDouble());
        Assert.Equal(0.5 * insider, Composite(result), 12);
        Assert.NotEqual(insider, Composite(result));
    }

    [Fact]
    public void EveryChannelScore_AndTheComposite_StayInZeroToOne()
    {
        var formula = V10(
            ScoringChannel.Collector("chatty", ["rss"], 0.5, 3),
            ScoringChannel.Breadth("attention", 0.5, 3));

        var signals = Enumerable.Range(0, 40)
            .Select(i => BuildSignal(
                "rss", sourceType: EvidenceSourceType.NewsArticle, sourceName: $"outlet-{i}"))
            .ToList();

        var result = formula.Compute(InputFrom(signals));

        using var doc = JsonDocument.Parse(result.ComponentJson);
        foreach (var channel in doc.RootElement.GetProperty("Channels").EnumerateArray())
        {
            Assert.InRange(channel.GetProperty("Score").GetDouble(), 0.0, 1.0);
        }

        Assert.InRange(Composite(result), 0.0, 1.0);
        Assert.InRange(result.Components.OpportunityScore, 0, 100);
    }

    [Fact]
    public void AllComponents_StayInZeroToOneHundred_ForAnEmptyWindow()
    {
        // Mirrors v8's and v9's empty-window behaviour exactly: all zeros, a valid explanation and
        // ComponentJson, and no contributions.
        var formula = V10(
            ScoringChannel.Collector("patents", ["patents"], 0.6, 3),
            ScoringChannel.Breadth("attention", 0.4, 3));

        var result = formula.Compute(InputFrom());

        Assert.Equal(new ScoreComponents(0, 0, 0, 0, 0), result.Components);
        Assert.NotEmpty(result.Explanation);
        Assert.Empty(result.Contributions);
        Assert.Equal(0.0, Composite(result));
    }

    [Fact]
    public void ComponentJson_StillDeserializesAsScoreComponents()
    {
        var formula = V10(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var result = formula.Compute(InputFrom([BuildSignal("patents")]));

        Assert.Equal(result.Components, JsonSerializer.Deserialize<ScoreComponents>(result.ComponentJson));
        using var doc = JsonDocument.Parse(result.ComponentJson);
        Assert.Equal(ScoreFormulaVersions.V10, doc.RootElement.GetProperty("Formula").GetString());
        Assert.Equal("rev1", doc.RootElement.GetProperty("Revision").GetString());
    }

    [Fact]
    public void LegacyEvidenceWithNoRecordedCollector_ContributesZero_AndDoesNotThrow()
    {
        var formula = V10(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var result = formula.Compute(InputFrom([BuildSignal(collector: null), BuildSignal(collector: null)]));

        Assert.Equal(0.0, Breakdown(result, "patents").GetProperty("Score").GetDouble());
        Assert.Equal(2, result.Contributions.Count);
        Assert.All(
            result.Contributions,
            c => Assert.Contains("no recorded collector", c.ContributionReason, StringComparison.Ordinal));
    }

    [Fact]
    public void Provenance_DistinguishesRanAndFoundNothing_FromDidNotRun()
    {
        var formula = V10(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("fda", ["fda"], 0.5, 3));

        var result = formula.Compute(InputFrom(current: [], enabledCollectors: ["patents", "sec-form4"]));

        var ranButQuiet = Breakdown(result, "patents");
        var neverRan = Breakdown(result, "fda");

        Assert.Equal(
            ["patents"], ranButQuiet.GetProperty("CollectorsRan").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(ranButQuiet.GetProperty("CollectorsNotRun").EnumerateArray());
        Assert.Empty(neverRan.GetProperty("CollectorsRan").EnumerateArray());
        Assert.Equal(
            ["fda"], neverRan.GetProperty("CollectorsNotRun").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Compute_IsPureAndDeterministic()
    {
        var formula = V10(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Breadth("attention", 0.5, 3));

        var input = InputFrom(
            [BuildSignal("patents"), BuildSignal("newssearch", sourceType: EvidenceSourceType.NewsArticle)],
            enabledCollectors: ["patents"]);

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
    public void TheOtherFourComponents_MatchV8_OverTheSameGatedSet()
    {
        // Only OpportunityScore changes meaning in a channel formula. Trajectory / Attention /
        // EvidenceConfidence / SignalVelocity keep their v8 values, so WeeklyReportActionPolicyV1's thresholds
        // stay valid for a v10 strategy and its snapshot stays legible next to a v8 one.
        var weights = Undiscounted();
        var v8 = new RadarScoreFormulaV8(weights, AllGenuine);
        var v10 = V10(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Breadth("attention", 0.5, 3));

        var signals = new[]
        {
            BuildSignal("patents", direction: SignalDirection.Positive, strength: 7),
            BuildSignal("newssearch", sourceType: EvidenceSourceType.NewsArticle, sourceName: "reuters"),
            BuildSignal("gdelt", direction: SignalDirection.Negative, strength: 3),
        };
        var input = InputFrom(signals, [BuildSignal("patents").Signal]);

        var a = v8.Compute(input).Components;
        var b = v10.Compute(input).Components;

        Assert.Equal(a.TrajectoryScore, b.TrajectoryScore);
        Assert.Equal(a.AttentionScore, b.AttentionScore);
        Assert.Equal(a.EvidenceConfidenceScore, b.EvidenceConfidenceScore);
        Assert.Equal(a.SignalVelocityScore, b.SignalVelocityScore);
    }

    // ---------------------------------------------------------------------------------------------------
    // The measured consequence, at the shape spec 153 was written about
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AFilingsLedStrategy_RankingRoutineFilingVolume_ScoresZeroUnderV10_AndPositivelyUnderV9()
    {
        // The concrete case from the spec: `filings-led` budgets sec-form4 (routine Form 4s extracted as
        // Neutral InsiderBuying) and sec-13dg (passive 13G filings, made Neutral BY DESIGN by spec 99 so they
        // never misfire bullish). Under v9 such a strategy was substantially ranking FILING VOLUME — and
        // larger companies file more. Under v10 it correctly reports that it has no evidence of improvement.
        var channels = new[]
        {
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Collector("ownership", ["sec-13dg"], 0.5, 2),
        };

        var routine = Signals(8, SignalDirection.Neutral, "sec-form4", strength: 4)
            .Concat(Signals(6, SignalDirection.Neutral, "sec-13dg", strength: 3))
            .ToList();
        var input = InputFrom(routine);

        var v10 = V10(channels).Compute(input);
        var v9 = V9(channels).Compute(input);

        Assert.Equal(0, v10.Components.OpportunityScore);
        Assert.True(
            v9.Components.OpportunityScore > 0,
            "the control: v9 turns pure filing volume into a positive Opportunity");

        // …and volume genuinely drove the v9 number: doubling it raises v9's score and leaves v10's at 0.
        var louder = routine.Concat(routine).ToList();
        Assert.True(
            V9(channels).Compute(InputFrom(louder)).Components.OpportunityScore
                > v9.Components.OpportunityScore);
        Assert.Equal(0, V10(channels).Compute(InputFrom(louder)).Components.OpportunityScore);
    }
}
