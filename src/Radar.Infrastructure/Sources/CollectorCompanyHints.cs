using Radar.Domain.Companies;

namespace Radar.Infrastructure.Sources;

/// <summary>
/// Shared, deterministic company-hint builder for the fetching collectors. Given a bound companyId and
/// the run's company lookup, returns the high-confidence hint from the configured binding: prefer the
/// ticker, fall back to the name, never invent a ticker. Returns an empty list when the company is
/// unknown or has neither a ticker nor a name. Consolidates the byte-identical helper previously copied
/// into the RSS, SEC, USASpending, and GDELT collectors (LocalFileEvidenceCollector deliberately uses
/// no hints and does not call this).
/// </summary>
internal static class CollectorCompanyHints
{
    public static IReadOnlyList<string> For(
        Guid companyId, IReadOnlyDictionary<Guid, Company> companiesById)
    {
        if (!companiesById.TryGetValue(companyId, out var company))
        {
            return [];
        }

        // High-confidence hint from the configured binding: prefer the ticker, fall back to the name.
        // Never invent a ticker.
        if (!string.IsNullOrWhiteSpace(company.Ticker))
        {
            return [company.Ticker];
        }

        return string.IsNullOrWhiteSpace(company.Name) ? [] : [company.Name];
    }
}
