using System.Text.Json;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The maintainer-owned <see cref="IScoreFormula"/> <c>radar-formula-v4</c>: an AD-6 refinement of
/// <c>radar-formula-v3</c> that applies <b>source-quality tiering</b> to the Attention reach breadth and
/// re-tunes the saturation for the resulting smaller distribution. Live data showed the distinct third-party
/// "publishers" driving reach were dominated by algorithmic finance-content mills that cover essentially
/// every ticker, so v3's flat distinct-publisher count measured media-noise breadth, not genuine market
/// notice. v4 replaces the flat count with a <b>tier-weighted distinct-publisher sum</b> via an injected
/// <see cref="IAttentionSourceWeights"/> (content mills contribute little, unknown outlets a conservative
/// default, genuine outlets full), so a name's reach now reflects who actually covered it. Because tiering
/// shrinks reach (a covered name drops from ~20 distinct publishers to ~2–6 genuine-equivalent ones), the
/// v3 half-saturation (12) is re-tuned down to 3 so Attention still spans a useful range. Only
/// <b>Attention</b> (and the Opportunity that consumes it) changes; every other component — Trajectory,
/// EvidenceConfidence, SignalVelocity, the media term (<c>0.25·mediaSignals</c>), the Opportunity discount
/// shape (<c>÷250</c>), recency, clamps, the empty-window behaviour, and the
/// <see cref="ScoringInput.PreviousSignals"/>/window/provenance/contribution rules — is byte-for-byte
/// identical to v3. Pure and deterministic (no clock, no randomness, no I/O; <see cref="_weights"/> is an
/// immutable lookup); every component clamps to [0,100]. Trajectory excludes zero-direction signals,
/// Attention counts only third-party (market) sources, EvidenceConfidence anchors on the strongest
/// signal/quality with a saturating diversity bonus, and SignalVelocity is unchanged. Emits exactly one
/// provenance-carrying contribution per current-window signal, in input order (including Neutral/Mixed,
/// which naturally weigh 0), and never from <see cref="ScoringInput.PreviousSignals"/>. Supersedes
/// <c>radar-formula-v3</c>.
/// </summary>
public sealed class RadarScoreFormulaV4 : IScoreFormula
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

    // Attention saturation: reach / (reach + K) → 0..1. v4 re-tuned the half-saturation point (12→3) for
    // the smaller filtered reach distribution after tier-weighting: once mills are down-weighted a covered
    // name's reach falls to ≈2–6 genuine-equivalent publishers, at which scale the v3 +12 would push everyone
    // back down to near-zero Attention. +3 re-centres the saturation so the filtered covered cluster lands
    // ~40–70 and a thin/mill-only name stays low. MediaAttention signals add a quarter unit of reach: the raw
    // media weight is unchanged from v3 (0.25) — it is a MediaAttention count, not a per-publisher term, so
    // tier weighting does not apply to it.
    private const double AttentionHalfSaturation = 3.0;
    private const double MediaReachWeight        = 0.25;

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

    // Opportunity: v3 softened the under-the-radar discount divisor (200→250) so covered quality names are
    // not uniformly crushed; attention at 100 now costs at most a 40% haircut (100/250), still never zeroes.
    // v4 leaves this shape unchanged — the filtered-and-re-saturated Attention now feeds it, which is the point.
    private const double OpportunityAttentionDivisor = 250.0;

    private readonly IAttentionSourceWeights _weights;

    /// <summary>
    /// Constructs the formula with the per-publisher attention-breadth weights it applies to the reach
    /// term. There is deliberately <b>no</b> parameterless construction: the tier policy is config data
    /// supplied by Infrastructure (AD-5). <paramref name="weights"/> must be an immutable lookup so the
    /// formula stays a pure, deterministic function of <c>(input, weights)</c> (AD-3).
    /// </summary>
    public RadarScoreFormulaV4(IAttentionSourceWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        _weights = weights;
    }

    /// <inheritdoc />
    public string Version => "radar-formula-v4";

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
                "radar-formula-v4: no signals in window.",
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
        var trajectoryScore = Score(TrajectoryNeutral + TrajectoryScale * tRaw);

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
            .Sum(name => _weights.WeightFor(name));
        var mediaCount = signals.Count(s => s.Signal.Type == SignalType.MediaAttention);
        var reach = weightedBreadth + MediaReachWeight * mediaCount;
        var attentionScore = Score(100 * reach / (reach + AttentionHalfSaturation));

        // ---- 3. EvidenceConfidenceScore ----
        // v2: best-anchored + diversity bonus. Anchor on the strongest signal confidence and the highest
        // evidence-quality weight, then apply a saturating diversity multiplier. Adding a weaker
        // signal/lower-quality source can never lower the base, so corroboration is monotonic.
        var bestConf = signals.Max(s => (double)s.Signal.Confidence); // 0..1
        var bestQualWeight = signals.Max(s => QualityWeight(s.Evidence.Quality));
        var distinctTypes = signals.Select(s => s.Evidence.SourceType).Distinct().Count();
        var divFactor = Math.Min(1, distinctTypes / DiversityTarget);
        var evidenceConfidenceScore = Score(
            100 * bestConf
                * (EcQualityBase + EcQualitySpan * bestQualWeight)
                * (EcDiversityBase + EcDiversitySpan * divFactor));

        // ---- 4. SignalVelocityScore (50 = steady activity) ---- (unchanged from v1)
        var actNow = signals.Sum(s => s.Signal.Strength);
        var actPrev = input.PreviousSignals.Sum(s => s.Strength);
        var ratio = (actNow + VelocitySmoothing) / (actPrev + VelocitySmoothing);
        var signalVelocityScore = Score(VelocitySteady * ratio);

        // ---- 5. OpportunityScore (multiplicative; uses clamped int components above) ---- (v3 divisor 250)
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
            $"radar-formula-v4: {input.Signals.Count} signal(s) over {windowDays}d → " +
            $"Trajectory {trajectoryScore}, Opportunity {opportunityScore} (Attention {attentionScore}, " +
            $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}).";

        var componentJson = JsonSerializer.Serialize(components);

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }
}
