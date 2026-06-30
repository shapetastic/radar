namespace Radar.Application.Collectors;

/// <summary>
/// Pure, stateless merge of many per-collector <see cref="CollectionResult"/>s into one. Evidence is
/// concatenated in the order the results are supplied (the caller orders collectors deterministically),
/// and the <see cref="CollectionSummary"/> is aggregated across sources. The merger never re-sorts,
/// de-duplicates, or mutates evidence: each collector's intentional ordering and declared
/// <c>SourceType</c> are preserved, and cross-collector duplicates are resolved downstream by the
/// insert-only <c>ContentHash</c> dedupe in the repository (AD-1).
/// </summary>
public static class CollectionResultMerger
{
    /// <summary>
    /// Merges per-collector results into one. Evidence is concatenated in the order the results are
    /// supplied (the caller orders collectors deterministically); the summary sums the scalar counts and
    /// concatenates the per-source failures in the same order. An empty input yields an empty result
    /// (no evidence, <see cref="CollectionSummary.Empty"/>).
    /// </summary>
    public static CollectionResult Merge(IReadOnlyList<CollectionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            return new CollectionResult([], CollectionSummary.Empty);
        }

        var evidence = new List<CollectedEvidence>();
        var failures = new List<SourceFailure>();
        var sourcesChecked = 0;
        var sourcesSucceeded = 0;
        var sourcesFailed = 0;
        var itemsCollected = 0;

        foreach (var result in results)
        {
            evidence.AddRange(result.Evidence);

            var summary = result.Summary;
            sourcesChecked += summary.SourcesChecked;
            sourcesSucceeded += summary.SourcesSucceeded;
            sourcesFailed += summary.SourcesFailed;
            itemsCollected += summary.ItemsCollected;
            failures.AddRange(summary.Failures);
        }

        return new CollectionResult(
            evidence,
            new CollectionSummary(
                sourcesChecked,
                sourcesSucceeded,
                sourcesFailed,
                itemsCollected,
                failures));
    }
}
