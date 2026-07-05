namespace Radar.Application.Pipeline;

/// <summary>Diagnostic severity for a collection-health finding. Observability only —
/// never a label, score, or advice language.</summary>
public enum CollectionHealthSeverity
{
    /// <summary>A note, not a defect (e.g. a config observation).</summary>
    Info,
    /// <summary>A real problem: expected data went missing.</summary>
    Warning,
}

/// <summary>One collection-health finding. Purely observational: it references no evidence,
/// carries no label/score, and never influences scoring.</summary>
public sealed record CollectionHealthWarning(
    string Code,
    CollectionHealthSeverity Severity,
    string FeedType,
    int DeclaredInSeed,
    int ReachedCollectors,
    string Message);

/// <summary>The collection-health findings for one run, in deterministic order.</summary>
public sealed record CollectionHealthReport(IReadOnlyList<CollectionHealthWarning> Warnings)
{
    public static CollectionHealthReport Empty { get; } = new([]);
    public bool HasWarnings => Warnings.Count > 0;
}
