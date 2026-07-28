using System.Text.Json;

using Radar.Application.Collectors;

namespace Radar.Application.Scoring;

/// <summary>
/// <c>radar-baseline-activity-v1</c> (spec 154) — the <b>CONTROL</b>, not a candidate strategy. A collector
/// channel's score is the saturated plain <b>COUNT</b> of the current-window signals it consumed:
/// <c>channelScore = count / (count + S_c)</c>. <b>No direction, no notedness, no quality weighting, no
/// recency, no strength, no confidence.</b> It answers exactly one question — <i>is Radar's score just
/// "something happened"?</i>
///
/// <para><b>WHY IT EXISTS.</b> Nothing Radar produced answered whether the composite adds anything over an
/// embarrassingly simple heuristic. The 2026-07-28 replay backtest ranked five deliberately-different
/// strategies and they landed within <b>0.016</b> of each other in Spearman ρ — the signature of one common
/// factor dominating all five, not of a ranking. When everything correlates with everything, the useful
/// comparison is not strategy-vs-strategy; it is <b>strategy vs. baseline</b>. This class is the cheapest
/// honest way to run that comparison through the very same seam, stores, fingerprints and leaderboard every
/// other strategy uses — see <b>AD-15</b> for the acceptance rule it enables.</para>
///
/// <para><b>WHY IT IS NOT <c>radar-formula-v11</c>.</b> The <c>radar-formula-vN</c> sequence is the lineage of
/// Radar's COMPOSITE, each version a considered evolution of the previous one (AD-6). This is not an evolution
/// of anything — it is the thing the composite must beat. Spec 154 §3 requires that a baseline's name says
/// what it is <i>wherever it appears</i>, and the formula token appears in the leaderboard's inputs, in the
/// persisted <c>EffectiveScoringConfig</c>, and in every snapshot's <c>ComponentJson</c>. Numbering it into the
/// composite lineage would make it read as the newest and best formula, which is the opposite of the truth.
/// It is still a first-class shippable formula: it is in <see cref="ScoreFormulaVersions.All"/>, it is
/// dispatched by <see cref="RadarScoreFormulaFactory"/>, and <see cref="ScoreFormulaVersions.ConsumesChannels"/>
/// answers for it exactly as it does for v9/v10.</para>
///
/// <para><b>REUSE, NOT COPY.</b> Everything except the two expressions below comes from the shared
/// <see cref="ScoringChannelComposition"/> pass and <see cref="ScoreSignalMath"/> — channel selection,
/// collector-attribution resolution and tally, the ran/not-run split, saturation, the per-signal channel
/// attribution, the composite sum, the never-renormalise rule, the contribution chain, and the four
/// v8-meaning components. This formula contributes exactly: <see cref="SignalCount"/> (its
/// <see cref="ChannelActivityMass"/>) and <see cref="Saturation"/> (its
/// <see cref="CollectorChannelScore"/>, which passes the saturation through and ignores the preponderance
/// entirely).</para>
///
/// <para><b>A BREADTH CHANNEL IS REJECTED AT CONSTRUCTION — decided, not overlooked.</b> The shared pass can
/// compute one (it is the tier-weighted distinct-publisher <see cref="ScoreSignalMath.AttentionReach"/>), and
/// admitting it would have been the smaller diff. It is refused because reach is <b>tier-weighted</b>: outlets
/// count as their source-quality tier (mills ≈0.1, unknown 0.5, genuine 1.0) rather than as 1. That is a
/// quality weighting, and a control whose headline claim is "no quality weighting" must not quietly contain
/// one. A baseline that measures something other than what it says it measures is worse than no baseline,
/// because it looks like a control while testing something else (spec 154 §1). A strategy that wants to test
/// "is Radar just tracking press coverage?" declares a COLLECTOR channel over the media collectors instead —
/// which is exactly what <c>baseline-media-only</c> does.</para>
///
/// <para><b>NO NOTEDNESS DISCOUNT.</b> <c>OpportunityScore = clamp(100 · composite)</c>, full stop. v9 and v10
/// damp the composed score by <see cref="ScoreSignalMath.NotednessDiscount"/>; this one deliberately does not,
/// because "how much evidence arrived" is the whole hypothesis under test and discounting it by company fame
/// would make the control a (weaker) copy of the composite. The consequence must be stated rather than
/// discovered: <b>this baseline is expected to rank large, widely-covered companies highly</b>. If it also
/// tracks price as well as the composite does, that is the finding — about Radar, not a recommendation.</para>
///
/// <para><b>THE OTHER FOUR COMPONENTS KEEP THEIR v8 MEANINGS</b>, computed over the strategy's gated set
/// exactly as v9 and v10 compute them, so <c>WeeklyReportActionPolicyV1</c>'s Trajectory /
/// EvidenceConfidence thresholds stay valid and a baseline snapshot stays legible beside a composite one.
/// Only <see cref="ScoreComponents.OpportunityScore"/> carries this formula's answer.</para>
///
/// <para><b>SCORES ARE NOT COMPARABLE IN ABSOLUTE TERMS</b> with any <c>radar-formula-vN</c> series — the
/// scales are unrelated. Only RANKINGS are, which is precisely what spec 140's leaderboard consumes.</para>
///
/// <para>Pure and deterministic (no clock, no randomness, no I/O). Emits exactly one provenance-carrying
/// contribution per current-window signal, in input order, each naming the channel(s) that consumed it, and
/// never from <see cref="ScoringInput.PreviousSignals"/>.</para>
/// </summary>
public sealed class RadarBaselineActivityFormulaV1 : IScoreFormula
{
    /// <summary>
    /// THE COMPOSITION REVISION OF THE TWO EXPRESSIONS DIRECTLY BELOW — the same obligation
    /// <see cref="RadarScoreFormulaV10"/> carries.
    /// <para>
    /// <b>If you change how this formula COMPOSES its score</b> — what counts as activity, whether direction or
    /// notedness enters, what the composite is multiplied by, which components are computed over which set —
    /// you MUST bump this token in the same change. Bumping it moves every baseline strategy's
    /// <c>ScoringConfigVersion</c> (it is folded in via <see cref="FormulaIdentity.Of"/>), which trips
    /// <c>StrategyIdentityGuard</c> on the next run and forces a conscious decision about the discontinuity.
    /// That matters even more for a control than for a composite: a baseline whose definition drifted silently
    /// would invalidate every "beats the baseline" claim made against it, retroactively and invisibly.
    /// </para>
    /// <para>
    /// It is NOT a substitute for AD-6. <c>RadarBaselineActivityFormulaV1CompositionGuardTests</c> pins this
    /// token together with the formula's full output and the fingerprint a default-weights baseline strategy
    /// stamps, so the three can only move together.
    /// </para>
    /// </summary>
    private const string Revision = "rev1";

