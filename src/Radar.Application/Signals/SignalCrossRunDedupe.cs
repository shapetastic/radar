using Radar.Domain.Signals;

namespace Radar.Application.Signals;

/// <summary>
/// The single definition of a signal's <b>stable cross-run identity</b> and of how duplicate persisted
/// copies of one signal collapse on a read.
///
/// <para>
/// Radar re-mints a signal with a fresh <see cref="Signal.Id"/> (and <see cref="Signal.CreatedAtUtc"/>)
/// every time the same underlying evidence is re-extracted, and the durable signal store path-keys on that
/// id — so N runs leave N files for ONE signal. Any read that serves accrued history must collapse those
/// copies or it silently inflates whatever it feeds (previous-window activity for velocity, spec 85; the
/// current scoring window and therefore the score itself, spec 142). Both reads share the key defined here
/// so the two can never drift apart.
/// </para>
/// </summary>
public static class SignalCrossRunDedupe
{
    /// <summary>
    /// The stable identity of a signal across runs: <c>(CompanyId, EvidenceId, Type, Direction)</c>.
    /// <list type="bullet">
    /// <item><see cref="Signal.EvidenceId"/> + <see cref="Signal.Type"/> + <see cref="Signal.Direction"/>
    /// distinguishes the genuinely-DISTINCT signals ONE evidence item can legitimately produce (e.g. a
    /// CustomerWin AND a GuidanceChange, or a Positive vs a Neutral), so the key never collapses distinct
    /// signals into one.</item>
    /// <item><see cref="Signal.CompanyId"/> is usually already fixed by the caller's own filter, but is kept
    /// in the key so it stays self-describing and correct for reads that are not per-company.</item>
    /// <item><see cref="Signal.ObservedAtUtc"/> is intentionally EXCLUDED: it is derived from the same
    /// evidence and is therefore constant across a signal's cross-run copies, so it adds nothing; including
    /// it would risk NOT collapsing copies if a future change perturbed ObservedAt derivation.
    /// <see cref="Signal.Strength"/>, <see cref="Signal.Confidence"/>, <see cref="Signal.Novelty"/>,
    /// <see cref="Signal.SupportingExcerpt"/> and <see cref="Signal.Reason"/> are likewise
    /// evidence/extractor-derived and identical across copies — excluded too.</item>
    /// </list>
    /// </summary>
    public static (Guid? CompanyId, Guid EvidenceId, SignalType Type, SignalDirection Direction) Key(
        Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return (signal.CompanyId, signal.EvidenceId, signal.Type, signal.Direction);
    }

    /// <summary>
    /// Collapses cross-run copies to one survivor per <see cref="Key"/>, choosing the survivor by
    /// <paramref name="survivor"/>. Grouping is order-independent and every rule is a total order, so the
    /// same input set always yields the same survivors (AD-3). The result is NOT re-ordered — callers apply
    /// their own deterministic ordering.
    /// </summary>
    public static IReadOnlyList<Signal> Collapse(IEnumerable<Signal> signals, SignalCopySurvivor survivor)
    {
        ArgumentNullException.ThrowIfNull(signals);

        return [.. signals
            .GroupBy(Key)
            .Select(group => survivor switch
            {
                // Earliest KNOWN copy. Load-bearing wherever the spec-136 known-at predicate
                // (CreatedAtUtc <= windowEndUtc) is applied AFTER this collapse: keeping a later-created
                // copy would hide from a replay at T a signal Radar demonstrably knew about at T, turning
                // "was this known by T?" into "was the copy that happened to survive created by T?".
                // CreatedAtUtc first, then Id — a total, stable order over Guid.
                SignalCopySurvivor.EarliestKnown =>
                    group.OrderBy(s => s.CreatedAtUtc).ThenBy(s => s.Id).First(),

                // Lowest SignalId. Used where the read has ALREADY applied the known-at predicate before
                // collapsing (so every surviving copy is equally "known"), and all copies carry identical
                // activity fields — the simplest reproducible total order.
                _ => group.OrderBy(s => s.Id).First(),
            })];
    }
}

/// <summary>Which copy of a cross-run-duplicated signal a <see cref="SignalCrossRunDedupe.Collapse"/> keeps.</summary>
public enum SignalCopySurvivor
{
    /// <summary>Lowest <see cref="Signal.Id"/>.</summary>
    LowestId,

    /// <summary>Earliest <see cref="Signal.CreatedAtUtc"/>, then lowest <see cref="Signal.Id"/>.</summary>
    EarliestKnown,
}
