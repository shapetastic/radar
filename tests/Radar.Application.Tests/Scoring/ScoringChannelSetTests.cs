using Radar.Application.Scoring;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 146 — the channel budget's canonicalisation and its fail-fast validation. Every message must name
/// the strategy, because a mis-declared budget silently rescales every score that strategy produces and the
/// output itself gives no hint that it happened.
/// </summary>
public sealed class ScoringChannelSetTests
{
    private static ScoringChannel Collector(string name, double weight, double saturation = 3.0) =>
        ScoringChannel.Collector(name, [name], weight, saturation);

    [Fact]
    public void NullOrEmpty_CanonicalisesOntoEmpty_AndDescribesVerbatim()
    {
        Assert.Same(ScoringChannelSet.Empty, ScoringChannelSet.Create(null, "s"));
        Assert.Same(ScoringChannelSet.Empty, ScoringChannelSet.Create([], "s"));

        // The load-bearing property: a strategy with no channels hashes EXACTLY what it hashed before this
        // type existed, which is what keeps the pinned default fingerprints unmoved.
        Assert.Equal("rules=x;", ScoringChannelSet.Empty.Describe("rules=x;"));
        Assert.True(ScoringChannelSet.Empty.IsEmpty);
    }

    [Fact]
    public void NonEmpty_AppendsACanonicalSegment_AfterTheExistingOnes()
    {
        var set = ScoringChannelSet.Create(
            [Collector("patents", 0.5, 3), ScoringChannel.Breadth("attention", 0.5, 4)], "patents-led");

        Assert.Equal(
            "rules=x;channels=attention:breadth:0.5:4:,patents:collector:0.5:3:patents;",
            set.Describe("rules=x;"));
    }

