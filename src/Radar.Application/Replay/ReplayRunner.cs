using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Scoring;

namespace Radar.Application.Replay;

/// <summary>
/// Scores every configured strategy at every requested historical as-of instant, over the signals Radar
/// already stored (spec 139). This is where spec 136's point-in-time honesty pays off: both scoring reads
/// filter <c>CreatedAtUtc &lt;= windowEndUtc</c>, so calling the SAME <see cref="IScoringEngine"/> the live
/// pipeline calls, with a past <c>windowEndUtc</c>, provably sees only what Radar knew then.
/// <para>
/// REUSE, NOT A FORK. There is no scoring logic here at all: the runner sequences
/// <see cref="IScoringEngine.ScoreCompanyAsync"/> and writes what it returns. The as-of instant was already a
/// parameter of that method, so "replay" is nothing more than passing a historical value for it. A second
/// copy of the scoring path would drift and silently invalidate the replay⊆forward invariant that makes the
/// whole exercise meaningful — so there is none.
/// </para>
/// <para>
/// READ-ONLY. Replay does not collect, extract, re-run the AI directional read, resolve, review, write the
/// scoring-config store, write a run record, or build a report. It reads companies + signals + evidence and
/// writes score snapshots into its OWN replay-scoped, labelled store. The live scores directory and the
/// shared score repository the weekly report renders are never touched — structurally, via the separate
/// <see cref="IReplayScoreSnapshotFileStoreFactory"/> / <see cref="IReplayScoringStrategyFactory"/> seams.
/// </para>
/// <para>
/// DETERMINISTIC (AD-3). The nesting is fixed — strategies in configured order, then as-of instants
/// ascending, then companies in repository order — and nothing in the scoring path reads a wall clock or a
/// random source. Two identical replays over an unchanged signal store therefore produce identical output,
/// modulo the snapshot/link <c>Guid</c>s the engine freshly mints on every call (forward runs do this too).
/// </para>
/// </summary>
/// <remarks>
/// <b>KNOWN LIMITATION — history hydration is a prerequisite follow-up, and is NOT implemented here.</b>
/// <para>
/// Replay reads a company's current-window signals from the in-memory <see cref="ISignalRepository"/> and
/// their evidence from the in-memory <see cref="IEvidenceRepository"/> (the previous/velocity window already
/// comes from the on-disk signal store). Both in-memory repositories start EMPTY in a fresh process, so a
/// replay run that does not also collect has nothing to score beyond the velocity window: today this harness
/// is exact and honest for a process that already holds the signals (and for tests), but a production replay
/// over months of accrued history additionally needs those two repositories hydrated from the on-disk stores.
/// </para>
/// <para>
/// That hydration is deliberately NOT built yet, because it cannot currently be done faithfully: the on-disk
/// raw-evidence schema (<c>FileRawEvidenceStore</c>'s persisted record) does not carry
/// <c>EvidenceQuality</c>, which the scoring formula reads. A lossy rehydration — defaulting the quality to
/// some plausible value — would silently produce replay scores that differ from what the forward run
/// produced, i.e. it would break the one invariant replay exists to uphold, while looking like it worked.
/// The honest sequence is: add the missing field to the raw-evidence schema first, then hydrate. Until then,
/// replay is exact over what the process holds and empty over what it does not — never approximated.
/// </para>
/// </remarks>
public sealed class ReplayRunner : IReplayRunner
{
    private readonly ICompanyRepository _companyRepository;
    private readonly IReplayScoringStrategyFactory _strategies;
    private readonly IReplayScoreSnapshotFileStoreFactory _scoreFileStores;
    private readonly ReplayPlan _plan;
    private readonly ILogger<ReplayRunner> _logger;

    public ReplayRunner(
        ICompanyRepository companyRepository,
        IReplayScoringStrategyFactory strategies,
        IReplayScoreSnapshotFileStoreFactory scoreFileStores,
        ReplayPlan plan,
        ILogger<ReplayRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(scoreFileStores);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(logger);

        _companyRepository = companyRepository;
        _strategies = strategies;
        _scoreFileStores = scoreFileStores;
        _plan = plan;
        _logger = logger;
    }

    public async Task<ReplayResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var strategies = _strategies.Runtimes;
        var series = _plan.Series;
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);

        // Stated UP FRONT, before any work: the spec forbids silently truncating a large range, and the
        // series applies no cap, so the size of what is about to run has to be visible rather than inferred
        // from however many files appear at the end.
        _logger.LogInformation(
            "Replay '{Label}': {AsOfPoints} as-of point(s) from {From:o} to {To:o} step {Step} × "
                + "{StrategyCount} strateg(ies) × {CompanyCount} company/companies = {Scorings} scoring(s). "
                + "Read-only over signals/evidence; output goes only to the replay-scoped store.",
            _plan.Label,
            series.Count,
            series.FromUtc,
            series.ToUtc,
            series.Step,
            strategies.Count,
            companies.Count,
            (long)series.Count * strategies.Count * companies.Count);

        var snapshotsWritten = 0;

        // Fixed, deterministic nesting (AD-3): strategy → as-of instant (ascending) → company. Strategy
        // outermost so each strategy's store is resolved once and its whole series is written contiguously.
        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            var scoreFileStore = _scoreFileStores.ForStrategy(_plan.Label, strategy.Definition);

            foreach (var asOfUtc in series.Points)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var company in companies)
                {
                    ct.ThrowIfCancellationRequested();

                    // THE point of the slice: the live engine, the live read seam, a historical as-of.
                    var result = await strategy.Engine
                        .ScoreCompanyAsync(company.Id, asOfUtc, ct).ConfigureAwait(false);
                    await scoreFileStore
                        .WriteAsync(result.Snapshot, result.Links, ct).ConfigureAwait(false);
                    snapshotsWritten++;
                }
            }

            _logger.LogInformation(
                "Replay '{Label}': strategy {StrategyName} replayed over {AsOfPoints} as-of point(s).",
                _plan.Label,
                strategy.Definition.Name,
                series.Count);
        }

        _logger.LogInformation(
            "Replay '{Label}' complete: {SnapshotsWritten} snapshot(s) written across {StrategyCount} "
                + "strateg(ies) and {AsOfPoints} as-of point(s).",
            _plan.Label,
            snapshotsWritten,
            strategies.Count,
            series.Count);

        return new ReplayResult(
            AsOfPoints: series.Count,
            Strategies: strategies.Count,
            SnapshotsWritten: snapshotsWritten);
    }
}
