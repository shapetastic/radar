using System.Text.Json;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class RadarScoreFormulaV6Tests
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

    // Convenience: construct the default-weights formula over the given source weights.
    private static RadarScoreFormulaV6 Formula(IAttentionSourceWeights sourceWeights) =>
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
    public void Version_IsRadarFormulaV6_AndAppearsInExplanation()
    {
        var formula = Formula(AllGenuine);

        Assert.Equal("radar-formula-v6", formula.Version);

        var result = formula.Compute(InputFrom(new[] { BuildSignal() }));
        Assert.Contains("radar-formula-v6", result.Explanation);
    }

    [Fact]
    public void Constructor_NullWeights_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RadarScoreFormulaV6(null!, AllGenuine));
    }

    [Fact]
    public void Constructor_NullSourceWeights_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RadarScoreFormulaV6(new ScoringWeights(), null!));
    }

    [Fact]
    public void Constructor_InvalidWeight_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV6(new ScoringWeights { OpportunityAttentionDivisor = 0 }, AllGenuine));
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

        // Neutral signals are excluded from both masses entirely, so the score is unchanged.
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
    public void HeliosScenario_LoneDirectionalSignal_IsDampedUnderV6()
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

        // Trajectory: a SINGLE directional signal (Positive strength 6, conf 0.65, recency 0.7333 →
        // w = 0.47667, Mpos = 2.86). T_raw = 10·2.86/(2.86 + 10) = 2.224 → 50 + 5·2.224 = 61.12 → 61.
        // Under v5 this lone signal reached 80 (50 + 5·6); v6 deliberately DAMPS an uncorroborated single
        // signal toward the neutral 50 — a strong read must be earned by corroboration, not asserted by one.
        Assert.Equal(61, c.TrajectoryScore);

        // Attention: all first-party (press release + filings) → 0 (independent of tier weights).
        Assert.Equal(0, c.AttentionScore);

        // EvidenceConfidence: bestConf 0.65, bestQual High (.85), 2 distinct source types →
        // 100·0.65·(0.6+0.4·0.85)·(0.7+0.3·(2/3)) ≈ 55.
        Assert.InRange(c.EvidenceConfidenceScore, 53, 57);

        // Opportunity: Trajectory 61 · (EC 55/100) · (1 − 0/250) = 33.55 → 34. Because the lone directional
        // signal is damped under v6 this no longer reaches the ~40 Watch band it did under the v5 mean — the
        // honest v6 value for an uncorroborated single positive (see the corroboration tests below for the
        // rewarded-majority case).
        Assert.Equal(34, c.OpportunityScore);
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

        // Low attention: a single strong Positive third-party source (the sole trajectory driver).
        var driver = BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
            quality: EvidenceQuality.PrimarySource,
            sourceType: EvidenceSourceType.NewsArticle, sourceName: "only");
        var lowAttention = formula.Compute(InputFrom(new[] { driver }));

        // High attention: the SAME single Positive trajectory driver, plus many distinct-publisher Neutral
        // MediaAttention signals. Under v6 the number of DIRECTIONAL signals drives Trajectory (corroboration),
        // so to isolate the attention→opportunity discount the directional set must be held fixed — the extra
        // attention comes from Neutral MediaAttention signals, which are excluded from both trajectory masses
        // (leaving Trajectory unchanged) while raising distinct-publisher breadth + mediaCount.
        var highSignals = new List<ScoringSignal> { driver };
        for (var i = 0; i < 8; i++)
        {
            highSignals.Add(BuildSignal(direction: SignalDirection.Neutral, type: SignalType.MediaAttention,
                quality: EvidenceQuality.PrimarySource,
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
    public void Trajectory_LoneRoutineInsiderSale_AtSpec110SellStrength_NoLongerDropsBy5()
    {
        // Spec 110 (AEHR regression), carried onto radar-formula-v6: a strong positive-directional majority
        // (record-results week) plus ONE lone routine open-market insider sale. Under the recalibrated default
        // SellTiers a ~$1.6M discretionary sale maps to Strength 4 (was 7 when SellTiers == BuyTiers). The
        // weaker Negative mass shrinks the trajectory drop below the WeeklyReportActionPolicyV1 deterioration
        // threshold (week-over-week delta <= -5), so a routine trim after record results no longer *by itself*
        // flips the label to "Thesis deteriorating". The pre-110 Strength-7 sale DID cross that threshold.
        // (Spec 111 hardens the non-flip at the FORMULA level: the v6 corroboration term damps a lone dissenter
        // against a corroborated majority, so the drops are even smaller than under the v5 mean — but the
        // spec-110 property, a lighter recalibrated sale drops Trajectory less than the old heavier sale, still
        // holds and stays demonstrable. The action policy is NOT edited here.)
        var formula = Formula(AllGenuine);

        // The corroborated positive majority (identical in both scenarios; equal confidence + observedAt so the
        // corroboration masses are directly comparable and the sale's effect is isolated).
        ScoringSignal[] Majority() => Enumerable.Range(0, 11)
            .Select(i => BuildSignal(strength: 6, direction: SignalDirection.Positive, sourceName: $"pos-{i}"))
            .ToArray();

        ScoringSignal Sale(int strength) =>
            BuildSignal(strength: strength, direction: SignalDirection.Negative, sourceName: "insider-sale");

        var baseTrajectory = formula.Compute(InputFrom(Majority())).Components.TrajectoryScore;
        var withSpec110Sale = formula.Compute(InputFrom([.. Majority(), Sale(4)])).Components.TrajectoryScore;
        var withPre110Sale = formula.Compute(InputFrom([.. Majority(), Sale(7)])).Components.TrajectoryScore;

        var spec110Drop = baseTrajectory - withSpec110Sale;
        var pre110Drop = baseTrajectory - withPre110Sale;

        // The recalibrated (Strength-4) sale stays under the -5 deterioration trigger (v6 drop is 4)...
        Assert.True(spec110Drop < 5, $"spec-110 sell drop must be < 5; was {spec110Drop}");
        // ...whereas the old (Strength-7) sale crossed it (v6 drop is 7), and the recalibration strictly
        // shrinks the drop.
        Assert.True(pre110Drop >= 5, $"pre-110 sell drop must be >= 5; was {pre110Drop}");
        Assert.True(spec110Drop < pre110Drop);
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

    // ---- radar-formula-v6 corroboration-aware Trajectory (spec 111) ----
    //
    // The v6 Trajectory splits the current-window directional signals into a positive mass and a negative mass
    // (each Σ strength·confidence·recency over that direction) and combines them as
    // T_raw = 10·(Mpos − Mneg)/(Mpos + Mneg + k), k = TrajectoryCorroborationK (default 10). Neutral/Mixed
    // still contribute 0. The tests below use observedAt = WindowEnd so recency = 1 and the masses are the
    // clean strength·confidence sums.

    // Build N distinct-source Positive signals at a fixed strength/confidence with recency 1 (observedAt at the
    // window end), non-media type so they never touch Attention's mediaCount.
    private static IReadOnlyList<ScoringSignal> Directional(
        int count, SignalDirection direction, int strength = 6, decimal confidence = 0.8m, string prefix = "d") =>
        Enumerable.Range(0, count)
            .Select(i => BuildSignal(strength: strength, direction: direction, confidence: confidence,
                sourceName: $"{prefix}-{i}", observedAt: WindowEnd))
            .ToArray();

    [Fact]
    public void Trajectory_CorroboratedPositiveMajority_ScoresHigherThanLoneSignal()
    {
        // Corroboration rewarded: five agreeing Positive signals move Trajectory strictly HIGHER than ONE
        // Positive signal of the same strength/confidence. Under the v5 mean these were identical (a mean of
        // five equal terms == one term); v6's preponderance ratio rewards the accrued mass.
        var formula = Formula(AllGenuine);

        var one = formula.Compute(InputFrom(Directional(1, SignalDirection.Positive))).Components.TrajectoryScore;
        var five = formula.Compute(InputFrom(Directional(5, SignalDirection.Positive))).Components.TrajectoryScore;

        Assert.True(five > one, $"a corroborated majority must beat a lone signal; five={five}, one={one}");
    }

    [Fact]
    public void Trajectory_StrongMajorityWithLoneDissenter_IsRobust_ButRecordsTheDissent()
    {
        // The AEHR robustness fix. A strong Positive majority (5 signals, strength 6) + ONE lone Negative.
        var formula = Formula(AllGenuine);

        var majority = Directional(5, SignalDirection.Positive, strength: 6, confidence: 0.8m, prefix: "pos");
        var loneNegative = BuildSignal(strength: 6, direction: SignalDirection.Negative, confidence: 0.8m,
            sourceName: "lone-neg", observedAt: WindowEnd);

        var withLoneNegative = formula
            .Compute(InputFrom([.. majority, loneNegative])).Components.TrajectoryScore;

        // (a) Strictly higher than the v5 MEAN would give for the SAME inputs. The v5 mean is
        // 50 + 5·(Σ sign·strength·w / Σ w) over the directional signals (recency 1, w = 0.8). With 5 positives
        // and 1 negative all at strength 6 the moderate strength (10 − 6 per signal of slack) makes the
        // corroboration ratio beat the count-mean: v6 damps the lone dissenter's pull more than the mean does.
        var w = 0.8; // confidence 0.8 · recency 1
        var sumSignStrengthW = (5 * 6 - 1 * 6) * w; // Σ sign·strength·w
        var sumW = 6 * w;                            // Σ w over the 6 directional signals
        var v5Mean = (int)Math.Round(50 + 5 * (sumSignStrengthW / sumW), MidpointRounding.AwayFromZero);
        Assert.True(withLoneNegative > v5Mean,
            $"v6 must beat the v5 mean for a majority + lone dissenter; v6={withLoneNegative}, v5Mean={v5Mean}");

        // (b) Strictly higher than the same majority against a CORROBORATED negative cluster (3 negatives) —
        // an isolated dissenter is damped, a corroborated dissenting cluster is not.
        var negativeCluster = Directional(3, SignalDirection.Negative, strength: 6, confidence: 0.8m, prefix: "neg");
        var withNegativeCluster = formula
            .Compute(InputFrom([.. majority, .. negativeCluster])).Components.TrajectoryScore;
        Assert.True(withLoneNegative > withNegativeCluster,
            $"a lone dissenter must damp less than a corroborated cluster; lone={withLoneNegative}, "
                + $"cluster={withNegativeCluster}");

        // (c) The lone dissent is NOT zeroed: it still moves Trajectory strictly below the no-negative
        // majority (the dissent is recorded, never ignored).
        var majorityOnly = formula.Compute(InputFrom(majority)).Components.TrajectoryScore;
        Assert.True(withLoneNegative < majorityOnly,
            $"the dissent must still be recorded; withLone={withLoneNegative}, majorityOnly={majorityOnly}");
    }

    [Fact]
    public void Trajectory_IsDirectionSymmetric_NoPositiveBias()
    {
        // The exact negative mirror of a corroborated case moves Trajectory DOWN from 50 by the same magnitude
        // it moved UP: swapping every Positive↔Negative negates (Mpos − Mneg) and hence T_raw.
        var formula = Formula(AllGenuine);

        var up = formula.Compute(InputFrom(Directional(5, SignalDirection.Positive))).Components.TrajectoryScore;
        var down = formula.Compute(InputFrom(Directional(5, SignalDirection.Negative))).Components.TrajectoryScore;

        Assert.True(up > 50);
        Assert.True(down < 50);
        Assert.Equal(up - 50, 50 - down);
    }

    [Fact]
    public void Trajectory_IsMonotone_InAddedSignals()
    {
        // Adding one more Positive never lowers Trajectory; adding one Negative never raises it.
        var formula = Formula(AllGenuine);

        var baseSet = Directional(3, SignalDirection.Positive, prefix: "base");
        var baseTraj = formula.Compute(InputFrom(baseSet)).Components.TrajectoryScore;

        var withExtraPositive = formula
            .Compute(InputFrom([.. baseSet, BuildSignal(strength: 6, direction: SignalDirection.Positive,
                sourceName: "extra-pos", observedAt: WindowEnd)])).Components.TrajectoryScore;
        var withExtraNegative = formula
            .Compute(InputFrom([.. baseSet, BuildSignal(strength: 6, direction: SignalDirection.Negative,
                sourceName: "extra-neg", observedAt: WindowEnd)])).Components.TrajectoryScore;

        Assert.True(withExtraPositive >= baseTraj,
            $"adding a positive must not lower Trajectory; base={baseTraj}, withPos={withExtraPositive}");
        Assert.True(withExtraNegative <= baseTraj,
            $"adding a negative must not raise Trajectory; base={baseTraj}, withNeg={withExtraNegative}");
    }

    [Fact]
    public void Trajectory_LargerCorroborationK_DampsSingleSignalTowardNeutral()
    {
        // The new TrajectoryCorroborationK is read from config: a larger k damps a single-signal Trajectory
        // toward the neutral 50 versus the default k, proving the constant flows through ScoringWeights.
        var single = InputFrom(Directional(1, SignalDirection.Positive, strength: 6, confidence: 0.8m));

        var defaultK = new RadarScoreFormulaV6(new ScoringWeights(), AllGenuine)
            .Compute(single).Components.TrajectoryScore;
        var largeK = new RadarScoreFormulaV6(new ScoringWeights { TrajectoryCorroborationK = 50.0 }, AllGenuine)
            .Compute(single).Components.TrajectoryScore;

        Assert.True(defaultK > 50);
        Assert.True(largeK > 50);
        Assert.True(largeK < defaultK,
            $"a larger k must damp toward 50; defaultK={defaultK}, largeK={largeK}");
    }

    [Fact]
    public void TrajectoryCorroborationK_MustBePositive()
    {
        // It is a denominator smoother, so a zero/negative k fails fast (Validate() + the formula ctor).
        Assert.Throws<InvalidOperationException>(() => new ScoringWeights { TrajectoryCorroborationK = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV6(new ScoringWeights { TrajectoryCorroborationK = -1 }, AllGenuine));
    }

    // ---- spec 112: AI directional earnings-read materiality ----

    [Fact]
    public void DeterministicNeutralGuidanceChange_IsTrajectoryInert_NotHarmful()
    {
        // Spec 112 acceptance (item 3): the deterministic "results of operations" 8-K keyword rule emits a
        // Neutral GuidanceChange (the AI directional read SUPERSEDES it when it fires, spec 78). A Neutral
        // direction is trajectory-EXCLUDED — DirectionSign(Neutral) == 0 — so it contributes 0 to BOTH the
        // positive and the negative mass and Trajectory stays at the neutral 50. It is inert, never harmful
        // (a record-guidance filing the extractor cannot read is not scored DOWN). Verify-and-document only:
        // no rule/formula change.
        var formula = Formula(AllGenuine);

        var result = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 3, direction: SignalDirection.Neutral, confidence: 0.5m,
                type: SignalType.GuidanceChange, quality: EvidenceQuality.High,
                sourceType: EvidenceSourceType.Filing, sourceName: "SEC EDGAR"),
        }));

        Assert.Equal(50, result.Components.TrajectoryScore);

        // Its provenance is still recorded (one contribution), but with weight 0 — excluded from both masses.
        var contribution = Assert.Single(result.Contributions);
        Assert.Equal(0, contribution.ContributionWeight);
    }

    [Fact]
    public void Materiality_ConfidentRaisedGuidanceAtStrength8_ClearsInvestigateGate_Strength6DoesNot()
    {
        // Spec 112 acceptance (item 2 / the materiality gap the slice closes): on a corroborated positive
        // trajectory, a confident raised-guidance directional GuidanceChange at the recalibrated default
        // Strength 8 lifts OpportunityScore over the Investigate gate
        // (WeeklyReportActionPolicyV1.InvestigateOpportunity == 60), whereas the SAME setup at the pre-112
        // Strength 6 (== the keyword max) does not. Uses a SYNTHETIC directional signal (NOT a live AI call) —
        // the AI provider boundary stays behind Infrastructure.
        var formula = Formula(AllGenuine);

        // The guidance read + one corroborating positive customer win (v6 rewards accrued directional mass, so
        // "corroborated"). Both first-party (Filing + PressRelease) so Attention is 0 and Opportunity tracks
        // Trajectory·EvidenceConfidence; recency = 1 (observed at the window end). EvidenceConfidence is 81
        // here (bestConf 0.9, PrimarySource quality, 2 distinct source types), so the only mover between the
        // two scenarios is the directional-mass shift from the guidance Strength.
        ScoringInput SetWithGuidanceStrength(int guidanceStrength) => InputFrom(new[]
        {
            BuildSignal(strength: guidanceStrength, direction: SignalDirection.Positive, confidence: 0.9m,
                type: SignalType.GuidanceChange, quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.Filing, sourceName: "SEC EDGAR", observedAt: WindowEnd),
            BuildSignal(strength: 5, direction: SignalDirection.Positive, confidence: 0.66m,
                type: SignalType.CustomerWin, quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.PressRelease, sourceName: "Acme Newsroom", observedAt: WindowEnd),
        });

        var atStrength6 = formula.Compute(SetWithGuidanceStrength(6)).Components;
        var atStrength8 = formula.Compute(SetWithGuidanceStrength(8)).Components;

        // Attention is 0 for both (first-party only) — isolating the directional-mass effect on Opportunity.
        Assert.Equal(0, atStrength6.AttentionScore);
        Assert.Equal(0, atStrength8.AttentionScore);

        // Monotonic materiality: Strength 8 yields a strictly higher Opportunity than Strength 6 on the
        // identical corroborated-positive set (Opp 59 → 62 for these inputs).
        Assert.True(atStrength8.OpportunityScore > atStrength6.OpportunityScore,
            $"Strength 8 must be materially stronger; s8={atStrength8.OpportunityScore}, s6={atStrength6.OpportunityScore}");

        // The gate crossing: Strength 6 stays below the Investigate gate (60); Strength 8 clears it.
        Assert.True(atStrength6.OpportunityScore < 60,
            $"Strength 6 must stay below the Investigate gate; was {atStrength6.OpportunityScore}");
        Assert.True(atStrength8.OpportunityScore >= 60,
            $"Strength 8 must clear the Investigate gate; was {atStrength8.OpportunityScore}");

        // The deterministic action policy flips Watch → Investigate on the SAME evidence, purely on the
        // recalibrated directional Strength — the human-facing materiality of the fix.
        var policy = new WeeklyReportActionPolicyV1();
        var at6Action = policy.Decide(new ReportActionContext(SnapshotFrom(atStrength6), null)).Action;
        var at8Action = policy.Decide(new ReportActionContext(SnapshotFrom(atStrength8), null)).Action;
        Assert.Equal(RadarReportAction.Watch, at6Action);
        Assert.Equal(RadarReportAction.Investigate, at8Action);
    }

    private static CompanyScoreSnapshot SnapshotFrom(ScoreComponents c) =>
        new ScoreSnapshotBuilder()
            .WithTrajectoryScore(c.TrajectoryScore)
            .WithOpportunityScore(c.OpportunityScore)
            .WithEvidenceConfidenceScore(c.EvidenceConfidenceScore)
            .Build();

    // ---- source-quality tiering + saturation pins (spec 88, carried forward to v6) ----
    //
    // The Attention breadth term is a tier-weighted distinct-publisher SUM (mill 0.1 / unknown 0.5 /
    // genuine 1.0) instead of a flat distinct count, and the half-saturation is 3. All expected Attention
    // values below are the direct closed form 100·reach/(reach+3), rounded away-from-zero.

    // Build N distinct-publisher Positive third-party NewsArticle signals under the given name prefix
    // (non-media type so mediaCount = 0; strength 10 / confidence 1.0 / PrimarySource quality). Reach = Σ tier
    // weight over the N distinct publishers.
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

    [Fact]
    public void Attention_AllAggregator_ScoresMateriallyLower_ThanGenuine_UnderRecalibratedWeights()
    {
        // Spec 90 headline property: under the recalibrated posture (unknown 0.25 + expanded mill denylist), an
        // all-aggregator name scores materially LOWER Attention than a genuinely-covered one at the SAME distinct
        // publisher count. The Tiered fake encodes the 0.25 posture, so this does not depend on production Default.
        //
        // Input A — 10 mills: reach = 10·0.1 = 1.0 → Att = 100·1/(1+3) = 25 (< 30 absolute).
        // Input B — 10 genuine: reach = 10·1.0 = 10 → Att = 100·10/(10+3) = 77.
        // Margin B−A = 52 (> 25): the all-aggregator name is materially lower.
        var formula = Formula(Tiered(unknown: 0.25));

        var aggregator = formula.Compute(InputFrom(News(10, "mill"))).Components.AttentionScore;   // 25
        var genuine = formula.Compute(InputFrom(News(10, "genuine"))).Components.AttentionScore;    // 77

        Assert.True(aggregator < 30, $"all-aggregator name must score low; was {aggregator}");
        Assert.True(genuine - aggregator > 25,
            $"genuine coverage must materially beat aggregator; genuine={genuine}, aggregator={aggregator}");
    }

    // ---- spec 89 config-driven weights pins ----

    [Fact]
    public void DefaultConfig_MatchesV6Baseline_ForARepresentativeInput()
    {
        // Headline baseline pin for radar-formula-v6. Only the Trajectory component changed from the recalibrated
        // v5 baseline: the two Positive signals now form a corroborated positive mass combined through the
        // corroboration-smoothing constant k (default 10), so Trajectory is 72 (was 86 under the v5 mean) and the
        // Opportunity that consumes it is 36 (was 43). Attention (42), EvidenceConfidence (60) and Velocity (100)
        // are byte-identical to v5 — the spec-94 MediaReachWeight 0.10 still drives Attention. These pinned
        // integers ARE the radar-formula-v6 default output for this input.
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

        // Trajectory: recency 0.7333 for both positives. Mpos = 6·(0.65·0.7333) + 8·(0.8·0.7333)
        // = 2.86 + 4.6933 = 7.5533, Mneg = 0. T_raw = 10·7.5533/(7.5533 + 10) = 4.303 →
        // 50 + 5·4.303 = 71.52 → 72.
        // reach = genuine(1.0) + genuine(1.0) + mill(0.1) + 0.10·mediaCount(1) = 2.20 → Att 100·2.20/5.20 = 42.
        // Opportunity = 72·(60/100)·(1 − 42/250) = 35.94 → 36. Velocity has no previous window →
        // 50·(18+10)/(0+10) = 140 → clamped 100.
        Assert.Equal(new ScoreComponents(
            TrajectoryScore: 72,
            OpportunityScore: 36,
            AttentionScore: 42,
            EvidenceConfidenceScore: 60,
            SignalVelocityScore: 100),
            c);

        Assert.Equal(JsonSerializer.Serialize(c), result.ComponentJson);
        Assert.Equal(
            "radar-formula-v6: 3 signal(s) over 30d → Trajectory 72, Opportunity 36 (Attention 42, "
                + "Confidence 60, Velocity 100).",
            result.Explanation);
    }

    [Fact]
    public void DefaultMediaReachWeight_IsSpec94Recalibrated_0_10()
    {
        // Direct pin so an accidental future revert of the spec-94 recalibration (back to the v4 value 0.25)
        // is caught at the source, independent of any derived fingerprint/component pin.
        Assert.Equal(0.10, new ScoringWeights().MediaReachWeight);
    }

    [Fact]
    public void ChangedWeight_MovesTheScore_ProvingConfigIsRead()
    {
        // Constructing V6 with a changed AttentionHalfSaturation (old v3 value 12.0) must move Attention (and
        // the Opportunity that consumes it) versus the default weights on the same input — proving the formula
        // reads the injected config, not const fields.
        var input = InputFrom(News(5, "genuine"));

        var defaultResult = new RadarScoreFormulaV6(new ScoringWeights(), Tiered()).Compute(input).Components;
        var retunedResult = new RadarScoreFormulaV6(
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, Tiered()).Compute(input).Components;

        Assert.NotEqual(defaultResult.AttentionScore, retunedResult.AttentionScore);
        Assert.NotEqual(defaultResult.OpportunityScore, retunedResult.OpportunityScore);
        // Larger half-saturation lowers Attention for the same reach.
        Assert.True(retunedResult.AttentionScore < defaultResult.AttentionScore);
    }
}
