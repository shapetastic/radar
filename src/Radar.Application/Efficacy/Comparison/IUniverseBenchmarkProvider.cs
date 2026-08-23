namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// Hands out THE process-wide <see cref="UniverseBenchmark"/> (spec 183 §1): loaded once — frozen artifact +
/// each member's price series — and shared by every consumer (the spec-140 leaderboard and the spec-179
/// news-risk evaluator), so the benchmark at one (universeVersion, D, horizon, tolerance) is computed once and
/// two arms can never derive different outcomes from different member sets.
/// <para>
/// Returns <c>null</c> when the frozen universe is unavailable (missing/invalid artifact): the consumers then
/// record every excess observation as <c>BenchmarkUnavailable</c> — named and counted, never a silent raw
/// fallback — and their artifacts state the universe could not be loaded.
/// </para>
/// </summary>
public interface IUniverseBenchmarkProvider
{
    Task<UniverseBenchmark?> GetAsync(CancellationToken ct);
}
