using System.Text.Json;

using Radar.Application.Collectors;

namespace Radar.Application.Scoring;

/// <summary>
/// The opt-in <see cref="IScoreFormula"/> <c>radar-formula-v11</c> (spec 157): a channel-composition formula
/// in which <b>neutral evidence does not enter a collector channel's score at all</b> — the AD-16 rule
/// "neutral volume must never amplify a directional read", made structural.
/// <para>
/// <b>THE ONE CHANGE FROM <c>radar-formula-v10</c>, in symbols.</b> A collector channel still scores
/// <c>saturation · max(0, preponderance)</c>, but its <c>saturation</c> is computed over
/// <b>DIRECTIONAL-ONLY activity</b> (<see cref="ScoreSignalMath.DirectionalActivityMass"/> — the
/// <c>strength · confidence · recency · quality</c> mass over Positive and Negative signals only) instead of
/// v10's all-signal <see cref="ScoreSignalMath.ActivityMass"/>. v10 already zeroed the DIRECTION factor for a
/// channel with no directional mass, but a Neutral signal still raised the channel's saturation, so neutral
/// coverage AMPLIFIED a directional read — pinned by
/// <c>RadarScoreFormulaV10Tests.NeutralCoverage_StillAmplifiesAGenuineDirectionalRead</c>, which stays exactly
/// as it is because v10 is still shipped and still the control. Under v11, adding any number of Neutral
/// signals changes a collector channel's score by <b>exactly zero</b>, bit-for-bit.
/// </para>
///
/// <para><b>WHICH SIDE OF THE LINE AN ABSENT/UNKNOWN DIRECTION FALLS ON, stated rather than left to the
/// reader.</b> Directional activity is <c>DirectionalMasses(...).Total</c>, which admits a signal iff
/// <see cref="ScoreSignalMath.DirectionSign"/> is non-zero — i.e. exactly <c>Positive</c> and
/// <c>Negative</c>. <c>Neutral</c>, <c>Mixed</c>, and any future/unmapped direction value all sign to 0 and
/// are therefore EXCLUDED from directional activity: an unknown direction is treated as "says nothing about
/// direction", never as directional mass.</para>
///
/// <para><b>THE §2 CONTRACT — three levels, three different guarantees.</b>
/// <list type="bullet">
/// <item><b>Collector channel score:</b> exactly invariant to Neutral additions (the guarantee above).</item>
/// <item><b>Final <see cref="ScoreComponents.OpportunityScore"/>:</b> Neutral additions may leave it
/// unchanged or REDUCE it — a Neutral third-party article still raises <c>AttentionScore</c>, which deepens
/// the spec-149 notedness discount — but can never increase it. With no breadth channel (below), that holds
/// BY CONSTRUCTION: a Neutral signal touches no collector channel and can only deepen the discount.</item>
/// <item><b>Diagnostic components:</b> permitted to change. Neutral evidence still counts IN FULL toward
/// <see cref="ScoreComponents.EvidenceConfidenceScore"/>, <see cref="ScoreComponents.SignalVelocityScore"/>
/// and <see cref="ScoreComponents.AttentionScore"/> (all keeping their exact v8 meanings over the gated set,
/// byte-identical to v10 over the same signals), still counts in each channel's <c>SignalCount</c>, still
/// keeps the channel out of <c>Dark</c>, and still emits its own evidence-linked
/// <see cref="ScoreContribution"/> naming the channel that consumed it. This formula removes a directional
/// AMPLIFICATION, never the evidence.</item>
/// </list></para>
///
/// <para><b>A BREADTH CHANNEL IS REJECTED AT CONSTRUCTION (spec 157 §3, amended after spec 158 measured
/// it).</b> Both ways of admitting one are closed: POSITIVE-ONLY breadth is structurally zero in the current
/// collector mix (spec 158 measured <c>Var(positive reach) = 0</c> across all 43 companies — spec 70 makes
/// every news signal Neutral and first-party RSS is not a third-party publisher — so a declared breadth
/// weight would be silently dead under the never-renormalise rule), and UNFILTERED breadth would let a
/// Neutral news item raise reach and therefore <c>OpportunityScore</c>, breaking the contract above. See
/// <c>docs/158-channel-feasibility-findings.md</c>. If the collector mix later produces Positive third-party
/// signals, positive-only breadth becomes viable again and earns <c>radar-formula-v12</c> under AD-6 — it
/// does not get retrofitted here. The retained <see cref="ScoreSignalMath.PositiveAttentionReach"/> helper is
/// consequently consumed by NO shipped formula.</para>
///
/// <para><b>⚠ THE <c>AttentionScore</c> COMPONENT IS NOT TOUCHED BY ANY OF THIS.</b> It keeps its v8 meaning
/// over the whole gated set, exactly as v10 keeps it, and feeds the notedness discount exactly as v10 feeds
/// it (once, on the COMPOSED score, via <see cref="ScoreSignalMath.NotednessDiscount"/>). Breadth-the-CHANNEL
/// and attention-the-DIAGNOSTIC both derive from publisher reach and are easy to conflate; narrowing the
/// diagnostic would corrupt AD-16's secondary comparator <c>baseline-attention-score</c>, which must remain
/// "all attention so far".</para>
///
/// <para><b>ALL-NEUTRAL vs ABSENT, still distinguishable.</b> An all-Neutral channel (<c>Score 0</c>,
/// <c>Dark false</c>, <c>SignalCount &gt; 0</c>, <c>DirectionState "none"</c>) differs from an absent one
/// (<c>Score 0</c>, <c>Dark true</c>, <c>SignalCount 0</c>) in the RECORD, exactly as under v10 — and the
/// distinction carries even more weight here, because under v11 the score separates them even less.</para>
///
/// <para><b>SCORES ARE ON A LOWER ABSOLUTE SCALE THAN v10's</b> wherever neutral coverage used to feed
/// saturation. v10 and v11 absolute scores are NOT comparable; only rankings are — the same caveat v10
/// carries against v9. Re-tuning weights or saturation constants to compensate is explicitly out of scope
/// (spec 157) — measure first.</para>
///
/// <para><b>v8, v9 AND v10 ARE UNTOUCHED.</b> Under AD-6 a structural change earns a new class, so this is
/// one — additive, opt-in per strategy, leaving all three predecessors available as controls (the live
/// <c>disclosure-led-v10-control</c> arm runs v10 over an identical budget precisely so any ranking
/// difference is attributable to directional-only versus all-signal collector saturation). No existing
/// strategy's <c>ScoringConfigVersion</c> moves.</para>
///
/// <para>Pure and deterministic (no clock, no randomness, no I/O). Emits exactly one provenance-carrying
/// contribution per current-window signal, in input order, each naming the channel(s) that consumed it, and
/// never from <see cref="ScoringInput.PreviousSignals"/>.</para>
/// </summary>
public sealed class RadarScoreFormulaV11 : IScoreFormula
{
    /// <summary>
    /// THE COMPOSITION REVISION OF THE EXPRESSIONS DIRECTLY BELOW — the same obligation
    /// <see cref="RadarScoreFormulaV10"/> carries (spec 153's mechanism, spec 157 §4 requires v11 to carry
    /// its own).
    /// <para>
    /// <b>OBLIGATION, and it is not optional:</b> if you change how this formula COMPOSES its score — the
    /// activity measure feeding a channel's saturation, the direction factor, where the notedness discount
    /// lands, what the composite is multiplied by, which components are computed over which set, or the
    /// breadth-rejection contract — you MUST bump this token in the same change. Bumping it moves every v11
    /// strategy's <c>ScoringConfigVersion</c> (via <see cref="FormulaIdentity.Of"/>), which trips
    /// <c>StrategyIdentityGuard</c> on the next run and forces a conscious decision about the discontinuity.
    /// <c>RadarScoreFormulaV11CompositionGuardTests</c> pins this token together with v11's full output and
    /// the fingerprint a default-weights v11 strategy stamps, so the three can only move together.
    /// </para>
    /// <para>
    /// It is NOT a substitute for AD-6: a genuinely new STRUCTURE still earns <c>radar-formula-v12</c>.
    /// </para>
    /// </summary>
    private const string Revision = "rev1";

