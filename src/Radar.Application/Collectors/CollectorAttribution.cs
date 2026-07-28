namespace Radar.Application.Collectors;

/// <summary>
/// WHERE a piece of evidence's collector attribution came from (spec 151). Attribution is provenance, and
/// provenance is sacred — so "the collector stamped this at collection time" and "Radar re-derived this
/// afterwards from the evidence's own fields" must never be the same value with the same weight.
/// <para>
/// <see cref="Unattributed"/> is deliberately <c>0</c>. <see cref="CollectorAttribution"/> is a struct, so
/// <c>default(CollectorAttribution)</c> is always constructible; pinning the zero member to the
/// name-less state is what makes that default satisfy the type's invariant
/// (<see cref="CollectorAttribution.CollectorName"/> is non-null iff the source is not
/// <see cref="Unattributed"/>) instead of producing a nameless "Recorded" attribution nobody wrote.
/// </para>
/// </summary>
public enum CollectorAttributionSource
{
    /// <summary>
    /// No collector could be established: nothing was recorded and either inference is off (the default) or
    /// the evidence is genuinely ambiguous. Radar never guesses — an ambiguous record stays unattributed, and
    /// a collector channel consumes nothing for it.
    /// </summary>
    Unattributed = 0,

    /// <summary>
    /// The collector that produced this evidence recorded its own name at collection time (spec 146's
    /// <c>collector</c> metadata key). This is a FACT, not a derivation.
    /// </summary>
    Recorded = 1,

    /// <summary>
    /// Radar re-derived the collector from the evidence's <c>SourceType</c> plus a collector-exclusive
    /// metadata marker (spec 151), because nothing was recorded. It is an INFERENCE — well-evidenced, but an
    /// inference — and every artifact built on it must be able to say so.
    /// </summary>
    Inferred = 2,
}

/// <summary>
/// Which collector retrieved a piece of evidence, <b>together with how that answer was obtained</b>
/// (spec 151).
/// <para>
/// <b>Why this type exists rather than a bare <c>string?</c>.</b> Spec 146 records the producing collector on
/// new evidence, but 94.7% of the evidence accrued before it carries no such stamp — so a
/// <c>radar-formula-v9</c> collector channel scores almost the whole accrued store at 0, and a backtest over
/// that window would measure the missing attribution rather than the strategy. Spec 151 re-derives the
/// dropped fact from fields the evidence still carries. That derivation is legitimate (it was deterministic
/// at collection time and simply was not persisted) but it is NOT the same kind of thing as a recorded stamp,
/// and the acceptance criterion is that the difference is visible <b>structurally, not by convention</b>. A
/// nullable string cannot carry that difference; this can, and the compiler forces every consumer to see it.
/// </para>
/// <para>
/// <b>Invariant, enforced at construction:</b> <see cref="CollectorName"/> is non-null and non-blank if and
/// only if <see cref="Source"/> is not <see cref="CollectorAttributionSource.Unattributed"/>. The only
/// constructor is private and the factories validate, so an "attributed but nameless" or "unattributed but
/// named" value is not expressible — including <c>default</c>, which is exactly
/// <see cref="Unattributed"/> (see the note on <see cref="CollectorAttributionSource.Unattributed"/>).
/// </para>
/// </summary>
public readonly record struct CollectorAttribution
{
    private CollectorAttribution(string? collectorName, CollectorAttributionSource source)
    {
        CollectorName = collectorName;
        Source = source;
    }

    /// <summary>
    /// No collector could be established. Identical to <c>default(CollectorAttribution)</c> by construction,
    /// so the two can never diverge.
    /// </summary>
    public static CollectorAttribution Unattributed => default;

    /// <summary>
    /// The collector's stable provenance name (<c>IEvidenceCollector.CollectorName</c>), or <c>null</c> when
    /// <see cref="Source"/> is <see cref="CollectorAttributionSource.Unattributed"/>. Matching is EXACT
    /// (ordinal) everywhere it is consumed — see <c>ScoringChannel.Consumes</c> — so this is never
    /// case-normalised or trimmed beyond what the producer wrote.
    /// </summary>
    public string? CollectorName { get; }

    /// <summary>How <see cref="CollectorName"/> was obtained. Never inferred from the name being present.</summary>
    public CollectorAttributionSource Source { get; }

    /// <summary>True when a collector was established, by either route.</summary>
    public bool IsAttributed => Source != CollectorAttributionSource.Unattributed;

    /// <summary>
    /// The collector recorded its own name at collection time (spec 146). Always preferred over an inference:
    /// a recorded stamp is the producing collector's own answer.
    /// </summary>
    public static CollectorAttribution Recorded(string collectorName) =>
        Create(collectorName, CollectorAttributionSource.Recorded);

    /// <summary>
    /// Radar re-derived the collector from the evidence itself (spec 151), because nothing was recorded.
    /// </summary>
    public static CollectorAttribution Inferred(string collectorName) =>
        Create(collectorName, CollectorAttributionSource.Inferred);

    private static CollectorAttribution Create(string collectorName, CollectorAttributionSource source)
    {
        // An attributed-but-nameless value would defeat the whole point: a consumer asking "which collector"
        // would get null while IsAttributed said yes.
        ArgumentException.ThrowIfNullOrWhiteSpace(collectorName);
        return new CollectorAttribution(collectorName, source);
    }
}
