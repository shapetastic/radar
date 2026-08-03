namespace Radar.Application.Efficacy.Statistics;

/// <summary>
/// One candidate date block with the EXACT endpoints of its outcome interval, supplied by the caller —
/// this helper is outcome-agnostic: a price outcome supplies its nominal <c>(d, d+h]</c> window, and a
/// non-price outcome (the AD-16 attention window, say) supplies its own endpoints.
/// <para>Interval semantics are open-left, closed-right: the outcome accrues over
/// <c>(IntervalStart, IntervalEnd]</c>, so two blocks overlap exactly when the later block's start falls
/// strictly before the earlier block's end.</para>
/// </summary>
public sealed record OutcomeWindowBlock(DateOnly Date, DateOnly IntervalStart, DateOnly IntervalEnd);

/// <summary>
/// The purge's full accounting: every candidate ends up in exactly one of the two lists, so a skipped date is
/// counted (<c>OverlappingOutcomeWindow</c>), never silently discarded.
/// </summary>
public sealed record OutcomeWindowPurgeResult(
    IReadOnlyList<OutcomeWindowBlock> Admitted,
    IReadOnlyList<OutcomeWindowBlock> Skipped);

/// <summary>
/// The deterministic overlap purge (spec 155): given candidates ascending by date, greedily admit the
/// EARLIEST candidate whose outcome interval does not overlap the last admitted interval.
/// <para>
/// <b>Earliest-first is the whole rule.</b> There is deliberately no search over weekday, phase or offset for
/// the subset that produces the most favourable result — that search would be a researcher degree of freedom
/// dressed as an implementation detail (AD-3). Changing the horizon changes the intervals the caller supplies
/// and therefore the purge distance, automatically.
/// </para>
/// <para>
/// The purge removes the KNOWN mechanical forward-window overlap. It does not make the admitted blocks
/// independent — macro regimes and company effects one horizon apart remain whatever they are — and no code
/// or rendered text may claim otherwise.
/// </para>
/// </summary>
public static class OutcomeWindowPurge
{
    public static OutcomeWindowPurgeResult Purge(IReadOnlyList<OutcomeWindowBlock> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var admitted = new List<OutcomeWindowBlock>();
        var skipped = new List<OutcomeWindowBlock>();

        OutcomeWindowBlock? lastAdmitted = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            ArgumentNullException.ThrowIfNull(candidate);

            if (candidate.IntervalEnd < candidate.IntervalStart)
            {
                throw new ArgumentException(
                    $"Candidate block {candidate.Date:yyyy-MM-dd} has an inverted outcome interval "
                        + $"({candidate.IntervalStart:yyyy-MM-dd} .. {candidate.IntervalEnd:yyyy-MM-dd}).",
                    nameof(candidates));
            }

            // Strictly ascending input is a precondition, not a preference: the greedy rule is only
            // deterministic and only "earliest first" over a sorted list, so an unsorted caller is a defect
            // to surface rather than silently re-sort around.
            if (i > 0 && candidate.Date <= candidates[i - 1].Date)
            {
                throw new ArgumentException(
                    "Candidate blocks must be strictly ascending by date, but "
                        + $"{candidate.Date:yyyy-MM-dd} follows {candidates[i - 1].Date:yyyy-MM-dd}.",
                    nameof(candidates));
            }

            // Open-left/closed-right non-overlap: the later interval may START exactly where the earlier one
            // ENDS, because the earlier outcome accrued through its end and the later begins strictly after
            // its own start.
            if (lastAdmitted is null || candidate.IntervalStart >= lastAdmitted.IntervalEnd)
            {
                admitted.Add(candidate);
                lastAdmitted = candidate;
            }
            else
            {
                skipped.Add(candidate);
            }
        }

        return new OutcomeWindowPurgeResult(admitted, skipped);
    }
}
