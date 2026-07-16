namespace Radar.Infrastructure.Filings;

/// <summary>
/// Options for <see cref="FileAnalyzedFilingCache"/> — the root directory for the per-accession
/// <c>{accession}.json</c> earnings-analysis-result cache files (spec 107).
/// </summary>
public sealed class FileAnalyzedFilingCacheOptions
{
    public required string RootDirectory { get; init; }
}
