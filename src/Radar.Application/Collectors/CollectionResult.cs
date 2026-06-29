using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

/// <summary>
/// The output of one collector run: the collected evidence plus an observational
/// <see cref="CollectionSummary"/> describing collection health.
/// </summary>
public sealed record CollectionResult(
    IReadOnlyCollection<CollectedEvidence> Evidence,
    CollectionSummary Summary);
