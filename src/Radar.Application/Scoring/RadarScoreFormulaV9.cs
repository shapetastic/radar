using System.Text.Json;

using Radar.Application.Collectors;

namespace Radar.Application.Scoring;

/// <summary>
/// The opt-in <see cref="IScoreFormula"/> <c>radar-formula-v9</c> (spec 146): a strategy's score is a
/// <b>weighted array of channels</b>, <c>score = Σ (weight_c · channelScore_c)</c>, with every
/// <c>channelScore_c ∈ [0,1]</c> and the weights summing to 1.
/// <para>
/// <b>v8 is untouched and remains the default.</b> This is an ADDITIVE structure (AD-6: a formula
/// structure change is a new <c>radar-formula-vN</c> class), so a v8 strategy and a v9 channel strategy run
/// over the SAME collection pass (spec 137) and can be compared directly. Nothing migrates onto v9.
/// </para>
///
/// <para><b>Why v8 cannot express this.</b> Every v8 component is computed over the signals that ARRIVED,
/// so the formula cannot know a strategy expected patents evidence and got none. Absence therefore costs
/// nothing; when it is visible at all it is incoherent (<c>SignalVelocity</c> correctly falls while
/// <c>AttentionScore</c>, an INVERSE discount inside Opportunity, perversely rises); and contributions are
/// incommensurable, so a high-traffic source dominates a high-value one. A declared channel budget fixes all
/// three at once.</para>
///
/// <para><b>Channel scores.</b>
/// <list type="bullet">
/// <item><b>Collector channel</b> — <c>channelScore = saturation · directionFactor</c>, where
/// <c>activity = Σ (strength · confidence · recency · qualityWeight)</c> over the signals whose EVIDENCE was
/// retrieved by one of the channel's declared collectors, <c>saturation = activity/(activity + S_c) ∈
/// [0,1)</c>, and <c>directionFactor = (1 + preponderance)/2 ∈ [0,1]</c> with <c>preponderance</c> the same
/// corroboration-smoothed <c>(Mpos−Mneg)/(Mpos+Mneg+k)</c> ratio v8's Trajectory uses. A channel with no
/// directional mass has <c>directionFactor = 0.5</c> — neutral, neither rewarded nor punished. Activity
/// counts Neutral/Mixed signals (something DID happen on that channel) while the direction factor does not,
/// which is the same split v8 makes.</item>
/// <item><b>Breadth channel</b> — <c>channelScore = reach/(reach + S_c)</c> over the tier-weighted
/// distinct-publisher <see cref="ScoreSignalMath.AttentionReach"/>, computed across every signal surviving
/// the strategy's <see cref="SignalTypeFilter"/> gate. Attention is inherently cross-source, so it is a
/// strategy-level channel rather than a per-collector sub-score. <b>In v9 it is DIRECTION-CORRECT: more
/// genuine breadth contributes MORE</b> — v8's per-component inverse attention discount is not carried
/// over.</item>
/// </list>
/// Direction, confidence, strength, recency and quality semantics are byte-identical to v8 — they come from
/// the shared <see cref="ScoreSignalMath"/>. Only the SET each term is computed over changes.
/// </para>
///
/// <para><b>THE NOTEDNESS DISCOUNT (spec 149), applied to the COMPOSED score.</b> The composite is
/// multiplied by <see cref="ScoreSignalMath.NotednessDiscount"/> — the same clamped
/// <c>1 − attention·w − tierDiscount·w</c> expression <c>radar-formula-v8</c> applies, over the same
/// <see cref="ScoringWeights"/> knobs and the same clamped-int attention component. v9 shipped with ZERO
/// references to it, and the first live three-strategy run (2026-07-27) showed the consequence: v9 nearly
/// inverted the v8 primary at the extremes (CAT 43rd of 43 under the v8 default, 1st under a v9 strategy),
/// because a formula that ranks on raw channel activity is largely ranking on SIZE — close to the inverse of
/// Radar's purpose. It is applied ONCE, to the composite, not per channel: notedness is a property of the
/// COMPANY, not of a source. Note that attention now enters v9 twice with opposite signs and different
/// meanings — as budgetable breadth a strategy earns share for, and as the fame that damps whatever it found.
/// Setting <see cref="ScoringWeights.OpportunityAttentionDiscountWeight"/> and
/// <see cref="ScoringWeights.FollowingTierDiscountWeight"/> to 0 (conveniently, inline under
/// <c>Radar:Strategies[i].Weights</c>) makes the discount exactly 1.0 and reproduces pre-149 v9
/// bit-for-bit — components, explanation and contributions — <b>except <c>ComponentJson</c>, which gains the
/// one additive <c>Discount</c> property, recorded unconditionally (see the <c>ComponentJson</c> note
/// below)</b>.</para>
///
/// <para>⚠ <b>AD-6, answered explicitly because the honest answer is uncomfortable.</b> Adding a
/// multiplicative discount changes v9's COMPOSITION, not merely its inputs: at the DEFAULT weights a v9
/// strategy scores differently after spec 149 than before it. Under a strict reading of AD-6 that is a
/// structure change and would earn a <c>radar-formula-v10</c>. Spec 149 put v10 out of scope, so the version
/// stays <c>radar-formula-v9</c> — and the consequence must be stated rather than buried: the default
/// <see cref="ScoringWeights"/> did not change either, so <b>a v9 strategy's <c>ScoringConfigVersion</c> does
/// NOT move even though its behaviour did</b>. v9 snapshots written before and after this slice are therefore
/// falsely comparable, and <c>StrategyIdentityGuard</c> will not trip on the difference. That is precisely the
/// failure mode spec 148 exists to prevent, accepted here on the strength of the mitigating facts: v9 is
/// opt-in, shipped days earlier, and has exactly ONE live run of history. Anyone who cares about the
/// discontinuity should apply spec 141's immutable-by-convention rule and give the retuned strategy a NEW
/// NAME (<c>patents-led</c> → <c>patents-led-v2</c>), which re-keys the series via <c>ScoreSeriesKey</c>
/// without needing the stamp to move. A future structural change to v9 should bump to
/// <c>radar-formula-v10</c> instead of repeating this.</para>
///
/// <para><b>ABSENCE COSTS SOMETHING, AND THE WEIGHTS ARE NEVER RENORMALISED.</b> A channel that produced no
/// signals scores 0 and the denominator does not shrink, so a strategy declaring three channels can only
/// approach 1.0 when all three fire. See <see cref="ScoringChannelSet"/> for the full argument; the short
/// version is that renormalising the surviving weights is the obvious-looking "fix" that would erase exactly
/// the penalty this formula exists to create.</para>
///
/// <para><b>Range reconciliation, verified rather than assumed.</b> <see cref="ScoreComponents"/>' contract
/// is five <c>int</c>s each clamped to <c>[0,100]</c>, and v8 honours it via
/// <see cref="ScoreSignalMath.Clamp0To100"/>. The v9 composite is a <c>double</c> in <c>[0,1]</c>, so it is
/// mapped as <c>composite · notednessDiscount · 100</c> into
/// <see cref="ScoreComponents.OpportunityScore"/> — the discount is itself in <c>(0,1]</c>, so the product
/// stays in range — the field
/// <c>WeeklyReportBuilder</c> ranks by and the spec-101/108 efficacy read side consumes, i.e. the one that
/// has to carry "this strategy's answer". The other four components keep their exact v8 meanings, computed
/// over the strategy's gated signal set, so <c>WeeklyReportActionPolicyV1</c>'s Trajectory /
/// EvidenceConfidence thresholds remain valid and the report stays legible for a v9 strategy. The unrounded
/// composite, the applied notedness discount and the full per-channel breakdown are additionally carried in
/// <c>ComponentJson</c>, whose first five properties are still exactly <see cref="ScoreComponents"/>' so any
/// existing reader keeps working.</para>
///
/// <para><b>"Ran and found nothing" vs "did not run" — weaker than it looks, stated plainly (spec 147).</b>
/// A channel scores 0 whether its source was down or genuinely quiet — Radar scores evidence, and absence of
/// evidence is not evidence — and the per-channel provenance splits each channel's declared collectors
/// against <see cref="ScoringInput.EnabledCollectors"/>. But <c>ScoringStrategyFactory</c> validates those
/// same channel collectors against that same vocabulary at STARTUP and refuses to build any engine if one is
/// missing, so once a run has started <see cref="ChannelBreakdown.CollectorsNotRun"/> is <b>structurally
/// empty</b> — in every run mode. A channel 0 therefore always means "this window holds no signals whose
/// evidence that collector retrieved"; it is not an outage signal. Spec 147 did not weaken this, it
/// UN-inverted it: before it, a spec-144 <c>score</c> pass had an empty vocabulary, so every declared
/// collector read as "did not run" for collectors that demonstrably had (and a v9 collector-channel strategy
/// could not start at all). Under a spec-139 replay the vocabulary reflects the REPLAYING process's
/// configuration, not the historical run's; the historical answer is on each snapshot's own
/// <c>CollectionProvenance</c>.</para>
///
/// <para><b>WHERE A SIGNAL'S COLLECTOR COMES FROM (spec 151).</b> A collector channel selects on the
/// collector behind each signal's evidence, resolved through the single
/// <see cref="ICollectorAttributionResolver"/> seam rather than by reading the metadata key inline. The
/// default resolver reads only what spec 146 RECORDED, so this formula's behaviour is unchanged; an opt-in
/// resolver additionally re-derives attribution for evidence that predates that recording. The formula keeps
/// the whole <see cref="CollectorAttribution"/> rather than just the name, so <b>how</b> each answer was
/// obtained survives into the provenance it emits: the per-channel breakdown counts recorded vs inferred vs
/// unattributed signals, and a contribution whose collector was inferred says so. What a channel MEANS is
/// untouched — <c>ScoringChannel.Consumes</c> still matches the collector name exactly, and an unattributed
/// signal is still consumed by no collector channel.</para>
///
/// <para>Pure and deterministic (no clock, no randomness, no I/O). Emits exactly one provenance-carrying
/// contribution per current-window signal, in input order, each naming the channel(s) that consumed it —
/// so evidence → signal → channel → score is traceable end to end — and never from
/// <see cref="ScoringInput.PreviousSignals"/>.</para>
/// </summary>
public sealed class RadarScoreFormulaV9 : IScoreFormula
{
    // The direction factor maps the preponderance ratio [-1,1] onto [0,1] with 0.5 = neutral. Structural
    // (the shape of "how much of this channel's share does its direction earn"), not a tunable magnitude,
    // so it stays const in the formula exactly like v8's TrajectoryBand.
    private const double DirectionNeutral = 0.5;
    private const double DirectionSpan = 0.5;

