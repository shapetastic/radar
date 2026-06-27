namespace Radar.Domain.Signals;

public enum SignalReviewDecision
{
    Approve,
    Reject,
    NeedsMoreEvidence,
    ReduceConfidence,
    EscalateToHuman
}
