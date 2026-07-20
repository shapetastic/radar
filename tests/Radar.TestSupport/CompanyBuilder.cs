using Radar.Domain.Companies;

namespace Radar.TestSupport;

public sealed class CompanyBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Acme Corp";
    private string? _legalName = "Acme Corporation Inc.";
    private string? _ticker = "ACME";
    private string? _exchange = "NASDAQ";
    private string? _countryCode = "US";
    private string? _sector = "Technology";
    private string? _industry = "Software";
    private CompanyStatus _status = CompanyStatus.Active;
    private DateTimeOffset _createdAtUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _updatedAtUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private FollowingTier _followingTier = FollowingTier.Small;

    public CompanyBuilder WithId(Guid v) { _id = v; return this; }
    public CompanyBuilder WithName(string v) { _name = v; return this; }
    public CompanyBuilder WithTicker(string? v) { _ticker = v; return this; }
    public CompanyBuilder WithStatus(CompanyStatus v) { _status = v; return this; }
    public CompanyBuilder WithFollowingTier(FollowingTier v) { _followingTier = v; return this; }

    public Company Build() => new(
        Id: _id,
        Name: _name,
        LegalName: _legalName,
        Ticker: _ticker,
        Exchange: _exchange,
        CountryCode: _countryCode,
        Sector: _sector,
        Industry: _industry,
        Status: _status,
        CreatedAtUtc: _createdAtUtc,
        UpdatedAtUtc: _updatedAtUtc,
        Themes: [],
        FollowingTier: _followingTier);
}