    // v8's structural trajectory band, reused unchanged for the (v8-meaning) TrajectoryScore component.
    private const double TrajectoryBand = 10.0;

    private readonly ScoringWeights _weights;
    private readonly IAttentionSourceWeights _sourceWeights;
    private readonly ScoringChannelSet _channels;
    private readonly ICollectorAttributionResolver _attribution;

    /// <summary>
    /// Constructs the formula with the strategy's magnitudes, the shared publisher tier map, and the
    /// strategy's validated channel array. There is deliberately no parameterless construction: all three
    /// are config data supplied by Infrastructure (AD-5) and all three must be immutable so the formula stays
    /// a pure function (AD-3). Fails fast on a nonsensical weight (<see cref="ScoringWeights.Validate"/>) and
    /// on an empty channel set — a v9 strategy with no channels could only ever score 0.
    /// </summary>
    /// <param name="attributionResolver">
    /// How the collector behind each signal's evidence is established (spec 151). Optional and defaulting to
    /// <see cref="RecordedOnlyCollectorAttributionResolver"/> — i.e. exactly what this formula did inline
    /// before the seam existed — so an omitted resolver is not merely "safe" but behaviourally identical to
    /// pre-151.
    /// </param>
    public RadarScoreFormulaV9(
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights,
        ScoringChannelSet channels,
        ICollectorAttributionResolver? attributionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        ArgumentNullException.ThrowIfNull(channels);
        weights.Validate();

        if (channels.IsEmpty)
        {
            throw new InvalidOperationException(
                $"{ScoreFormulaVersions.V9} requires at least one channel; a channel-composition formula with "
                    + "no channels would score every company 0. Declare Channels on the strategy, or use "
                    + $"{ScoreFormulaVersions.V8}.");
        }

        _weights = weights;
        _sourceWeights = sourceWeights;
        _channels = channels;
        _attribution = attributionResolver ?? RecordedOnlyCollectorAttributionResolver.Instance;
    }

