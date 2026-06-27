using Radar.Domain.Companies;

namespace Radar.Domain.Tests;

public class DomainRecordsTests
{
    [Fact]
    public void Company_WithIdenticalFields_AreEqual()
    {
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var updatedAt = createdAt.AddMinutes(5);

        var a = new Company(
            Id: id,
            Name: "Acme Corp",
            LegalName: "Acme Corporation Inc.",
            Ticker: "ACME",
            Exchange: "NASDAQ",
            CountryCode: "US",
            Sector: "Technology",
            Industry: "Software",
            Status: CompanyStatus.Active,
            CreatedAtUtc: createdAt,
            UpdatedAtUtc: updatedAt);

        var b = new Company(
            Id: id,
            Name: "Acme Corp",
            LegalName: "Acme Corporation Inc.",
            Ticker: "ACME",
            Exchange: "NASDAQ",
            CountryCode: "US",
            Sector: "Technology",
            Industry: "Software",
            Status: CompanyStatus.Active,
            CreatedAtUtc: createdAt,
            UpdatedAtUtc: updatedAt);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
