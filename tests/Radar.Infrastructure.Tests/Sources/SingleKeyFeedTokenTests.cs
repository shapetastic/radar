using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

public sealed class SingleKeyFeedTokenTests
{
    [Fact]
    public void TrySplit_KeyPresent_ReturnsTrimmedValue()
    {
        var ok = SingleKeyFeedToken.TrySplit("assignee=Mercury Systems, Inc.", "assignee=", out var value);

        Assert.True(ok);
        Assert.Equal("Mercury Systems, Inc.", value);
    }

    [Fact]
    public void TrySplit_ValueHasSurroundingWhitespace_IsTrimmed()
    {
        var ok = SingleKeyFeedToken.TrySplit("applicant=  TransMedics  ", "applicant=", out var value);

        Assert.True(ok);
        Assert.Equal("TransMedics", value);
    }

    [Fact]
    public void TrySplit_KeyMissing_ReturnsFalseWithEmptyValue()
    {
        var ok = SingleKeyFeedToken.TrySplit("platform=greenhouse", "assignee=", out var value);

        Assert.False(ok);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TrySplit_KeyPresentButValueEmpty_ReturnsTrueWithEmptyValue()
    {
        // Blank-value policy is the CALLER's — the splitter returns true with an empty value.
        var ok = SingleKeyFeedToken.TrySplit("applicant=", "applicant=", out var value);

        Assert.True(ok);
        Assert.Equal(string.Empty, value);
    }
}
