namespace Radar.Application.Scoring;

/// <summary>
/// The FULL effective resolved scoring config for one run — the exact inputs the ScoringConfigVersion
/// fingerprint (spec 89) hashes: engine identity, formula structure identity, every <see cref="ScoringWeights"/>
/// value, the attention tier-map canonical descriptor, the signal-source IDENTITY descriptor (the extractor
/// rule-set identity + the optional AI directional-filing magnitudes + the strategy's declared signal types;
/// spec 95, narrowed by spec 141), the insider-materiality descriptor (the
/// config-tunable buy/sell tiers + cluster boost, spec 96), the media-collapse descriptor (the
/// same-event media-attention collapse structure + window, spec 109), and the recent-signal WINDOW length
/// (spec 148). Persisted content-addressed by the
/// fingerprint so a historical snapshot's stamp dereferences back to the weights that produced it
/// (provenance completion — AD-10-as-amended). Immutable and Domain-free (an Application projection,
/// not an aggregate). Recomputing the fingerprint from
/// Engine/FormulaVersion/Weights/AttentionDescriptor/SignalSourceDescriptor/InsiderMaterialityDescriptor/MediaCollapseDescriptor/Window
/// via <see cref="ScoringConfigFingerprint"/> MUST equal <paramref name="Fingerprint"/> — the store's
/// self-verification invariant (the persisted config carries every field verbatim).
/// <para>
/// <b><paramref name="Window"/> IS NULLABLE ON PURPOSE.</b> A config file written before spec 148 has no
/// window field at all, and deserializing that absence as <see cref="TimeSpan.Zero"/> would be a FALSE
/// record — it would claim a zero-length window that no run ever used. <c>null</c> therefore means
/// "written pre-148; the window was not recorded", which is honest and un-recomputable, exactly as it
/// should be. Every NEW write populates it, so the self-verification invariant above holds for everything
/// this codebase writes from now on.
/// </para>
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
/// <param name="Window">The recent-signal window length (<see cref="ScoringOptions.Window"/>, spec 148),
/// carried verbatim so the fingerprint stays recomputable from the stored record. <c>null</c> ⇒ the file was
/// written before spec 148 and the window was never recorded — see the note on the type.</param>
public sealed record EffectiveScoringConfig(
    string Fingerprint,
    string EngineVersion,
    string FormulaVersion,
    ScoringWeights Weights,
    string AttentionDescriptor,
    string SignalSourceDescriptor,
    string InsiderMaterialityDescriptor,
    string MediaCollapseDescriptor,
    TimeSpan? Window);
