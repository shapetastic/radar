using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.EntityResolution;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// Guardrails over the SHIPPED watch universe (<c>data/companies.json</c>) — the curated, diversified efficacy
/// sample. These assertions pin two things a well-meaning later edit could silently undo: the universe size
/// (spec 125 expanded it 29 -> 43; spec 159 expanded it 43 -> 66) and the ticker-collision rule for the
/// tickers that are substrings of common headline words. The universe is NOT a scoring input, so nothing here
/// touches the fingerprint.
/// </summary>
public sealed class ProductionCompanySeedTests
{
    /// <summary>Universe size after the spec-159 cross-sectional-power batch (43 existing + 23 added).</summary>
    private const int ExpectedCompanyCount = 66;

    /// <summary>
    /// <c>NewsAttentionCollector.IsRelevant</c> matches the ticker with an unanchored, case-insensitive
    /// <c>Contains</c> on the headline. These tickers are substrings of common headline words ("deal"/"idea",
    /// "shoot", "latex", "Shenzhen" — and, from the spec-159 batch, "kgs" the kilograms abbreviation, "pump",
    /// "cassette"/"Picasso", "manipulate", "plus"/"surplus", "calm", "midterm"/"midtown"), so their newssearch
    /// feed must carry NO <c>ticker=</c> token — relevance is driven by the query phrase alone (same treatment
    /// as V/Visa). False-positive media evidence inflates Attention, and radar-formula-v8 credits collapsed
    /// distinct-publisher breadth into the reach term, so junk headlines would distort the notedness discount
    /// the 117->124 calibration arc settled.
    /// </summary>
    private static readonly string[] TickersWithoutTickerToken =
        ["DEA", "SHOO", "ATEX", "SHEN", "KGS", "PUMP", "CASS", "ANIP", "PLUS", "CALM", "IDT"];

