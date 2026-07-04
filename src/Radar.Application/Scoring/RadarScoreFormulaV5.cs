using System.Text.Json;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The maintainer-owned <see cref="IScoreFormula"/> <c>radar-formula-v5</c>: an AD-6 refinement of
/// <c>radar-formula-v4</c> that moves the ~20 tunable magnitude constants OUT of <c>const</c> fields and into
/// an injected, immutable <see cref="ScoringWeights"/> config object. The formula reads every magnitude from
/// <see cref="_weights"/> instead of hardcoded consts, so a tunable-number change is now a <b>config edit</b>
/// (a new/edited <c>Radar:Scoring</c> profile), not a new formula-version class. Only the formula's
/// <b>structure</b> — the component shape, the fixed field ordering, and the direction-sign semantics — stays
/// versioned code; <see cref="ScoringWeights"/> defaults EQUAL the v4 constants, so a blank/absent config
/// yields <b>byte-identical</b> output to v4 (the identity advances v4 → v5 only to mark the structural
/// change: a new injected dependency plus a content-fingerprint stamp). Because runtime-configurable weights
/// mean a hand-typed generation string no longer uniquely determines the score, the engine now stamps a
/// deterministic content fingerprint of the effective resolved config (structure + all weights + tier map)
/// as <c>ScoringConfigVersion</c> (AD-10 amended). Supersedes <c>radar-formula-v4</c>.
/// <para>
/// Pure and deterministic (no clock, no randomness, no I/O; both <see cref="_weights"/> and
/// <see cref="_sourceWeights"/> are immutable lookups); every component clamps to [0,100]. Trajectory
/// excludes zero-direction signals, Attention counts only third-party (market) sources with a tier-weighted
/// distinct-publisher reach, EvidenceConfidence anchors on the strongest signal/quality with a saturating
/// diversity bonus, and SignalVelocity is a smoothed activity ratio. Emits exactly one provenance-carrying
/// contribution per current-window signal, in input order (including Neutral/Mixed, which naturally weigh 0),
/// and never from <see cref="ScoringInput.PreviousSignals"/>.
/// </para>
/// </summary>
public sealed class RadarScoreFormulaV5 : IScoreFormula
{
    // Direction → sign used in trajectory. These are structural direction SIGNS, not tunable magnitudes
    // (flipping a sign is a structural change, not a weight experiment), so they stay const in the formula.
    private const int DirPositive = +1;
    private const int DirNegative = -1;
    // Neutral and Mixed contribute 0 to direction (see DirectionSign()).

    private readonly ScoringWeights _weights;
    private readonly IAttentionSourceWeights _sourceWeights;

    /// <summary>
    /// Constructs the formula with the injected magnitude weights (<see cref="ScoringWeights"/>) and the
    /// per-publisher attention-breadth weights (<see cref="IAttentionSourceWeights"/>). There is
    /// deliberately <b>no</b> parameterless construction: both are config data supplied by Infrastructure
    /// (AD-5). Both must be immutable so the formula stays a pure, deterministic function of
    /// <c>(input, weights, sourceWeights)</c> (AD-3). Fails fast (<see cref="InvalidOperationException"/>)
    /// on a nonsensical weight that would break the math or the [0,100] clamp contract — see
    /// <see cref="ScoringWeights.Validate"/>.
    /// </summary>
    public RadarScoreFormulaV5(ScoringWeights weights, IAttentionSourceWeights sourceWeights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        weights.Validate();
        _weights = weights;
        _sourceWeights = sourceWeights;
    }

    /// <inheritdoc />
    public string Version => "radar-formula-v5";

    private static int DirectionSign(SignalDirection d) => d switch
    {
        SignalDirection.Positive => DirPositive,
        SignalDirection.Negative => DirNegative,
        _ => 0,                       // Neutral and Mixed are direction-neutral
    };

    private double QualityWeight(EvidenceQuality q) => q switch
    {
        EvidenceQuality.PrimarySource => _weights.QualityPrimarySource,
        EvidenceQuality.High          => _weights.QualityHigh,
        EvidenceQuality.Medium        => _weights.QualityMedium,
        EvidenceQuality.Low           => _weights.QualityLow,
        _ => _weights.QualityUnknown, // Unknown (and any unmapped) → QualityUnknown
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
                "radar-formula-v5: no signals in window.",
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

            recency[i] = 1 - _weights.RecencyFloor * age;
        }

