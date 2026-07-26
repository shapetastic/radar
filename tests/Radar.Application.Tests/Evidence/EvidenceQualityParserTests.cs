using Radar.Application.Evidence;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.Evidence;

/// <summary>
/// The shared quality-parsing rule (spec 142). It is the SAME rule
/// <see cref="Radar.Application.Collectors.CollectedEvidenceMapper"/> applies at collection time and the
/// durable raw-evidence hydration path applies to a legacy file's persisted <c>metadata.quality</c> — so
/// recovering a legacy value reproduces exactly what the item carried when it was scored live.
/// </summary>
public sealed class EvidenceQualityParserTests
{
    [Theory]
    [InlineData("Unknown", EvidenceQuality.Unknown)]
    [InlineData("Low", EvidenceQuality.Low)]
    [InlineData("Medium", EvidenceQuality.Medium)]
    [InlineData("High", EvidenceQuality.High)]
    [InlineData("PrimarySource", EvidenceQuality.PrimarySource)]
    [InlineData("primarysource", EvidenceQuality.PrimarySource)]
    [InlineData("HIGH", EvidenceQuality.High)]
    public void DefinedNames_ParseCaseInsensitively(string declared, EvidenceQuality expected)
    {
        Assert.Equal(expected, EvidenceQualityParser.Parse(declared, out var status));
        Assert.Equal(EvidenceQualityParseStatus.Recognized, status);
    }

    [Fact]
    public void EveryDeclaredMember_RoundTripsThroughItsOwnName()
    {
        foreach (var quality in Enum.GetValues<EvidenceQuality>())
        {
            Assert.Equal(quality, EvidenceQualityParser.Parse(quality.ToString()));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3")]   // digit-only: Enum.TryParse would otherwise accept it as an ordinal
    [InlineData(" 42 ")]
    public void MissingBlankOrDigitOnly_IsUnknown(string? declared)
    {
        Assert.Equal(EvidenceQuality.Unknown, EvidenceQualityParser.Parse(declared, out var status));
        Assert.Equal(EvidenceQualityParseStatus.Missing, status);
    }

    [Theory]
    [InlineData("Excellent")]
    [InlineData("high-ish")]
    public void UnrecognizedName_IsUnknown_AndReportedDistinctly(string declared)
    {
        Assert.Equal(EvidenceQuality.Unknown, EvidenceQualityParser.Parse(declared, out var status));
        Assert.Equal(EvidenceQualityParseStatus.Unrecognized, status);
    }
}