    private readonly ScoringWeights _weights;
    private readonly IAttentionSourceWeights _sourceWeights;
    private readonly ScoringChannelSet _channels;
    private readonly ICollectorAttributionResolver _attribution;

    /// <summary>
    /// <b>THE DIRECTION FACTOR:</b> <c>saturation · max(0, preponderance)</c> — byte-for-byte the same
    /// expression as <see cref="RadarScoreFormulaV10"/>'s (no directional mass ⇒ exactly 0; balanced ⇒
    /// exactly 0; net-negative floored at 0, deterioration being Trajectory's job). What differs in v11 is
    /// the SATURATION fed into it — see <see cref="DirectionalActivity"/>.
    /// <para><b>Changing this expression obliges a <see cref="Revision"/> bump</b> (see the note there).</para>
    /// </summary>
    private static readonly CollectorChannelScore DirectionFactor =
        (saturation, preponderance) => saturation * Math.Max(0.0, preponderance);

    /// <summary>
    /// <b>THE ACTIVITY MEASURE — the whole of spec 157 §1:</b> a collector channel's saturation is built on
    /// <see cref="ScoreSignalMath.DirectionalActivityMass"/> (the directional-only mass extracted by spec
    /// 158), not on v10's all-signal <see cref="ScoreSignalMath.ActivityMass"/>. Neutral/Mixed — and any
    /// unknown direction, which signs to 0 — contribute exactly zero here, so neutral volume cannot raise the
    /// saturation and thereby amplify a directional read.
    /// <para><b>Changing this expression obliges a <see cref="Revision"/> bump</b> (see the note there).</para>
    /// </summary>
    private static readonly ChannelActivityMass DirectionalActivity =
        ScoreSignalMath.DirectionalActivityMass;

