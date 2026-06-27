// ============================================================================================
// PROVISIONAL — REPLACEABLE STAND-IN. NOT THE REAL SCORING FORMULA.
//
// PlaceholderScoreFormula exists ONLY so the solution builds and the scoring engine (task 15) can
// be wired and tested end to end. Its component math is the simplest defensible deterministic
// mapping of the inputs — it is NOT tuned, NOT reviewed, and NOT endorsed. Do not read its numbers
// as a considered weighting and do not build product behaviour on its specific scores.
//
// The real IScoreFormula (the weights and the exact computation of the five component scores) is a
// product decision the MAINTAINER OWNS and will drop in later. When they do, only this file should
// change — everything else in Stage 6 depends on the IScoreFormula seam, not on this placeholder.
// ============================================================================================

using System.Text.Json;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Provisional, replaceable <see cref="IScoreFormula"/> stand-in. Pure and deterministic; clamps
/// every component to [0,100]; emits exactly one provenance-carrying contribution per input signal.
/// See the file header: this is NOT the real formula and its numbers are not endorsed.
/// </summary>
public sealed class PlaceholderScoreFormula : IScoreFormula
{
    /// <inheritdoc />
    public string Version => "placeholder-v0";

    /// <inheritdoc />
    public ScoreComputation Compute(ScoringInput input)
    {
        var signals = input.Signals;
        var count = signals.Count;

        // PROVISIONAL placeholder — maintainer to replace with the real formula
        var totalStrength = 0;
        // PROVISIONAL placeholder — maintainer to replace with the real formula
        var positiveCount = 0;

        var contributions = new List<ScoreContribution>(count);
        foreach (var s in signals)
        {
            var signal = s.Signal;

            // PROVISIONAL placeholder — maintainer to replace with the real formula
            totalStrength += signal.Strength;
            // PROVISIONAL placeholder — maintainer to replace with the real formula
            if (signal.Direction == SignalDirection.Positive)
            {
                positiveCount++;
            }

            contributions.Add(new ScoreContribution(
                SignalId: signal.Id,
                EvidenceId: s.Evidence.Id,
                ContributionReason: $"{signal.Type} ({signal.Direction})",
                ContributionWeight: signal.Strength));
        }

        var components = new ScoreComponents(
            // PROVISIONAL placeholder — maintainer to replace with the real formula
            TrajectoryScore: Clamp(totalStrength),
            // PROVISIONAL placeholder — maintainer to replace with the real formula
            OpportunityScore: Clamp(positiveCount * 10),
            // PROVISIONAL placeholder — maintainer to replace with the real formula
            AttentionScore: Clamp(count * 10),
            // PROVISIONAL placeholder — maintainer to replace with the real formula
            EvidenceConfidenceScore: Clamp(contributions.Count * 10),
            // PROVISIONAL placeholder — maintainer to replace with the real formula
            SignalVelocityScore: Clamp(count));

        var explanation = $"placeholder-v0: scored from {count} signal(s).";
        var componentJson = JsonSerializer.Serialize(components);

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);
}
