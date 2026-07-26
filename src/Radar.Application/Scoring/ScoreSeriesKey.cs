using Radar.Domain.Scoring;

namespace Radar.Application.Scoring;

/// <summary>
/// The ONE definition of a score series' key (spec 141): a snapshot belongs to the series named by its
/// <see cref="CompanyScoreSnapshot.StrategyName"/>, with <c>null</c>/blank canonicalised to
/// <see cref="ScoringStrategySet.DefaultStrategyName"/>.
/// <para>
/// WHY THE NAME AND NOT THE FINGERPRINT. A strategy is <b>immutable by convention</b> — to change one you add
/// a new named strategy (<c>momentum</c> → <c>momentum-v2</c>), enforced at startup by the fingerprint
/// tripwire. The name is therefore a stable, human-meaningful series key that an unrelated collector toggle
/// cannot move, whereas <c>ScoringConfigVersion</c> is a content hash that shifted 17 times over 851 live
/// snapshots and fragmented the series it was being used to key. The fingerprint remains recorded provenance
/// and a drift detector; it is no longer a primary key.
/// </para>
/// <para>
/// LEGACY <c>null</c> IS THE PRIMARY SERIES, NOT AN ORPHAN. Snapshots written before spec 137 (and any
/// produced outside the strategy composition) carry a null name; the pre-137 composition and the synthesised
/// single-strategy default are the same scoring, so they read as the <c>"default"</c> series and keep
/// comparing against today's primary run rather than being stranded.
/// </para>
/// <para>
/// Comparison is case-INSENSITIVE, matching <see cref="ScoringStrategySet"/>'s case-insensitive uniqueness
/// rule: two names that cannot coexist as distinct strategies must not read as two distinct series. Pure and
/// deterministic (AD-3).
/// </para>
/// <para>
/// THE RETURNED KEY IS THE CANONICAL FORM of that equivalence class — trimmed and invariant-lowercased — so
/// the string uniquely identifies the series. Anything that groups by the key STRING (the efficacy CSV's
/// <c>seriesKey</c> column, a spreadsheet pivot, any downstream consumer) therefore groups exactly as
/// <see cref="SameSeries"/> compares; without the fold, <c>"Momentum"</c> and <c>"momentum"</c> would compare
/// equal yet split into two groups.
/// </para>
/// </summary>
public static class ScoreSeriesKey
{
    /// <summary>The series key of a snapshot: its strategy name, blank/null ⇒ the primary default series.</summary>
    public static string For(CompanyScoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return For(snapshot.StrategyName);
    }

    /// <summary>
    /// The series key of a strategy name: trimmed and invariant-lowercased (the canonical form of the
    /// case-insensitive equivalence class <see cref="SameSeries"/> compares), with blank/null ⇒ the primary
    /// default series. Idempotent, so re-keying an already-canonical key is a no-op.
    /// </summary>
    public static string For(string? strategyName) =>
        string.IsNullOrWhiteSpace(strategyName)
            ? ScoringStrategySet.DefaultStrategyName
            : strategyName.Trim().ToLowerInvariant();

    /// <summary>
    /// True when two snapshots belong to the same series — the comparability gate's rule. Defined in terms of
    /// <see cref="For(string?)"/> so the key and the comparison can never drift apart.
    /// </summary>
    public static bool SameSeries(string? a, string? b) =>
        string.Equals(For(a), For(b), StringComparison.OrdinalIgnoreCase);
}
