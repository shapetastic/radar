namespace Radar.Application.Collectors;

/// <summary>
/// Whether collection actually happened in the pass that produced a score (spec 147). This is a DIFFERENT
/// fact from <see cref="EnabledCollectorVocabulary"/> ("which collectors this run is configured with"), and
/// conflating the two is exactly what made a spec-144 <c>score</c> pass record false provenance: an empty
/// collector CSV read as "no collector was configured" when the truth was "these N collectors are configured
/// and none of them ran in THIS pass, because this pass scores what a previous collect pass gathered".
/// </summary>
public enum CollectionPassKind
{
    /// <summary>
    /// This pass collected (or was at least able to): <c>full</c>, <c>collect</c>, and every library-only
    /// composition. The default, so every existing composition is byte-for-byte unchanged.
    /// </summary>
    Collected = 0,

    /// <summary>
    /// This pass ran NO collector: spec 144's standalone <c>score</c> pass, which scores whatever a previous
    /// <c>collect</c> pass accrued. The configured vocabulary is still recorded — it describes what THIS
    /// scoring process is configured with, which is not necessarily what gathered the data being scored (the
    /// collector configuration may have changed since that collect pass) — and the pass is marked precisely
    /// so a later reader reads it as configuration rather than mistaking it for a record of collection.
    /// </summary>
    NoCollectionThisPass,
}

/// <summary>
/// Registered as a singleton by the composition root to declare which kind of pass this process is
/// (spec 147). Follows the established <c>*Options</c> convention: a <c>TryAddSingleton</c> default means an
/// unaware composition keeps the pre-147 behaviour exactly.
/// </summary>
public sealed class CollectionPassOptions
{
    /// <summary>
    /// Defaults to <see cref="CollectionPassKind.Collected"/>, so a composition that never mentions this
    /// type produces byte-identical provenance to before spec 147.
    /// </summary>
    public CollectionPassKind Kind { get; init; } = CollectionPassKind.Collected;
}
