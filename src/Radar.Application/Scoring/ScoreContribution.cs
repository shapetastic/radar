namespace Radar.Application.Scoring;

/// <summary>
/// One signal's contribution to a score, the basis for a domain <c>ScoreEvidenceLink</c>. Preserves
/// provenance: every contribution names the signal and the evidence behind it.
/// </summary>
public sealed record ScoreContribution(
    Guid SignalId,
    Guid EvidenceId,
    string ContributionReason,
    int ContributionWeight);
