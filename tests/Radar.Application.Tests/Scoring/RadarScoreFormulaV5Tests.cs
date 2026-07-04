using System.Text.Json;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class RadarScoreFormulaV5Tests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// In-test <see cref="IAttentionSourceWeights"/> that counts every publisher as a full genuine outlet
    /// (weight 1.0). Under it the weighted-breadth sum equals the distinct-publisher count, so the ported
    /// behavioural tests keep their reach semantics.
    /// </summary>
    private static readonly IAttentionSourceWeights AllGenuine = new FuncWeights(_ => 1.0);

    private sealed class FuncWeights : IAttentionSourceWeights
    {
        private readonly Func<string?, double> _fn;
        public FuncWeights(Func<string?, double> fn) => _fn = fn;
        public double WeightFor(string? sourceName) => _fn(sourceName);
        public string CanonicalDescriptor() => "test-func-weights";
    }

    // A tiered fake matching the spec seed weights: names starting "mill" → 0.1, "genuine" → 1.0, anything
    // else (incl. blank/null) → the unknown default (0.5). Lets the pins control the tier of each publisher.
    private static IAttentionSourceWeights Tiered(double mill = 0.1, double genuine = 1.0, double unknown = 0.5) =>
        new FuncWeights(name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return unknown;
            if (name.StartsWith("mill", StringComparison.OrdinalIgnoreCase)) return mill;
            if (name.StartsWith("genuine", StringComparison.OrdinalIgnoreCase)) return genuine;
            return unknown;
        });

    // Convenience: construct the default-weights formula (byte-identical to v4) over the given source weights.
    private static RadarScoreFormulaV5 Formula(IAttentionSourceWeights sourceWeights) =>
        new(new ScoringWeights(), sourceWeights);

    private static ScoringSignal BuildSignal(
        int strength = 6,
        SignalDirection direction = SignalDirection.Positive,
        decimal confidence = 0.8m,
        SignalType type = SignalType.CustomerWin,
        EvidenceQuality quality = EvidenceQuality.High,
        EvidenceSourceType sourceType = EvidenceSourceType.PressRelease,
        string sourceName = "Acme Newsroom",
        DateTimeOffset? observedAt = null)
    {
        var evidence = new EvidenceBuilder()
            .WithQuality(quality)
            .WithSourceType(sourceType)
            .WithSourceName(sourceName)
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
        IReadOnlyList<Signal>? previous = null) => new(
        CompanyId: Guid.NewGuid(),
        WindowStartUtc: WindowStart,
        WindowEndUtc: WindowEnd,
        Signals: current ?? Array.Empty<ScoringSignal>(),
        PreviousSignals: previous ?? Array.Empty<Signal>());

    [Fact]
    public void Version_IsRadarFormulaV5_AndAppearsInExplanation()
    {
        var formula = Formula(AllGenuine);

        Assert.Equal("radar-formula-v5", formula.Version);

        var result = formula.Compute(InputFrom(new[] { BuildSignal() }));
        Assert.Contains("radar-formula-v5", result.Explanation);
    }

    [Fact]
    public void Constructor_NullWeights_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RadarScoreFormulaV5(null!, AllGenuine));
    }

    [Fact]
    public void Constructor_NullSourceWeights_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RadarScoreFormulaV5(new ScoringWeights(), null!));
    }

    [Fact]
    public void Constructor_InvalidWeight_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV5(new ScoringWeights { OpportunityAttentionDivisor = 0 }, AllGenuine));
    }

    [Fact]
    public void NeutralBaseline_SingleNeutralSignal_TrajectoryIs50()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(new[] { BuildSignal(direction: SignalDirection.Neutral) });

        var result = formula.Compute(input);

        Assert.Equal(50, result.Components.TrajectoryScore);
    }

    [Fact]
    public void AllPositive_Improves_AllNegative_Declines()
    {
        var formula = Formula(AllGenuine);

        var positive = formula.Compute(InputFrom(new[]
        {
            BuildSignal(direction: SignalDirection.Positive),
            BuildSignal(direction: SignalDirection.Positive),
        }));
        var negative = formula.Compute(InputFrom(new[]
        {
            BuildSignal(direction: SignalDirection.Negative),
            BuildSignal(direction: SignalDirection.Negative),
        }));

        Assert.True(positive.Components.TrajectoryScore > 50);
        Assert.True(negative.Components.TrajectoryScore < 50);
    }

    [Fact]
    public void MixedInput_AllComponentsInRange()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(
            new[]
            {
                BuildSignal(strength: 3, direction: SignalDirection.Positive),
                BuildSignal(strength: 7, direction: SignalDirection.Neutral, sourceName: "Second Source"),
                BuildSignal(strength: 9, direction: SignalDirection.Negative, sourceName: "Third Source",
                    type: SignalType.MediaAttention, sourceType: EvidenceSourceType.NewsArticle),
            },
            new[] { BuildSignal(strength: 5).Signal });

        var c = formula.Compute(input).Components;

        Assert.InRange(c.TrajectoryScore, 0, 100);
        Assert.InRange(c.OpportunityScore, 0, 100);
        Assert.InRange(c.AttentionScore, 0, 100);
        Assert.InRange(c.EvidenceConfidenceScore, 0, 100);
        Assert.InRange(c.SignalVelocityScore, 0, 100);
    }

    [Fact]
    public void ClampHoldsAtExtremes()
    {
        var formula = Formula(AllGenuine);

        var maxPositive = new List<ScoringSignal>();
        var maxNegative = new List<ScoringSignal>();
        for (var i = 0; i < 10; i++)
        {
            maxPositive.Add(BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                sourceName: $"src-{i}"));
            maxNegative.Add(BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Negative,
                sourceName: $"src-{i}"));
        }

        var positive = formula.Compute(InputFrom(maxPositive));
        var negative = formula.Compute(InputFrom(maxNegative));

        Assert.InRange(positive.Components.TrajectoryScore, 0, 100);
        Assert.InRange(negative.Components.TrajectoryScore, 0, 100);
        Assert.True(positive.Components.TrajectoryScore <= 100);
        Assert.True(negative.Components.TrajectoryScore >= 0);
    }

    [Fact]
    public void Trajectory_NeutralSignals_DoNotDilute_DirectionalRead()
    {
        var formula = Formula(AllGenuine);

        var positiveOnly = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 6, direction: SignalDirection.Positive, sourceName: "src-a"),
        }));

        var positiveWithNeutrals = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 6, direction: SignalDirection.Positive, sourceName: "src-a"),
            BuildSignal(strength: 9, direction: SignalDirection.Neutral, sourceName: "src-b"),
            BuildSignal(strength: 9, direction: SignalDirection.Neutral, sourceName: "src-c"),
        }));

        // Neutral signals are excluded from trajectory entirely, so the score is unchanged.
        Assert.Equal(
            positiveOnly.Components.TrajectoryScore,
            positiveWithNeutrals.Components.TrajectoryScore);
    }

    [Fact]
    public void Trajectory_OnlyNeutralSignals_Is50()
    {
        var formula = Formula(AllGenuine);

        var result = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 8, direction: SignalDirection.Neutral, sourceName: "src-a"),
            BuildSignal(strength: 4, direction: SignalDirection.Mixed, sourceName: "src-b"),
        }));

        Assert.Equal(50, result.Components.TrajectoryScore);
    }

    [Fact]
    public void Attention_FirstPartyOnly_IsZero_ThirdPartyRaisesIt()
    {
        var formula = Formula(AllGenuine);

        // A company's own press release + filing: both first-party → no market attention.
        var firstParty = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.PressRelease, sourceName: "Acme Newsroom"),
            BuildSignal(sourceType: EvidenceSourceType.Filing, sourceName: "SEC EDGAR"),
        }));

        Assert.Equal(0, firstParty.Components.AttentionScore);

        // Adding a third-party news source raises attention above zero.
        var withThirdParty = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.PressRelease, sourceName: "Acme Newsroom"),
            BuildSignal(sourceType: EvidenceSourceType.Filing, sourceName: "SEC EDGAR"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "The Ledger"),
        }));

        Assert.True(withThirdParty.Components.AttentionScore > 0);
    }

    [Fact]
    public void MediaAttentionSignals_OverNews_LiftAttention_LeaveTrajectoryAtBaseline()
    {
        var formula = Formula(AllGenuine);

        // The downstream payoff of spec 70: each NewsArticle evidence item now carries a Neutral
        // MediaAttention signal. Two such signals (mediaCount 2 + at least one distinct third-party
        // source name) push AttentionScore above zero while Neutral keeps Trajectory at the 50 baseline.
        var result = formula.Compute(InputFrom(new[]
        {
            BuildSignal(direction: SignalDirection.Neutral, type: SignalType.MediaAttention,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "The Ledger"),
            BuildSignal(direction: SignalDirection.Neutral, type: SignalType.MediaAttention,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "Yahoo Finance"),
        }));

        Assert.True(result.Components.AttentionScore > 0);
        Assert.Equal(50, result.Components.TrajectoryScore);
    }

    [Fact]
    public void Attention_Saturates_AndIsMonotonic()
    {
        var formula = Formula(AllGenuine);

        // Third-party source types so attention is measurable.
        var one = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "only-source"),
        }));

        var five = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "src-a"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "src-b"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "src-c"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "src-d"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "src-e"),
        }));

        Assert.True(five.Components.AttentionScore > one.Components.AttentionScore);
        Assert.True(five.Components.AttentionScore < 100);
    }

    [Fact]
    public void Attention_DistinctPublisherSourceNames_RaiseBreadth_SameMediaCount()
    {
        // Spec 84: once the collector maps SourceName to the real outlet, distinct-publisher breadth becomes
        // real. Three signals with THREE distinct third-party SourceNames outscore three signals sharing ONE
        // SourceName. mediaCount (the formula counts SignalType.MediaAttention, not set size) is 0 in both
        // because BuildSignal defaults to the non-media CustomerWin type, so reach moves on breadth alone.
        // Under the all-genuine weights fake each distinct publisher weighs 1.0, so breadth == distinct count.
        var formula = Formula(AllGenuine);

        var oneOutlet = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
        }));

        var threeOutlets = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Yahoo Finance"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "MarketBeat"),
        }));

        Assert.True(threeOutlets.Components.AttentionScore > oneOutlet.Components.AttentionScore);
    }

    [Fact]
    public void Attention_RepeatedSamePublisher_DoesNotInflateBreadth()
    {
        // Regression lock for outlet-dedupe: three NewsArticle items sharing one SourceName deliver the same
        // breadth as a single one (the formula's Distinct(SourceName)). mediaCount stays 0 in both because
        // BuildSignal defaults to the non-media CustomerWin type — so breadth is isolated.
        var formula = Formula(AllGenuine);

        var one = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
        }));

        var threeSameOutlet = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "Reuters"),
        }));

        Assert.Equal(one.Components.AttentionScore, threeSameOutlet.Components.AttentionScore);
    }

    [Fact]
    public void EvidenceConfidence_RewardsQualityAndDiversity()
    {
        var formula = Formula(AllGenuine);

        // High quality + diverse source types.
        var rich = formula.Compute(InputFrom(new[]
        {
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.Filing, sourceName: "src-a"),
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.EarningsTranscript, sourceName: "src-b"),
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.GovernmentContract, sourceName: "src-c"),
        }));

        // Same confidences, low quality, single source type.
        var poor = formula.Compute(InputFrom(new[]
        {
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.SocialMedia, sourceName: "src-a"),
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.SocialMedia, sourceName: "src-b"),
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.SocialMedia, sourceName: "src-c"),
        }));

        Assert.True(rich.Components.EvidenceConfidenceScore > poor.Components.EvidenceConfidenceScore);
    }

    [Fact]
    public void EvidenceConfidence_IsMonotonic_UnderCorroboration()
    {
        var formula = Formula(AllGenuine);

        var baseSignal = new[]
        {
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.Medium,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "src-a"),
        };
        var baseline = formula.Compute(InputFrom(baseSignal)).Components.EvidenceConfidenceScore;

        // Adding a weaker (lower-confidence) signal of the same source type must not lower the score.
        var withWeaker = formula.Compute(InputFrom(new[]
        {
            baseSignal[0],
            BuildSignal(confidence: 0.3m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "src-b"),
        })).Components.EvidenceConfidenceScore;

        Assert.True(withWeaker >= baseline);

        // Adding evidence of a NEW source type raises it (diversity bonus).
        var withNewType = formula.Compute(InputFrom(new[]
        {
            baseSignal[0],
            BuildSignal(confidence: 0.3m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.Filing, sourceName: "src-b"),
        })).Components.EvidenceConfidenceScore;

        Assert.True(withNewType > baseline);
    }

    [Fact]
    public void EvidenceConfidence_HighQualityItem_RaisesBestQualWeight()
    {
        var formula = Formula(AllGenuine);

        var allLow = formula.Compute(InputFrom(new[]
        {
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "src-a"),
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "src-b"),
        })).Components.EvidenceConfidenceScore;

        var withHigh = formula.Compute(InputFrom(new[]
        {
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.Low,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "src-a"),
            BuildSignal(confidence: 0.8m, quality: EvidenceQuality.High,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "src-b"),
        })).Components.EvidenceConfidenceScore;

        Assert.True(withHigh > allLow);
    }

    [Fact]
    public void HeliosScenario_Corroboration_ReachesWatchTerritory()
    {
        var formula = Formula(AllGenuine);

        // A strong Positive press-release guidance change plus two Neutral High-quality 8-K filings.
        var input = InputFrom(new[]
        {
            BuildSignal(strength: 6, direction: SignalDirection.Positive, confidence: 0.65m,
                type: SignalType.GuidanceChange, quality: EvidenceQuality.Medium,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "Helios Newsroom"),
            BuildSignal(strength: 4, direction: SignalDirection.Neutral, confidence: 0.40m,
                type: SignalType.Other, quality: EvidenceQuality.High,
                sourceType: EvidenceSourceType.Filing, sourceName: "SEC EDGAR"),
            BuildSignal(strength: 4, direction: SignalDirection.Neutral, confidence: 0.40m,
                type: SignalType.Other, quality: EvidenceQuality.High,
                sourceType: EvidenceSourceType.Filing, sourceName: "SEC EDGAR"),
        });

        var c = formula.Compute(input).Components;

        // Trajectory: single directional signal (Positive strength 6) → 50 + 5·6 = 80.
        Assert.Equal(80, c.TrajectoryScore);

        // Attention: all first-party (press release + filings) → 0 (independent of tier weights).
        Assert.Equal(0, c.AttentionScore);

        // EvidenceConfidence: bestConf 0.65, bestQual High (.85), 2 distinct source types →
        // 100·0.65·(0.6+0.4·0.85)·(0.7+0.3·(2/3)) ≈ 55.
        Assert.InRange(c.EvidenceConfidenceScore, 53, 57);

        // Opportunity: Trajectory 80 · (EC/100) · (1 − 0/250) ≈ 44 — comfortably in Watch territory.
        // Attention is 0 so the discount divisor (250) leaves this scenario unchanged from v4.
        Assert.True(c.OpportunityScore >= 40);
    }

    [Fact]
    public void Velocity_Acceleration_AbovePrevious_IsAbove50()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(
            new[] { BuildSignal(strength: 10), BuildSignal(strength: 10) },
            new[] { BuildSignal(strength: 1).Signal });

        Assert.True(formula.Compute(input).Components.SignalVelocityScore > 50);
    }

    [Fact]
    public void Velocity_Deceleration_BelowPrevious_IsBelow50()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(
            new[] { BuildSignal(strength: 1) },
            new[] { BuildSignal(strength: 10).Signal, BuildSignal(strength: 10).Signal });

        Assert.True(formula.Compute(input).Components.SignalVelocityScore < 50);
    }

    [Fact]
    public void Velocity_EqualActivity_Is50()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(
            new[] { BuildSignal(strength: 6) },
            new[] { BuildSignal(strength: 6).Signal });

        Assert.Equal(50, formula.Compute(input).Components.SignalVelocityScore);
    }

    [Fact]
    public void Velocity_EmptyPreviousWithCurrent_IsAbove50()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(
            new[] { BuildSignal(strength: 8) },
            Array.Empty<Signal>());

        Assert.True(formula.Compute(input).Components.SignalVelocityScore > 50);
    }

    [Fact]
    public void Opportunity_FallsAsAttentionRises_NeverZeroes()
    {
        var formula = Formula(AllGenuine);

        // Low attention: single third-party source.
        var lowAttention = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "only"),
        }));

        // High attention: many distinct third-party sources + media attention, same trajectory drivers.
        var highSignals = new List<ScoringSignal>();
        for (var i = 0; i < 8; i++)
        {
            highSignals.Add(BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                quality: EvidenceQuality.PrimarySource, type: SignalType.MediaAttention,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: $"src-{i}"));
        }
        var highAttention = formula.Compute(InputFrom(highSignals));

        Assert.True(highAttention.Components.AttentionScore > lowAttention.Components.AttentionScore);
        Assert.True(highAttention.Components.OpportunityScore <= lowAttention.Components.OpportunityScore);
        Assert.True(highAttention.Components.OpportunityScore > 0);
    }

    [Fact]
    public void Contributions_OnePerCurrentSignal_InOrder_WithProvenance_AndSignedWeight()
    {
        var formula = Formula(AllGenuine);
        var current = new[]
        {
            BuildSignal(strength: 8, direction: SignalDirection.Positive, sourceName: "src-a"),
            BuildSignal(strength: 7, direction: SignalDirection.Negative, sourceName: "src-b"),
        };
        var previous = new[] { BuildSignal(strength: 5).Signal };
        var input = InputFrom(current, previous);

        var result = formula.Compute(input);

        Assert.Equal(current.Length, result.Contributions.Count);
        for (var i = 0; i < current.Length; i++)
        {
            Assert.Equal(current[i].Signal.Id, result.Contributions[i].SignalId);
            Assert.Equal(current[i].Evidence.Id, result.Contributions[i].EvidenceId);
        }

        // Negative-direction signal yields negative weight.
        Assert.True(result.Contributions[1].ContributionWeight < 0);
        Assert.True(result.Contributions[0].ContributionWeight > 0);

        // No contribution references a previous-window signal.
        var previousIds = previous.Select(s => s.Id).ToHashSet();
        Assert.All(result.Contributions, c => Assert.DoesNotContain(c.SignalId, previousIds));
    }

    [Fact]
    public void Contributions_IncludeNeutralSignals_WithZeroWeight()
    {
        var formula = Formula(AllGenuine);
        var current = new[]
        {
            BuildSignal(strength: 6, direction: SignalDirection.Positive, sourceName: "src-a"),
            BuildSignal(strength: 9, direction: SignalDirection.Neutral, sourceName: "src-b"),
        };

        var result = formula.Compute(InputFrom(current));

        // Neutral signal still emits a contribution (provenance unchanged), with weight 0.
        Assert.Equal(current.Length, result.Contributions.Count);
        Assert.Equal(current[1].Signal.Id, result.Contributions[1].SignalId);
        Assert.Equal(0, result.Contributions[1].ContributionWeight);
    }

    [Fact]
    public void EmptyCurrentWindow_EvenWithPrevious_AllZero_EmptyContributions_ValidJson()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(
            Array.Empty<ScoringSignal>(),
            new[] { BuildSignal(strength: 9).Signal });

        var result = formula.Compute(input);

        Assert.Equal(new ScoreComponents(0, 0, 0, 0, 0), result.Components);
        Assert.Empty(result.Contributions);
        Assert.False(string.IsNullOrWhiteSpace(result.Explanation));

        var roundTripped = JsonSerializer.Deserialize<ScoreComponents>(result.ComponentJson);
        Assert.NotNull(roundTripped);
    }

    [Fact]
    public void Determinism_SameInput_ProducesEqualOutputs()
    {
        var formula = Formula(AllGenuine);
        var input = InputFrom(
            new[]
            {
                BuildSignal(strength: 4, direction: SignalDirection.Positive, sourceName: "src-a"),
                BuildSignal(strength: 6, direction: SignalDirection.Negative, sourceName: "src-b"),
                BuildSignal(strength: 8, direction: SignalDirection.Neutral, sourceName: "src-c"),
            },
            new[] { BuildSignal(strength: 5).Signal });

        var first = formula.Compute(input);
        var second = formula.Compute(input);

        Assert.Equal(first.Components, second.Components);
        Assert.Equal(first.ComponentJson, second.ComponentJson);
        Assert.Equal(first.Explanation, second.Explanation);
        Assert.Equal(first.Contributions, second.Contributions);
    }

    [Fact]
    public void Recency_NewerPositiveSignal_WeighsAtLeastAsMuch()
    {
        var formula = Formula(AllGenuine);

        var older = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 8, direction: SignalDirection.Positive,
                observedAt: new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)),
        }));

        var newer = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 8, direction: SignalDirection.Positive,
                observedAt: new DateTimeOffset(2026, 1, 29, 0, 0, 0, TimeSpan.Zero)),
        }));

        Assert.True(newer.Components.TrajectoryScore >= older.Components.TrajectoryScore);
    }

    // ---- source-quality tiering + saturation pins (spec 88, carried forward to v5) ----
    //
    // The Attention breadth term is a tier-weighted distinct-publisher SUM (mill 0.1 / unknown 0.5 /
    // genuine 1.0) instead of a flat distinct count, and the half-saturation is 3. All expected Attention
    // values below are the direct closed form 100·reach/(reach+3), rounded away-from-zero.

    // Build N distinct-publisher Positive third-party NewsArticle signals under the given name prefix
    // (non-media type so mediaCount = 0; strength 10 / confidence 1.0 / PrimarySource quality → Trajectory
    // 100, EC 80). Reach = Σ tier weight over the N distinct publishers.
    private static IReadOnlyList<ScoringSignal> News(int count, string prefix) =>
        Enumerable.Range(0, count)
            .Select(i => BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: $"{prefix}-{i}"))
            .ToArray();

    [Fact]
    public void Attention_MillDominated_IsMateriallyLower_ThanGenuine_AtEqualPublisherCount()
    {
        // Pin 1 (headline property): five distinct MILL publishers (weight 0.1 → reach 0.5 → Att 14) score
        // materially LOWER than five distinct GENUINE publishers (weight 1.0 → reach 5 → Att 63), at the same
        // publisher count. Breadth is now earned by genuine notice, not raw publisher count.
        var formula = Formula(Tiered());

        var mill = formula.Compute(InputFrom(News(5, "mill"))).Components.AttentionScore;      // 100·0.5/3.5 = 14
        var genuine = formula.Compute(InputFrom(News(5, "genuine"))).Components.AttentionScore; // 100·5/8   = 63

        Assert.InRange(mill, 12, 16);
        Assert.InRange(genuine, 60, 66);
        Assert.True(genuine - mill > 20, $"genuine must materially beat mill; genuine={genuine}, mill={mill}");
    }

    [Fact]
    public void Attention_ThinlyGenuine_IsNotZeroed_AndBeatsLargerPileOfMills()
    {
        // Pin 2: a thinly-but-genuinely-covered name (2 genuine → reach 2 → Att 40) is NOT wrongly zeroed and
        // beats a larger pile of mills whose weighted reach is smaller (10 mills → reach 1.0 → Att 25).
        var formula = Formula(Tiered());

        var thinGenuine = formula.Compute(InputFrom(News(2, "genuine"))).Components.AttentionScore; // 100·2/5 = 40
        var manyMills = formula.Compute(InputFrom(News(10, "mill"))).Components.AttentionScore;      // 100·1/4 = 25

        Assert.True(thinGenuine > 0);
        Assert.InRange(thinGenuine, 35, 45);
        Assert.True(thinGenuine > manyMills,
            $"a few real outlets must beat many mills; thinGenuine={thinGenuine}, manyMills={manyMills}");
    }

    [Fact]
    public void Attention_UnknownPublishers_DefaultToSaneNonZeroWeight()
    {
        // Pin 3: publishers in no tier default to the non-zero UnknownWeight (0.5), never silently zeroed.
        // Six unknown publishers → weighted reach 6·0.5 = 3.0 → Att 100·3/6 = 50.
        var formula = Formula(Tiered());

        var att = formula.Compute(InputFrom(News(6, "unknown"))).Components.AttentionScore;

        Assert.True(att > 0);
        Assert.Equal(50, att); // reflects reach ≈ N·UnknownWeight = 6·0.5 = 3.0
    }

    [Fact]
    public void Attention_TierList_IsConfigDriven_SamePublisherClassifiedDifferently()
    {
        // Pin 4: the same publisher name yields different Attention under two different IAttentionSourceWeights,
        // proving the tier policy comes from config, not from the formula. One classifies "AcmeWire" genuine
        // (1.0 → reach 1 → Att 25), the other mill (0.1 → reach 0.1 → Att 3).
        var single = new[]
        {
            BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "AcmeWire"),
        };

        var asGenuine = Formula(new FuncWeights(_ => 1.0))
            .Compute(InputFrom(single)).Components.AttentionScore;   // 100·1/4   = 25
        var asMill = Formula(new FuncWeights(_ => 0.1))
            .Compute(InputFrom(single)).Components.AttentionScore;   // 100·0.1/3.1 = 3

        Assert.True(asGenuine > asMill,
            $"tiers must be config-driven; asGenuine={asGenuine}, asMill={asMill}");
        Assert.InRange(asGenuine, 23, 27);
        Assert.InRange(asMill, 2, 5);
    }

    [Fact]
    public void Attention_SaturationRetuned_ForFilteredScale()
    {
        // Pin 5: on the +3 scale a weighted reach ≈ 5 lands in the ~60s and a mill-only thin reach ≈ 0.6 lands
        // low (~15–20), locking the useful spread of the re-tuned saturation.
        var formula = Formula(Tiered());

        var mid = formula.Compute(InputFrom(News(5, "genuine"))).Components.AttentionScore; // reach 5   → 100·5/8   = 63
        var thin = formula.Compute(InputFrom(News(6, "mill"))).Components.AttentionScore;    // reach 0.6 → 100·0.6/3.6 = 17

        Assert.InRange(mid, 60, 66);
        Assert.InRange(thin, 15, 20);
    }

    [Fact]
    public void Attention_DistinctByPublisher_MillRepeatedManyTimes_CountsWeightOnce()
    {
        // Pin 6: the same mill publisher appearing many times contributes its weight ONCE (distinct-by-publisher
        // preserved). Ten copies of one mill outlet score identically to a single copy (both reach 0.1 → Att 3).
        var formula = Formula(Tiered());

        var once = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "mill-outlet"),
        })).Components.AttentionScore;

        var manyCopies = formula.Compute(InputFrom(
            Enumerable.Range(0, 10)
                .Select(_ => BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "mill-outlet"))
                .ToArray())).Components.AttentionScore;

        Assert.Equal(once, manyCopies);
    }

    // ---- spec 89 config-driven weights pins ----

    [Fact]
    public void DefaultConfig_IsByteIdenticalToV4_ForARepresentativeInput()
    {
        // Headline regression guarantee: default ScoringWeights == the radar-formula-v4 constants, so a
        // representative multi-signal input yields the EXACT v4 component integers, ComponentJson, and
        // explanation body (only the version prefix differs). These pinned integers ARE the v4 output.
        var formula = Formula(Tiered());

        var input = InputFrom(new[]
        {
            BuildSignal(strength: 6, direction: SignalDirection.Positive, confidence: 0.65m,
                type: SignalType.GuidanceChange, quality: EvidenceQuality.Medium,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "genuine-a"),
            BuildSignal(strength: 8, direction: SignalDirection.Positive, confidence: 0.8m,
                type: SignalType.CustomerWin, quality: EvidenceQuality.High,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "genuine-b"),
            BuildSignal(strength: 4, direction: SignalDirection.Neutral, confidence: 0.40m,
                type: SignalType.MediaAttention, quality: EvidenceQuality.High,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "mill-c"),
        });

        var result = formula.Compute(input);
        var c = result.Components;

        // reach = genuine(1.0) + genuine(1.0) + mill(0.1) + 0.25·mediaCount(1) = 2.35 → Att 100·2.35/5.35 = 44.
        // Velocity has no previous window → 50·(18+10)/(0+10) = 140 → clamped 100.
        Assert.Equal(new ScoreComponents(
            TrajectoryScore: 86,
            OpportunityScore: 43,
            AttentionScore: 44,
            EvidenceConfidenceScore: 60,
            SignalVelocityScore: 100),
            c);

        Assert.Equal(JsonSerializer.Serialize(c), result.ComponentJson);
        Assert.Equal(
            "radar-formula-v5: 3 signal(s) over 30d → Trajectory 86, Opportunity 43 (Attention 44, "
                + "Confidence 60, Velocity 100).",
            result.Explanation);
    }

    [Fact]
    public void ChangedWeight_MovesTheScore_ProvingConfigIsRead()
    {
        // Constructing V5 with a changed AttentionHalfSaturation (old v3 value 12.0) must move Attention (and
        // the Opportunity that consumes it) versus the default weights on the same input — proving the formula
        // reads the injected config, not const fields.
        var input = InputFrom(News(5, "genuine"));

        var defaultResult = new RadarScoreFormulaV5(new ScoringWeights(), Tiered()).Compute(input).Components;
        var retunedResult = new RadarScoreFormulaV5(
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, Tiered()).Compute(input).Components;

        Assert.NotEqual(defaultResult.AttentionScore, retunedResult.AttentionScore);
        Assert.NotEqual(defaultResult.OpportunityScore, retunedResult.OpportunityScore);
        // Larger half-saturation lowers Attention for the same reach.
        Assert.True(retunedResult.AttentionScore < defaultResult.AttentionScore);
    }
}
