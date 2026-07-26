using Radar.Application.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// The ONE definition of the score-series key (spec 141). These assertions are what every consumer — the
/// weekly report's comparability gate and the efficacy segmentation — inherits by routing through it.
/// </summary>
public sealed class ScoreSeriesKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOrNullStrategyName_IsThePrimaryDefaultSeries(string? name)
    {
        // Legacy (pre-137) snapshots and any snapshot produced outside the strategy composition carry a null
        // name. They ARE the default strategy's history, so they must not be orphaned into a nameless series.
        Assert.Equal(ScoringStrategySet.DefaultStrategyName, ScoreSeriesKey.For(name));
        Assert.Equal("default", ScoreSeriesKey.For(name));
    }

    [Fact]
    public void NamedStrategy_KeepsItsOwnSeries()
    {
        Assert.Equal("insider-only", ScoreSeriesKey.For("insider-only"));
        Assert.False(ScoreSeriesKey.SameSeries("insider-only", "momentum"));
        Assert.False(ScoreSeriesKey.SameSeries("insider-only", null));
    }

    [Fact]
    public void LegacyNullAndExplicitDefault_AreTheSameSeries()
    {
        Assert.True(ScoreSeriesKey.SameSeries(null, "default"));
        Assert.True(ScoreSeriesKey.SameSeries("default", null));
        Assert.True(ScoreSeriesKey.SameSeries(null, null));
    }

    [Fact]
    public void ComparisonIsCaseInsensitive_MatchingStrategySetUniqueness()
    {
        // ScoringStrategySet rejects two strategies whose names differ only by case, so two such names can
        // never denote two distinct strategies — and therefore must never read as two distinct series.
        Assert.True(ScoreSeriesKey.SameSeries("Momentum", "momentum"));
        Assert.True(ScoreSeriesKey.SameSeries("DEFAULT", null));
    }

    [Fact]
    public void SnapshotOverload_ReadsTheStrategyName_NotTheFingerprint()
    {
        // The fingerprint is deliberately irrelevant to the key: two snapshots of one strategy with different
        // ScoringConfigVersions are ONE series (that is the whole spec-141 reversal).
        var a = new ScoreSnapshotBuilder()
            .WithStrategyName("momentum")
            .WithScoringConfigVersion("radar-scoring-fp-aaaa")
            .Build();
        var b = new ScoreSnapshotBuilder()
            .WithStrategyName("momentum")
            .WithScoringConfigVersion("radar-scoring-fp-bbbb")
            .Build();

        Assert.Equal("momentum", ScoreSeriesKey.For(a));
        Assert.Equal(ScoreSeriesKey.For(a), ScoreSeriesKey.For(b));
    }

    [Fact]
    public void NullSnapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ScoreSeriesKey.For((Radar.Domain.Scoring.CompanyScoreSnapshot)null!));
    }
}
