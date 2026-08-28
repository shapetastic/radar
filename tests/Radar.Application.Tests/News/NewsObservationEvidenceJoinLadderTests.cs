using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.News;

/// <summary>
/// SPEC 197 §1.1/§1.2 — the deterministic observation→evidence MATCH LADDER, and the precedence rules that
/// make it fail-closed: exact URL + headline + publication instant, then exact URL + headline, then the
/// pre-197 unique-headline rule. Zero candidates may fall through; ambiguity may not.
/// <para>
/// Every fixture is CONSTRUCTED. Nothing here reads, edits or regenerates a live artifact — the live shapes
/// these tests encode (the EOSE same-title/same-URL/different-instant pair) are rebuilt from first
/// principles so no assertion can go green because of what happens to be on disk.
/// </para>
/// </summary>
public sealed class NewsObservationEvidenceJoinLadderTests
{
    private static readonly Guid CompanyA = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = new("bbbbbbbb-0000-0000-0000-00000000000b");

    private const string Url = "https://news.google.com/rss/articles/CBMiK2h0dHBzOi8vZXhhbXBsZS5jb20?oc=5";
    private const string OtherUrl = "https://news.google.com/rss/articles/CBMiZGlmZmVyZW50?oc=5";
    private const string Headline = "Eos Energy narrows quarterly loss";

