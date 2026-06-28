using Radar.Domain.Companies;

namespace Radar.Application.EntityResolution;

/// <summary>
/// The watch-universe to seed: companies (each carrying its themes via <see cref="Company.Themes"/>),
/// their aliases, and their source feeds.
/// </summary>
public sealed record CompanySeedData(
    IReadOnlyList<Company> Companies,
    IReadOnlyList<CompanyAlias> Aliases,
    IReadOnlyList<CompanySourceFeed> SourceFeeds);
