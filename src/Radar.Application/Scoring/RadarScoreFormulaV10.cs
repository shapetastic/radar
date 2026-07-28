using System.Text.Json;

using Radar.Application.Collectors;

namespace Radar.Application.Scoring;

/// <summary>
/// The opt-in <see cref="IScoreFormula"/> <c>radar-formula-v10</c> (spec 153): a channel-composition formula
/// in which <b>neutral evidence establishes COVERAGE but contributes no DIRECTIONAL OPPORTUNITY</b>.
/// <para>
/// <b>THE ONE CHANGE FROM <c>radar-formula-v9</c>, in symbols.</b> A collector channel scores
/// <c>saturation · max(0, preponderance)</c>, where v9 scored <c>saturation · (0.5 + 0.5·preponderance)</c>.
/// Everything else — the breadth channel, the four v8-meaning components, the spec-149 notedness discount, the
/// composite, the contribution chain, the no-renormalisation rule — is v9's, reached through the shared
/// <see cref="ScoringChannelComposition"/> and <see cref="ScoreSignalMath"/> rather than copied.
/// </para>
///
/// <para><b>WHY.</b> v9's factor puts a channel with no directional mass at exactly <c>0.5</c>. The code
/// called that "neither rewarded nor punished", and relative to a MIXED channel it is — but relative to an
/// INACTIVE channel, which contributes 0, an all-Neutral channel scores <c>saturation · 0.5</c>, RISING WITH
/// ACTIVITY. Volume alone produced score. That is not a corner case: measured on the live store,
/// <b>87.6% of all 49,793 signals are Neutral</b> (Positive 8.1%, Negative 4.3%), and it landed hardest on
/// exactly the strategies built to test the thesis — a <c>sec-form4</c> channel sees routine Form 4s extracted
/// as Neutral <c>InsiderBuying</c>, and a <c>sec-13dg</c> channel sees passive 13G filings that spec 99 made
/// Neutral BY DESIGN so they could never misfire bullish. Such a strategy was substantially ranking FILING
/// VOLUME, which larger companies produce more of — close to the inverse of Radar's purpose. The corroborating
/// symptom: five deliberately-different strategies backtested on 2026-07-28 came in at in-sample Spearman
/// ρ −0.0849 / −0.0969 / −0.0999 / −0.1000 / −0.1009, a spread of 0.016, which is what a single common factor
/// dominating all five looks like.</para>
///
/// <para><b>THE ALL-NEUTRAL vs BALANCED QUESTION, DECIDED AND RECORDED.</b> Two states reach
/// <c>preponderance = 0</c> and they are genuinely different: a channel with <b>no directional signals at
/// all</b>, and a channel whose <b>positive and negative mass cancel</b>. <b>Both contribute exactly zero
/// directional opportunity</b>, because Opportunity answers "is this trajectory improving?" and neither state
/// provides any evidence that it is. They differ in the EVIDENCE TRAIL, not in the score — and the trail
/// carries the difference explicitly, in each channel's <c>DirectionState</c>
/// (<see cref="ChannelDirectionState.None"/> vs <see cref="ChannelDirectionState.Balanced"/>) alongside the
/// raw preponderance and total directional mass. <b>A net-NEGATIVE channel also floors at 0</b>, for two
/// reasons that point the same way: v10's Opportunity measures IMPROVEMENT, and deterioration is what the
/// (v8-meaning) <see cref="ScoreComponents.TrajectoryScore"/> component — which v10 keeps unchanged — is for;
/// and a negative channel share would SUBTRACT from other channels' genuine findings, breaking the
/// <c>[0,1]</c> share semantics the whole budget rests on.</para>
///
/// <para><b>THE BREADTH CHANNEL IS UNCHANGED — and the tension in that is recorded, not hidden.</b> It is
/// still <c>reach/(reach + S_c)</c> over the tier-weighted distinct-publisher
/// <see cref="ScoreSignalMath.AttentionReach"/>, and the spec-149 notedness discount is still applied exactly
/// as v9 applies it: ONCE, to the composed score, through the shared
/// <see cref="ScoreSignalMath.NotednessDiscount"/>. <b>The honest tension:</b> a breadth channel therefore
/// still earns share from pure COVERAGE, which is adjacent to the very "volume alone produces score" problem
/// this formula exists to fix. It is kept deliberately, on two grounds. First, breadth is an explicitly
/// strategy-BUDGETED measure of NOTICE, not of improvement — a strategy that does not want to pay for notice
/// simply does not declare the channel, and then it costs nothing (unlike v9's 0.5 floor, which a strategy
/// could not opt out of). Second, it is already damped by the notedness discount, so attention enters v10
/// twice with opposite signs exactly as it does v9: as budgetable positive breadth, and as the company-level
/// fame that damps whatever was found. Spec 153's MEASURED target is the direction factor on collector
/// channels; re-tuning or removing breadth is explicitly out of scope, and would need its own evidence.</para>
///
/// <para><b>NEUTRAL EVIDENCE IS NOT DISCARDED — this slice removes a directional CONTRIBUTION, never the
/// evidence.</b> A Neutral signal still counts as ACTIVITY in its channel's saturation, so neutral coverage
/// AMPLIFIES a genuine directional read (a positive channel with more neutral corroboration around it scores
/// HIGHER); it still counts in <see cref="ScoreComponents.EvidenceConfidenceScore"/> and
/// <see cref="ScoreComponents.SignalVelocityScore"/>; it still counts in the channel's
/// <c>SignalCount</c> and keeps the channel out of the <c>Dark</c> state; and it still emits its own
/// provenance-carrying <see cref="ScoreContribution"/> naming the channel that consumed it. An all-Neutral
/// channel (<c>Score 0</c>, <c>Dark false</c>, <c>SignalCount &gt; 0</c>) is therefore distinguishable in the
/// breakdown from an absent one (<c>Score 0</c>, <c>Dark true</c>, <c>SignalCount 0</c>) — same score,
/// different record.</para>
///
/// <para><b>SCORES ARE ON A LOWER ABSOLUTE SCALE THAN v9's.</b> Removing a 0.5 floor from every collector
/// channel lowers essentially every v10 score relative to the same strategy under v9. <b>v9 and v10 absolute
/// scores are therefore NOT comparable; only rankings are.</b> That is the intended consequence of the change,
/// not a calibration defect, and re-tuning channel weights or saturations to compensate is deliberately out of
/// scope (spec 153) — measure first.</para>
///
/// <para><b>RANGE.</b> <c>max(0, preponderance)</c> lies in <c>[0,1)</c> (the corroboration smoother
/// <c>k &gt; 0</c> keeps the ratio strictly inside <c>(-1,1)</c>) and <c>saturation ∈ [0,1)</c>, so a channel
/// score stays in <c>[0,1)</c> — the composite range contract, the <c>[0,1]</c> clamp and the
/// <b>NEVER-RENORMALISE</b> rule are all untouched. See <see cref="ScoringChannelSet"/>: a channel that
/// produced nothing scores 0 and the denominator does not shrink.</para>
///
/// <para><b>v8 AND v9 ARE UNTOUCHED.</b> Under AD-6 a component-shape change earns a new class, so this is
/// one — additive, opt-in per strategy, and leaving both predecessors available as the controls that make the
/// change measurable (exactly as v8 remained when v9 shipped). No existing strategy's
/// <c>ScoringConfigVersion</c> moves.</para>
///
/// <para>Pure and deterministic (no clock, no randomness, no I/O). Emits exactly one provenance-carrying
/// contribution per current-window signal, in input order, each naming the channel(s) that consumed it, and
/// never from <see cref="ScoringInput.PreviousSignals"/>.</para>
/// </summary>
public sealed class RadarScoreFormulaV10 : IScoreFormula
{
    /// <summary>
    /// THE COMPOSITION REVISION OF THE EXPRESSION DIRECTLY BELOW.
    /// <para>
    /// <b>OBLIGATION, and it is not optional:</b> if you change how this formula COMPOSES its score — the
    /// direction factor, where the notedness discount lands, what the composite is multiplied by, which
    /// components are computed over which set — you MUST bump this token in the same change. Bumping it moves
    /// every v10 strategy's <c>ScoringConfigVersion</c> (it is folded in via
    /// <see cref="FormulaIdentity.Of"/>), which trips <c>StrategyIdentityGuard</c> on the next run and forces
    /// a conscious decision about the discontinuity. That is exactly what spec 149 could not do to
    /// <c>radar-formula-v9</c>, which silently mixed pre- and post-change scores in one series.
    /// </para>
    /// <para>
    /// It is NOT a substitute for AD-6: a genuinely new STRUCTURE still earns <c>radar-formula-v11</c>. The
    /// revision exists so that an in-place ADJUSTMENT cannot happen invisibly.
    /// <c>RadarScoreFormulaV10CompositionGuardTests</c> pins this token together with v10's full output and
    /// the fingerprint a default-weights v10 strategy stamps, so the three can only move together.
    /// </para>
    /// </summary>
    private const string Revision = "rev1";

