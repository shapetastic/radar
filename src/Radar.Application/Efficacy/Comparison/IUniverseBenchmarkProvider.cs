using Radar.Application.Acquisitions;

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
    /// <summary>
    /// SPEC 217 §3 — the acquisitions projection the returned benchmark was built with. Consumers apply the
    /// OBSERVATION exclusion (<c>observation-eligibility-v2</c>) over this exact instance, so it cannot
    /// disagree with the PEER-MEAN exclusion (<c>excess-vs-universe-v2</c>) baked into the benchmark. It is
    /// <see cref="PendingAcquisitions.None"/> until <c>GetAsync</c> has run.
    /// </summary>
    PendingAcquisitions Acquisitions { get; }

    Task<UniverseBenchmark?> GetAsync(CancellationToken ct);
}
