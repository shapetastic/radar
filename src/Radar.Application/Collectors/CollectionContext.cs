using Radar.Domain.Companies;

namespace Radar.Application.Collectors;

/// <summary>
/// The watch universe Radar hands every collector at collection time. A company-specific collector
/// uses <see cref="Companies"/> for company-hint resolution and <see cref="SourceFeeds"/> to know
/// which feeds to fetch (the bound CompanyId is the high-confidence company hint). Kept a record so
/// later slices can extend it without breaking callers.
/// </summary>
public sealed record CollectionContext(
    IReadOnlyList<Company> Companies,
    IReadOnlyList<CompanySourceFeed> SourceFeeds)
{
    /// <summary>
    /// Convenience constructor for callers/tests that only supply companies; source feeds default to
    /// empty.
    /// </summary>
    public CollectionContext(IReadOnlyList<Company> companies) : this(companies, []) { }
}
