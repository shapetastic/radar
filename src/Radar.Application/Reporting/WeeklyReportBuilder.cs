namespace Radar.Application.Reporting;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Lifecycle;
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
/// <para>
/// Everything above is the PRIMARY strategy's series (spec 137) and stays that way. Spec 150 adds one
/// additional, purely-numeric <see cref="StrategyReportSection"/> per configured strategy when more than one
/// is configured — see <c>BuildStrategySectionsAsync</c> — so a multi-strategy run stops being a report
/// about one of them. With a single strategy nothing is built and the report is byte-identical to before.
/// </para>
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
    private readonly IScoringStrategyFactory _scoringStrategies;
    private readonly IScoreRepositoryFactory _scoreRepositoryFactory;
    private readonly IScoreSnapshotFileStoreFactory _scoreSnapshotFileStores;
    private readonly IOperatingCallSource _operatingCalls;
    private readonly IStrategyEvidenceFactsSource _evidenceFacts;
    private readonly WeeklyReportOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WeeklyReportBuilder> _logger;
    private readonly IWeeklyReportJudgmentRerenderer? _judgmentRerenderer;

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
        // Spec 150: both are REQUIRED, never optional-nullable. An optional dependency that is silently
        // null means a production wiring mistake produces no strategy sections while every test stays
        // green — the exact class of bug spec 146's review caught. Both are already registered in DI, so
        // AddSingleton<IWeeklyReportBuilder, WeeklyReportBuilder>() resolves them automatically.
        IScoringStrategyFactory scoringStrategies,
        IScoreRepositoryFactory scoreRepositoryFactory,
        // Spec 184: all three REQUIRED, never optional-nullable, for the spec-150 reason above — a silently
        // absent optional dependency would render no call layer while every test stays green. The file
        // stores are the read path for a non-primary LEAD's cross-run "previous snapshot"; the call and
        // facts sources have inert Application defaults (NullOperatingCallSource /
        // UnavailableStrategyEvidenceFactsSource) registered by the library, so every existing DI
        // composition still resolves.
        IScoreSnapshotFileStoreFactory scoreSnapshotFileStores,
        IOperatingCallSource operatingCalls,
        IStrategyEvidenceFactsSource evidenceFacts,
        WeeklyReportOptions options,
        TimeProvider timeProvider,
        ILogger<WeeklyReportBuilder> logger,
        // Spec 185: OPTIONAL by design, unlike the required dependencies above — the seam is registered
        // ONLY when the judgment step is, and its PRESENCE is the signal that this run's first render must
        // carry the `? unassessed (judgment-pending)` markers (absent ⇒ the honest `no-judgment` default).
        // A null here is therefore a meaningful state, not a silent wiring hole: the rendered report states
        // it either way.
        IWeeklyReportJudgmentRerenderer? judgmentRerenderer = null)
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
        ArgumentNullException.ThrowIfNull(scoringStrategies);
        ArgumentNullException.ThrowIfNull(scoreRepositoryFactory);
        ArgumentNullException.ThrowIfNull(scoreSnapshotFileStores);
        ArgumentNullException.ThrowIfNull(operatingCalls);
        ArgumentNullException.ThrowIfNull(evidenceFacts);
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
        _scoringStrategies = scoringStrategies;
        _scoreRepositoryFactory = scoreRepositoryFactory;
        _scoreSnapshotFileStores = scoreSnapshotFileStores;
        _operatingCalls = operatingCalls;
        _evidenceFacts = evidenceFacts;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _judgmentRerenderer = judgmentRerenderer;
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

        // Spec 184: the operating-call layer + evidence statuses. Built ONLY with more than one configured
        // strategy — with a single strategy neither source is even consulted (structural inertness; the
        // single-strategy report stays byte-identical). The LEAD arm then governs the whole narrative walk
        // below: with declared calls and a non-primary lead, the walk reads the lead's own repository and
        // file-store series; under StopAll (declared, or the predeclared zero-Lead fallback) no arm has
        // earned the front page and NO narrative is built at all. Radar:PrimaryStrategy remains untouched:
        // it is storage/series identity only, and without declared calls it keeps the narrative by default
        // (stated explicitly in the rendered report, never silent).
        var lifecycle = _scoringStrategies.Runtimes.Count > 1
            ? await BuildLifecycleAsync(ct).ConfigureAwait(false)
            : null;

        var narrativeRepository = _scoreRepository;
        var narrativeFileStore = _scoreSnapshotFileStore;
        var buildNarrative = true;
        if (lifecycle is not null && lifecycle.Calls.HasDeclaredCalls)
        {
            if (lifecycle.Calls.StopAll)
            {
                buildNarrative = false;
            }
            else
            {
                var leadRuntime = _scoringStrategies.Runtimes.First(r => string.Equals(
                    r.Definition.Name, lifecycle.Calls.LeadStrategyName, StringComparison.OrdinalIgnoreCase));
                if (!leadRuntime.Definition.IsPrimary)
                {
                    // The SAME factories the scoring stage writes through — no second route to the lead's
                    // score files. When the lead IS the storage primary the injected pair above is used
                    // unchanged, so that path is byte-identical to pre-184.
                    narrativeRepository = _scoreRepositoryFactory.ForStrategy(leadRuntime.Definition);
                    narrativeFileStore = _scoreSnapshotFileStores.ForStrategy(leadRuntime.Definition);
                }
            }
        }

        IReadOnlyList<Company> narrativeCompanies = buildNarrative ? companies : [];
        var candidates = new List<CandidateEntry>();
        foreach (var company in narrativeCompanies)
        {
            ct.ThrowIfCancellationRequested();

            // Already ordered by CreatedAtUtc ascending (AD-3).
            var snapshots = await narrativeRepository
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
            var links = await narrativeRepository
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
            var previous = await narrativeFileStore
                .ReadLatestBeforeAsync(c.Current.CompanyId, c.Current.CreatedAtUtc, ct)
                .ConfigureAwait(false);

            // Two snapshots are comparable only when they belong to the SAME SCORE SERIES — i.e. the same
            // strategy (spec 141, ScoreSeriesKey). A strategy is immutable by convention (to change one you
            // add a new name, enforced at startup by StrategyIdentityGuard), so the name is a stable series
            // key that an unrelated collector toggle cannot move; the ScoringConfigVersion fingerprint used
            // to key this gate and moved 17 times over 851 live snapshots, rendering "(scoring updated)" for
            // changes that could not touch a score. A legacy null-named snapshot reads as the primary
            // "default" series, so pre-137 history keeps comparing rather than being orphaned.
            var comparable = previous is not null
                && ScoreSeriesKey.SameSeries(c.Current.StrategyName, previous.StrategyName);

            // The contributing signal set is built BEFORE Decide because the policy's corroboration
            // floor measures agreement across it (and the report's "why noticed" block reuses the very
            // same list — built once, never fetched twice).
            var signals = await BuildSignalRefsAsync(c.Current, links, ct).ConfigureAwait(false);

            var action = _policy.Decide(new ReportActionContext(
                c.Current,
                previous,
                PreviousComparable: comparable,
                ContributingSignals: signals,
                FollowingTier: c.Company.FollowingTier));
            var evidence = await BuildEvidenceRefsAsync(c.Current, links, ct).ConfigureAwait(false);
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
                PreviousScoringChanged: previous is not null && !comparable,
                FollowingTier: c.Company.FollowingTier));
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

        // Spec 150: one plain ranked table per configured strategy, appended after everything above. Built
        // from the SAME company list the primary walk used, so the two views cannot disagree about which
        // companies exist.
        var strategySections = await BuildStrategySectionsAsync(
            companies, periodStartUtc, periodEndUtc, ct).ConfigureAwait(false);

        var generatedAt = _timeProvider.GetUtcNow();
        var title = string.Format(
            CultureInfo.InvariantCulture,
            "Radar Weekly — {0:yyyy-MM-dd} to {1:yyyy-MM-dd}",
            periodStartUtc,
            periodEndUtc);

        // Spec 185 §4: the semantic-read marker source for the first render. The judgment step runs AFTER
        // the pipeline, so a registered rerenderer means "judgment-pending" now (re-rendered with the real
        // markers later); an absent one means the honest "no-judgment" default the renderer applies to a
        // null model. Only meaningful with strategy sections — a single-strategy report has no leaders
        // section and stays byte-identical.
        var judgmentMarkers = _judgmentRerenderer is not null && strategySections is { Count: > 0 }
            ? NewsJudgmentMarkerReportModel.Pending
            : null;

        var model = new WeeklyReportModel(
            Title: title,
            PeriodStartUtc: periodStartUtc,
            PeriodEndUtc: periodEndUtc,
            GeneratedAtUtc: generatedAt,
            Entries: entries,
            SignalsNeedingReview: needsReview,
            Collection: collection,
            RecentRuns: recentRuns,
            Health: health,
            Strategies: strategySections,
            Lifecycle: lifecycle,
            NewsJudgment: judgmentMarkers);

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

        // Spec 185 §5: hand the EXACT rendered model/report pair to the re-render seam, so the Worker's
        // post-judgment pass re-renders the same model with only the marker source changed and overwrites
        // the same report file through the same writer.
        if (judgmentMarkers is not null)
        {
            _judgmentRerenderer!.CaptureRendered(model, report);
        }

        _logger.LogInformation(
            "Generated weekly report {ReportId} with {ItemCount} item(s) for period {PeriodStart:yyyy-MM-dd}..{PeriodEnd:yyyy-MM-dd}.",
            report.Id,
            items.Count,
            periodStartUtc,
            periodEndUtc);

        // Spec 179 §2: return the EXACT section instances built (and rendered) above — the one structured
        // row source the news-risk shadow step may consume. Never rebuilt, never re-ranked.
        return new WeeklyReportResult(report, items, strategySections);
    }

    /// <summary>
    /// Builds the spec-184 lifecycle view: the per-strategy evidence statuses (computed from the persisted
    /// efficacy artifacts — descriptive, never a verdict) and the reduced operating calls. Called ONLY when
    /// more than one strategy is configured; with a single strategy neither source is consulted at all, so
    /// the call layer is structurally inert there (spec 184 §4).
    /// <para>
    /// No calls FILE is not a failure: it reduces to the explicit "no operating call is declared"
    /// resolution, which the renderer states plainly while prominence stays with the storage primary by
    /// default. An INVALID file, by contrast, throws — through the same <see cref="OperatingCallReducer"/>
    /// validation the Worker already ran at startup — naming the file and the violated rule.
    /// </para>
    /// </summary>
    private async Task<StrategyLifecycleReportModel> BuildLifecycleAsync(CancellationToken ct)
    {
        var definitions = _scoringStrategies.Runtimes.Select(r => r.Definition).ToList();

        var facts = await _evidenceFacts.ReadAsync(ct).ConfigureAwait(false);
        var statuses = StrategyEvidenceStatusCalculator.Compute(facts, definitions);
        var statusLines = definitions
            .Select(d => new StrategyLifecycleStatusLine(d.Name, statuses[d.Name]))
            .ToList();

        var file = await _operatingCalls.ReadAsync(ct).ConfigureAwait(false);
        var calls = file is null
            ? ResolvedOperatingCalls.None("no operating-calls file was found")
            : OperatingCallReducer.Reduce(
                file, definitions, StrategyEvidenceStatusCalculator.GateVerdicts(facts, definitions));

        return new StrategyLifecycleReportModel(calls, statusLines);
    }

    /// <summary>
    /// Builds one plain ranked table per configured scoring strategy (spec 150), primary first.
    /// <para>
    /// <b>Gated on more than one strategy.</b> With a single configured strategy — the synthesised
    /// <c>default</c>, i.e. every deployment that never set <c>Radar:Strategies</c> — this returns
    /// <c>null</c> and the report is byte-identical to the pre-150 output. Null rather than an empty list,
    /// consistently, so "no sections" has exactly one representation.
    /// </para>
    /// <para>
    /// <b>The <c>MaxItems</c> cap applies PER SECTION, independently.</b> Decided rather than inherited:
    /// each strategy is capped by the same <see cref="WeeklyReportOptions.MaxItems"/> the primary narrative
    /// uses, so one strategy can never crowd another out of the report, and every strategy is shown on the
    /// same terms. Because that cap can hide rows, the section carries both the number of companies with
    /// linked evidence and the number of rows actually kept, and the renderer states the truncation in the
    /// section header — silently shortening a table is the spec-125 failure that motivated raising the cap
    /// in the first place.
    /// </para>
    /// <para>
    /// Snapshots are read through <see cref="IScoreRepositoryFactory"/> — the SAME read path the scoring
    /// stage writes through — so this adds no second route to the per-strategy score files. Both the
    /// candidate rule (latest snapshot in <c>(periodStartUtc, periodEndUtc]</c>, a company with none simply
    /// omitted) and the ordering (Opportunity descending, then CompanyId ascending — deterministic, AD-3)
    /// are the primary walk's existing rules, reused verbatim.
    /// </para>
    /// <para>
    /// Deliberately NOT built here, and deliberately not built at all in this slice: cross-strategy
    /// composition of any kind (disagreement metrics, merged rankings, composite scores, "consensus"
    /// columns), per-strategy evidence blocks or "why noticed", per-strategy labels, and strategy-vs-price
    /// ranking (spec 140 already does that). Composition over a few days of accrued history would rank
    /// noise and invite trusting it.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<StrategyReportSection>?> BuildStrategySectionsAsync(
        IReadOnlyList<Company> companies,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken ct)
    {
        var runtimes = _scoringStrategies.Runtimes;
        if (runtimes.Count <= 1)
        {
            return null; // single strategy → byte-identical to the pre-150 report
        }

        // Primary first (a reader needs to know which series the narrative sections above describe), then
        // the remaining runtimes in their configured relative order. Runtimes is already in configured
        // order and the primary may be anywhere in it, so this is a stable partition, not a sort.
        var ordered = new List<ScoringStrategyRuntime>(runtimes.Count);
        ordered.AddRange(runtimes.Where(r => r.Definition.IsPrimary));
        ordered.AddRange(runtimes.Where(r => !r.Definition.IsPrimary));

        var sections = new List<StrategyReportSection>(ordered.Count);
        foreach (var runtime in ordered)
        {
            ct.ThrowIfCancellationRequested();

            var repository = _scoreRepositoryFactory.ForStrategy(runtime.Definition);

            var candidates = new List<CandidateEntry>();
            foreach (var company in companies)
            {
                ct.ThrowIfCancellationRequested();

                // Already ordered by CreatedAtUtc ascending (AD-3) → the last in-period match is the latest.
                var snapshots = await repository
                    .GetSnapshotsForCompanyAsync(company.Id, ct)
                    .ConfigureAwait(false);

                CompanyScoreSnapshot? current = null;
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.CreatedAtUtc > periodStartUtc && snapshot.CreatedAtUtc <= periodEndUtc)
                    {
                        current = snapshot;
                    }
                }

                if (current is null)
                {
                    continue; // this strategy did not score this company in-period → omitted, never invented
                }

                candidates.Add(new CandidateEntry(company, current));
            }

            var ranked = candidates
                .OrderByDescending(c => c.Current.OpportunityScore)
                .ThenBy(c => c.Company.Id)
                .ToList();

            // Links are fetched for EVERY candidate rather than only up to the cap (as the primary walk
            // does), because "how many companies had linked evidence" is a number this section renders: it
            // is what makes the spec-53 exclusion visible instead of silent. The cost is one link lookup per
            // in-period company per non-primary strategy.
            var withLinks = new List<CandidateEntry>(ranked.Count);
            foreach (var c in ranked)
            {
                ct.ThrowIfCancellationRequested();

                var links = await repository
                    .GetLinksForSnapshotAsync(c.Current.Id, ct)
                    .ConfigureAwait(false);

                if (links.Count == 0)
                {
                    // Spec 53, reused verbatim: a company scored from zero in-window signals is an absence
                    // of data, not an opportunity. An all-zero row in a rank table repeats exactly the
                    // mistake that rule fixed, so it is excluded here too — and counted, so it is not silent.
                    continue;
                }

                withLinks.Add(c);
            }

            var rows = new List<StrategyReportRow>(Math.Min(withLinks.Count, _options.MaxItems));
            foreach (var c in withLinks)
            {
                if (rows.Count >= _options.MaxItems)
                {
                    break;
                }

                rows.Add(new StrategyReportRow(
                    Rank: rows.Count + 1,
                    CompanyId: c.Current.CompanyId,
                    CompanyName: c.Company.Name,
                    Ticker: c.Company.Ticker,
                    ScoreSnapshotId: c.Current.Id,
                    Snapshot: c.Current));
            }

            // One engine IS one strategy (it resolves its effective config once in its constructor), so this
            // is a single authoritative fingerprint for the whole section — DISPLAYED, never computed here.
            var fingerprint = runtime.Engine.EffectiveConfig.Fingerprint;

            sections.Add(new StrategyReportSection(
                StrategyName: runtime.Definition.Name,
                FormulaVersion: runtime.Definition.Formula,
                ScoringConfigVersion: string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint,
                IsPrimary: runtime.Definition.IsPrimary,
                CompaniesScored: candidates.Count,
                CompaniesWithLinkedEvidence: withLinks.Count,
                Rows: rows)
            {
                // Spec 176: the declared reporting purpose, carried onto the section so the renderer can
                // group the live strategy leaders without inferring purpose from a name/formula/channel.
                Purpose = runtime.Definition.Purpose,
            });
        }

        return sections;
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
