using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Deterministic (AD-3: no clock, config, state, IO, or randomness) read/assembly-time supersede of the
/// deterministic Neutral <see cref="SignalType.GuidanceChange"/> by a directional one over the SAME filing
/// evidence (spec 113). A filing first collected while the directional earnings read failed (e.g. the
/// www.sec.gov self-block era) has its spec-57 Neutral GuidanceChange already persisted; the signal stores
/// are append-only (AD-8), so instead of deleting the stale Neutral we exclude it when the assembled signal
/// set also carries a directional GuidanceChange for that filing. This extends the spec-78
/// suppress-before-store supersede (which only covers evidence newly stored in the current run) to cover
/// already-persisted signals — same supersede key, applied at scoring-assembly time.
///
/// <para>
/// Rule: among <see cref="SignalType.GuidanceChange"/> signals sharing one <c>EvidenceId</c>, at most ONE
/// survives — a directional one (<see cref="Signal.Direction"/> != <see cref="SignalDirection.Neutral"/>;
/// Mixed counts as directional, matching the spec-78 supersede where ANY directional read replaces the
/// deterministic Neutral) when present, else the Neutral. If multiple candidates remain on the winning side
/// (contradictory Positive+Negative, or duplicate copies), the tie-break is the stable signal order:
/// earliest <c>ObservedAtUtc</c>, then lowest <c>Id</c> — a total order independent of input order, so the
/// survivor is identical on every assembly (AD-3). Non-GuidanceChange signals pass through untouched,
/// nothing collapses across different <c>EvidenceId</c>s, and survivors keep the input's relative ordering.
/// </para>
/// <para>
/// Deliberately NOT a fingerprint input: this is a pipeline-correctness fix (which already-available signal
/// is scored), not a scoring-config change — there is no <c>CanonicalDescriptor()</c> and the default
/// <c>ScoringConfigVersion</c> fingerprint must not move.
/// </para>
/// </summary>
public static class GuidanceChangeSupersede
{
    /// <summary>
    /// Applies the supersede to the current-window signal+evidence pairs scored by the engine. Returns the
    /// input instance unchanged when no supersede can apply (zero or one GuidanceChange present).
    /// </summary>
    public static IReadOnlyList<ScoringSignal> Apply(IReadOnlyList<ScoringSignal> signals) =>
        ApplyCore(signals, static s => s.Signal);

    /// <summary>
    /// Applies the supersede to a plain signal list — the activity-only previous window used for velocity
    /// (no double-count, ever: a filing whose stale Neutral and directional copy both persist on disk must
    /// not count twice as activity). On the healthy spec-78 path only one GuidanceChange per filing ever
    /// persists, so this is behaviour-identical there.
    /// </summary>
    public static IReadOnlyList<Signal> Apply(IReadOnlyList<Signal> signals) =>
        ApplyCore(signals, static s => s);

    private static IReadOnlyList<T> ApplyCore<T>(IReadOnlyList<T> items, Func<T, Signal> signalOf)
    {
        ArgumentNullException.ThrowIfNull(items);

        // One winner per EvidenceId among the GuidanceChange signals. Winner selection is
        // order-independent (Beats is a strict comparison), so the survivor never depends on input order.
        var winners = new Dictionary<Guid, Signal>();
        var guidanceCount = 0;
        foreach (var item in items)
        {
            var signal = signalOf(item);
            if (signal.Type != SignalType.GuidanceChange)
            {
                continue;
            }

            guidanceCount++;
            if (!winners.TryGetValue(signal.EvidenceId, out var incumbent) || Beats(signal, incumbent))
            {
                winners[signal.EvidenceId] = signal;
            }
        }

        // Fast path: zero or one GuidanceChange in the whole set can never conflict — return the input
        // instance so the untouched (healthy spec-78) path is allocation-free and byte-identical.
        if (guidanceCount <= 1)
        {
            return items;
        }

        // Filter, preserving the input's relative ordering of survivors. The emitted-set guard keeps AT
        // MOST one GuidanceChange per EvidenceId even if the winner appears twice (exact duplicate copies).
        var result = new List<T>(items.Count);
        var emittedEvidenceIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            var signal = signalOf(item);
            if (signal.Type != SignalType.GuidanceChange)
            {
                result.Add(item);
                continue;
            }

            if (signal.Id == winners[signal.EvidenceId].Id && emittedEvidenceIds.Add(signal.EvidenceId))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> supersedes <paramref name="incumbent"/> for the same
    /// EvidenceId: directional beats Neutral; within the same side the stable order (earliest
    /// ObservedAtUtc, then lowest Id) wins. Strict — never true for the same signal.
    /// </summary>
    private static bool Beats(Signal candidate, Signal incumbent)
    {
        var candidateDirectional = candidate.Direction != SignalDirection.Neutral;
        var incumbentDirectional = incumbent.Direction != SignalDirection.Neutral;
        if (candidateDirectional != incumbentDirectional)
        {
            return candidateDirectional;
        }

        var byObserved = candidate.ObservedAtUtc.CompareTo(incumbent.ObservedAtUtc);
        return byObserved != 0 ? byObserved < 0 : candidate.Id.CompareTo(incumbent.Id) < 0;
    }
}
