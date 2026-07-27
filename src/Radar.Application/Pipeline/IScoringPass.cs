using Radar.Domain.Companies;

namespace Radar.Application.Pipeline;

/// <summary>
/// Stage 6 as an independently invokable unit (spec 144): score a supplied company universe at a supplied
/// as-of instant, once PER CONFIGURED STRATEGY (spec 137). It depends on <b>no collector</b>, no extractor,
/// no resolver and no AI seam — that is what makes a standalone <c>score</c> pass provably free of collection
/// side effects rather than free of them by convention.
/// <para>
/// There is exactly ONE scoring code path. The combined run, the standalone <c>score</c> pass and the spec-139
/// replay all reach <c>ScoringEngine.ScoreCompanyAsync</c>; this type is the shared stage-6 loop for the first
/// two, and replay drives the same engines through its own read-only, replay-scoped harness. A second copy
/// would drift and silently invalidate <c>replay ⊆ forward</c>.
/// </para>
/// </summary>
public interface IScoringPass
{
    /// <summary>
    /// Scores <paramref name="companies"/> at <paramref name="asOfUtc"/> with every configured strategy.
    /// The caller supplies the universe: the combined run reuses the list the collection pass already loaded
    /// (one repository read per run), and a standalone score pass loads it itself.
    /// </summary>
    Task<ScoringPassResult> RunAsync(
        IReadOnlyList<Company> companies, DateTimeOffset asOfUtc, CancellationToken ct);
}

/// <summary>
/// What stage 6 did: how many companies the PRIMARY strategy scored (the run record's established meaning of
/// "companies scored"), the strategy names in run order, and which of them was primary.
/// </summary>
public sealed record ScoringPassResult(
    int CompaniesScored,
    IReadOnlyList<string> Strategies,
    string PrimaryStrategy);
