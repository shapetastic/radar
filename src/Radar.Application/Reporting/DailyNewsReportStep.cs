using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.SignalExtraction;

namespace Radar.Application.Reporting;

/// <summary>
/// The in-process daily news view step — a SEPARATE step AFTER judgment-signal materialization (whose minted
/// signals it resolves back from the durable store) and OUTSIDE <c>IRadarPipeline</c>, following the
/// established Worker step pattern. Read-side only: it re-reads the signals the materializer just persisted
/// (a persisted signal must always resolve from the store — the same rule spec 206 pinned for evidence),
/// renders the day view and writes one markdown file. It makes no model call, changes no score, label,
/// snapshot or rank, and its failure never affects the run.
/// </summary>
public interface IDailyNewsReportStep
{
    Task RunAsync(Guid? runId, NewsJudgmentRunResult? judgment, CancellationToken ct);
}

/// <inheritdoc cref="IDailyNewsReportStep"/>
public sealed class DailyNewsReportStep : IDailyNewsReportStep
{
    private readonly ISignalRepository _signalRepository;
    private readonly IDailyNewsReportWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DailyNewsReportStep> _logger;

    public DailyNewsReportStep(
        ISignalRepository signalRepository,
        IDailyNewsReportWriter writer,
        TimeProvider timeProvider,
        ILogger<DailyNewsReportStep> logger)
    {
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _signalRepository = signalRepository;
        _writer = writer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(Guid? runId, NewsJudgmentRunResult? judgment, CancellationToken ct)
    {
        // No judgment pass, or materialization not attempted (null summary, never an all-zero one): there is
        // no day view to build. Deliberately a silent skip like the sibling steps — "not attempted" is the
        // judgment step's fact to report, not this step's to restate.
        if (judgment?.SignalMaterialization is not { } accounting)
        {
            return;
        }

        try
        {
            var report = await BuildAsync(runId, judgment, accounting, ct).ConfigureAwait(false);
            var markdown = DailyNewsReportRenderer.Render(report);
            var write = await _writer.WriteAsync(report.GeneratedAtUtc, markdown, ct).ConfigureAwait(false);
            if (write.Written)
            {
                _logger.LogInformation(
                    "Daily news view for run {RunId}: {Rows} judged directional row(s) written to {Path}.",
                    runId, report.Rows.Count, write.Path);
            }
            else
            {
                _logger.LogWarning(
                    "Daily news view for run {RunId} could NOT be durably written to {Path}; the run itself "
                        + "is unaffected and the underlying signals remain in the signal store.",
                    runId, write.Path);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Belt and braces, mirroring the judgment/shadow steps: a derived view's failure must never
            // abort the host loop or the run whose signals it summarizes.
            _logger.LogError(ex, "Daily news view failed unexpectedly; the Radar run itself is unaffected.");
        }
    }

    private async Task<DailyNewsReport> BuildAsync(
        Guid? runId,
        NewsJudgmentRunResult judgment,
        NewsJudgmentSignalMaterializationSummary accounting,
        CancellationToken ct)
    {
        // This run's judgment ids and per-company display names, from the judgment records themselves.
        var judgmentIds = new HashSet<Guid>(judgment.Judgments.Select(j => j.JudgmentId));
        var nameByCompany = new Dictionary<Guid, string>();
        foreach (var record in judgment.Judgments)
        {
            nameByCompany.TryAdd(record.CompanyId, record.CompanyName);
        }

        var rows = new List<DailyNewsReportRow>();
        // Deterministic company order (AD-3) so repository reads — and any failure — happen in a stable order.
        foreach (var companyId in nameByCompany.Keys.OrderBy(id => id))
        {
            ct.ThrowIfCancellationRequested();
            var signals = await _signalRepository.GetByCompanyAsync(companyId, ct).ConfigureAwait(false);
            foreach (var signal in signals)
            {
                if (!NewsDirectionalSignalMetadata.IsJudgmentDerived(signal))
                {
                    continue;
                }

                if (!EvidenceMetadata.TryRead(signal.MetadataJson, out var metadata, out _)
                    || !metadata.TryGetValue(NewsDirectionalSignalMetadata.JudgmentIdKey, out var judgmentIdText)
                    || !Guid.TryParse(judgmentIdText, out var judgmentId)
                    || !judgmentIds.Contains(judgmentId))
                {
                    // A judgment-derived signal from an EARLIER run. Valid history, but not this day's news.
                    continue;
                }

                metadata.TryGetValue(NewsDirectionalSignalMetadata.TrajectoryKey, out var trajectory);
                rows.Add(new DailyNewsReportRow(
                    SignalId: signal.Id,
                    EvidenceId: signal.EvidenceId,
                    JudgmentId: judgmentId,
                    CompanyId: companyId,
                    CompanyName: nameByCompany[companyId],
                    Direction: signal.Direction,
                    Strength: signal.Strength,
                    Confidence: signal.Confidence,
                    JudgedTrajectory: string.IsNullOrWhiteSpace(trajectory) ? null : trajectory,
                    Headline: FirstLine(signal.SupportingExcerpt)));
            }
        }

        // Signals the materializer claims exist for this pass that the durable read did not surface. Never
        // negative: extra resolved rows would mean double-counting upstream, which the per-judgment
        // deterministic signal id already prevents.
        var claimed = accounting.Materialized + accounting.AlreadyMaterialized;
        var notResolved = Math.Max(0, claimed - rows.Count);

        return new DailyNewsReport(
            RunId: runId,
            GeneratedAtUtc: _timeProvider.GetUtcNow(),
            Rows: rows,
            Accounting: accounting,
            MaterializedNotResolved: notResolved);
    }

    private static string FirstLine(string excerpt)
    {
        foreach (var line in excerpt.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return excerpt.Trim();
    }
}
