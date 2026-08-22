namespace Radar.Infrastructure.News;

/// <summary>Root directory options for the file news-observation archive (spec 177).</summary>
public sealed class NewsObservationArchiveOptions
{
    public required string RootDirectory { get; init; }
}
