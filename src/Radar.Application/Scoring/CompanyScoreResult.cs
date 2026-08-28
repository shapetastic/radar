using Radar.Domain.Scoring;

namespace Radar.Application.Scoring;

/// <summary>The persisted snapshot together with the evidence links that trace it to signals/evidence.</summary>
/// <param name="Snapshot">The persisted snapshot.</param>
/// <param name="Links">One link per contribution, tracing the snapshot to signals/evidence.</param>
/// <param name="Diagnostics">
/// Spec 197 §3: what THIS strategy-company evaluation had to discard or correct while assembling its input.
/// TRANSIENT ORCHESTRATION STATE — never persisted, never a wire contract, never an identity input, hashed
/// into nothing. It is a required (not nullable-optional) member so a caller that can see the whole operation
/// cannot silently receive nothing to aggregate: an absent diagnostic is unrepresentable.
/// </param>
public sealed record CompanyScoreResult(
    CompanyScoreSnapshot Snapshot,
    IReadOnlyList<ScoreEvidenceLink> Links,
    ScoreAssemblyDiagnostics Diagnostics);
