using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.EntityResolution;
using Radar.Domain.Companies;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// Guardrails over the SHIPPED watch universe (<c>data/companies.json</c>) — the curated, diversified efficacy
/// sample. These assertions pin two things a well-meaning later edit could silently undo: the universe size
/// (spec 125 expanded it 29 -> 43; spec 159 expanded it 43 -> 66; spec 166 expanded it 66 -> 74; spec 199
/// expanded it 74 -> 94; spec 207 expanded it 94 -> 102) and the ticker-collision rule for the tickers that
/// are substrings of common headline words. The universe is NOT a scoring input, so nothing here touches the
/// fingerprint.
/// </summary>
public sealed class ProductionCompanySeedTests
{
    /// <summary>
    /// Universe size after the spec-207 AI-robotics expansion (94 existing + the 8 additions listed in
    /// <see cref="Spec207Ciks"/>; <c>small</c> 55 -> 59). Spec 199 had taken it 74 -> 94 (the 20
    /// under-covered additions in <see cref="Spec199Ciks"/>), spec 166 had taken it 66 -> 74 (batch 4:
    /// PSTL, FR, CCOI, ATNI, CARS, MHO, THRM, BKE — the event-enriched exploratory cohort recorded in
    /// <c>docs/cohorts/event-enriched-2026-07.json</c>), and spec 159 had taken it 43 -> 66. Specs 199 and
    /// 207 are both additions only: no existing company was modified, removed or re-tiered.
    /// </summary>
    private const int ExpectedCompanyCount = 102;

    /// <summary>
    /// The <c>followingTier: small</c> population after spec 207 (55 after spec 199 + the four small-tier
    /// additions PDYN, STXS, CMCO, ALNT). Pinned separately from the total because the under-covered end of
    /// the universe is the stated purpose of the recent expansions, and a quiet re-tier changes this count
    /// without changing <see cref="ExpectedCompanyCount"/>.
    /// </summary>
    private const int ExpectedSmallTierCount = 59;

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
    /// <para>
    /// Spec 199 added four: <c>ITIC</c> ("cr<b>itic</b>", "pol<b>itic</b>al"), <c>GEOS</c>
    /// ("<b>geos</b>patial", "<b>geos</b>cience"), <c>CTO</c> ("dire<b>cto</b>r", "se<b>cto</b>r",
    /// "fa<b>cto</b>r", "do<b>cto</b>r") and <c>UTL</c> ("o<b>utl</b>ook", "o<b>utl</b>et",
    /// "o<b>utl</b>ine").
    /// </para>
    /// <para>
    /// Spec 200 added <c>ESQ</c>: "Esquire" is an ordinary word AND a publisher name (a headline ending
    /// " - Esquire" belongs to whichever company the article is about, not to Esquire Financial), and because
    /// <c>IsRelevant</c> is an unanchored substring <c>Contains</c> over phrase OR ticker, the ticker token
    /// <c>ESQ</c> would have admitted every such headline — the spec-199 ITIC/GEOS/CTO/UTL precedent. The
    /// issuer phrase alone is sufficiently specific, so the feed is exactly <c>query=Esquire Financial</c>.
    /// </para>
    /// <para>
    /// Spec 207 added <c>OUST</c>: "ouster" is a common English noun ("the CEO's <b>ouster</b>", "calls for
    /// his <b>ouster</b>"), so it collides as BOTH the ticker token and the bare company name — the token
    /// <c>OUST</c> and the phrase "Ouster" would each admit every removal-from-office headline. The phrase
    /// therefore includes <c>Inc</c> for precision (exactly <c>query=Ouster Inc</c>) and carries no ticker
    /// token. This deliberately trades recall for precision: a headline that writes only "Ouster (OUST)" is
    /// missed, which is the declared spec-207 §2 risk case, recorded in
    /// <c>docs/cohorts/ai-robotics-2026-09.md</c>. Neither direction may be "fixed" by a quiet query edit.
    /// </para>
    /// </summary>
    private static readonly string[] TickersWithoutTickerToken =
    [
        "DEA", "SHOO", "ATEX", "SHEN", "KGS", "PUMP", "CASS", "ANIP", "PLUS", "CALM", "IDT", "FR", "CARS",
        "ITIC", "GEOS", "CTO", "UTL",
        "ESQ", // spec 200
        "OUST", // spec 207
    ];

