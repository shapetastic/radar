using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>
/// One directional-count bin of the spec-172 distribution table. The bins are FIXED and ORDERED
/// (0, 1, 2, 3, 4+); an empty bin is rendered with empty statistics rather than being dropped, so the table's
/// shape never depends on the data.
/// </summary>
public sealed record DenominatorBin(
    string Label,
    int Count,
    double? MedianAbsDeltaOpportunity,
    double? P90AbsDeltaOpportunity);

/// <summary>
/// One strategy's whole audit result: every consecutive-pair observation, the two Spearman coefficients
/// (|ΔOpportunity| vs DirectionalCount — the hypothesis — and vs LinkCount, reported alongside so the two
/// denominators are never conflated), and the binned |ΔOpportunity| distribution.
/// <para>
/// A degenerate coefficient carries its NAMED reason (the shared rank-correlation vocabulary) rather than
/// NaN. <see cref="CompaniesWalked"/> / <see cref="CompaniesWithPairs"/> make the coverage honest: a company
/// with a single snapshot contributes no pair, and that is visible arithmetic rather than a silent drop.
/// </para>
/// </summary>
public sealed record DenominatorAuditStrategyResult(
    string StrategyName,
    int CompaniesWalked,
    int CompaniesWithPairs,
    IReadOnlyList<DenominatorObservation> Observations,
    SpearmanRhoResult RhoAbsDeltaVsDirectionalCount,
    SpearmanRhoResult RhoAbsDeltaVsLinkCount,
    IReadOnlyList<DenominatorBin> Bins);

/// <summary>The whole audit report: one result per configured strategy, in configured order.</summary>
public sealed record DenominatorAuditReport(IReadOnlyList<DenominatorAuditStrategyResult> Strategies);
