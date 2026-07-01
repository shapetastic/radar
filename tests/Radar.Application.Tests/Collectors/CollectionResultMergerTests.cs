using Radar.Application.Collectors;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.Collectors;

public sealed class CollectionResultMergerTests
{
    private static CollectedEvidence Evidence(string title, string rawText) =>
        new(
            SourceType: EvidenceSourceType.LocalFile,
            SourceName: "Test Source",
            SourceUrl: "https://example.com/" + title,
            Title: title,
            RawText: rawText,
            PublishedAt: new DateTimeOffset(2026, 2, 6, 0, 0, 0, TimeSpan.Zero),
            CollectedAt: new DateTimeOffset(2026, 2, 7, 0, 0, 0, TimeSpan.Zero),
            Metadata: new Dictionary<string, string>());

    [Fact]
    public void Merge_TwoResults_ConcatenatesEvidenceAndAggregatesSummaryInOrder()
    {
        var a1 = Evidence("A1", "alpha one");
        var a2 = Evidence("A2", "alpha two");
        var b1 = Evidence("B1", "bravo one");

        var aFailure = new SourceFailure("Feed A", "https://a.test", "HTTP 500");
        var bFailure = new SourceFailure("Feed B", "https://b.test", "timeout");

        var a = new CollectionResult([a1, a2], new CollectionSummary(2, 1, 1, 2, [aFailure]));
        var b = new CollectionResult([b1], new CollectionSummary(1, 1, 0, 1, [bFailure]));

        var merged = CollectionResultMerger.Merge([a, b]);

        // Evidence is A's items then B's items, in order, with each result's own order preserved.
        Assert.Equal([a1, a2, b1], merged.Evidence);

        // Summary counts are element-wise sums.
        Assert.Equal(3, merged.Summary.SourcesChecked);
        Assert.Equal(2, merged.Summary.SourcesSucceeded);
        Assert.Equal(1, merged.Summary.SourcesFailed);
        Assert.Equal(3, merged.Summary.ItemsCollected);

        // Failures are A's then B's, in order.
        Assert.Equal([aFailure, bFailure], merged.Summary.Failures);
    }

    [Fact]
    public void Merge_SingleResult_IsIdentity()
    {
        var e1 = Evidence("E1", "echo one");
        var failure = new SourceFailure("Feed", "https://feed.test", "parse error");
        var r = new CollectionResult([e1], new CollectionSummary(1, 0, 1, 0, [failure]));

        var merged = CollectionResultMerger.Merge([r]);

        Assert.Equal(r.Evidence, merged.Evidence);
        Assert.Equal(1, merged.Summary.SourcesChecked);
        Assert.Equal(0, merged.Summary.SourcesSucceeded);
        Assert.Equal(1, merged.Summary.SourcesFailed);
        Assert.Equal(0, merged.Summary.ItemsCollected);
        Assert.Equal(r.Summary.Failures, merged.Summary.Failures);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmptyResult()
    {
        var merged = CollectionResultMerger.Merge([]);

        Assert.Empty(merged.Evidence);
        Assert.Equal(0, merged.Summary.SourcesChecked);
        Assert.Equal(0, merged.Summary.SourcesSucceeded);
        Assert.Equal(0, merged.Summary.SourcesFailed);
        Assert.Equal(0, merged.Summary.ItemsCollected);
        Assert.Empty(merged.Summary.Failures);
    }

    [Fact]
    public void Merge_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CollectionResultMerger.Merge(null!));
    }

    [Fact]
    public void Merge_DoesNotDeDupOrReSort_CollidingEvidenceBothPresentInOrder()
    {
        // Two results whose evidence would collide on a downstream content hash (identical title +
        // rawText). The merger must keep BOTH, in input order, and must not re-sort or de-dupe.
        var aFirst = Evidence("Same Title", "same body");
        var aSecond = Evidence("A second", "a second body");
        var bDuplicate = Evidence("Same Title", "same body");

        var a = new CollectionResult([aFirst, aSecond], CollectionSummary.Empty);
        var b = new CollectionResult([bDuplicate], CollectionSummary.Empty);

        var merged = CollectionResultMerger.Merge([a, b]);

        // Count is the sum (no de-dup), and ordering within each result is preserved.
        Assert.Equal(3, merged.Evidence.Count);
        Assert.Equal([aFirst, aSecond, bDuplicate], merged.Evidence);
    }
}