    /// <inheritdoc />
    public string Version => ScoreFormulaVersions.V9;

    /// <summary>The strategy's declared channel budget (exposed for provenance/tests; immutable).</summary>
    public ScoringChannelSet Channels => _channels;

    /// <inheritdoc />
    public ScoreComputation Compute(ScoringInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var signals = input.Signals;
        var windowLength = input.WindowEndUtc - input.WindowStartUtc;

        // Shared per-signal primitives (spec 146) — identical to what v8 computes, over the same set.
        var recency = ScoreSignalMath.RecencyFactors(
            signals, input.WindowStartUtc, input.WindowEndUtc, _weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(signals, _weights);

        // The collector behind each signal's evidence, WITH how that answer was obtained (spec 146 recorded
        // it; spec 151 added the seam and the opt-in legacy inference). Unattributed evidence is consumed by
        // NO collector channel and contributes 0 — which is exactly what the standing "never backfill accrued
        // history" rule implies, and remains the default answer for every pre-spec-146 record unless an
        // operator explicitly turns the inference on.
        var attributionOf = new CollectorAttribution[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            attributionOf[i] = _attribution.Resolve(signals[i].Evidence);
        }

        var enabled = new HashSet<string>(input.EnabledCollectors, StringComparer.Ordinal);

        // ---- The channels ----
        var breakdown = new List<ChannelBreakdown>(_channels.Channels.Count);
        // Per-signal channel attribution, for the contribution reasons (provenance: signal → channel). Filled
        // as each channel selects, so a signal consumed by more than one channel names all of them.
        var channelsPerSignal = new List<string>?[signals.Count];

        foreach (var channel in _channels.Channels)
        {
            double channelScore;
            // "Nothing to measure" — the state the no-renormalisation rule is about. It is NOT the same as
            // "scored 0": a collector channel whose signals are uniformly negative also scores 0, and that is
            // a measurement, not an absence. So it is recorded per kind at the source rather than inferred
            // from the score.
            bool dark;

            // The current-window signals this channel consumed, in input order. Filled by BOTH branches so
            // the attribution tally below has exactly one definition of "this channel's signals" — the same
            // set SignalCount reports and the same set each branch attributes in the contribution reasons.
            var consumedIndices = new List<int>();

            if (channel.Kind == ScoringChannelKind.Breadth)
            {
                // Breadth is cross-source by construction: it reads the whole gated set regardless of which
                // collector retrieved what, and it is POSITIVE — more genuine (tier-weighted,
                // distinct-publisher) reach earns more of its share.
                var reach = ScoreSignalMath.AttentionReach(
                    signals, input.PreCollapseSignals, _weights, _sourceWeights);
                channelScore = Saturate(reach, channel.Saturation);
                dark = reach <= 0;

                // Attribution: the signals that actually FED the reach term (a third-party publisher, or a
                // MediaAttention signal), not simply every signal in the window — so a contribution reason
                // naming this channel is true of that signal. Publishers credited only from the pre-collapse
                // set have no current-window signal to attribute to, by construction.
                for (var i = 0; i < signals.Count; i++)
                {
                    if (ScoreSignalMath.ContributesToReach(signals[i]))
                    {
                        consumedIndices.Add(i);
                        (channelsPerSignal[i] ??= []).Add(channel.Name);
                    }
                }
            }
            else
            {
                for (var i = 0; i < signals.Count; i++)
                {
                    // Consumes() matches the collector NAME exactly and is false for a null name, so an
                    // unattributed signal selects into no collector channel regardless of how attribution was
                    // resolved (spec 151 changes which signals HAVE a name, never what a channel means).
                    if (channel.Consumes(attributionOf[i].CollectorName))
                    {
                        consumedIndices.Add(i);
                        (channelsPerSignal[i] ??= []).Add(channel.Name);
                    }
                }

                dark = consumedIndices.Count == 0;

                // Sub-slices in the SAME input order, so the shared primitives see exactly the shape they see
                // for a whole window — the channel changes the SET, never the math.
                var subSignals = consumedIndices.Select(i => signals[i]).ToList();
                var subRecency = consumedIndices.Select(i => recency[i]).ToList();
                var subQuality = consumedIndices.Select(i => quality[i]).ToList();

                var activity = ScoreSignalMath.ActivityMass(subSignals, subRecency, subQuality);
                var saturation = Saturate(activity, channel.Saturation);

                // (1 + preponderance)/2 maps the ratio [-1,1] onto [0,1]. A channel with no directional mass
                // sits at EXACTLY 0.5 — neutral, neither rewarded nor punished. Otherwise the corroboration
                // smoother k keeps the ratio strictly inside (-1,1), so a positive channel approaches (never
                // reaches) its full saturated share as its corroborated positive mass grows, and a negative
                // one approaches (never reaches) zero — the same damped-but-not-zeroed shape v6 gave
                // Trajectory, so a single loud signal cannot swing a channel to an extreme.
                var mass = ScoreSignalMath.DirectionalMasses(subSignals, subRecency, subQuality);
                var preponderance = ScoreSignalMath.Preponderance(
                    mass, _weights.TrajectoryCorroborationK, band: 1.0);
                var directionFactor = DirectionNeutral + DirectionSpan * preponderance;

                channelScore = saturation * directionFactor;
            }

            // "Ran and found nothing" vs "did not run" (spec 146). A 0 is a 0 either way — Radar scores
            // evidence and absence of evidence is not evidence — but which it was must be recoverable after
            // the fact, so it is recorded rather than inferred.
            var ran = channel.Collectors.Where(enabled.Contains).ToArray();
            var notRun = channel.Collectors.Where(c => !enabled.Contains(c)).ToArray();

            // ATTRIBUTION PROVENANCE (spec 151): how much of this channel's mass rests on a collector the
            // producing collector RECORDED, versus one Radar re-derived afterwards. Recorded in the snapshot
            // so that any artifact built on inferred attribution — a replayed series, a strategy leaderboard —
            // can state the fraction rather than imply it is all first-hand.
            var recordedSignals = 0;
            var inferredSignals = 0;
            var unattributedSignals = 0;
            foreach (var i in consumedIndices)
            {
                switch (attributionOf[i].Source)
                {
                    case CollectorAttributionSource.Recorded:
                        recordedSignals++;
                        break;
                    case CollectorAttributionSource.Inferred:
                        inferredSignals++;
                        break;
                    default:
                        unattributedSignals++;
                        break;
                }
            }

            breakdown.Add(new ChannelBreakdown(
                Name: channel.Name,
                Kind: ScoringChannelSet.KindToken(channel.Kind),
                Weight: channel.Weight,
                Saturation: channel.Saturation,
                Score: channelScore,
                WeightedContribution: channel.Weight * channelScore,
                SignalCount: consumedIndices.Count,
                Dark: dark,
                Collectors: channel.Collectors,
                CollectorsRan: ran,
                CollectorsNotRun: notRun,
                RecordedSignals: recordedSignals,
                InferredSignals: inferredSignals,
                UnattributedSignals: unattributedSignals));
        }

        // THE COMPOSITE. Summed over the DECLARED channels — never over "the channels that fired" — so a dark
        // channel costs the strategy its whole share. DO NOT renormalise by the surviving weights: that is the
        // obvious-looking fix, and it would erase exactly the penalty this formula exists to create. The clamp
        // is a defensive range guarantee only: the weights are validated to sum to 1 and every channel score is
        // in [0,1], so the sum is already in [0,1].
        var composite = 0.0;
        foreach (var channel in breakdown)
        {
            composite += channel.WeightedContribution;
        }

        composite = Math.Clamp(composite, 0.0, 1.0);

        // ---- The four v8-meaning components, over the strategy's gated set ----
        // Empty window short-circuits to zeros exactly as v8 does, so a v9 strategy's "nothing to score"
        // snapshot is indistinguishable from a v8 one (the composite is 0 there anyway).
        var trajectoryScore = 0;
        var attentionScore = 0;
        var evidenceConfidenceScore = 0;
        var signalVelocityScore = 0;

        if (signals.Count > 0)
        {
            var mass = ScoreSignalMath.DirectionalMasses(signals, recency);
            var tRaw = ScoreSignalMath.Preponderance(
                mass, _weights.TrajectoryCorroborationK, TrajectoryBand);
            trajectoryScore = ScoreSignalMath.Clamp0To100(
                _weights.TrajectoryNeutral + _weights.TrajectoryScale * tRaw);

            var reach = ScoreSignalMath.AttentionReach(
                signals, input.PreCollapseSignals, _weights, _sourceWeights);
            attentionScore = ScoreSignalMath.Clamp0To100(
                100 * reach / (reach + _weights.AttentionHalfSaturation));

            var bestConf = signals.Max(s => (double)s.Signal.Confidence);
            var bestQualWeight = signals.Max(s => ScoreSignalMath.QualityWeight(_weights, s.Evidence.Quality));
            var distinctTypes = signals.Select(s => s.Evidence.SourceType).Distinct().Count();
            var divFactor = Math.Min(1, distinctTypes / _weights.DiversityTarget);
            evidenceConfidenceScore = ScoreSignalMath.Clamp0To100(
                100 * bestConf
                    * (_weights.EcQualityBase + _weights.EcQualitySpan * bestQualWeight)
                    * (_weights.EcDiversityBase + _weights.EcDiversitySpan * divFactor));

            var actNow = signals.Sum(s => s.Signal.Strength);
            var actPrev = input.PreviousSignals.Sum(s => s.Strength);
            var ratio = (actNow + _weights.VelocitySmoothing) / (actPrev + _weights.VelocitySmoothing);
            signalVelocityScore = ScoreSignalMath.Clamp0To100(_weights.VelocitySteady * ratio);
        }

        // ---- The notedness discount (spec 149) ----
        // Applied to the COMPOSED channel score, never per channel: notedness is a property of the COMPANY
        // ("how much of this is already priced into everyone's attention"), not of a source, so discounting
        // each channel separately would apply it once per channel and confuse a source's reach with the
        // company's fame. The same clamped expression v8 uses, over the same ScoringWeights knobs, fed the
        // same clamped-int attentionScore — see ScoreSignalMath.NotednessDiscount.
        //
        // NOTE the remaining, deliberate asymmetry with v8: breadth still earns its own POSITIVE channel share
        // here (v8's inverse per-component attention discount is not carried over). Attention therefore enters
        // v9 twice with opposite signs and different meanings — as genuine reach a strategy can budget for,
        // and as the notedness that damps whatever it found — which is the whole point: a widely-covered
        // company is easy to find evidence about and correspondingly less interesting to surface.
        //
        // At OpportunityAttentionDiscountWeight = 0 AND FollowingTierDiscountWeight = 0 the discount is
        // EXACTLY 1.0, and multiplying by exactly 1.0 is the IEEE-754 identity — so a strategy opts out
        // bit-for-bit, reproducing pre-149 v9 output. That is the compatibility proof, asserted in
        // RadarScoreFormulaV9Tests.
        var notednessDiscount = ScoreSignalMath.NotednessDiscount(
            _weights, attentionScore, input.FollowingTier);

        // The composite IS this strategy's answer, so it lands in OpportunityScore — see the range
        // reconciliation in the class remarks.
        var opportunityScore = ScoreSignalMath.Clamp0To100(100.0 * composite * notednessDiscount);

        var components = new ScoreComponents(
            TrajectoryScore: trajectoryScore,
            OpportunityScore: opportunityScore,
            AttentionScore: attentionScore,
            EvidenceConfidenceScore: evidenceConfidenceScore,
            SignalVelocityScore: signalVelocityScore);

        // ---- Contributions (provenance — current window only) ----
        // The IScoreFormula contract, unchanged: exactly one contribution per current-window signal, in input
        // order, including signals no channel consumed (which weigh into no channel and are named as such).
        // The per-signal WEIGHT keeps v8's shape — the channel weight and saturation are AGGREGATE transforms
        // over a channel's signals, exactly as v8's consensus shaping and following discount are aggregate
        // transforms over its signals — and the channel attribution (with its share) is carried in the reason,
        // which is what makes evidence → signal → channel → score traceable.
        var contributions = new List<ScoreContribution>(signals.Count);
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i].Signal;
            var w = (double)signal.Confidence * recency[i];
            var weight = (int)Math.Round(
                ScoreSignalMath.DirectionSign(signal.Direction) * signal.Strength * w,
                MidpointRounding.AwayFromZero);

            contributions.Add(new ScoreContribution(
                SignalId: signal.Id,
                EvidenceId: signals[i].Evidence.Id,
                ContributionReason:
                    $"{signal.Type} ({signal.Direction}), strength {signal.Strength}, "
                        + $"confidence {signal.Confidence:0.00} — {DescribeAttribution(channelsPerSignal[i], attributionOf[i])}",
                ContributionWeight: weight));
        }

