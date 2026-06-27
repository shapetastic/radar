namespace Radar.Domain.Companies;

public sealed record Company(
    Guid Id,
    string Name,
    string? LegalName,
    string? Ticker,
    string? Exchange,
    string? CountryCode,
    string? Sector,
    string? Industry,
    CompanyStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
