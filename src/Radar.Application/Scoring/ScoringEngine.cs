using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Domain.Companies;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Deterministic Stage 6 orchestration. Selects the recent-signal window, loads the evidence behind
/// each reviewed signal, loads the company's curated <see cref="FollowingTier"/> (spec 117 — the
/// non-price notedness input the v7 Opportunity discount consumes; a missing company fail-safes to
/// <see cref="FollowingTier.Small"/>), delegates the actual scoring math to <see cref="IScoreFormula"/>,
/// maps the result onto a domain <see cref="CompanyScoreSnapshot"/>, builds one
/// <see cref="ScoreEvidenceLink"/> per contribution, and persists everything via
/// <see cref="IScoreRepository"/>.
///
/// <para>
/// HUMAN-OWNED BOUNDARY: this engine contains <b>no scoring formula</b>. It never inspects, computes,
/// or hard-codes weights — all scoring math lives behind <see cref="IScoreFormula"/>. The operational
/// knobs introduced here (the window length and the "only Approved signals" rule) are tunable pipeline
/// scaffolding, not formula weights.
/// </para>
/// <para>
/// GENERATION STAMP: the <c>ScoringConfigVersion</c> stamped on every snapshot is no longer a hand-bumped
/// code constant but a <b>deterministic content fingerprint</b> of the effective resolved scoring config —
/// the structure identity (<see cref="EngineVersion"/> + <c>_formula.Version</c>) plus every
/// <see cref="ScoringWeights"/> value plus the attention tier-map descriptor
/// (<see cref="IAttentionSourceWeights.CanonicalDescriptor"/>) plus the signal-source descriptor
/// (<see cref="ISignalSourceDescriptor.CanonicalDescriptor"/> — the enabled collector set + extractor
/// rule-set identity, spec 95) plus the insider-materiality descriptor
/// (<see cref="InsiderMaterialityWeights.CanonicalDescriptor"/> — the config-tunable buy/sell tiers +
/// cluster boost, spec 96) plus the media-collapse descriptor
/// (<see cref="MediaAttentionCollapse.CanonicalDescriptor"/> — the same-event media-attention collapse
/// structure + window, spec 109), computed once via
/// <see cref="ScoringConfigFingerprint"/> (AD-10 as amended). Any output-affecting change (formula shape,
/// any weight, the tier map, enabling/disabling a collector, an insider materiality tier, the media-collapse
/// window) re-stamps
/// automatically, so the spec-69
/// comparability gate keeps working when weights are runtime-configurable. <c>ScoringVersion</c> (structure
/// identity, <c>$"{EngineVersion}+{_formula.Version}"</c>) is unchanged.
/// </para>
/// <para>
/// ONE ENGINE INSTANCE IS ONE STRATEGY (spec 137). Every scoring-affecting input is constructor-injected and
/// the effective config + fingerprint are resolved once here, so running N strategies over one collection
/// pass is purely a COMPOSITION concern (<see cref="IScoringStrategyFactory"/>) — the scoring core, the
/// <see cref="IScoringEngine"/> signature and the formula are all untouched. The engine additionally stamps
/// the strategy's human-readable name on each snapshot; that name is NOT a fingerprint input.
/// </para>
/// <para>
/// A strategy may additionally declare WHICH <see cref="SignalType"/>s it consumes (spec 138) via
/// <see cref="SignalTypeFilter"/>. That set — unlike the name — IS folded into the fingerprint (through the
/// signal-source descriptor), because two strategies scoring different signal sets are genuinely different
/// scorings. The default filter consumes everything and folds in as a no-op, so the pinned default
/// fingerprints are unmoved.
/// </para>
/// </summary>
public sealed class ScoringEngine : IScoringEngine
{
    private const string EngineVersion = "mvp-engine-v1";

    private readonly ISignalRepository _signalRepository;
    private readonly ISignalFileStore _signalFileStore;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IScoreRepository _scoreRepository;
    private readonly ICompanyRepository _companyRepository;
    private readonly IScoreFormula _formula;
    private readonly MediaAttentionCollapse _mediaCollapse;
    private readonly ScoringOptions _options;
    private readonly ILogger<ScoringEngine> _logger;

    // The human-readable identity of the strategy this engine instance IS (spec 137), stamped on every
    // snapshot alongside the opaque fingerprint. Null when the engine is composed outside a strategy set
    // (⇒ primary/legacy). Deliberately NOT a fingerprint input and NOT part of EffectiveScoringConfig: two
    // strategies that resolve to the same effective config are genuinely comparable, and hashing the name
    // would move the pinned default fingerprints for no scoring-affecting reason (AD-10).
    private readonly string? _strategyName;

