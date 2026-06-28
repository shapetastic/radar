namespace Radar.Application.Reporting;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;

/// <summary>
/// Deterministic Stage 7 orchestration: gathers each company's most recent in-period
/// <see cref="CompanyScoreSnapshot"/>, labels it via <see cref="IReportActionPolicy"/>, resolves the
/// evidence behind that snapshot from stored <see cref="ScoreEvidenceLink"/>s, renders the markdown
/// via <see cref="IWeeklyReportRenderer"/>, and persists a <see cref="RadarReport"/> plus one
/// <see cref="RadarReportItem"/> per surfaced company. Contains no scoring math and no label
/// thresholds — labels come from the policy, layout from the renderer. Every item carries its
/// <see cref="RadarReportItem.ScoreSnapshotId"/> so a reported company is reproducible from stored
/// data: report → snapshot → signals/evidence.
/// </summary>
public sealed class WeeklyReportBuilder : IWeeklyReportBuilder
{
    private readonly ICompanyRepository _companyRepository;
    private readonly IScoreRepository _scoreRepository;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly ISignalRepository _signalRepository;
    private readonly IReportActionPolicy _policy;
    private readonly IWeeklyReportRenderer _renderer;
    private readonly IReportRepository _reportRepository;
    private readonly WeeklyReportOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WeeklyReportBuilder> _logger;

    public WeeklyReportBuilder(
        ICompanyRepository companyRepository,
        IScoreRepository scoreRepository,
        IEvidenceRepository evidenceRepository,
        ISignalRepository signalRepository,
        IReportActionPolicy policy,
        IWeeklyReportRenderer renderer,
        IReportRepository reportRepository,
        WeeklyReportOptions options,
        TimeProvider timeProvider,
        ILogger<WeeklyReportBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(scoreRepository);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(reportRepository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (options.Period <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "WeeklyReportOptions.Period must be a positive duration.", nameof(options));
        }

        if (options.MaxItems <= 0)
        {
            throw new ArgumentException(
                "WeeklyReportOptions.MaxItems must be greater than zero.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ReportType))
        {
            throw new ArgumentException(
                "WeeklyReportOptions.ReportType must be a non-empty label.", nameof(options));
        }

        _companyRepository = companyRepository;
        _scoreRepository = scoreRepository;
        _evidenceRepository = evidenceRepository;
        _signalRepository = signalRepository;
        _policy = policy;
        _renderer = renderer;
        _reportRepository = reportRepository;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<WeeklyReportResult> GenerateAsync(DateTimeOffset periodEndUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Enforce the pipeline's UTC-only convention: a non-zero offset would make the persisted
        // window metadata and all "Utc" timestamps inconsistent with the actual instant.
        if (periodEndUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "periodEndUtc must be UTC (zero offset).", nameof(periodEndUtc));
        }

        // Reporting window: (periodStartUtc, periodEndUtc] — exclusive start, inclusive end,
        // matching the scoring-window convention.
        var periodStartUtc = periodEndUtc - _options.Period;

        // IScoreRepository has no cross-company query, so we iterate companies and pull each
        // company's snapshots. A future GetSnapshotsBetween(periodStart, periodEnd) could fetch
        // the in-period snapshots in one query and avoid per-company round-trips; we deliberately
        // keep the repository surface untouched in this slice.
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);

        var interim = new List<InterimEntry>();
        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();

            // Already ordered by CreatedAtUtc ascending (AD-3).
            var snapshots = await _scoreRepository
                .GetSnapshotsForCompanyAsync(company.Id, ct)
                .ConfigureAwait(false);

            // Current = latest snapshot with CreatedAtUtc in (periodStartUtc, periodEndUtc].
            CompanyScoreSnapshot? current = null;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.CreatedAtUtc > periodStartUtc && snapshot.CreatedAtUtc <= periodEndUtc)
                {
                    current = snapshot; // snapshots ascending → last match is the latest in-period
                }
            }

            if (current is null)
            {
                continue; // nothing scored this period
            }

