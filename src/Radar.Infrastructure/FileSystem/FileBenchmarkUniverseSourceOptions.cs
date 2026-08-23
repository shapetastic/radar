namespace Radar.Infrastructure.FileSystem;

/// <summary>Options for <see cref="FileBenchmarkUniverseSource"/> — the committed frozen-universe artifact path.</summary>
public sealed class FileBenchmarkUniverseSourceOptions
{
    public required string FilePath { get; init; }
}