    // The SignalTypes this strategy consumes (spec 138). Defaults to SignalTypeFilter.All (consume
    // everything), which Describe() folds into the source descriptor as a NO-OP, so the default composition's
    // fingerprint is byte-identical to the pre-138 value. Unlike the strategy NAME this IS a fingerprint
    // input: two strategies scoring different signal sets are genuinely different scorings.
    private readonly SignalTypeFilter _signalTypes;

    // The whole scoring-generation stamp: a content fingerprint of the effective resolved scoring config
    // (structure + all weights + tier map), computed once and stamped on every snapshot's
    // ScoringConfigVersion (AD-10 amended, spec 89). Gates cross-run comparability (distinct from
    // ScoringVersion).
    private readonly string _scoringConfigFingerprint;

    // The effective resolved scoring config projection (same tuple the fingerprint hashes), built once in
    // the constructor and exposed as a pure accessor for content-addressed persistence (spec 91). Additive:
    // it does not change scoring output or the stamped fingerprint value.
    private readonly EffectiveScoringConfig _effectiveConfig;

    public ScoringEngine(
        ISignalRepository signalRepository,
        ISignalFileStore signalFileStore,
        IEvidenceRepository evidenceRepository,
        IScoreRepository scoreRepository,
        ICompanyRepository companyRepository,
        IScoreFormula formula,
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights,
        ISignalSourceDescriptor sourceDescriptor,
        InsiderMaterialityWeights insiderMaterialityWeights,
        MediaAttentionCollapse mediaCollapse,
        ScoringOptions options,
        ILogger<ScoringEngine> logger,
        string? strategyName = null,
        SignalTypeFilter? signalTypes = null)
    {
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalFileStore);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(scoreRepository);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(sourceWeights);
        ArgumentNullException.ThrowIfNull(sourceDescriptor);
        ArgumentNullException.ThrowIfNull(insiderMaterialityWeights);
        ArgumentNullException.ThrowIfNull(mediaCollapse);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _signalRepository = signalRepository;
        _signalFileStore = signalFileStore;
        _evidenceRepository = evidenceRepository;
        _scoreRepository = scoreRepository;
        _companyRepository = companyRepository;
        _formula = formula;
        _mediaCollapse = mediaCollapse;
        _options = options;
        _logger = logger;
        _strategyName = strategyName;
        _signalTypes = signalTypes ?? SignalTypeFilter.All;

        var attentionDescriptor = sourceWeights.CanonicalDescriptor();

        // Spec 138: the strategy's declared signal-type set is folded into the SIGNAL-SOURCE descriptor here,
        // inside the engine that also applies the filter behaviourally, so the gate and the hashed identity
        // can never drift apart. ScoringConfigFingerprint and EffectiveScoringConfig are untouched — they
        // store/hash the composed descriptor verbatim, so the scoring-config store's self-verification
        // invariant (the persisted descriptor reproduces the persisted fingerprint) still holds. For the
        // default "all types" filter Describe() returns its input unchanged, so the pinned default
        // fingerprints do not move.
        var signalSourceDescriptor = _signalTypes.Describe(sourceDescriptor.CanonicalDescriptor());
        var insiderMaterialityDescriptor = insiderMaterialityWeights.CanonicalDescriptor();
        var mediaCollapseDescriptor = mediaCollapse.CanonicalDescriptor();
        _scoringConfigFingerprint = ScoringConfigFingerprint.Compute(
            EngineVersion, formula.Version, weights, attentionDescriptor, signalSourceDescriptor,
            insiderMaterialityDescriptor, mediaCollapseDescriptor);

