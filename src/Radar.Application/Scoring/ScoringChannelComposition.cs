using Radar.Application.Collectors;

namespace Radar.Application.Scoring;

/// <summary>
/// How ONE collector channel measures its raw ACTIVITY over the signals it consumed — the <c>x</c> that
/// <see cref="ScoringChannelComposition"/> then saturates as <c>x/(x+S_c)</c>. Its shape deliberately mirrors
/// <see cref="ScoreSignalMath.ActivityMass"/>, which <c>radar-formula-v9</c> and <c>radar-formula-v10</c> both
/// pass here as a method group.
/// <para>
/// <b>It exists so a CONTROL formula can measure something deliberately dumber than the composite does</b>
/// (spec 154). <c>radar-baseline-activity-v1</c> passes a plain <b>signal COUNT</b> — no strength, no
/// confidence, no recency, no evidence quality — because a baseline that quietly weighted its inputs the same
/// way the composite does would not be a baseline. Everything else about the channel pass (selection,
/// attribution, provenance, saturation, the composite sum, the contribution chain) stays shared.
/// </para>
/// <para>
/// The parameters are the channel's SUB-SLICES, in the window's input order: only the SET changes, never the
/// alignment, so an implementation may index all three interchangeably.
/// </para>
/// </summary>
/// <param name="signals">The current-window signals this channel consumed, in input order.</param>
/// <param name="recency">Their recency factors, aligned with <paramref name="signals"/>.</param>
/// <param name="qualityFactors">Their evidence-quality factors, aligned with <paramref name="signals"/>.</param>
/// <returns>The channel's raw, non-negative activity magnitude.</returns>
public delegate double ChannelActivityMass(
    IReadOnlyList<ScoringSignal> signals,
    IReadOnlyList<double> recency,
    IReadOnlyList<double> qualityFactors);

/// <summary>
/// How a collector channel turns its two measured quantities — the saturated ACTIVITY on that channel and the
/// corroboration-smoothed directional PREPONDERANCE of its signals — into that channel's share of the
/// composite.
/// <list type="bullet">
/// <item><c>radar-formula-v9</c>: <c>saturation · (0.5 + 0.5·preponderance)</c> — a channel with no
/// directional mass keeps HALF its saturated share, so activity alone produces score.</item>
/// <item><c>radar-formula-v10</c>: <c>saturation · max(0, preponderance)</c> — no directional mass ⇒ exactly
/// 0 (spec 153).</item>
/// <item><c>radar-baseline-activity-v1</c>: <c>saturation</c>, verbatim — direction is not consulted at all
/// (spec 154).</item>
/// </list>
/// <para>
/// <b>Corrected by spec 154:</b> this delegate used to be documented as "the ONLY behavioural difference
/// between <c>radar-formula-v9</c> and <c>radar-formula-v10</c>", and of those two it still is. It is no
/// longer the only axis the shared pass is parameterised on: <see cref="ChannelActivityMass"/> is the second,
/// and the statement is corrected here rather than left to rot.
/// </para>
/// <para>
/// It returns the whole channel score rather than a multiplier, deliberately: a formula's arithmetic shape is
/// part of its identity (IEEE-754 is not associative — see <see cref="ScoreSignalMath"/>), so each formula
/// writes its own product exactly as it means it, rather than having this type impose a multiplication order.
/// </para>
/// </summary>
/// <param name="saturation">The channel's saturated activity, <c>activity/(activity+S_c) ∈ [0,1)</c>.</param>
/// <param name="preponderance">
/// The corroboration-smoothed <c>(Mpos−Mneg)/(Mpos+Mneg+k)</c> ratio over the channel's signals, in
/// <c>(-1,1)</c>, and exactly <c>0</c> when the channel carries no directional mass at all.
/// </param>
/// <returns>The channel's score, which every caller must keep within <c>[0,1]</c>.</returns>
public delegate double CollectorChannelScore(double saturation, double preponderance);

