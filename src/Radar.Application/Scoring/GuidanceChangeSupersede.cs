using System.Collections.Frozen;

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
/// <para>
/// SPEC 193 §2: this was the ONLY signal-removal step in <c>ScoringEngine.ScoreCompanyAsync</c> with no
/// trace — the dropped-evidence path above it aggregates a per-company Warning, and
/// <see cref="MediaAttentionCollapse"/> below it surfaces its collapsed count on the survivor's contribution
/// reason. It now RETURNS what it removed, in the same shape the media collapse uses (survivors + a count
/// keyed by the surviving signal's id). <b>Accounting only: which signals are removed is untouched</b>, and
/// pinned by <c>GuidanceChangeSupersedeAccountingTests</c>.
/// </para>
/// </summary>
public static class GuidanceChangeSupersede
{
    /// <summary>
    /// Applies the supersede to the current-window signal+evidence pairs scored by the engine. Returns the
    /// input instance unchanged (and an empty count map) when no supersede can apply (zero or one
    /// GuidanceChange present).
    /// </summary>
    public static GuidanceChangeSupersedeResult<ScoringSignal> Apply(IReadOnlyList<ScoringSignal> signals) =>
        ApplyCore(signals, static s => s.Signal);

    /// <summary>
    /// Applies the supersede to a plain signal list — the activity-only previous window used for velocity
    /// (no double-count, ever: a filing whose stale Neutral and directional copy both persist on disk must
    /// not count twice as activity). On the healthy spec-78 path only one GuidanceChange per filing ever
    /// persists, so this is behaviour-identical there.
    /// </summary>
    public static GuidanceChangeSupersedeResult<Signal> Apply(IReadOnlyList<Signal> signals) =>
        ApplyCore(signals, static s => s);

    private static GuidanceChangeSupersedeResult<T> ApplyCore<T>(
        IReadOnlyList<T> items, Func<T, Signal> signalOf)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Fast path: zero or one GuidanceChange in the whole set can never conflict — return the input
        // instance so the untouched (healthy spec-78) path is allocation-free and byte-identical. Counted
        // with an indexed loop (an interface foreach would box the enumerator) and no winner map yet.
        var guidanceCount = 0;
        for (var i = 0; i < items.Count && guidanceCount <= 1; i++)
        {
            if (signalOf(items[i]).Type == SignalType.GuidanceChange)
            {
                guidanceCount++;
            }
        }

        if (guidanceCount <= 1)
        {
            return GuidanceChangeSupersedeResult<T>.Untouched(items);
        }

        // One winner per EvidenceId among the GuidanceChange signals. Winner selection is
        // order-independent (Beats is a strict comparison), so the survivor never depends on input order.
        var winners = new Dictionary<Guid, Signal>();
        foreach (var item in items)
        {
            var signal = signalOf(item);
            if (signal.Type != SignalType.GuidanceChange)
            {
                continue;
            }

            if (!winners.TryGetValue(signal.EvidenceId, out var incumbent) || Beats(signal, incumbent))
            {
                winners[signal.EvidenceId] = signal;
            }
        }

        // Filter, preserving the input's relative ordering of survivors. The emitted-set guard keeps AT
        // MOST one GuidanceChange per EvidenceId even if the winner appears twice (exact duplicate copies).
        //
        // SPEC 193 §2 (accounting only — every branch condition below is byte-for-byte the pre-193 one):
        // every GuidanceChange that is NOT emitted is charged to the SURVIVOR of its own EvidenceId. That
        // attribution is deliberate, and it is the only defensible one available here: a supersede is a
        // per-EVIDENCE removal, so unlike a media bucket there is no "representative that absorbed N nearby
        // items" — but there IS exactly one signal that took the removed signal's place for that filing, and
        // keeping the trace on it is what lets a reader of the persisted ScoreEvidenceLink see that this
        // contribution REPLACED something rather than that something simply vanished. It is deterministic
        // (AD-3): the survivor is chosen order-independently by Beats, and the count is a pure tally. A
        // superseded signal contributes no link of its own, before or after this slice.
        var result = new List<T>(items.Count);
        var emittedEvidenceIds = new HashSet<Guid>();
        Dictionary<Guid, int>? supersededBySurvivorId = null;
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
                continue;
            }

            // Removed: a losing candidate, or a duplicate copy of the winner beyond the first. Charged to
            // the winner for this EvidenceId, which is emitted the first time it is seen whatever the input
            // order, so the key is always a surviving signal's id.
            var survivorId = winners[signal.EvidenceId].Id;
            supersededBySurvivorId ??= [];
            supersededBySurvivorId[survivorId] =
                supersededBySurvivorId.GetValueOrDefault(survivorId) + 1;
        }

        return new GuidanceChangeSupersedeResult<T>(
            result,
            supersededBySurvivorId is null
                ? GuidanceChangeSupersedeResult<T>.NoCounts
                : supersededBySurvivorId);
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

/// <summary>
/// The result of a <see cref="GuidanceChangeSupersede.Apply(IReadOnlyList{ScoringSignal})"/>: the surviving
/// signals and, per SURVIVING <c>Signal.Id</c>, how many stale <see cref="SignalType.GuidanceChange"/>
/// signals over the same filing evidence it superseded (only entries with a positive count are present).
/// <para>
/// The shape deliberately mirrors <see cref="MediaCollapseResult"/> (survivors + a count keyed by the
/// surviving signal's id) rather than inventing a second one, so the two accounting steps either side of it
/// in <c>ScoringEngine</c> read the same way. It is GENERIC where <see cref="MediaCollapseResult"/> is not,
/// because the supersede runs over BOTH the current window's <c>ScoringSignal</c> pairs and the previous
/// window's plain <see cref="Signal"/> list — one definition serving both beats two near-identical copies.
/// </para>
/// </summary>
public sealed record GuidanceChangeSupersedeResult<T>(
    IReadOnlyList<T> Signals,
    IReadOnlyDictionary<Guid, int> SupersededCounts)
{
    /// <summary>
    /// The shared empty map, so the untouched fast path allocates no dictionary. It is a
    /// <see cref="FrozenDictionary{TKey,TValue}"/> rather than a bare <see cref="Dictionary{TKey,TValue}"/>
    /// precisely BECAUSE it is shared: a consumer that cast the interface back to the concrete type could
    /// otherwise mutate every past and future fast-path result at once.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Guid, int> NoCounts = FrozenDictionary<Guid, int>.Empty;

    /// <summary>
    /// The fast path: no supersede could apply, so the INPUT INSTANCE is handed back unchanged (no list
    /// allocation, exactly the pre-193 return) alongside an empty count map.
    /// </summary>
    internal static GuidanceChangeSupersedeResult<T> Untouched(IReadOnlyList<T> signals) =>
        new(signals, NoCounts);

    /// <summary>
    /// How many signals the supersede removed in total — the sum over every survivor. Drives the
    /// per-company scoring log; the per-survivor breakdown drives the contribution reasons.
    /// </summary>
    public int TotalSuperseded
    {
        get
        {
            var total = 0;
            foreach (var count in SupersededCounts.Values)
            {
                total += count;
            }

            return total;
        }
    }
}
