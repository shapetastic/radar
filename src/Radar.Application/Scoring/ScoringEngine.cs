using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
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

    private readonly ISignalRepository _signalRepository;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IScoreRepository _scoreRepository;
    private readonly IScoreFormula _formula;
    private readonly ScoringOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScoringEngine> _logger;

    public ScoringEngine(
        ISignalRepository signalRepository,
        IEvidenceRepository evidenceRepository,
        IScoreRepository scoreRepository,
        IScoreFormula formula,
        ScoringOptions options,
        TimeProvider timeProvider,
        ILogger<ScoringEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(scoreRepository);
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _signalRepository = signalRepository;
        _evidenceRepository = evidenceRepository;
        _scoreRepository = scoreRepository;
        _formula = formula;
        _options = options;
        _timeProvider = timeProvider;
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

        var input = new ScoringInput(companyId, windowStartUtc, windowEndUtc, pairs);
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
            CreatedAtUtc: _timeProvider.GetUtcNow());

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
