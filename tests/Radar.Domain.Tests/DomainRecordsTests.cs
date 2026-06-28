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
        IReadOnlyList<string> themes = ["space", "defence"];

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
            UpdatedAtUtc: updatedAt,
            Themes: themes);

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
            UpdatedAtUtc: updatedAt,
            Themes: themes);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void CompanySourceFeed_WithIdenticalFields_AreEqual()
    {
        var id = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var a = new CompanySourceFeed(
            Id: id,
            CompanyId: companyId,
            FeedType: "rss",
            Name: "Acme Investor News",
            Url: "https://example.com/rss",
            CreatedAtUtc: createdAt);

        var b = new CompanySourceFeed(
            Id: id,
            CompanyId: companyId,
            FeedType: "rss",
            Name: "Acme Investor News",
            Url: "https://example.com/rss",
            CreatedAtUtc: createdAt);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
