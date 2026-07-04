using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
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
/// </summary>
public sealed class ScoringEngine : IScoringEngine
{
    private const string EngineVersion = "mvp-engine-v1";

    // Whole scoring-generation stamp gating cross-run comparability (distinct from ScoringVersion).
    // CONVENTION: bump on ANY scoring-affecting change (formula, extractor rules, materiality tiers,
    // ScoringOptions). This generation adds NEGATIVE-DIRECTION DILUTIVE CapitalRaise cues (spec 86): the
    // KeywordSignalExtractor now emits Negative CapitalRaise signals for rights / registered-direct / ATM /
    // shelf / warrant / reverse-split offerings (and "dilution"/"dilutive"), ordered first so a mixed
    // dilutive/growth headline resolves Negative; and it demotes "convertible note" / "credit facility" /
    // "debt financing" from Positive to Neutral (a capital event whose valence the code cannot read). This
    // is an EXTRACTOR-RULE change that moves scoring output — new Negative signals lower Trajectory and the
    // demoted cues now weigh 0 — so per AD-10 the stamp bumps. It is NOT a formula-math change:
    // RadarScoreFormulaV2.Compute already maps Negative -> -1 and excludes Neutral/Mixed from Trajectory, so
    // ScoringVersion/EngineVersion/formula Version are NOT touched. Prior generation: spec 84 shipped news
    // attention breadth by publisher (v7).
    private const string ScoringConfigVersion = "radar-scoring-config-v8";

    private readonly ISignalRepository _signalRepository;
    private readonly ISignalFileStore _signalFileStore;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IScoreRepository _scoreRepository;
    private readonly IScoreFormula _formula;
    private readonly ScoringOptions _options;
    private readonly ILogger<ScoringEngine> _logger;

    public ScoringEngine(
        ISignalRepository signalRepository,
        ISignalFileStore signalFileStore,
        IEvidenceRepository evidenceRepository,
        IScoreRepository scoreRepository,
        IScoreFormula formula,
        ScoringOptions options,
        ILogger<ScoringEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalFileStore);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(scoreRepository);
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _signalRepository = signalRepository;
        _signalFileStore = signalFileStore;
        _evidenceRepository = evidenceRepository;
        _scoreRepository = scoreRepository;
        _formula = formula;
        _options = options;
        _logger = logger;
    }

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

        var input = new ScoringInput(companyId, windowStartUtc, windowEndUtc, pairs, previousSignals);
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
            ScoringConfigVersion: ScoringConfigVersion);

        var links = new List<ScoreEvidenceLink>(computation.Contributions.Count);
        foreach (var contribution in computation.Contributions)
        {
            links.Add(new ScoreEvidenceLink(
                Id: Guid.NewGuid(),
                ScoreSnapshotId: snapshot.Id,
                SignalId: contribution.SignalId,
                EvidenceId: contribution.EvidenceId,
                ContributionReason: contribution.ContributionReason,
                ContributionWeight: contribution.ContributionWeight));
        }

        await _scoreRepository.AddSnapshotAsync(snapshot, ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            await _scoreRepository.AddEvidenceLinkAsync(link, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Scored company {CompanyId} from {SignalCount} signal(s) using {ScoringVersion}.",
            companyId, pairs.Count, scoringVersion);

        return new CompanyScoreResult(snapshot, links);
    }
}
