using System.Text.Json;

using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class RadarFileStoreJsonTests
{
    private enum SampleDirection
    {
        Negative,
        Positive,
    }

    private sealed record SampleRecord(SampleDirection SignalDirection, string CompanyName);

    [Fact]
    public void Options_SerializeEnum_EmitsNameNotOrdinal()
    {
        var json = JsonSerializer.Serialize(
            new SampleRecord(SampleDirection.Positive, "Northwind"),
            RadarFileStoreJson.Options);

        // Enum rendered as its string name, never the integer ordinal (1).
        Assert.Contains("\"Positive\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"signalDirection\": 1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_SerializeRecord_UsesCamelCasePropertyNames()
    {
        var json = JsonSerializer.Serialize(
            new SampleRecord(SampleDirection.Positive, "Northwind"),
            RadarFileStoreJson.Options);

        Assert.Contains("\"signalDirection\"", json, StringComparison.Ordinal);
        Assert.Contains("\"companyName\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SignalDirection\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CompanyName\"", json, StringComparison.Ordinal);
    }
}