        var windowDays = (int)Math.Round(windowLength.TotalDays, MidpointRounding.AwayFromZero);
        var channelSummary = string.Join(
            ", ",
            breakdown.Select(c =>
                $"{c.Name} {c.Score:0.000}×{c.Weight:0.00}{(c.Dark ? " (dark)" : string.Empty)}"));
        // The discount is named in the explanation ONLY when it actually moved the number (spec 149). Two
        // reasons, and they point the same way. (1) Honesty: "Opportunity 33 (composite 0.412 = …)" reads as
        // an arithmetic error unless the transform between the two is stated, and a score Radar cannot explain
        // is not a score. (2) Compatibility: an inert discount is exactly 1.0, so omitting it then is not a
        // hidden term — it is the true statement that Opportunity IS composite·100 — and it keeps a strategy
        // that opted out (both discount weights 0) byte-identical to pre-149 v9, explanation included.
        var notednessSummary = notednessDiscount == 1.0
            ? string.Empty
            : $"; × notedness {notednessDiscount:0.000}";
        var explanation =
            $"{ScoreFormulaVersions.V9}: {signals.Count} signal(s) over {windowDays}d across "
                + $"{breakdown.Count} channel(s) → Opportunity {opportunityScore} (composite {composite:0.000} = "
                + $"{channelSummary}{notednessSummary}); Trajectory {trajectoryScore}, Attention {attentionScore}, "
                + $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}.";

        // ComponentJson keeps ScoreComponents' five properties FIRST and by the same names, so an existing
        // reader that deserializes it as ScoreComponents is unaffected (extra properties are ignored), and
        // adds the unrounded composite, the spec-149 notedness discount and the per-channel breakdown that
        // makes each share auditable. Discount is recorded UNCONDITIONALLY (unlike the explanation's
        // conditional mention): this is the machine-readable record, and "the discount was 1.000" is a fact a
        // later audit needs stated rather than inferred from its absence. It is the one thing about a v9
        // snapshot's ComponentJson that spec 149 changed — see the class remarks.
        var componentJson = JsonSerializer.Serialize(new V9ComponentJson(
            TrajectoryScore: components.TrajectoryScore,
            OpportunityScore: components.OpportunityScore,
            AttentionScore: components.AttentionScore,
            EvidenceConfidenceScore: components.EvidenceConfidenceScore,
            SignalVelocityScore: components.SignalVelocityScore,
            Formula: Version,
            Composite: composite,
            Discount: notednessDiscount,
            Channels: breakdown));

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }

    /// <summary>The half-saturation shape <c>x/(x+S)</c>, in <c>[0,1)</c> for non-negative <c>x</c>.</summary>
    private static double Saturate(double raw, double halfSaturation) => raw / (raw + halfSaturation);

    private static string DescribeAttribution(
        IReadOnlyList<string>? channels, CollectorAttribution attribution)
    {
        // Spec 151: an INFERRED collector is named as such, in the persisted contribution reason, so the
        // provenance chain itself says which of its links is a re-derivation. Appended only when the
        // attribution actually was inferred — with the default recorded-only resolver this is always empty and
        // every reason below is byte-identical to pre-151.
        var inferred = attribution.Source == CollectorAttributionSource.Inferred
            ? " (collector attribution inferred)"
            : string.Empty;

        if (channels is { Count: > 0 })
        {
            return $"channel {string.Join(" + ", channels)}{inferred}";
        }

        // Explicitly distinguishes the two reasons a signal fed no collector channel, because they mean very
        // different things: legacy evidence that predates collector recording (never backfilled, by rule)
        // versus a collector this strategy simply did not budget for.
        return attribution.CollectorName is null
            ? "no channel (evidence has no recorded collector)"
            : $"no channel (collector {attribution.CollectorName} is not budgeted by this strategy){inferred}";
    }

    /// <summary>
    /// One channel's audited share, serialized into <c>ComponentJson</c>: what it measured, what it was
    /// worth, how much it actually earned, whether it had anything to measure at all
    /// (<paramref name="Dark"/>), and — for a collector channel — which of its declared collectors ran.
    /// </summary>
    /// <param name="SignalCount">
    /// How many current-window signals this channel consumed — for a collector channel, those whose evidence
    /// its collectors retrieved; for a breadth channel, those that actually fed the reach term. It can differ
    /// from what drove the score: breadth also credits publishers recovered from the pre-collapse set, which
    /// have no current-window signal to count. Use <paramref name="Dark"/>, not this, to ask whether the
    /// channel measured anything.
    /// </param>
    /// <param name="Dark">
    /// True when the channel had nothing to measure — a collector channel that consumed no signals, or a
    /// breadth channel with zero reach. Deliberately distinct from <c>Score == 0</c>: a channel whose signals
    /// are uniformly negative also scores 0, and that is a measurement, not an absence.
    /// </param>
    /// <param name="RecordedSignals">
    /// How many of this channel's <paramref name="SignalCount"/> signals sit on evidence whose producing
    /// collector RECORDED its own name (spec 146). First-hand provenance.
    /// </param>
    /// <param name="InferredSignals">
    /// How many sit on evidence whose collector Radar re-derived afterwards (spec 151). <b>This is the number
    /// that qualifies the channel</b>: a channel whose mass is mostly inferred is measuring a reconstruction,
    /// and any artifact ranking it must say so. It is 0 unless
    /// <c>Radar:Scoring:InferLegacyCollectorAttribution</c> is enabled.
    /// </param>
    /// <param name="UnattributedSignals">
    /// How many sit on evidence with no establishable collector. <b>Stated plainly because it is weaker than
    /// it looks: for a COLLECTOR channel this is structurally 0</b> — <c>ScoringChannel.Consumes</c> is false
    /// for a null collector name, so an unattributed signal can never be one of that channel's consumed
    /// signals. It is meaningful only for the BREADTH channel, which consumes on reach rather than on
    /// provenance and therefore does count unattributed signals. The window-wide unattributed fraction is not
    /// a per-channel fact and is deliberately not reported here.
    /// </param>
    private sealed record ChannelBreakdown(
        string Name,
        string Kind,
        double Weight,
        double Saturation,
        double Score,
        double WeightedContribution,
        int SignalCount,
        bool Dark,
        IReadOnlyList<string> Collectors,
        IReadOnlyList<string> CollectorsRan,
        IReadOnlyList<string> CollectorsNotRun,
        int RecordedSignals,
        int InferredSignals,
        int UnattributedSignals);

    /// <summary>
    /// The <c>ComponentJson</c> shape. The first five properties are <see cref="ScoreComponents"/>' exactly,
    /// by name and order, so the enrichment is backward-compatible with any reader that deserializes it as
    /// <see cref="ScoreComponents"/>.
    /// </summary>
    /// <param name="Composite">
    /// The unrounded weighted channel sum, BEFORE <paramref name="Discount"/> is applied —
    /// <c>OpportunityScore ≈ round(100 · Composite · Discount)</c>.
    /// </param>
    /// <param name="Discount">
    /// The spec-149 notedness discount actually applied to <paramref name="Composite"/>. Recorded because it
    /// is a multiplicative transform on the headline number and because the curated
    /// <c>FollowingTier</c> that feeds it is not otherwise present anywhere in a v9 snapshot; without it a
    /// reader cannot reconcile the composite with the Opportunity score. <c>1.0</c> means it was inert.
    /// </param>
    private sealed record V9ComponentJson(
        int TrajectoryScore,
        int OpportunityScore,
        int AttentionScore,
        int EvidenceConfidenceScore,
        int SignalVelocityScore,
        string Formula,
        double Composite,
        double Discount,
        IReadOnlyList<ChannelBreakdown> Channels);
}
