namespace Radar.Application.Collectors;

/// <summary>
/// The output of one collector run: the collected evidence plus an observational
/// <see cref="CollectionSummary"/> describing collection health.
/// </summary>
/// <param name="Evidence">The collected raw evidence, in the collector's own deterministic order.</param>
/// <param name="Summary">Observational collection health for this collector run.</param>
/// <param name="CompanyCoverage">
/// Optional per-company collection coverage (spec 169), recorded by the collectors whose completeness the
/// AD-16 attention-arrival evaluator has to be able to PROVE. Trailing + optional so every existing collector
/// and every existing construction site is unchanged; <c>null</c> means this collector records no
/// per-company coverage, which downstream reads as UNPROVEN — never as success.
/// </param>
public sealed record CollectionResult(
    IReadOnlyCollection<CollectedEvidence> Evidence,
    CollectionSummary Summary,
    IReadOnlyList<CollectorCompanyCoverage>? CompanyCoverage = null);
