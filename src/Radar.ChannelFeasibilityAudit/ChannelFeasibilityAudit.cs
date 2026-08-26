using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.ChannelFeasibilityAudit;

/// <summary>
/// The spec-158 INPUT-ONLY channel-feasibility characterization: at ONE pinned as-of instant, which scoring
/// channels can carry usable variation, measured from scoring INPUTS alone.
/// <para>
/// <b>NO FORWARD OUTCOME IS COMPUTED, READ OR INSPECTED HERE.</b> No price, no attention after the as-of
/// instant, no efficacy statistic. AD-16's pre-commitment binds the OUTCOME; characterizing inputs before an
/// arm is declared is design work and does not consume it (spec 158 §1).
/// </para>
/// <para>
/// <b>ONE PRODUCTION MATH PATH (spec 158 §4).</b> The eligibility funnel replicates
/// <c>ScoringEngine.ScoreCompanyAsync</c>'s input assembly step for step against the SAME production
/// components — the durable <see cref="ISignalRepository"/>/<see cref="IEvidenceRepository"/> (spec 142
/// hydration), the window/known-at/Approved predicates (spec 136), the evidence join with its
/// drop-on-unresolvable rule, <see cref="GuidanceChangeSupersede"/> (spec 113),
/// <see cref="MediaAttentionCollapse"/> (spec 109), the spec-151 <see cref="ICollectorAttributionResolver"/>
/// seam, and the shared <see cref="ScoreSignalMath"/>/<see cref="ScoringChannelComposition"/> primitives.
/// Where a prospective v11 term is needed it calls the shared production helpers
/// (<see cref="ScoreSignalMath.DirectionalActivityMass"/>, <see cref="ScoreSignalMath.PositiveAttentionReach"/>)
/// — never a second copy of the scoring math.
/// </para>
/// <para>
/// <b>Deliberately NOT replicated:</b> the previous/velocity window read. No number this audit reports
/// consumes <c>SignalVelocityScore</c>, so <see cref="ScoringInput.PreviousSignals"/> is passed empty; the
/// funnel, every channel term, the breadth answer, the attention component, the notedness discount and the
/// §6 composite are all velocity-independent.
/// </para>
/// <para>Deterministic (AD-3): same store + same declared as-of ⇒ same output. Read-only: this type never
/// writes anything anywhere.</para>
/// </summary>
public sealed class ChannelFeasibilityAudit
{
    /// <summary>
    /// The pinned as-of instant D (spec 158 §2): the common <c>WindowEndUtc</c> of the latest completed
    /// full-collection baseline run present when the spec was accepted. Never the audit execution time.
    /// </summary>
    public static readonly DateTimeOffset PinnedAsOfUtc =
        DateTimeOffset.Parse("2026-07-28T08:04:27.7605621Z", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The <c>PipelineRunRecord</c> id whose <c>WindowEndUtc</c> pins <see cref="PinnedAsOfUtc"/>.</summary>
    public const string PinnedRunRecordId = "120c99e2-2b8d-4831-99aa-1f02a0d58896";

    /// <summary>The observation window: exactly the 60-day baseline window, (D − 60d, D].</summary>
    public static readonly TimeSpan PinnedWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// The candidate collector channels (spec 158 §3), by exact ordinal <c>IEvidenceCollector.CollectorName</c>
    /// — referenced from <see cref="RadarCollectorNames"/> so they cannot drift from the shipped collectors.
    /// </summary>
    public static readonly IReadOnlyList<string> CandidateCollectors =
    [
        RadarCollectorNames.SecForm4,
        RadarCollectorNames.Sec13DG,
        RadarCollectorNames.Rss,
        RadarCollectorNames.NewsSearch,
        RadarCollectorNames.UsaSpending,
        RadarCollectorNames.SecEdgar,
        RadarCollectorNames.Fda,
    ];

    /// <summary>
    /// The prospective v11 collector direction factor — <c>saturation · max(0, preponderance)</c>, i.e.
    /// <c>radar-formula-v10</c>'s composition, which spec 157 §4 fixes as v11's base (v11 = v10 + the two
    /// AD-16 corrections: directional-only saturation via
    /// <see cref="ScoreSignalMath.DirectionalActivityMass"/>, positive-only breadth via
    /// <see cref="ScoreSignalMath.PositiveAttentionReach"/>). Declared here because v10 keeps its own factor
    /// private to its class; a shipped v11 will declare its own beside its composition exactly as v10 does.
    /// </summary>
    private static readonly CollectorChannelScore ProspectiveV11DirectionFactor =
        (saturation, preponderance) => saturation * Math.Max(0.0, preponderance);

    /// <summary>
    /// The predeclared <c>filings-led-v11</c> budget, exactly as spec 157 §7 writes it: insider
    /// <c>sec-form4</c> .50 / institutional <c>sec-13dg</c> .30 / breadth .20; saturations 2 / 3 / 3.
    /// Built through the production <see cref="ScoringChannelSet.Create"/> validator; evaluated in memory
    /// only — no strategy is configured, no snapshot is persisted.
    /// </summary>
    public static ScoringChannelSet FilingsLedV11Budget { get; } = ScoringChannelSet.Create(
        [
            ScoringChannel.Collector("insider", [RadarCollectorNames.SecForm4], 0.50, 2),
            ScoringChannel.Collector("institutional", [RadarCollectorNames.Sec13DG], 0.30, 3),
            ScoringChannel.Breadth("breadth", 0.20, 3),
        ],
        "filings-led-v11");

    private readonly ISignalRepository _signals;
    private readonly IEvidenceRepository _evidence;
    private readonly ICompanyRepository _companies;
    private readonly MediaAttentionCollapse _mediaCollapse;
    private readonly ScoringWeights _weights;
    private readonly IAttentionSourceWeights _sourceWeights;
    private readonly ICollectorAttributionResolver _attribution;

    public ChannelFeasibilityAudit(
        ISignalRepository signals,
        IEvidenceRepository evidence,
        ICompanyRepository companies,
        MediaAttentionCollapse mediaCollapse,
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights,
        ICollectorAttributionResolver attribution)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(mediaCollapse);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        ArgumentNullException.ThrowIfNull(attribution);
        weights.Validate();

        _signals = signals;
        _evidence = evidence;
        _companies = companies;
        _mediaCollapse = mediaCollapse;
        _weights = weights;
        _sourceWeights = sourceWeights;
        _attribution = attribution;
    }