    /// <summary>
    /// The three spec-200 feed-identity repairs, pinned as EXACT url strings (not merely ticker presence or
    /// absence). Each was corrected at the seed BEFORE its first collection (spec 200 §2 found zero history
    /// for all three ids), so a later "tidy-up" that widened any of them would silently re-open the very
    /// false-positive channel this pin closes: "University of Utah Medical …", "investors title …" as a
    /// theme, and "Esquire" as a word/publisher.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Spec200ExactNewsSearchUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UTMD"] = "query=Utah Medical Products&ticker=UTMD",
            ["ITIC"] = "query=Investors Title Company",
            ["ESQ"] = "query=Esquire Financial",
        };

    /// <summary>
    /// The eight spec-207 newssearch feed identities, pinned as EXACT url strings (spec 207 §2). Every
    /// phrase/ticker was chosen against <c>FeedTargetRelevance.IsRelevant</c>'s unanchored substring rule:
    /// CMCO must never be shortened to the bare "Columbus" (the city), and OUST is phrase-only with
    /// <c>Inc</c> in the phrase because "ouster" is a common noun (see <see cref="TickersWithoutTickerToken"/>).
    /// A later "tidy-up" that widened any of these would silently open a false-positive channel that is
    /// never healed, because evidence is never backfilled.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Spec207ExactNewsSearchUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PDYN"] = "query=Palladyne AI&ticker=PDYN",
            ["STXS"] = "query=Stereotaxis&ticker=STXS",
            ["CMCO"] = "query=Columbus McKinnon&ticker=CMCO",
            ["ALNT"] = "query=Allient&ticker=ALNT",
            ["OUST"] = "query=Ouster Inc",
            ["PRCT"] = "query=PROCEPT BioRobotics&ticker=PRCT",
            ["NOVT"] = "query=Novanta&ticker=NOVT",
            ["AMBA"] = "query=Ambarella&ticker=AMBA",
        };

    /// <summary>
    /// The spec-207 batch, pinned ticker -> 10-digit EDGAR CIK. Every CIK was resolved from the canonical
    /// SEC mapping <c>https://www.sec.gov/files/company_tickers.json</c> and then live-verified against
    /// <c>https://data.sec.gov/submissions/CIK{cik}.json</c> on 2026-09-03 (HTTP 200; entity name and ticker
    /// matched). This test file is the OWNER of these values (spec 207 §3): a mistyped digit silently points
    /// a company's three filings feeds at a DIFFERENT registrant, and that evidence is never backfilled.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Spec207Ciks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PDYN"] = "0001826681",
            ["STXS"] = "0001289340",
            ["CMCO"] = "0001005229",
            ["ALNT"] = "0000046129",
            ["OUST"] = "0001816581",
            ["PRCT"] = "0001588978",
            ["NOVT"] = "0001076930",
            ["AMBA"] = "0001280263",
        };

    /// <summary>The spec-207 tier split: four <c>small</c>, four <c>mid</c> (spec 207 §1).</summary>
    private static readonly IReadOnlyDictionary<string, FollowingTier> Spec207Tiers =
        new Dictionary<string, FollowingTier>(StringComparer.OrdinalIgnoreCase)
        {
            ["PDYN"] = FollowingTier.Small,
            ["STXS"] = FollowingTier.Small,
            ["CMCO"] = FollowingTier.Small,
            ["ALNT"] = FollowingTier.Small,
            ["OUST"] = FollowingTier.Mid,
            ["PRCT"] = FollowingTier.Mid,
            ["NOVT"] = FollowingTier.Mid,
            ["AMBA"] = FollowingTier.Mid,
        };

    /// <summary>
    /// The spec-199 batch, pinned ticker -> 10-digit EDGAR CIK. Every CIK was live-verified against
    /// <c>https://data.sec.gov/submissions/CIK{cik}.json</c> on 2026-08-29 (HTTP 200; entity name, ticker and
    /// exchange matched; filings within the last month; Form 4 and SC 13 present). The pin exists because a
    /// CIK is the load-bearing identity of a registrant and a mistyped digit silently points a company's
    /// filings feed at a DIFFERENT company — evidence that would then be scored under the wrong name, and
    /// which is never backfilled once collected.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Spec199Ciks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GHM"] = "0000716314",
            ["CLMB"] = "0000945983",
            ["UTMD"] = "0000706698",
            ["MLAB"] = "0000724004",
            ["JOUT"] = "0000788329",
            ["FLXS"] = "0000037472",
            ["ITIC"] = "0000720858",
            ["ESQ"] = "0001531031",
            ["SGA"] = "0000886136",
            ["OOMA"] = "0001327688",
            ["JBSS"] = "0000880117",
            ["SENEA"] = "0000088948",
            ["NWPX"] = "0001001385",
            ["KOP"] = "0001315257",
            ["GEOS"] = "0001001115",
            ["EPM"] = "0001006655",
            ["CTO"] = "0000023795",
            ["OLP"] = "0000712770",
            ["UTL"] = "0000755001",
            ["RGCO"] = "0001069533",
        };

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
    [InlineData("ITIC")] // spec-199: "critic", "critical", "political".
    [InlineData("GEOS")] // spec-199: "geospatial", "geoscience", "geosciences".
    [InlineData("CTO")] // spec-199: "director", "sector", "factor", "doctor".
    [InlineData("UTL")] // spec-199: "outlook", "outlet", "outline".
    [InlineData("ESQ")] // spec-200: "Esquire" — an ordinary word and a publisher name.
    [InlineData("OUST")] // spec-207: "ouster" — a common noun, colliding as both ticker and bare name.
    public async Task ProductionSeed_CollidingTickers_HaveNoTickerTokenInNewsSearchFeed(string ticker)
    {
        var urls = await GetNewsSearchUrlsAsync(ticker);

        Assert.All(urls, url =>
        {
            Assert.DoesNotContain("ticker=", url, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("query=", url, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("HWKN")]
    [InlineData("DGII")] // spec-159 newcomer: distinctive tickers from the batch keep the token too.
    [InlineData("CCOI")] // spec-166 newcomer: same honesty control for batch 4.
    [InlineData("CLMB")] // spec-199 newcomer: same honesty control for the under-covered batch.
    [InlineData("AMBA")] // spec-207 newcomer: same honesty control for the AI-robotics batch.
    public async Task ProductionSeed_DistinctiveTicker_KeepsTheTickerToken(string ticker)
    {
        // Honesty control for the theory above: a distinctive ticker still carries the token.
        var urls = await GetNewsSearchUrlsAsync(ticker);

        Assert.All(urls, url => Assert.Contains($"&ticker={ticker}", url, StringComparison.Ordinal));
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
        var url = Assert.Single(await GetNewsSearchUrlsAsync("JJSF"));

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
        var url = Assert.Single(await GetNewsSearchUrlsAsync("BKE"));

        Assert.Equal("query=The Buckle&ticker=BKE", url);
    }

    /// <summary>
    /// CARS carries TWO newssearch feeds (post-spec-166 review fix). <c>NewsAttentionCollector.IsRelevant</c>
    /// consults only the feed's own query phrase (plus the optional ticker token) — never the seed aliases —
    /// and Cars.com titles its releases under both the site brand "Cars.com" and the parent brand
    /// "Cars Commerce". With the colliding <c>CARS</c> ticker deliberately omitted (a common plural noun),
    /// a single-phrase feed would silently drop every "Cars Commerce"-styled headline and undercount
    /// Attention — and that gap is unhealable, because evidence is never backfilled. Neither phrase may
    /// gain a <c>ticker=</c> token.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Cars_CarriesBothBrandNewsSearchFeeds()
    {
        var urls = await GetNewsSearchUrlsAsync("CARS");

        Assert.Equal(
            ["query=Cars Commerce", "query=Cars.com"],
            urls.OrderBy(u => u, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Spec 199: the 20 additions, pinned ticker -> CIK, with all THREE EDGAR feeds (<c>sec</c>,
    /// <c>secform4</c>, <c>sec13dg</c>) resolving to the same submissions document for that registrant. This
    /// is the guard that a later edit cannot silently re-point one of the three at the wrong registrant: the
    /// three feed kinds are driven by ONE submissions url, so a divergence between them is always a defect,
    /// and a divergence from <see cref="Spec199Ciks"/> means the company's filings evidence would be
    /// collected under another company's identity.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Spec199Additions_CarryTheirLiveVerifiedCikOnAllThreeEdgarFeeds()
    {
        var seed = await LoadProductionSeedAsync();

        foreach (var (ticker, cik) in Spec199Ciks)
        {
            var company = Assert.Single(
                seed.Companies,
                c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));

            var expectedUrl = $"https://data.sec.gov/submissions/CIK{cik}.json";

            foreach (var feedType in new[] { "sec", "secform4", "sec13dg" })
            {
                var feed = Assert.Single(
                    seed.SourceFeeds,
                    f => f.CompanyId == company.Id
                        && string.Equals(f.FeedType, feedType, StringComparison.OrdinalIgnoreCase));

                Assert.Equal(expectedUrl, feed.Url);
            }
        }
    }

    /// <summary>
    /// Spec 199 §6: every addition is a US-listed <c>followingTier: small</c> name with a working SEC
    /// submissions feed and at least one <c>newssearch</c> feed. The tier matters because the batch's stated
    /// purpose is to shift the universe toward the UNDER-COVERED end (35/74 small before, 55/94 after) — a
    /// later re-tier of one of these to <c>mid</c> would quietly undo that without changing any count this
    /// file pins. The SEC feed is the load-bearing one: spec 199 §1 refuses to add a company without one,
    /// because filings are the highest-quality evidence source and the arms under test are disclosure-led.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Spec199Additions_AreSmallTierUsCompaniesWithSecAndNewsFeeds()
    {
        var seed = await LoadProductionSeedAsync();

        foreach (var ticker in Spec199Ciks.Keys)
        {
            var company = Assert.Single(
                seed.Companies,
                c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(FollowingTier.Small, company.FollowingTier);
            Assert.Equal("US", company.CountryCode);

            var feeds = seed.SourceFeeds.Where(f => f.CompanyId == company.Id).ToList();

            var secFeed = Assert.Single(
                feeds,
                f => string.Equals(f.FeedType, "sec", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(secFeed.Url));

            Assert.Contains(
                feeds,
                f => string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// JBSS is the spec-199 repeat of the spec-159 <c>&amp;</c> trap. <c>TwoKeyFeedToken.TrySplit</c> splits
    /// the feed url on the FIRST <c>&amp;</c> after the value start, so the registrant name
    /// "John B. Sanfilippo &amp; Son" written verbatim as <c>query=John B. Sanfilippo &amp; Son&amp;ticker=JBSS</c>
    /// would parse the phrase as "John B. Sanfilippo " plus a junk second key — losing the ticker token
    /// entirely. The url must therefore stay EXACTLY the ampersand-free phrase below. Do NOT "restore" the
    /// ampersand for consistency with <c>legalName</c>/<c>name</c>: that is the single most likely
    /// well-meaning edit to break it, and the resulting evidence gap is unhealable because evidence is never
    /// backfilled.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Jbss_NewsSearchUrlCarriesNoAmpersandInThePhrase()
    {
        var url = Assert.Single(await GetNewsSearchUrlsAsync("JBSS"));

        Assert.Equal("query=John B. Sanfilippo&ticker=JBSS", url);
    }

    /// <summary>
    /// NWPX carries TWO newssearch feeds, for the same reason CARS does (spec 166's post-review fix).
    /// <c>NewsAttentionCollector.IsRelevant</c> consults only the feed's OWN query phrase (plus the optional
    /// ticker token) — never the seed aliases — and the company renamed from "Northwest Pipe Company" to
    /// "NWPX Infrastructure", so a single-phrase feed would silently drop every legacy-brand headline and
    /// undercount Attention. Both phrases keep the distinctive <c>NWPX</c> ticker token.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Nwpx_CarriesBothCurrentAndLegacyBrandNewsSearchFeeds()
    {
        var urls = await GetNewsSearchUrlsAsync("NWPX");

        Assert.Equal(
            ["query=NWPX Infrastructure&ticker=NWPX", "query=Northwest Pipe&ticker=NWPX"],
            urls.OrderBy(u => u, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The spec-97 feed-identity collision guard, applied seed-wide. <c>LocalFileCompanySeedSource</c> derives
    /// each feed's id deterministically from <c>(companyId, "feed", "{feedType}|{url}")</c> so re-seeding
    /// upserts the same rows — which also means two feeds on one company sharing a type AND a url would
    /// collapse onto ONE id and the second would silently vanish at seed time, uncounted. The three EDGAR
    /// feeds are safe because their TYPES differ; a duplicated newssearch phrase would not be. This asserts
    /// the whole 102-company seed, not just the spec-199/207 batches.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_EverySourceFeedIdIsDistinct()
    {
        var seed = await LoadProductionSeedAsync();

        Assert.Equal(
            seed.SourceFeeds.Count,
            seed.SourceFeeds.Select(f => f.Id).Distinct().Count());
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

    /// <summary>
    /// Spec 200 §1/§3: the three repaired feed identities are pinned as EXACT strings. UTMD keeps its
    /// non-colliding ticker but the phrase must name the ISSUER, not a university plus the word "medical";
    /// ITIC (already phrase-only under spec 199) uses the issuer's full public name; ESQ drops its colliding
    /// ticker and relies on the issuer phrase alone. Each company has exactly ONE newssearch feed.
    /// </summary>
    [Theory]
    [InlineData("UTMD")]
    [InlineData("ITIC")]
    [InlineData("ESQ")]
    public async Task ProductionSeed_Spec200RepairedFeeds_NewsSearchUrlIsExactly(string ticker)
    {
        var urls = await GetNewsSearchUrlsAsync(ticker);

        var url = Assert.Single(urls);
        Assert.Equal(Spec200ExactNewsSearchUrls[ticker], url);
    }

    /// <summary>
    /// Spec 207 §2/§3: the eight AI-robotics newssearch feed identities are pinned as EXACT strings, each
    /// company carrying exactly ONE newssearch feed. See <see cref="Spec207ExactNewsSearchUrls"/> for the
    /// per-ticker collision reasoning.
    /// </summary>
    [Theory]
    [InlineData("PDYN")]
    [InlineData("STXS")]
    [InlineData("CMCO")]
    [InlineData("ALNT")]
    [InlineData("OUST")]
    [InlineData("PRCT")]
    [InlineData("NOVT")]
    [InlineData("AMBA")]
    public async Task ProductionSeed_Spec207Additions_NewsSearchUrlIsExactly(string ticker)
    {
        var urls = await GetNewsSearchUrlsAsync(ticker);

        var url = Assert.Single(urls);
        Assert.Equal(Spec207ExactNewsSearchUrls[ticker], url);
    }

    /// <summary>
    /// Spec 207: the eight additions, pinned ticker -> CIK, with all THREE EDGAR feeds (<c>sec</c>,
    /// <c>secform4</c>, <c>sec13dg</c>) resolving to the same submissions document for that registrant — the
    /// spec-199 guard (<see cref="ProductionSeed_Spec199Additions_CarryTheirLiveVerifiedCikOnAllThreeEdgarFeeds"/>)
    /// applied to the AI-robotics batch. A divergence between the three, or from <see cref="Spec207Ciks"/>,
    /// means the company's filings evidence would be collected under another company's identity.
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Spec207Additions_CarryTheirLiveVerifiedCikOnAllThreeEdgarFeeds()
    {
        var seed = await LoadProductionSeedAsync();

        Assert.Equal(8, Spec207Ciks.Count);

        foreach (var (ticker, cik) in Spec207Ciks)
        {
            var company = Assert.Single(
                seed.Companies,
                c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));

            var expectedUrl = $"https://data.sec.gov/submissions/CIK{cik}.json";

            foreach (var feedType in new[] { "sec", "secform4", "sec13dg" })
            {
                var feed = Assert.Single(
                    seed.SourceFeeds,
                    f => f.CompanyId == company.Id
                        && string.Equals(f.FeedType, feedType, StringComparison.OrdinalIgnoreCase));

                Assert.Equal(expectedUrl, feed.Url);
            }
        }
    }

    /// <summary>
    /// Spec 207 §1: four additions are <c>followingTier: small</c> (PDYN, STXS, CMCO, ALNT) and four are
    /// <c>mid</c> (OUST, PRCT, NOVT, AMBA); every one is <c>countryCode: US</c> with a working SEC submissions
    /// feed and a <c>newssearch</c> feed, and no rss/IR press feed (spec 207 §2: newssearch + the three SEC
    /// feeds only). The tier is pinned per ticker because a later re-tier in EITHER direction would
    /// quietly change what the retrospective in <c>docs/cohorts/ai-robotics-2026-09.md</c> is measuring.
    /// The seed-wide <c>small</c> population is pinned alongside (55 after spec 199 + 4 = <see cref="ExpectedSmallTierCount"/>).
    /// </summary>
    [Fact]
    public async Task ProductionSeed_Spec207Additions_HaveTheDeclaredTiersAndUsSecAndNewsFeeds()
    {
        var seed = await LoadProductionSeedAsync();

        Assert.Equal(8, Spec207Tiers.Count);
        Assert.Equal(4, Spec207Tiers.Values.Count(t => t == FollowingTier.Small));
        Assert.Equal(4, Spec207Tiers.Values.Count(t => t == FollowingTier.Mid));

        foreach (var (ticker, tier) in Spec207Tiers)
        {
            var company = Assert.Single(
                seed.Companies,
                c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(tier, company.FollowingTier);
            Assert.Equal("US", company.CountryCode);

            var feeds = seed.SourceFeeds.Where(f => f.CompanyId == company.Id).ToList();

            var secFeed = Assert.Single(
                feeds,
                f => string.Equals(f.FeedType, "sec", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(secFeed.Url));

            Assert.Contains(
                feeds,
                f => string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain(
                feeds,
                f => string.Equals(f.FeedType, "rss", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(
            ExpectedSmallTierCount,
            seed.Companies.Count(c => c.FollowingTier == FollowingTier.Small));
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
            var feeds = seed.SourceFeeds
                .Where(f => f.CompanyId == company.Id
                    && string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.NotEmpty(feeds);

            var ticker = company.Ticker ?? string.Empty;
            if (exempt.Contains(ticker))
            {
                continue;
            }

            Assert.All(feeds, feed => Assert.Contains(
                $"&ticker={ticker}",
                feed.Url,
                StringComparison.Ordinal));
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

    private static async Task<IReadOnlyList<string>> GetNewsSearchUrlsAsync(string ticker)
    {
        var seed = await LoadProductionSeedAsync();
        var company = Assert.Single(
            seed.Companies,
            c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        var urls = seed.SourceFeeds
            .Where(f => f.CompanyId == company.Id
                && string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Url)
            .ToList();
        Assert.NotEmpty(urls);
        return urls;
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
