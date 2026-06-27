namespace Radar.Application.EntityResolution;

/// <summary>
/// Deterministic, conservative entity resolver mapping a company mention string to a
/// known company in the seed universe via exact, normalized name/alias matching, or an
/// exact case-insensitive match on the raw ticker (tickers are not normalized: no
/// punctuation or suffix stripping applies to them).
/// No fuzzy, substring, or AI matching: ambiguity and uncertainty resolve to unresolved.
/// </summary>
public interface ICompanyResolver
{
    Task<CompanyResolutionResult> ResolveAsync(string mentionText, CancellationToken ct);
}
