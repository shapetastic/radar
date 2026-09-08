namespace Radar.Infrastructure.Filings;

/// <summary>
/// Options for <see cref="FileReportedMetricStore"/> — the root directory of the append-only
/// reported-metrics ledger (spec 215 §1), laid out as <c>{RootDirectory}/{companyId}/{accession}.json</c>.
/// </summary>
public sealed class FileReportedMetricStoreOptions
{
    public required string RootDirectory { get; init; }
}
