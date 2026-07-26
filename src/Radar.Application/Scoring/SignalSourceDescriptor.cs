using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Scoring;

/// <summary>
/// Default <see cref="ISignalSourceDescriptor"/>: builds BOTH canonical strings ONCE at construction from the
/// composed signal-source set, walking the collector list exactly once.
/// <list type="bullet">
/// <item><description>
/// <b>Identity</b> (<see cref="CanonicalDescriptor"/>) — <c>rules=&lt;RuleSetVersion&gt;;[ai=…;]</c>: the
/// deterministic extractor's rule-set identity (<see cref="KeywordSignalExtractor.RuleSetVersion"/>) plus,
/// when the opt-in AI directional-filing path is registered, that source's per-signal magnitudes (its
/// <see cref="IDirectionalFilingSignalSource.ScoringDescriptor"/>, escaped). This is what the
/// <c>ScoringConfigVersion</c> fingerprint hashes.
/// </description></item>
/// <item><description>
/// <b>Provenance</b> (<see cref="CollectionProvenance"/>) — <c>collectors=&lt;csv&gt;;</c>: the distinct,
/// Ordinal-ordered, escaped enabled collector names. Recorded on every snapshot, hashed into nothing
/// (spec 141).
/// </description></item>
/// </list>
/// <para>
/// It reads only <see cref="IEvidenceCollector.CollectorName"/> and
/// <see cref="IDirectionalFilingSignalSource.ScoringDescriptor"/> and NEVER calls
/// <see cref="IEvidenceCollector.CollectAsync"/> or <see cref="IDirectionalFilingSignalSource.ProduceAsync"/>,
/// so it has zero collection side effects and stays a pure function of the composed signal-source set (AD-3).
/// When the AI source is absent (null — AI off) NOTHING is appended to the identity descriptor, so the AI-off
/// identity is byte-identical to the AI-on-minus-<c>ai=</c> form.
/// </para>
/// </summary>
public sealed class SignalSourceDescriptor : ISignalSourceDescriptor
{
    private readonly string _identityDescriptor;
    private readonly string _collectionProvenance;

    public SignalSourceDescriptor(
        IEnumerable<IEvidenceCollector> collectors,
        IDirectionalFilingSignalSource? aiFilingSource = null)
    {
        ArgumentNullException.ThrowIfNull(collectors);

        // Read ONLY CollectorName — never CollectAsync (no collection side effects). De-dupe defensively so a
        // mis-registration listing a collector twice does not change the descriptor, and order by Ordinal so
        // registration order is irrelevant. Enumerated ONCE, feeding the provenance string only.
        var names = collectors
            .Select(c => c.CollectorName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(DescriptorEscaping.Escape);

        var csv = string.Join(',', names);

        // COLLECTION PROVENANCE (spec 141), hashed into nothing. CollectorName is each collector's stable
        // provenance identifier (e.g. "RssPressReleaseCollector", "sec-edgar", "sec-form4", "usaspending",
        // "newssearch") — NOT the Radar:Collectors config "kind" token. Treat it as opaque: it is
        // delimiter-free today, but escaping keeps the serialization injective (AD-3) so a name that ever
        // contained a reserved delimiter cannot collide with a different collector set.
        _collectionProvenance = $"collectors={csv};";

        // STRATEGY IDENTITY (spec 141): the extractor rule-set identity, plus the AI directional-filing
        // magnitudes when that path is registered (fixed field ordering, AD-3, reusing the shared
        // DescriptorEscaping so the whole descriptor stays injective). The enabled-collector set is
        // deliberately ABSENT: a collector toggle must not move a strategy's identity, because it does not
        // change what hypothesis the strategy scores. The ai= segment stays here because it carries per-signal
        // magnitudes and the reading model, which change signal DIRECTION (spec 119) — genuinely different
        // scorings that must never share a fingerprint.
        var descriptor = $"rules={KeywordSignalExtractor.RuleSetVersion};";
        if (aiFilingSource is not null)
        {
            descriptor += $"ai={DescriptorEscaping.Escape(aiFilingSource.ScoringDescriptor())};";
        }

        _identityDescriptor = descriptor;
    }

    /// <inheritdoc />
    public string CanonicalDescriptor() => _identityDescriptor;

    /// <inheritdoc />
    public string CollectionProvenance() => _collectionProvenance;
}