    private readonly ScoringWeights _weights;
    private readonly IAttentionSourceWeights _sourceWeights;
    private readonly ScoringChannelSet _channels;
    private readonly ICollectorAttributionResolver _attribution;

    /// <summary>
    /// <b>THE ACTIVITY MEASURE:</b> the plain COUNT of the signals a channel consumed. The recency and quality
    /// factors are supplied and deliberately ignored — a strength-10, PrimarySource, same-day signal and a
    /// strength-1, Unknown-quality, month-old one each count exactly 1. That is the whole hypothesis: "did
    /// something arrive on this channel", nothing more.
    /// <para><b>Changing this expression obliges a <see cref="Revision"/> bump</b> (see the note there).</para>
    /// </summary>
    private static readonly ChannelActivityMass SignalCount = (signals, _, _) => signals.Count;

    /// <summary>
    /// <b>THE CHANNEL SCORE:</b> the saturation, verbatim — <c>count/(count + S_c) ∈ [0,1)</c>. The
    /// preponderance is supplied by the shared pass and deliberately ignored, so an all-Negative channel and an
    /// all-Positive channel of the same size score identically. That is what "no direction" means here, and it
    /// is the exact axis <c>radar-formula-v10</c> exists to get RIGHT — which is why this control is worth
    /// running beside it.
    /// <para><b>Changing this expression obliges a <see cref="Revision"/> bump</b> (see the note there).</para>
    /// </summary>
    private static readonly CollectorChannelScore Saturation = (saturation, _) => saturation;

