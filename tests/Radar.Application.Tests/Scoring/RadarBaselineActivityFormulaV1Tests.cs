using System.Text.Json;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 154 — <c>radar-baseline-activity-v1</c>, the CONTROL: a collector channel scores the saturated plain
/// COUNT of the signals it consumed, with no direction, no notedness and no quality weighting.
/// <para>
/// Modelled on <see cref="RadarScoreFormulaV10Tests"/>, and deliberately asserting each "no X" claim <b>as a
/// difference against a composite formula on the SAME fixture</b> rather than as a bare equality — an equality
/// alone would also pass if the fixture never exercised X in the first place.
/// </para>
/// </summary>
public sealed class RadarBaselineActivityFormulaV1Tests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

    private sealed class FuncWeights(Func<string?, double> fn) : IAttentionSourceWeights
    {
        public double WeightFor(string? sourceName) => fn(sourceName);
        public string CanonicalDescriptor() => "test-func-weights";
    }

    /// <summary>Every publisher counts as a full genuine outlet, so reach == distinct-publisher count.</summary>
    private static readonly IAttentionSourceWeights AllGenuine = new FuncWeights(_ => 1.0);

    private static RadarBaselineActivityFormulaV1 Baseline(params ScoringChannel[] channels) =>
        new(new ScoringWeights(), AllGenuine, ScoringChannelSet.Create(channels, "baseline-test"));

    private static RadarScoreFormulaV10 V10(params ScoringChannel[] channels) =>
        new(new ScoringWeights(), AllGenuine, ScoringChannelSet.Create(channels, "baseline-test"));

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
        int count,
        SignalDirection direction,
        string collector = "sec-form4",
        int strength = 6,
        decimal confidence = 0.8m,
        EvidenceQuality quality = EvidenceQuality.High) =>
        Enumerable.Range(0, count)
            .Select(_ => BuildSignal(
                collector,
                strength: strength,
                direction: direction,
                confidence: confidence,
                quality: quality))
            .ToList();

    // ---------------------------------------------------------------------------------------------------
    // Identity / construction
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Version_IsTheBaselineToken_AndTheExplanationSaysItIsAControl()
    {
        var formula = Baseline(ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2));

        Assert.Equal("radar-baseline-activity-v1", formula.Version);
        Assert.Equal(ScoreFormulaVersions.BaselineActivityV1, formula.Version);
        Assert.Equal("rev1", formula.CompositionRevision);

        var result = formula.Compute(InputFrom([BuildSignal("sec-form4")]));

        Assert.Contains("radar-baseline-activity-v1", result.Explanation, StringComparison.Ordinal);
        // Spec 154 §3: a baseline must say what it is wherever it surfaces, and the explanation is what a
        // human reads on a snapshot.
        Assert.Contains("BASELINE CONTROL", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullsAndAnEmptyChannelSet()
    {
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2)], "s");

        Assert.Throws<ArgumentNullException>(
            () => new RadarBaselineActivityFormulaV1(null!, AllGenuine, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarBaselineActivityFormulaV1(new ScoringWeights(), null!, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarBaselineActivityFormulaV1(new ScoringWeights(), AllGenuine, null!));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RadarBaselineActivityFormulaV1(
                new ScoringWeights(), AllGenuine, ScoringChannelSet.Empty));
        Assert.Contains(
            ScoreFormulaVersions.BaselineActivityV1, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABreadthChannel_IsRejectedAtConstruction_NamingTheChannelAndTheReason()
    {
        // DECIDED, NOT OVERLOOKED (see the class remarks on RadarBaselineActivityFormulaV1). The shared pass
        // CAN score a breadth channel — v10 does — but its reach is TIER-WEIGHTED, i.e. a quality weighting,
        // and this control's whole claim is that it applies none. A baseline that quietly measured something
        // other than what it says would be worse than no baseline.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new RadarBaselineActivityFormulaV1(
                new ScoringWeights(),
                AllGenuine,
                ScoringChannelSet.Create(
                    [
                        ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
                        ScoringChannel.Breadth("attention", 0.5, 3),
                    ],
                    "confused-baseline")));

        Assert.Contains("attention", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tier-weighted", ex.Message, StringComparison.OrdinalIgnoreCase);
        // …and the SAME budget is perfectly legal under v10, so this is a property of the control, not of the
        // channel machinery.
        _ = V10(
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Breadth("attention", 0.5, 3));
    }

    // ---------------------------------------------------------------------------------------------------
    // THE control: a pure COUNT
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ChannelScore_IsTheSaturatedSignalCount_AndNothingElse()
    {
        var formula = Baseline(ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2));

        var result = formula.Compute(InputFrom(Signals(6, SignalDirection.Positive)));

        // 6 signals, saturation 2 ⇒ 6/(6+2) = 0.75, verifiable by hand from the persisted breakdown alone.
        var channel = Breakdown(result, "insider");
        Assert.Equal(6, channel.GetProperty("SignalCount").GetInt32());
        Assert.Equal(0.75, channel.GetProperty("Score").GetDouble(), 12);
        Assert.Equal(0.75, Composite(result), 12);
        Assert.Equal(75, result.Components.OpportunityScore);
    }

    [Fact]
    public void AStrongHighQualitySignal_AndAWeakLowQualityOne_CountExactlyTheSame()
    {
        // No strength, no confidence, no recency, no evidence quality. The two fixtures differ in every
        // per-signal magnitude the composite consumes and are identical in the only thing this control reads.
        var channel = ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2);

        var loud = Signals(
            3,
            SignalDirection.Positive,
            strength: 10,
            confidence: 1.0m,
            quality: EvidenceQuality.PrimarySource);
        var quiet = Signals(
            3,
            SignalDirection.Positive,
            strength: 1,
            confidence: 0.1m,
            quality: EvidenceQuality.Low);

        var loudResult = Baseline(channel).Compute(InputFrom(loud));
        var quietResult = Baseline(channel).Compute(InputFrom(quiet));

        Assert.Equal(
            Breakdown(loudResult, "insider").GetProperty("Score").GetDouble(),
            Breakdown(quietResult, "insider").GetProperty("Score").GetDouble());
        Assert.Equal(Composite(loudResult), Composite(quietResult));
        Assert.Equal(loudResult.Components.OpportunityScore, quietResult.Components.OpportunityScore);

        // The control, so the fixture is proven to exercise what it claims: under radar-formula-v10 the very
        // same two sets score differently.
        Assert.NotEqual(
            V10(channel).Compute(InputFrom(loud)).Components.OpportunityScore,
            V10(channel).Compute(InputFrom(quiet)).Components.OpportunityScore);
    }

    [Fact]
    public void AllNeutralSignals_ScoreExactlyTheSameAsAllPositiveOnes()
    {
        // NO DIRECTION. This is the axis radar-formula-v10 exists to get RIGHT, which is precisely why a
        // control that ignores it is worth running beside v10.
        var channel = ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2);

        var positive = InputFrom(Signals(5, SignalDirection.Positive));
        var neutral = InputFrom(Signals(5, SignalDirection.Neutral));
        var negative = InputFrom(Signals(5, SignalDirection.Negative));

        var p = Baseline(channel).Compute(positive);
        var n = Baseline(channel).Compute(neutral);
        var g = Baseline(channel).Compute(negative);

        Assert.Equal(Composite(p), Composite(n));
        Assert.Equal(Composite(p), Composite(g));
        Assert.Equal(p.Components.OpportunityScore, n.Components.OpportunityScore);
        Assert.Equal(p.Components.OpportunityScore, g.Components.OpportunityScore);
        Assert.True(p.Components.OpportunityScore > 0);

        // …while v10 separates exactly these three, which is what makes the equality above a finding rather
        // than an artefact of a fixture with no directional mass.
        Assert.True(V10(channel).Compute(positive).Components.OpportunityScore > 0);
        Assert.Equal(0, V10(channel).Compute(neutral).Components.OpportunityScore);
        Assert.Equal(0, V10(channel).Compute(negative).Components.OpportunityScore);

        // Deterioration is still REPORTED — in the (v8-meaning) Trajectory component, which this formula keeps.
        Assert.True(g.Components.TrajectoryScore < new ScoringWeights().TrajectoryNeutral);
    }

    // ---------------------------------------------------------------------------------------------------
    // The notedness discount is provably ABSENT
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FollowingTier.Small)]
    [InlineData(FollowingTier.Mid)]
    [InlineData(FollowingTier.Large)]
    [InlineData(FollowingTier.Mega)]
    public void TheCuratedFollowingTier_DoesNotMoveTheScore(FollowingTier tier)
    {
        // Two companies differing ONLY in FollowingTier. Under v9/v10 the tier damps the composed score
        // (spec 149); this control applies no discount at all, deliberately — see the class remarks.
        var channel = ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2);
        var signals = Signals(4, SignalDirection.Positive);

        var small = Baseline(channel).Compute(InputFrom(signals, tier: FollowingTier.Small));
        var actual = Baseline(channel).Compute(InputFrom(signals, tier: tier));

        Assert.Equal(small.Components.OpportunityScore, actual.Components.OpportunityScore);
        Assert.Equal(Composite(small), Composite(actual));

        // ComponentJson carries no Discount property, because there is no discount: recording a constant
        // 1.0 would imply a transform that does not exist.
        using var doc = JsonDocument.Parse(actual.ComponentJson);
        Assert.False(doc.RootElement.TryGetProperty("Discount", out _));

        // Opportunity IS composite × 100, with nothing between them.
        Assert.Equal(
            ScoreSignalMath.Clamp0To100(100.0 * Composite(actual)), actual.Components.OpportunityScore);
    }

    [Fact]
    public void TheCuratedFollowingTier_DoesMoveAV10Score_SoTheAbsenceAboveIsAFinding()
    {
        // The positive control for the theory above: the same fixture under radar-formula-v10, where a
        // Mega-tier company is discounted well below a Small-tier one at the default weights.
        var channel = ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2);
        var signals = Signals(4, SignalDirection.Positive);

        Assert.True(
            V10(channel).Compute(InputFrom(signals, tier: FollowingTier.Small)).Components.OpportunityScore
                > V10(channel).Compute(InputFrom(signals, tier: FollowingTier.Mega)).Components.OpportunityScore,
            "v10 must discount a Mega-tier company, or the baseline's tier-invariance proves nothing");
    }

    [Fact]
    public void MeasuredAttention_DoesNotMoveTheScoreEither()
    {
        // Attention enters v8 as an inverse Opportunity discount and v9/v10 as the notedness damping. Here it
        // is REPORTED and never consulted. The two fixtures consume an IDENTICAL set of channel signals; the
        // second merely adds widely-syndicated coverage that no declared channel budgets for.
        var channel = ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2);
        var consumed = Signals(4, SignalDirection.Positive);

        var unnoticed = InputFrom(consumed);
        var famous = InputFrom(consumed
            .Concat(Enumerable.Range(0, 8).Select(i => BuildSignal(
                "newssearch",
                type: SignalType.MediaAttention,
                sourceType: EvidenceSourceType.NewsArticle,
                sourceName: $"outlet-{i}")))
            .ToList());

        var a = Baseline(channel).Compute(unnoticed);
        var b = Baseline(channel).Compute(famous);

        // The fixture genuinely differs in measured attention…
        Assert.True(b.Components.AttentionScore > a.Components.AttentionScore);
        // …and the baseline's answer does not move.
        Assert.Equal(Composite(a), Composite(b));
        Assert.Equal(a.Components.OpportunityScore, b.Components.OpportunityScore);

        // The control: v10 damps the second one, because attention IS an input there.
        Assert.True(
            V10(channel).Compute(famous).Components.OpportunityScore
                < V10(channel).Compute(unnoticed).Components.OpportunityScore);
    }

    // ---------------------------------------------------------------------------------------------------
    // Everything the control still owes: provenance, budget discipline, legibility
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ADarkChannel_ContributesZero_AndTheSurvivingWeightsAreNotRenormalised()
    {
        // DO NOT "FIX" THIS BY RENORMALISING — the same invariant v9/v10 carry, unchanged here. A baseline
        // that renormalised would be measuring a different thing from the strategies it is compared against.
        var formula = Baseline(
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.5, 3));

        var result = formula.Compute(InputFrom(Signals(6, SignalDirection.Positive)));

        var insider = Breakdown(result, "insider").GetProperty("Score").GetDouble();
        Assert.Equal(0.75, insider, 12);
        Assert.Equal(0.0, Breakdown(result, "filings").GetProperty("Score").GetDouble());
        Assert.Equal(0.5 * insider, Composite(result), 12);
        Assert.NotEqual(insider, Composite(result));
    }

    [Fact]
    public void ADarkChannel_IsFlagged_AndIsTheOnlyWayACollectorChannelScoresZero()
    {
        // Under v10 an all-Neutral channel also scores 0, so Dark carries the difference. Under a PURE COUNT
        // the two coincide — score 0 ⟺ Dark for a collector channel — and that is recorded here rather than
        // left as an assumption a reader has to re-derive: a 0 on this formula is unambiguously "nothing
        // arrived", never "something arrived that said nothing".
        var formula = Baseline(
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.5, 3));

        var result = formula.Compute(InputFrom(Signals(4, SignalDirection.Neutral)));

        var alive = Breakdown(result, "insider");
        var dark = Breakdown(result, "filings");

        Assert.False(alive.GetProperty("Dark").GetBoolean());
        Assert.Equal(4, alive.GetProperty("SignalCount").GetInt32());
        Assert.True(
            alive.GetProperty("Score").GetDouble() > 0,
            "an all-Neutral channel is ACTIVITY, so this control scores it above zero");

        Assert.True(dark.GetProperty("Dark").GetBoolean());
        Assert.Equal(0, dark.GetProperty("SignalCount").GetInt32());
        Assert.Equal(0.0, dark.GetProperty("Score").GetDouble());
    }

    [Fact]
    public void EveryCurrentWindowSignal_StillEmitsAContributionLinkedToItsEvidence()
    {
        // Provenance is not relaxed because the score is simple: a score without evidence is invalid
        // (CLAUDE.md), and a baseline's snapshot has to survive the spec-53 zero-evidence-link exclusion in
        // the weekly report exactly as any other strategy's does.
        var formula = Baseline(ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2));

        var consumed = Signals(3, SignalDirection.Positive);
        var unconsumed = new[] { BuildSignal("sec-edgar"), BuildSignal(collector: null) };
        var all = consumed.Concat(unconsumed).ToList();

        var result = formula.Compute(InputFrom(all));

        Assert.Equal(5, result.Contributions.Count);
        Assert.Equal(all.Select(s => s.Signal.Id), result.Contributions.Select(c => c.SignalId));
        Assert.Equal(all.Select(s => s.Evidence.Id), result.Contributions.Select(c => c.EvidenceId));

        Assert.All(
            result.Contributions.Take(3),
            c => Assert.Contains("channel insider", c.ContributionReason, StringComparison.Ordinal));
        Assert.Contains(
            "not budgeted by this strategy", result.Contributions[3].ContributionReason, StringComparison.Ordinal);
        Assert.Contains(
            "no recorded collector", result.Contributions[4].ContributionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherFourComponents_MatchV8_OverTheSameGatedSet()
    {
        // Only OpportunityScore carries this formula's answer. Trajectory / Attention / EvidenceConfidence /
        // SignalVelocity keep their v8 values, so WeeklyReportActionPolicyV1's thresholds stay valid and a
        // baseline snapshot stays legible beside a composite one.
        var weights = new ScoringWeights();
        var v8 = new RadarScoreFormulaV8(weights, AllGenuine);
        var baseline = Baseline(
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.5, 3));

        var signals = new[]
        {
            BuildSignal("sec-edgar", direction: SignalDirection.Positive, strength: 7),
            BuildSignal("newssearch", sourceType: EvidenceSourceType.NewsArticle, sourceName: "reuters"),
            BuildSignal("sec-form4", direction: SignalDirection.Negative, strength: 3),
        };
        var input = InputFrom(signals, [BuildSignal("sec-form4").Signal]);

        var a = v8.Compute(input).Components;
        var b = baseline.Compute(input).Components;

        Assert.Equal(a.TrajectoryScore, b.TrajectoryScore);
        Assert.Equal(a.AttentionScore, b.AttentionScore);
        Assert.Equal(a.EvidenceConfidenceScore, b.EvidenceConfidenceScore);
        Assert.Equal(a.SignalVelocityScore, b.SignalVelocityScore);
        Assert.NotEqual(a.OpportunityScore, b.OpportunityScore);
    }

    [Fact]
    public void EveryChannelScore_AndTheComposite_StayInRange()
    {
        var formula = Baseline(
            ScoringChannel.Collector("chatty", ["rss"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));

        var result = formula.Compute(InputFrom(
            Signals(200, SignalDirection.Positive, "rss")
                .Concat(Signals(200, SignalDirection.Negative))
                .ToList()));

        using var doc = JsonDocument.Parse(result.ComponentJson);
        foreach (var channel in doc.RootElement.GetProperty("Channels").EnumerateArray())
        {
            Assert.InRange(channel.GetProperty("Score").GetDouble(), 0.0, 1.0);
        }

        Assert.InRange(Composite(result), 0.0, 1.0);
        Assert.InRange(result.Components.OpportunityScore, 0, 100);
    }

    [Fact]
    public void AnEmptyWindow_ScoresAllZeros_WithAValidExplanationAndNoContributions()
    {
        var result = Baseline(ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2))
            .Compute(InputFrom());

        Assert.Equal(new ScoreComponents(0, 0, 0, 0, 0), result.Components);
        Assert.NotEmpty(result.Explanation);
        Assert.Empty(result.Contributions);
        Assert.Equal(0.0, Composite(result));
    }

    [Fact]
    public void ComponentJson_StillDeserializesAsScoreComponents_AndNamesTheFormulaAndRevision()
    {
        var result = Baseline(ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2))
            .Compute(InputFrom([BuildSignal("sec-form4")]));

        Assert.Equal(result.Components, JsonSerializer.Deserialize<ScoreComponents>(result.ComponentJson));

        using var doc = JsonDocument.Parse(result.ComponentJson);
        Assert.Equal(
            ScoreFormulaVersions.BaselineActivityV1, doc.RootElement.GetProperty("Formula").GetString());
        Assert.Equal("rev1", doc.RootElement.GetProperty("Revision").GetString());
    }

    [Fact]
    public void Provenance_DistinguishesRanAndFoundNothing_FromDidNotRun()
    {
        var formula = Baseline(
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Collector("fda", ["fda"], 0.5, 3));

        var result = formula.Compute(InputFrom(current: [], enabledCollectors: ["sec-form4", "sec-edgar"]));

        var ranButQuiet = Breakdown(result, "insider");
        var neverRan = Breakdown(result, "fda");

        Assert.Equal(
            ["sec-form4"],
            ranButQuiet.GetProperty("CollectorsRan").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(ranButQuiet.GetProperty("CollectorsNotRun").EnumerateArray());
        Assert.Empty(neverRan.GetProperty("CollectorsRan").EnumerateArray());
        Assert.Equal(
            ["fda"], neverRan.GetProperty("CollectorsNotRun").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void LegacyEvidenceWithNoRecordedCollector_ContributesZero_AndDoesNotThrow()
    {
        var formula = Baseline(ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2));

        var result = formula.Compute(InputFrom([BuildSignal(collector: null), BuildSignal(collector: null)]));

        Assert.Equal(0.0, Breakdown(result, "insider").GetProperty("Score").GetDouble());
        Assert.True(Breakdown(result, "insider").GetProperty("Dark").GetBoolean());
        Assert.Equal(2, result.Contributions.Count);
    }

    [Fact]
    public void Compute_IsPureAndDeterministic()
    {
        var formula = Baseline(
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2),
            ScoringChannel.Collector("filings", ["sec-edgar"], 0.5, 3));

        var input = InputFrom(
            [BuildSignal("sec-form4"), BuildSignal("sec-edgar", sourceType: EvidenceSourceType.Filing)],
            enabledCollectors: ["sec-form4"]);

        var a = formula.Compute(input);
        var b = formula.Compute(input);

        Assert.Equal(a.Components, b.Components);
        Assert.Equal(a.Explanation, b.Explanation);
        Assert.Equal(a.ComponentJson, b.ComponentJson);
        Assert.Equal(
            a.Contributions.Select(c => (c.SignalId, c.EvidenceId, c.ContributionReason, c.ContributionWeight)),
            b.Contributions.Select(c => (c.SignalId, c.EvidenceId, c.ContributionReason, c.ContributionWeight)));
    }
}
