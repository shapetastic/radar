using System.Text.Json;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The first real, maintainer-owned <see cref="IScoreFormula"/>: <c>radar-formula-v1</c>. Pure and
/// deterministic (no clock, no randomness, no I/O); every component clamps to [0,100]. All tunable
/// numbers are named <c>private const</c> fields below — simple, visible, versioned. The five
/// component computations were designed and approved by the maintainer; this class transcribes them
/// exactly. Emits exactly one provenance-carrying contribution per current-window signal, in input
/// order, and never from <see cref="ScoringInput.PreviousSignals"/>.
/// </summary>
public sealed class RadarScoreFormulaV1 : IScoreFormula
{
    // Direction → sign used in trajectory.
    private const int DirPositive = +1;
    private const int DirNegative = -1;
    // Neutral and Mixed contribute 0 to direction (see DirectionSign()).

    // Recency weighting within the current window: newest signal counts 1.0, oldest counts RecencyFloor.
    private const double RecencyFloor = 0.5;

    // Trajectory mapping: neutral midpoint and scale (T_raw ∈ [-10,10] → 0..100).
    private const double TrajectoryNeutral = 50.0;
    private const double TrajectoryScale   = 5.0;

    // Attention saturation: reach / (reach + K) → 0..1. MediaAttention signals add half a unit of reach.
    private const double AttentionHalfSaturation = 5.0;
    private const double MediaReachWeight        = 0.5;

    // EvidenceConfidence quality weights (by EvidenceQuality).
    private const double QualPrimarySource = 1.00;
    private const double QualHigh          = 0.85;
    private const double QualMedium        = 0.60;
    private const double QualLow           = 0.35;
    private const double QualUnknown       = 0.40;

    // EvidenceConfidence blend: each adjustment has a floor + span so it discounts but never zeroes.
    private const double EcQualityBase  = 0.60;
    private const double EcQualitySpan  = 0.40;   // base+span = 1.0 at best quality
    private const double EcDiversityBase = 0.70;
    private const double EcDiversitySpan = 0.30;  // base+span = 1.0 at full diversity
    private const double DiversityTarget = 3.0;   // distinct source types at/above which diversity is maxed

    // Velocity: 50 * (now+λ)/(prev+λ). λ smooths low-activity ratios.
    private const double VelocitySmoothing = 10.0;
    private const double VelocitySteady    = 50.0;

    // Opportunity: attention at 100 halves the score (divisor 200), never zeroes it.
    private const double OpportunityAttentionDivisor = 200.0;

    /// <inheritdoc />
    public string Version => "radar-formula-v1";

    private static int DirectionSign(SignalDirection d) => d switch
    {
        SignalDirection.Positive => DirPositive,
        SignalDirection.Negative => DirNegative,
        _ => 0,                       // Neutral and Mixed are direction-neutral
    };

    private static double QualityWeight(EvidenceQuality q) => q switch
    {
        EvidenceQuality.PrimarySource => QualPrimarySource,
        EvidenceQuality.High          => QualHigh,
        EvidenceQuality.Medium        => QualMedium,
        EvidenceQuality.Low           => QualLow,
        _ => QualUnknown,             // Unknown (and any unmapped) → QualUnknown
    };

