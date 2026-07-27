using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The per-signal scoring PRIMITIVES shared by every <c>radar-formula-vN</c> — recency, direction sign,
/// evidence-quality weight, the directional positive/negative masses and their preponderance ratio, the
/// tier-weighted distinct-publisher attention reach, and (spec 149) the notedness/following discount.
/// <para>
/// EXTRACTED FROM <see cref="RadarScoreFormulaV8"/>, NOT COPIED (spec 146, CLAUDE.md reuse-over-copy).
/// <c>radar-formula-v9</c> composes a strategy's score from per-CHANNEL sub-scores, and every one of those
/// sub-scores needs exactly the machinery v8 already had. Pasting a second copy would let the two drift on
/// the next recency/quality/direction fix, and the whole point of v9 is that only the SET a term is computed
/// over changes — the direction, confidence, strength and recency semantics must be identical by
/// construction. v8 is routed through this type and stays byte-for-byte identical in output; its pinned
/// characterization tests and the pinned <c>ScoringConfigVersion</c> fingerprints are the guard.
/// </para>
/// <para>
/// FLOATING-POINT EXACTNESS IS PART OF THE CONTRACT. Every helper preserves v8's original expression shape
/// and accumulation order, because IEEE-754 multiplication/division is not associative: rewriting
/// <c>band * (p - n) / (t + k)</c> as <c>band * ((p - n) / (t + k))</c> can move the result by an ULP, which
/// can flip a midpoint <see cref="Clamp0To100"/> rounding. That is why <see cref="Preponderance"/> takes the
/// <c>band</c> as a parameter rather than returning a raw ratio the caller then scales.
/// </para>
/// <para>Pure and deterministic: no clock, no randomness, no I/O (AD-3).</para>
/// </summary>
public static class ScoreSignalMath
{
    // Direction → sign. These are structural direction SIGNS, not tunable magnitudes (flipping a sign is a
    // structural change, not a weight experiment), so they stay const here rather than moving into config.
    private const int DirPositive = +1;
    private const int DirNegative = -1;

    /// <summary>
    /// The direction sign used by every directional term: <c>+1</c> Positive, <c>-1</c> Negative, and
    /// <c>0</c> for Neutral/Mixed (which are therefore excluded from both directional masses and weigh 0 in
    /// a per-signal contribution).
    /// </summary>
    public static int DirectionSign(SignalDirection direction) => direction switch
    {
        SignalDirection.Positive => DirPositive,
        SignalDirection.Negative => DirNegative,
        _ => 0,                       // Neutral and Mixed are direction-neutral
    };

    /// <summary>
    /// The configured weight of an <see cref="EvidenceQuality"/>. Unknown (and any unmapped value) maps to
    /// <see cref="ScoringWeights.QualityUnknown"/>, which sits BELOW Medium/High/PrimarySource by default, so
    /// unrecoverable quality never flatters a score.
    /// </summary>
    public static double QualityWeight(ScoringWeights weights, EvidenceQuality quality)
    {
        ArgumentNullException.ThrowIfNull(weights);

        return quality switch
        {
            EvidenceQuality.PrimarySource => weights.QualityPrimarySource,
            EvidenceQuality.High          => weights.QualityHigh,
            EvidenceQuality.Medium        => weights.QualityMedium,
            EvidenceQuality.Low           => weights.QualityLow,
            _ => weights.QualityUnknown,  // Unknown (and any unmapped) → QualityUnknown
        };
    }

