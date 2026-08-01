using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.EntityResolution;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// Guardrails over the SHIPPED watch universe (<c>data/companies.json</c>) — the curated, diversified efficacy
/// sample. These assertions pin two things a well-meaning later edit could silently undo: the universe size
/// (spec 125 expanded it 29 -> 43; spec 159 expanded it 43 -> 66; spec 166 expanded it 66 -> 74) and the
/// ticker-collision rule for the tickers that are substrings of common headline words. The universe is NOT a
/// scoring input, so nothing here touches the fingerprint.
/// </summary>
public sealed class ProductionCompanySeedTests
{
    /// <summary>
    /// Universe size after the spec-166 batch-4 expansion (66 existing + 8 added: PSTL, FR, CCOI, ATNI, CARS,
    /// MHO, THRM, BKE — the event-enriched exploratory cohort recorded in
    /// <c>docs/cohorts/event-enriched-2026-07.json</c>). Spec 159 had taken it 43 -> 66.
    /// </summary>
    private const int ExpectedCompanyCount = 74;

    /// <summary>
    /// The AD-16 §7 exclusion-cohort file (under <c>docs/cohorts/</c>) naming the spec-166 batch-4 companies.
    /// The seed is pinned against it so the exclusion cohort and the shipped universe cannot drift apart.
    /// </summary>
    private const string EventEnrichedCohortFile = "event-enriched-2026-07.json";

    /// <summary>
    /// <c>NewsAttentionCollector.IsRelevant</c> matches the ticker with an unanchored, case-insensitive
    /// <c>Contains</c> on the headline. These tickers are substrings of common headline words ("deal"/"idea",
    /// "shoot", "latex", "Shenzhen" — and, from the spec-159 batch, "kgs" the kilograms abbreviation, "pump",
    /// "cassette"/"Picasso", "manipulate", "plus"/"surplus", "calm", "midterm"/"midtown"), so their newssearch
    /// feed must carry NO <c>ticker=</c> token — relevance is driven by the query phrase alone (same treatment
    /// as V/Visa). False-positive media evidence inflates Attention, and radar-formula-v8 credits collapsed
    /// distinct-publisher breadth into the reach term, so junk headlines would distort the notedness discount
    /// the 117->124 calibration arc settled.
    /// <para>
    /// Spec 166 added two more: <c>FR</c> is a near-universal bigram ("<b>fr</b>om", "<b>fr</b>ee",
    /// "<b>Fr</b>iday", "<b>Fr</b>ance") and <c>CARS</c> is a common plural noun ("used <b>cars</b>",
    /// "<b>cars</b> recalled").
    /// </para>
    /// </summary>
    private static readonly string[] TickersWithoutTickerToken =
        ["DEA", "SHOO", "ATEX", "SHEN", "KGS", "PUMP", "CASS", "ANIP", "PLUS", "CALM", "IDT", "FR", "CARS"];

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
    [InlineData("FR")] // spec-166: "from", "free", "Friday", "France" — a near-universal bigram.
    [InlineData("CARS")] // spec-166: "cars", "used cars", "cars recalled".
    public async Task ProductionSeed_CollidingTickers_HaveNoTickerTokenInNewsSearchFeed(string ticker)
    {
        var url = await GetNewsSearchUrlAsync(ticker);

        Assert.DoesNotContain("ticker=", url, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("query=", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HWKN")]
    [InlineData("DGII")] // spec-159 newcomer: distinctive tickers from the batch keep the token too.
    [InlineData("CCOI")] // spec-166 newcomer: same honesty control for batch 4.
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

    /// <summary>
    /// BKE's query phrase must stay EXACTLY <c>The Buckle</c> (spec 166). <c>NewsAttentionCollector.IsRelevant</c>
    /// is an unanchored case-insensitive <c>Contains</c>, so the bare phrase "Buckle" would match "buckle up",
    /// "buckle under pressure" and every other idiomatic use — false-positive media evidence that inflates
    /// Attention and, via the reach term, distorts the notedness discount. The distinctive <c>BKE</c> ticker
    /// token stays, because it is what carries the finance-styled headlines.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Bke_NewsSearchUrlUsesTheDisambiguatedPhrase()
    {
        var url = await GetNewsSearchUrlAsync("BKE");

        Assert.Equal("query=The Buckle&ticker=BKE", url);
    }

    /// <summary>
    /// The spec-166 batch-4 names form the AD-16 §7 event-enriched EXCLUSION cohort, declared machine-readably
    /// in <c>docs/cohorts/event-enriched-2026-07.json</c> (the efficacy evaluator reads that file, never git
    /// history). If the seed and the cohort file drift — a ticker renamed here, a CIK corrected there — the
    /// evaluator would silently include an excluded company in the binding primary screen, or exclude one that
    /// was never enriched. This pins the cohort -> seed direction: each of the eight cohort tickers resolves to
    /// exactly one seed company whose <c>sec</c> feed url carries the cohort file's CIK. The reverse direction
    /// is not expressible — nothing in the seed marks a company as event-enriched — so a ninth enriched name
    /// added to the seed without updating the cohort file would go undetected here.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_MatchesTheEventEnrichedCohortFile()
    {
        var cohortPath = Path.Combine(LocateRepoRoot(), "docs", "cohorts", EventEnrichedCohortFile);
        Assert.True(File.Exists(cohortPath), $"Expected the AD-16 exclusion cohort at {cohortPath}.");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(cohortPath));
        var cohort = document.RootElement.GetProperty("companies")
            .EnumerateArray()
            .Select(e => (
                Ticker: e.GetProperty("ticker").GetString() ?? string.Empty,
                Cik: e.GetProperty("cik").GetString() ?? string.Empty))
            .ToList();

        Assert.Equal(8, cohort.Count);
        Assert.Equal(8, cohort.Select(c => c.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var seed = await LoadProductionSeedAsync();

        foreach (var (ticker, cik) in cohort)
        {
            var company = Assert.Single(
                seed.Companies,
                c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));

            var secFeed = Assert.Single(
                seed.SourceFeeds,
                f => f.CompanyId == company.Id
                    && string.Equals(f.FeedType, "sec", StringComparison.OrdinalIgnoreCase));

            Assert.Equal($"https://data.sec.gov/submissions/CIK{cik}.json", secFeed.Url);
        }
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
