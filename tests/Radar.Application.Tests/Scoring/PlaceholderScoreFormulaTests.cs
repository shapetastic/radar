using System.Text.Json;
using Radar.Application.Scoring;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

public sealed class PlaceholderScoreFormulaTests
{
    private static ScoringSignal BuildSignal(int strength, SignalDirection direction = SignalDirection.Positive)
    {
        var evidence = new EvidenceBuilder().Build();
        var signal = new SignalBuilder()
            .WithEvidenceId(evidence.Id)
            .WithStrength(strength)
            .WithDirection(direction)
            .Build();
        return new ScoringSignal(signal, evidence);
    }

    private static ScoringInput InputFrom(params ScoringSignal[] signals) => new(
        CompanyId: Guid.NewGuid(),
        WindowStartUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        WindowEndUtc: new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero),
        Signals: signals,
        PreviousSignals: Array.Empty<Signal>());

    private static void AssertComponentsInRange(ScoreComponents c)
    {
        Assert.InRange(c.TrajectoryScore, 0, 100);
        Assert.InRange(c.OpportunityScore, 0, 100);
        Assert.InRange(c.AttentionScore, 0, 100);
        Assert.InRange(c.EvidenceConfidenceScore, 0, 100);
        Assert.InRange(c.SignalVelocityScore, 0, 100);
    }

    [Fact]
    public void Compute_AllComponentsInRange()
    {
        var formula = new PlaceholderScoreFormula();
        var input = InputFrom(BuildSignal(3), BuildSignal(6, SignalDirection.Neutral), BuildSignal(8));

        var result = formula.Compute(input);

        AssertComponentsInRange(result.Components);
    }

    [Fact]
    public void Compute_EmitsOneContributionPerSignalInOrder()
    {
        var formula = new PlaceholderScoreFormula();
        var input = InputFrom(BuildSignal(2), BuildSignal(5), BuildSignal(9));

        var result = formula.Compute(input);

        Assert.Equal(input.Signals.Count, result.Contributions.Count);
        for (var i = 0; i < input.Signals.Count; i++)
        {
            Assert.Equal(input.Signals[i].Signal.Id, result.Contributions[i].SignalId);
            Assert.Equal(input.Signals[i].Evidence.Id, result.Contributions[i].EvidenceId);
        }
    }

    [Fact]
    public void Compute_ContributionWeightsAreInSaneBound()
    {
        var formula = new PlaceholderScoreFormula();
        var input = InputFrom(BuildSignal(1), BuildSignal(7), BuildSignal(10));

        var result = formula.Compute(input);

        Assert.All(result.Contributions, c => Assert.True(c.ContributionWeight >= 0));
    }

    [Fact]
    public void Compute_EmptyInput_ReturnsValidComputation()
    {
        var formula = new PlaceholderScoreFormula();
        var input = InputFrom();

        var result = formula.Compute(input);

        AssertComponentsInRange(result.Components);
        Assert.Empty(result.Contributions);
        Assert.False(string.IsNullOrWhiteSpace(result.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(result.ComponentJson));

        var roundTripped = JsonSerializer.Deserialize<ScoreComponents>(result.ComponentJson);
        Assert.NotNull(roundTripped);
    }

    [Fact]
    public void Compute_MaxStrengthSignals_ClampBoundHolds()
    {
        var formula = new PlaceholderScoreFormula();
        var manyMax = new List<ScoringSignal>();
        for (var i = 0; i < 50; i++)
        {
            manyMax.Add(BuildSignal(10));
        }
        var input = InputFrom([.. manyMax]);

        var result = formula.Compute(input);

        AssertComponentsInRange(result.Components);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var formula = new PlaceholderScoreFormula();
        var input = InputFrom(BuildSignal(4), BuildSignal(6, SignalDirection.Negative), BuildSignal(8));

        var first = formula.Compute(input);
        var second = formula.Compute(input);

        Assert.Equal(first.Components, second.Components);
        Assert.Equal(first.ComponentJson, second.ComponentJson);
        Assert.Equal(first.Explanation, second.Explanation);
        // Compare contributions element-wise: ScoreContribution is a record (structural equality),
        // but the enclosing List<T> does not, so we cannot compare ScoreComputation as a whole.
        Assert.Equal(first.Contributions.Count, second.Contributions.Count);
        Assert.Equal(first.Contributions, second.Contributions);
    }

    [Fact]
    public void Version_IsPlaceholderV0_AndAppearsInExplanation()
    {
        var formula = new PlaceholderScoreFormula();

        Assert.Equal("placeholder-v0", formula.Version);

        var result = formula.Compute(InputFrom(BuildSignal(5)));
        Assert.Contains("placeholder-v0", result.Explanation);
    }
}
