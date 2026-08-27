using Radar.Application.News;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.News;

/// <summary>
/// SPEC 194 §1.2 — the REVERSE (observation → evidence) direction of the join, and the proof that adding it
/// left the forward direction, the lowest-id representative rule and the observation counts byte-unchanged.
/// <para>
/// The reverse lookup is load-bearing for the judgment-signal materializer: it starts from the fact ids the
/// judge CITED, which resolve to whichever observation the typing pass happened to type. If only the
/// representative (lowest-id) observation resolved, a perfectly good citation would fail provenance for a
/// reason invented by a tie-break rule rather than by any gap in the evidence chain.
/// </para>
/// </summary>
public sealed class NewsObservationEvidenceJoinReverseLookupTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryMatchByObservation_ResolvesEveryObservationOnAJoinedKey_NotOnlyTheRepresentative()
    {
        var companyId = Guid.NewGuid();
        var evidence = NewsEvidence("Acme widens quarterly loss");

        // Two captures of the SAME article — expected and benign (two feeds, or two capture modes). The
        // forward match reports only the lowest id; both must resolve in reverse.
        var low = Observation(new Guid("11111111-1111-1111-1111-111111111111"), companyId, evidence.Title);
        var high = Observation(new Guid("99999999-9999-9999-9999-999999999999"), companyId, evidence.Title);

        var join = NewsObservationEvidenceJoin.Build([high, low], [evidence]);

        var byLow = join.TryMatchByObservation(low.ObservationId);
        var byHigh = join.TryMatchByObservation(high.ObservationId);

        Assert.NotNull(byLow);
        Assert.NotNull(byHigh);
        Assert.Equal(evidence.Id, byLow!.EvidenceId);
        Assert.Equal(evidence.Id, byHigh!.EvidenceId);
        Assert.Equal(companyId, byHigh.CompanyId);

        // Both hand back the SAME match instance, so the representative rule keeps exactly one definition:
        // the reported ObservationId is the key's lowest, never the id that was asked about.
        Assert.Same(byLow, byHigh);
        Assert.Equal(low.ObservationId, byHigh.ObservationId);
    }

    [Fact]
    public void TryMatchByObservation_IsNullForEveryUnjoinedObservation()
    {
        var companyId = Guid.NewGuid();
        var evidence = NewsEvidence("Acme widens quarterly loss");

        var noMatch = Observation(Guid.NewGuid(), companyId, "A headline no evidence carries");
        var nullCompany = Observation(Guid.NewGuid(), companyId: null, headline: evidence.Title);
        var blank = Observation(Guid.NewGuid(), companyId, headline: "   ");

        var join = NewsObservationEvidenceJoin.Build([noMatch, nullCompany, blank], [evidence]);

        Assert.Null(join.TryMatchByObservation(noMatch.ObservationId));
        Assert.Null(join.TryMatchByObservation(nullCompany.ObservationId));
        Assert.Null(join.TryMatchByObservation(blank.ObservationId));
        Assert.Null(join.TryMatchByObservation(Guid.NewGuid()));
    }

    [Fact]
    public void TryMatchByObservation_IsNullWhenTheKeyIsAmbiguous()
    {
        // Two companies claiming one normalized headline: ambiguous for BOTH (the fail-closed rule that
        // makes "a same-headline article belonging to a DIFFERENT company never joins" true rather than
        // merely likely). The reverse lookup must inherit that, not soften it.
        var evidence = NewsEvidence("Quarterly results announced");
        var first = Observation(Guid.NewGuid(), Guid.NewGuid(), evidence.Title);
        var second = Observation(Guid.NewGuid(), Guid.NewGuid(), evidence.Title);

        var join = NewsObservationEvidenceJoin.Build([first, second], [evidence]);

        Assert.Null(join.TryMatchByObservation(first.ObservationId));
        Assert.Null(join.TryMatchByObservation(second.ObservationId));
        Assert.Equal(2, join.Counts.UnjoinedAmbiguous);
    }

    [Fact]
    public void ForwardApiAndCounts_AreUnchangedByTheReverseIndex()
    {
        // The pin for "the existing forward TryMatch semantics, the counts and the lowest-id representative
        // rule must be byte-unchanged": one mixed fixture exercising joined (with duplicate captures),
        // no-match, null-company and ambiguous at once.
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var joinedEvidence = NewsEvidence("Acme widens quarterly loss");
        var ambiguousEvidence = NewsEvidence("Quarterly results announced");
        var unmatchedEvidence = NewsEvidence("Nobody observed this one");

        var low = Observation(new Guid("22222222-2222-2222-2222-222222222222"), companyA, joinedEvidence.Title);
        var high = Observation(new Guid("88888888-8888-8888-8888-888888888888"), companyA, joinedEvidence.Title);
        var ambiguousA = Observation(Guid.NewGuid(), companyA, ambiguousEvidence.Title);
        var ambiguousB = Observation(Guid.NewGuid(), companyB, ambiguousEvidence.Title);
        var noMatch = Observation(Guid.NewGuid(), companyB, "An unindexed headline");
        var nullCompany = Observation(Guid.NewGuid(), null, joinedEvidence.Title);

        var join = NewsObservationEvidenceJoin.Build(
            [high, low, ambiguousA, ambiguousB, noMatch, nullCompany],
            [joinedEvidence, ambiguousEvidence, unmatchedEvidence]);

        // Forward: only the joined evidence resolves, and it reports the LOWEST observation id.
        var forward = join.TryMatch(joinedEvidence.Id);
        Assert.NotNull(forward);
        Assert.Equal(low.ObservationId, forward!.ObservationId);
        Assert.Equal(companyA, forward.CompanyId);
        Assert.Null(join.TryMatch(ambiguousEvidence.Id));
        Assert.Null(join.TryMatch(unmatchedEvidence.Id));

        // Counts partition the OBSERVATIONS exactly as before: 2 joined, 2 ambiguous, 2 no-match
        // (the unmatched headline and the null-company capture).
        Assert.Equal(2, join.Counts.Joined);
        Assert.Equal(2, join.Counts.UnjoinedAmbiguous);
        Assert.Equal(2, join.Counts.UnjoinedNoMatch);
        Assert.Equal(
            6,
            join.Counts.Joined + join.Counts.UnjoinedAmbiguous + join.Counts.UnjoinedNoMatch);
    }

    private static EvidenceItem NewsEvidence(string title) => new(
        Id: Guid.NewGuid(),
        SourceType: EvidenceSourceType.NewsArticle,
        SourceName: "Example Wire",
        SourceUrl: "https://example.test/a",
        Title: title,
        Summary: null,
        RawText: title + " — body text.",
        ContentHash: Guid.NewGuid().ToString("N"),
        PublishedAtUtc: At,
        CollectedAtUtc: At,
        Quality: EvidenceQuality.Medium,
        MetadataJson: null);

    private static NewsObservationRecord Observation(
        Guid observationId, Guid? companyId, string headline) => new(
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        ObservationId: observationId,
        CompanyId: companyId,
        Ticker: "ACM",
        Collector: "newssearch",
        QueryPhrase: null,
        FeedId: null,
        FeedName: null,
        GoogleLandingUrl: "https://news.example.test/" + observationId.ToString("N"),
        Publisher: "Example Wire",
        PublisherSiteUrl: null,
        Headline: headline,
        DescriptionRaw: null,
        DescriptionText: null,
        DescriptionTruncated: false,
        PublishedAtUtc: At,
        RetrievedAtUtc: At,
        FirstObservedAtUtc: At,
        PayloadHash: observationId.ToString("N"),
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        ArticleFetch: null);
}
