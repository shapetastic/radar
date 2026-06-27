using Radar.Domain.Evidence;
using Radar.Domain.Validation;
using Radar.TestSupport;

namespace Radar.Domain.Tests.TestSupport;

public class BuildersTests
{
    [Fact]
    public void SignalBuilder_Default_IsValid_AndHasNonEmptyEvidenceId()
    {
        var signal = new SignalBuilder().Build();

        Assert.True(SignalValidation.IsValid(signal));
        Assert.NotEqual(Guid.Empty, signal.EvidenceId);
    }

    [Fact]
    public void EvidenceBuilder_Default_HasNonEmptyKeyFields()
    {
        var evidence = new EvidenceBuilder().Build();

        Assert.False(string.IsNullOrWhiteSpace(evidence.Title));
        Assert.False(string.IsNullOrWhiteSpace(evidence.RawText));
        Assert.False(string.IsNullOrWhiteSpace(evidence.ContentHash));
        Assert.False(string.IsNullOrWhiteSpace(evidence.SourceName));
    }

    [Fact]
    public void SignalBuilder_WithStrength_OverridesStrength()
    {
        var signal = new SignalBuilder().WithStrength(3).Build();

        Assert.Equal(3, signal.Strength);
    }

    [Fact]
    public void EvidenceBuilder_WithQuality_OverridesQuality()
    {
        var evidence = new EvidenceBuilder().WithQuality(EvidenceQuality.Low).Build();

        Assert.Equal(EvidenceQuality.Low, evidence.Quality);
    }
}