/// <summary>
/// How a BREADTH channel measures its raw REACH over the strategy's gated window — the <c>x</c> that
/// <see cref="ScoringChannelComposition"/> then saturates as <c>x/(x+S_c)</c> (spec 158 §4).
/// <list type="bullet">
/// <item><c>radar-formula-v9</c> / <c>radar-formula-v10</c> / <c>radar-baseline-activity-v1</c> pass the
/// existing <see cref="ScoreSignalMath.AttentionReach"/> explicitly — the same static method the shared pass
/// used to call directly, so their arithmetic (and therefore their last bit) is unchanged.</item>
/// <item>The prospective <c>radar-formula-v11</c> (spec 157 §3) passes
/// <see cref="ScoreSignalMath.PositiveAttentionReach"/>, the positive-only narrowing of the same term.</item>
/// </list>
/// <para>
/// <b>REQUIRED, with no default value, deliberately</b> — the same reasoning as
/// <see cref="ChannelActivityMass"/>: a silent default would let a new formula quietly inherit the full-set
/// reach while claiming to measure positive-only breadth (or vice versa).
/// </para>
/// </summary>
/// <param name="signals">The current-window (post-collapse) signals, in input order.</param>
/// <param name="preCollapseSignals">The same window BEFORE the spec-109 media collapse (breadth-only input).</param>
/// <param name="weights">The strategy's resolved magnitudes.</param>
/// <param name="sourceWeights">The shared publisher tier map.</param>
/// <returns>The channel's raw, non-negative reach magnitude.</returns>
public delegate double BreadthChannelReach(
    IReadOnlyList<ScoringSignal> signals,
    IReadOnlyList<ScoringSignal> preCollapseSignals,
    ScoringWeights weights,
    IAttentionSourceWeights sourceWeights);

/// <summary>
/// The four states a collector channel's directional mass can be in — <b>provenance only, never a score
/// input</b> (spec 153). They exist because <c>radar-formula-v10</c> maps two genuinely different situations
/// onto the same 0: a channel with NO directional mass at all and a channel whose positive and negative mass
/// cancel. Both mean "no net evidence that this trajectory is improving", so both contribute zero directional
/// opportunity — they differ in the EVIDENCE TRAIL, not in the score, and this token is what carries that
/// difference into the persisted breakdown.
/// </summary>
public static class ChannelDirectionState
{
    /// <summary>No directional mass at all — every consumed signal is Neutral/Mixed.</summary>
    public const string None = "none";

    /// <summary>Directional mass IS present and nets to exactly zero: positive and negative cancel.</summary>
    public const string Balanced = "balanced";

    /// <summary>Net positive directional mass.</summary>
    public const string Positive = "positive";

    /// <summary>Net negative directional mass.</summary>
    public const string Negative = "negative";
}

/// <summary>
/// The directional read of ONE collector channel — recorded provenance, never a score input beyond the
/// <see cref="Preponderance"/> the formula's own <see cref="CollectorChannelScore"/> already consumed.
/// </summary>
/// <param name="Preponderance">
/// The corroboration-smoothed <c>(Mpos−Mneg)/(Mpos+Mneg+k)</c> ratio, in <c>(-1,1)</c>.
/// </param>
/// <param name="DirectionalMass">
/// <c>Mpos + Mneg</c> — the total directional mass. Zero means no directional signal was consumed at all,
/// which is what separates <see cref="ChannelDirectionState.None"/> from
/// <see cref="ChannelDirectionState.Balanced"/>.
/// </param>
/// <param name="State">One of the <see cref="ChannelDirectionState"/> tokens.</param>
public sealed record ChannelDirection(double Preponderance, double DirectionalMass, string State);

/// <summary>
/// ONE channel's computed share, formula-agnostic. Each formula projects this into its OWN
/// <c>ComponentJson</c> record shape — v9's must stay byte-identical while v10's adds the directional fields —
/// so this type is the shared COMPUTATION result, not a serialization contract.
/// </summary>
/// <param name="Channel">The declared channel (name, kind, weight, saturation constant, collectors).</param>
/// <param name="Score">This channel's sub-score, in <c>[0,1]</c>.</param>
/// <param name="WeightedContribution"><c>Channel.Weight · Score</c> — its actual share of the composite.</param>
/// <param name="SignalCount">How many current-window signals this channel consumed.</param>
/// <param name="Dark">
/// True when the channel had NOTHING TO MEASURE — a collector channel that consumed no signals, or a breadth
/// channel with zero reach. Deliberately distinct from <c>Score == 0</c>, and that distinction is sharper
/// under <c>radar-formula-v10</c> than it was under v9: an all-Neutral channel now also scores 0, so
/// <paramref name="Dark"/> plus <paramref name="SignalCount"/> is the ONLY way to tell "we looked and found
/// activity that says nothing about direction" from "we looked and found nothing".
/// </param>
/// <param name="CollectorsRan">Declared collectors present in this run's enabled vocabulary.</param>
/// <param name="CollectorsNotRun">
/// Declared collectors absent from it. Structurally EMPTY in any composed run (spec 147 §4) — the startup
/// guard validates channel collectors against the very same vocabulary.
/// </param>
/// <param name="RecordedSignals">Consumed signals whose collector was RECORDED at collection time (spec 146).</param>
/// <param name="InferredSignals">Consumed signals whose collector Radar re-derived afterwards (spec 151).</param>
/// <param name="UnattributedSignals">
/// Consumed signals with no establishable collector. Structurally 0 for a COLLECTOR channel
/// (<c>ScoringChannel.Consumes</c> is false for a null name); informative only for the breadth channel.
/// </param>
/// <param name="Direction">
/// The channel's directional read, or <c>null</c> for a BREADTH channel — which never consults direction at
/// all (its score is reach saturation), so reporting a measured "no directional mass" for it would claim a
/// reading that was never taken.
/// </param>
public sealed record ChannelComputation(
    ScoringChannel Channel,
    double Score,
    double WeightedContribution,
    int SignalCount,
    bool Dark,
    IReadOnlyList<string> CollectorsRan,
    IReadOnlyList<string> CollectorsNotRun,
    int RecordedSignals,
    int InferredSignals,
    int UnattributedSignals,
    ChannelDirection? Direction);

