using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// Deterministic Stage 6 orchestration. Selects the recent-signal window, loads the evidence behind
/// each reviewed signal, delegates the actual scoring math to <see cref="IScoreFormula"/>, maps the
/// result onto a domain <see cref="CompanyScoreSnapshot"/>, builds one <see cref="ScoreEvidenceLink"/>
/// per contribution, and persists everything via <see cref="IScoreRepository"/>.
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
/// </summary>
public sealed class ScoringEngine : IScoringEngine
{
    private const string EngineVersion = "mvp-engine-v1";

    private readonly ISignalRepository _signalRepository;
    private readonly ISignalFileStore _signalFileStore;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IScoreRepository _scoreRepository;
    private readonly IScoreFormula _formula;
    private readonly MediaAttentionCollapse _mediaCollapse;
    private readonly ScoringOptions _options;
    private readonly ILogger<ScoringEngine> _logger;

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
        IScoreFormula formula,
        ScoringWeights weights,
        IAttentionSourceWeights sourceWeights,
        ISignalSourceDescriptor sourceDescriptor,
        InsiderMaterialityWeights insiderMaterialityWeights,
        MediaAttentionCollapse mediaCollapse,
        ScoringOptions options,
        ILogger<ScoringEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalFileStore);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(scoreRepository);
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
        _formula = formula;
        _mediaCollapse = mediaCollapse;
        _options = options;
        _logger = logger;

        var attentionDescriptor = sourceWeights.CanonicalDescriptor();
        var signalSourceDescriptor = sourceDescriptor.CanonicalDescriptor();
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

        // Window + review filter — both are tunable pipeline scaffolding, NOT formula:
        //   * window rule: ObservedAtUtc in (windowStartUtc, windowEndUtc] — exclusive start, inclusive end;
        //   * review rule: scoring consumes only Approved (human/deterministically reviewed) signals.
        var windowedApproved = allSignals
            .Where(s => s.ObservedAtUtc > windowStartUtc && s.ObservedAtUtc <= windowEndUtc)
            .Where(s => s.ReviewStatus == SignalReviewStatus.Approved);

        var pairs = new List<ScoringSignal>();
        foreach (var signal in windowedApproved)
        {
            // Provenance cannot be established without the source evidence; drop the signal and log once.
            var evidence = await _evidenceRepository.GetByIdAsync(signal.EvidenceId, ct).ConfigureAwait(false);
            if (evidence is null)
            {
                _logger.LogWarning(
                    "Dropping signal {SignalId} for company {CompanyId}: evidence {EvidenceId} not found.",
                    signal.Id, companyId, signal.EvidenceId);
                continue;
            }

            pairs.Add(new ScoringSignal(signal, evidence));
        }

        // Deterministic ordering so the formula input and resulting links are stable across runs.
        pairs.Sort(static (a, b) =>
        {
            var byObserved = a.Signal.ObservedAtUtc.CompareTo(b.Signal.ObservedAtUtc);
            return byObserved != 0 ? byObserved : a.Signal.Id.CompareTo(b.Signal.Id);
        });

        // Same-event media collapse (spec 109): many near-simultaneous outlets covering ONE event each emit a
        // MediaAttention signal, inflating the media contribution and the signal count with duplication (not
        // breadth). Collapse those to one representative per event window BEFORE the formula sees them (a
        // signal-count de-noising transform, not a formula change). Provenance is preserved: the representative
        // is a real signal keeping its evidence link, and the collapsed count is surfaced on its contribution
        // reason below. Non-MediaAttention signals and the activity-only previousSignals are untouched.
        var collapse = _mediaCollapse.Collapse(pairs);
        var scoredSignals = collapse.Signals.ToList();

        // The immediately-preceding window of the same length, now sourced from the ON-DISK signal store
        // (cross-run) rather than the in-memory repo — the in-memory repo starts empty every process and
        // holds only THIS run's signals, so slicing the previous window from it left previous-window
        // activity at 0 on every fresh run. Velocity then collapsed to its no-previous behaviour: exactly
        // 50 only on a quiet current window, and above 50 whenever the current window had any activity.
        // It is carried as activity-only input for velocity measurement:
        //   * window rule: ObservedAtUtc in (previousWindowStartUtc, windowStartUtc] — note the shared
        //     boundary with the current window means a signal exactly at windowStartUtc belongs here (AD-6);
        //   * review rule: same Approved-only filter as the current window.
        // The read returns Approved-only, window-filtered, deterministically-ordered signals (AD-3). No
        // evidence is loaded for it and it never builds contributions / ScoreEvidenceLinks — provenance is
        // only the current-window signals (AD-6). A failed/empty read degrades to an empty previous window
        // (the safe no-previous velocity); the store swallows per-file failures, but OperationCanceledException
        // still propagates (no broad catch here).
        var previousWindowStartUtc = windowStartUtc - _options.Window;

        var previousSignals = await _signalFileStore
            .ReadApprovedInWindowAsync(companyId, previousWindowStartUtc, windowStartUtc, ct)
            .ConfigureAwait(false);

        var input = new ScoringInput(companyId, windowStartUtc, windowEndUtc, scoredSignals, previousSignals);
        var computation = _formula.Compute(input);

        // Record both identities so snapshots remain reproducible and auditable.
        var scoringVersion = $"{EngineVersion}+{_formula.Version}";

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
            ScoringConfigVersion: _scoringConfigFingerprint);

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

        _logger.LogInformation(
            "Scored company {CompanyId} from {SignalCount} signal(s) using {ScoringVersion}.",
            companyId, scoredSignals.Count, scoringVersion);

        return new CompanyScoreResult(snapshot, links);
    }
}
