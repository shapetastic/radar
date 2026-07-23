using Radar.Infrastructure.Fda;

namespace Radar.Infrastructure.Tests.Fda;

public sealed class FdaFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsApplicant()
    {
        var target = FdaFeedTarget.Parse("applicant=Axogen");

        Assert.NotNull(target);
        Assert.Equal("Axogen", target.ApplicantName);
    }

    [Fact]
    public void Parse_ValueWithSpacesAndCommas_IsPreserved()
    {
        // The value may contain spaces/commas (the token is NOT URL-decoded).
        var target = FdaFeedTarget.Parse("applicant=TransMedics, Inc.");

        Assert.NotNull(target);
        Assert.Equal("TransMedics, Inc.", target.ApplicantName);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = FdaFeedTarget.Parse("  applicant=TransMedics  ");

        Assert.NotNull(target);
        Assert.Equal("TransMedics", target.ApplicantName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/rss")]
    [InlineData("assignee=Mercury Systems, Inc.")]
    [InlineData("applicant=")]
    [InlineData("applicant=   ")]
    public void Parse_MalformedOrMissingKey_ReturnsNull(string? token)
    {
        Assert.Null(FdaFeedTarget.Parse(token));
    }
}
