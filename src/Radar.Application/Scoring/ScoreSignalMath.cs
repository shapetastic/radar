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
/// <para>
/// SPEC 153 MOVED FOUR WHOLE COMPONENT BLOCKS ON THE SAME TERMS (the audit's M3, deferred by spec 148 and
/// closed here). <c>radar-formula-v9</c> carried VERBATIM COPIES of v8's Trajectory, Attention,
/// EvidenceConfidence and SignalVelocity blocks; adding <c>radar-formula-v10</c> would have made a THIRD copy
/// of each, so they are extracted instead — see <see cref="TrajectoryScore"/>,
/// <see cref="AttentionComponent"/>, <see cref="EvidenceConfidenceScore"/> and
/// <see cref="SignalVelocityScore"/>, plus the <see cref="Saturate"/> shape v9 held privately. All three
/// formulas route through them and every expression shape is preserved verbatim, which
/// <c>ScoringOutputStabilityTests</c> (v8) and <c>RadarScoreFormulaV9OutputStabilityTests</c> (v9) pin.
/// </para>
/// <para>
/// ⚠ ONE OBSERVATION RECORDED RATHER THAN FIXED (spec 153 §3). v8's Trajectory lands an ALL-NEUTRAL company
/// at <see cref="ScoringWeights.TrajectoryNeutral"/> (50) — mid-scale rather than zero — because
/// <see cref="Preponderance"/> is exactly 0 with no directional mass and Trajectory is centred on 50 by
/// design. That is the same "no directional evidence reads as a middling positive" property spec 153
/// removes from v10's channel scores. <b>v8 is deliberately NOT changed</b>: it is the established baseline
/// and the control for every strategy comparison, so fixing it (if it needs fixing) is a separate, deliberate
/// decision with its own <c>radar-formula-vN</c>.
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
    /// The strength ceiling / band half-width that scales the directional preponderance ratio
    /// <c>(Mpos−Mneg)/(Mpos+Mneg+k) ∈ [-1,1]</c> into the implicit <c>[-10,10]</c> band
    /// <c>radar-formula-v5</c> used (v5's trajectory mean of <c>sign·strength</c> was itself bounded by the
    /// <c>[0,10]</c> strength ceiling). STRUCTURAL — the band's shape, not a tunable magnitude — so it stays a
    /// const here rather than moving into config, exactly as it did when it lived in the formula classes.
    /// </summary>
    private const double TrajectoryBand = 10.0;

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
    /// The half-saturation shape <c>x/(x+S)</c>, in <c>[0,1)</c> for non-negative <c>x</c> — the one
    /// definition of "how much of this raw magnitude counts as a full share", used by every
    /// <c>radar-formula-v9</c>/<c>v10</c> channel.
    /// <para>
    /// Deliberately NOT used for <see cref="AttentionComponent"/>, whose expression is
    /// <c>100·reach/(reach+S)</c> — associated as <c>(100·reach)/(reach+S)</c>, which is not the same
    /// floating-point value as <c>100·(reach/(reach+S))</c>. Routing that block through this helper would be
    /// the tidy-looking change that moves v8's last bit; see the type remarks.
    /// </para>
    /// </summary>
    public static double Saturate(double raw, double halfSaturation) => raw / (raw + halfSaturation);

    /// <summary>
    /// The (v8-meaning) <c>TrajectoryScore</c> component: <c>TrajectoryNeutral + TrajectoryScale · T_raw</c>
    /// where <c>T_raw = TrajectoryBand·(Mpos−Mneg)/(Mpos+Mneg+k) ∈ [-10,10]</c>, clamped to <c>[0,100]</c>.
    /// No directional signals ⇒ <c>Mpos == Mneg == 0</c> ⇒ <c>T_raw = 0</c> ⇒ exactly
    /// <see cref="ScoringWeights.TrajectoryNeutral"/> (see the ⚠ note on this type).
    /// <para>
    /// EXTRACTED BY SPEC 153 (previously duplicated verbatim in v8 and v9). No quality factors are passed:
    /// the per-signal trajectory weight is <c>confidence·recency</c> alone, unchanged since v5. The caller
    /// must not pass an empty set expecting a defined answer — v8 and v9 both short-circuit an empty window
    /// to zeros before reaching here.
    /// </para>
    /// </summary>
    public static int TrajectoryScore(
        IReadOnlyList<ScoringSignal> signals, IReadOnlyList<double> recency, ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        var mass = DirectionalMasses(signals, recency);
        var tRaw = Preponderance(mass, weights.TrajectoryCorroborationK, TrajectoryBand);
        return Clamp0To100(weights.TrajectoryNeutral + weights.TrajectoryScale * tRaw);
    }

    /// <summary>
    /// The (v8-meaning) <c>AttentionScore</c> component from a raw <see cref="AttentionReach"/>:
    /// <c>100·reach/(reach + AttentionHalfSaturation)</c>, clamped to <c>[0,100]</c>. Extracted by spec 153;
    /// the expression's association is preserved verbatim (see <see cref="Saturate"/> for why it is not
    /// expressed in terms of that helper).
    /// </summary>
    public static int AttentionComponent(double reach, ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        return Clamp0To100(100 * reach / (reach + weights.AttentionHalfSaturation));
    }

    /// <summary>
    /// The (v8-meaning) <c>EvidenceConfidenceScore</c> component: best-anchored plus a saturating
    /// source-type diversity bonus. Anchors on the strongest signal confidence and the highest evidence-quality
    /// weight, so adding a weaker signal or a lower-quality source can never lower the base — corroboration is
    /// monotonic. Extracted by spec 153; expression shape and accumulation order preserved verbatim.
    /// <para>
    /// <b>Neutral evidence counts here in full.</b> That is deliberate and load-bearing for spec 153: a
    /// channel of purely Neutral signals contributes no DIRECTIONAL opportunity under
    /// <c>radar-formula-v10</c>, but it is still evidence Radar gathered and it still raises confidence and
    /// coverage. This method never inspects <see cref="SignalDirection"/>.
    /// </para>
    /// </summary>
    public static int EvidenceConfidenceScore(
        IReadOnlyList<ScoringSignal> signals, ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(weights);

        var bestConf = signals.Max(s => (double)s.Signal.Confidence); // 0..1
        var bestQualWeight = signals.Max(s => QualityWeight(weights, s.Evidence.Quality));
        var distinctTypes = signals.Select(s => s.Evidence.SourceType).Distinct().Count();
        var divFactor = Math.Min(1, distinctTypes / weights.DiversityTarget);
        return Clamp0To100(
            100 * bestConf
                * (weights.EcQualityBase + weights.EcQualitySpan * bestQualWeight)
                * (weights.EcDiversityBase + weights.EcDiversitySpan * divFactor));
    }

    /// <summary>
    /// The (v8-meaning) <c>SignalVelocityScore</c> component: the smoothed current-versus-previous activity
    /// ratio, <c>VelocitySteady · (actNow + s)/(actPrev + s)</c>, clamped to <c>[0,100]</c>. Extracted by
    /// spec 153; expression shape and accumulation order preserved verbatim.
    /// <para>
    /// <paramref name="previousSignals"/> is activity-only (no evidence) and never builds provenance — see
    /// <see cref="ScoringInput.PreviousSignals"/>.
    /// </para>
    /// </summary>
    public static int SignalVelocityScore(
        IReadOnlyList<ScoringSignal> signals,
        IReadOnlyList<Signal> previousSignals,
        ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(previousSignals);
        ArgumentNullException.ThrowIfNull(weights);

        var actNow = signals.Sum(s => s.Signal.Strength);
        var actPrev = previousSignals.Sum(s => s.Strength);
        var ratio = (actNow + weights.VelocitySmoothing) / (actPrev + weights.VelocitySmoothing);
        return Clamp0To100(weights.VelocitySteady * ratio);
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
    /// THE <c>radar-formula-v11</c> COLLECTOR-ACTIVITY PRIMITIVE (extracted by spec 158 §4, normatively fixed
    /// and consumed by spec 157 §1): a collector channel's DIRECTIONAL-ONLY activity — exactly
    /// <c>DirectionalMasses(signals, recency, qualityFactors).Total</c>, i.e. the
    /// <c>strength · confidence · recency · quality</c> mass summed over Positive and Negative signals only.
    /// Neutral/Mixed signals contribute <b>exactly zero</b>, so a channel's saturation built on this term
    /// cannot rise on neutral volume — the AD-16 property v10's <see cref="ActivityMass"/> lacks.
    /// <para>
    /// It matches the <c>ChannelActivityMass</c> delegate shape, and <c>RadarScoreFormulaV11</c> passes it
    /// where v9/v10 pass <see cref="ActivityMass"/> — the spec-158 input-only characterization and v11 share
    /// this ONE definition instead of drifting copies. <see cref="ActivityMass"/> is untouched — v9/v10 keep
    /// their exact arithmetic.
    /// </para>
    /// </summary>
    public static double DirectionalActivityMass(
        IReadOnlyList<ScoringSignal> signals,
        IReadOnlyList<double> recency,
        IReadOnlyList<double> qualityFactors)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(recency);
        ArgumentNullException.ThrowIfNull(qualityFactors);

        return DirectionalMasses(signals, recency, qualityFactors).Total;
    }

    /// <summary>
    /// PROSPECTIVE <c>radar-formula-v11</c> PRIMITIVE (spec 158 §4; the exact mechanics fixed by spec 157
    /// §3): the POSITIVE-ONLY breadth reach. Applies <c>Direction == Positive</c> to BOTH the post-collapse
    /// and the pre-collapse input sets and then evaluates the <b>existing, unchanged</b>
    /// <see cref="AttentionReach"/> term over the narrowed inputs — the third-party-publisher test, the tier
    /// weights, the collapsed-publisher credit and the media-count term are all the same expressions, simply
    /// seeing a smaller input set.
    /// <list type="bullet">
    /// <item><b>Publisher inclusion stays BINARY and DISTINCT</b>: a publisher qualifies on at least ONE
    /// Positive signal and is counted once — several Positive signals earn no extra reach (inherited from
    /// <see cref="DistinctThirdPartyPublishers"/>' set semantics, not re-implemented).</item>
    /// <item><b>Negative is excluded alongside Neutral</b>, from the media-count term as well as from
    /// publisher reach: only Positive signals pass the filter. Broad NEGATIVE coverage must never raise a
    /// score named Opportunity — deterioration is Trajectory's job (spec 157 §3).</item>
    /// <item>A Neutral <see cref="SignalType.MediaAttention"/> signal contributes zero here even when the
    /// same publisher separately qualifies via a Positive signal.</item>
    /// <item><b><c>AttentionScore</c> is NOT filtered</b> — the attention COMPONENT (and the notedness
    /// discount it feeds) stays over the full gated set via <see cref="AttentionReach"/> /
    /// <see cref="AttentionComponent"/>. This term narrows the breadth CHANNEL only.</item>
    /// </list>
    /// <para>
    /// Matches the <c>BreadthChannelReach</c> delegate shape. <b>No shipped formula consumes it, and that is
    /// now a DECISION rather than a pending state</b>: spec 158 measured this rule as structurally zero in
    /// the current collector mix (spec 70 makes every news signal Neutral; first-party RSS is not a
    /// third-party publisher), so spec 157 §3 as amended has <c>radar-formula-v11</c> REJECT breadth channels
    /// outright instead of narrowing them through this term. The helper stays because the spec-158 audit
    /// measures through it, and because positive-only breadth becomes viable again — as
    /// <c>radar-formula-v12</c>, under AD-6 — if the collector mix ever produces Positive third-party
    /// signals. See <c>docs/158-channel-feasibility-findings.md</c>.
    /// </para>
    /// </summary>
    public static double PositiveAttentionReach(
        IReadOnlyList<ScoringSignal> signals,
        IReadOnlyList<ScoringSignal> preCollapseSignals,
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(preCollapseSignals);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);

        return AttentionReach(
            FilterPositive(signals), FilterPositive(preCollapseSignals), weights, sourceWeights);
    }

    /// <summary>
    /// The §3 input filter: Positive signals only. Neutral, Mixed AND Negative are excluded — the filter is
    /// applied to the INPUTS, so every downstream reach term sees a smaller set rather than a changed rule.
    /// </summary>
    private static IReadOnlyList<ScoringSignal> FilterPositive(IReadOnlyList<ScoringSignal> signals)
    {
        var positive = new List<ScoringSignal>(signals.Count);
        for (var i = 0; i < signals.Count; i++)
        {
            if (signals[i].Signal.Direction == SignalDirection.Positive)
            {
                positive.Add(signals[i]);
            }
        }

        return positive;
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