    [Fact]
    public void ChannelOrder_AndCollectorOrder_AreIrrelevantToIdentity()
    {
        // Two operators writing the SAME budget in a different order have written the same strategy: it must
        // score identically AND hash identically, or a cosmetic config reshuffle would fork the series.
        var a = ScoringChannelSet.Create(
            [
                ScoringChannel.Collector("filings", ["sec-edgar", "sec-form4"], 0.4, 2),
                ScoringChannel.Breadth("attention", 0.6, 3),
            ],
            "s");
        var b = ScoringChannelSet.Create(
            [
                ScoringChannel.Breadth("attention", 0.6, 3),
                ScoringChannel.Collector("filings", ["sec-form4", "sec-edgar"], 0.4, 2),
            ],
            "s");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.Describe("d;"), b.Describe("d;"));
        // The RUNTIME order is canonicalised too, so the composite's summation order is a property of the
        // strategy rather than of how its channels happened to be listed.
        Assert.Equal(["attention", "filings"], a.Channels.Select(c => c.Name));
        Assert.Equal(["attention", "filings"], b.Channels.Select(c => c.Name));
    }

    [Fact]
    public void DifferentWeights_AreDifferentIdentities()
    {
        var a = ScoringChannelSet.Create([Collector("p", 0.5), Collector("q", 0.5)], "s");
        var b = ScoringChannelSet.Create([Collector("p", 0.6), Collector("q", 0.4)], "s");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.Describe("d;"), b.Describe("d;"));
    }

    [Fact]
    public void DifferentSaturations_AreDifferentIdentities()
    {
        var a = ScoringChannelSet.Create([Collector("p", 1.0, saturation: 3)], "s");
        var b = ScoringChannelSet.Create([Collector("p", 1.0, saturation: 9)], "s");

        Assert.NotEqual(a.Describe("d;"), b.Describe("d;"));
    }

    [Fact]
    public void NestedDelimitersInNames_StayInjective()
    {
        // A channel name containing the nested list separators must not be able to impersonate a different
        // budget (AD-3 injectivity). ':' and '|' are escaped by DescriptorEscaping.EscapeNested.
        var spliced = ScoringChannelSet.Create(
            [ScoringChannel.Collector("a:collector:1:1:b", ["x"], 1.0, 3)], "s");
        var honest = ScoringChannelSet.Create(
            [ScoringChannel.Collector("a", ["x"], 1.0, 3)], "s");

        Assert.NotEqual(spliced.Describe("d;"), honest.Describe("d;"));
        Assert.DoesNotContain("a:collector:1:1:b", spliced.Describe("d;"), StringComparison.Ordinal);
    }

    [Fact]
    public void WeightsNotSummingToOne_FailFast_NamingTheStrategyAndTheActualSum()
    {
        // THE typo this validation exists for: a budget that does not add up silently rescales every score
        // the strategy produces, and nothing in the output reveals it.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringChannelSet.Create([Collector("p", 0.5), Collector("q", 0.3)], "patents-led"));

        Assert.Contains("patents-led", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.8", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not 1.0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WeightsSummingToOneWithinFloatError_AreAccepted()
    {
        // 0.5 + 0.3 + 0.2 is 0.9999999999999999 in IEEE-754. The tolerance exists for exactly this and for
        // nothing looser: a genuinely unbalanced budget (above) still fails.
        var set = ScoringChannelSet.Create(
            [Collector("a", 0.5), Collector("b", 0.3), Collector("c", 0.2)], "s");

        Assert.Equal(3, set.Channels.Count);
    }

    [Theory]
    [InlineData(-0.1, 1.1)]
    [InlineData(1.5, -0.5)]
    public void WeightOutsideUnitRange_FailsFast_NamingTheStrategyAndTheChannel(double a, double b)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringChannelSet.Create([Collector("good", a), Collector("bad", b)], "patents-led"));

        Assert.Contains("patents-led", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[0, 1]", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void NonPositiveSaturation_FailsFast(double saturation)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringChannelSet.Create([Collector("p", 1.0, saturation)], "patents-led"));

        Assert.Contains("patents-led", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Saturation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankChannelName_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringChannelSet.Create(
                [ScoringChannel.Collector("   ", ["x"], 1.0, 3)], "patents-led"));

        Assert.Contains("blank Name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateChannelName_FailsFast_CaseInsensitively()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringChannelSet.Create(
                [
                    ScoringChannel.Collector("patents", ["a"], 0.5, 3),
                    ScoringChannel.Collector("PATENTS", ["b"], 0.5, 3),
                ],
                "patents-led"));

        Assert.Contains("duplicate channel Name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BreadthChannelDeclaringCollectors_FailsFast()
    {
        var scoped = new ScoringChannel("attention", ScoringChannelKind.Breadth, ["newssearch"], 1.0, 3);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringChannelSet.Create([scoped], "patents-led"));

        Assert.Contains("cross-source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectorChannelDeclaringNoCollectors_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringChannelSet.Create(
                [ScoringChannel.Collector("patents", null, 1.0, 3)], "patents-led"));

        Assert.Contains("declares no collectors", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectorList_IsCanonicalisedOrdinally_SoACasingNearMissSurvivesToTheStartupCheck()
    {
        var channel = ScoringChannel.Collector(
            "sources", [" sec-form4 ", "patents", "sec-form4", "", "Patents"], 1.0, 3);

        // Trimmed, blank-free, Ordinal-ordered, and EXACT duplicates collapsed...
        // ...but "Patents" is NOT collapsed onto "patents": collector names are matched exactly everywhere
        // else, so a case-insensitive de-dupe would swallow the typo before ScoringStrategyFactory could
        // reject it against the registered collectors — and which spelling survived would be config-order
        // dependent.
        Assert.Equal(["Patents", "patents", "sec-form4"], channel.Collectors);
    }

    [Fact]
    public void Consumes_MatchesExactly_AndNeverMatchesUnrecordedProvenance()
    {
        var channel = ScoringChannel.Collector("filings", ["sec-form4"], 1.0, 3);

        Assert.True(channel.Consumes("sec-form4"));
        // Exact (ordinal) matching: a case near-miss is caught at startup by the strategy factory, not
        // silently absorbed here.
        Assert.False(channel.Consumes("SEC-Form4"));
        // Legacy evidence carries no recorded collector — it is consumed by no channel and contributes 0.
        Assert.False(channel.Consumes(null));
        Assert.False(channel.Consumes("   "));
    }
}
