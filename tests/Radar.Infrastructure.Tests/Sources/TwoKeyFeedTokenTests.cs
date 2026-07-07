using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// Parity tests for the shared two-key token splitter that <c>QueryFeedTarget</c>,
/// <c>UsaSpendingFeedTarget</c>, and <c>HiringFeedTarget</c> all route through (spec 103's
/// reuse-over-copy extraction). The per-parser tests remain the behavioural source of truth; these pin
/// the shared split semantics directly (order robustness, first-<c>&amp;</c> boundary, trimming, and the
/// caller-owns-blank-values contract).
/// </summary>
public sealed class TwoKeyFeedTokenTests
{
    private const string KeyA = "alpha=";
    private const string KeyB = "beta=";

    [Fact]
    public void TrySplit_CanonicalOrder_SplitsBothValues()
    {
        var ok = TwoKeyFeedToken.TrySplit("alpha=one two&beta=three", KeyA, KeyB, out var a, out var b);

        Assert.True(ok);
        Assert.Equal("one two", a); // literal spaces inside a value are preserved (no URL decoding)
        Assert.Equal("three", b);
    }

    [Fact]
    public void TrySplit_ReversedOrder_SplitsBothValues()
    {
        var ok = TwoKeyFeedToken.TrySplit("beta=three&alpha=one two", KeyA, KeyB, out var a, out var b);

        Assert.True(ok);
        Assert.Equal("one two", a);
        Assert.Equal("three", b);
    }

    [Fact]
    public void TrySplit_ValuesAreTrimmed()
    {
        var ok = TwoKeyFeedToken.TrySplit("alpha= padded &beta= also ", KeyA, KeyB, out var a, out var b);

        Assert.True(ok);
        Assert.Equal("padded", a);
        Assert.Equal("also", b);
    }

    [Fact]
    public void TrySplit_BlankValues_ComeBackEmpty_CallerDecidesPolicy()
    {
        // The splitter reports blank values as empty strings; rejecting (UsaSpending/Hiring) or
        // tolerating (QueryFeedTarget's optional ticker) them is the CALLER's hook.
        var ok = TwoKeyFeedToken.TrySplit("alpha=&beta=", KeyA, KeyB, out var a, out var b);

        Assert.True(ok);
        Assert.Equal(string.Empty, a);
        Assert.Equal(string.Empty, b);
    }

    [Theory]
    [InlineData("alpha=one")]                // second key missing
    [InlineData("beta=three")]               // first key missing
    [InlineData("alpha=one beta=three")]     // no '&' boundary between the keys
    [InlineData("not-a-token")]              // neither key
    public void TrySplit_MalformedToken_ReturnsFalseWithEmptyValues(string token)
    {
        var ok = TwoKeyFeedToken.TrySplit(token, KeyA, KeyB, out var a, out var b);

        Assert.False(ok);
        Assert.Equal(string.Empty, a);
        Assert.Equal(string.Empty, b);
    }

    [Fact]
    public void TrySplit_UsesFirstBoundaryBetweenTheKeys()
    {
        // The FIRST '&' after the leading key's value start is the boundary; everything after the second
        // key belongs to it verbatim (our seeds never put '&' inside a value).
        var ok = TwoKeyFeedToken.TrySplit("alpha=one&beta=three&four", KeyA, KeyB, out var a, out var b);

        Assert.True(ok);
        Assert.Equal("one", a);
        Assert.Equal("three&four", b);
    }
}
