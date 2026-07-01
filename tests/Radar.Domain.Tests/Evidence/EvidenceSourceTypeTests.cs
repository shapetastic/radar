using Radar.Domain.Evidence;

namespace Radar.Domain.Tests.Evidence;

public sealed class EvidenceSourceTypeTests
{
    [Theory]
    [InlineData("RegulatoryAnnouncement")]
    [InlineData("InsiderTransaction")]
    [InlineData("ConferenceMention")]
    public void NewSourceType_IsDefinedAndParsesByName(string name)
    {
        var parsed = Enum.Parse<EvidenceSourceType>(name);

        Assert.True(Enum.IsDefined(parsed));
        Assert.Equal(name, parsed.ToString());
    }

    [Fact]
    public void GovernmentContract_IsNotThirdPartyAttentionSource()
    {
        // A federal contract award is an official primary record (like a Filing), not third-party market
        // attention — it must contribute nothing to measured attention.
        Assert.False(
            EvidenceSourceTypes.IsThirdPartyAttentionSource(EvidenceSourceType.GovernmentContract));
    }
}