    private readonly ScoringWeights _weights;
    private readonly IAttentionSourceWeights _sourceWeights;
    private readonly ScoringChannelSet _channels;
    private readonly ICollectorAttributionResolver _attribution;

    /// <summary>
    /// <b>THE COMPOSITION.</b> A collector channel earns <c>saturation · max(0, preponderance)</c>:
    /// <list type="bullet">
    /// <item>no directional mass at all ⇒ <see cref="ScoreSignalMath.Preponderance"/> is exactly 0 ⇒ the
    /// channel contributes <b>exactly 0</b>;</item>
    /// <item>balanced positive/negative mass ⇒ preponderance is exactly 0 ⇒ <b>also exactly 0</b> (same score,
    /// different <c>DirectionState</c> in the trail — see the class remarks);</item>
    /// <item>net-negative mass ⇒ floored at <b>0</b>, never negative;</item>
    /// <item>net-positive mass ⇒ the channel approaches, but never reaches, its full saturated share, because
    /// the corroboration smoother <c>k</c> keeps the ratio strictly below 1 — so a single loud signal cannot
    /// swing a channel to an extreme.</item>
    /// </list>
    /// <b>Changing this expression obliges a <see cref="Revision"/> bump</b> (see the note there).
    /// </summary>
    private static readonly CollectorChannelScore DirectionFactor =
        (saturation, preponderance) => saturation * Math.Max(0.0, preponderance);

