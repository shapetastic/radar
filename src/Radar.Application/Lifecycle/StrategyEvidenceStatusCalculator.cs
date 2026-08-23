using Radar.Application.Efficacy.Claims;
using Radar.Application.Scoring;

namespace Radar.Application.Lifecycle;

/// <summary>
/// The pure, deterministic mapping from persisted efficacy FACTS onto each strategy's
/// <see cref="StrategyEvidenceStatus"/> (spec 184 §1). No clock, no I/O, no randomness (AD-3).
/// <para>
/// Rules, in precedence order per strategy:
/// <list type="number">
/// <item>Gate statuses apply ONLY to the arm the paired artifact judges — the PREDECLARED paired primary
/// with a precommitted boundary. <c>Qualifies</c> ⇒ <see cref="StrategyEvidenceStatusKind.GatePassed"/>;
/// every gate reason being a MERIT code (<c>median-paired-delta-not-positive</c> /
/// <c>interval-lower-bound-not-positive</c>) ⇒ <see cref="StrategyEvidenceStatusKind.GateFailed"/>; any
/// accrual/prerequisite reason ⇒ <see cref="StrategyEvidenceStatusKind.GatePending"/>. Noise is never
/// converted into pass/fail ahead of the precommitted gate: "not enough data yet" is pending, not failed.</item>
/// <item>Otherwise the leaderboard: a ranked row ⇒ <c>Ranked</c> WITH its numbers; a dropped/absent row ⇒
/// <c>Accruing</c> with the drop reason as detail.</item>
/// <item>No readable artifact at all ⇒ <c>Accruing (evidence unavailable)</c> — the display degrades, the
/// arm is never hidden.</item>
/// </list>
/// Descriptive and confirmatory facts stay orthogonal: a gate status carries the leaderboard numbers
/// BESIDE it when they exist, and a CI spanning zero renders as a sentence, never a verdict.
/// </para>
/// </summary>
public static class StrategyEvidenceStatusCalculator
{
    /// <summary>
    /// The two closed spec-170 codes that mean the price gate evaluated ON ITS MERITS and came out
    /// negative. Every other code means the gate could not (yet) evaluate — accrual, missing
    /// predeclaration, or the AD-16 prerequisite — which is pending, never failed.
    /// </summary>
    private static readonly string[] MeritFailureCodes =
    [
        Ad15GateReasonCodes.MedianPairedDeltaNotPositive,
        Ad15GateReasonCodes.IntervalLowerBoundNotPositive,
    ];

    private static readonly string[] NonMeritCodes =
    [
        Ad15GateReasonCodes.NoPredeclaredPrimary,
        Ad15GateReasonCodes.NoPrecommittedBoundary,
        Ad15GateReasonCodes.NoBaselines,
        Ad15GateReasonCodes.EmptyIntersection,
        Ad15GateReasonCodes.NoEligibleBlocks,
        Ad15GateReasonCodes.InsufficientPurgedBlocks,
        Ad15GateReasonCodes.Ad16ScreenNotCalculated,
        Ad15GateReasonCodes.Ad16ScreenUnavailable,
        Ad15GateReasonCodes.Ad16ScreenPending,
        Ad15GateReasonCodes.Ad16ScreenInvalid,
    ];

    /// <summary>Computes every configured strategy's status. Keys are case-insensitive strategy names.</summary>
    public static IReadOnlyDictionary<string, StrategyEvidenceStatus> Compute(
        EfficacyEvidenceFacts facts, IReadOnlyList<ScoringStrategyDefinition> strategies)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(strategies);

        var byName = new Dictionary<string, StrategyEvidenceStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in strategies)
        {
            byName[strategy.Name] = ComputeOne(facts, strategy.Name);
        }

        return byName;
    }

    /// <summary>
    /// The PERSISTED gate verdicts the operating-call reducer consumes (spec 184 §2 rule 1): one per arm
    /// whose status is <c>GatePassed</c>/<c>GateFailed</c>, stamped with the artifact's own write instant.
    /// A pending gate is deliberately NOT a verdict — it must not demote or promote anything.
    /// </summary>
    public static IReadOnlyList<StrategyGateVerdict> GateVerdicts(
        EfficacyEvidenceFacts facts, IReadOnlyList<ScoringStrategyDefinition> strategies)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(strategies);

        var verdicts = new List<StrategyGateVerdict>(1);
        foreach (var strategy in strategies)
        {
            var status = ComputeOne(facts, strategy.Name);
            if (status.Kind == StrategyEvidenceStatusKind.GatePassed)
            {
                verdicts.Add(new StrategyGateVerdict(
                    strategy.Name, Passed: true, facts.Paired!.ArtifactWrittenAtUtc));
            }
            else if (status.Kind == StrategyEvidenceStatusKind.GateFailed)
            {
                verdicts.Add(new StrategyGateVerdict(
                    strategy.Name, Passed: false, facts.Paired!.ArtifactWrittenAtUtc));
            }
        }

        return verdicts;
    }

    private static StrategyEvidenceStatus ComputeOne(EfficacyEvidenceFacts facts, string strategyName)
    {
        var ranked = FindRanked(facts, strategyName, out var dropReason);

        // Gate statuses only for the arm actually under the precommitted confirmatory test.
        if (facts.PairedAvailable
            && facts.Paired is { } paired
            && paired.PrimaryPredeclared
            && paired.BoundaryDeclared
            && string.Equals(paired.PrimaryStrategyName, strategyName, StringComparison.OrdinalIgnoreCase))
        {
            if (paired.Qualifies)
            {
                return StrategyEvidenceStatus.GatePassed(ranked, detail: null);
            }

            var reasons = paired.GateReasons;
            var hasMerit = MeritFailureCodes.Any(c => reasons.Contains(c, StringComparison.Ordinal));
            var hasNonMerit = NonMeritCodes.Any(c => reasons.Contains(c, StringComparison.Ordinal));

            // Failed ONLY when the gate evaluated everywhere and every stated reason is a merit reason.
            // A blank reason list beside a non-qualifying verdict is inconsistent input — pending, never
            // failed (absence of an explanation must not read as a negative result).
            return hasMerit && !hasNonMerit
                ? StrategyEvidenceStatus.GateFailed(ranked, reasons)
                : StrategyEvidenceStatus.GatePending(ranked, reasons.Length > 0 ? reasons : null);
        }

        if (!facts.LeaderboardAvailable)
        {
            return StrategyEvidenceStatus.AccruingEvidenceUnavailable();
        }

        if (ranked is not null)
        {
            return StrategyEvidenceStatus.RankedStatus(ranked);
        }

        return StrategyEvidenceStatus.Accruing(dropReason);
    }

    private static RankedEvidence? FindRanked(
        EfficacyEvidenceFacts facts, string strategyName, out string? dropReason)
    {
        dropReason = null;
        if (!facts.LeaderboardAvailable)
        {
            return null;
        }

        foreach (var row in facts.Leaderboard)
        {
            if (!string.Equals(row.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (row.Ranked && row.Numbers is not null)
            {
                return row.Numbers;
            }

            dropReason = row.DropReason;
            return null;
        }

        return null;
    }
}

/// <summary>
/// A PERSISTED gate verdict for one arm (spec 184 §2 reducer rule 1): the AD-15 composite gate evaluated
/// and the outcome is on disk. <paramref name="VerdictAtUtc"/> is the artifact's write instant — the
/// instant a human override's <c>asOfUtc</c> must post-date to count.
/// </summary>
public sealed record StrategyGateVerdict(string StrategyName, bool Passed, DateTimeOffset VerdictAtUtc);
