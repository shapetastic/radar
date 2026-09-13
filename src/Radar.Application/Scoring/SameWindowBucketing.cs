namespace Radar.Application.Scoring;

/// <summary>
/// THE one definition of greedy same-window bucketing (spec 224 extracted it from
/// <see cref="MediaAttentionCollapse"/> so <see cref="InsiderActivityCollapse"/> could share it rather than
/// carry a second copy — CLAUDE.md reuse-over-copy). Given a list ALREADY SORTED ascending by
/// <c>(ObservedAtUtc, Id)</c> (see <see cref="CompareObservedThenId"/>), <see cref="GreedyWindows"/> yields
/// consecutive half-open index ranges <c>[Start, End)</c>: each bucket opens at the first unbucketed item and
/// absorbs every subsequent item observed within <paramref name="window"/> of the bucket's EARLIEST member;
/// the first item outside opens the next bucket.
/// <para>
/// <b>The boundary is measured from the bucket's FIRST member, never from whichever member a caller later
/// chooses to represent it, and that separation is load-bearing</b> — <c>MediaAttentionCollapse</c>'s
/// header records why: measuring from a later representative would let the representative choice widen or
/// shrink the bucket, so the collapsed counts would silently depend on which member won. Keeping the loop
/// here, with no knowledge of representatives, makes that impossible by construction for every caller.
/// </para>
/// <para>
/// Deterministic (AD-3): pure over its inputs, no clock, IO or randomness. Byte-identical to the inline
/// loop <c>media-collapse-v1</c>/<c>v2</c> carried before the extraction — the media collapse's pinned
/// tests were left unmodified as the proof.
/// </para>
/// </summary>
public static class SameWindowBucketing
{
    /// <summary>
    /// Yields the greedy same-window buckets of <paramref name="sorted"/> as <c>[Start, End)</c> index
    /// ranges. <paramref name="sorted"/> MUST be ordered ascending by <c>Signal.ObservedAtUtc</c> (the
    /// caller's responsibility — an unsorted input would produce buckets that depend on caller order, which
    /// AD-3 forbids). An empty list yields nothing.
    /// </summary>
    public static IEnumerable<(int Start, int End)> GreedyWindows(
        IReadOnlyList<ScoringSignal> sorted, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(sorted);

        var i = 0;
        while (i < sorted.Count)
        {
            var bucketFirst = sorted[i];

            // Greedy: each subsequent signal within `window` of the bucket's FIRST/earliest signal joins
            // this bucket; the first one outside opens the next bucket.
            var j = i + 1;
            while (j < sorted.Count
                && sorted[j].Signal.ObservedAtUtc - bucketFirst.Signal.ObservedAtUtc <= window)
            {
                j++;
            }

            yield return (i, j);
            i = j;
        }
    }

    /// <summary>
    /// The shared deterministic ordering both collapses sort by before bucketing (and re-sort their output
    /// by): <c>ObservedAtUtc</c> ascending, then <c>Id</c> as the tiebreak — so bucketing never depends on
    /// caller order (AD-3).
    /// </summary>
    public static int CompareObservedThenId(ScoringSignal a, ScoringSignal b)
    {
        var byObserved = a.Signal.ObservedAtUtc.CompareTo(b.Signal.ObservedAtUtc);
        return byObserved != 0 ? byObserved : a.Signal.Id.CompareTo(b.Signal.Id);
    }
}
