namespace Radar.Infrastructure.FileSystem;

/// <summary>Options for <see cref="FilePriceHistoryStore"/> — the root directory for <c>{ticker}.json</c> files.</summary>
public sealed class FilePriceHistoryStoreOptions
{
    public required string RootDirectory { get; init; }
}
