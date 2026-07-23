using Radar.Infrastructure.Patents;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

public sealed class SingleKeyFeedTokenTests
{
    [Fact]
    public void TrySplit_ValidToken_ReturnsValuePreservingSpacesAndCommas()
    {
        var ok = SingleKeyFeedToken.TrySplit("grantee=Mercury Systems, Inc.", "grantee=", out var value);

        Assert.True(ok);
        // The value's own literal spaces and comma survive (the token is NOT URL-decoded).
        Assert.Equal("Mercury Systems, Inc.", value);
    }

    [Fact]
    public void TrySplit_TrimsValue()
    {
        // The caller trims the token; the splitter still trims the extracted value's surrounding whitespace.
        var ok = SingleKeyFeedToken.TrySplit("grantee=  Bel Fuse Inc.  ".Trim(), "grantee=", out var value);

        Assert.True(ok);
        Assert.Equal("Bel Fuse Inc.", value);
    }

    [Fact]
    public void TrySplit_MissingKey_ReturnsFalseWithEmptyValue()
    {
        var ok = SingleKeyFeedToken.TrySplit("https://example.com/rss", "grantee=", out var value);

        Assert.False(ok);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TrySplit_KeyPresentButBlankValue_ReturnsTrueWithEmptyValue()
    {
        // A present-but-empty value is a TRUE split with an empty value — the CALLER decides whether an empty
        // value is acceptable (both PatentFeedTarget and FccFeedTarget reject it).
        var ok = SingleKeyFeedToken.TrySplit("grantee=   ", "grantee=", out var value);

        Assert.True(ok);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void PatentFeedTarget_StillParsesIdentically_ThroughSharedSplitter()
    {
        // Regression guard (spec 128): routing PatentFeedTarget through the shared SingleKeyFeedToken must not
        // change its parsing behaviour — a valid assignee token still parses, preserving spaces/commas.
        var target = PatentFeedTarget.Parse("  assignee=Mercury Systems, Inc.  ");

        Assert.NotNull(target);
        Assert.Equal("Mercury Systems, Inc.", target.AssigneeName);

        // And the blank/missing-key cases still yield null.
        Assert.Null(PatentFeedTarget.Parse("assignee="));
        Assert.Null(PatentFeedTarget.Parse("assignee=   "));
        Assert.Null(PatentFeedTarget.Parse("no-key-here"));
    }
}
