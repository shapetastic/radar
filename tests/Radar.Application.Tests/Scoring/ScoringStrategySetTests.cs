using Radar.Application.Scoring;
using Radar.Domain.Signals;

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
    public void SignalTypes_DefaultToAllTypes()
    {
        // Spec 138: a definition that says nothing about signal types consumes everything, which is what keeps
        // every pre-138 construction site (and the synthesised default) byte-identical.
        Assert.Same(SignalTypeFilter.All, Def("alpha", primary: true).SignalTypes);
        Assert.Same(
            SignalTypeFilter.All,
            Assert.Single(ScoringStrategySet.SingleDefault(new ScoringWeights()).Strategies).SignalTypes);
    }

    [Fact]
    public void NullSignalTypes_IsRejected()
    {
        // Only reachable by explicitly nulling the defaulted property; failing fast beats silently scoring a
        // strategy as "all types" when the operator meant it to be narrow.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet([Def("alpha", primary: true) with { SignalTypes = null! }]));

        Assert.Contains("SignalTypes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignalTypes_ArePreservedOnTheSet()
    {
        var narrow = Def("alpha", primary: true) with
        {
            SignalTypes = SignalTypeFilter.Create([SignalType.InsiderBuying]),
        };

        var set = new ScoringStrategySet([narrow]);

        Assert.Equal([SignalType.InsiderBuying], Assert.Single(set.Strategies).SignalTypes.Types);
    }

    [Fact]
    public void ConfiguredOrder_IsPreserved()
    {
        var set = new ScoringStrategySet([Def("alpha", false), Def("beta", true), Def("gamma", false)]);

        Assert.Equal(["alpha", "beta", "gamma"], set.Strategies.Select(s => s.Name).ToArray());
        Assert.Equal("beta", set.Primary.Name);
    }
}
