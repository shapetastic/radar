namespace Radar.Infrastructure.FileSystem;

/// <summary>Options for <see cref="FileEfficacyArtifactStore"/> — the root directory for the per-ticker
/// <c>{ticker}.svg</c> / <c>{ticker}.csv</c> efficacy artifacts.</summary>
public sealed class FileEfficacyArtifactStoreOptions
{
    public required string RootDirectory { get; init; }
}
