using Radar.Application.News;

namespace Radar.Application.Tests.News;

public sealed class NewsObservationIdentityTests
{
    private const string Url = "https://news.google.com/rss/articles/AAA";
    private const string Headline = "Rocket Lab wins new launch contract - SpaceNews";
    private const string Publisher = "SpaceNews";
    private const string Description = "<a>Rocket Lab wins new launch contract</a>";

    [Fact]
    public void PayloadHash_And_ObservationId_AreDeterministic()
    {
        var hash1 = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher, Description);
        var hash2 = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher, Description);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 lower-case hex
        Assert.Equal(
            NewsObservationIdentity.ObservationIdFor(Url, hash1),
            NewsObservationIdentity.ObservationIdFor(Url, hash2));
    }

    [Fact]
    public void PayloadHash_ChangesWithAnyProviderField_AndWithCaptureMode()
    {
        var baseline = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher, Description);

        Assert.NotEqual(baseline, NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url + "x", Headline, Publisher, Description));
        Assert.NotEqual(baseline, NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url, Headline + "x", Publisher, Description));
        Assert.NotEqual(baseline, NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher + "x", Description));
        Assert.NotEqual(baseline, NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher, Description + "x"));
        // The three capture modes are distinguishable IDENTITIES, not just labels: a legacy headline-only
        // record and a prospective record of the same article never collide.
        Assert.NotEqual(baseline, NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.LegacyHeadlineOnly, Url, Headline, Publisher, Description));
    }

    [Fact]
    public void PayloadHash_NullDescription_IsDistinctFromEmptyDescription()
    {
        // The length-prefixed encoding makes null ("the feed supplied nothing") a different observation
        // from empty ("the feed supplied an empty element") — delimiter-joined encodings conflate them.
        Assert.NotEqual(
            NewsObservationIdentity.ComputePayloadHash(
                NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher, null),
            NewsObservationIdentity.ComputePayloadHash(
                NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher, string.Empty));
    }

    [Fact]
    public void PayloadHash_FieldContentCannotBleedAcrossFieldBoundaries()
    {
        // Length prefixes, not delimiters: moving a suffix from one field to the next must change the hash.
        Assert.NotEqual(
            NewsObservationIdentity.ComputePayloadHash(
                NewsObservationCaptureMode.ProspectiveRss, Url, "headAB", "pub", null),
            NewsObservationIdentity.ComputePayloadHash(
                NewsObservationCaptureMode.ProspectiveRss, Url, "head", "ABpub", null));
    }

    [Fact]
    public void RetrospectiveFetch_FoldsTheFetchedContentHash_SoUnchangedPagesDedupeAndChangedPagesDoNot()
    {
        var unchanged1 = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.RetrospectiveUrlFetch, Url, Headline, Publisher, null, "hashA");
        var unchanged2 = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.RetrospectiveUrlFetch, Url, Headline, Publisher, null, "hashA");
        var changed = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.RetrospectiveUrlFetch, Url, Headline, Publisher, null, "hashB");

        Assert.Equal(unchanged1, unchanged2);
        Assert.NotEqual(unchanged1, changed);
    }

    [Fact]
    public void ObservationId_NormalizesTheLandingUrlByTrimOnly()
    {
        var hash = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss, Url, Headline, Publisher, null);

        Assert.Equal(
            NewsObservationIdentity.ObservationIdFor(Url, hash),
            NewsObservationIdentity.ObservationIdFor("  " + Url + " ", hash));
        // Deliberately conservative: case is NOT folded (a cleverer canonicalisation risks merging
        // genuinely distinct articles).
        Assert.NotEqual(
            NewsObservationIdentity.ObservationIdFor(Url, hash),
            NewsObservationIdentity.ObservationIdFor(Url.ToUpperInvariant(), hash));
    }

    [Fact]
    public void PayloadEncodingVersion_IsThePinnedPersistedFormatConstant()
    {
        Assert.Equal("news-payload-v1", NewsObservationIdentity.PayloadEncodingVersion);
    }
}
