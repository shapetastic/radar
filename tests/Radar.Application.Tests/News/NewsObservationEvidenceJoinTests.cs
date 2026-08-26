using Radar.Application.News;
using Radar.Application.Tests.NewsRisk;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.News;

/// <summary>
/// Spec 191 §1 — the derived-on-read observation ↔ evidence join, and its fail-closed rules. Nothing here
/// is persisted; the counts are the per-run measurement the spec requires.
/// </summary>
public sealed class NewsObservationEvidenceJoinTests
{
    private static readonly Guid CompanyA = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = new("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset Observed = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private static EvidenceItem News(Guid id, string title, EvidenceSourceType type = EvidenceSourceType.NewsArticle) =>
        new(
            Id: id,
            SourceType: type,
            SourceName: "Example Wire",
            SourceUrl: "https://example.com/a",
            Title: title,
            Summary: null,
            RawText: title + " — body",
            ContentHash: "hash-" + id.ToString("N"),
            PublishedAtUtc: Observed,
            CollectedAtUtc: Observed,
            Quality: EvidenceQuality.Medium,
            MetadataJson: null);

    private static Guid Id(int n) => new($"cccccccc-0000-0000-0000-{n:D12}");

    [Fact]
    public void ExactSingleMatch_Joins_AndReportsTheObservationId()
    {
        var evidenceId = Id(1);
        var observationId = Id(101);
        var join = NewsObservationEvidenceJoin.Build(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed, observationId: observationId)],
            [News(evidenceId, "Acme wins order")]);

        var match = join.TryMatch(evidenceId);

