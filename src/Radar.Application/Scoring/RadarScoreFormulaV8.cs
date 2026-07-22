using System.Text.Json;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The maintainer-owned <see cref="IScoreFormula"/> <c>radar-formula-v8</c>: an AD-6 refinement of
/// <c>radar-formula-v7</c> that changes <b>only</b> the Attention <i>reach</i> term so the breadth of a
/// spec-109-collapsed media event is preserved. Every other component (the v6 corroboration-aware
/// Trajectory, the v7 following-tier Opportunity discount, EvidenceConfidence, SignalVelocity, recency,
/// the empty-window behaviour, the <see cref="ScoringInput.PreviousSignals"/> handling, the direction
/// SIGNS, and the per-signal provenance <see cref="ScoreContribution"/> weights) is <b>byte-for-byte</b>
/// identical to v7.
/// <para>
/// v7's Attention counted tier-weighted distinct third-party publishers over the signal set it was handed —
/// i.e. AFTER <c>MediaAttentionCollapse</c> (spec 109) had kept ONE representative per same-event bucket.
/// That collapse legitimately removes duplicate media VOLUME, but it also discarded genuine
/// distinct-publisher BREADTH: fifteen different real outlets covering one event expressed the attention of
/// exactly one (spec 124's characterization pinned Attention 10 for a burst versus 78 for the same fifteen
/// outlets spread across distinct events). v8 separates the two concerns:
/// <c>reach = breadthSurvivors + CollapsedBreadthCredit·breadthCollapsedExtra + MediaReachWeight·mediaCount</c>,
/// where <c>breadthCollapsedExtra</c> is the tier-weighted sum over publishers present ONLY in the
/// pre-collapse set (<see cref="ScoringInput.PreCollapseSignals"/>) and <c>mediaCount</c> stays
/// POST-collapse — so loudness/velocity is still collapsed and no raw-volume or time-derivative term is
/// admitted (AD-14 clean; the spec-94 anti-volume posture holds). The credit is tier-weighted, so the
/// anti-mill guard is intact: fifteen mill re-posts of one event add ≈1.5, fifteen genuine outlets add 15.
/// At <see cref="ScoringWeights.CollapsedBreadthCredit"/> = 0.0 the extra term drops out and v8 reproduces
/// <c>radar-formula-v7</c> byte-for-byte; an empty <see cref="ScoringInput.PreCollapseSignals"/> does the
/// same. Supersedes <c>radar-formula-v7</c>.
/// </para>
/// <para>
/// v7's own refinement is carried forward unchanged: the Opportunity discount folds the curated
/// <see cref="ScoringInput.FollowingTier"/> alongside measured attention,
/// <c>followingDiscount = 1 − (attention/OpportunityAttentionDivisor)·OpportunityAttentionDiscountWeight
/// − TierDiscount(tier)·FollowingTierDiscountWeight</c>, and
/// <c>Opportunity = Trajectory · (EvidenceConfidence/100) ·
/// clamp(followingDiscount, OpportunityDiscountFloor, 1)</c>. The tier is CURATED seed metadata
/// (AD-14 — never price/market-cap/volume-derived), and the discount is a graded LEAN, never a filter: the
/// strictly-positive floor means a strong-enough trajectory can still surface a mega-cap. It is monotone —
/// <see cref="ScoringWeights.Validate"/> enforces tier discounts Mega ≥ Large ≥ Mid ≥ Small, so a higher
/// tier never RAISES Opportunity.
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
public sealed class RadarScoreFormulaV8 : IScoreFormula
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
    public RadarScoreFormulaV8(ScoringWeights weights, IAttentionSourceWeights sourceWeights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        weights.Validate();
        _weights = weights;
        _sourceWeights = sourceWeights;
    }

    /// <inheritdoc />
    public string Version => "radar-formula-v8";

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

    // The curated-following discount magnitude for a tier (spec 117). Reads the four config-tunable
    // ScoringWeights magnitudes; Small (and any unmapped value) falls through to the Small discount —
    // the fail-safe "no extra discount" default.
    private double TierDiscount(FollowingTier tier) => tier switch
    {
        FollowingTier.Mega  => _weights.FollowingTierDiscountMega,
        FollowingTier.Large => _weights.FollowingTierDiscountLarge,
        FollowingTier.Mid   => _weights.FollowingTierDiscountMid,
        _ => _weights.FollowingTierDiscountSmall,
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
                "radar-formula-v8: no signals in window.",
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
        //
        // v8 (spec 122) is the ONLY change from v7 and it is confined to this reach block: the survivor
        // breadth below is v7's term unchanged, and it is now joined by breadthCollapsedExtra — the
        // tier-weighted sum over third-party publishers that appear ONLY in the PRE-collapse set, i.e. the
        // distinct outlets the spec-109 same-event collapse dropped. Fifteen genuine outlets covering ONE
        // event are genuine notedness (breadth), which v7 discarded as collateral damage while removing
        // duplicate volume. mediaCount below deliberately stays POST-collapse, so volume/loudness is still
        // collapsed and no velocity term is admitted (AD-14). The extra is tier-weighted exactly like the
        // survivor breadth, so mill re-posts of one event add ≈0.1 each, never 1.0.
        var survivorPublishers = signals
            .Where(s => EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType))
            .Select(s => s.Evidence.SourceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var breadthSurvivors = survivorPublishers.Sum(name => _sourceWeights.WeightFor(name));

        // Publishers present only in the pre-collapse set. An empty PreCollapseSignals (the ScoringInput
        // default, and every caller that does not collapse) yields 0 here — reach is then exactly v7's.
        var breadthCollapsedExtra = input.PreCollapseSignals
            .Where(s => EvidenceSourceTypes.IsThirdPartyAttentionSource(s.Evidence.SourceType))
            .Select(s => s.Evidence.SourceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !survivorPublishers.Contains(name))
            .Sum(name => _sourceWeights.WeightFor(name));

        var mediaCount = signals.Count(s => s.Signal.Type == SignalType.MediaAttention);
        var reach = breadthSurvivors
            + _weights.CollapsedBreadthCredit * breadthCollapsedExtra
            + _weights.MediaReachWeight * mediaCount;
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

        // ---- 5. OpportunityScore (multiplicative; uses clamped int components above) ----
        // v7: the discount folds the curated following tier alongside the measured attention (spec 117).
        // The clamp's strictly-positive floor keeps this a graded lean, never a hard exclusion, and the
        // ceiling 1 means the discount can never become a bonus. At default weights a Small tier reduces
        // this to the v6 term 1 − attention/250 exactly (the clamp is inert there: 0.6 ≤ term ≤ 1).
        var followingDiscount =
            1 - attentionScore / _weights.OpportunityAttentionDivisor * _weights.OpportunityAttentionDiscountWeight
              - TierDiscount(input.FollowingTier) * _weights.FollowingTierDiscountWeight;
        var opportunityScore = Score(
            trajectoryScore
            * (evidenceConfidenceScore / 100.0)
            * Math.Clamp(followingDiscount, _weights.OpportunityDiscountFloor, 1.0));

        var components = new ScoreComponents(
            TrajectoryScore: trajectoryScore,
            OpportunityScore: opportunityScore,
            AttentionScore: attentionScore,
            EvidenceConfidenceScore: evidenceConfidenceScore,
            SignalVelocityScore: signalVelocityScore);

        // ---- Contributions (provenance — current window only) ----
        // Still one contribution per current-window signal in input order, including Neutral/Mixed
        // (which naturally get weight 0 from DirectionSign). The per-signal contribution weight is unchanged
        // from v6 — provenance is per-signal; the consensus shaping and the following discount are AGGREGATE
        // transforms over these signals.
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
            $"radar-formula-v8: {input.Signals.Count} signal(s) over {windowDays}d → " +
            $"Trajectory {trajectoryScore}, Opportunity {opportunityScore} (Attention {attentionScore}, " +
            $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}).";

        var componentJson = JsonSerializer.Serialize(components);

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }
}