    /// <summary>
    /// Runs the whole characterization over every company in the repository. <paramref name="extraBudgets"/>
    /// lets a §6 recommendation candidate be evaluated through the SAME in-memory pass over the SAME
    /// assembled inputs — never a second scoring implementation, never a persisted snapshot.
    /// </summary>
    public async Task<AuditReport> RunAsync(
        DateTimeOffset asOfUtc,
        TimeSpan window,
        CancellationToken ct,
        params IReadOnlyList<ScoringChannelSet> extraBudgets)
    {
        var windowStartUtc = asOfUtc - window;

        var companies = (await _companies.GetAllAsync(ct).ConfigureAwait(false))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();

        var results = new List<CompanyAuditResult>(companies.Count);
        foreach (var company in companies)
        {
            results.Add(
                await AuditCompanyAsync(company, windowStartUtc, asOfUtc, extraBudgets, ct)
                    .ConfigureAwait(false));
        }

        return new AuditReport(asOfUtc, windowStartUtc, window, results);
    }

    private async Task<CompanyAuditResult> AuditCompanyAsync(
        Company company,
        DateTimeOffset windowStartUtc,
        DateTimeOffset asOfUtc,
        IReadOnlyList<ScoringChannelSet> extraBudgets,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var allSignals = await _signals.GetByCompanyAsync(company.Id, ct).ConfigureAwait(false);

        // ScoringEngine's window + known-at + review filter, verbatim (spec 136; the default strategy
        // consumes every SignalType, so no type gate applies).
        var windowedApproved = allSignals
            .Where(s => s.ObservedAtUtc > windowStartUtc && s.ObservedAtUtc <= asOfUtc)
            .Where(s => s.CreatedAtUtc <= asOfUtc)
            .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
            .ToList();

        // Evidence join, with ScoringEngine's drop rule: a signal whose evidence id does not resolve is
        // dropped BEFORE collector attribution — reported as evidence-unresolvable, NEVER relabelled
        // unattributed (spec 158 §3).
        var pairs = new List<ScoringSignal>();
        var unresolvableSignals = 0;
        var unresolvableEvidenceIds = new HashSet<Guid>();
        foreach (var signal in windowedApproved)
        {
            var evidence = await _evidence.GetByIdAsync(signal.EvidenceId, ct).ConfigureAwait(false);
            if (evidence is null)
            {
                unresolvableSignals++;
                unresolvableEvidenceIds.Add(signal.EvidenceId);
                continue;
            }

            pairs.Add(new ScoringSignal(signal, evidence));
        }

        // ScoringEngine's deterministic ordering, verbatim.
        pairs.Sort(static (a, b) =>
        {
            var byObserved = a.Signal.ObservedAtUtc.CompareTo(b.Signal.ObservedAtUtc);
            return byObserved != 0 ? byObserved : a.Signal.Id.CompareTo(b.Signal.Id);
        });

        // Spec 193 §2: the supersede now returns survivors + a per-survivor removed count. This audit is a
        // read-only diagnostic and consumes only the survivors, exactly as before.
        var superseded = GuidanceChangeSupersede.Apply(pairs).Signals;
        var collapse = _mediaCollapse.Collapse(superseded);
        var scored = collapse.Signals.ToList();

        // Collector attribution over the RESOLVED scoring inputs (the post-collapse set the channels
        // consume), through the one spec-151 seam.
        var attributionOf = new CollectorAttribution[scored.Count];
        var recorded = 0;
        var inferred = 0;
        var unattributed = 0;
        for (var i = 0; i < scored.Count; i++)
        {
            attributionOf[i] = _attribution.Resolve(scored[i].Evidence);
            switch (attributionOf[i].Source)
            {
                case CollectorAttributionSource.Recorded:
                    recorded++;
                    break;
                case CollectorAttributionSource.Inferred:
                    inferred++;
                    break;
                default:
                    unattributed++;
                    break;
            }
        }

        // The shared per-signal factors, exactly as every formula computes them over the same set.
        var recency = ScoreSignalMath.RecencyFactors(scored, windowStartUtc, asOfUtc, _weights.RecencyFloor);
        var quality = ScoreSignalMath.QualityFactors(scored, _weights);

        // ---- Per candidate collector channel: the saturation-independent structural inputs ----
        var channels = new List<CompanyChannelReading>(CandidateCollectors.Count);
        foreach (var collectorName in CandidateCollectors)
        {
            // Selection mirrors ScoringChannel.Consumes: EXACT ordinal collector-name match, false for an
            // unattributed (null-named) signal — reused, not re-implemented.
            var selector = ScoringChannel.Collector(collectorName, [collectorName], 0.0, 1.0);

            var subSignals = new List<ScoringSignal>();
            var subRecency = new List<double>();
            var subQuality = new List<double>();
            var channelRecorded = 0;
            var channelInferred = 0;
            for (var i = 0; i < scored.Count; i++)
            {
                if (!selector.Consumes(attributionOf[i].CollectorName))
                {
                    continue;
                }

                subSignals.Add(scored[i]);
                subRecency.Add(recency[i]);
                subQuality.Add(quality[i]);
                if (attributionOf[i].Source == CollectorAttributionSource.Recorded)
                {
                    channelRecorded++;
                }
                else
                {
                    channelInferred++;
                }
            }

            var mass = ScoreSignalMath.DirectionalMasses(subSignals, subRecency, subQuality);
            var preponderance = ScoreSignalMath.Preponderance(
                mass, _weights.TrajectoryCorroborationK, band: 1.0);

            channels.Add(new CompanyChannelReading(
                Collector: collectorName,
                SignalCount: subSignals.Count,
                RecordedSignals: channelRecorded,
                InferredSignals: channelInferred,
                DirectionalActivityMass: ScoreSignalMath.DirectionalActivityMass(
                    subSignals, subRecency, subQuality),
                Preponderance: preponderance,
                DirectionState: ScoringChannelComposition.DirectionStateOf(mass, preponderance)));
        }

        // ---- The §3/§5 positive-only breadth reading, via the shared prospective primitives ----
        var positivePost = ScoreSignalMath.DistinctThirdPartyPublishers(
            scored.Where(s => s.Signal.Direction == SignalDirection.Positive));
        var positivePre = ScoreSignalMath.DistinctThirdPartyPublishers(
            superseded.Where(s => s.Signal.Direction == SignalDirection.Positive));
        var positiveReach = ScoreSignalMath.PositiveAttentionReach(
            scored, superseded, _weights, _sourceWeights);
        var positiveMediaCount = scored.Count(s =>
            s.Signal.Direction == SignalDirection.Positive && s.Signal.Type == SignalType.MediaAttention);

        // The full-set attention component + notedness discount — the CURRENT-at-D diagnostic the existing
        // composition needs (allowed by spec 158 §6; nothing after D is read). Guarded on an empty window
        // exactly as the formulas short-circuit it.
        var fullReach = scored.Count > 0
            ? ScoreSignalMath.AttentionReach(scored, superseded, _weights, _sourceWeights)
            : 0.0;
        var attentionScore = scored.Count > 0
            ? ScoreSignalMath.AttentionComponent(fullReach, _weights)
            : 0;
        var notednessDiscount = ScoreSignalMath.NotednessDiscount(
            _weights, attentionScore, company.FollowingTier);

        var breadth = new CompanyBreadthReading(
            DistinctPositivePublishersPostCollapse: positivePost.Count,
            DistinctPositivePublishersPreCollapse: positivePre.Count,
            PositivePublisherNames: positivePre.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            PositiveReach: positiveReach,
            PositiveMediaCount: positiveMediaCount,
            FullReach: fullReach,
            AttentionScore: attentionScore,
            NotednessDiscount: notednessDiscount);

        // ---- §6: the predeclared filings-led-v11 budget (and any recommendation candidates), in memory ----
        var filingsLed = EvaluateProspectiveV11(
            company, windowStartUtc, asOfUtc, scored, superseded, recency, quality,
            FilingsLedV11Budget, notednessDiscount);

        var extras = new List<BudgetEvaluation>(extraBudgets.Count);
        foreach (var budget in extraBudgets)
        {
            extras.Add(EvaluateProspectiveV11(
                company, windowStartUtc, asOfUtc, scored, superseded, recency, quality,
                budget, notednessDiscount));
        }

        return new CompanyAuditResult(
            CompanyId: company.Id,
            Name: company.Name,
            Ticker: company.Ticker,
            FollowingTier: company.FollowingTier,
            ApprovedInWindow: windowedApproved.Count,
            EvidenceUnresolvableSignals: unresolvableSignals,
            DistinctUnresolvableEvidenceIds: unresolvableEvidenceIds.Count,
            ResolvedBeforeSupersede: pairs.Count,
            AfterSupersede: superseded.Count,
            AfterCollapse: scored.Count,
            RecordedAttribution: recorded,
            InferredAttribution: inferred,
            UnattributedAttribution: unattributed,
            Channels: channels,
            Breadth: breadth,
            FilingsLedV11: filingsLed,
            ExtraBudgets: extras);
    }