    /// <summary>
    /// Clamp+round a double component to an int in <c>[0,100]</c> with deterministic midpoint handling —
    /// the range contract every <see cref="ScoreComponents"/> member obeys.
    /// </summary>
    public static int Clamp0To100(double value) =>
        Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);

    /// <summary>
    /// Per-signal recency factors for the current window, aligned with <paramref name="signals"/>' input
    /// order: <c>1 − recencyFloor·age</c> where <c>age</c> is the signal's fractional age within the window,
    /// clamped to <c>[0,1]</c>. A non-positive window degrades to <c>age = 0</c> (recency 1.0 for all) — the
    /// divide-by-zero guard.
    /// </summary>
    public static double[] RecencyFactors(
        IReadOnlyList<ScoringSignal> signals,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        double recencyFloor)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var windowLength = windowEndUtc - windowStartUtc;
        var hasPositiveWindow = windowLength > TimeSpan.Zero;

        var recency = new double[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            double age;
            if (hasPositiveWindow)
            {
                age = (windowEndUtc - signals[i].Signal.ObservedAtUtc).TotalSeconds
                      / windowLength.TotalSeconds;
                age = Math.Clamp(age, 0, 1);
            }
            else
            {
                age = 0; // divide-by-zero guard: recency 1.0 for all
            }

            recency[i] = 1 - recencyFloor * age;
        }

        return recency;
    }

    /// <summary>
    /// The per-signal evidence-quality factors aligned with <paramref name="signals"/>' input order — the
    /// companion array <see cref="DirectionalMasses"/> and <see cref="ActivityMass"/> accept so a caller does
    /// not re-derive the mapping per term.
    /// </summary>
    public static double[] QualityFactors(IReadOnlyList<ScoringSignal> signals, ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(weights);

        var factors = new double[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            factors[i] = QualityWeight(weights, signals[i].Evidence.Quality);
        }

        return factors;
    }

    /// <summary>
    /// Splits the signal set into a POSITIVE and a NEGATIVE directional mass — each the
    /// <c>strength · confidence · recency</c> sum over that direction, optionally scaled per signal by
    /// <paramref name="qualityFactors"/>. Neutral/Mixed contribute to neither.
    /// <para>
    /// <paramref name="qualityFactors"/> is <c>null</c> for <c>radar-formula-v8</c>'s Trajectory (whose
    /// per-signal weight is <c>confidence · recency</c> alone, unchanged since v5) and non-null for
    /// <c>radar-formula-v9</c>'s per-channel direction factor, where evidence quality shapes how much a
    /// channel's signals count. Passing <c>null</c> skips the multiplication entirely rather than
    /// multiplying by 1.0, so v8's arithmetic is untouched even at ULP resolution.
    /// </para>
    /// </summary>
    public static DirectionalMass DirectionalMasses(
        IReadOnlyList<ScoringSignal> signals,
        IReadOnlyList<double> recency,
        IReadOnlyList<double>? qualityFactors = null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(recency);

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
            if (qualityFactors is not null)
            {
                mass *= qualityFactors[i];
            }

            if (sign > 0)
            {
                mPos += mass;
            }
            else
            {
                mNeg += mass;
            }
        }

        return new DirectionalMass(mPos, mNeg);
    }

    /// <summary>
    /// The total <c>strength · confidence · recency · quality</c> mass over EVERY signal in the set,
    /// including Neutral/Mixed. Distinct from <see cref="DirectionalMasses"/> on purpose: a Neutral signal is
    /// still ACTIVITY on a channel (something happened there) even though it says nothing about direction, so
    /// <c>radar-formula-v9</c>'s channel saturation counts it while the channel's direction factor does not.
    /// </summary>
    public static double ActivityMass(
        IReadOnlyList<ScoringSignal> signals,
        IReadOnlyList<double> recency,
        IReadOnlyList<double> qualityFactors)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(recency);
        ArgumentNullException.ThrowIfNull(qualityFactors);

        var total = 0.0;
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i].Signal;
            var w = (double)signal.Confidence * recency[i];
            total += signal.Strength * w * qualityFactors[i];
        }

        return total;
    }

    /// <summary>
    /// The corroboration-smoothed directional preponderance,
    /// <c>band · (Mpos − Mneg) / (Mpos + Mneg + k)</c>, which lies in <c>[-band, band]</c> and is exactly
    /// <c>0</c> when there is no directional mass at all (no directional signals ⇒ the neutral answer, and
    /// <c>k &gt; 0</c> makes <c>0/(0+k) = 0</c> anyway — the guard keeps v5's <c>sumMass &lt;= 0</c> shape).
    /// <para>
    /// <paramref name="band"/> scales the ratio into the formula's band: <c>radar-formula-v8</c> passes its
    /// structural <c>TrajectoryBand</c> (10) to land Trajectory in the implicit <c>[-10,10]</c> band v5 used;
    /// <c>radar-formula-v9</c> passes <c>1.0</c> to get the raw ratio in <c>[-1,1]</c>. It is a PARAMETER
    /// rather than a caller-side multiply so v8's expression shape — and therefore its last bit — is
    /// preserved (see the type remarks).
    /// </para>
    /// </summary>
    public static double Preponderance(DirectionalMass mass, double corroborationK, double band)
    {
        var sumMass = mass.Total;
        return sumMass <= 0
            ? 0
            : band * (mass.Positive - mass.Negative) / (sumMass + corroborationK);
    }

    /// <summary>
    /// True when this signal's evidence names a THIRD-PARTY (market attention) publisher, i.e. when it can
    /// contribute to the breadth term. A company's own disclosures (press releases, filings, …) are not
    /// market attention, and a blank publisher name names nobody.
    /// </summary>
    public static bool IsBreadthPublisher(ScoringSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return EvidenceSourceTypes.IsThirdPartyAttentionSource(signal.Evidence.SourceType)
            && !string.IsNullOrWhiteSpace(signal.Evidence.SourceName);
    }

    /// <summary>
    /// True when this signal contributes to <see cref="AttentionReach"/> at all — either as a third-party
    /// publisher (the breadth term) or as a <see cref="SignalType.MediaAttention"/> signal (the media term).
    /// Used for per-signal ATTRIBUTION only (which signals a <c>radar-formula-v9</c> breadth channel actually
    /// consumed); the reach VALUE still comes from <see cref="AttentionReach"/>.
    /// </summary>
    public static bool ContributesToReach(ScoringSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return IsBreadthPublisher(signal) || signal.Signal.Type == SignalType.MediaAttention;
    }

    /// <summary>
    /// The distinct THIRD-PARTY (market attention) publisher names in the set, compared case-insensitively.
    /// A company's own disclosures (press releases, filings, …) are not market attention and are excluded;
    /// blank publisher names are dropped.
    /// </summary>
    public static HashSet<string> DistinctThirdPartyPublishers(IEnumerable<ScoringSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        return signals
            .Where(IsBreadthPublisher)
            .Select(s => s.Evidence.SourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tier-weighted sum over a distinct-publisher set: each publisher counts as its source-quality tier
    /// weight (mills ≈0.1, unknown 0.5, genuine 1.0) rather than as 1, so breadth reflects genuine notice and
    /// not mill volume. Enumeration order is the caller's, and addition over a set of non-negative weights is
    /// what it is — the caller hands the same collection v8 handed it.
    /// </summary>
    public static double TierWeightedReach(
        IEnumerable<string> publishers, IAttentionSourceWeights sourceWeights)
    {
        ArgumentNullException.ThrowIfNull(publishers);
        ArgumentNullException.ThrowIfNull(sourceWeights);

        return publishers.Sum(sourceWeights.WeightFor);
    }

    /// <summary>
    /// The full attention REACH term, byte-identically as <c>radar-formula-v8</c> computes it (spec 122):
    /// <c>reach = breadthSurvivors + CollapsedBreadthCredit·breadthCollapsedExtra + MediaReachWeight·mediaCount</c>.
    /// <list type="bullet">
    /// <item><c>breadthSurvivors</c> — the tier-weighted distinct third-party publishers in the
    /// POST-collapse set;</item>
    /// <item><c>breadthCollapsedExtra</c> — the tier-weighted publishers present ONLY in
    /// <paramref name="preCollapseSignals"/>, i.e. the distinct outlets the spec-109 same-event collapse
    /// dropped. An empty pre-collapse set yields 0, which reproduces <c>radar-formula-v7</c>'s reach;</item>
    /// <item><c>mediaCount</c> — deliberately POST-collapse, so loudness/volume stays collapsed and no
    /// raw-volume or time-derivative term is admitted (AD-14 clean).</item>
    /// </list>
    /// <para>
    /// <c>radar-formula-v9</c> reuses this whole term as its BREADTH channel's raw input, so "breadth" means
    /// the same measured thing in both formulas — only how it enters the composite differs (v8 discounts
    /// Opportunity by attention; v9 gives breadth its own positively-weighted channel AND, since spec 149,
    /// damps the composed score by <see cref="NotednessDiscount"/> — so in v9 attention enters twice, with
    /// opposite signs and different meanings).
    /// </para>
    /// </summary>
    public static double AttentionReach(
        IReadOnlyList<ScoringSignal> signals,
        IReadOnlyList<ScoringSignal> preCollapseSignals,
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(preCollapseSignals);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);

        var survivorPublishers = DistinctThirdPartyPublishers(signals);
        var breadthSurvivors = TierWeightedReach(survivorPublishers, sourceWeights);

        var breadthCollapsedExtra = preCollapseSignals
            .Where(IsBreadthPublisher)
            .Select(s => s.Evidence.SourceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !survivorPublishers.Contains(name))
            .Sum(sourceWeights.WeightFor);

        var mediaCount = signals.Count(s => s.Signal.Type == SignalType.MediaAttention);
        return breadthSurvivors
            + weights.CollapsedBreadthCredit * breadthCollapsedExtra
            + weights.MediaReachWeight * mediaCount;
    }

    /// <summary>
    /// The curated-following discount magnitude for a tier (spec 117). Reads the four config-tunable
    /// <see cref="ScoringWeights"/> magnitudes; <see cref="FollowingTier.Small"/> — and any unmapped value —
    /// falls through to the Small discount, the fail-safe "no extra discount" default.
    /// <para>
    /// The tier is CURATED seed metadata (AD-14 — never price/market-cap/volume-derived).
    /// <see cref="ScoringWeights.Validate"/> enforces the discounts monotone Mega ≥ Large ≥ Mid ≥ Small, so a
    /// higher tier can never be discounted LESS than a lower one.
    /// </para>
    /// </summary>
    public static double TierDiscount(ScoringWeights weights, FollowingTier tier)
    {
        ArgumentNullException.ThrowIfNull(weights);

        return tier switch
        {
            FollowingTier.Mega  => weights.FollowingTierDiscountMega,
            FollowingTier.Large => weights.FollowingTierDiscountLarge,
            FollowingTier.Mid   => weights.FollowingTierDiscountMid,
            _ => weights.FollowingTierDiscountSmall,
        };
    }

    /// <summary>
    /// THE NOTEDNESS DISCOUNT — the multiplicative factor by which "already noticed" damps a company's
    /// headline score, folding MEASURED attention together with the CURATED following tier (spec 117):
    /// <c>clamp(1 − (attention/OpportunityAttentionDivisor)·OpportunityAttentionDiscountWeight
    /// − TierDiscount(tier)·FollowingTierDiscountWeight, OpportunityDiscountFloor, 1)</c>.
    /// <para>
    /// EXTRACTED FROM <see cref="RadarScoreFormulaV8"/> BY SPEC 149, NOT COPIED. <c>radar-formula-v9</c>
    /// shipped with zero references to it, so a v9 strategy ranked on raw channel activity — largely a size
    /// proxy, close to the inverse of Radar's stated purpose (surface companies BEFORE the market notices).
    /// Both formulas now read the same knobs through this one implementation, so the two differ in
    /// COMPOSITION — where the discount is applied — and never in what notedness MEANS.
    /// </para>
    /// <para>
    /// It is a graded LEAN, never a filter: the strictly-positive
    /// <see cref="ScoringWeights.OpportunityDiscountFloor"/> means a strong-enough trajectory can still
    /// surface a mega-cap, and the ceiling of 1 means the discount can never become a bonus. Setting BOTH
    /// <see cref="ScoringWeights.OpportunityAttentionDiscountWeight"/> and
    /// <see cref="ScoringWeights.FollowingTierDiscountWeight"/> to 0 makes this return EXACTLY <c>1.0</c>
    /// (both subtracted terms are a finite value times zero, and the default floor 0.05 ≤ 1), which is how a
    /// strategy opts out — and multiplying by exactly 1.0 is the IEEE-754 identity, so an opted-out strategy
    /// is bit-for-bit undiscounted rather than approximately so.
    /// </para>
    /// <para>
    /// <paramref name="attentionScore"/> is the CLAMPED INT attention component, not a raw reach: v8 has
    /// always fed the rounded [0,100] component here, and v9 feeds the same one, so "how noticed is this
    /// company" is one number in both formulas.
    /// </para>
    /// </summary>
    public static double NotednessDiscount(
        ScoringWeights weights, int attentionScore, FollowingTier tier)
    {
        ArgumentNullException.ThrowIfNull(weights);

        // v8's ORIGINAL expression shape and accumulation order, moved verbatim (see the type remarks on
        // floating-point exactness). Do not re-associate or factor these terms: IEEE-754 arithmetic is not
        // associative and a 1-ULP move can flip a midpoint Clamp0To100 rounding.
        var followingDiscount =
            1 - attentionScore / weights.OpportunityAttentionDivisor * weights.OpportunityAttentionDiscountWeight
              - TierDiscount(weights, tier) * weights.FollowingTierDiscountWeight;

        return Math.Clamp(followingDiscount, weights.OpportunityDiscountFloor, 1.0);
    }
}

/// <summary>
/// The directional split of a signal set: the summed POSITIVE and NEGATIVE
/// <c>strength · confidence · recency [· quality]</c> masses. Neutral/Mixed signals are in neither.
/// </summary>
public readonly record struct DirectionalMass(double Positive, double Negative)
{
    /// <summary>The combined directional mass, <c>Positive + Negative</c> — the preponderance denominator.</summary>
    public double Total => Positive + Negative;
}
