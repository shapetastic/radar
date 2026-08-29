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
/// READ-ONLY OVER EVERYTHING IT SCORES. Replay does not collect, extract, re-run the AI directional read,
/// resolve, review, write a run record, or build a report. It reads companies + signals + evidence and writes
/// score snapshots into its OWN replay-scoped, labelled store. The live scores directory and the shared score
/// repository the weekly report renders are never touched — structurally, via the separate
/// <see cref="IReplayScoreSnapshotFileStoreFactory"/> / <see cref="IReplayScoringStrategyFactory"/> seams.
/// </para>
/// <para>
/// <b>IT DOES WRITE THE SCORING-CONFIG STORE, AND THAT IS A PROVENANCE RECORD RATHER THAN A SCORING MUTATION
/// (spec 148).</b> Until this slice replay took neither the config store nor the tripwire, so a replay-only
/// run in a fresh data root emitted snapshots stamped with a <c>ScoringConfigVersion</c> that dereferenced to
/// NOTHING — the weights that produced those scores were unrecoverable. That is the weakest provenance in the
/// system sitting on exactly the path the spec-140 leaderboard is meant to rank strategies from. So the
/// runner now does what all three forward runners do, and only that:
/// <list type="bullet">
/// <item><description><see cref="StrategyIdentityGuard.VerifyAsync"/> runs FIRST, before a single company is
/// read, so an in-place strategy edit costs no scoring — and no snapshot lands under the old name. The guard
/// is read-mostly and its store read degrades to "unrecorded" on failure (AD-8), so it cannot fail a
/// read-only mode on a disk hiccup;</description></item>
/// <item><description><c>WriteIfNewAsync(strategy.Engine.EffectiveConfig)</c> once per strategy, so every
/// replayed snapshot's stamp dereferences back to the exact weights. Insert-if-new, so it is free when the
/// config already exists — which, for a replay of a strategy the forward pipeline also runs, it does.
/// </description></item>
/// </list>
/// Neither writes a signal, an evidence item, or a byte under the live scores root, and neither changes a
/// single score: the whole point is that <c>replay ⊆ forward</c> still holds field for field.
/// </para>
/// <para>
/// <b>SAME-LABEL OVERWRITE WARNS LOUDLY, AGGREGATED PER STRATEGY (spec 148).</b> Replay files are named by
/// as-of instant, which is what makes a re-run idempotent — and equally what makes a re-run under an
/// already-used label REPLACE a series that may already have been ranked. The decision recorded here is
/// warn, not fail and not silent: failing would break the legitimate "re-replay after fixing a data problem"
/// workflow, and silence is how a comparison quietly becomes wrong. It is ONE warning per (label, strategy)
/// carrying the count of replaced as-of points — per-file warnings would be thousands of lines, so this
/// follows spec 145's aggregation precedent — and it says what the count MEANS: if the strategy's config
/// changed since the old output was written, the two are not comparable, and a new label keeps both.
/// </para>
/// <para>
/// <b>SCORE-ASSEMBLY DIAGNOSTICS ARE AGGREGATED FOR THE WHOLE INVOCATION (spec 197 §3).</b> The engine no
/// longer warns per company about unresolved evidence or neutralized accrued news directions; it returns the
/// counts on <see cref="CompanyScoreResult.Diagnostics"/>. Replay is exactly where the old shape was worst —
/// strategies × as-of points × companies — so it aggregates through the SAME
/// <see cref="ScoreAssemblyDiagnosticsAggregator"/> the forward pass uses and emits at most ONE Warning per
/// category across the entire replay, with the distinct as-of count as a fourth honesty axis. Moving the
/// Warning out of the shared engine must not make replay silent.
/// </para>
/// <para>
/// DETERMINISTIC (AD-3). The nesting is fixed — strategies in configured order, then as-of instants
/// ascending, then companies in repository order — and nothing in the scoring path reads a wall clock or a
/// random source. Two identical replays over an unchanged signal store therefore produce identical output,
/// modulo the snapshot/link <c>Guid</c>s the engine freshly mints on every call (forward runs do this too).
/// </para>
/// </summary>
/// <remarks>
/// <b>HISTORY HYDRATION — the prerequisite, now shipped (spec 142).</b>
/// <para>
/// Replay reads a company's current-window signals from <see cref="ISignalRepository"/> and their evidence
/// from <see cref="IEvidenceRepository"/> (the previous/velocity window already comes from the on-disk
/// signal store). In the composed app both of those now resolve to the DURABLE file stores, which hydrate
/// the accrued <c>signals/</c> and <c>evidence/raw/</c> history lazily on first read — so a replay in a
/// fresh process finally has something to replay. Spec 142 also closed the correctness hole that blocked
/// this: the raw-evidence schema now carries <c>EvidenceQuality</c> explicitly (recovering it for legacy
/// files from the <c>metadata.quality</c> the collector persisted all along), so hydrated evidence scores
/// the way it scored live rather than approximately.
/// </para>
/// <para>
/// A test or a host that composes the in-memory repositories instead still behaves exactly as before —
/// exact over what the process holds, empty over what it does not, never approximated.
/// </para>
/// <para>
/// <b>What accrued history can actually support is a separate, measured question.</b> A signal is only
/// replayable if its <c>EvidenceId</c> still resolves, and evidence identity is a fresh <c>Guid</c> per run
/// while raw-evidence FILES are keyed by content hash — so a signal re-extracted in a later run cites an
/// evidence id that was never written to disk. Spec 142's durable evidence repository stops this happening
/// going FORWARD (re-collection no longer re-extracts), but it does not backfill; the honest span of
/// replayable history is therefore whatever the resolvable-provenance ratio says it is, not the raw signal
/// count.
/// </para>
/// </remarks>
public sealed class ReplayRunner : IReplayRunner
{
    private readonly ICompanyRepository _companyRepository;
    private readonly IReplayScoringStrategyFactory _strategies;
    private readonly IReplayScoreSnapshotFileStoreFactory _scoreFileStores;
    private readonly IScoringConfigStore _scoringConfigStore;
    private readonly ReplayPlan _plan;
    private readonly ILogger<ReplayRunner> _logger;

