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
}
