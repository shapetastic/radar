using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Scoring;

/// <summary>
/// Default <see cref="ISignalSourceDescriptor"/>: builds a canonical, deterministic descriptor of the run's
/// signal-production surface ONCE at construction — the distinct enabled collector names plus the
/// deterministic extractor's rule-set identity (<see cref="KeywordSignalExtractor.RuleSetVersion"/>), and,
/// when the opt-in AI directional-filing path is registered, that source's per-signal magnitudes (its
/// <see cref="IDirectionalFilingSignalSource.ScoringDescriptor"/>). It reads only
/// <see cref="IEvidenceCollector.CollectorName"/> and <see cref="IDirectionalFilingSignalSource.ScoringDescriptor"/>
/// and NEVER calls <see cref="IEvidenceCollector.CollectAsync"/> or
/// <see cref="IDirectionalFilingSignalSource.ProduceAsync"/>, so it has zero collection side effects and stays
/// a pure function of the composed signal-source set (AD-3). When the AI source is absent (null — AI off) the
/// descriptor is byte-identical to the collectors-only form, so the AI-off fingerprint is unchanged.
/// </summary>
public sealed class SignalSourceDescriptor : ISignalSourceDescriptor
{
    private readonly string _descriptor;

    public SignalSourceDescriptor(
        IEnumerable<IEvidenceCollector> collectors,
        IDirectionalFilingSignalSource? aiFilingSource = null)
    {
        ArgumentNullException.ThrowIfNull(collectors);

        // Read ONLY CollectorName — never CollectAsync (no collection side effects). De-dupe defensively so a
        // mis-registration listing a collector twice does not change the descriptor, and order by Ordinal so
        // registration order is irrelevant.
        var names = collectors
            .Select(c => c.CollectorName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(DescriptorEscaping.Escape);

        var csv = string.Join(',', names);

        // CollectorName is each collector's stable provenance identifier (e.g. "RssPressReleaseCollector",
        // "sec-edgar", "sec-form4", "usaspending", "newssearch") — NOT the Radar:Collectors config "kind"
        // token. Treat it as opaque: it is delimiter-free today, but escaping keeps the serialization injective
        // (AD-3) so a name that ever contained a reserved delimiter cannot collide with a different collector set.
        // The AI directional-filing source (when registered) contributes an escaped ai=… segment AFTER the
        // collectors segment (fixed field ordering, AD-3), reusing the shared DescriptorEscaping so the whole descriptor stays
        // injective. When it is null (AI off) NOTHING is appended, so the AI-off descriptor is byte-identical to
        // the pre-spec-106 form (the pinned AI-off default fingerprint is unchanged).
        var descriptor = $"rules={KeywordSignalExtractor.RuleSetVersion};collectors={csv};";
        if (aiFilingSource is not null)
        {
            descriptor += $"ai={DescriptorEscaping.Escape(aiFilingSource.ScoringDescriptor())};";
        }

        _descriptor = descriptor;
    }

    /// <inheritdoc />
    public string CanonicalDescriptor() => _descriptor;
}
