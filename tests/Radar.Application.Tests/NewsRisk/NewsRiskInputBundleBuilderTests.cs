using Radar.Application.News;
using Radar.Application.NewsRisk;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §4: the point-in-time input bundle — admission filters (nothing after the selection instant D
/// enters), the publisher-suffix-only headline dedupe, newest-first ordering, the article/fetched caps, and
/// the honest assessment cutoff (max over supplied retrieval instants, never backward).
/// </summary>
public sealed class NewsRiskInputBundleBuilderTests
{
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTimeOffset D = NewsRiskTestData.SelectionAsOf;

    private static NewsRiskInputBundle Build(
        IReadOnlyList<NewsObservationRecord> observations, int maxArticles = 12, int maxFetched = 3) =>
        NewsRiskInputBundleBuilder.Build(
            Company, observations, D,
            lookbackDays: 30, maxArticlesPerCompany: maxArticles, maxFetchedArticlesPerCompany: maxFetched);

    [Fact]
    public void PostCutoffAndOutOfWindowObservations_NeverEnter()
    {
        var admitted = NewsRiskTestData.Observation(Company, "in window", D.AddDays(-1));
        var observations = new[]
        {
            admitted,
            // Observed after D.
            NewsRiskTestData.Observation(Company, "future observed", D.AddHours(1)),
            // Retrieved after D even though observed before it.
            NewsRiskTestData.Observation(Company, "future retrieved", D.AddDays(-1), retrievedAtUtc: D.AddHours(2)),
            // Published after D.
            NewsRiskTestData.Observation(Company, "future published", D.AddDays(-1), publishedAtUtc: D.AddHours(3)),
            // Older than the lookback window.
            NewsRiskTestData.Observation(Company, "stale", D.AddDays(-31)),
            // Another company entirely.
            NewsRiskTestData.Observation(Guid.NewGuid(), "other company", D.AddDays(-1)),
        };

        var bundle = Build(observations);

        var article = Assert.Single(bundle.Articles);
        Assert.Equal(admitted.ObservationId, article.ObservationId);
    }

    [Fact]
    public void NullPublishedAt_IsAdmitted()
    {
        var bundle = Build([NewsRiskTestData.Observation(Company, "no pub date", D.AddDays(-2), publishedAtUtc: null)]);

        Assert.Single(bundle.Articles);
    }

    [Fact]
    public void DuplicateNormalizedHeadlines_Collapse_UsingOnlyThePublisherSuffixStrip()
    {
        var newer = NewsRiskTestData.Observation(Company, "Acme wins contract - Reuters", D.AddDays(-1));
        var older = NewsRiskTestData.Observation(Company, "Acme wins contract - Yahoo Finance", D.AddDays(-2));
        // A merely whitespace-different headline is NOT a duplicate — normalization is ONLY the suffix strip.
        var spaced = NewsRiskTestData.Observation(Company, "Acme  wins contract - Reuters", D.AddDays(-3));

        var bundle = Build([older, newer, spaced]);

        Assert.Equal(2, bundle.Articles.Count);
        // Newest copy survives, with its STORED headline untouched (suffix intact — provenance).
        Assert.Equal(newer.ObservationId, bundle.Articles[0].ObservationId);
        Assert.Equal("Acme wins contract - Reuters", bundle.Articles[0].Headline);
        Assert.Equal(spaced.ObservationId, bundle.Articles[1].ObservationId);
    }

    [Fact]
    public void Order_IsNewestFirst_ThenObservationId_AndTheCapKeepsTheNewest()
    {
        var oldest = NewsRiskTestData.Observation(Company, "h1", D.AddDays(-3));
        var middle = NewsRiskTestData.Observation(Company, "h2", D.AddDays(-2));
        var newest = NewsRiskTestData.Observation(Company, "h3", D.AddDays(-1));

        var bundle = Build([oldest, newest, middle], maxArticles: 2);

        Assert.Equal(
            new[] { newest.ObservationId, middle.ObservationId },
            bundle.Articles.Select(a => a.ObservationId));
    }

    [Fact]
    public void RssOnlyInput_KeepsTheSelectionInstantAsTheCutoff()
    {
        var bundle = Build([NewsRiskTestData.Observation(Company, "h", D.AddDays(-1))]);

        Assert.Equal(D, bundle.AssessmentCutoffUtc);
    }

    [Fact]
    public void StoredFetchedBody_MovesTheCutoffToTheActualRetrievalInstant_NeverBackward()
    {
        // A retrospective body fetched at E (after D): the assessment cutoff becomes E, because the
        // assessment could not have existed before E. The article itself was observed before D.
        var fetchAt = D.AddHours(5);
        var fetched = NewsRiskTestData.Observation(
            Company, "with body", D.AddDays(-1),
            articleFetch: new NewsArticleFetchResult(
                Outcome: NewsArticleFetchOutcome.Fetched,
                RetrievedAtUtc: fetchAt,
                RedirectHops: 0,
                ResolvedUrl: "https://example.com/a",
                HttpStatus: 200,
                ContentType: "text/html",
                Truncated: false,
                ExtractorVersion: "vt-1",
                ContentHash: "bodyhash",
                BodyText: "the publisher body text",
                RetrievalPolicy: "policy-1"));

        var bundle = Build([fetched]);

        Assert.Equal("the publisher body text", Assert.Single(bundle.Articles).BodyText);
        Assert.Equal(fetchAt, bundle.AssessmentCutoffUtc);

        // A body retrieved BEFORE D never moves the cutoff backward: max(D, …) is structural.
        var early = fetched with
        {
            ArticleFetch = fetched.ArticleFetch! with { RetrievedAtUtc = D.AddDays(-10) },
        };
        Assert.Equal(D, Build([early]).AssessmentCutoffUtc);
    }

    [Fact]
    public void FetchedBodyCap_AttachesNewestFirst_AndZeroDisablesAttachment()
    {
        NewsObservationRecord WithBody(string headline, DateTimeOffset at) =>
            NewsRiskTestData.Observation(
                Company, headline, at,
                articleFetch: new NewsArticleFetchResult(
                    NewsArticleFetchOutcome.Fetched, at, 0, null, 200, "text/html",
                    false, "vt-1", "hash-" + headline, "body of " + headline, "policy-1"));

        var a = WithBody("a", D.AddDays(-1));
        var b = WithBody("b", D.AddDays(-2));
        var c = WithBody("c", D.AddDays(-3));

        var bundle = Build([a, b, c], maxFetched: 2);
        Assert.Equal(
            [true, true, false],
            bundle.Articles.Select(x => x.BodyText is not null));

        var none = Build([a, b, c], maxFetched: 0);
        Assert.All(none.Articles, x => Assert.Null(x.BodyText));
    }

    [Fact]
    public void NonFetchedStoredAttempt_NeverSuppliesABody()
    {
        var paywalled = NewsRiskTestData.Observation(
            Company, "paywalled", D.AddDays(-1),
            articleFetch: new NewsArticleFetchResult(
                NewsArticleFetchOutcome.Paywalled, D.AddDays(-1), 0, null, 403, null,
                false, null, null, null, "policy-1"));

        var bundle = Build([paywalled]);

        Assert.Null(Assert.Single(bundle.Articles).BodyText);
    }

    [Fact]
    public void QualifyingCount_CountsDedupeSurvivors_NotDuplicates_AndTheCapMakesTheBundleCapped()
    {
        // Four observations, two sharing a normalized headline: THREE qualify (a dedupe-collapsed
        // duplicate is NOT a cap drop — spec 182 §2). With a cap of 2, one qualifying article is dropped.
        var a = NewsRiskTestData.Observation(Company, "Acme wins contract - Reuters", D.AddDays(-1));
        var dupe = NewsRiskTestData.Observation(Company, "Acme wins contract - Yahoo Finance", D.AddDays(-2));
        var b = NewsRiskTestData.Observation(Company, "Second story", D.AddDays(-3));
        var c = NewsRiskTestData.Observation(Company, "Third story", D.AddDays(-4));

        var uncapped = Build([a, dupe, b, c]);
        Assert.Equal(3, uncapped.Articles.Count);
        Assert.Equal(3, uncapped.QualifyingArticleCount);
        Assert.Equal(NewsRiskAssessmentBundle.Complete, uncapped.Completeness);

        var capped = Build([a, dupe, b, c], maxArticles: 2);
        Assert.Equal(2, capped.Articles.Count);
        Assert.Equal(3, capped.QualifyingArticleCount);
        Assert.Equal(NewsRiskAssessmentBundle.Capped, capped.Completeness);
    }

    // ---------------------------------------------------------------------------------------------
    // SPEC 193 §3 — count the syndication the duplicate-headline collapse discards.
    //
    // Only the SURVIVING article's own Publisher travels into the bundle, so a company with 40 syndicated
    // copies of one story used to be indistinguishable from one with a single article — and syndication
    // breadth is itself a presence measurement.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SyndicationCounts_NCopiesAcrossMPublishers_RecordNMinusOneCollapsed_AndMPublishers()
    {
        // Four copies of ONE story across three distinct publishers, plus an unrelated story.
        var a = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Reuters", D.AddDays(-1), publisher: "Reuters");
        var b = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Yahoo Finance", D.AddDays(-2), publisher: "Yahoo Finance");
        var c = NewsRiskTestData.Observation(
            Company, "Acme wins contract - MarketWatch", D.AddDays(-3), publisher: "MarketWatch");
        var d = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Reuters", D.AddDays(-4), publisher: "Reuters");
        var unrelated = NewsRiskTestData.Observation(
            Company, "Unrelated story", D.AddDays(-5), publisher: "Barron's");

        var bundle = Build([a, b, c, d, unrelated]);

        // Two survivors: the newest copy of the syndicated story, plus the unrelated one.
        Assert.Equal(2, bundle.Articles.Count);

        // N = 4 copies ⇒ N − 1 = 3 collapsed.
        Assert.Equal(3, bundle.SyndicatedDuplicateCount);

        // M = 3 distinct publishers carried the collapsed STORY (the survivor's own publisher included —
        // this is the story's syndication breadth, not a count of publishers whose copy was dropped). The
        // unrelated story's publisher never counts: it collapsed nothing.
        Assert.Equal(3, bundle.SyndicatedDistinctPublisherCount);
    }

    [Fact]
    public void SyndicationCounts_AreZeroWhenNothingCollapses()
    {
        var a = NewsRiskTestData.Observation(Company, "First story", D.AddDays(-1), publisher: "Reuters");
        var b = NewsRiskTestData.Observation(Company, "Second story", D.AddDays(-2), publisher: "Barron's");

        var bundle = Build([a, b]);

        Assert.Equal(2, bundle.Articles.Count);
        Assert.Equal(0, bundle.SyndicatedDuplicateCount);
        Assert.Equal(0, bundle.SyndicatedDistinctPublisherCount);
    }

    [Fact]
    public void SyndicationCounts_AreIndependentOfTheArticleCap()
    {
        // The collapse runs BEFORE the cap, so capping the bundle cannot change what collapsed.
        var a = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Reuters", D.AddDays(-1), publisher: "Reuters");
        var b = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Yahoo Finance", D.AddDays(-2), publisher: "Yahoo Finance");
        var c = NewsRiskTestData.Observation(Company, "Second story", D.AddDays(-3));
        var d = NewsRiskTestData.Observation(Company, "Third story", D.AddDays(-4));

        var uncapped = Build([a, b, c, d]);
        var capped = Build([a, b, c, d], maxArticles: 1);

        Assert.Equal(1, uncapped.SyndicatedDuplicateCount);
        Assert.Equal(2, uncapped.SyndicatedDistinctPublisherCount);
        Assert.Equal(uncapped.SyndicatedDuplicateCount, capped.SyndicatedDuplicateCount);
        Assert.Equal(
            uncapped.SyndicatedDistinctPublisherCount, capped.SyndicatedDistinctPublisherCount);
    }

    [Fact]
    public void SyndicationCounts_DoNotAffectCompleteness()
    {
        // Completeness keeps its EXACT pre-193 meaning: Capped is about the bundle bound, and a dedupe
        // collapse is not a cap drop (spec 182 §2).
        var a = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Reuters", D.AddDays(-1), publisher: "Reuters");
        var b = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Yahoo Finance", D.AddDays(-2), publisher: "Yahoo Finance");
        var c = NewsRiskTestData.Observation(
            Company, "Acme wins contract - MarketWatch", D.AddDays(-3), publisher: "MarketWatch");

        var bundle = Build([a, b, c]);

        Assert.Single(bundle.Articles);
        Assert.Equal(1, bundle.QualifyingArticleCount);
        Assert.Equal(2, bundle.SyndicatedDuplicateCount);
        Assert.Equal(NewsRiskAssessmentBundle.Complete, bundle.Completeness);
    }

    [Fact]
    public void SyndicationCounts_AreNotABundleHashInput_SoTheAssessmentCacheKeyDoesNotMove()
    {
        // The pin: for the SAME surviving articles, the hash is byte-identical whether or not copies were
        // collapsed to get there. The precedent is QualifyingArticleCount's — the hash stays over the
        // supplied articles only, so no cohort forks.
        var survivor = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Reuters", D.AddDays(-1), publisher: "Reuters");
        var syndicatedCopy = NewsRiskTestData.Observation(
            Company, "Acme wins contract - Yahoo Finance", D.AddDays(-2), publisher: "Yahoo Finance");

        var withSyndication = Build([survivor, syndicatedCopy]);
        var withoutSyndication = Build([survivor]);

        // Same single surviving article either way.
        Assert.Equal(
            withoutSyndication.Articles.Select(a => a.ObservationId),
            withSyndication.Articles.Select(a => a.ObservationId));

        Assert.Equal(1, withSyndication.SyndicatedDuplicateCount);
        Assert.Equal(0, withoutSyndication.SyndicatedDuplicateCount);
        Assert.Equal(withoutSyndication.BundleHash, withSyndication.BundleHash);
    }

    [Fact]
    public void QualifyingCount_IsNotABundleHashInput_SoTheCacheKeyDoesNotMove()
    {
        // The hash stays over the SUPPLIED articles only: a capped bundle hashes identically to a bundle
        // built from just its supplied articles, whatever the qualifying count says.
        var a = NewsRiskTestData.Observation(Company, "First story", D.AddDays(-1));
        var b = NewsRiskTestData.Observation(Company, "Second story", D.AddDays(-2));
        var c = NewsRiskTestData.Observation(Company, "Third story", D.AddDays(-3));

        var capped = Build([a, b, c], maxArticles: 2);
        var exact = Build([a, b], maxArticles: 2);

        Assert.Equal(NewsRiskAssessmentBundle.Capped, capped.Completeness);
        Assert.Equal(NewsRiskAssessmentBundle.Complete, exact.Completeness);
        Assert.Equal(exact.BundleHash, capped.BundleHash);
        Assert.Equal(
            NewsRiskInputBundleBuilder.ComputeBundleHash(capped.Articles), capped.BundleHash);
    }

    [Fact]
    public void BundleHash_ChangesWithContentIdentity_Order_AndSuppliedFields()
    {
        var obsA = NewsRiskTestData.Article(Guid.NewGuid(), "a", description: "d");
        var obsB = NewsRiskTestData.Article(Guid.NewGuid(), "b", description: "d");

        var baseline = NewsRiskInputBundleBuilder.ComputeBundleHash([obsA, obsB]);

        Assert.NotEqual(baseline, NewsRiskInputBundleBuilder.ComputeBundleHash([obsB, obsA]));
        Assert.NotEqual(baseline, NewsRiskInputBundleBuilder.ComputeBundleHash([obsA]));
        Assert.NotEqual(
            baseline,
            NewsRiskInputBundleBuilder.ComputeBundleHash([obsA with { DescriptionText = null }, obsB]));
        Assert.NotEqual(
            baseline,
            NewsRiskInputBundleBuilder.ComputeBundleHash(
                [obsA with { BodyText = "body", BodyContentHash = "bh" }, obsB]));
        Assert.Equal(baseline, NewsRiskInputBundleBuilder.ComputeBundleHash([obsA, obsB]));
    }
}
