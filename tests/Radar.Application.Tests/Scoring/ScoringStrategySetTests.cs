using Radar.Application.Scoring;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 137 — the strategy-set invariants that hold regardless of how a set is composed (config binding or
/// direct construction). Each of these otherwise surfaces later as a confusing empty or mislabelled series.
/// </summary>
public sealed class ScoringStrategySetTests
{
    private static ScoringStrategyDefinition Def(string name, bool primary) =>
        new(name, "default", new ScoringWeights(), primary);

    [Fact]
    public void SingleDefault_IsOneNamedPrimaryStrategy()
    {
        var set = ScoringStrategySet.SingleDefault(new ScoringWeights());

        var only = Assert.Single(set.Strategies);
        Assert.Equal("default", only.Name);
        Assert.True(only.IsPrimary);
        Assert.Same(only, set.Primary);
    }

    [Fact]
    public void EmptySet_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new ScoringStrategySet([]));
    }

    [Fact]
    public void BlankName_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet([Def("  ", primary: true)]));

        Assert.Contains("Name", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a:b")]
    [InlineData(" padded")]
    public void UnusableName_IsRejected(string name)
    {
        Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet([Def(name, primary: true)]));
    }

    [Fact]
    public void DuplicateNames_AreRejectedCaseInsensitively()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet([Def("alpha", true), Def("ALPHA", false)]));

        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPrimary_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet([Def("alpha", false), Def("beta", false)]));
    }

    [Fact]
    public void TwoPrimaries_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet([Def("alpha", true), Def("beta", true)]));
    }

    [Fact]
    public void ConfiguredOrder_IsPreserved()
    {
        var set = new ScoringStrategySet([Def("alpha", false), Def("beta", true), Def("gamma", false)]);

        Assert.Equal(["alpha", "beta", "gamma"], set.Strategies.Select(s => s.Name).ToArray());
        Assert.Equal("beta", set.Primary.Name);
    }
}
