namespace Radar.Domain.Companies;

public sealed record CompanyAlias(
    Guid Id,
    Guid CompanyId,
    string Alias,
    string AliasType,
    DateTimeOffset CreatedAtUtc);