        // Build the effective-config projection from the SAME tuple the fingerprint hashes, so
        // EffectiveConfig.Fingerprint always equals the stamp on every snapshot this engine produces.
        _effectiveConfig = new EffectiveScoringConfig(
            Fingerprint: _scoringConfigFingerprint,
            EngineVersion: EngineVersion,
            FormulaVersion: formula.Version,
            Weights: weights,
            AttentionDescriptor: attentionDescriptor,
            SignalSourceDescriptor: signalSourceDescriptor,
            InsiderMaterialityDescriptor: insiderMaterialityDescriptor,
            MediaCollapseDescriptor: mediaCollapseDescriptor);
    }

    public EffectiveScoringConfig EffectiveConfig => _effectiveConfig;

    public async Task<CompanyScoreResult> ScoreCompanyAsync(
        Guid companyId, DateTimeOffset windowEndUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Operational scaffolding (tunable, NOT a formula weight): a fixed-length recent-signal window.
        var windowStartUtc = windowEndUtc - _options.Window;

        var allSignals = await _signalRepository.GetByCompanyAsync(companyId, ct).ConfigureAwait(false);

        // Window + known-at + review filter — all three are tunable pipeline scaffolding, NOT formula:
        //   * window rule: ObservedAtUtc in (windowStartUtc, windowEndUtc] — exclusive start, inclusive end;
        //   * known-at rule (spec 136, point-in-time honesty): CreatedAtUtc <= windowEndUtc — score only
        //     what Radar KNEW by asOf, so a historical replay at asOf = T never sees a signal that entered
        //     the store after T. A forward run is provably unaffected (AD-7, one run one instant): this
        //     run's signals carry CreatedAtUtc == asOfUtc == windowEndUtc exactly, satisfied by equality;
        //   * review rule: scoring consumes only Approved (human/deterministically reviewed) signals;
        //   * signal-type rule (spec 138): this strategy scores only the SignalTypes it declared. Applied
        //     LAST, after the window / known-at / review predicates, because it is a pure "does this strategy
        //     consume this type" gate and not a provenance change — an out-of-set signal is not deleted, its
        //     evidence chain is untouched, and every other strategy still scores it. Default is all types.
        var windowedApproved = allSignals
            .Where(s => s.ObservedAtUtc > windowStartUtc && s.ObservedAtUtc <= windowEndUtc)
            .Where(s => s.CreatedAtUtc <= windowEndUtc)
            .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
            .Where(s => _signalTypes.Includes(s.Type));

        var pairs = new List<ScoringSignal>();

        // Dropped-signal accounting, AGGREGATED PER COMPANY (spec 145). This used to emit one Warning per
        // dropped signal — ~9,500 per run PER STRATEGY on the live store, because pre-145 evidence identity
        // was minted fresh per run and so a signal's EvidenceId rarely matched any persisted evidence.
        // Spec 145 heals that FORWARD only (accrued history is deliberately left as-is), so the legacy
        // residue does not go away and the flood would not either. Silencing it is not an option — an
        // unresolvable evidence chain is a real provenance defect — so it is aggregated instead: ONE Warning
        // per company carrying the dropped count and the distinct-evidence-id count (those two differing
        // tells you whether it is N signals off one evidence item or N separate items), with the per-signal
        // detail demoted to Debug. The HashSet is allocated only if something actually drops, so the healthy
        // path costs nothing.
        var droppedSignalCount = 0;
        HashSet<Guid>? droppedEvidenceIds = null;

        foreach (var signal in windowedApproved)
        {
            // Provenance cannot be established without the source evidence; drop the signal.
            var evidence = await _evidenceRepository.GetByIdAsync(signal.EvidenceId, ct).ConfigureAwait(false);
            if (evidence is null)
            {
                droppedSignalCount++;
                (droppedEvidenceIds ??= []).Add(signal.EvidenceId);
                _logger.LogDebug(
                    "Dropping signal {SignalId} for company {CompanyId}: evidence {EvidenceId} not found.",
                    signal.Id, companyId, signal.EvidenceId);
                continue;
            }

            pairs.Add(new ScoringSignal(signal, evidence));
        }

        if (droppedSignalCount > 0)
        {
            _logger.LogWarning(
                "Dropped {DroppedSignalCount} signal(s) for company {CompanyId} whose evidence could not be "
                    + "resolved, across {DistinctEvidenceCount} distinct evidence id(s); per-signal detail at Debug.",
                droppedSignalCount, companyId, droppedEvidenceIds!.Count);
        }

        // Deterministic ordering so the formula input and resulting links are stable across runs.
        pairs.Sort(static (a, b) =>
        {
            var byObserved = a.Signal.ObservedAtUtc.CompareTo(b.Signal.ObservedAtUtc);
            return byObserved != 0 ? byObserved : a.Signal.Id.CompareTo(b.Signal.Id);
        });

        // GuidanceChange supersede (spec 113): a filing first collected while the directional earnings read
        // failed has its deterministic Neutral GuidanceChange already persisted; the signal stores are
        // append-only (AD-8), so we supersede at read/assembly time instead of deleting — when the set
        // carries both a directional and a Neutral GuidanceChange over the SAME filing evidence, only the
        // directional one is scored (at most one GuidanceChange per filing; no double-count, ever). The
        // Neutral stays on disk for provenance but gets no contribution/ScoreEvidenceLink. Pure and
        // deterministic (AD-3), and deliberately NOT a fingerprint input (a correctness fix, not a
        // scoring-config change — the stamp must not move).
        var superseded = GuidanceChangeSupersede.Apply(pairs);

        // Same-event media collapse (spec 109): many near-simultaneous outlets covering ONE event each emit a
        // MediaAttention signal, inflating the media contribution and the signal count with duplication (not
        // breadth). Collapse those to one representative per event window BEFORE the formula sees them (a
        // signal-count de-noising transform, not a formula change). Provenance is preserved: the representative
        // is a real signal keeping its evidence link, and the collapsed count is surfaced on its contribution
        // reason below. Non-MediaAttention signals and the activity-only previousSignals are untouched.
        var collapse = _mediaCollapse.Collapse(superseded);
        var scoredSignals = collapse.Signals.ToList();

        // The immediately-preceding window of the same length, now sourced from the ON-DISK signal store
        // (cross-run) rather than the in-memory repo — the in-memory repo starts empty every process and
        // holds only THIS run's signals, so slicing the previous window from it left previous-window
        // activity at 0 on every fresh run. Velocity then collapsed to its no-previous behaviour: exactly
        // 50 only on a quiet current window, and above 50 whenever the current window had any activity.
        // It is carried as activity-only input for velocity measurement:
        //   * window rule: ObservedAtUtc in (previousWindowStartUtc, windowStartUtc] — note the shared
        //     boundary with the current window means a signal exactly at windowStartUtc belongs here (AD-6);
        //   * known-at rule (spec 136): the knowledge threshold is windowEndUtc — the scoring instant —
        //     NOT windowStartUtc. The previous window's OBSERVATION range ends at windowStartUtc, but the
        //     question is "what did Radar know at asOf about the preceding period"; passing windowStartUtc
        //     would under-count previous-window activity and silently shift velocity;
        //   * review rule: same Approved-only filter as the current window.
        // The read returns Approved-only, window-filtered, deterministically-ordered signals (AD-3). No
        // evidence is loaded for it and it never builds contributions / ScoreEvidenceLinks — provenance is
        // only the current-window signals (AD-6). A failed/empty read degrades to an empty previous window
        // (the safe no-previous velocity); the store swallows per-file failures, but OperationCanceledException
        // still propagates (no broad catch here).
        var previousWindowStartUtc = windowStartUtc - _options.Window;

        var previousSignals = await _signalFileStore
            .ReadApprovedInWindowAsync(companyId, previousWindowStartUtc, windowStartUtc, windowEndUtc, ct)
            .ConfigureAwait(false);

        // Spec 113, previous window too (no double-count, ever): the read's cross-run dedupe key includes
        // Direction (spec 85), so a filing whose stale Neutral AND directional GuidanceChange both persist
        // on disk comes back as TWO signals — the same filing must not count twice as activity for
        // velocity. On the healthy spec-78 path only one GuidanceChange per filing ever persists, so this
        // is behaviour-identical there.
        previousSignals = GuidanceChangeSupersede.Apply(previousSignals);

        // Spec 138, previous window too: the velocity comparison must be like-for-like. If a strategy does not
        // consume a SignalType in the CURRENT window, prior activity of that type is not this strategy's prior
        // activity either — otherwise a filtered strategy would measure its own (narrow) current activity
        // against the FULL previous window and read as decelerating for a reason that has nothing to do with
        // the company. Applied AFTER the spec-85 cross-run dedupe/read predicate and the spec-113 supersede,
        // for the same reason as the current window: a pure membership gate, applied last.
        if (!_signalTypes.IsAll)
        {
            previousSignals = [.. previousSignals.Where(s => _signalTypes.Includes(s.Type))];
        }

        // The company's curated following tier (spec 117): the non-price notedness input the v7 Opportunity
        // discount consumes (AD-14 — seed-curated, never price-derived). A missing company degrades to
        // Small (no extra discount) — the fail-safe; never a throw.
        var company = await _companyRepository.GetByIdAsync(companyId, ct).ConfigureAwait(false);
        var followingTier = company?.FollowingTier ?? FollowingTier.Small;

        // Spec 122 (radar-formula-v8): the collapse above discards the distinct-publisher BREADTH of the
        // outlets it dropped alongside the duplicate volume it is meant to remove. Hand the formula the
        // PRE-collapse set as well so it can credit those collapsed-away publishers back into the Attention
        // breadth term (tier-weighted, scaled by ScoringWeights.CollapsedBreadthCredit) while the media COUNT
        // it consumes stays post-collapse. The collapse transform itself is untouched (media-collapse-v1) —
        // this only reads the set the engine already had in hand.
        var input = new ScoringInput(
            companyId, windowStartUtc, windowEndUtc, scoredSignals, previousSignals, followingTier)
        {
            PreCollapseSignals = superseded,
        };
        var computation = _formula.Compute(input);

        // Record both identities so snapshots remain reproducible and auditable.
        var scoringVersion = $"{EngineVersion}+{_formula.Version}";

        // ZERO CONSUMED SIGNALS (spec 138), stated explicitly because it is a deliberate choice: a strategy
        // whose signal-type filter excludes every one of a company's signals gets EXACTLY what a company with
        // no signals already gets today — a neutral snapshot carrying zero ScoreEvidenceLinks. There is no
        // early return and no special case, so the two are indistinguishable by construction. That is not a
        // phantom: a snapshot with zero evidence links IS how Radar already represents "no evidence for this
        // company in this window", and suppressing it instead would make a filtered strategy's series
        // silently discontinuous (a missing company reads as "not scored", not as "nothing to score"),
        // breaking the spec-140 strategy-vs-price comparison the filter exists to enable.

        var snapshot = new CompanyScoreSnapshot(
            Id: Guid.NewGuid(),
            CompanyId: companyId,
            ScoringVersion: scoringVersion,
            TrajectoryScore: computation.Components.TrajectoryScore,
            OpportunityScore: computation.Components.OpportunityScore,
            AttentionScore: computation.Components.AttentionScore,
            EvidenceConfidenceScore: computation.Components.EvidenceConfidenceScore,
            SignalVelocityScore: computation.Components.SignalVelocityScore,
            Explanation: computation.Explanation,
            ComponentJson: computation.ComponentJson,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            // CreatedAtUtc IS the single run instant (windowEndUtc / asOfUtc), NOT a separate
            // wall-clock read. Using the run instant keeps the snapshot deterministic/reproducible
            // and AD-7-consistent: a fresh GetUtcNow() lands a few ms after asOfUtc, so the snapshot
            // would have CreatedAtUtc > periodEndUtc and be excluded by the report's inclusive
            // upper-bound window — the run could never report the snapshots it just created.
            CreatedAtUtc: windowEndUtc,
            ScoringConfigVersion: _scoringConfigFingerprint,
            // Human-readable strategy identity (spec 137), additive alongside the opaque fingerprint. Null
            // ⇒ primary/legacy composition; it never affects the scores, the fingerprint or comparability.
            StrategyName: _strategyName);

        var links = new List<ScoreEvidenceLink>(computation.Contributions.Count);
        foreach (var contribution in computation.Contributions)
        {
            // If this contribution's signal was the representative of a collapsed same-event media bucket,
            // surface the collapsed count on its reason so the report shows ONE line naming the coverage
            // breadth rather than N duplicate lines (provenance for the dropped duplicates; spec 109). The
            // formula itself is untouched — only the persisted ScoreEvidenceLink text is enriched.
            var reason = contribution.ContributionReason;
            if (collapse.CollapsedCounts.TryGetValue(contribution.SignalId, out var collapsedN) && collapsedN > 0)
            {
                reason = $"{reason} (collapsed {collapsedN} same-event media items)";
            }

            links.Add(new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshot.Id,
                SignalId: contribution.SignalId,
                EvidenceId: contribution.EvidenceId,
                ContributionReason: reason,
                ContributionWeight: contribution.ContributionWeight));
        }

        await _scoreRepository.AddSnapshotAsync(snapshot, ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            await _scoreRepository.AddEvidenceLinkAsync(link, ct).ConfigureAwait(false);
        }

        // _signalTypes.ToString() is a precomputed-free join over at most a handful of members ("all types"
        // for the default), so logging the strategy's declared signal set stays cheap while making a filtered
        // strategy's narrower signal count self-explanatory in the run log.
        _logger.LogInformation(
            "Scored company {CompanyId} from {SignalCount} signal(s) using {ScoringVersion} " +
            "(strategy {StrategyName}, signal types {SignalTypes}).",
            companyId, scoredSignals.Count, scoringVersion, _strategyName ?? "(none)", _signalTypes.ToString());

        return new CompanyScoreResult(snapshot, links);
    }
}
