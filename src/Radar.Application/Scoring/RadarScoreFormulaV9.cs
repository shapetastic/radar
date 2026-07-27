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
/// genuine breadth contributes MORE.</b> v8's inverse attention discount stays in v8 and is deliberately not
/// carried over.</item>
/// </list>
/// Direction, confidence, strength, recency and quality semantics are byte-identical to v8 — they come from
/// the shared <see cref="ScoreSignalMath"/>. Only the SET each term is computed over changes.
/// </para>
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
/// mapped as <c>composite · 100</c> into <see cref="ScoreComponents.OpportunityScore"/> — the field
/// <c>WeeklyReportBuilder</c> ranks by and the spec-101/108 efficacy read side consumes, i.e. the one that
/// has to carry "this strategy's answer". The other four components keep their exact v8 meanings, computed
/// over the strategy's gated signal set, so <c>WeeklyReportActionPolicyV1</c>'s Trajectory /
/// EvidenceConfidence thresholds remain valid and the report stays legible for a v9 strategy. The unrounded
/// composite and the full per-channel breakdown are additionally carried in <c>ComponentJson</c>, whose first
/// five properties are still exactly <see cref="ScoreComponents"/>' so any existing reader keeps
/// working.</para>
///
/// <para><b>"Ran and found nothing" vs "did not run".</b> A channel scores 0 whether its source was down or
/// genuinely quiet — Radar scores evidence, and absence of evidence is not evidence — but the per-channel
/// provenance records WHICH it was, by splitting each channel's declared collectors against
/// <see cref="ScoringInput.EnabledCollectors"/>. Under a spec-139 replay that reflects the REPLAYING
/// process's registered collectors, not the historical run's; the historical answer is on each snapshot's
/// own <c>CollectionProvenance</c>.</para>
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

    /// <summary>
    /// Constructs the formula with the strategy's magnitudes, the shared publisher tier map, and the
    /// strategy's validated channel array. There is deliberately no parameterless construction: all three
    /// are config data supplied by Infrastructure (AD-5) and all three must be immutable so the formula stays
    /// a pure function (AD-3). Fails fast on a nonsensical weight (<see cref="ScoringWeights.Validate"/>) and
    /// on an empty channel set — a v9 strategy with no channels could only ever score 0.
    /// </summary>
    public RadarScoreFormulaV9(
        ScoringWeights weights, IAttentionSourceWeights sourceWeights, ScoringChannelSet channels)
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

        // The recorded collector behind each signal's evidence (spec 146). Null for legacy evidence that
        // predates the recording — such a signal is consumed by NO collector channel and contributes 0, which
        // is exactly what the standing "never backfill accrued history" rule implies.
        var collectorOf = new string?[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            collectorOf[i] = CollectionProvenanceMetadata.Read(signals[i].Evidence);
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
            int consumed;
            // "Nothing to measure" — the state the no-renormalisation rule is about. It is NOT the same as
            // "scored 0": a collector channel whose signals are uniformly negative also scores 0, and that is
            // a measurement, not an absence. So it is recorded per kind at the source rather than inferred
            // from the score.
            bool dark;

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
                consumed = 0;
                for (var i = 0; i < signals.Count; i++)
                {
                    if (ScoreSignalMath.ContributesToReach(signals[i]))
                    {
                        consumed++;
                        (channelsPerSignal[i] ??= []).Add(channel.Name);
                    }
                }
            }
            else
            {
                var indices = new List<int>();
                for (var i = 0; i < signals.Count; i++)
                {
                    if (channel.Consumes(collectorOf[i]))
                    {
                        indices.Add(i);
                        (channelsPerSignal[i] ??= []).Add(channel.Name);
                    }
                }

                consumed = indices.Count;
                dark = consumed == 0;

                // Sub-slices in the SAME input order, so the shared primitives see exactly the shape they see
                // for a whole window — the channel changes the SET, never the math.
                var subSignals = indices.Select(i => signals[i]).ToList();
                var subRecency = indices.Select(i => recency[i]).ToList();
                var subQuality = indices.Select(i => quality[i]).ToList();

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

            breakdown.Add(new ChannelBreakdown(
                Name: channel.Name,
                Kind: ScoringChannelSet.KindToken(channel.Kind),
                Weight: channel.Weight,
                Saturation: channel.Saturation,
                Score: channelScore,
                WeightedContribution: channel.Weight * channelScore,
                SignalCount: consumed,
                Dark: dark,
                Collectors: channel.Collectors,
                CollectorsRan: ran,
                CollectorsNotRun: notRun));
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

        // The composite IS this strategy's answer, so it lands in OpportunityScore — see the range
        // reconciliation in the class remarks. NOTE the deliberate asymmetry with v8: v9's Opportunity is NOT
        // discounted by attention (that inversion stays in v8); breadth earns its share positively instead.
        var opportunityScore = ScoreSignalMath.Clamp0To100(100.0 * composite);

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
                        + $"confidence {signal.Confidence:0.00} — {DescribeAttribution(channelsPerSignal[i], collectorOf[i])}",
                ContributionWeight: weight));
        }

        var windowDays = (int)Math.Round(windowLength.TotalDays, MidpointRounding.AwayFromZero);
        var channelSummary = string.Join(
            ", ",
            breakdown.Select(c =>
                $"{c.Name} {c.Score:0.000}×{c.Weight:0.00}{(c.Dark ? " (dark)" : string.Empty)}"));
        var explanation =
            $"{ScoreFormulaVersions.V9}: {signals.Count} signal(s) over {windowDays}d across "
                + $"{breakdown.Count} channel(s) → Opportunity {opportunityScore} (composite {composite:0.000} = "
                + $"{channelSummary}); Trajectory {trajectoryScore}, Attention {attentionScore}, "
                + $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}.";

        // ComponentJson keeps ScoreComponents' five properties FIRST and by the same names, so an existing
        // reader that deserializes it as ScoreComponents is unaffected (extra properties are ignored), and
        // adds the unrounded composite plus the per-channel breakdown that makes each share auditable.
        var componentJson = JsonSerializer.Serialize(new V9ComponentJson(
            TrajectoryScore: components.TrajectoryScore,
            OpportunityScore: components.OpportunityScore,
            AttentionScore: components.AttentionScore,
            EvidenceConfidenceScore: components.EvidenceConfidenceScore,
            SignalVelocityScore: components.SignalVelocityScore,
            Formula: Version,
            Composite: composite,
            Channels: breakdown));

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }

    /// <summary>The half-saturation shape <c>x/(x+S)</c>, in <c>[0,1)</c> for non-negative <c>x</c>.</summary>
    private static double Saturate(double raw, double halfSaturation) => raw / (raw + halfSaturation);

    private static string DescribeAttribution(IReadOnlyList<string>? channels, string? collector)
    {
        if (channels is { Count: > 0 })
        {
            return $"channel {string.Join(" + ", channels)}";
        }

        // Explicitly distinguishes the two reasons a signal fed no collector channel, because they mean very
        // different things: legacy evidence that predates collector recording (never backfilled, by rule)
        // versus a collector this strategy simply did not budget for.
        return collector is null
            ? "no channel (evidence has no recorded collector)"
            : $"no channel (collector {collector} is not budgeted by this strategy)";
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
        IReadOnlyList<string> CollectorsNotRun);

    /// <summary>
    /// The <c>ComponentJson</c> shape. The first five properties are <see cref="ScoreComponents"/>' exactly,
    /// by name and order, so the enrichment is backward-compatible with any reader that deserializes it as
    /// <see cref="ScoreComponents"/>.
    /// </summary>
    private sealed record V9ComponentJson(
        int TrajectoryScore,
        int OpportunityScore,
        int AttentionScore,
        int EvidenceConfidenceScore,
        int SignalVelocityScore,
        string Formula,
        double Composite,
        IReadOnlyList<ChannelBreakdown> Channels);
}