    /// <summary>
    /// UNREACHABLE BY CONSTRUCTION: the constructor rejects every breadth channel, so the shared pass can
    /// never take its breadth branch for a v11 budget. A throwing delegate rather than a plausible-looking
    /// reach term, deliberately — silently inheriting <see cref="ScoreSignalMath.AttentionReach"/> (or the
    /// retained <see cref="ScoreSignalMath.PositiveAttentionReach"/>, which no shipped formula consumes)
    /// would quietly reintroduce exactly the channel spec 157 §3 rejects if the constructor guard ever
    /// weakened.
    /// </summary>
    private static readonly BreadthChannelReach NoBreadth =
        (_, _, _, _) => throw new InvalidOperationException(
            $"{ScoreFormulaVersions.V11} scored a breadth channel, which its constructor rejects "
                + "(spec 157 §3 / spec 158). This is a bug in the formula wiring, not a configuration error.");

    /// <summary>
    /// Constructs the formula with the strategy's magnitudes, the shared publisher tier map, and the
    /// strategy's validated channel array — the same contract <see cref="RadarScoreFormulaV10"/> has, so the
    /// factory builds both from the same definition. Fails fast on a nonsensical weight
    /// (<see cref="ScoringWeights.Validate"/>), on an empty channel set, and on a BREADTH channel (see the
    /// class remarks; <see cref="ScoringStrategySet"/> applies the same rule at the config boundary through
    /// <see cref="ScoreFormulaVersions.RejectsBreadthChannels"/>, naming the strategy — this guard is the
    /// second line of defence for a definition composed in code).
    /// </summary>
    /// <param name="attributionResolver">
    /// How the collector behind each signal's evidence is established (spec 151). Optional and defaulting to
    /// <see cref="RecordedOnlyCollectorAttributionResolver"/> — recorded attribution only, i.e. no inference.
    /// </param>
    public RadarScoreFormulaV11(
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
                $"{ScoreFormulaVersions.V11} requires at least one channel; a channel-composition formula with "
                    + "no channels would score every company 0. Declare Channels on the strategy, or use "
                    + $"{ScoreFormulaVersions.V8}.");
        }

        var breadth = channels.Channels
            .Where(c => c.Kind == ScoringChannelKind.Breadth)
            .Select(c => c.Name)
            .ToArray();
        if (breadth.Length > 0)
        {
            throw new InvalidOperationException(
                $"{ScoreFormulaVersions.V11} rejects breadth channel(s) {string.Join(", ", breadth)}: "
                    + "spec 158 measured positive-only breadth as structurally ZERO in the current collector "
                    + "mix (no third-party publisher can qualify, so the declared weight would be silently "
                    + "dead under the never-renormalise rule), and unfiltered breadth would let a Neutral "
                    + "news item raise OpportunityScore, which contradicts AD-16. See "
                    + "docs/158-channel-feasibility-findings.md. Declare collector channels only, or use "
                    + $"{ScoreFormulaVersions.V10} if you want breadth.");
        }

        _weights = weights;
        _sourceWeights = sourceWeights;
        _channels = channels;
        _attribution = attributionResolver ?? RecordedOnlyCollectorAttributionResolver.Instance;
    }

    /// <inheritdoc />
    public string Version => ScoreFormulaVersions.V11;

    /// <inheritdoc />
    public string CompositionRevision => Revision;

    /// <summary>The strategy's declared channel budget (exposed for provenance/tests; immutable).</summary>
    public ScoringChannelSet Channels => _channels;

    /// <inheritdoc />
    public ScoreComputation Compute(ScoringInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var signals = input.Signals;
        var windowLength = input.WindowEndUtc - input.WindowStartUtc;

        // Shared per-signal primitives — identical to what v8/v9/v10 compute, over the same set.
        var recency = ScoreSignalMath.RecencyFactors(
            signals, input.WindowStartUtc, input.WindowEndUtc, _weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(signals, _weights);

        // ---- The channels ----
        // THE SHARED PASS (spec 153/154/158): selection, collector attribution + tally, the ran/not-run
        // split, activity → saturation → preponderance, per-signal channel attribution and the weighted
        // composite. This formula contributes exactly two expressions to it — DirectionalActivity (the whole
        // of spec 157 §1) and DirectionFactor (v10's, unchanged). The breadth delegate is unreachable by
        // construction; see NoBreadth.
        var composition = ScoringChannelComposition.Compose(
            input,
            recency,
            quality,
            _channels,
            _weights,
            _sourceWeights,
            _attribution,
            DirectionalActivity,
            DirectionFactor,
            NoBreadth);

        var breakdown = composition.Channels.Select(ToBreakdown).ToList();
        var composite = composition.Composite;

        // ---- The four v8-meaning components, over the strategy's gated set ----
        // Identical to v10's, through the same shared primitives: Neutral evidence counts here IN FULL. An
        // empty window short-circuits to zeros exactly as v8/v9/v10 do.
        var trajectoryScore = 0;
        var attentionScore = 0;
        var evidenceConfidenceScore = 0;
        var signalVelocityScore = 0;

        if (signals.Count > 0)
        {
            trajectoryScore = ScoreSignalMath.TrajectoryScore(signals, recency, _weights);

            // FULL-SET AttentionReach, deliberately — the diagnostic keeps its v8 meaning (see the ⚠ class
            // remark). Only breadth-the-CHANNEL was rejected; attention-the-DIAGNOSTIC is untouched.
            var reach = ScoreSignalMath.AttentionReach(
                signals, input.PreCollapseSignals, _weights, _sourceWeights);
            attentionScore = ScoreSignalMath.AttentionComponent(reach, _weights);

            evidenceConfidenceScore = ScoreSignalMath.EvidenceConfidenceScore(signals, _weights);

            signalVelocityScore = ScoreSignalMath.SignalVelocityScore(
                signals, input.PreviousSignals, _weights);
        }

        // ---- The notedness discount (spec 149), applied exactly as v9 and v10 apply it ----
        // ONCE, to the COMPOSED channel score, never per channel. This is the path by which a Neutral
        // third-party article may still LOWER a v11 OpportunityScore (more attention ⇒ deeper discount) —
        // the permitted direction under the §2 contract. It can never raise it.
        var notednessDiscount = ScoreSignalMath.NotednessDiscount(
            _weights, attentionScore, input.FollowingTier);

        var opportunityScore = ScoreSignalMath.Clamp0To100(100.0 * composite * notednessDiscount);

        var components = new ScoreComponents(
            TrajectoryScore: trajectoryScore,
            OpportunityScore: opportunityScore,
            AttentionScore: attentionScore,
            EvidenceConfidenceScore: evidenceConfidenceScore,
            SignalVelocityScore: signalVelocityScore);

        // ---- Contributions (provenance — current window only) ----
        // Shared with v9/v10: exactly one contribution per current-window signal, in input order, INCLUDING
        // the Neutral ones (which weigh 0 and still name their channel) and including signals no channel
        // consumed. Spec 157 blinds the SCORE to neutral volume, never the evidence trail.
        var contributions = ScoringChannelComposition.BuildContributions(signals, recency, composition);

        var windowDays = (int)Math.Round(windowLength.TotalDays, MidpointRounding.AwayFromZero);
        var channelSummary = ScoringChannelComposition.DescribeChannels(composition.Channels);

        // The discount is named ONLY when it actually moved the number (spec 149's rule, kept).
        var notednessSummary = notednessDiscount == 1.0
            ? string.Empty
            : $"; × notedness {notednessDiscount:0.000}";
        var explanation =
            $"{ScoreFormulaVersions.V11}: {signals.Count} signal(s) over {windowDays}d across "
                + $"{breakdown.Count} channel(s) → Opportunity {opportunityScore} (composite {composite:0.000} = "
                + $"{channelSummary}{notednessSummary}); Trajectory {trajectoryScore}, Attention {attentionScore}, "
                + $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}.";

        // ComponentJson keeps ScoreComponents' five properties FIRST and by the same names, so an existing
        // reader that deserializes it as ScoreComponents is unaffected. The shape is v10's EXACTLY — no new
        // property is appended, because the one number v11 changes the meaning of is already recorded:
        // for a v11 collector channel, DirectionalMass IS the raw activity that fed its saturation
        // (DirectionalActivityMass ≡ DirectionalMasses(...).Total over the same sub-slices), so the
        // breakdown is verifiable by hand without a second copy of the same value.
        var componentJson = JsonSerializer.Serialize(new V11ComponentJson(
            TrajectoryScore: components.TrajectoryScore,
            OpportunityScore: components.OpportunityScore,
            AttentionScore: components.AttentionScore,
            EvidenceConfidenceScore: components.EvidenceConfidenceScore,
            SignalVelocityScore: components.SignalVelocityScore,
            Formula: Version,
            Revision: Revision,
            Composite: composite,
            Discount: notednessDiscount,
            Channels: breakdown));

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }

    /// <summary>
    /// Projects the shared <see cref="ChannelComputation"/> onto THIS formula's persisted channel shape —
    /// <c>radar-formula-v10</c>'s sixteen properties exactly (same names, same order), so a reader written
    /// against a v10 breakdown still works unchanged. Every declared channel is a collector channel here, so
    /// the three directional properties are always populated.
    /// </summary>
    private static ChannelBreakdown ToBreakdown(ChannelComputation computed) => new(
        Name: computed.Channel.Name,
        Kind: ScoringChannelSet.KindToken(computed.Channel.Kind),
        Weight: computed.Channel.Weight,
        Saturation: computed.Channel.Saturation,
        Score: computed.Score,
        WeightedContribution: computed.WeightedContribution,
        SignalCount: computed.SignalCount,
        Dark: computed.Dark,
        Collectors: computed.Channel.Collectors,
        CollectorsRan: computed.CollectorsRan,
        CollectorsNotRun: computed.CollectorsNotRun,
        RecordedSignals: computed.RecordedSignals,
        InferredSignals: computed.InferredSignals,
        UnattributedSignals: computed.UnattributedSignals,
        Preponderance: computed.Direction?.Preponderance,
        DirectionalMass: computed.Direction?.DirectionalMass,
        DirectionState: computed.Direction?.State);

    /// <summary>
    /// One channel's audited share, serialized into <c>ComponentJson</c> — <c>radar-formula-v10</c>'s shape
    /// verbatim (see <see cref="RadarScoreFormulaV10"/> for the per-property semantics).
    /// </summary>
    /// <param name="SignalCount">
    /// How many current-window signals this channel consumed, <b>Neutral signals counted in full</b> — which
    /// is how an all-Neutral channel remains visibly covered despite scoring 0.
    /// </param>
    /// <param name="Dark">
    /// True when the channel consumed no signals at all. Even more load-bearing under v11 than under v10:
    /// with directional-only saturation, <paramref name="Dark"/> plus <paramref name="SignalCount"/> is the
    /// only thing separating "we looked and found activity that says nothing about direction" from "we
    /// looked and found nothing".
    /// </param>
    /// <param name="DirectionalMass">
    /// <c>Mpos + Mneg</c> — and, for a v11 collector channel, <b>also the raw activity that fed its
    /// saturation</b> (spec 157 §1), so <c>Score = (m/(m+S)) · max(0, Preponderance)</c> is verifiable by
    /// hand from this record.
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
        int UnattributedSignals,
        double? Preponderance,
        double? DirectionalMass,
        string? DirectionState);

    /// <summary>
    /// The <c>ComponentJson</c> shape — <c>radar-formula-v10</c>'s exactly (the first five properties are
    /// <see cref="ScoreComponents"/>' by name and order; <c>Formula</c>/<c>Revision</c> identify what
    /// produced it; <c>Composite</c> is the unrounded weighted channel sum before <c>Discount</c>).
    /// </summary>
    private sealed record V11ComponentJson(
        int TrajectoryScore,
        int OpportunityScore,
        int AttentionScore,
        int EvidenceConfidenceScore,
        int SignalVelocityScore,
        string Formula,
        string Revision,
        double Composite,
        double Discount,
        IReadOnlyList<ChannelBreakdown> Channels);
}
