using System.Globalization;
using System.Net;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Domain.Companies;
using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

/// <summary>
/// Spec 190 — the NewsSearch local-limit audit. Radar's <c>Radar:News:MaxRecordsPerCompany</c> is its OWN
/// effective/local retention limit, not a measured provider ceiling, and before this slice a response that
/// reached it was indistinguishable from a response that held exactly that many items. These tests pin the
/// two halves of the fix together:
/// <list type="bullet">
/// <item>the retained prefix — and therefore every collected evidence record and observation candidate —
/// is EXACTLY what it was before, in count, identity, order and content; and</item>
/// <item>the diagnostics report the already-fetched response tail (raw observed size, confirmed local
/// truncation, unique company-relevant unadmitted items) without one extra request, page or article fetch,
/// and without admitting a single tail item anywhere.</item>
/// </list>
/// </summary>
public sealed class NewsSearchLocalLimitAuditTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RklbId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string MrcyPhrase = "Mercury Systems";
    private const string MrcyToken = "query=Mercury Systems&ticker=MRCY";
    private const string RklbPhrase = "Rocket Lab";
    private const string RklbToken = "query=Rocket Lab&ticker=RKLB";

    // Spec 195 §3: a SECOND feed for the SAME company, under a different query phrase, so the fake reader
    // can answer the company's two feeds differently. "Mercury" matches the fixture headlines under the
    // unchanged IsRelevant rule, so relevance is never the variable a company-wide-uniqueness test measures.
    private const string SecondPhrase = "Mercury";
    private const string SecondToken = "query=Mercury&ticker=MRCY";

    /// <summary>The shipped effective local retention limit this audit is written against.</summary>
    private const int ShippedLimit = 25;

    // ---------------------------------------------------------------------------------------------
    // Reader: the bounded diagnostic tail over the SAME already-parsed document.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reader_MoreValidItemsThanTheLimit_KeepsThePrefixAndReportsTheTail()
    {
        var reader = CreateReader(Feed(30));

        var result = await reader.ReadAsync(SearchQuery(ShippedLimit), CancellationToken.None);

        Assert.True(result.IsSuccess);

        // The retained prefix is byte-identical to the pre-190 behaviour: the FIRST 25 items, in feed order.
        Assert.Equal(ShippedLimit, result.Items.Count);
        Assert.Equal(
            Enumerable.Range(1, ShippedLimit).Select(ItemUrl),
            result.Items.Select(i => i.Url));

        // The tail is the rest of the same response — observed, never retained.
        Assert.Equal(30, result.ValidItemsObserved);
        Assert.True(result.ObservedValidItemBeyondLocalLimit);
        Assert.Equal(
            Enumerable.Range(ShippedLimit + 1, 5).Select(ItemUrl),
            result.DiagnosticTail.Select(i => i.Url));

        // A tail item is built through the SAME per-item path as a retained one, so the audit compares
        // like with like (title, publisher and pubDate are all parsed, not stubbed).
        var lastTail = result.DiagnosticTail[^1];
        Assert.Equal(ItemTitle(30), lastTail.Title);
        Assert.Equal("SpaceNews", lastTail.SourceName);
        Assert.NotNull(lastTail.PublishedAt);
    }

    [Fact]
    public async Task Reader_ExactlyTheLimit_FillsThePrefixWithNoObservedTail()
    {
        var result = await CreateReader(Feed(ShippedLimit))
            .ReadAsync(SearchQuery(ShippedLimit), CancellationToken.None);

        Assert.Equal(ShippedLimit, result.Items.Count);
        Assert.Equal(ShippedLimit, result.ValidItemsObserved);
        Assert.Empty(result.DiagnosticTail);
        // Nothing was observed beyond the limit — which is NOT a claim that the provider had nothing more.
        Assert.False(result.ObservedValidItemBeyondLocalLimit);
    }

    [Fact]
    public async Task Reader_FewerItemsThanTheLimit_IsBelowLimit()
    {
        var result = await CreateReader(Feed(3))
            .ReadAsync(SearchQuery(ShippedLimit), CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(3, result.ValidItemsObserved);
        Assert.Empty(result.DiagnosticTail);
        Assert.False(result.ObservedValidItemBeyondLocalLimit);
    }

    [Fact]
    public async Task Reader_MalformedTailItems_AreNotCountedAsObservedItems()
    {
        // Two link-less <item>s sit beyond the prefix. The "no <link> ⇒ skip" rule is unchanged, so they are
        // structurally invalid: neither the observed count nor the tail may include them.
        var body = FeedWith(BuildItems(1, 4) + LinklessItem() + LinklessItem() + BuildItems(5, 5));

        var result = await CreateReader(body).ReadAsync(SearchQuery(4), CancellationToken.None);

        Assert.Equal(4, result.Items.Count);
        Assert.Equal(5, result.ValidItemsObserved);
        Assert.Equal([ItemUrl(5)], result.DiagnosticTail.Select(i => i.Url));
    }

    [Fact]
    public async Task Reader_AbsoluteSafetyCeiling_BoundsPrefixAndTailTogether_AndIsNotRaised()
    {
        // 120 valid items, a requested limit of 25: the pre-existing absolute ceiling of 100 valid items per
        // response still applies and is NOT raised — it now bounds prefix + tail together.
        var result = await CreateReader(Feed(120)).ReadAsync(SearchQuery(ShippedLimit), CancellationToken.None);

        Assert.Equal(ShippedLimit, result.Items.Count);
        Assert.Equal(100, result.ValidItemsObserved);
        Assert.Equal(75, result.DiagnosticTail.Count);
    }

    [Fact]
    public async Task Reader_ScanningTheTail_IssuesExactlyOneHttpRequest()
    {
        // The tail comes out of the response body Radar already has. No second page, no article follow.
        var handler = new CountingHandler(HttpStatusCode.OK, Feed(60));
        var reader = new HttpNewsSearchReader(
            new HttpClient(handler), NullLogger<HttpNewsSearchReader>.Instance, new FixedTime(FixedNow));

        var result = await reader.ReadAsync(SearchQuery(ShippedLimit), CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(35, result.DiagnosticTail.Count);
        Assert.Equal(
            // Every request went to the search endpoint; not one article landing URL was fetched.
            ["https://news.google.com/rss/search"],
            handler.RequestedUris.Select(u => u.GetLeftPart(UriPartial.Path)).Distinct());
    }

    [Fact]
    public async Task Reader_FailedRead_CarriesNoDiagnostics()
    {
        var result = await CreateReader("nonsense", HttpStatusCode.InternalServerError)
            .ReadAsync(SearchQuery(ShippedLimit), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.ValidItemsObserved);
        Assert.Empty(result.DiagnosticTail);
        Assert.False(result.ObservedValidItemBeyondLocalLimit);
    }

    // ---------------------------------------------------------------------------------------------
    // THE GOLDEN REGRESSION: >25 items in, byte-equivalent evidence and observations out.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The acceptance centrepiece. A 30-item feed is collected through the REAL reader; the expectation is
    /// built from the retained prefix alone. Evidence and observation candidates must match it in count,
    /// identity, order and content, and no tail URL or headline may appear in either output — while the
    /// diagnostics record the raw observed size, confirmed local truncation and the relevant unique tail.
    /// </summary>
    [Fact]
    public async Task Collect_FeedLargerThanTheLimit_LeavesEvidenceAndObservationsUnchanged()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, Feed(30));
        var collector = CreateCollector(
            new HttpNewsSearchReader(
                new HttpClient(handler), NullLogger<HttpNewsSearchReader>.Instance, new FixedTime(FixedNow)),
            ShippedLimit);

        var feed = FeedBinding("cccccccc-0000-0000-0000-000000000001", MrcyId, MrcyToken);
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        var expectedUrls = Enumerable.Range(1, ShippedLimit).Select(ItemUrl).ToArray();
        var expectedTitles = Enumerable.Range(1, ShippedLimit).Select(ItemTitle).ToArray();

        // Count, identity, ORDER and content — all from the retained prefix, exactly as pre-190.
        Assert.Equal(expectedUrls, result.Evidence.Select(e => e.SourceUrl));
        Assert.Equal(expectedTitles, result.Evidence.Select(e => e.Title));
        Assert.Equal(expectedUrls, result.Observations!.Select(o => o.GoogleLandingUrl));
        Assert.Equal(expectedTitles, result.Observations!.Select(o => o.Headline));

        // Not one tail item leaked into evidence or the observation sidecar.
        foreach (var index in Enumerable.Range(ShippedLimit + 1, 5))
        {
            Assert.DoesNotContain(result.Evidence, e => e.SourceUrl == ItemUrl(index));
            Assert.DoesNotContain(result.Evidence, e => e.Title == ItemTitle(index));
            Assert.DoesNotContain(result.Observations!, o => o.GoogleLandingUrl == ItemUrl(index));
            Assert.DoesNotContain(result.Observations!, o => o.Headline == ItemTitle(index));
        }

        // …and the audit is now visible on the durable coverage row.
        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.Equal(ShippedLimit, coverage.EffectiveResultLimit);
        Assert.Equal(30, coverage.MaxValidItemsObserved);
        Assert.True(coverage.ConfirmedLocalTruncation);
        Assert.Equal(5, coverage.UnadmittedRelevantTailItemCount);

        // Fail-closed possible-truncation semantics are untouched, and exactly one request was issued.
        Assert.True(coverage.HitEffectiveResultLimit);
        Assert.Equal([CollectionCoverageIssues.ResultLimitReached], coverage.Issues);
        Assert.Equal(1, handler.CallCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Collector: the tail is counted under the EXISTING relevance + dedupe rules, and admits nothing.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Collect_IrrelevantTailItems_AreNotCountedAndAreNeverAdmitted()
    {
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail:
                [
                    Article(ItemUrl(2), "An unrelated company restructures - Reuters"),
                    Article(ItemUrl(3), "Mercury Systems expands production - SpaceNews"),
                ]),
        };

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 1);

        // Only the company-relevant tail item counts; neither became evidence.
        Assert.Equal(1, coverage.UnadmittedRelevantTailItemCount);
        Assert.True(coverage.ConfirmedLocalTruncation);
        Assert.Equal(3, coverage.MaxValidItemsObserved);
    }

    [Fact]
    public async Task Collect_TailItemDuplicatingARetainedUrl_IsNotCountedTwice()
    {
        // The retained prefix here is capped at ONE item while the reader retained two, so the evidence
        // loop's own `seenUrls` never even sees the second retained URL. Deduping the tail against the FULL
        // retained prefix is what keeps a re-published duplicate from being counted as something new.
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Read(
                prefix:
                [
                    Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters"),
                    Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews"),
                ],
                tail: [Article(ItemUrl(2), "Mercury Systems expands production - Yahoo Finance")]),
        };

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 1);

        Assert.Equal(0, coverage.UnadmittedRelevantTailItemCount);
        Assert.True(coverage.ConfirmedLocalTruncation);
    }

    [Fact]
    public async Task Collect_TailItemDuplicatingAnEarlierTailUrl_IsCountedOnce()
    {
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail:
                [
                    Article(ItemUrl(9), "Mercury Systems expands production - SpaceNews"),
                    Article(ItemUrl(9), "Mercury Systems expands production - Yahoo Finance"),
                ]),
        };

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 1);

        Assert.Equal(1, coverage.UnadmittedRelevantTailItemCount);
    }

    [Fact]
    public async Task Collect_AtTheLimitWithNoTail_RecordsPossibleButNotConfirmedTruncation()
    {
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Plain(
            [
                Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters"),
                Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews"),
            ]),
        };

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 2);

        Assert.True(coverage.HitEffectiveResultLimit);              // possible truncation — fail-closed
        Assert.False(coverage.ConfirmedLocalTruncation);            // …but nothing was observed beyond it
        Assert.Equal(0, coverage.UnadmittedRelevantTailItemCount);
        Assert.Equal(2, coverage.MaxValidItemsObserved);
        Assert.Equal([CollectionCoverageIssues.ResultLimitReached], coverage.Issues);
    }

    [Fact]
    public async Task Collect_BelowTheLimit_RecordsNeitherPossibleNorConfirmedTruncation()
    {
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Plain([Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")]),
        };

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 5);

        Assert.False(coverage.HitEffectiveResultLimit);
        Assert.False(coverage.ConfirmedLocalTruncation);
        Assert.Equal(1, coverage.MaxValidItemsObserved);
        Assert.Empty(coverage.Issues);
    }

    /// <summary>
    /// A CONFIRMED tail must never downgrade the fail-closed possible-truncation token, and neither
    /// diagnostic may remove <c>ResultLimitReached</c> — AD-16 / news-risk coverage must not silently
    /// upgrade on the strength of a diagnostic that cannot see the provider's own result set.
    /// </summary>
    [Fact]
    public async Task Collect_ConfirmedTruncation_LeavesTheFailClosedIssueTokenExactlyAsItWas()
    {
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail: [Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews")]),
        };

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 1);

        Assert.True(coverage.HitEffectiveResultLimit);
        Assert.True(coverage.ConfirmedLocalTruncation);
        Assert.Equal([CollectionCoverageIssues.ResultLimitReached], coverage.Issues);
    }

    /// <summary>
    /// Across a company's feeds the observed response size accumulates as a MAXIMUM (the biggest response
    /// that company produced) while the unadmitted relevant tail is COMPANY-WIDE UNIQUE.
    /// <para>
    /// This test originally asserted 4 here, because spec 190 deduped the tail inside each feed and then
    /// SUMMED the per-feed integers — so two feeds answering with the same two tail items counted them
    /// twice. Spec 195 §3 replaced the sum with company-scoped URL sets, so the correct answer for this
    /// fixture is 2: the two feeds observed the SAME two unadmitted articles, and Radar declined to admit
    /// two articles, not four. The two accumulation rules still answer different questions and are still
    /// asserted side by side.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Collect_MaxObservedIsTheMaximumAcrossACompanysFeeds()
    {
        var feedA = FeedBinding("cccccccc-0000-0000-0000-00000000000a", MrcyId, MrcyToken, "Mercury — News A");
        var feedB = FeedBinding("cccccccc-0000-0000-0000-00000000000b", MrcyId, MrcyToken, "Mercury — News B");

        // Both feeds carry the same phrase, so the fake answers both with the same 4-item response.
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Read(
                prefix:
                [
                    Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters"),
                    Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews"),
                ],
                tail:
                [
                    Article(ItemUrl(3), "Mercury Systems opens a facility - Yahoo Finance"),
                    Article(ItemUrl(4), "Mercury Systems names a CFO - MarketBeat"),
                ]),
        };

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY")], [feedA, feedB]);
        var result = await CreateCollector(reader, maxRecords: 2)
            .CollectAsync(context, CancellationToken.None);

        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.Equal(2, coverage.SuccessfulFeedCount);
        Assert.Equal(4, coverage.MaxValidItemsObserved);              // MAX across the company's feeds …
        Assert.Equal(2, coverage.UnadmittedRelevantTailItemCount);    // … while the tail is company-unique.
    }

    [Fact]
    public async Task Collect_FailedFeed_RecordsZeroObservedItems_AndNoConfirmedTruncation()
    {
        var reader = new FakeNewsSearchReader();
        reader.SetFailure(MrcyPhrase, NewsSearchReadOutcome.RateLimited, "HTTP 429 (rate limited)");

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 5);

        Assert.Equal(0, coverage.MaxValidItemsObserved);
        Assert.False(coverage.ConfirmedLocalTruncation);
        Assert.Equal(0, coverage.UnadmittedRelevantTailItemCount);
        Assert.Equal([CollectionCoverageIssues.SourceFailure], coverage.Issues);
    }

    // ---------------------------------------------------------------------------------------------
    // §4: the ONE aggregated, deterministic, advice-free audit line.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Collect_EmitsExactlyOneAuditLine_WithTheFirstRunSummaryNumbers()
    {
        var mrcyFeed = FeedBinding("dddddddd-0000-0000-0000-000000000001", MrcyId, MrcyToken);
        var rklbFeed = FeedBinding(
            "dddddddd-0000-0000-0000-000000000002", RklbId, RklbToken, "Rocket Lab — News");

        var reader = new FakeNewsSearchReader
        {
            // MRCY: 2 retained + 2 relevant tail items ⇒ at limit AND confirmed truncation.
            [MrcyPhrase] = Read(
                prefix:
                [
                    Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters"),
                    Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews"),
                ],
                tail:
                [
                    Article(ItemUrl(3), "Mercury Systems opens a facility - Yahoo Finance"),
                    Article(ItemUrl(4), "Mercury Systems names a CFO - MarketBeat"),
                ]),
            // RKLB: a single item ⇒ below limit. Observed sizes are therefore 4 and 1 ⇒ max 4, median 2.5.
            [RklbPhrase] = Plain([Article(ItemUrl(9), "Rocket Lab wins a launch contract - SpaceNews")]),
        };

        var logger = new CapturingLogger<NewsAttentionCollector>();
        var collector = new NewsAttentionCollector(
            reader,
            logger,
            new FixedTime(FixedNow),
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, MaxRecordsPerCompany = 2 });

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(RklbId, "Rocket Lab", "RKLB")],
            [mrcyFeed, rklbFeed]);

        await collector.CollectAsync(context, CancellationToken.None);

        var audit = Assert.Single(
            logger.Entries, e => e.Message.Contains("local-limit audit", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Contains("diagnostic only; no response-tail item was admitted", audit.Message, StringComparison.Ordinal);
        Assert.Contains("1 company/companies reached the effective LOCAL retention limit of 2", audit.Message, StringComparison.Ordinal);
        Assert.Contains("1 confirmed a response tail beyond it", audit.Message, StringComparison.Ordinal);
        Assert.Contains("2 additional unique company-relevant tail item(s)", audit.Message, StringComparison.Ordinal);
        Assert.Contains("across 2 successful feed(s): max 4, median 2.5", audit.Message, StringComparison.Ordinal);
        Assert.Contains("3 evidence item(s), 3 observation candidate(s)", audit.Message, StringComparison.Ordinal);

        // Advice-free (the hard output rule) and never a claim about the provider.
        foreach (var banned in new[] { "buy", "sell", "guaranteed", "safe bet", "provider cap" })
        {
            Assert.DoesNotContain(banned, audit.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Collect_NoSuccessfulFeed_RendersTheDistributionAsUnmeasured()
    {
        // A measured zero and an unmeasured one are different facts: with nothing observed, the audit says
        // n/a rather than printing 0 as if it had been measured.
        var reader = new FakeNewsSearchReader();
        reader.SetFailure(MrcyPhrase, NewsSearchReadOutcome.Unreachable, "transport error");

        var logger = new CapturingLogger<NewsAttentionCollector>();
        var collector = new NewsAttentionCollector(
            reader,
            logger,
            new FixedTime(FixedNow),
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, MaxRecordsPerCompany = 5 });

        var feed = FeedBinding("dddddddd-0000-0000-0000-000000000003", MrcyId, MrcyToken);
        await collector.CollectAsync(
            new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]),
            CancellationToken.None);

        var audit = Assert.Single(
            logger.Entries, e => e.Message.Contains("local-limit audit", StringComparison.Ordinal));
        Assert.Contains("across 0 successful feed(s): max n/a, median n/a", audit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_TwoRunsOverTheSameResponses_ProduceTheSameAuditLine()
    {
        // AD-3: the audit is a pure function of the responses, so it is byte-stable across runs.
        static FakeNewsSearchReader NewReader() => new()
        {
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail: [Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews")]),
        };

        var messages = new List<string>();
        for (var run = 0; run < 2; run++)
        {
            var logger = new CapturingLogger<NewsAttentionCollector>();
            var collector = new NewsAttentionCollector(
                NewReader(),
                logger,
                new FixedTime(FixedNow),
                new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, MaxRecordsPerCompany = 1 });

            await collector.CollectAsync(
                new CollectionContext(
                    [Company(MrcyId, "Mercury Systems", "MRCY")],
                    [FeedBinding("dddddddd-0000-0000-0000-000000000004", MrcyId, MrcyToken)]),
                CancellationToken.None);

            messages.Add(logger.Entries
                .Single(e => e.Message.Contains("local-limit audit", StringComparison.Ordinal)).Message);
        }

        Assert.Equal(messages[0], messages[1]);
    }

    // ---------------------------------------------------------------------------------------------
    // SPEC 195 §3 — the unadmitted relevant tail is COMPANY-WIDE UNIQUE, not a sum of per-feed counts.
    //
    // Spec 190 deduped inside each feed and then summed the per-feed integers, which overcounted twice:
    // the same relevant tail URL returned by two feeds counted twice, and a URL in feed A's tail counted
    // as unadmitted even when feed B admitted that URL in its retained prefix. The accumulator now holds
    // company-scoped sets and differences them once, so the answer is a property of the COMPANY and cannot
    // depend on feed iteration order.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The same relevant tail URL observed by TWO of a company's feeds is ONE unadmitted article. Radar
    /// declined to admit one thing, and saying two would overstate what it discarded.
    /// </summary>
    [Fact]
    public async Task Collect_TailUrlObservedByTwoOfACompanysFeeds_IsCountedOnce()
    {
        var reader = new FakeNewsSearchReader
        {
            // Feed A: retains item 1, observes item 9 in its tail.
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail: [Article(ItemUrl(9), "Mercury Systems opens a facility - Yahoo Finance")]),
            // Feed B: a different query, retaining a different article, but its tail holds the SAME item 9.
            [SecondPhrase] = Read(
                prefix: [Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews")],
                tail: [Article(ItemUrl(9), "Mercury Systems opens a facility - Yahoo Finance")]),
        };

        var coverage = await CollectTwoFeedCoverage(reader, maxRecords: 1);

        Assert.Equal(1, coverage.UnadmittedRelevantTailItemCount);
    }

    /// <summary>
    /// A URL in one feed's TAIL that another feed RETAINED in its prefix is not unadmitted — some feed
    /// admitted it. Asserted in BOTH feed orders, because "independent of feed iteration order" is a claim
    /// that has to be measured rather than commented: under the per-feed sum the answer depended on whether
    /// the admitting feed ran first.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collect_TailUrlAdmittedInAnotherFeedsPrefix_CountsZero_RegardlessOfFeedOrder(
        bool reverseFeedOrder)
    {
        var reader = new FakeNewsSearchReader
        {
            // Feed A observes item 7 only in its TAIL …
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail: [Article(ItemUrl(7), "Mercury Systems names a CFO - MarketBeat")]),
            // … while feed B RETAINS item 7 in its prefix, so the company did admit that article.
            [SecondPhrase] = Plain([Article(ItemUrl(7), "Mercury Systems names a CFO - MarketBeat")]),
        };

        var coverage = await CollectTwoFeedCoverage(reader, maxRecords: 1, reverseFeedOrder);

        Assert.Equal(0, coverage.UnadmittedRelevantTailItemCount);
    }

    /// <summary>
    /// The general order-independence property over a mixed fixture (overlapping tails, a cross-feed
    /// prefix admission and one genuinely unadmitted article): every feed permutation reports the same
    /// count, and it is the count of DISTINCT tail URLs no feed retained.
    /// </summary>
    [Fact]
    public async Task Collect_UnadmittedTailCount_IsTheSameInEitherFeedOrder()
    {
        static FakeNewsSearchReader NewReader() => new()
        {
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail:
                [
                    Article(ItemUrl(7), "Mercury Systems names a CFO - MarketBeat"),   // admitted by feed B
                    Article(ItemUrl(9), "Mercury Systems opens a facility - Yahoo Finance"), // shared tail
                ]),
            [SecondPhrase] = Read(
                prefix: [Article(ItemUrl(7), "Mercury Systems names a CFO - MarketBeat")],
                tail:
                [
                    Article(ItemUrl(9), "Mercury Systems opens a facility - Yahoo Finance"), // shared tail
                    Article(ItemUrl(8), "Mercury Systems raises guidance - Reuters"),   // genuinely unadmitted
                ]),
        };

        var forward = await CollectTwoFeedCoverage(NewReader(), maxRecords: 1);
        var reversed = await CollectTwoFeedCoverage(NewReader(), maxRecords: 1, reverseFeedOrder: true);

        // Items 9 and 8 are unadmitted; item 7 was retained by a feed, so it is not.
        Assert.Equal(2, forward.UnadmittedRelevantTailItemCount);
        Assert.Equal(forward.UnadmittedRelevantTailItemCount, reversed.UnadmittedRelevantTailItemCount);
    }

    /// <summary>
    /// An IRRELEVANT tail article is still excluded, in exactly spec 190's terms: the company-scoped set is
    /// populated through the SAME <c>IsRelevant</c> predicate, and this slice introduces no canonicalization,
    /// tracking-query stripping or wider semantic duplicate rule.
    /// </summary>
    [Fact]
    public async Task Collect_IrrelevantTailArticles_AreStillExcludedFromTheCompanySet()
    {
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Read(
                prefix: [Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters")],
                tail:
                [
                    Article(ItemUrl(9), "An unrelated company restructures - Reuters"),
                    Article(ItemUrl(10), "Mercury Systems opens a facility - Yahoo Finance"),
                ]),
        };

        var coverage = await CollectOneCompanyCoverage(reader, maxRecords: 1);

        Assert.Equal(1, coverage.UnadmittedRelevantTailItemCount);
    }

    /// <summary>
    /// THE non-goal, pinned: the tail diagnostics are OBSERVATIONAL. Two fixtures differing ONLY in the
    /// diagnostic tail produce record-for-record identical evidence and observation candidates and identical
    /// collection counters — only the diagnostic number moves. Nothing here admits one extra item or raises
    /// a cap.
    /// </summary>
    [Fact]
    public async Task Collect_TailDiagnostics_ChangeNoAdmittedEvidenceObservationOrCounter()
    {
        var prefix = new[]
        {
            Article(ItemUrl(1), "Mercury Systems wins a radar contract - Reuters"),
            Article(ItemUrl(2), "Mercury Systems expands production - SpaceNews"),
        };

        // Identical retained prefixes; one fixture additionally observes a tail the other never saw.
        var withoutTail = new FakeNewsSearchReader { [MrcyPhrase] = Plain(prefix) };
        var withTail = new FakeNewsSearchReader
        {
            [MrcyPhrase] = Read(
                prefix: prefix,
                tail:
                [
                    Article(ItemUrl(3), "Mercury Systems opens a facility - Yahoo Finance"),
                    Article(ItemUrl(4), "Mercury Systems names a CFO - MarketBeat"),
                ]),
        };

        var feed = FeedBinding("eeeeeeee-0000-0000-0000-000000000001", MrcyId, MrcyToken);
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var plain = await CreateCollector(withoutTail, maxRecords: 2).CollectAsync(context, CancellationToken.None);
        var tailed = await CreateCollector(withTail, maxRecords: 2).CollectAsync(context, CancellationToken.None);

        // Record for record, field for field — a new evidence field is therefore covered the day it lands.
        Assert.Equal(EvidenceFields(plain.Evidence), EvidenceFields(tailed.Evidence));
        Assert.Equal(ObservationFields(plain.Observations!), ObservationFields(tailed.Observations!));

        // Collection counters are untouched; ONLY the diagnostic moves.
        Assert.Equal(plain.Summary.SourcesChecked, tailed.Summary.SourcesChecked);
        Assert.Equal(plain.Summary.SourcesFailed, tailed.Summary.SourcesFailed);
        Assert.Equal(0, Assert.Single(plain.CompanyCoverage!).UnadmittedRelevantTailItemCount);
        Assert.Equal(2, Assert.Single(tailed.CompanyCoverage!).UnadmittedRelevantTailItemCount);
    }

    private static object[] EvidenceFields(IEnumerable<CollectedEvidence> evidence) =>
    [
        .. evidence.Select(object (e) => new
        {
            e.SourceType,
            e.SourceName,
            e.SourceUrl,
            e.Title,
            e.RawText,
            e.PublishedAt,
            e.CollectedAt,
            Metadata = string.Join("|", e.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + kv.Value)),
            CompanyHints = string.Join("|", e.CompanyHints),
        })
    ];

    private static object[] ObservationFields(IEnumerable<NewsObservationCandidate> observations) =>
    [
        .. observations.Select(object (o) => new
        {
            o.CompanyId,
            o.Ticker,
            o.Collector,
            o.QueryPhrase,
            o.FeedId,
            o.FeedName,
            o.GoogleLandingUrl,
            o.Publisher,
            o.PublisherSiteUrl,
            o.Headline,
            o.DescriptionRaw,
            o.DescriptionText,
            o.DescriptionTruncated,
            o.PublishedAtUtc,
            o.RetrievedAtUtc,
        })
    ];

    /// <summary>
    /// Two feeds bound to the SAME company but carrying DIFFERENT query phrases, so the fake reader can
    /// answer them with different responses. Both phrases match the fixture headlines under the unchanged
    /// <c>IsRelevant</c> rule, so relevance is never what a §3 test is measuring.
    /// </summary>
    private static async Task<CollectorCompanyCoverage> CollectTwoFeedCoverage(
        FakeNewsSearchReader reader, int maxRecords, bool reverseFeedOrder = false)
    {
        var feedA = FeedBinding("cccccccc-0000-0000-0000-0000000000a1", MrcyId, MrcyToken, "Mercury — News A");
        var feedB = FeedBinding(
            "cccccccc-0000-0000-0000-0000000000b1", MrcyId, SecondToken, "Mercury — News B");

        var feeds = reverseFeedOrder ? new[] { feedB, feedA } : [feedA, feedB];
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], feeds);

        var result = await CreateCollector(reader, maxRecords)
            .CollectAsync(context, CancellationToken.None);

        return Assert.Single(result.CompanyCoverage!);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static async Task<CollectorCompanyCoverage> CollectOneCompanyCoverage(
        FakeNewsSearchReader reader, int maxRecords)
    {
        var feed = FeedBinding("cccccccc-0000-0000-0000-000000000002", MrcyId, MrcyToken);
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader, maxRecords)
            .CollectAsync(context, CancellationToken.None);

        // A tail item can never become evidence, so the sidecar and the evidence set stay in lockstep and
        // neither may carry a URL the reader placed in the diagnostic tail.
        Assert.Equal(result.Evidence.Count, result.Observations!.Count);
        var admittedUrls = result.Evidence.Select(e => e.SourceUrl).ToHashSet(StringComparer.Ordinal);
        Assert.All(
            reader.DiagnosticTailUrls,
            url => Assert.DoesNotContain(url, admittedUrls, StringComparer.Ordinal));

        return Assert.Single(result.CompanyCoverage!);
    }

    private static NewsAttentionCollector CreateCollector(INewsSearchReader reader, int maxRecords) =>
        new(
            reader,
            NullLogger<NewsAttentionCollector>.Instance,
            new FixedTime(FixedNow),
            new NewsCollectorOptions
            {
                InterRequestDelay = TimeSpan.Zero,
                MaxRecordsPerCompany = maxRecords,
            });

    private static HttpNewsSearchReader CreateReader(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(
            new HttpClient(new CountingHandler(status, body)),
            NullLogger<HttpNewsSearchReader>.Instance,
            new FixedTime(FixedNow));

    private static NewsSearchQuery SearchQuery(int maxRecords) =>
        new(QueryPhrase: MrcyPhrase, MaxRecords: maxRecords, EnglishOnly: true);

    private static NewsSearchReadResult Read(
        IReadOnlyList<NewsArticleItem> prefix, IReadOnlyList<NewsArticleItem> tail) =>
        NewsSearchReadResult.Success(prefix, prefix.Count + tail.Count, tail);

    /// <summary>A success with NO diagnostic scan recorded — the legacy factory every other test fake uses.</summary>
    private static NewsSearchReadResult Plain(IReadOnlyList<NewsArticleItem> items) =>
        NewsSearchReadResult.Success(items);

    private static string ItemUrl(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"https://news.google.com/rss/articles/ITEM{index:D3}");

    private static string ItemTitle(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"Mercury Systems update {index:D3} - SpaceNews");

    /// <summary>An RSS 2.0 search response holding <paramref name="count"/> structurally valid items.</summary>
    private static string Feed(int count) => FeedWith(BuildItems(1, count));

    private static string FeedWith(string items) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>"Mercury Systems" - Google News</title>
            <link>https://news.google.com/search?q=Mercury+Systems</link>
        {items}  </channel>
        </rss>
        """;

    private static string BuildItems(int from, int toInclusive)
    {
        var builder = new StringBuilder();
        for (var i = from; i <= toInclusive; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"""
                    <item>
                      <title>{ItemTitle(i)}</title>
                      <link>{ItemUrl(i)}</link>
                      <pubDate>Thu, 02 Jul 2026 12:40:51 GMT</pubDate>
                      <source url="https://spacenews.com">SpaceNews</source>
                    </item>

                """);
        }

        return builder.ToString();
    }

    /// <summary>An <c>&lt;item&gt;</c> with no <c>&lt;link&gt;</c> — structurally invalid, skipped as before.</summary>
    private static string LinklessItem() =>
        """
            <item>
              <title>Mercury Systems headline with no landing page - SpaceNews</title>
              <pubDate>Thu, 02 Jul 2026 12:40:51 GMT</pubDate>
            </item>

        """;

    private static NewsArticleItem Article(string url, string title, string sourceName = "SpaceNews") =>
        new(
            Url: url,
            Title: title,
            SourceName: sourceName,
            PublishedAt: new DateTimeOffset(2026, 8, 26, 11, 0, 0, TimeSpan.Zero),
            RetrievedAt: FixedNow);

    private static Company Company(Guid id, string name, string ticker) =>
        new(
            Id: id,
            Name: name,
            LegalName: null,
            Ticker: ticker,
            Exchange: null,
            CountryCode: null,
            Sector: null,
            Industry: null,
            Status: CompanyStatus.Active,
            CreatedAtUtc: FixedNow,
            UpdatedAtUtc: FixedNow,
            Themes: []);

    private static CompanySourceFeed FeedBinding(
        string id, Guid companyId, string token, string name = "Mercury — News") =>
        new(Guid.Parse(id), companyId, "newssearch", name, token, FixedNow);

    private sealed class FakeNewsSearchReader : INewsSearchReader
    {
        private readonly Dictionary<string, NewsSearchReadResult> _byPhrase = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        /// <summary>Every URL this fake placed in a diagnostic tail — none of them may ever be admitted.</summary>
        public IEnumerable<string> DiagnosticTailUrls =>
            _byPhrase.Values.SelectMany(r => r.DiagnosticTail).Select(a => a.Url);

        /// <summary>Seeds one phrase's whole read result, so a test can supply a diagnostic tail as well as a prefix.</summary>
        public NewsSearchReadResult this[string phrase]
        {
            set => _byPhrase[phrase] = value;
        }

        public void SetFailure(string phrase, NewsSearchReadOutcome outcome, string detail) =>
            _byPhrase[phrase] = NewsSearchReadResult.Failure(outcome, detail);

        public Task<NewsSearchReadResult> ReadAsync(NewsSearchQuery query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(
                _byPhrase.TryGetValue(query.QueryPhrase, out var result)
                    ? result
                    : NewsSearchReadResult.Success([]));
        }
    }

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            });
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