/// <summary>The whole channel pass over one company's window.</summary>
/// <param name="Channels">One <see cref="ChannelComputation"/> per DECLARED channel, in canonical order.</param>
/// <param name="Composite">
/// <c>Σ (weight_c · channelScore_c)</c> over the DECLARED channels, clamped to <c>[0,1]</c>. Never
/// renormalised by the surviving weights — see <see cref="ScoringChannelSet"/>.
/// </param>
/// <param name="Attribution">Each current-window signal's resolved collector attribution, in input order.</param>
/// <param name="ChannelsPerSignal">
/// Each current-window signal's consuming channel names (null when no channel consumed it), in input order —
/// what makes evidence → signal → channel → score traceable in the contribution reasons.
/// </param>
public sealed record ChannelComposition(
    IReadOnlyList<ChannelComputation> Channels,
    double Composite,
    IReadOnlyList<CollectorAttribution> Attribution,
    IReadOnlyList<IReadOnlyList<string>?> ChannelsPerSignal);

/// <summary>
/// THE ONE channel-composition pass shared by every channel formula (<c>radar-formula-v9</c>,
/// <c>radar-formula-v10</c> since spec 153, and the <c>radar-baseline-activity-v1</c> control since spec 154):
/// channel selection, collector-attribution resolution and tally, the ran/not-run split, the activity →
/// saturation → preponderance computation, the per-signal channel attribution the contribution reasons carry,
/// and the weighted composite sum.
/// <para>
/// EXTRACTED FROM <see cref="RadarScoreFormulaV9"/>, NOT COPIED (CLAUDE.md reuse-over-copy). Each channel
/// formula differs from the others in a HANDFUL OF EXPRESSIONS — the collector
/// <see cref="CollectorChannelScore"/> and, since spec 154, the <see cref="ChannelActivityMass"/> — so pasting
/// ~150 lines of selection/attribution/provenance logic to change one multiplication would guarantee they
/// drift on the next attribution or provenance fix, and would make it impossible to argue that v9 and v10 are
/// byte-identical afterwards. What is deliberately NOT shared: the <c>ComponentJson</c> record shapes (v9's
/// must stay byte-identical at 13 channel properties while v10's adds the directional read) and the
/// explanation prefix (each formula names its own version).
/// </para>
/// <para>
/// <b>FLOATING-POINT EXACTNESS IS PART OF THE CONTRACT</b>, exactly as it is for
/// <see cref="ScoreSignalMath"/>: every expression here is v9's verbatim, in v9's order.
/// <c>RadarScoreFormulaV9OutputStabilityTests</c> pins the whole of v9's output — captured from the pre-spec-153
/// sources — so a re-association that moves an ULP fails there rather than silently rescoring a live series.
/// </para>
/// <para>Pure and deterministic: no clock, no randomness, no I/O (AD-3).</para>
/// </summary>
public static class ScoringChannelComposition
{
    /// <summary>
    /// Runs the declared channel budget over one window and returns every channel's share plus the composite.
    /// </summary>
    /// <param name="input">The windowed scoring input (its <c>Signals</c> are the current window).</param>
    /// <param name="recency">Per-signal recency factors, aligned with <c>input.Signals</c>' order.</param>
    /// <param name="quality">Per-signal evidence-quality factors, aligned with the same order.</param>
    /// <param name="channels">The strategy's validated, non-empty channel budget.</param>
    /// <param name="weights">The strategy's resolved magnitudes.</param>
    /// <param name="sourceWeights">The shared publisher tier map (breadth channel input).</param>
    /// <param name="attributionResolver">The single collector-attribution seam (spec 151).</param>
    /// <param name="activityMass">
    /// How the formula measures a collector channel's raw activity (spec 154). <b>REQUIRED, with no default
    /// value, deliberately</b>: a silent default is exactly how spec 152's partial-window mislabelling slipped
    /// through once, and here it would let a new formula quietly inherit the composite's own weighting while
    /// claiming to measure something simpler. v9 and v10 pass
    /// <see cref="ScoreSignalMath.ActivityMass"/> explicitly at their call sites.
    /// </param>
    /// <param name="collectorScore">
    /// The formula's collector direction factor — the ONE thing v9 and v10 disagree about, and the axis on
    /// which the spec-154 control opts out of direction entirely.
    /// </param>
    /// <param name="breadthReach">
    /// How the formula measures a BREADTH channel's raw reach (spec 158 §4). <b>REQUIRED, no default</b> —
    /// see <see cref="BreadthChannelReach"/>. v9, v10 and the baseline control pass
    /// <see cref="ScoreSignalMath.AttentionReach"/> explicitly at their call sites, which is byte-identical
    /// to the direct call this pass previously made.
    /// </param>
    public static ChannelComposition Compose(
        ScoringInput input,
        IReadOnlyList<double> recency,
        IReadOnlyList<double> quality,
        ScoringChannelSet channels,
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights,
        ICollectorAttributionResolver attributionResolver,
        ChannelActivityMass activityMass,
        CollectorChannelScore collectorScore,
        BreadthChannelReach breadthReach)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(recency);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        ArgumentNullException.ThrowIfNull(attributionResolver);
        ArgumentNullException.ThrowIfNull(activityMass);
        ArgumentNullException.ThrowIfNull(collectorScore);
        ArgumentNullException.ThrowIfNull(breadthReach);

