namespace Radar.Application.Collectors;

/// <summary>
/// The enabled-collector <b>VOCABULARY</b> (spec 147): the NAMES of the evidence collectors this run is
/// configured with, and nothing else. It is the ONE definition of the ordered-distinct-Ordinal projection
/// that <see cref="Radar.Application.Scoring.ISignalSourceDescriptor"/> renders as
/// <c>CollectionProvenance()</c> and hands out as <c>EnabledCollectors()</c>.
/// <para>
/// <b>Why this type exists.</b> "Can collect" and "is a known collector" were welded into
/// <see cref="IEvidenceCollector"/>. Spec 144's standalone <c>score</c> pass registers ZERO collectors on
/// purpose (construction is what opens the typed HttpClients), which silently took the vocabulary away with
/// the capability: every snapshot recorded <c>collectors=;</c> although a <c>collect</c> pass had genuinely
/// gathered its evidence from N collectors, and a <c>radar-formula-v9</c> collector-channel strategy could
/// not start at all. Splitting the names out restores the vocabulary in every mode <b>without</b> smuggling a
/// fetch capability back in: this type holds strings. It cannot collect, and it has no reference to anything
/// that can.
/// </para>
/// <para>
/// Names are de-duped and ordered <b>Ordinal</b> so registration/config order is irrelevant and a
/// mis-registration listing a collector twice cannot change what is recorded. The list is handed out behind
/// a genuinely read-only wrapper rather than the backing array: a bare array can be cast back to
/// <c>string[]</c> and mutated, and this is a process-lifetime singleton every scoring engine reads.
/// </para>
/// </summary>
public sealed class EnabledCollectorVocabulary
{
    /// <summary>The empty vocabulary — no collector is configured for this run.</summary>
    public static EnabledCollectorVocabulary Empty { get; } = new(Array.Empty<string>());

    private EnabledCollectorVocabulary(string[] orderedDistinctNames) =>
        CollectorNames = Array.AsReadOnly(orderedDistinctNames);

    /// <summary>
    /// The distinct, Ordinal-ordered collector names. Recorded provenance: hashed into <b>nothing</b>
    /// (spec 141 — the collector set is not strategy identity).
    /// </summary>
    public IReadOnlyList<string> CollectorNames { get; }

    /// <summary>
    /// Builds the vocabulary from names that are already known without constructing anything — the
    /// composition root's kind→collector table, which is resolved identically in every run mode.
    /// </summary>
    public static EnabledCollectorVocabulary FromNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new EnabledCollectorVocabulary(Project(names));
    }

    /// <summary>
    /// Builds the vocabulary from the composed collector set. Reads ONLY
    /// <see cref="IEvidenceCollector.CollectorName"/> and never calls
    /// <see cref="IEvidenceCollector.CollectAsync"/>, so it has zero collection side effects (AD-3). This is
    /// the library-only default for compositions that register their collectors and nothing else; the Worker
    /// registers the config-derived vocabulary instead, so a <c>score</c> pass still has one.
    /// </summary>
    public static EnabledCollectorVocabulary FromCollectors(IEnumerable<IEvidenceCollector> collectors)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        return new EnabledCollectorVocabulary(Project(collectors.Select(c => c.CollectorName)));
    }

    // THE projection (moved here verbatim from SignalSourceDescriptor's constructor, spec 147): distinct by
    // Ordinal, ordered by Ordinal, enumerated exactly once.
    private static string[] Project(IEnumerable<string> names) =>
        names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
