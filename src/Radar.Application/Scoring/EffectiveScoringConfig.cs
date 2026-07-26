namespace Radar.Application.Scoring;

/// <summary>
/// The FULL effective resolved scoring config for one run — the exact inputs the ScoringConfigVersion
/// fingerprint (spec 89) hashes: engine identity, formula structure identity, every <see cref="ScoringWeights"/>
/// value, the attention tier-map canonical descriptor, the signal-source IDENTITY descriptor (the extractor
/// rule-set identity + the optional AI directional-filing magnitudes + the strategy's declared signal types;
/// spec 95, narrowed by spec 141), the insider-materiality descriptor (the
/// config-tunable buy/sell tiers + cluster boost, spec 96), and the media-collapse descriptor (the
/// same-event media-attention collapse structure + window, spec 109). Persisted content-addressed by the
/// fingerprint so a historical snapshot's stamp dereferences back to the weights that produced it
/// (provenance completion — AD-10-as-amended). Immutable and Domain-free (an Application projection,
/// not an aggregate). Recomputing the fingerprint from
/// Engine/FormulaVersion/Weights/AttentionDescriptor/SignalSourceDescriptor/InsiderMaterialityDescriptor/MediaCollapseDescriptor
/// via <see cref="ScoringConfigFingerprint"/> MUST equal <paramref name="Fingerprint"/> — the store's
/// self-verification invariant (the persisted config carries every field verbatim).
/// <para>
/// THE ENABLED-COLLECTOR SET IS DELIBERATELY ABSENT (spec 141). This store is content-addressed by the
/// identity fingerprint and insert-if-new/immutable, so a per-RUN fact stored here would be permanently
/// pinned to whichever run happened to write the file first — a silently wrong record for every later run
/// that shares the fingerprint with a different collector set. Collection provenance is therefore recorded
/// per-snapshot instead (<c>CompanyScoreSnapshot.CollectionProvenance</c>), where it is true of exactly the
/// run it describes. <paramref name="SignalSourceDescriptor"/> keeps carrying the (now identity-only)
/// descriptor verbatim, so recompute-from-stored still equals the filename.
/// </para>
/// </summary>
/// <param name="Fingerprint">The generation stamp (== the <c>CompanyScoreSnapshot.ScoringConfigVersion</c>).</param>
/// <param name="EngineVersion">The engine structure identity (e.g. <c>mvp-engine-v1</c>).</param>
/// <param name="FormulaVersion">The formula structure identity (e.g. <c>radar-formula-v8</c>).</param>
/// <param name="Weights">Every scoring magnitude value (the spec-89 record).</param>
/// <param name="AttentionDescriptor">The attention tier-map <c>CanonicalDescriptor()</c>, stored verbatim.</param>
/// <param name="SignalSourceDescriptor">The signal-source IDENTITY <c>CanonicalDescriptor()</c> (extractor
/// rule-set identity + optional AI directional-filing magnitudes + declared signal types; spec 95 narrowed by
/// spec 141 — NOT the enabled collector set), stored verbatim.</param>
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
