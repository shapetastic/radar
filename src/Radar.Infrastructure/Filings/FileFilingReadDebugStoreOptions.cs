namespace Radar.Infrastructure.Filings;

/// <summary>
/// Options for <see cref="FileFilingReadDebugStore"/> — the root directory for the per-accession
/// <c>{accession}.json</c> AI filing-read debug records (spec 115, opt-in diagnostic-only).
/// </summary>
public sealed class FileFilingReadDebugStoreOptions
{
    public required string RootDirectory { get; init; }
}