    /// <summary>
    /// Constructs the formula with the strategy's magnitudes, the shared publisher tier map, and the
    /// strategy's validated channel array — the same contract <see cref="RadarScoreFormulaV9"/> has, so the
    /// factory builds both from the same definition. There is deliberately no parameterless construction: all
    /// three are config data supplied by Infrastructure (AD-5) and all three must be immutable so the formula
    /// stays a pure function (AD-3). Fails fast on a nonsensical weight
    /// (<see cref="ScoringWeights.Validate"/>) and on an empty channel set.
    /// </summary>
    /// <param name="attributionResolver">
    /// How the collector behind each signal's evidence is established (spec 151). Optional and defaulting to
    /// <see cref="RecordedOnlyCollectorAttributionResolver"/> — recorded attribution only, i.e. no inference.
    /// </param>
    public RadarScoreFormulaV10(
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
                $"{ScoreFormulaVersions.V10} requires at least one channel; a channel-composition formula with "
                    + "no channels would score every company 0. Declare Channels on the strategy, or use "
                    + $"{ScoreFormulaVersions.V8}.");
        }

        _weights = weights;
        _sourceWeights = sourceWeights;
        _channels = channels;
        _attribution = attributionResolver ?? RecordedOnlyCollectorAttributionResolver.Instance;
    }

    /// <inheritdoc />
    public string Version => ScoreFormulaVersions.V10;

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

        // Shared per-signal primitives — identical to what v8 and v9 compute, over the same set.
        var recency = ScoreSignalMath.RecencyFactors(
            signals, input.WindowStartUtc, input.WindowEndUtc, _weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(signals, _weights);

        // ---- The channels ----
        // The SHARED pass: selection, collector attribution + tally, the ran/not-run split, activity →
        // saturation → preponderance, per-signal channel attribution and the weighted composite. This formula
        // contributes exactly one thing to it — DirectionFactor — which IS the whole of spec 153. (Spec 154
        // made the ACTIVITY measure a parameter too; v10 passes the same ScoreSignalMath.ActivityMass the
        // shared pass used to call directly, so this formula's composition is unchanged and its
        // CompositionRevision correctly does NOT move.)
        var composition = ScoringChannelComposition.Compose(
            input,
            recency,
            quality,
            _channels,
            _weights,
            _sourceWeights,
            _attribution,
            ScoreSignalMath.ActivityMass,
            DirectionFactor,
            // Spec 158 made the breadth REACH a parameter too (so the prospective v11 can narrow it to
            // positive-only); v10 passes the same ScoreSignalMath.AttentionReach the shared pass used to
            // call directly, so this formula's composition is unchanged and its CompositionRevision
            // correctly does NOT move.
            ScoreSignalMath.AttentionReach);

        var breakdown = composition.Channels.Select(ToBreakdown).ToList();
        var composite = composition.Composite;

        // ---- The four v8-meaning components, over the strategy's gated set ----
        // Empty window short-circuits to zeros exactly as v8 and v9 do, so a v10 strategy's "nothing to score"
        // snapshot is indistinguishable from theirs (the composite is 0 there anyway).
        //
        // TrajectoryScore keeps its v8 meaning DELIBERATELY, and it is the counterpart to flooring the channel
        // direction factor at 0: deterioration is reported HERE (Trajectory below its neutral point), not as a
        // negative Opportunity share.
        var trajectoryScore = 0;
        var attentionScore = 0;
        var evidenceConfidenceScore = 0;
        var signalVelocityScore = 0;

        if (signals.Count > 0)
        {
            trajectoryScore = ScoreSignalMath.TrajectoryScore(signals, recency, _weights);

            var reach = ScoreSignalMath.AttentionReach(
                signals, input.PreCollapseSignals, _weights, _sourceWeights);
            attentionScore = ScoreSignalMath.AttentionComponent(reach, _weights);

            // Neutral evidence counts here in FULL — coverage and confidence are exactly what this slice
            // preserves while removing the directional contribution.
            evidenceConfidenceScore = ScoreSignalMath.EvidenceConfidenceScore(signals, _weights);

            signalVelocityScore = ScoreSignalMath.SignalVelocityScore(
                signals, input.PreviousSignals, _weights);
        }

        // ---- The notedness discount (spec 149), applied exactly as radar-formula-v9 applies it ----
        // ONCE, to the COMPOSED channel score, never per channel: notedness is a property of the COMPANY, not
        // of a source, so discounting each channel separately would apply it once per declared channel and
        // confuse a source's reach with the company's fame. Same shared expression, same knobs, fed the same
        // clamped-int attentionScore. Setting OpportunityAttentionDiscountWeight and
        // FollowingTierDiscountWeight to 0 makes it exactly 1.0 (the IEEE-754 identity), which is how a
        // strategy opts out bit-for-bit.
        var notednessDiscount = ScoreSignalMath.NotednessDiscount(
            _weights, attentionScore, input.FollowingTier);

        // The composite IS this strategy's answer, so it lands in OpportunityScore — see the range note in the
        // class remarks. The discount is in (0,1], so the product stays in range.
        var opportunityScore = ScoreSignalMath.Clamp0To100(100.0 * composite * notednessDiscount);

        var components = new ScoreComponents(
            TrajectoryScore: trajectoryScore,
            OpportunityScore: opportunityScore,
            AttentionScore: attentionScore,
            EvidenceConfidenceScore: evidenceConfidenceScore,
            SignalVelocityScore: signalVelocityScore);

        // ---- Contributions (provenance — current window only) ----
        // Shared with v9: exactly one contribution per current-window signal, in input order, INCLUDING the
        // Neutral ones (which weigh 0 and still name their channel) and including signals no channel consumed.
        var contributions = ScoringChannelComposition.BuildContributions(signals, recency, composition);

        var windowDays = (int)Math.Round(windowLength.TotalDays, MidpointRounding.AwayFromZero);
        var channelSummary = ScoringChannelComposition.DescribeChannels(composition.Channels);

        // The discount is named ONLY when it actually moved the number (spec 149's rule, kept): an inert
        // discount is exactly 1.0, so omitting it then is the true statement that Opportunity IS composite·100
        // rather than a hidden term.
        var notednessSummary = notednessDiscount == 1.0
            ? string.Empty
            : $"; × notedness {notednessDiscount:0.000}";
        var explanation =
            $"{ScoreFormulaVersions.V10}: {signals.Count} signal(s) over {windowDays}d across "
                + $"{breakdown.Count} channel(s) → Opportunity {opportunityScore} (composite {composite:0.000} = "
                + $"{channelSummary}{notednessSummary}); Trajectory {trajectoryScore}, Attention {attentionScore}, "
                + $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}.";

        // ComponentJson keeps ScoreComponents' five properties FIRST and by the same names, so an existing
        // reader that deserializes it as ScoreComponents is unaffected (extra properties are ignored). Beyond
        // v9's shape it records the composition Revision — so a snapshot says which composition produced it
        // without having to be re-derived from the fingerprint — and, per channel, the directional read that
        // makes "no directional mass" distinguishable from "balanced" at the same score.
        var componentJson = JsonSerializer.Serialize(new V10ComponentJson(
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
    /// Projects the shared <see cref="ChannelComputation"/> onto THIS formula's persisted channel shape. The
    /// first thirteen properties are <c>radar-formula-v9</c>'s exactly (same names, same order), so a reader
    /// written against a v9 breakdown still works; the three directional properties are appended.
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
    /// One channel's audited share, serialized into <c>ComponentJson</c>: what it measured, what it was worth,
    /// how much it actually earned, whether it had anything to measure at all (<paramref name="Dark"/>),
    /// which of its declared collectors ran, how its collector attribution was obtained, and — new in
    /// <c>radar-formula-v10</c> — the DIRECTIONAL READ behind its score.
    /// </summary>
    /// <param name="SignalCount">
    /// How many current-window signals this channel consumed — for a collector channel, those whose evidence
    /// its collectors retrieved; for a breadth channel, those that actually fed the reach term. <b>Neutral
    /// signals are counted here in full</b>, which is how an all-Neutral channel remains visibly covered
    /// despite scoring 0.
    /// </param>
    /// <param name="Dark">
    /// True when the channel had NOTHING to measure — a collector channel that consumed no signals, or a
    /// breadth channel with zero reach. <b>Load-bearing in v10, more than it was in v9:</b> an all-Neutral
    /// channel now also scores 0, so this flag plus <paramref name="SignalCount"/> is the only thing that
    /// separates "we looked and found activity that says nothing about direction" from "we looked and found
    /// nothing".
    /// </param>
    /// <param name="RecordedSignals">
    /// Consumed signals whose producing collector RECORDED its own name (spec 146). First-hand provenance.
    /// </param>
    /// <param name="InferredSignals">
    /// Consumed signals whose collector Radar re-derived afterwards (spec 151); 0 unless
    /// <c>Radar:Scoring:InferLegacyCollectorAttribution</c> is enabled. A channel whose mass is mostly
    /// inferred is measuring a reconstruction, and any artifact ranking it must say so.
    /// </param>
    /// <param name="UnattributedSignals">
    /// Consumed signals with no establishable collector. Structurally 0 for a COLLECTOR channel
    /// (<c>ScoringChannel.Consumes</c> is false for a null name); meaningful only for the breadth channel.
    /// </param>
    /// <param name="Preponderance">
    /// The corroboration-smoothed <c>(Mpos−Mneg)/(Mpos+Mneg+k)</c> ratio this channel's score was built from,
    /// or <c>null</c> for a BREADTH channel, which never consults direction at all. Provenance: it is the
    /// input to <see cref="DirectionFactor"/>, not a second score.
    /// </param>
    /// <param name="DirectionalMass">
    /// <c>Mpos + Mneg</c>, or <c>null</c> for a breadth channel. Zero here with signals present is exactly the
    /// all-Neutral case; non-zero with a preponderance of 0 is the balanced case.
    /// </param>
    /// <param name="DirectionState">
    /// One of the <see cref="ChannelDirectionState"/> tokens (<c>none</c> / <c>balanced</c> / <c>positive</c> /
    /// <c>negative</c>), or <c>null</c> for a breadth channel. <b>Provenance only — it never feeds a
    /// score</b>; it exists so that the two states v10 deliberately maps onto the same 0 stay distinguishable
    /// after the fact.
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
    /// The <c>ComponentJson</c> shape. The first five properties are <see cref="ScoreComponents"/>' exactly,
    /// by name and order, so the enrichment is backward-compatible with any reader that deserializes it as
    /// <see cref="ScoreComponents"/>.
    /// </summary>
    /// <param name="Revision">
    /// <see cref="CompositionRevision"/> — recorded so a stored snapshot states which COMPOSITION produced it
    /// directly, rather than requiring a reader to dereference the fingerprint.
    /// </param>
    /// <param name="Composite">
    /// The unrounded weighted channel sum, BEFORE <paramref name="Discount"/> is applied —
    /// <c>OpportunityScore ≈ round(100 · Composite · Discount)</c>.
    /// </param>
    /// <param name="Discount">
    /// The spec-149 notedness discount actually applied. <c>1.0</c> means it was inert.
    /// </param>
    private sealed record V10ComponentJson(
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