        var signals = input.Signals;

        // The collector behind each signal's evidence, WITH how that answer was obtained (spec 146 recorded
        // it; spec 151 added the seam and the opt-in legacy inference). Unattributed evidence is consumed by
        // NO collector channel and contributes 0 — which is exactly what the standing "never backfill accrued
        // history" rule implies, and remains the default answer for every pre-spec-146 record unless an
        // operator explicitly turns the inference on.
        var attributionOf = new CollectorAttribution[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            attributionOf[i] = attributionResolver.Resolve(signals[i].Evidence);
        }

        var enabled = new HashSet<string>(input.EnabledCollectors, StringComparer.Ordinal);

        var results = new List<ChannelComputation>(channels.Channels.Count);

        // Per-signal channel attribution, for the contribution reasons (provenance: signal → channel). Filled
        // as each channel selects, so a signal consumed by more than one channel names all of them.
        var channelsPerSignal = new List<string>?[signals.Count];

        foreach (var channel in channels.Channels)
        {
            double channelScore;
            // "Nothing to measure" — the state the no-renormalisation rule is about. It is NOT the same as
            // "scored 0": a collector channel whose signals are uniformly negative (or, under
            // radar-formula-v10, uniformly Neutral) also scores 0, and that is a measurement, not an absence.
            // So it is recorded per kind at the source rather than inferred from the score.
            bool dark;
            ChannelDirection? direction;

            // The current-window signals this channel consumed, in input order. Filled by BOTH branches so
            // the attribution tally below has exactly one definition of "this channel's signals" — the same
            // set SignalCount reports and the same set each branch attributes in the contribution reasons.
            var consumedIndices = new List<int>();

            if (channel.Kind == ScoringChannelKind.Breadth)
            {
                // Breadth is cross-source by construction: it reads the whole gated set regardless of which
                // collector retrieved what, and it is POSITIVE — more genuine (tier-weighted,
                // distinct-publisher) reach earns more of its share. HOW reach is measured is the formula's
                // own choice (spec 158 §4): v9/v10/baseline pass ScoreSignalMath.AttentionReach — the exact
                // call this pass previously made inline — and the prospective v11 passes the positive-only
                // PositiveAttentionReach.
                var reach = breadthReach(
                    signals, input.PreCollapseSignals, weights, sourceWeights);
                channelScore = ScoreSignalMath.Saturate(reach, channel.Saturation);
                dark = reach <= 0;
                // A breadth channel never consults direction, so it reports no directional read at all rather
                // than a measured "none" it did not take.
                direction = null;

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

                // ACTIVITY counts Neutral/Mixed signals too — something DID happen on that channel — while
                // the directional masses below do not. That split is what lets neutral coverage AMPLIFY a
                // genuine directional read (more activity ⇒ higher saturation) without ever creating one.
                // HOW activity is measured is the formula's own choice (spec 154): v9/v10 pass
                // ScoreSignalMath.ActivityMass, the baseline control passes a plain signal count.
                var activity = activityMass(subSignals, subRecency, subQuality);
                var saturation = ScoreSignalMath.Saturate(activity, channel.Saturation);

                var mass = ScoreSignalMath.DirectionalMasses(subSignals, subRecency, subQuality);
                var preponderance = ScoreSignalMath.Preponderance(
                    mass, weights.TrajectoryCorroborationK, band: 1.0);

                channelScore = collectorScore(saturation, preponderance);
                direction = new ChannelDirection(
                    preponderance, mass.Total, DirectionStateOf(mass, preponderance));
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

            results.Add(new ChannelComputation(
                Channel: channel,
                Score: channelScore,
                WeightedContribution: channel.Weight * channelScore,
                SignalCount: consumedIndices.Count,
                Dark: dark,
                CollectorsRan: ran,
                CollectorsNotRun: notRun,
                RecordedSignals: recordedSignals,
                InferredSignals: inferredSignals,
                UnattributedSignals: unattributedSignals,
                Direction: direction));
        }

        // THE COMPOSITE. Summed over the DECLARED channels — never over "the channels that fired" — so a dark
        // channel costs the strategy its whole share. DO NOT renormalise by the surviving weights: that is the
        // obvious-looking fix, and it would erase exactly the penalty this formula exists to create. The clamp
        // is a defensive range guarantee only: the weights are validated to sum to 1 and every channel score is
        // in [0,1], so the sum is already in [0,1].
        var composite = 0.0;
        foreach (var channel in results)
        {
            composite += channel.WeightedContribution;
        }

        composite = Math.Clamp(composite, 0.0, 1.0);

        return new ChannelComposition(results, composite, attributionOf, channelsPerSignal);
    }

