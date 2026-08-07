namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>
/// One consecutive-snapshot-pair observation of the spec-172 audit: how far the score moved between two
/// CONSECUTIVE SNAPSHOTS of one company under one strategy (consecutive snapshots in as-of order — NOT
/// consecutive calendar days; a gap in the as-of dates still pairs the neighbouring snapshots), and how thick
/// the evidence base under the LATER snapshot was.
/// <para>
/// <see cref="DirectionalCount"/> is the denominator the hypothesis is actually about: the later snapshot's
/// evidence links whose contribution reason is NOT Neutral. <see cref="LinkCount"/> is reported alongside
/// because it is the number a reader sees in the weekly report, and the two must not be conflated.
/// </para>
/// </summary>
public sealed record DenominatorObservation(
    string StrategyName,
    Guid CompanyId,
    DateOnly AsOfDate,
    int DeltaOpportunity,
    int DeltaTrajectory,
    int LinkCount,
    int DirectionalCount)
{
    /// <summary>|ΔOpportunity| — the magnitude the statistic and the bin table are computed over.</summary>
    public int AbsDeltaOpportunity => Math.Abs(DeltaOpportunity);
}
