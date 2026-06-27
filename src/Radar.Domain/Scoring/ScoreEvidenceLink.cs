namespace Radar.Domain.Scoring;

public sealed record ScoreEvidenceLink(
    Guid Id,
    Guid ScoreSnapshotId,
    Guid SignalId,
    Guid EvidenceId,
    string ContributionReason,
    int ContributionWeight);
