namespace Radar.Application.Reporting;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Pipeline;
using Radar.Application.Scoring;
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
    private readonly ISignalReviewRepository _signalReviewRepository;
    private readonly IReportActionPolicy _policy;
    private readonly IWeeklyReportRenderer _renderer;
    private readonly IReportRepository _reportRepository;
    private readonly IPipelineRunStore _runStore;
    private readonly IScoreSnapshotFileStore _scoreSnapshotFileStore;
    private readonly WeeklyReportOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WeeklyReportBuilder> _logger;

    public WeeklyReportBuilder(
        ICompanyRepository companyRepository,
        IScoreRepository scoreRepository,
        IEvidenceRepository evidenceRepository,
        ISignalRepository signalRepository,
        ISignalReviewRepository signalReviewRepository,
        IReportActionPolicy policy,
        IWeeklyReportRenderer renderer,
        IReportRepository reportRepository,
        IPipelineRunStore runStore,
        IScoreSnapshotFileStore scoreSnapshotFileStore,
        WeeklyReportOptions options,
        TimeProvider timeProvider,
        ILogger<WeeklyReportBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(scoreRepository);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalReviewRepository);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(reportRepository);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(scoreSnapshotFileStore);
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
        _signalReviewRepository = signalReviewRepository;
        _policy = policy;
        _renderer = renderer;
        _reportRepository = reportRepository;
        _runStore = runStore;
        _scoreSnapshotFileStore = scoreSnapshotFileStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<WeeklyReportResult> GenerateAsync(
        DateTimeOffset periodEndUtc,
        CollectionSummary collection,
        CollectionHealthReport? health,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(collection);
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

        var candidates = new List<CandidateEntry>();
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

            candidates.Add(new CandidateEntry(company, current));
        }

        // Rank by OpportunityScore descending, then CompanyId ascending (deterministic, AD-3 spirit).
        // Link fetching is deferred to the rank-ordered walk below so we only touch the repository for
        // companies that can actually surface, keeping link lookups close to MaxItems in the common
        // case instead of O(companyCount) per run.
        var ranked = candidates
            .OrderByDescending(c => c.Current.OpportunityScore)
            .ThenBy(c => c.Company.Id)
            .ToList();

        var entries = new List<WeeklyReportEntry>(Math.Min(ranked.Count, _options.MaxItems));
        foreach (var c in ranked)
        {
            if (entries.Count >= _options.MaxItems)
            {
                break; // cap reached → no need to fetch links for lower-ranked candidates
            }

            ct.ThrowIfCancellationRequested();

            // A company scored from zero in-window signals has no score-evidence links behind its
            // snapshot. That is an absence of data, not an opportunity, so it must not surface as an
            // all-zero "Highest opportunity" row (spec 53). Walking in rank order and skipping
            // zero-link snapshots yields the same surfaced set as filtering before the cap, while
            // bounding link fetches by the number of rendered rows in the common case. The fetched
            // links are reused by both ref builders, so survivors are never double-fetched.
            var links = await _scoreRepository
                .GetLinksForSnapshotAsync(c.Current.Id, ct)
                .ConfigureAwait(false);

            if (links.Count == 0)
            {
                continue; // no evidence behind the score → not surfaced
            }

            // Previous = latest PERSISTED snapshot strictly before current, read from the file store
            // (the in-memory repo only holds THIS run's snapshots, so it can never see an earlier run's
            // snapshot — the cross-run "vs last run" comparison the report needs). Deferred to here,
            // after the MaxItems cap and zero-link check, so only entries that actually surface pay the
            // disk read (mirroring the link-fetch deferral above) rather than every in-period company.
            // The store swallows per-file read failures and returns null, so a null previous simply
            // renders "(first snapshot)"; no builder-level try/catch is required.
            var previous = await _scoreSnapshotFileStore
                .ReadLatestBeforeAsync(c.Current.CompanyId, c.Current.CreatedAtUtc, ct)
                .ConfigureAwait(false);

            // Two snapshots are comparable only when they were produced by the SAME scoring generation.
            // A null stamp (old on-disk snapshot, or any pre-stamp snapshot) is never comparable.
            var comparable =
                previous is not null
                && !string.IsNullOrEmpty(c.Current.ScoringConfigVersion)
                && string.Equals(
                    c.Current.ScoringConfigVersion, previous.ScoringConfigVersion, StringComparison.Ordinal);

            var action = _policy.Decide(new ReportActionContext(c.Current, previous, PreviousComparable: comparable));
            var evidence = await BuildEvidenceRefsAsync(c.Current, links, ct).ConfigureAwait(false);
            var signals = await BuildSignalRefsAsync(c.Current, links, ct).ConfigureAwait(false);
            entries.Add(new WeeklyReportEntry(
                CompanyId: c.Current.CompanyId,
                CompanyName: c.Company.Name,
                Ticker: c.Company.Ticker,
                ScoreSnapshotId: c.Current.Id,
                Snapshot: c.Current,
                Action: action.Action,
                Rationale: action.Rationale,
                Rank: entries.Count + 1,
                Evidence: evidence,
                Signals: signals,
                PreviousOpportunityScore: comparable ? previous!.OpportunityScore : (int?)null,
                PreviousTrajectoryScore: comparable ? previous!.TrajectoryScore : (int?)null,
                PreviousScoringChanged: previous is not null && !comparable));
        }

        // Signals needing review observed in-period, surfaced for human attention.
        var observed = await _signalRepository
            .GetObservedBetweenAsync(periodStartUtc, periodEndUtc, ct)
            .ConfigureAwait(false);

        // GetObservedBetweenAsync is inclusive on its start bound, but the report window is
        // exclusive-start (periodStartUtc, periodEndUtc]; drop signals exactly at periodStartUtc
        // so this section matches the scoring-window convention used above.
        var surfaced = observed
            .Where(s => s.ObservedAtUtc > periodStartUtc)
            .Where(s => s.ReviewStatus is SignalReviewStatus.Pending or SignalReviewStatus.NeedsHumanReview)
            // Most-recent-first so the cap never silently hides the newest needs-review signals;
            // Id is the deterministic tiebreaker (AD-3). Order before Take.
            .OrderByDescending(s => s.ObservedAtUtc)
            .ThenBy(s => s.Id)
            .Take(_options.MaxItems)
            .ToList();

        // Surface the latest persisted review reason per signal (provenance: report → review →
        // signal → evidence). The lookup is async, so iterate the already ordered+capped set
        // rather than projecting in LINQ — ordering, cap, and the surfaced set are unchanged.
        var needsReview = new List<NeedsReviewSignalRef>(surfaced.Count);
        foreach (var s in surfaced)
        {
            ct.ThrowIfCancellationRequested();

            // GetBySignalAsync is AD-3-ordered by ReviewedAtUtc then Id, so the last element is
            // the most recent review. No stored review → honest fallback rather than an invented
            // reason.
            var reviews = await _signalReviewRepository
                .GetBySignalAsync(s.Id, ct)
                .ConfigureAwait(false);

            string reviewReason;
            if (reviews.Count > 0)
            {
                var latest = reviews[^1];
                // Some reviewers (e.g. DeterministicSignalReviewer) already prefix the Summary with
                // the decision; don't double it up (e.g. "EscalateToHuman: EscalateToHuman: ...").
                var decisionPrefix = $"{latest.Decision}: ";
                reviewReason = latest.Summary.StartsWith(decisionPrefix, StringComparison.Ordinal)
                    ? latest.Summary
                    : decisionPrefix + latest.Summary;
            }
            else
            {
                reviewReason = "Pending review";
            }

            needsReview.Add(new NeedsReviewSignalRef(
                SignalId: s.Id,
                EvidenceId: s.EvidenceId,
                CompanyMention: s.CompanyMention,
                Summary: s.Reason,
                ReviewReason: reviewReason));
        }

        // Read the recent run history for the observational footer. This degrades to null (section
        // omitted) on any read failure — the report must never abort because the run log is
        // unreadable. Cancellation still propagates (the catch filter excludes it).
        IReadOnlyList<RecentRunSummary>? recentRuns = null;
        try
        {
            var runs = await _runStore
                .ReadRecentAsync(_options.RecentRunsInReport, ct)
                .ConfigureAwait(false);

            // The store returns records newest-first (AD-3). Note: the run currently being generated
            // is persisted by the runner AFTER this report is built (spec 59 writes at the end of
            // RunAsync), so this footer intentionally shows the PRIOR runs only.
            recentRuns = runs
                .Select(r => new RecentRunSummary(
                    r.CreatedAtUtc,
                    r.Collectors,
                    r.EvidenceNew,
                    r.SignalsApproved,
                    r.CompaniesScored,
                    r.SourcesChecked,
                    r.SourcesFailed))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read recent run history for the weekly report footer; omitting the section.");
        }

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
            SignalsNeedingReview: needsReview,
            Collection: collection,
            RecentRuns: recentRuns,
            Health: health);

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
        CompanyScoreSnapshot current, IReadOnlyList<ScoreEvidenceLink> links, CancellationToken ct)
    {
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

    private async Task<IReadOnlyList<ReportSignalRef>> BuildSignalRefsAsync(
        CompanyScoreSnapshot current, IReadOnlyList<ScoreEvidenceLink> links, CancellationToken ct)
    {
        // The same signal can back multiple evidence links; collapse to distinct contributing
        // signals so the "why noticed" block lists each signal once.
        var distinctSignalIds = links
            .Select(l => l.SignalId)
            .Distinct()
            .ToList();

        var refs = new List<ReportSignalRef>(distinctSignalIds.Count);
        foreach (var signalId in distinctSignalIds)
        {
            var signal = await _signalRepository
                .GetByIdAsync(signalId, ct)
                .ConfigureAwait(false);

            if (signal is null)
            {
                // Never drop provenance silently: the signal id is cited by the score snapshot but
                // could not be loaded; warn and skip (the evidence-link block still carries the id).
                _logger.LogWarning(
                    "Signal {SignalId} referenced by score snapshot {SnapshotId} was not found; skipping its 'why noticed' line.",
                    signalId,
                    current.Id);
                continue;
            }

            refs.Add(new ReportSignalRef(signal.Id, signal.Type, signal.Direction, signal.Reason));
        }

        // Deterministic order: by Type (enum order), then Direction, then SignalId (AD-3 spirit).
        return refs
            .OrderBy(r => r.Type)
            .ThenBy(r => r.Direction)
            .ThenBy(r => r.SignalId)
            .ToList();
    }

    private sealed record CandidateEntry(
        Company Company,
        CompanyScoreSnapshot Current);
}