    // The live EOSE shape: one article published 2026-06-30T07:00:00+00:00, and a second record carrying
    // the SAME title and URL at a different instant.
    private static readonly DateTimeOffset Published = new(2026, 6, 30, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Republished = new(2026, 7, 2, 11, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Collected = new(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);

    private static Guid Id(int n) => new($"eeeeeeee-0000-0000-0000-{n:D12}");

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 1 — the live EOSE shape.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SameTitleAndUrl_DifferentInstants_ResolveByTheExactInstant()
    {
        // THE LIVE FAILURE, reconstructed. Two evidence records share the normalized title AND the article
        // URL; only the publication instant separates them. The pre-197 title-only join saw two title
        // matches, declared the article ambiguous and discarded the very fields that select one record —
        // which is why EOSE's validated Deteriorating judgment produced no signal at all.
        //
        // MUTATION PROOF: restoring title-only joining (or dropping tier 1) turns this red — the
        // observation's title bucket genuinely holds TWO evidence records, asserted below.
        var published = News(Id(1), Headline, Url, Published);
        var republished = News(Id(2), Headline, Url, Republished);
        var observation = Observation(Id(101), CompanyA, Headline, Url, Published);

        var join = NewsObservationEvidenceJoin.Build([observation], [published, republished]);

        var resolution = join.Resolve(observation.ObservationId);
        Assert.Equal(NewsObservationEvidenceDisposition.ExactArticleInstant, resolution.Disposition);
        Assert.Equal(published.Id, resolution.Match!.EvidenceId);
        Assert.Equal(CompanyA, resolution.Match.CompanyId);
        Assert.Equal(observation.ObservationId, resolution.Match.ObservationId);

        Assert.Equal(1, join.Counts.ExactArticleInstant);
        Assert.Equal(0, join.Counts.UniqueHeadlineFallback);
        Assert.Equal(0, join.Counts.UnjoinedAmbiguous);

        // The weaker buckets really are ambiguous — this is what the pre-197 rule saw.
        Assert.Equal(2, TitleBucketSize([published, republished], Headline));
    }

    [Fact]
    public void TheInstantIsComparedAsAnInstant_NotAsAnOffsetBearingValue()
    {
        // 07:00+00:00 and 09:00+02:00 are the SAME moment written two ways. The evidence and observation
        // writers are not guaranteed to render the same offset, and a key that hashed the offset would
        // silently demote a genuine tier-1 match to the weaker tiers.
        var evidence = News(Id(3), Headline, Url, Published);
        var observation = Observation(
            Id(102), CompanyA, Headline, Url, Published.ToOffset(TimeSpan.FromHours(2)));

        var join = NewsObservationEvidenceJoin.Build([observation], [evidence]);

        Assert.Equal(
            NewsObservationEvidenceDisposition.ExactArticleInstant,
            join.Resolve(observation.ObservationId).Disposition);
    }

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 2 — genuine ambiguity stays fail-closed.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TwoEvidenceRecordsSharingUrlTitleAndInstant_StayAmbiguous_AndNothingIsChosen()
    {
        // MUTATION PROOF: taking the first record (`evidenceIds[0]` without the count check) turns this
        // red. Radar cannot tell which of two identical-identity records the judgment cited, and attaching
        // one article's direction to another article's evidence is the failure the join exists to prevent.
        // The complementary mutation — restoring title-only joining — is proven red by the EOSE test above.
        var first = News(Id(4), Headline, Url, Published);
        var second = News(Id(5), Headline, Url, Published);
        var observation = Observation(Id(103), CompanyA, Headline, Url, Published);

        var join = NewsObservationEvidenceJoin.Build([observation], [first, second]);

        var resolution = join.Resolve(observation.ObservationId);
        Assert.Equal(NewsObservationEvidenceDisposition.Ambiguous, resolution.Disposition);
        Assert.Null(resolution.Match);
        Assert.Null(join.TryMatchByObservation(observation.ObservationId));
        Assert.Null(join.TryMatch(first.Id));
        Assert.Null(join.TryMatch(second.Id));
        Assert.Equal(1, join.Counts.UnjoinedAmbiguous);
        Assert.Equal(0, join.Counts.Joined);
    }

    [Fact]
    public void OneKeyClaimedByTwoCompanies_IsAmbiguousAtTheSTRONGESTTierToo()
    {
        // Exactness of the URL is not a licence to attach one company's verdict to a multi-company article.
        var evidence = News(Id(6), Headline, Url, Published);
        var a = Observation(Id(104), CompanyA, Headline, Url, Published);
        var b = Observation(Id(105), CompanyB, Headline, Url, Published);

        var join = NewsObservationEvidenceJoin.Build([a, b], [evidence]);

        Assert.Equal(
            NewsObservationEvidenceDisposition.Ambiguous, join.Resolve(a.ObservationId).Disposition);
        Assert.Equal(
            NewsObservationEvidenceDisposition.Ambiguous, join.Resolve(b.ObservationId).Disposition);
        Assert.Equal(2, join.Counts.UnjoinedAmbiguous);
    }

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 3 — tier 2, and the rule that ambiguity never falls through.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactUrlAndTitle_Resolve_WhenOneSideHasNoPublicationInstant(bool observationHasInstant)
    {
        // A missing timestamp records NO equality fact, so the observation (or the evidence) cannot enter
        // tier 1 at all. Tier 2 is what a restated or absent instant falls through to — zero candidates may
        // fall through.
        var evidence = News(Id(7), Headline, Url, observationHasInstant ? null : Published);
        var observation = Observation(
            Id(106), CompanyA, Headline, Url, observationHasInstant ? Published : null);

        var join = NewsObservationEvidenceJoin.Build([observation], [evidence]);

        var resolution = join.Resolve(observation.ObservationId);
        Assert.Equal(NewsObservationEvidenceDisposition.ExactArticleUrl, resolution.Disposition);
        Assert.Equal(evidence.Id, resolution.Match!.EvidenceId);
        Assert.Equal(1, join.Counts.ExactArticleUrl);
        Assert.Equal(0, join.Counts.ExactArticleInstant);
        Assert.Equal(0, join.Counts.UniqueHeadlineFallback);
    }

    [Fact]
    public void TwoUrlAndTitleMatches_StayAmbiguous_AndNeverFallThroughToTheHeadlineTier()
    {
        // The observation carries no instant, so tier 1 is not enterable; tier 2 finds TWO records and the
        // ladder stops. See AnAmbiguousStrongTier_StopsTheLadder… below for why this is a structural
        // guarantee rather than a mutation proof: the weaker tier holds the same two records.
        var first = News(Id(8), Headline, Url, Published);
        var second = News(Id(9), Headline, Url, Republished);
        var observation = Observation(Id(107), CompanyA, Headline, Url, publishedAtUtc: null);

        var join = NewsObservationEvidenceJoin.Build([observation], [first, second]);

        Assert.Equal(
            NewsObservationEvidenceDisposition.Ambiguous,
            join.Resolve(observation.ObservationId).Disposition);
        Assert.Equal(0, join.Counts.UniqueHeadlineFallback);
    }

    [Fact]
    public void AnAmbiguousStrongTier_StopsTheLadder_AndTheKeySetsAreMonotoneSoItCannotJoinLater()
    {
        // THE PRECEDENCE RULE, isolated — and stated with its real strength rather than an overclaim.
        //
        // The rule ("ambiguity stops the ladder; only ZERO candidates fall through") is asserted here as a
        // STRUCTURAL guarantee. It is deliberately NOT presented as a mutation proof, because with today's
        // keys it cannot be one: each weaker tier's candidate set is a strict SUPERSET of the stronger
        // tier's (an evidence item keyed on URL+title+instant is also keyed on URL+title and on title
        // alone, and the same nesting holds for the companies claiming a key). Ambiguity is therefore
        // MONOTONE — anything ambiguous at tier 1 is ambiguous at tiers 2 and 3 too — so deleting the early
        // stop changes no outcome today, and a test claiming otherwise would be false.
        //
        // It is still asserted, and the assertion still earns its place: the guarantee must survive a
        // FUTURE key change that breaks the nesting (a tier keyed on a field the weaker tiers do not carry
        // would let a refusal silently become a join), and this test is where that regression surfaces.
        var first = News(Id(10), Headline, Url, Published);
        var second = News(Id(11), Headline, Url, Published);
        var observation = Observation(Id(108), CompanyA, Headline, Url, Published);

        var join = NewsObservationEvidenceJoin.Build([observation], [first, second]);

        Assert.Equal(
            NewsObservationEvidenceDisposition.Ambiguous,
            join.Resolve(observation.ObservationId).Disposition);
        Assert.Equal(0, join.Counts.ExactArticleUrl);
        Assert.Equal(0, join.Counts.UniqueHeadlineFallback);

        // The nesting the paragraph above relies on, made checkable rather than merely asserted in prose:
        // every weaker bucket holds at least what the stronger one held.
        Assert.Equal(2, UrlAndTitleBucketSize([first, second], Url, Headline));
        Assert.Equal(2, TitleBucketSize([first, second], Headline));
    }

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 4 — the weakest tier still works, and the fail-closed cases are unchanged.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AGenuinelyUniqueHeadline_StillJoins_WhenTheUrlsDiffer()
    {
        // The pre-197 rule, preserved: the evidence and observation URLs do not match (a re-rendered link,
        // or evidence collected by another path), so both strong tiers find zero and the ladder falls
        // through to the headline.
        var evidence = News(Id(12), Headline, OtherUrl, Published);
        var observation = Observation(Id(109), CompanyA, Headline, Url, Published);

        var join = NewsObservationEvidenceJoin.Build([observation], [evidence]);

        var resolution = join.Resolve(observation.ObservationId);
        Assert.Equal(NewsObservationEvidenceDisposition.UniqueHeadlineFallback, resolution.Disposition);
        Assert.Equal(evidence.Id, resolution.Match!.EvidenceId);
        Assert.Equal(evidence.Id, join.TryMatch(evidence.Id)!.EvidenceId);
        Assert.Equal(1, join.Counts.UniqueHeadlineFallback);
    }

    [Fact]
    public void BlankKey_NoEvidence_NullCompany_AndCrossCompanyClaims_RetainTheirFailClosedOutcomes()
    {
        var evidence = News(Id(13), Headline, Url, Published);
        var shared = News(Id(14), "Sector index rises", OtherUrl, Published);

        // A blank normalized headline can form no key at ANY tier, even with an exact URL and instant.
        var blank = Observation(Id(110), CompanyA, "—  ---", Url, Published);
        // No evidence carries this article at all.
        var nothing = Observation(Id(111), CompanyA, "A headline no evidence carries", OtherUrl, Published);
        // A null company: Radar cannot tell WHICH company the article belongs to.
        var nullCompany = Observation(Id(112), companyId: null, Headline, Url, Published);
        // The same article claimed by two companies stays ambiguous for BOTH.
        var crossA = Observation(Id(113), CompanyA, "Sector index rises", OtherUrl, Published);
        var crossB = Observation(Id(114), CompanyB, "Sector index rises", OtherUrl, Published);

        var join = NewsObservationEvidenceJoin.Build(
            [blank, nothing, nullCompany, crossA, crossB], [evidence, shared]);

        Assert.Equal(NewsObservationEvidenceDisposition.NoMatch, join.Resolve(blank.ObservationId).Disposition);
        Assert.Equal(NewsObservationEvidenceDisposition.NoMatch, join.Resolve(nothing.ObservationId).Disposition);
        Assert.Equal(
            NewsObservationEvidenceDisposition.NoMatch, join.Resolve(nullCompany.ObservationId).Disposition);
        Assert.Equal(
            NewsObservationEvidenceDisposition.Ambiguous, join.Resolve(crossA.ObservationId).Disposition);
        Assert.Equal(
            NewsObservationEvidenceDisposition.Ambiguous, join.Resolve(crossB.ObservationId).Disposition);

        // An observation this join never saw is NoMatch, never null — the reverse lookup is total.
        Assert.Equal(NewsObservationEvidenceDisposition.NoMatch, join.Resolve(Id(999)).Disposition);

        Assert.Equal(new NewsObservationEvidenceJoinCounts(0, 0, 0, 3, 2), join.Counts);
    }

    [Fact]
    public void SeveralObservationsOfOneArticle_AllResolve_AndReportTheLowestObservationId()
    {
        var evidence = News(Id(15), Headline, Url, Published);
        var low = Observation(Id(200), CompanyA, Headline, Url, Published);
        var high = Observation(Id(300), CompanyA, Headline, Url, Published);

        var join = NewsObservationEvidenceJoin.Build([high, low], [evidence]);

        // Both captures resolve in reverse (a cited fact names whichever one the typing pass typed) and
        // both hand back the SAME match instance — one definition of the representative.
        var byLow = join.TryMatchByObservation(low.ObservationId);
        var byHigh = join.TryMatchByObservation(high.ObservationId);
        Assert.Same(byLow, byHigh);
        Assert.Equal(low.ObservationId, byHigh!.ObservationId);
        Assert.Equal(low.ObservationId, join.TryMatch(evidence.Id)!.ObservationId);
        Assert.Equal(2, join.Counts.ExactArticleInstant);
    }

    // ---------------------------------------------------------------------------------------------
    // §5.2 item 5 — order independence and exact conservation.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void InputOrder_ChangesNoMatch_NoRepresentative_AndNoBucketTotal()
    {
        var (observations, evidence) = MixedFixture();

        var forward = NewsObservationEvidenceJoin.Build(observations, evidence);
        var reversedObservations = Enumerable.Reverse(observations).ToList();
        var reversedEvidence = Enumerable.Reverse(evidence).ToList();
        var reversed = NewsObservationEvidenceJoin.Build(reversedObservations, reversedEvidence);

        Assert.Equal(forward.Counts, reversed.Counts);

        foreach (var observation in observations)
        {
            var a = forward.Resolve(observation.ObservationId);
            var b = reversed.Resolve(observation.ObservationId);
            Assert.Equal(a.Disposition, b.Disposition);
            Assert.Equal(a.Match, b.Match);
        }

        foreach (var item in evidence)
        {
            Assert.Equal(forward.TryMatch(item.Id), reversed.TryMatch(item.Id));
        }
    }

    [Fact]
    public void EveryObservationLandsInExactlyOneBucket_AndTheRoutesSumToJoined()
    {
        var (observations, evidence) = MixedFixture();

        var join = NewsObservationEvidenceJoin.Build(observations, evidence);
        var counts = join.Counts;

        // The conservation identity, stated as the spec states it.
        Assert.Equal(
            observations.Count,
            counts.ExactArticleInstant
                + counts.ExactArticleUrl
                + counts.UniqueHeadlineFallback
                + counts.UnjoinedNoMatch
                + counts.UnjoinedAmbiguous);
        Assert.Equal(observations.Count, counts.Observations);
        Assert.Equal(
            counts.ExactArticleInstant + counts.ExactArticleUrl + counts.UniqueHeadlineFallback,
            counts.Joined);

        // …and the per-observation dispositions agree with the totals, so the buckets cannot be a second,
        // separately-maintained answer to the same question.
        foreach (var disposition in Enum.GetValues<NewsObservationEvidenceDisposition>())
        {
            Assert.Equal(
                observations.Count(o => join.Resolve(o.ObservationId).Disposition == disposition),
                counts.For(disposition));
        }

        // Every route is genuinely exercised by this fixture — a conservation test over an all-no-match
        // fixture would conserve perfectly and prove nothing.
        Assert.True(counts.ExactArticleInstant > 0);
        Assert.True(counts.ExactArticleUrl > 0);
        Assert.True(counts.UniqueHeadlineFallback > 0);
        Assert.True(counts.UnjoinedNoMatch > 0);
        Assert.True(counts.UnjoinedAmbiguous > 0);
    }

    /// <summary>One fixture exercising all five dispositions at once.</summary>
    private static (List<NewsObservationRecord> Observations, List<EvidenceItem> Evidence) MixedFixture()
    {
        var instantA = News(Id(20), "Alpha wins a supply contract", Url, Published);
        var urlOnly = News(Id(21), "Beta opens a plant", OtherUrl, null);
        var headlineOnly = News(Id(22), "Gamma raises guidance", "https://other.test/g", Published);
        var ambiguousOne = News(Id(23), "Delta restates results", "https://other.test/d", Published);
        var ambiguousTwo = News(Id(24), "Delta restates results", "https://other.test/d", Published);

        var observations = new List<NewsObservationRecord>
        {
            Observation(Id(400), CompanyA, "Alpha wins a supply contract", Url, Published),
            Observation(Id(401), CompanyA, "Beta opens a plant", OtherUrl, Published),
            Observation(Id(402), CompanyA, "Gamma raises guidance", "https://news.test/g", Published),
            Observation(Id(403), CompanyB, "Delta restates results", "https://other.test/d", Published),
            Observation(Id(404), CompanyB, "Epsilon files a lawsuit", "https://news.test/e", Published),
        };

        return (observations, [instantA, urlOnly, headlineOnly, ambiguousOne, ambiguousTwo]);
    }

    /// <summary>How many evidence records the tier-2 (URL + headline) key would have seen.</summary>
    private static int UrlAndTitleBucketSize(
        IReadOnlyList<EvidenceItem> evidence, string url, string headline) =>
        evidence.Count(e => string.Equals(e.SourceUrl, url, StringComparison.Ordinal)
            && string.Equals(
                NewsTextNormalization.Normalize(e.Title),
                NewsTextNormalization.Normalize(headline),
                StringComparison.Ordinal));

    /// <summary>How many evidence records the PRE-197 title-only rule would have seen for one headline.</summary>
    private static int TitleBucketSize(IReadOnlyList<EvidenceItem> evidence, string headline) =>
        evidence.Count(e => string.Equals(
            NewsTextNormalization.Normalize(e.Title),
            NewsTextNormalization.Normalize(headline),
            StringComparison.Ordinal));

    private static EvidenceItem News(
        Guid id, string title, string? sourceUrl, DateTimeOffset? publishedAtUtc) => new(
        Id: id,
        SourceType: EvidenceSourceType.NewsArticle,
        SourceName: "Example Wire",
        SourceUrl: sourceUrl,
        Title: title,
        Summary: null,
        RawText: title + " — body text.",
        ContentHash: "hash-" + id.ToString("N"),
        PublishedAtUtc: publishedAtUtc,
        CollectedAtUtc: Collected,
        Quality: EvidenceQuality.Medium,
        MetadataJson: null);

    private static NewsObservationRecord Observation(
        Guid observationId,
        Guid? companyId,
        string headline,
        string googleLandingUrl,
        DateTimeOffset? publishedAtUtc) => new(
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        ObservationId: observationId,
        CompanyId: companyId,
        Ticker: "TST",
        Collector: "newssearch",
        QueryPhrase: null,
        FeedId: null,
        FeedName: null,
        GoogleLandingUrl: googleLandingUrl,
        Publisher: "Example Wire",
        PublisherSiteUrl: null,
        Headline: headline,
        DescriptionRaw: null,
        DescriptionText: null,
        DescriptionTruncated: false,
        PublishedAtUtc: publishedAtUtc,
        RetrievedAtUtc: Collected,
        FirstObservedAtUtc: Collected,
        PayloadHash: observationId.ToString("N"),
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        ArticleFetch: null);
}