    /// <summary>
    /// The per-channel summary spliced into a channel formula's explanation:
    /// <c>name score×weight</c> per channel, with the dark ones flagged.
    /// </summary>
    public static string DescribeChannels(IReadOnlyList<ChannelComputation> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        return string.Join(
            ", ",
            channels.Select(c =>
                $"{c.Channel.Name} {c.Score:0.000}×{c.Channel.Weight:0.00}{(c.Dark ? " (dark)" : string.Empty)}"));
    }

    /// <summary>
    /// The per-signal provenance chain every channel formula emits: exactly one
    /// <see cref="ScoreContribution"/> per CURRENT-window signal, in input order, including signals no channel
    /// consumed (which weigh into no channel and are named as such), and never from
    /// <see cref="ScoringInput.PreviousSignals"/>.
    /// <para>
    /// The per-signal WEIGHT keeps <c>radar-formula-v8</c>'s shape — the channel weight and saturation are
    /// AGGREGATE transforms over a channel's signals, exactly as v8's consensus shaping and following discount
    /// are aggregate transforms over its signals — and the channel attribution is carried in the reason, which
    /// is what makes evidence → signal → channel → score traceable. A NEUTRAL signal therefore weighs 0 here
    /// (<see cref="ScoreSignalMath.DirectionSign"/>) and is still emitted, named, and linked to its evidence:
    /// spec 153 removes a directional CONTRIBUTION, never the evidence trail.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ScoreContribution> BuildContributions(
        IReadOnlyList<ScoringSignal> signals,
        IReadOnlyList<double> recency,
        ChannelComposition composition)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(recency);
        ArgumentNullException.ThrowIfNull(composition);

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
                        + $"confidence {signal.Confidence:0.00} — "
                        + DescribeAttribution(composition.ChannelsPerSignal[i], composition.Attribution[i]),
                ContributionWeight: weight));
        }

        return contributions;
    }

    /// <summary>
    /// The ONE mapping from a channel's directional read onto its <see cref="ChannelDirectionState"/> token.
    /// Public (spec 158) so the input-only channel-feasibility audit classifies a channel's preponderance
    /// sign with exactly this rule rather than a second copy; provenance only, never a score input.
    /// </summary>
    public static string DirectionStateOf(DirectionalMass mass, double preponderance)
    {
        // Preponderance is exactly 0 both when there is NO directional mass and when the two masses cancel,
        // which is precisely the pair spec 153 decided to score identically and record differently.
        if (mass.Total <= 0)
        {
            return ChannelDirectionState.None;
        }

        return preponderance > 0
            ? ChannelDirectionState.Positive
            : preponderance < 0
                ? ChannelDirectionState.Negative
                : ChannelDirectionState.Balanced;
    }

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
}
