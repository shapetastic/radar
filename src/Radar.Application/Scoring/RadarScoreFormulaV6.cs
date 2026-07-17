using System.Text.Json;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The maintainer-owned <see cref="IScoreFormula"/> <c>radar-formula-v6</c>: an AD-6 refinement of
/// <c>radar-formula-v5</c> that changes <b>only</b> the Trajectory component to be
/// corroboration/consensus-aware. Every other component (Attention incl. the spec-109 collapsed media set,
/// EvidenceConfidence, SignalVelocity, Opportunity, recency, the empty-window behaviour, the
/// <see cref="ScoringInput.PreviousSignals"/> handling, the direction SIGNS, and the per-signal provenance
/// <see cref="ScoreContribution"/> weights) is <b>byte-for-byte</b> identical to v5.
/// <para>
/// The v5 Trajectory was a confidence/recency-weighted <b>mean</b> of <c>sign·strength</c> over directional
/// signals, so a lone dissenting signal carried weight comparable to each of many corroborating signals: five
/// agreeing customer wins moved Trajectory no more decisively than one, and a single countervailing signal
/// could overturn the read (the live AEHR case, Trajectory 79→68). v6 instead splits the current-window
/// directional signals into a <b>positive mass</b> <c>Mpos = Σ strengthᵢ·wᵢ</c> and a <b>negative mass</b>
/// <c>Mneg = Σ strengthᵢ·wᵢ</c> (each per-signal weight <c>wᵢ = confidenceᵢ·recencyᵢ</c>, exactly as v5;
/// Neutral/Mixed contribute 0 to both, as v5) and combines them as
/// <c>T_raw = TrajectoryBand·(Mpos − Mneg)/(Mpos + Mneg + k)</c>, where <see cref="TrajectoryBand"/> (=10) is
/// the structural strength ceiling / band half-width (the same implicit [-10,10] band v5 used) and <c>k</c> is
/// the config-tunable corroboration-smoothing constant <see cref="ScoringWeights.TrajectoryCorroborationK"/>.
/// The smoothing constant means a small directional set (a lone signal) cannot swing <c>T_raw</c> to an
/// extreme, but a corroborated majority (large mass) can — and an equally-corroborated negative majority
/// swings it down symmetrically. So a corroborated direction is rewarded, an isolated dissenter is
/// damped-but-not-zeroed (the dissent is still recorded), and a corroborated dissenting cluster still bites.
/// The transform is monotone (adding a positive never lowers Trajectory; adding a negative never raises it),
/// direction-symmetric (no positive bias), and an empty directional set still yields the neutral 50 (the
/// <c>0/(0+k)=0</c> fall-through). Supersedes <c>radar-formula-v5</c>.
/// </para>
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
public sealed class RadarScoreFormulaV6 : IScoreFormula
{
    // Direction → sign used in trajectory. These are structural direction SIGNS, not tunable magnitudes
    // (flipping a sign is a structural change, not a weight experiment), so they stay const in the formula.
    private const int DirPositive = +1;
    private const int DirNegative = -1;
    // Neutral and Mixed contribute 0 to direction (see DirectionSign()).

    // The strength ceiling / band half-width that scales the directional preponderance ratio
    // (Mpos−Mneg)/(Mpos+Mneg+k) ∈ [-1,1] into the same implicit [-10,10] band radar-formula-v5 used (v5's
    // trajectory mean of sign·strength was itself bounded by the [0,10] strength ceiling). This is a
    // STRUCTURAL constant — the band's shape, not a tunable magnitude — so it stays const in the formula
    // (like the direction-sign consts). The tunable corroboration-smoothing constant k lives in config.
    private const double TrajectoryBand = 10.0;

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
    public RadarScoreFormulaV6(ScoringWeights weights, IAttentionSourceWeights sourceWeights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        weights.Validate();
        _weights = weights;
        _sourceWeights = sourceWeights;
    }

    /// <inheritdoc />
    public string Version => "radar-formula-v6";

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
                "radar-formula-v6: no signals in window.",
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
        // v6: corroboration/consensus-aware. Split the directional signals into a positive mass and a negative
        // mass (each the confidence·recency·strength sum over that direction; Neutral/Mixed still contribute 0
        // to both, as v5), then combine via the corroboration-smoothing constant k so a corroborated majority
        // is rewarded and a lone dissenter is damped-but-not-zeroed. The per-signal weight w = confidence·recency
        // is byte-identical to v5 (only the AGGREGATION over the signals changed — a preponderance ratio, not a
        // mean of sign·strength).
        var mPos = 0.0;
        var mNeg = 0.0;
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i].Signal;
            var sign = DirectionSign(signal.Direction);
            if (sign == 0)
            {
                continue; // Neutral/Mixed excluded from both masses.
            }

            var w = (double)signal.Confidence * recency[i];
            var mass = signal.Strength * w;
            if (sign > 0)
            {
                mPos += mass;
            }
            else
            {
                mNeg += mass;
            }
        }

        // T_raw = TrajectoryBand·(Mpos − Mneg)/(Mpos + Mneg + k) ∈ [-10, 10]. No directional signals →
        // Mpos == Mneg == 0 → 0 → 50 (the guard keeps v5's sumMass<=0 shape; k>0 makes 0/(0+k)=0 too).
        var sumMass = mPos + mNeg;
        var tRaw = sumMass <= 0
            ? 0
            : TrajectoryBand * (mPos - mNeg) / (sumMass + _weights.TrajectoryCorroborationK);
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
        // (which naturally get weight 0 from DirectionSign). The per-signal contribution weight is unchanged
        // from v5 — provenance is per-signal; the v6 consensus shaping is an AGGREGATE over these signals.
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
            $"radar-formula-v6: {input.Signals.Count} signal(s) over {windowDays}d → " +
            $"Trajectory {trajectoryScore}, Opportunity {opportunityScore} (Attention {attentionScore}, " +
            $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}).";

        var componentJson = JsonSerializer.Serialize(components);

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }
}
