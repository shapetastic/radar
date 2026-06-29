using Radar.Application.SignalExtraction;

namespace Radar.Application.Tests.SignalExtraction;

public class EvidenceSearchableTextTests
{
    [Fact]
    public void Compose_TitleAndBody_JoinsWithSingleNewline()
    {
        Assert.Equal("Headline\nBody", EvidenceSearchableText.Compose("Headline", "Body"));
    }

    [Fact]
    public void Compose_NullTitle_TreatedAsEmpty()
    {
        Assert.Equal("\nBody", EvidenceSearchableText.Compose(null, "Body"));
    }

    [Fact]
    public void Compose_NullRawText_TreatedAsEmpty()
    {
        Assert.Equal("Headline\n", EvidenceSearchableText.Compose("Headline", null));
    }

    [Fact]
    public void Compose_BothNull_YieldsSingleNewline()
    {
        Assert.Equal("\n", EvidenceSearchableText.Compose(null, null));
    }
}
