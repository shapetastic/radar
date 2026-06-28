using Radar.Domain.Companies;

namespace Radar.Application.EntityResolution;

/// <summary>The watch-universe to seed: companies, their aliases, and their source feeds.</summary>
public sealed record CompanySeedData(
    IReadOnlyList<Company> Companies,
    IReadOnlyList<CompanyAlias> Aliases,
    IReadOnlyList<CompanySourceFeed> SourceFeeds);