        Assert.NotNull(match);
        Assert.Equal(evidenceId, match.EvidenceId);
        Assert.Equal(CompanyA, match.CompanyId);
        Assert.Equal(observationId, match.ObservationId);
        Assert.Equal(new NewsObservationEvidenceJoinCounts(1, 0, 0), join.Counts);
    }

    [Fact]
    public void HeadlineAndTitle_AreNormalizedBeforeMatching()
    {
        // Punctuation/case differences must not defeat the join — it is the SHARED fact-layer normalization.
        var evidenceId = Id(2);
        var join = NewsObservationEvidenceJoin.Build(
            [NewsRiskTestData.Observation(CompanyA, "Acme Corp. WINS a $5m order!", Observed)],
            [News(evidenceId, "acme corp wins a 5m order")]);

        Assert.NotNull(join.TryMatch(evidenceId));
    }

    [Fact]
    public void ZeroMatches_StayUnjoined_AsNoMatch()
    {
        var join = NewsObservationEvidenceJoin.Build(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(Id(3), "A completely different headline")]);

        Assert.Null(join.TryMatch(Id(3)));
        Assert.Equal(new NewsObservationEvidenceJoinCounts(0, 1, 0), join.Counts);
    }

    [Fact]
    public void TwoOrMoreMatchingEvidenceItems_AreAmbiguous_AndNeverGuessed()
    {
        var join = NewsObservationEvidenceJoin.Build(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(Id(4), "Acme wins order"), News(Id(5), "Acme wins order")]);

        Assert.Null(join.TryMatch(Id(4)));
        Assert.Null(join.TryMatch(Id(5)));
        Assert.Equal(new NewsObservationEvidenceJoinCounts(0, 0, 1), join.Counts);
    }

    [Fact]
    public void SameHeadlineClaimedByTwoCompanies_NeverJoins()
    {
        // The spec's "a candidate matches only within the same company" rule, made fail-closed: an
        // ambiguous company would attach one company's direction to the other's evidence.
        var evidenceId = Id(6);
        var join = NewsObservationEvidenceJoin.Build(
            [
                NewsRiskTestData.Observation(CompanyA, "Sector index rises", Observed),
                NewsRiskTestData.Observation(CompanyB, "Sector index rises", Observed),
            ],
            [News(evidenceId, "Sector index rises")]);

        Assert.Null(join.TryMatch(evidenceId));
        Assert.Equal(new NewsObservationEvidenceJoinCounts(0, 0, 2), join.Counts);
    }

    [Fact]
    public void ADifferentCompanysSameHeadlineArticle_DoesNotBleedIntoAJoinedCompany()
    {
        // Company A's article joins ONLY when company B does not also claim that headline. Here B claims a
        // DIFFERENT headline, so A's join survives and B's stays unjoined — no cross-company leakage.
        var aEvidence = Id(7);
        var bEvidence = Id(8);
        var join = NewsObservationEvidenceJoin.Build(
            [
                NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed),
                NewsRiskTestData.Observation(CompanyB, "Beta wins order", Observed),
            ],
            [News(aEvidence, "Acme wins order")]);

        Assert.Equal(CompanyA, join.TryMatch(aEvidence)?.CompanyId);
        Assert.Null(join.TryMatch(bEvidence));
        Assert.Equal(new NewsObservationEvidenceJoinCounts(1, 1, 0), join.Counts);
    }

    [Fact]
    public void NullCompanyObservation_NeverJoins_AndCountsAsNoMatch()
    {
        var evidenceId = Id(9);
        var observation = NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)
            with { CompanyId = null };

        var join = NewsObservationEvidenceJoin.Build([observation], [News(evidenceId, "Acme wins order")]);

        Assert.Null(join.TryMatch(evidenceId));
        Assert.Equal(new NewsObservationEvidenceJoinCounts(0, 1, 0), join.Counts);
    }

    [Fact]
    public void BlankNormalizedKey_NeverJoins()
    {
        var evidenceId = Id(10);
        var join = NewsObservationEvidenceJoin.Build(
            [NewsRiskTestData.Observation(CompanyA, "—  ---", Observed)],
            [News(evidenceId, "***")]);

        Assert.Null(join.TryMatch(evidenceId));
        Assert.Equal(new NewsObservationEvidenceJoinCounts(0, 1, 0), join.Counts);
    }

    [Fact]
    public void SeveralObservationsOfOneArticle_Join_AndReportTheLowestObservationId()
    {
        // Expected and benign: the same article captured by two feeds/capture modes. The reported
        // observation id is the LOWEST — deterministic, never enumeration-order dependent (AD-3).
        var evidenceId = Id(11);
        var low = Id(200);
        var high = Id(300);

        var forward = NewsObservationEvidenceJoin.Build(
            [
                NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed, observationId: high),
                NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed, observationId: low),
            ],
            [News(evidenceId, "Acme wins order")]);
        var reversed = NewsObservationEvidenceJoin.Build(
            [
                NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed, observationId: low),
                NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed, observationId: high),
            ],
            [News(evidenceId, "Acme wins order")]);

        Assert.Equal(low, forward.TryMatch(evidenceId)?.ObservationId);
        Assert.Equal(low, reversed.TryMatch(evidenceId)?.ObservationId);
        // BOTH observations count as joined: the buckets partition OBSERVATIONS.
        Assert.Equal(new NewsObservationEvidenceJoinCounts(2, 0, 0), forward.Counts);
    }

    [Fact]
    public void Counts_PartitionEveryObservationExactlyOnce()
    {
        var join = NewsObservationEvidenceJoin.Build(
            [
                NewsRiskTestData.Observation(CompanyA, "Joined headline", Observed),
                NewsRiskTestData.Observation(CompanyA, "Missing headline", Observed),
                NewsRiskTestData.Observation(CompanyA, "Ambiguous headline", Observed),
                NewsRiskTestData.Observation(CompanyB, "Ambiguous headline", Observed),
            ],
            [News(Id(12), "Joined headline"), News(Id(13), "Ambiguous headline")]);

        Assert.Equal(new NewsObservationEvidenceJoinCounts(1, 1, 2), join.Counts);
        Assert.Equal(
            4,
            join.Counts.Joined + join.Counts.UnjoinedNoMatch + join.Counts.UnjoinedAmbiguous);
    }

    [Fact]
    public void EmptyInputs_ProduceZeroCounts_AndNoMatches()
    {
        var join = NewsObservationEvidenceJoin.Build([], []);

        Assert.Equal(new NewsObservationEvidenceJoinCounts(0, 0, 0), join.Counts);
        Assert.Null(join.TryMatch(Id(14)));
    }
}
