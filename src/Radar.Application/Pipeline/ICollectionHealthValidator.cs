using Radar.Application.Collectors;

namespace Radar.Application.Pipeline;

/// <summary>
/// Diagnostic collection-health validator: reconciles the feed-type inventory DECLARED in the seed
/// against the feed-type inventory that actually REACHED the <see cref="CollectionContext"/> and reports
/// any per-feed-type shrinkage (declared &gt; reached). Observability only — its output is never
/// evidence, never a signal, never a scoring input, and never fails-fast the run.
/// </summary>
public interface ICollectionHealthValidator
{
    Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct);
}