    [Fact]
    public async Task ProductionSeed_ContainsTheExpectedUniverseSize()
    {
        var seed = await LoadProductionSeedAsync();

        Assert.Equal(ExpectedCompanyCount, seed.Companies.Count);
        Assert.Equal(ExpectedCompanyCount, seed.Companies.Select(c => c.Id).Distinct().Count());
        Assert.Equal(
            ExpectedCompanyCount,
            seed.Companies.Select(c => c.Ticker ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Theory]
    [InlineData("DEA")]
    [InlineData("SHOO")]
    [InlineData("ATEX")]
    [InlineData("SHEN")]
    [InlineData("KGS")]
    [InlineData("PUMP")]
    [InlineData("CASS")]
    [InlineData("ANIP")]
    [InlineData("PLUS")]
    [InlineData("CALM")]
    [InlineData("IDT")]
    public async Task ProductionSeed_CollidingTickers_HaveNoTickerTokenInNewsSearchFeed(string ticker)
    {
        var url = await GetNewsSearchUrlAsync(ticker);

        Assert.DoesNotContain("ticker=", url, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("query=", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HWKN")]
    [InlineData("DGII")] // spec-159 newcomer: distinctive tickers from the batch keep the token too.
    public async Task ProductionSeed_DistinctiveTicker_KeepsTheTickerToken(string ticker)
    {
        // Honesty control for the theory above: a distinctive ticker still carries the token.
        var url = await GetNewsSearchUrlAsync(ticker);

        Assert.Contains($"&ticker={ticker}", url, StringComparison.Ordinal);
    }

    /// <summary>
    /// JJSF is the <c>&amp;</c> trap (spec 159): <c>TwoKeyFeedToken.TrySplit</c> splits on the first
    /// <c>&amp;</c> after the value start and our seeds never put <c>&amp;</c> inside a value, so
    /// <c>query=J&amp;J Snack Foods&amp;ticker=JJSF</c> would silently parse the phrase as "J" — whose
    /// unanchored <c>Contains</c> relevance admits nearly every headline in existence. The url must therefore
    /// stay EXACTLY <c>query=JJSF</c> (phrase = ticker, no <c>ticker=</c> token). Do not "normalise" it to the
    /// registrant name for consistency — that is the single most likely well-meaning edit to break it.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Jjsf_NewsSearchUrlIsExactlyQueryEqualsTicker()
    {
        var url = await GetNewsSearchUrlAsync("JJSF");

        Assert.Equal("query=JJSF", url);
    }

    [Fact]
    public async Task ProductionSeed_EveryCompanyWithoutACollidingTicker_KeepsTheTickerToken()
    {
        var seed = await LoadProductionSeedAsync();
        var exempt = new HashSet<string>(TickersWithoutTickerToken, StringComparer.OrdinalIgnoreCase)
        {
            // Visa's single-letter ticker matched almost any headline (spec 120 fix).
            "V",

            // The spec-159 `&` trap: JJSF's url is exactly "query=JJSF" (phrase = ticker, no ticker= token),
            // pinned by ProductionSeed_Jjsf_NewsSearchUrlIsExactlyQueryEqualsTicker above.
            "JJSF",
        };

        foreach (var company in seed.Companies)
        {
            var feed = seed.SourceFeeds.SingleOrDefault(
                f => f.CompanyId == company.Id
                    && string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(feed);

            var ticker = company.Ticker ?? string.Empty;
            if (exempt.Contains(ticker))
            {
                continue;
            }

            Assert.Contains(
                $"&ticker={ticker}",
                feed!.Url,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The <c>patents</c> feeds the shipped seed carries, pinned by ticker. Spec 134's live ODP verification
    /// dropped MRCY (zero grants, filings AND publications in two years; last grant 2021-07-06 — a permanently
    /// empty feed producing only log noise), so a later edit cannot silently re-add a dead assignee token.
    /// <para>
    /// EOSE's token is the SHORT form "Eos Energy", not its listed name. ODP keys on the FILING entity, and
    /// Eos files as "EOS Energy Storage, LLC" (x50) and "EOS ENERGY TECHNOLOGY HOLDINGS, LLC" (x21) — the
    /// listed "Eos Energy Enterprises, Inc." matches ZERO rows at any date, so the original token was a
    /// silently-dead feed that read as "no recent grants" rather than "wrong token" (live-verified
    /// 2026-07-25). The normalized prefix EOSENERGY captures both filing entities and admits no unrelated
    /// company in the 78 rows returned. Do not "restore" this to the listed name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProductionSeed_PatentsFeeds_AreTheTwoLiveVerifiedAssignees()
    {
        var seed = await LoadProductionSeedAsync();

        var patentsByTicker = seed.SourceFeeds
            .Where(f => string.Equals(f.FeedType, "patents", StringComparison.OrdinalIgnoreCase))
            .Join(seed.Companies, f => f.CompanyId, c => c.Id, (f, c) => (Ticker: c.Ticker ?? string.Empty, f.Url))
            .ToDictionary(x => x.Ticker, x => x.Url, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, patentsByTicker.Count);
        Assert.Equal("assignee=Energy Recovery, Inc.", patentsByTicker["ERII"]);
        Assert.Equal("assignee=Eos Energy", patentsByTicker["EOSE"]);
        Assert.False(patentsByTicker.ContainsKey("MRCY"), "MRCY's patents feed was dropped by spec 134.");
    }

    private static async Task<string> GetNewsSearchUrlAsync(string ticker)
    {
        var seed = await LoadProductionSeedAsync();
        var company = Assert.Single(
            seed.Companies,
            c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        var feed = Assert.Single(
            seed.SourceFeeds,
            f => f.CompanyId == company.Id
                && string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase));
        return feed.Url;
    }

    private static Task<CompanySeedData> LoadProductionSeedAsync()
    {
        var path = Path.Combine(LocateRepoRoot(), "data", "companies.json");
        Assert.True(File.Exists(path), $"Expected the production company seed at {path}.");

        var source = new LocalFileCompanySeedSource(
            new LocalFileCompanySeedOptions { FilePath = path },
            NullLogger<LocalFileCompanySeedSource>.Instance,
            TimeProvider.System);

        return source.GetSeedAsync(CancellationToken.None);
    }

    private static string LocateRepoRoot()
    {
        // Walk up from the test assembly's base directory to the repo root (the folder holding Radar.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
