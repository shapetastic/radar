using Radar.Domain.Companies;

namespace Radar.Application.EntityResolution;

/// <summary>The watch-universe to seed: companies and their aliases.</summary>
public sealed record CompanySeedData(
    IReadOnlyList<Company> Companies,
    IReadOnlyList<CompanyAlias> Aliases);