    // Clamp+round any double component to an int in [0,100], deterministic midpoint handling.
    private static int Score(double v) =>
        Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 100);

    /// <inheritdoc />
    public ScoreComputation Compute(ScoringInput input)
    {
        var signals = input.Signals;

        if (signals.Count == 0)
        {
            var emptyComponents = new ScoreComponents(0, 0, 0, 0, 0);
            return new ScoreComputation(
                emptyComponents,
                "radar-formula-v1: no signals in window.",
                JsonSerializer.Serialize(emptyComponents),
                new List<ScoreContribution>());
        }

        var windowLength = input.WindowEndUtc - input.WindowStartUtc;
        var hasPositiveWindow = windowLength > TimeSpan.Zero;

        // Per-signal recency factors (current window only), aligned with input order.
        var recency = new double[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            double age;
            if (hasPositiveWindow)
            {
                age = (input.WindowEndUtc - signals[i].Signal.ObservedAtUtc).TotalSeconds
                      / windowLength.TotalSeconds;
                age = Math.Clamp(age, 0, 1);
            }
            else
            {
                age = 0; // divide-by-zero guard: recency 1.0 for all
            }

            recency[i] = 1 - RecencyFloor * age;
        }

        // ---- 1. TrajectoryScore (50 = neutral, >50 improving) ----
        var sumW = 0.0;
        var sumTerm = 0.0;
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i].Signal;
            var w = (double)signal.Confidence * recency[i];
            var term = DirectionSign(signal.Direction) * signal.Strength * w;
            sumW += w;
            sumTerm += term;
        }

        var tRaw = sumW <= 0 ? 0 : sumTerm / sumW; // ∈ [-10, 10]
        var trajectoryScore = Score(TrajectoryNeutral + TrajectoryScale * tRaw);

        // ---- 2. AttentionScore (saturating on breadth) ----
        var distinctSources = signals
            .Select(s => s.Evidence.SourceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var mediaCount = signals.Count(s => s.Signal.Type == SignalType.MediaAttention);
        var reach = distinctSources + MediaReachWeight * mediaCount;
        var attentionScore = Score(100 * reach / (reach + AttentionHalfSaturation));

        // ---- 3. EvidenceConfidenceScore ----
        var avgConf = signals.Average(s => (double)s.Signal.Confidence); // 0..1
        var qualFactor = signals.Average(s => QualityWeight(s.Evidence.Quality));
        var distinctTypes = signals.Select(s => s.Evidence.SourceType).Distinct().Count();
        var divFactor = Math.Min(1, distinctTypes / DiversityTarget);
        var evidenceConfidenceScore = Score(
            100 * avgConf
                * (EcQualityBase + EcQualitySpan * qualFactor)
                * (EcDiversityBase + EcDiversitySpan * divFactor));

        // ---- 4. SignalVelocityScore (50 = steady activity) ----
        var actNow = signals.Sum(s => s.Signal.Strength);
        var actPrev = input.PreviousSignals.Sum(s => s.Strength);
        var ratio = (actNow + VelocitySmoothing) / (actPrev + VelocitySmoothing);
        var signalVelocityScore = Score(VelocitySteady * ratio);

        // ---- 5. OpportunityScore (multiplicative; uses clamped int components above) ----
        var opportunityScore = Score(
            trajectoryScore
            * (evidenceConfidenceScore / 100.0)
            * (1 - attentionScore / OpportunityAttentionDivisor));

        var components = new ScoreComponents(
            TrajectoryScore: trajectoryScore,
            OpportunityScore: opportunityScore,
            AttentionScore: attentionScore,
            EvidenceConfidenceScore: evidenceConfidenceScore,
            SignalVelocityScore: signalVelocityScore);

        // ---- Contributions (provenance — current window only) ----
        var contributions = new List<ScoreContribution>(signals.Count);
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i].Signal;
            var w = (double)signal.Confidence * recency[i];
            var weight = (int)Math.Round(
                DirectionSign(signal.Direction) * signal.Strength * w,
                MidpointRounding.AwayFromZero);

            contributions.Add(new ScoreContribution(
                SignalId: signal.Id,
                EvidenceId: signals[i].Evidence.Id,
                ContributionReason:
                    $"{signal.Type} ({signal.Direction}), strength {signal.Strength}, confidence {signal.Confidence:0.00}",
                ContributionWeight: weight));
        }

        var windowDays = (int)Math.Round(windowLength.TotalDays, MidpointRounding.AwayFromZero);
        var explanation =
            $"radar-formula-v1: {input.Signals.Count} signal(s) over {windowDays}d → " +
            $"Trajectory {trajectoryScore}, Opportunity {opportunityScore} (Attention {attentionScore}, " +
            $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}).";

        var componentJson = JsonSerializer.Serialize(components);

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }
}