    /// <summary>
    /// Evaluates one channel budget under the prospective v11 terms through the SAME
    /// <see cref="ScoringChannelComposition.Compose"/> pass every shipped channel formula runs — only the
    /// three delegates differ: directional-only activity, the v10/v11 <c>max(0, preponderance)</c> factor,
    /// and positive-only breadth reach. The final stored-shape integer is
    /// <c>Clamp0To100(100 · composite · notednessDiscount)</c>, exactly the v9/v10 Opportunity mapping.
    /// Nothing is persisted.
    /// </summary>
    private BudgetEvaluation EvaluateProspectiveV11(
        Company company,
        DateTimeOffset windowStartUtc,
        DateTimeOffset asOfUtc,
        IReadOnlyList<ScoringSignal> scored,
        IReadOnlyList<ScoringSignal> preCollapse,
        IReadOnlyList<double> recency,
        IReadOnlyList<double> quality,
        ScoringChannelSet budget,
        double notednessDiscount)
    {
        // PreviousSignals empty: velocity feeds no reported number (see the class remarks). EnabledCollectors
        // is provenance-only inside Compose (the ran/not-run split) and never a score input.
        var input = new ScoringInput(
            company.Id, windowStartUtc, asOfUtc, scored,
            Array.Empty<Signal>(), company.FollowingTier)
        {
            PreCollapseSignals = preCollapse,
            EnabledCollectors = CandidateCollectors,
        };

        var composition = ScoringChannelComposition.Compose(
            input,
            recency,
            quality,
            budget,
            _weights,
            _sourceWeights,
            _attribution,
            ScoreSignalMath.DirectionalActivityMass,
            ProspectiveV11DirectionFactor,
            ScoreSignalMath.PositiveAttentionReach);

        var opportunityScore = ScoreSignalMath.Clamp0To100(
            100.0 * composition.Composite * notednessDiscount);

        return new BudgetEvaluation(
            Composite: composition.Composite,
            OpportunityScore: opportunityScore,
            ChannelScores: composition.Channels
                .Select(c => new BudgetChannelScore(c.Channel.Name, c.Score, c.SignalCount, c.Dark))
                .ToList());
    }
}

