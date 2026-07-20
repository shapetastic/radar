namespace Radar.Application.Scoring;

/// <summary>
/// The FULL effective resolved scoring config for one run — the exact inputs the ScoringConfigVersion
/// fingerprint (spec 89) hashes: engine identity, formula structure identity, every <see cref="ScoringWeights"/>
/// value, the attention tier-map canonical descriptor, the signal-source descriptor (the enabled
/// collector set + extractor rule-set identity, spec 95), the insider-materiality descriptor (the
/// config-tunable buy/sell tiers + cluster boost, spec 96), and the media-collapse descriptor (the
/// same-event media-attention collapse structure + window, spec 109). Persisted content-addressed by the
/// fingerprint so a historical snapshot's stamp dereferences back to the weights that produced it
/// (provenance completion — AD-10-as-amended). Immutable and Domain-free (an Application projection,
/// not an aggregate). Recomputing the fingerprint from
/// Engine/FormulaVersion/Weights/AttentionDescriptor/SignalSourceDescriptor/InsiderMaterialityDescriptor/MediaCollapseDescriptor
/// via <see cref="ScoringConfigFingerprint"/> MUST equal <paramref name="Fingerprint"/> — the store's
/// self-verification invariant (the persisted config carries every field verbatim).
/// </summary>
/// <param name="Fingerprint">The generation stamp (== the <c>CompanyScoreSnapshot.ScoringConfigVersion</c>).</param>
/// <param name="EngineVersion">The engine structure identity (e.g. <c>mvp-engine-v1</c>).</param>
/// <param name="FormulaVersion">The formula structure identity (e.g. <c>radar-formula-v7</c>).</param>
/// <param name="Weights">Every scoring magnitude value (the spec-89 record).</param>
/// <param name="AttentionDescriptor">The attention tier-map <c>CanonicalDescriptor()</c>, stored verbatim.</param>
/// <param name="SignalSourceDescriptor">The signal-source <c>CanonicalDescriptor()</c> (enabled collector set +
/// extractor rule-set identity, spec 95), stored verbatim.</param>
/// <param name="InsiderMaterialityDescriptor">The insider-materiality <c>CanonicalDescriptor()</c> (config-tunable
/// buy/sell tiers + cluster boost, spec 96), stored verbatim.</param>
/// <param name="MediaCollapseDescriptor">The media-collapse <c>CanonicalDescriptor()</c> (the same-event
/// media-attention collapse structure + window, spec 109), stored verbatim.</param>
public sealed record EffectiveScoringConfig(
    string Fingerprint,
    string EngineVersion,
    string FormulaVersion,
    ScoringWeights Weights,
    string AttentionDescriptor,
    string SignalSourceDescriptor,
    string InsiderMaterialityDescriptor,
    string MediaCollapseDescriptor);