    public ReplayRunner(
        ICompanyRepository companyRepository,
        IReplayScoringStrategyFactory strategies,
        IReplayScoreSnapshotFileStoreFactory scoreFileStores,
        IScoringConfigStore scoringConfigStore,
        ReplayPlan plan,
        ILogger<ReplayRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(scoreFileStores);
        ArgumentNullException.ThrowIfNull(scoringConfigStore);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(logger);

        _companyRepository = companyRepository;
        _strategies = strategies;
        _scoreFileStores = scoreFileStores;
        _scoringConfigStore = scoringConfigStore;
        _plan = plan;
        _logger = logger;
    }

    public async Task<ReplayResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var strategies = _strategies.Runtimes;

        // Spec 141's tripwire, spec 148's addition: the FIRST real statement, mirroring all three forward
        // runners. A strategy that was edited in place must fail before anything is read or written, so a
        // misconfiguration costs no scoring and — the point for replay specifically — no snapshot lands in a
        // labelled series under a name whose meaning has changed. Read failures degrade to "unrecorded"
        // inside the store (AD-8), so a disk hiccup cannot fail this read-only mode.
        await StrategyIdentityGuard
            .VerifyAsync(strategies, _scoringConfigStore, _logger, ct).ConfigureAwait(false);

        var series = _plan.Series;
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);

        // Stated UP FRONT, before any work: the spec forbids silently truncating a large range, and the
        // series applies no cap, so the size of what is about to run has to be visible rather than inferred
        // from however many files appear at the end.
        _logger.LogInformation(
            "Replay '{Label}': {AsOfPoints} as-of point(s) from {From:o} to {To:o} step {Step} × "
                + "{StrategyCount} strateg(ies) × {CompanyCount} company/companies = {Scorings} scoring(s). "
                + "Read-only over signals/evidence; score output goes only to the replay-scoped store.",
            _plan.Label,
            series.Count,
            series.FromUtc,
            series.ToUtc,
            series.Step,
            strategies.Count,
            companies.Count,
            (long)series.Count * strategies.Count * companies.Count);

        var snapshotsWritten = 0;

        // Spec 197 §3: the engine no longer emits its own per-company Warnings for unresolved evidence
        // (spec 145) or neutralized accrued news directions (spec 194 §1.4) — it returns the counts. Replay
        // is the WORST case for the old shape: strategies × as-of points × companies, so a real series would
        // have produced thousands of lines. Moving the Warning out of the shared engine must not make replay
        // SILENT, so the same bounded pair is emitted here, once for the COMPLETE invocation, with the as-of
        // axis included because a replay legitimately spans many instants.
        var assemblyDiagnostics =
            new ScoreAssemblyDiagnosticsAggregator($"Replay '{_plan.Label}'", reportAsOfAxis: true);

        // Fixed, deterministic nesting (AD-3): strategy → as-of instant (ascending) → company. Strategy
        // outermost so each strategy's store is resolved once and its whole series is written contiguously.
        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            // Spec 148: persist the effective resolved config ONCE PER STRATEGY, exactly as ScoringPass does
            // for a forward run — content-addressed and insert-if-new, so it costs nothing when the forward
            // pipeline already wrote it, and it is the difference between a replayed snapshot's stamp
            // dereferencing to the weights that produced it and dereferencing to nothing. Best-effort like
            // every other file store: it never aborts the replay or changes a count.
            var configWrite = await _scoringConfigStore
                .WriteIfNewAsync(strategy.Engine.EffectiveConfig, ct)
                .ConfigureAwait(false);
            if (!configWrite.Written)
            {
                // Spec 201 §1: the outcome is no longer discarded. ONE Warning per strategy (the config is
                // written once per strategy, so this IS the aggregate) — every replayed snapshot of this
                // strategy carries a stamp that dereferences to nothing on disk.
                _logger.LogWarning(
                    "Replay '{Label}': strategy {StrategyName}'s effective scoring config could NOT be "
                        + "durably persisted to {Path}. Its replayed snapshots still carry the "
                        + "ScoringConfigVersion stamp, but the stamp dereferences to nothing on disk until "
                        + "a later run writes the same content-addressed file.",
                    _plan.Label,
                    strategy.Definition.Name,
                    configWrite.Path);
            }

            var scoreFileStore = _scoreFileStores.ForStrategy(_plan.Label, strategy.Definition);

            // Monotonic per factory instance (a re-run in the same process keeps counting), so the number
            // this run is responsible for is the DIFFERENCE across the strategy's loop.
            var overwrittenBefore = _scoreFileStores.OverwrittenCount(_plan.Label, strategy.Definition);

            foreach (var asOfUtc in series.Points)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var company in companies)
                {
                    ct.ThrowIfCancellationRequested();

                    // THE point of the slice: the live engine, the live read seam, a historical as-of.
                    var result = await strategy.Engine
                        .ScoreCompanyAsync(company.Id, asOfUtc, ct).ConfigureAwait(false);
                    assemblyDiagnostics.Record(
                        strategy.Definition.Name, company.Id, asOfUtc, result.Diagnostics);
                    await scoreFileStore
                        .WriteAsync(result.Snapshot, result.Links, ct).ConfigureAwait(false);
                    snapshotsWritten++;
                }
            }

            // ONE warning per (label, strategy), never per file (spec 145's aggregation precedent): a
            // per-point line would be thousands of entries on a real series and the operator would learn
            // nothing extra from any of them. It warns rather than fails because re-replaying a label after
            // fixing a data problem is legitimate; it is not silent because a replaced series that was
            // already ranked is how a strategy comparison quietly becomes wrong.
            var overwritten =
                _scoreFileStores.OverwrittenCount(_plan.Label, strategy.Definition) - overwrittenBefore;
            if (overwritten > 0)
            {
                _logger.LogWarning(
                    "Replay '{Label}': strategy {StrategyName} OVERWROTE {OverwrittenCount} as-of point(s) "
                        + "already on disk under this label. A previously written — and possibly already "
                        + "ranked — series has been replaced in place; if this strategy's configuration "
                        + "changed since that output was written, the old and new results are NOT comparable. "
                        + "Use a NEW replay label to keep both.",
                    _plan.Label,
                    strategy.Definition.Name,
                    overwritten);
            }

            _logger.LogInformation(
                "Replay '{Label}': strategy {StrategyName} replayed over {AsOfPoints} as-of point(s).",
                _plan.Label,
                strategy.Definition.Name,
                series.Count);
        }

        // Spec 197 §3: at most one Warning per diagnostic category for the COMPLETE replay invocation —
        // never per strategy, per as-of point or per company.
        assemblyDiagnostics.LogAggregates(_logger);

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