    /// <summary>
    /// Constructs the control with the strategy's magnitudes (which reach only the four v8-meaning components
    /// — never the composite), the shared publisher tier map, and the strategy's validated channel array. The
    /// signature is deliberately identical to <see cref="RadarScoreFormulaV9"/>'s and
    /// <see cref="RadarScoreFormulaV10"/>'s so <see cref="RadarScoreFormulaFactory"/> builds all three from the
    /// same definition and nothing downstream has to know which it got.
    /// </summary>
    /// <param name="attributionResolver">
    /// How the collector behind each signal's evidence is established (spec 151). Optional and defaulting to
    /// <see cref="RecordedOnlyCollectorAttributionResolver"/> — recorded attribution only, i.e. no inference.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The channel set is empty (a channel formula with no channels could only ever score 0), or it declares a
    /// BREADTH channel (see the class remarks — refused because reach is tier-weighted).
    /// </exception>
    public RadarBaselineActivityFormulaV1(
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
                $"{ScoreFormulaVersions.BaselineActivityV1} requires at least one channel; a channel-composition "
                    + "formula with no channels would score every company 0. Declare Channels on the strategy.");
        }

        var breadth = channels.Channels
            .Where(c => c.Kind == ScoringChannelKind.Breadth)
            .Select(c => c.Name)
            .ToArray();
        if (breadth.Length > 0)
        {
            throw new InvalidOperationException(
                $"{ScoreFormulaVersions.BaselineActivityV1} rejects breadth channel(s) "
                    + $"{string.Join(", ", breadth)}: a breadth channel scores TIER-WEIGHTED publisher reach, "
                    + "which is a quality weighting — and this formula's entire claim is that it applies none, "
                    + "so admitting one would make it a control that measures something other than what it says. "
                    + "Declare a COLLECTOR channel over the media collectors instead (that is what a "
                    + $"media-only baseline is), or use {ScoreFormulaVersions.V10} if you want breadth.");
        }

        _weights = weights;
        _sourceWeights = sourceWeights;
        _channels = channels;
        _attribution = attributionResolver ?? RecordedOnlyCollectorAttributionResolver.Instance;
    }

    /// <inheritdoc />
    public string Version => ScoreFormulaVersions.BaselineActivityV1;

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

        // The shared per-signal primitives are still computed: the four v8-meaning components below need
        // recency, and the shared pass takes both arrays. This formula's own two expressions ignore them,
        // which is the point — it is not that the information is unavailable, it is that the control declines
        // to use it.
        var recency = ScoreSignalMath.RecencyFactors(
            signals, input.WindowStartUtc, input.WindowEndUtc, _weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(signals, _weights);

        // ---- The channels ----
        // THE SHARED PASS. This formula contributes exactly two expressions to it (SignalCount, Saturation);
        // everything else — selection, attribution + tally, the ran/not-run split, the weighted composite and
        // the never-renormalise rule — is the same code v9 and v10 run.
        var composition = ScoringChannelComposition.Compose(
            input,
            recency,
            quality,
            _channels,
            _weights,
            _sourceWeights,
            _attribution,
            SignalCount,
            Saturation);

        var breakdown = composition.Channels.Select(ToBreakdown).ToList();
        var composite = composition.Composite;

        // ---- The four v8-meaning components, over the strategy's gated set ----
        // Identical to v9's and v10's, through the same shared primitives, so a baseline snapshot is legible
        // beside a composite one and the report's action thresholds keep meaning what they mean.
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

            evidenceConfidenceScore = ScoreSignalMath.EvidenceConfidenceScore(signals, _weights);

            signalVelocityScore = ScoreSignalMath.SignalVelocityScore(
                signals, input.PreviousSignals, _weights);
        }

        // ---- NO notedness discount ----
        // Deliberate and load-bearing: see the class remarks. AttentionScore is REPORTED (it is a v8-meaning
        // component) and is never consulted by the composite, so this baseline is expected to favour
        // widely-covered companies — which is exactly the bias a composite claiming to find under-noticed ones
        // has to out-perform.
        var opportunityScore = ScoreSignalMath.Clamp0To100(100.0 * composite);

        var components = new ScoreComponents(
            TrajectoryScore: trajectoryScore,
            OpportunityScore: opportunityScore,
            AttentionScore: attentionScore,
            EvidenceConfidenceScore: evidenceConfidenceScore,
            SignalVelocityScore: signalVelocityScore);

        // ---- Contributions (provenance — current window only) ----
        // The shared chain: exactly one contribution per current-window signal, in input order, each linked to
        // its evidence and naming the channel that consumed it. A baseline is held to the same provenance
        // invariant as everything else — a score without evidence is invalid (CLAUDE.md), and that is not
        // relaxed because the score is simple.
        var contributions = ScoringChannelComposition.BuildContributions(signals, recency, composition);

        var windowDays = (int)Math.Round(windowLength.TotalDays, MidpointRounding.AwayFromZero);
        var channelSummary = ScoringChannelComposition.DescribeChannels(composition.Channels);
        var explanation =
            $"{ScoreFormulaVersions.BaselineActivityV1}: {signals.Count} signal(s) over {windowDays}d across "
                + $"{breakdown.Count} channel(s) → Opportunity {opportunityScore} (composite {composite:0.000} = "
                + $"{channelSummary}); Trajectory {trajectoryScore}, Attention {attentionScore}, "
                + $"Confidence {evidenceConfidenceScore}, Velocity {signalVelocityScore}. "
                + "BASELINE CONTROL: signal count only — no direction, no notedness, no quality weighting.";

        // ComponentJson keeps ScoreComponents' five properties FIRST and by the same names, so an existing
        // reader that deserializes it as ScoreComponents is unaffected. There is no Discount property because
        // there is no discount — recording a constant 1.0 would imply a transform that does not exist.
        var componentJson = JsonSerializer.Serialize(new BaselineComponentJson(
            TrajectoryScore: components.TrajectoryScore,
            OpportunityScore: components.OpportunityScore,
            AttentionScore: components.AttentionScore,
            EvidenceConfidenceScore: components.EvidenceConfidenceScore,
            SignalVelocityScore: components.SignalVelocityScore,
            Formula: Version,
            Revision: Revision,
            Composite: composite,
            Channels: breakdown));

        return new ScoreComputation(components, explanation, componentJson, contributions);
    }

    /// <summary>
    /// Projects the shared <see cref="ChannelComputation"/> onto THIS formula's persisted channel shape. The
    /// thirteen properties are <c>radar-formula-v9</c>'s exactly (same names, same order), so an existing
    /// breakdown reader still works. <see cref="ChannelComputation.Direction"/> is deliberately NOT projected:
    /// the shared pass measures it, this formula never consults it, and recording it here would suggest it fed
    /// the score.
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
        UnattributedSignals: computed.UnattributedSignals);

    /// <summary>
    /// One channel's audited share, serialized into <c>ComponentJson</c>.
    /// </summary>
    /// <param name="SignalCount">
    /// How many current-window signals this channel consumed — and, for this formula alone, <b>also the raw
    /// input to its score</b>: <c>Score = SignalCount/(SignalCount + Saturation)</c>, which a reader can
    /// verify by hand.
    /// </param>
    /// <param name="Dark">
    /// True when the channel consumed no signals. Distinct from <c>Score == 0</c> only in principle here —
    /// with a pure count, score 0 and dark coincide — but recorded per kind at the source rather than inferred,
    /// exactly as v9/v10 record it.
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
    /// The <c>ComponentJson</c> shape. The first five properties are <see cref="ScoreComponents"/>' exactly, by
    /// name and order, so the enrichment is backward-compatible with any reader that deserializes it as
    /// <see cref="ScoreComponents"/>.
    /// </summary>
    /// <param name="Composite">
    /// The unrounded weighted channel sum — <c>OpportunityScore = round(100 · Composite)</c>, with nothing
    /// between the two.
    /// </param>
    private sealed record BaselineComponentJson(
        int TrajectoryScore,
        int OpportunityScore,
        int AttentionScore,
        int EvidenceConfidenceScore,
        int SignalVelocityScore,
        string Formula,
        string Revision,
        double Composite,
        IReadOnlyList<ChannelBreakdown> Channels);
}
