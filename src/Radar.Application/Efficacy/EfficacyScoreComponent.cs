namespace Radar.Application.Efficacy;

/// <summary>
/// Which numeric score component the efficacy SVG plots against price. Slice 1 defaults to
/// <see cref="Opportunity"/> (the headline); the field is kept selectable so a later slice can render other
/// components without a rewrite. Radar plots NUMERIC scores, never labels (AD-9).
/// </summary>
public enum EfficacyScoreComponent
{
    Trajectory,
    Opportunity,
    Attention,
    EvidenceConfidence,
    SignalVelocity,
}
