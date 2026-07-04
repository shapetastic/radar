using System.Text.Json;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The maintainer-owned <see cref="IScoreFormula"/> <c>radar-formula-v3</c>: an AD-6 refinement of
/// <c>radar-formula-v2</c> that re-tunes the attention saturation and the under-the-radar discount so
/// Attention separates under-followed from widely-covered names again. Live data showed the v2 Attention
/// saturating at 76–85 across almost the whole board (the small <c>+5</c> half-saturation maxed out at
/// normal coverage), and the <c>(1 − Attention/200)</c> discount haircutting a near-uniform ~40% that
/// compressed the board and penalised the most-covered quality names. v3 raises the half-saturation
/// (5→12), down-weights the raw media term (0.5→0.25 — distinct-publisher breadth is the cleaner signal),
/// and softens the discount divisor (200→250). Only <b>Attention</b> and <b>Opportunity</b> change; every
/// other component is byte-for-byte identical to v2. Pure and deterministic (no clock, no randomness, no
/// I/O); every component clamps to [0,100]. Trajectory excludes zero-direction signals, Attention counts
/// only third-party (market) sources, EvidenceConfidence anchors on the strongest signal/quality with a
/// saturating diversity bonus, and SignalVelocity is unchanged. Emits exactly one provenance-carrying
/// contribution per current-window signal, in input order (including Neutral/Mixed, which naturally weigh
/// 0), and never from <see cref="ScoringInput.PreviousSignals"/>.
/// </summary>
public sealed class RadarScoreFormulaV3 : IScoreFormula
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

    // Attention saturation: reach / (reach + K) → 0..1. v3 raised the half-saturation point (5→12) so
    // normal coverage (reach ≈ 16–28) no longer saturates at 76–85; the covered cluster now lands ~57–70
    // and thinly-covered names stay low, restoring the discriminator. MediaAttention signals add a quarter
    // unit of reach: v3 halved the raw media weight (0.5→0.25) because one event routinely spawns many
    // near-duplicate articles, so raw media volume is duplication-prone — lean on distinct-publisher breadth.
    private const double AttentionHalfSaturation = 12.0;
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
    private const double OpportunityAttentionDivisor = 250.0;

    /// <inheritdoc />
    public string Version => "radar-formula-v3";

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
                "radar-formula-v3: no signals in window.",
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
        // own disclosures (press releases, filings, ...) are not market attention. v3 raised the
        // half-saturation constant (12) and down-weighted the raw media term (0.25) — see field comments.
        var distinctThirdPartySources = signals
            .Where(s => EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType))
            .Select(s => s.Evidence.SourceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var mediaCount = signals.Count(s => s.Signal.Type == SignalType.MediaAttention);
        var reach = distinctThirdPartySources + MediaReachWeight * mediaCount;
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
            $"radar-formula-v3: {input.Signals.Count} signal(s) over {windowDays}d → " +
            $"Trajectory {trajectoryScore}, Opportunity {opportunityScore} (Attention {attentionScore}, " +
            $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}).";

        var componentJson = JsonSerializer.Serialize(components);

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }
}
