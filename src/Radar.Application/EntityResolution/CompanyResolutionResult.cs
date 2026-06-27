namespace Radar.Application.EntityResolution;

/// <summary>
/// Outcome of resolving a company mention string against the seed universe.
/// <see cref="CompanyId"/> is <c>null</c> when unresolved, in which case
/// <see cref="Confidence"/> is <c>0m</c>. <see cref="Reason"/> is a human-readable
/// explanation that downstream code can record on an evidence mention to preserve
/// provenance (why a company was, or was not, matched).
/// </summary>
public sealed record CompanyResolutionResult(
    Guid? CompanyId,
    decimal Confidence,
    string Reason,
    string? MatchedAlias);
