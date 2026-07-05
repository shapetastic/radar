namespace Radar.Application.Scoring;

/// <summary>
/// Canonical, deterministic descriptor of the run's SIGNAL-PRODUCTION surface — the enabled
/// evidence-collector set plus the deterministic extractor's rule-set identity — folded into the
/// <c>ScoringConfigVersion</c> content fingerprint (AD-10) so that enabling/disabling a collector (or
/// changing the extractor rule set) re-stamps the scoring generation. Two runs whose signal-production
/// surface differs must NOT be judged comparable by the spec-69 gate. Deterministic: stable ordering,
/// culture-invariant, no clock/IO/randomness (AD-3). Consumed only for fingerprinting.
/// </summary>
public interface ISignalSourceDescriptor
{
    string CanonicalDescriptor();
}
