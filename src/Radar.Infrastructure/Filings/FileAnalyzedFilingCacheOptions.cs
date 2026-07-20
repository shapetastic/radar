namespace Radar.Infrastructure.Filings;

/// <summary>
/// Options for <see cref="FileAnalyzedFilingCache"/> — the root directory for the per-accession
/// <c>{accession}.json</c> earnings-analysis-result cache files (spec 107).
/// </summary>
public sealed class FileAnalyzedFilingCacheOptions
{
    public required string RootDirectory { get; init; }

    /// <summary>
    /// An optional filename-safe sub-directory segment that scopes cache files to the analyzing model/provider
    /// identity (spec 118), so a model switch is a clean cache MISS (re-analyze) rather than a replay of another
    /// model's reads. Empty (the default) ⇒ files live directly under <see cref="RootDirectory"/> (back-compat).
    /// </summary>
    public string ModelSegment { get; init; } = string.Empty;
}
