using System.Text.Json;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class RadarScoreFormulaV1Tests
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
    public void Version_IsRadarFormulaV1_AndAppearsInExplanation()
    {
        var formula = new RadarScoreFormulaV1();

        Assert.Equal("radar-formula-v1", formula.Version);

        var result = formula.Compute(InputFrom(new[] { BuildSignal() }));
        Assert.Contains("radar-formula-v1", result.Explanation);
    }

    [Fact]
    public void NeutralBaseline_SingleNeutralSignal_TrajectoryIs50()
    {
        var formula = new RadarScoreFormulaV1();
        var input = InputFrom(new[] { BuildSignal(direction: SignalDirection.Neutral) });

        var result = formula.Compute(input);

        Assert.Equal(50, result.Components.TrajectoryScore);
    }

    [Fact]
    public void AllPositive_Improves_AllNegative_Declines()
    {
        var formula = new RadarScoreFormulaV1();

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
        var formula = new RadarScoreFormulaV1();
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
        var formula = new RadarScoreFormulaV1();

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
    public void Attention_Saturates_AndIsMonotonic()
    {
        var formula = new RadarScoreFormulaV1();

        var one = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceName: "only-source"),
        }));

        var five = formula.Compute(InputFrom(new[]
        {
            BuildSignal(sourceName: "src-a"),
            BuildSignal(sourceName: "src-b"),
            BuildSignal(sourceName: "src-c"),
            BuildSignal(sourceName: "src-d"),
            BuildSignal(sourceName: "src-e"),
        }));

        Assert.True(five.Components.AttentionScore > one.Components.AttentionScore);
        Assert.True(five.Components.AttentionScore < 100);
    }

    [Fact]
    public void EvidenceConfidence_RewardsQualityAndDiversity()
    {
        var formula = new RadarScoreFormulaV1();

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
    public void Velocity_Acceleration_AbovePrevious_IsAbove50()
    {
        var formula = new RadarScoreFormulaV1();
        var input = InputFrom(
            new[] { BuildSignal(strength: 10), BuildSignal(strength: 10) },
            new[] { BuildSignal(strength: 1).Signal });

        Assert.True(formula.Compute(input).Components.SignalVelocityScore > 50);
    }

    [Fact]
    public void Velocity_Deceleration_BelowPrevious_IsBelow50()
    {
        var formula = new RadarScoreFormulaV1();
        var input = InputFrom(
            new[] { BuildSignal(strength: 1) },
            new[] { BuildSignal(strength: 10).Signal, BuildSignal(strength: 10).Signal });

        Assert.True(formula.Compute(input).Components.SignalVelocityScore < 50);
    }

    [Fact]
    public void Velocity_EqualActivity_Is50()
    {
        var formula = new RadarScoreFormulaV1();
        var input = InputFrom(
            new[] { BuildSignal(strength: 6) },
            new[] { BuildSignal(strength: 6).Signal });

        Assert.Equal(50, formula.Compute(input).Components.SignalVelocityScore);
    }

    [Fact]
    public void Velocity_EmptyPreviousWithCurrent_IsAbove50()
    {
        var formula = new RadarScoreFormulaV1();
        var input = InputFrom(
            new[] { BuildSignal(strength: 8) },
            Array.Empty<Signal>());

        Assert.True(formula.Compute(input).Components.SignalVelocityScore > 50);
    }

    [Fact]
    public void Opportunity_FallsAsAttentionRises_NeverZeroes()
    {
        var formula = new RadarScoreFormulaV1();

        // Low attention: single source.
        var lowAttention = formula.Compute(InputFrom(new[]
        {
            BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                quality: EvidenceQuality.PrimarySource, sourceName: "only"),
        }));

        // High attention: many distinct sources + media attention, same trajectory/confidence drivers.
        var highSignals = new List<ScoringSignal>();
        for (var i = 0; i < 8; i++)
        {
            highSignals.Add(BuildSignal(strength: 10, confidence: 1.0m, direction: SignalDirection.Positive,
                quality: EvidenceQuality.PrimarySource, type: SignalType.MediaAttention,
                sourceName: $"src-{i}"));
        }
        var highAttention = formula.Compute(InputFrom(highSignals));

        Assert.True(highAttention.Components.AttentionScore > lowAttention.Components.AttentionScore);
        Assert.True(highAttention.Components.OpportunityScore <= lowAttention.Components.OpportunityScore);
        Assert.True(highAttention.Components.OpportunityScore > 0);
    }

    [Fact]
    public void Contributions_OnePerCurrentSignal_InOrder_WithProvenance_AndSignedWeight()
    {
        var formula = new RadarScoreFormulaV1();
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
    public void EmptyCurrentWindow_EvenWithPrevious_AllZero_EmptyContributions_ValidJson()
    {
        var formula = new RadarScoreFormulaV1();
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
        var formula = new RadarScoreFormulaV1();
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
        var formula = new RadarScoreFormulaV1();

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
}