/// <summary>One collector channel's saturation-independent structural inputs for one company (spec 158 §3).</summary>
public sealed record CompanyChannelReading(
    string Collector,
    int SignalCount,
    int RecordedSignals,
    int InferredSignals,
    double DirectionalActivityMass,
    double Preponderance,
    string DirectionState);

/// <summary>The §3/§5 positive-only breadth reading plus the current-at-D attention diagnostics.</summary>
public sealed record CompanyBreadthReading(
    int DistinctPositivePublishersPostCollapse,
    int DistinctPositivePublishersPreCollapse,
    IReadOnlyList<string> PositivePublisherNames,
    double PositiveReach,
    int PositiveMediaCount,
    double FullReach,
    int AttentionScore,
    double NotednessDiscount);

/// <summary>One in-memory budget evaluation (§6). Nothing here is ever persisted.</summary>
public sealed record BudgetEvaluation(
    double Composite,
    int OpportunityScore,
    IReadOnlyList<BudgetChannelScore> ChannelScores);

/// <summary>One channel's share inside an in-memory §6 budget evaluation.</summary>
public sealed record BudgetChannelScore(string Channel, double Score, int SignalCount, bool Dark);

/// <summary>One company's full audit row: the eligibility funnel, channels, breadth and the §6 evaluations.</summary>
public sealed record CompanyAuditResult(
    Guid CompanyId,
    string Name,
    string? Ticker,
    FollowingTier FollowingTier,
    int ApprovedInWindow,
    int EvidenceUnresolvableSignals,
    int DistinctUnresolvableEvidenceIds,
    int ResolvedBeforeSupersede,
    int AfterSupersede,
    int AfterCollapse,
    int RecordedAttribution,
    int InferredAttribution,
    int UnattributedAttribution,
    IReadOnlyList<CompanyChannelReading> Channels,
    CompanyBreadthReading Breadth,
    BudgetEvaluation FilingsLedV11,
    IReadOnlyList<BudgetEvaluation> ExtraBudgets);

/// <summary>The whole audit over every company at one pinned instant.</summary>
public sealed record AuditReport(
    DateTimeOffset AsOfUtc,
    DateTimeOffset WindowStartUtc,
    TimeSpan Window,
    IReadOnlyList<CompanyAuditResult> Companies);