            // Previous = latest snapshot with CreatedAtUtc < current.CreatedAtUtc (any time), else null.
            CompanyScoreSnapshot? previous = null;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.CreatedAtUtc < current.CreatedAtUtc)
                {
                    previous = snapshot; // ascending → last such is the latest prior snapshot
                }
            }

            var action = _policy.Decide(new ReportActionContext(current, previous));

            // Evidence is resolved later, only for entries that survive ranking/capping, so we
            // avoid per-company score-link and evidence lookups that would be discarded by Take().
            interim.Add(new InterimEntry(company, current, action));
        }

        // Rank by OpportunityScore descending, then CompanyId ascending (deterministic, AD-3 spirit).
        var ranked = interim
            .OrderByDescending(e => e.Current.OpportunityScore)
            .ThenBy(e => e.Company.Id)
            .Take(_options.MaxItems)
            .ToList();

        var entries = new List<WeeklyReportEntry>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var e = ranked[i];
            var evidence = await BuildEvidenceRefsAsync(e.Current, ct).ConfigureAwait(false);
            entries.Add(new WeeklyReportEntry(
                CompanyId: e.Current.CompanyId,
                CompanyName: e.Company.Name,
                Ticker: e.Company.Ticker,
                ScoreSnapshotId: e.Current.Id,
                Snapshot: e.Current,
                Action: e.Action.Action,
                Rationale: e.Action.Rationale,
                Rank: i + 1,
                Evidence: evidence));
        }

        // Signals needing review observed in-period, surfaced for human attention.
        var observed = await _signalRepository
            .GetObservedBetweenAsync(periodStartUtc, periodEndUtc, ct)
            .ConfigureAwait(false);

        // GetObservedBetweenAsync is inclusive on its start bound, but the report window is
        // exclusive-start (periodStartUtc, periodEndUtc]; drop signals exactly at periodStartUtc
        // so this section matches the scoring-window convention used above.
        var needsReview = observed
            .Where(s => s.ObservedAtUtc > periodStartUtc)
            .Where(s => s.ReviewStatus is SignalReviewStatus.Pending or SignalReviewStatus.NeedsHumanReview)
            .OrderBy(s => s.Id)
            .Take(_options.MaxItems)
            .Select(s => new NeedsReviewSignalRef(
                SignalId: s.Id,
                EvidenceId: s.EvidenceId,
                CompanyMention: s.CompanyMention,
                Summary: s.Reason))
            .ToList();

        var generatedAt = _timeProvider.GetUtcNow();
        var title = string.Format(
            CultureInfo.InvariantCulture,
            "Radar Weekly — {0:yyyy-MM-dd} to {1:yyyy-MM-dd}",
            periodStartUtc,
            periodEndUtc);

        var model = new WeeklyReportModel(
            Title: title,
            PeriodStartUtc: periodStartUtc,
            PeriodEndUtc: periodEndUtc,
            GeneratedAtUtc: generatedAt,
            Entries: entries,
            SignalsNeedingReview: needsReview);

        var markdown = _renderer.Render(model);

        var report = new RadarReport(
            Id: Guid.NewGuid(),
            ReportType: _options.ReportType,
            Title: title,
            PeriodStartUtc: periodStartUtc,
            PeriodEndUtc: periodEndUtc,
            MarkdownContent: markdown,
            CreatedAtUtc: generatedAt);

        var items = entries
            .Select(entry => new RadarReportItem(
                Id: Guid.NewGuid(),
                ReportId: report.Id,
                CompanyId: entry.CompanyId,
                ScoreSnapshotId: entry.ScoreSnapshotId,
                SuggestedAction: entry.Action,
                Summary: entry.Rationale,
                Rank: entry.Rank))
            .ToList();

        await _reportRepository.AddAsync(report, items, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Generated weekly report {ReportId} with {ItemCount} item(s) for period {PeriodStart:yyyy-MM-dd}..{PeriodEnd:yyyy-MM-dd}.",
            report.Id,
            items.Count,
            periodStartUtc,
            periodEndUtc);

        return new WeeklyReportResult(report, items);
    }

    private async Task<IReadOnlyList<ReportEvidenceRef>> BuildEvidenceRefsAsync(
        CompanyScoreSnapshot current, CancellationToken ct)
    {
        var links = await _scoreRepository
            .GetLinksForSnapshotAsync(current.Id, ct)
            .ConfigureAwait(false);

        // Order by ContributionWeight descending, then SignalId (deterministic).
        var ordered = links
            .OrderByDescending(l => l.ContributionWeight)
            .ThenBy(l => l.SignalId)
            .ToList();

        var refs = new List<ReportEvidenceRef>(ordered.Count);
        foreach (var link in ordered)
        {
            var evidence = await _evidenceRepository
                .GetByIdAsync(link.EvidenceId, ct)
                .ConfigureAwait(false);

            if (evidence is null)
            {
                // Never drop provenance silently: keep the link's reason but flag the missing evidence.
                _logger.LogWarning(
                    "Evidence {EvidenceId} referenced by score snapshot {SnapshotId} (signal {SignalId}) was not found; rendering placeholder.",
                    link.EvidenceId,
                    current.Id,
                    link.SignalId);

                refs.Add(new ReportEvidenceRef(
                    EvidenceId: link.EvidenceId,
                    SignalId: link.SignalId,
                    SourceName: "(unknown)",
                    SourceUrl: null,
                    Title: "(evidence unavailable)",
                    ContributionReason: link.ContributionReason));
                continue;
            }

            refs.Add(new ReportEvidenceRef(
                EvidenceId: evidence.Id,
                SignalId: link.SignalId,
                SourceName: evidence.SourceName,
                SourceUrl: evidence.SourceUrl,
                Title: evidence.Title,
                ContributionReason: link.ContributionReason));
        }

        return refs;
    }

    private sealed record InterimEntry(
        Company Company,
        CompanyScoreSnapshot Current,
        ReportActionResult Action);
}
