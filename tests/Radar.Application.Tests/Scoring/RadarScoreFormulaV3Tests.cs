using System.Text.Json;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class RadarScoreFormulaV3Tests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

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
    public void Version_IsRadarFormulaV3_AndAppearsInExplanation()
    {
        var formula = new RadarScoreFormulaV3();

        Assert.Equal("radar-formula-v3", formula.Version);

        var result = formula.Compute(InputFrom(new[] { BuildSignal() }));
        Assert.Contains("radar-formula-v3", result.Explanation);
    }

    [Fact]
    public void NeutralBaseline_SingleNeutralSignal_TrajectoryIs50()
    {
        var formula = new RadarScoreFormulaV3();
        var input = InputFrom(new[] { BuildSignal(direction: SignalDirection.Neutral) });

        var result = formula.Compute(input);

        Assert.Equal(50, result.Components.TrajectoryScore);
    }

    [Fact]
    public void AllPositive_Improves_AllNegative_Declines()
    {
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();
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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        // SourceName. mediaCount (RadarScoreFormulaV3 counts SignalType.MediaAttention, not set size) is 0 in
        // both because BuildSignal defaults to the non-media CustomerWin type, so reach moves on breadth alone.
        var formula = new RadarScoreFormulaV3();

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
        // Regression lock the fix relies on for outlet-dedupe: three NewsArticle items sharing one SourceName
        // deliver the same breadth as a single one (the formula's existing Distinct(SourceName)). mediaCount
        // (RadarScoreFormulaV3 counts SignalType.MediaAttention) stays 0 in both because BuildSignal defaults to
        // the non-media CustomerWin type — not overridden here — so breadth is isolated.
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();

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

        // Attention: all first-party (press release + filings) → 0.
        Assert.Equal(0, c.AttentionScore);

        // EvidenceConfidence: bestConf 0.65, bestQual High (.85), 2 distinct source types →
        // 100·0.65·(0.6+0.4·0.85)·(0.7+0.3·(2/3)) ≈ 55.
        Assert.InRange(c.EvidenceConfidenceScore, 53, 57);

        // Opportunity: Trajectory 80 · (EC/100) · (1 − 0/250) ≈ 44 — comfortably in Watch territory.
        // Attention is 0 so the softened divisor (250) leaves this scenario unchanged from v2.
        Assert.True(c.OpportunityScore >= 40);
    }

    [Fact]
    public void Velocity_Acceleration_AbovePrevious_IsAbove50()
    {
        var formula = new RadarScoreFormulaV3();
        var input = InputFrom(
            new[] { BuildSignal(strength: 10), BuildSignal(strength: 10) },
            new[] { BuildSignal(strength: 1).Signal });

        Assert.True(formula.Compute(input).Components.SignalVelocityScore > 50);
    }

    [Fact]
    public void Velocity_Deceleration_BelowPrevious_IsBelow50()
    {
        var formula = new RadarScoreFormulaV3();
        var input = InputFrom(
            new[] { BuildSignal(strength: 1) },
            new[] { BuildSignal(strength: 10).Signal, BuildSignal(strength: 10).Signal });

        Assert.True(formula.Compute(input).Components.SignalVelocityScore < 50);
    }

    [Fact]
    public void Velocity_EqualActivity_Is50()
    {
        var formula = new RadarScoreFormulaV3();
        var input = InputFrom(
            new[] { BuildSignal(strength: 6) },
            new[] { BuildSignal(strength: 6).Signal });

        Assert.Equal(50, formula.Compute(input).Components.SignalVelocityScore);
    }

    [Fact]
    public void Velocity_EmptyPreviousWithCurrent_IsAbove50()
    {
        var formula = new RadarScoreFormulaV3();
        var input = InputFrom(
            new[] { BuildSignal(strength: 8) },
            Array.Empty<Signal>());

        Assert.True(formula.Compute(input).Components.SignalVelocityScore > 50);
    }

    [Fact]
    public void Opportunity_FallsAsAttentionRises_NeverZeroes()
    {
        var formula = new RadarScoreFormulaV3();

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
        var formula = new RadarScoreFormulaV3();
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
        var formula = new RadarScoreFormulaV3();
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
        var formula = new RadarScoreFormulaV3();
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
        var formula = new RadarScoreFormulaV3();
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
        var formula = new RadarScoreFormulaV3();

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

    // ---- v3 recalibration pins (spec 87) ----

    // Build N distinct-publisher Positive third-party NewsArticle signals (non-media type so mediaCount = 0),
    // all with strength 10 / confidence 1.0 / PrimarySource quality → Trajectory 100, EC 80, reach = N.
    private static IReadOnlyList<ScoringSignal> DistinctPublisherNews(int count) =>
        Enumerable.Range(0, count)
            .Select(i => BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                quality: EvidenceQuality.PrimarySource,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: $"publisher-{i}"))
            .ToArray();

    [Fact]
    public void Attention_HalfSaturationRaised_NormalCoverageNoLongerSaturates()
    {
        // Pin 1: reach ≈ 21 gave Att ≈ 81 under v2 (+5). Under v3 (+12) it lands in the ~60s.
        // Direct formula: 100·21/(21+12) = 63.6 → 64.
        var formula = new RadarScoreFormulaV3();

        var att = formula.Compute(InputFrom(DistinctPublisherNews(21))).Components.AttentionScore;

        Assert.True(att < 70, $"expected normal coverage no longer to saturate; got {att}");
        Assert.InRange(att, 60, 66); // 100·21/33 = 63.6 → 64
    }

    [Fact]
    public void Attention_MediaTermDownWeighted_BreadthDominatesRawMediaVolume()
    {
        // Pin 2: same distinct-publisher breadth (3), differing MediaAttention counts. The raw media term is
        // now 0.25/signal, so adding 4 media signals (reach +1.0) lifts Attention far less than adding 4 new
        // distinct publishers (reach +4.0). Breadth dominates raw media volume.
        var formula = new RadarScoreFormulaV3();

        var basePublishers = new[]
        {
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "P1"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "P2"),
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: "P3"),
        };
        var attBase = formula.Compute(InputFrom(basePublishers)).Components.AttentionScore;

        // Same 3-publisher breadth (media signals reuse P1's SourceName so distinct breadth stays 3), but
        // 4 extra MediaAttention signals → mediaCount 4, reach 3 + 0.25·4 = 4.
        var moreMedia = basePublishers.Concat(Enumerable.Range(0, 4).Select(_ =>
            BuildSignal(type: SignalType.MediaAttention, direction: SignalDirection.Neutral,
                sourceType: EvidenceSourceType.NewsArticle, sourceName: "P1"))).ToArray();
        var attMoreMedia = formula.Compute(InputFrom(moreMedia)).Components.AttentionScore;

        // Instead add 4 NEW distinct publishers (non-media) → distinct breadth 7, reach 7, mediaCount 0.
        var moreBreadth = basePublishers.Concat(Enumerable.Range(0, 4).Select(i =>
            BuildSignal(sourceType: EvidenceSourceType.NewsArticle, sourceName: $"Q{i}"))).ToArray();
        var attMoreBreadth = formula.Compute(InputFrom(moreBreadth)).Components.AttentionScore;

        var mediaDelta = attMoreMedia - attBase;
        var breadthDelta = attMoreBreadth - attBase;

        Assert.True(mediaDelta > 0, "the raw media term still contributes something (0.25/signal)");
        Assert.True(mediaDelta < breadthDelta,
            $"breadth must dominate raw media volume; mediaDelta={mediaDelta}, breadthDelta={breadthDelta}");
    }

    [Fact]
    public void Opportunity_DiscountSoftened_SaturatedCompanyKeepsMostOfBase()
    {
        // Pin 3: a widely-covered company (many distinct publishers → Att in the 60s) is now only modestly
        // discounted — it keeps more than 70% of its Trajectory·EC base (was ~40% haircut under /200).
        var formula = new RadarScoreFormulaV3();

        var c = formula.Compute(InputFrom(DistinctPublisherNews(21))).Components;

        // Att ≈ 64 (60s–70s band).
        Assert.InRange(c.AttentionScore, 60, 75);

        var baseScore = c.TrajectoryScore * (c.EvidenceConfidenceScore / 100.0);
        Assert.True(c.OpportunityScore > 0.70 * baseScore,
            $"saturated company should keep >70% of base; Opp={c.OpportunityScore}, base={baseScore}");
    }

    [Fact]
    public void Opportunity_UnderTheRadarUpliftPreserved_LowAttentionBeatsSaturated()
    {
        // Pin 4: a genuinely under-followed company (1–2 distinct publishers → Att ~15) keeps ≥90% of its
        // base, and — sharing the same Trajectory·EC drivers — outranks the saturated company from pin 3.
        var formula = new RadarScoreFormulaV3();

        var low = formula.Compute(InputFrom(DistinctPublisherNews(2))).Components;   // reach 2 → Att ≈ 14
        var high = formula.Compute(InputFrom(DistinctPublisherNews(21))).Components; // reach 21 → Att ≈ 64

        // Same Trajectory·EC drivers, so the bases match.
        Assert.Equal(low.TrajectoryScore, high.TrajectoryScore);
        Assert.Equal(low.EvidenceConfidenceScore, high.EvidenceConfidenceScore);

        var baseScore = low.TrajectoryScore * (low.EvidenceConfidenceScore / 100.0);
        Assert.InRange(low.AttentionScore, 10, 20);
        Assert.True(low.OpportunityScore >= 0.90 * baseScore,
            $"under-the-radar company should keep ≥90% of base; Opp={low.OpportunityScore}, base={baseScore}");

        // Low attention still earns the biggest opportunity uplift (ordered above the widely-covered name).
        Assert.True(low.OpportunityScore > high.OpportunityScore);
    }

    [Fact]
    public void Opportunity_NeverZeroes_EvenAtMaximalAttention()
    {
        // Pin 5: even pushing Attention near 100 (max haircut 100/250 = 40%), Opportunity stays > 0 for a
        // positive Trajectory·EC base.
        var formula = new RadarScoreFormulaV3();

        var c = formula.Compute(InputFrom(DistinctPublisherNews(240))).Components; // reach 240 → Att ≈ 95

        Assert.True(c.AttentionScore >= 90, $"expected near-maximal attention; got {c.AttentionScore}");
        Assert.True(c.OpportunityScore > 0, $"opportunity must never zero out; got {c.OpportunityScore}");
    }
}
