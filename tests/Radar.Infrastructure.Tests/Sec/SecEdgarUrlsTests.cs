using System.Text.RegularExpressions;

using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class SecEdgarUrlsTests
{
    [Theory]
    [InlineData("0000320193", "320193")]
    [InlineData("320193", "320193")]
    [InlineData("0000000000", "0")]
    [InlineData("0", "0")]
    [InlineData("", "0")]
    public void StripLeadingZeros_canonicalises_cik(string cik, string expected)
    {
        Assert.Equal(expected, SecEdgarUrls.StripLeadingZeros(cik));
    }

    [Fact]
    public void BuildArchiveBaseUrl_strips_cik_zeros_and_dedashes_accession()
    {
        var baseUrl = SecEdgarUrls.BuildArchiveBaseUrl("0000320193", "0000320193-23-000106");

        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/320193/000032019323000106",
            baseUrl);
    }

    [Fact]
    public void BuildIndexUrl_htm_form_keeps_dashed_accession_in_filename()
    {
        var url = SecEdgarUrls.BuildIndexUrl("0000320193", "0000320193-23-000106", ".htm");

        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/320193/000032019323000106/0000320193-23-000106-index.htm",
            url);
    }

    [Fact]
    public void BuildIndexUrl_html_form_keeps_dashed_accession_in_filename()
    {
        var url = SecEdgarUrls.BuildIndexUrl("0000320193", "0000320193-23-000106", ".html");

        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/320193/000032019323000106/0000320193-23-000106-index.html",
            url);
    }

    [Fact]
    public void BuildIndexUrl_html_round_trips_through_the_directional_index_regex()
    {
        // Mirrors DirectionalFilingSignalSource.IndexUrlRegex — pins that Radar can re-parse a URL it authored.
        var regex = new Regex(
            @"/edgar/data/(?<cik>\d+)/[^/]+/(?<accession>[^/]+?)-index\.html?$",
            RegexOptions.IgnoreCase);

        var url = SecEdgarUrls.BuildIndexUrl("0000320193", "0000320193-23-000106", ".html");

        var match = regex.Match(url);

        Assert.True(match.Success);
        Assert.Equal("320193", match.Groups["cik"].Value.TrimStart('0'));
        Assert.Equal("0000320193-23-000106", match.Groups["accession"].Value);
    }
}
