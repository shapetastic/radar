namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>
/// Composes the spec-172 read-only audit: reads each configured strategy's persisted snapshot series (with
/// evidence links), builds the consecutive-pair observations, computes the statistics, and writes ONE
/// CSV + markdown artifact pair. Opt-in (<c>Radar:Efficacy:DenominatorAudit:Enabled</c>, default OFF inside
/// the already-opt-in <c>Radar:Efficacy</c> gate) and skipped entirely by a replay run. It produces a number;
/// a human reads it — nothing is ranked, promoted or remediated here.
/// </summary>
public interface IScoreMoveDenominatorAuditGenerator
{
    Task<DenominatorAuditReport> GenerateAsync(CancellationToken ct);
}