        // ---- 1. TrajectoryScore (50 = neutral, >50 improving) ----
        // v2: only Positive/Negative signals contribute to numerator AND denominator. Neutral/Mixed are
        // excluded entirely so they no longer dilute the directional read toward 50.
        var sumW = 0.0;
        var sumTerm = 0.0;
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i].Signal;
            var sign = DirectionSign(signal.Direction);
            if (sign == 0)
            {
                continue; // Neutral/Mixed excluded from both numerator and denominator.
            }

            var w = (double)signal.Confidence * recency[i];
            sumW += w;
            sumTerm += sign * signal.Strength * w;
        }

        var tRaw = sumW <= 0 ? 0 : sumTerm / sumW; // ∈ [-10, 10]; no directional signals → 0 → 50
        var trajectoryScore = Score(_weights.TrajectoryNeutral + _weights.TrajectoryScale * tRaw);

        // ---- 2. AttentionScore (saturating on breadth) ----
        // v2: only third-party (market attention) evidence source names count toward reach; a company's
        // own disclosures (press releases, filings, ...) are not market attention. v4 weights each distinct
        // third-party publisher by its source-quality tier (mills ≈0.1, unknown 0.5, genuine 1.0) instead of
        // counting every distinct publisher as 1, so breadth reflects genuine notice, not mill volume; the
        // half-saturation constant was re-tuned (12→3) for the resulting smaller reach — see field comments.
        var weightedBreadth = signals
            .Where(s => EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType))
            .Select(s => s.Evidence.SourceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(name => _sourceWeights.WeightFor(name));
        var mediaCount = signals.Count(s => s.Signal.Type == SignalType.MediaAttention);
        var reach = weightedBreadth + _weights.MediaReachWeight * mediaCount;
        var attentionScore = Score(100 * reach / (reach + _weights.AttentionHalfSaturation));

        // ---- 3. EvidenceConfidenceScore ----
        // v2: best-anchored + diversity bonus. Anchor on the strongest signal confidence and the highest
        // evidence-quality weight, then apply a saturating diversity multiplier. Adding a weaker
        // signal/lower-quality source can never lower the base, so corroboration is monotonic.
        var bestConf = signals.Max(s => (double)s.Signal.Confidence); // 0..1
        var bestQualWeight = signals.Max(s => QualityWeight(s.Evidence.Quality));
        var distinctTypes = signals.Select(s => s.Evidence.SourceType).Distinct().Count();
        var divFactor = Math.Min(1, distinctTypes / _weights.DiversityTarget);
        var evidenceConfidenceScore = Score(
            100 * bestConf
                * (_weights.EcQualityBase + _weights.EcQualitySpan * bestQualWeight)
                * (_weights.EcDiversityBase + _weights.EcDiversitySpan * divFactor));

        // ---- 4. SignalVelocityScore (50 = steady activity) ---- (unchanged from v1)
        var actNow = signals.Sum(s => s.Signal.Strength);
        var actPrev = input.PreviousSignals.Sum(s => s.Strength);
        var ratio = (actNow + _weights.VelocitySmoothing) / (actPrev + _weights.VelocitySmoothing);
        var signalVelocityScore = Score(_weights.VelocitySteady * ratio);

        // ---- 5. OpportunityScore (multiplicative; uses clamped int components above) ---- (v3 divisor 250)
        var opportunityScore = Score(
            trajectoryScore
            * (evidenceConfidenceScore / 100.0)
            * (1 - attentionScore / _weights.OpportunityAttentionDivisor));

        var components = new ScoreComponents(
            TrajectoryScore: trajectoryScore,
            OpportunityScore: opportunityScore,
            AttentionScore: attentionScore,
            EvidenceConfidenceScore: evidenceConfidenceScore,
            SignalVelocityScore: signalVelocityScore);

        // ---- Contributions (provenance — current window only) ----
        // Still one contribution per current-window signal in input order, including Neutral/Mixed
        // (which naturally get weight 0 from DirectionSign). Provenance is unchanged from v1.
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
            $"radar-formula-v5: {input.Signals.Count} signal(s) over {windowDays}d → " +
            $"Trajectory {trajectoryScore}, Opportunity {opportunityScore} (Attention {attentionScore}, " +
            $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}).";

        var componentJson = JsonSerializer.Serialize(components);

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }
}
