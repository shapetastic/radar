using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Pipeline;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Scoring;
using Radar.Domain.Signals;

namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// AD-16's precommitted attention-arrival screen, made executable (spec 169).
/// <para>
/// <b>This is an evaluator, not a strategy and not a place to retune the thesis.</b> Every value it screens on
/// — the primary arm, the outcome, the 21-day horizon, both comparators, the cohort exclusion, the minimum of
/// 20 companies and 20 dates, and the median-δ failure rule — is fixed by AD-16 and lives in
/// <see cref="AttentionArrivalScreen"/> as a code constant. This class arranges those decisions; it does not
/// make them. It may expose missing data. It must never repair a result by changing what is measured.
/// </para>
/// <para>
/// <b>Read-only.</b> It reads companies, run records, persisted snapshots, durable signals and evidence, and
/// creates, amends or deletes nothing. No scoring input, formula version, rule-set version or fingerprint is
/// touched, and it promotes no strategy — a human decides (AD-9).
/// </para>
/// <para>
/// <b>Deterministic (AD-3).</b> No clock, no randomness, no sampling and no bootstrap. Companies are ordered
/// by id, dates by instant, diagnostics by configured strategy order, so two runs over unchanged stores
/// produce byte-identical artifacts.
/// </para>
/// </summary>
public sealed class AttentionArrivalScreenEvaluator
{
    private readonly ScoringStrategySet _strategies;
    private readonly IStrategyScoreSnapshotStoreSelector _stores;
    private readonly ICompanyRepository _companies;
    private readonly IPipelineRunStore _runs;
    private readonly IExcludedCohortStore _cohorts;
    private readonly EnabledCollectorVocabulary _vocabulary;
    private readonly AttentionPublisherCountBuilder _publisherCounts;
    private readonly AttentionCoverageEvaluator _coverage;
    private readonly AttentionArrivalOptions _options;

    public AttentionArrivalScreenEvaluator(
        ScoringStrategySet strategies,
        IStrategyScoreSnapshotStoreSelector stores,
        ICompanyRepository companies,
        IPipelineRunStore runs,
        IExcludedCohortStore cohorts,
        EnabledCollectorVocabulary vocabulary,
        AttentionPublisherCountBuilder publisherCounts,
        AttentionCoverageEvaluator coverage,
        AttentionArrivalOptions options)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(cohorts);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(publisherCounts);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(options);

        _strategies = strategies;
        _stores = stores;
        _companies = companies;
        _runs = runs;
        _cohorts = cohorts;
        _vocabulary = vocabulary;
        _publisherCounts = publisherCounts;
        _coverage = coverage;
        _options = options;
    }

    public async Task<AttentionArrivalScreenResult> EvaluateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // --- Prerequisite 1: the binding exclusion cohort ------------------------------------------------
        // AD-16's 2026-07-31 amendment makes the exclusion binding. Silently including all companies because
        // a file was missing would produce a primary screen that violates an accepted amendment while looking
        // completely normal, so a load failure suppresses the status instead.
        var cohort = await _cohorts.LoadAsync(ct).ConfigureAwait(false);
        if (!cohort.IsAvailable)
        {
            return AttentionArrivalScreenResult.Unavailable(
                AttentionEvaluationUnavailableReason.CohortConfigurationUnavailable,
                cohort.UnavailableDetail ?? "The excluded-cohort configuration could not be loaded.",
                _options.AttentionCollector);
        }

        // --- Prerequisite 2: only ONE attention producer, and it is the one that proves its coverage ------
        var unsupported = _vocabulary.CollectorNames
            .Where(n => _options.ThirdPartyAttentionCollectors.Contains(n, StringComparer.Ordinal))
            .Where(n => !string.Equals(n, _options.AttentionCollector, StringComparison.Ordinal))
            .ToList();
        if (unsupported.Count > 0)
        {
            return AttentionArrivalScreenResult.Unavailable(
                AttentionEvaluationUnavailableReason.UnsupportedAttentionCollector,
                $"Collector(s) {string.Join(", ", unsupported)} can emit third-party MediaAttention but supply "
                    + "no per-company collection-coverage contract, so an outcome built over their signals "
                    + "cannot be proved complete. Disable them, or extend the coverage contract to them.",
                _options.AttentionCollector);
        }

        // --- Prerequisite 3: the primary arm exists ------------------------------------------------------
        var primaryStrategy = FindStrategy(AttentionArrivalScreen.PrimaryStrategyName);
        if (primaryStrategy is null)
        {
            return AttentionArrivalScreenResult.Unavailable(
                AttentionEvaluationUnavailableReason.PrimaryStrategyNotConfigured,
                $"AD-16 §7's primary arm '{AttentionArrivalScreen.PrimaryStrategyName}' is not among the "
                    + "configured strategies, so there is nothing to screen.",
                _options.AttentionCollector);
        }

        var universe = await _companies.GetAllAsync(ct).ConfigureAwait(false);
        var feeds = await _companies.GetSourceFeedsAsync(ct).ConfigureAwait(false);

        // --- Prerequisite 4: the cohort declaration agrees with the watch universe -----------------------
        var contradiction = FindCohortContradiction(cohort.Members, universe, feeds);
        if (contradiction is not null)
        {
            return AttentionArrivalScreenResult.Unavailable(
                AttentionEvaluationUnavailableReason.CohortConfigurationUnavailable,
                contradiction,
                _options.AttentionCollector);
        }

        // --- Snapshot series -----------------------------------------------------------------------------
        // Every arm's persisted series is read ONCE, through the existing selector seam (spec 140) — the very
        // stores the scoring stage wrote through, never a second route to the score files.
        var orderedUniverse = universe.OrderBy(c => c.Id).ToList();
        var series = await LoadSeriesAsync(orderedUniverse, ct).ConfigureAwait(false);
        var primarySeries = series[AttentionArrivalScreen.PrimaryStrategyName];

        var allPrimarySnapshots = primarySeries.Values.SelectMany(s => s).ToList();
        if (allPrimarySnapshots.Count == 0)
        {
            // Nothing has accrued yet. That is Pending — expected accrual, not a defect and not a failure.
            return Compose(
                new AttentionArrivalSection(
                    AttentionArrivalSections.Primary, true, 0, 0, false, 0.0, []),
                AttentionArrivalScreenResult.EmptySection(AttentionArrivalSections.Exploratory, false));
        }

        // --- Run history ---------------------------------------------------------------------------------
        // Time-bounded (never "the newest N"): the coverage chain must be able to tell "no run happened" from
        // "the read truncated before reaching it". The span covers every candidate's comparator and outcome
        // window plus the checkpoint tolerance at both ends.
        var horizon = AttentionArrivalScreen.Horizon;
        var gap = AttentionArrivalScreen.MaximumCheckpointGap;
        var runRecords = await _runs
            .ReadBetweenAsync(
                allPrimarySnapshots.Min(s => s.WindowEndUtc) - horizon - gap,
                allPrimarySnapshots.Max(s => s.WindowEndUtc) + horizon + gap,
                ct)
            .ConfigureAwait(false);

        // --- Candidate as-of instants --------------------------------------------------------------------
        var candidates = SelectCandidates(allPrimarySnapshots, runRecords);

        // --- Company sets: the cohort is removed BEFORE anything is counted ------------------------------
        var cohortCompanyIds = ResolveCohortCompanyIds(cohort.Members, orderedUniverse);
        var primaryCompanies = orderedUniverse.Where(c => !cohortCompanyIds.Contains(c.Id)).ToList();
        var exploratoryCompanies = orderedUniverse.Where(c => cohortCompanyIds.Contains(c.Id)).ToList();

        // ONE durable signal read per company for the WHOLE evaluation. The store scans and de-duplicates its
        // entire index per call, and the screen asks about the same company across two windows per as-of date
        // and across every candidate date — so without this the cost grows with accrued history × candidate
        // dates. Memoizing cannot change the answer: nothing here writes, so the store is fixed for the
        // duration of one evaluation, and the memo lives only for that call (a process-lifetime cache WOULD
        // be wrong — the Worker runs on an interval and signals accrue between runs).
        var companySignals = new Dictionary<Guid, IReadOnlyList<Signal>>();

        var primarySection = await BuildSectionAsync(
                AttentionArrivalSections.Primary,
                isPrimary: true,
                candidates,
                primaryCompanies,
                // The cohort members themselves, so each date can RECORD their exclusion rather than have
                // them silently absent. Spec 150's precedent: an exclusion that is visible arithmetic
                // (considered − excluded = included) is auditable; a silent drop is not.
                cohortExcludedCompanies: exploratoryCompanies,
                series,
                runRecords,
                companySignals,
                ct)
            .ConfigureAwait(false);

        // The event-enriched cohort runs through the SAME builders, on a DISJOINT company set, into its own
        // section. It is never pooled with the primary, can never satisfy the primary minimum N, and cannot
        // change the primary status — the two sections are computed independently and only rendered beside
        // each other.
        var exploratorySection = await BuildSectionAsync(
                AttentionArrivalSections.Exploratory,
                isPrimary: false,
                candidates,
                exploratoryCompanies,
                // The exploratory section IS the cohort, so it excludes nobody for cohort membership.
                cohortExcludedCompanies: [],
                series,
                runRecords,
                companySignals,
                ct)
            .ConfigureAwait(false);

        return Compose(primarySection, exploratorySection);
    }

    private AttentionArrivalScreenResult Compose(
        AttentionArrivalSection primary, AttentionArrivalSection exploratory) =>
        new(
            Availability: AttentionEvaluationAvailability.Available,
            UnavailableReason: AttentionEvaluationUnavailableReason.None,
            UnavailableDetail: null,
            // AD-16 §7's failure screen, verbatim and in this order: fewer than 20 eligible dates is Pending;
            // otherwise a median δ <= 0 is a MISS and a median δ > 0 clears this NECESSARY screen only.
            ScreenStatus: primary.EligibleDates < AttentionArrivalScreen.MinimumEligibleDates
                ? AttentionScreenStatus.Pending
                : primary.IsMedianDeltaDefined && primary.MedianDelta > 0.0
                    ? AttentionScreenStatus.ClearsNecessaryScreen
                    : AttentionScreenStatus.Miss,
            FirstEligibleAsOfDateUtc: AttentionArrivalScreen.FirstEligibleAsOfDateUtc,
            HorizonDays: (int)AttentionArrivalScreen.Horizon.TotalDays,
            MinimumCompaniesPerDate: AttentionArrivalScreen.MinimumCompaniesPerDate,
            MinimumEligibleDates: AttentionArrivalScreen.MinimumEligibleDates,
            PrimaryStrategy: AttentionArrivalScreen.PrimaryStrategyName,
            ControlStrategy: AttentionArrivalScreen.ControlStrategyName,
            BaselineStrategies: AttentionArrivalScreen.BaselineStrategyNames,
            AttentionCollector: _options.AttentionCollector,
            Primary: primary,
            Exploratory: exploratory);

    // -----------------------------------------------------------------------------------------------------
    // Candidate selection
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The candidate as-of instants: those primary-arm <c>WindowEndUtc</c> values that EXACTLY equal the
    /// <c>CreatedAtUtc</c> of an unfiltered run that recorded a scoring of the primary arm. One candidate per
    /// UTC calendar date — the latest exact instant when a date holds more than one.
    /// <para>
    /// <b>T is the exact instant, not the date.</b> Its calendar date is only the report label; using the
    /// whole day for the metric windows would look ahead to articles published later on the scoring day.
    /// </para>
    /// <para>
    /// <b>The tie-break uses only run/snapshot provenance, never the outcome</b> — picking the instant that
    /// produced the better statistic would be selecting on the answer.
    /// </para>
    /// <para>
    /// <b>Why the anchor test is exactly "unfiltered + recorded a scoring of the primary arm", and must NOT
    /// later be tightened to "a run that also collected".</b> The obvious-looking extra condition
    /// <c>Collectors.Count &gt; 0</c> would BREAK the spec-144 split deployment, where collection runs on its
    /// own schedule and the standalone <c>score</c> passes are the only runs that produce snapshots at all —
    /// under that wiring the tightened rule would find zero candidates forever, silently. It is also
    /// unnecessary, because nothing here is load-bearing for coverage:
    /// </para>
    /// <list type="number">
    /// <item><see cref="AttentionCoverageEvaluator"/> independently refuses a score-only run as a checkpoint
    /// (<see cref="AttentionCheckpointDisqualification.ScoreOnlyRunWithoutCollection"/>), so a
    /// score-anchored <c>T</c> still requires a real COLLECT checkpoint within 36 hours on <b>both</b> sides
    /// before a single company can enter the date.</item>
    /// <item>Spec 144 refuses a past-dated standalone <c>score</c> pass outright, so a score anchor cannot
    /// back-date <c>T</c> into a window whose coverage was already settled.</item>
    /// <item>The spec-161 company filter is collect-only by guard, so a filtered pass never scores — the
    /// <c>CompanyFilter is null</c> test here is belt-and-braces against a partial pass, not the only
    /// defence.</item>
    /// </list>
    /// <para>
    /// So this test decides only WHICH instants are candidate as-of points; whether the data behind one is
    /// trustworthy is decided, separately and strictly, by the coverage chain.
    /// </para>
    /// </summary>
    private static IReadOnlyList<(DateOnly Date, DateTimeOffset Instant)> SelectCandidates(
        IReadOnlyList<CompanyScoreSnapshot> primarySnapshots,
        IReadOnlyList<PipelineRunRecord> runRecords)
    {
        var anchors = runRecords
            .Where(r => r.CompanyFilter is null)
            .Where(r => r.Strategies is not null
                && r.Strategies.Contains(
                    AttentionArrivalScreen.PrimaryStrategyName, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.CreatedAtUtc)
            .ToHashSet();

        return primarySnapshots
            .Select(s => s.WindowEndUtc)
            .Where(anchors.Contains)
            .Distinct()
            .GroupBy(instant => DateOnly.FromDateTime(instant.UtcDateTime))
            .Select(g => (Date: g.Key, Instant: g.Max()))
            .OrderBy(c => c.Instant)
            .ToList();
    }

    // -----------------------------------------------------------------------------------------------------
    // Section construction
    // -----------------------------------------------------------------------------------------------------

    private async Task<AttentionArrivalSection> BuildSectionAsync(
        string label,
        bool isPrimary,
        IReadOnlyList<(DateOnly Date, DateTimeOffset Instant)> candidates,
        IReadOnlyList<Company> companies,
        IReadOnlyList<Company> cohortExcludedCompanies,
        IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>>> series,
        IReadOnlyList<PipelineRunRecord> runRecords,
        Dictionary<Guid, IReadOnlyList<Signal>> companySignals,
        CancellationToken ct)
    {
        var rows = new List<AttentionArrivalDateRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await BuildDateRowAsync(
                    candidate.Date,
                    candidate.Instant,
                    companies,
                    cohortExcludedCompanies,
                    series,
                    runRecords,
                    companySignals,
                    ct)
                .ConfigureAwait(false));
        }

        var eligible = rows.Where(r => r.IsEligible && r.IsDeltaDefined).ToList();
        var median = Median([.. eligible.Select(r => r.Delta)]);

        return new AttentionArrivalSection(
            Label: label,
            IsPrimary: isPrimary,
            CandidateDates: rows.Count,
            EligibleDates: eligible.Count,
            IsMedianDeltaDefined: median is not null,
            MedianDelta: median ?? 0.0,
            Dates: rows);
    }

    private async Task<AttentionArrivalDateRow> BuildDateRowAsync(
        DateOnly date,
        DateTimeOffset asOf,
        IReadOnlyList<Company> companies,
        IReadOnlyList<Company> cohortExcludedCompanies,
        IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>>> series,
        IReadOnlyList<PipelineRunRecord> runRecords,
        Dictionary<Guid, IReadOnlyList<Signal>> companySignals,
        CancellationToken ct)
    {
        var horizon = AttentionArrivalScreen.Horizon;
        var primarySeries = series[AttentionArrivalScreen.PrimaryStrategyName];

        var observations = new List<AttentionCompanyObservation>();

        // The cohort exclusion is recorded FIRST and unconditionally — it is a property of the company set,
        // not of the date, and it applies before the minimum N is counted (AD-16, 2026-07-31). Recording it
        // as a real exclusion row is what makes `CompaniesConsidered − CompaniesInExcludedCohort =
        // CompaniesIncluded` visible arithmetic rather than a shortfall the reader has to infer.
        var exclusions = cohortExcludedCompanies
            .Select(c => Exclude(c, AttentionCompanyExclusionReason.EventEnrichedCohort))
            .ToList();

        // Before the boundary the arms may accrue, but transitional snapshots do not enter the primary screen
        // (AD-16 §4). Recorded as its own counted reason rather than silently dropped, so the artifact shows
        // how much history is deliberately not being screened.
        var beforeBoundary = date < AttentionArrivalScreen.FirstEligibleAsOfDateUtc;

        if (!beforeBoundary)
        {
            foreach (var company in companies)
            {
                ct.ThrowIfCancellationRequested();

                var snapshot = SnapshotAt(primarySeries, company.Id, asOf);
                if (snapshot is null)
                {
                    exclusions.Add(Exclude(company, AttentionCompanyExclusionReason.NoPrimarySnapshot));
                    continue;
                }

                // AD-16 §5: coverage is required across BOTH windows — the comparator is a publisher count on
                // the same construction as the outcome, so a gap corrupts it identically.
                var comparatorCoverage = _coverage.Evaluate(company.Id, asOf - horizon, asOf, runRecords);
                if (!comparatorCoverage.IsComplete)
                {
                    exclusions.Add(Exclude(
                        company,
                        AttentionCompanyExclusionReason.IncompleteAttentionCollection,
                        comparatorCoverage));
                    continue;
                }

                var outcomeCoverage = _coverage.Evaluate(company.Id, asOf, asOf + horizon, runRecords);
                if (!outcomeCoverage.IsComplete)
                {
                    exclusions.Add(Exclude(
                        company,
                        AttentionCompanyExclusionReason.IncompleteAttentionCollection,
                        outcomeCoverage));
                    continue;
                }

                if (!companySignals.TryGetValue(company.Id, out var signals))
                {
                    signals = await _publisherCounts
                        .ReadCompanySignalsAsync(company.Id, ct).ConfigureAwait(false);
                    companySignals[company.Id] = signals;
                }

                var comparator = await _publisherCounts
                    .BuildAsync(
                        signals, company.Id, asOf - horizon, asOf, AttentionWindow.Comparator, ct)
                    .ConfigureAwait(false);
                if (!comparator.IsDefined)
                {
                    exclusions.Add(Exclude(company, Map(comparator.Failure)));
                    continue;
                }

                var outcome = await _publisherCounts
                    .BuildAsync(signals, company.Id, asOf, asOf + horizon, AttentionWindow.Outcome, ct)
                    .ConfigureAwait(false);
                if (!outcome.IsDefined)
                {
                    exclusions.Add(Exclude(company, Map(outcome.Failure)));
                    continue;
                }

                observations.Add(new AttentionCompanyObservation(
                    CompanyId: company.Id,
                    Ticker: company.Ticker ?? string.Empty,
                    PrimaryOpportunityScore: snapshot.OpportunityScore,
                    AttentionScore: snapshot.AttentionScore,
                    ComparatorPublishers: comparator.Count,
                    OutcomePublishers: outcome.Count));
            }
        }

        var included = observations.Count;

        // The vectors, built ONCE over exactly the company set every statistic on this row uses. AD-16 §7
        // pairs the comparison on exactly the same eligible companies; sharing the vectors makes that a
        // property of the code rather than a rule somebody has to remember.
        var primaryScores = observations.Select(o => (double)o.PrimaryOpportunityScore).ToList();
        var attentionScores = observations.Select(o => (double)o.AttentionScore).ToList();
        var persistence = observations.Select(o => (double)o.ComparatorPublishers).ToList();
        var outcomes = observations.Select(o => (double)o.OutcomePublishers).ToList();

        var primaryRho = beforeBoundary || included < AttentionArrivalScreen.MinimumCompaniesPerDate
            ? AttentionDiagnostic.Undefined(AttentionArrivalScreen.PrimaryStrategyName, "InsufficientSupport")
            : Diagnose(AttentionArrivalScreen.PrimaryStrategyName, primaryScores, outcomes);
        var persistenceRho = beforeBoundary || included < AttentionArrivalScreen.MinimumCompaniesPerDate
            ? AttentionDiagnostic.Undefined(PersistenceComparatorName, "InsufficientSupport")
            : Diagnose(PersistenceComparatorName, persistence, outcomes);

        var exclusionReason = ClassifyDate(beforeBoundary, included, primaryRho, persistenceRho);
        var isEligible = exclusionReason == AttentionDateExclusionReason.None;

        var deltaDefined = isEligible && primaryRho.IsDefined && persistenceRho.IsDefined;

        // The SECONDARY comparator (AD-16 §6): reported, never screened on. A constant AttentionScore makes
        // only this diagnostic undefined — it can never exclude the date.
        var secondary = isEligible
            ? Diagnose(SecondaryComparatorName, attentionScores, outcomes)
            : AttentionDiagnostic.Undefined(SecondaryComparatorName, "DateNotEligible");

        var (control, controlDelta, controlDeltaDefined) = isEligible
            ? BuildControl(series, observations, outcomes, asOf, primaryRho)
            : (AttentionDiagnostic.Undefined(AttentionArrivalScreen.ControlStrategyName, "DateNotEligible"),
                0.0, false);

        var baselines = AttentionArrivalScreen.BaselineStrategyNames
            .Select(name => isEligible
                ? BuildFixedArm(
                    name, series, observations, outcomes, asOf, IncompleteBaselineSupport)
                : AttentionDiagnostic.Undefined(name, "DateNotEligible"))
            .ToList();

        return new AttentionArrivalDateRow(
            AsOfDateUtc: date,
            AsOfInstantUtc: asOf,
            IsEligible: isEligible,
            ExclusionReason: exclusionReason,
            // Considered = the whole candidate set BEFORE the cohort exclusion, so the three numbers below
            // reconcile on the page: considered − inExcludedCohort − (other drops) = included.
            CompaniesConsidered: companies.Count + cohortExcludedCompanies.Count,
            CompaniesInExcludedCohort: cohortExcludedCompanies.Count,
            CompaniesIncluded: included,
            ExclusionCounts: CountExclusions(exclusions),
            PrimaryCorrelation: primaryRho,
            PersistenceCorrelation: persistenceRho,
            IsDeltaDefined: deltaDefined,
            Delta: deltaDefined ? primaryRho.Rho - persistenceRho.Rho : 0.0,
            SecondaryAttentionScoreCorrelation: secondary,
            ControlCorrelation: control,
            IsPrimaryMinusControlDefined: controlDeltaDefined,
            PrimaryMinusControl: controlDelta,
            BaselineCorrelations: baselines,
            Observations: observations,
            Exclusions: exclusions);
    }

    /// <summary>
    /// AD-16 §7's date-level rules, in the order they bind: the precommitted boundary, then the minimum of 20
    /// companies, then the degeneracy rule. A constant OUTCOME, PRIMARY predictor or PERSISTENCE predictor
    /// excludes the date under its own named reason; a constant secondary, control or baseline never can.
    /// </summary>
    private static AttentionDateExclusionReason ClassifyDate(
        bool beforeBoundary,
        int included,
        AttentionDiagnostic primaryRho,
        AttentionDiagnostic persistenceRho)
    {
        if (beforeBoundary)
        {
            return AttentionDateExclusionReason.BeforeFirstEligibleDate;
        }

        if (included < AttentionArrivalScreen.MinimumCompaniesPerDate)
        {
            return AttentionDateExclusionReason.InsufficientCompanies;
        }

        // The outcome is the SECOND vector in both correlations, so ConstantReturns from either names the
        // outcome. Checked first: a constant outcome makes both predictors unanswerable, and reporting it as
        // a predictor problem would point the operator at the wrong thing.
        if (primaryRho.UndefinedReason == nameof(RankCorrelationUndefinedReason.ConstantReturns)
            || persistenceRho.UndefinedReason == nameof(RankCorrelationUndefinedReason.ConstantReturns))
        {
            return AttentionDateExclusionReason.ConstantOutcome;
        }

        if (!primaryRho.IsDefined)
        {
            return AttentionDateExclusionReason.ConstantPrimaryPredictor;
        }

        if (!persistenceRho.IsDefined)
        {
            return AttentionDateExclusionReason.ConstantPersistencePredictor;
        }

        return AttentionDateExclusionReason.None;
    }

    /// <summary>
    /// The v10 formula control (AD-16 §7): computed ONLY when an exact-time control snapshot exists for EVERY
    /// company in the eligible set. Partial support is reported as <c>IncompleteControlSupport</c> rather than
    /// computed over a subset — a diagnostic measured on different companies than the primary is not a
    /// control.
    /// </summary>
    private (AttentionDiagnostic Control, double Delta, bool DeltaDefined) BuildControl(
        IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>>> series,
        IReadOnlyList<AttentionCompanyObservation> observations,
        IReadOnlyList<double> outcomes,
        DateTimeOffset asOf,
        AttentionDiagnostic primaryRho)
    {
        var control = BuildFixedArm(
            AttentionArrivalScreen.ControlStrategyName,
            series,
            observations,
            outcomes,
            asOf,
            IncompleteControlSupport);
        var defined = control.IsDefined && primaryRho.IsDefined;
        return (control, defined ? primaryRho.Rho - control.Rho : 0.0, defined);
    }

    /// <summary>
    /// One fixed arm's ρ against the outcome over the FULL primary company set, or its own support/degeneracy
    /// reason. These rows are retained for spec 155's later joint-support gate; they cannot alter AD-16's
    /// status here.
    /// <para>
    /// <paramref name="incompleteSupportReason"/> is supplied by the caller rather than fixed here because
    /// the token is what spec 155 will read: reporting a <c>baseline-*</c> arm's missing support as
    /// "IncompleteControlSupport" would name it as the v10 formula control, which it is not.
    /// </para>
    /// </summary>
    private AttentionDiagnostic BuildFixedArm(
        string strategyName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>>> series,
        IReadOnlyList<AttentionCompanyObservation> observations,
        IReadOnlyList<double> outcomes,
        DateTimeOffset asOf,
        string incompleteSupportReason)
    {
        if (!series.TryGetValue(strategyName, out var arm))
        {
            return AttentionDiagnostic.Undefined(strategyName, "StrategyNotConfigured");
        }

        var scores = new List<double>(observations.Count);
        foreach (var observation in observations)
        {
            var snapshot = SnapshotAt(arm, observation.CompanyId, asOf);
            if (snapshot is null)
            {
                // Partial support is reported, never computed over a subset: an arm measured on different
                // companies than the primary is not comparable with it.
                return AttentionDiagnostic.Undefined(strategyName, incompleteSupportReason);
            }

            scores.Add(snapshot.OpportunityScore);
        }

        return Diagnose(strategyName, scores, outcomes);
    }

    /// <summary>AD-16 §7's v10 formula control lacks an exact-time snapshot for at least one eligible company.</summary>
    internal const string IncompleteControlSupport = "IncompleteControlSupport";

    /// <summary>
    /// A configured <c>baseline-*</c> arm lacks an exact-time snapshot for at least one eligible company.
    /// Deliberately distinct from <see cref="IncompleteControlSupport"/>: spec 155's joint-support gate reads
    /// these tokens, and a baseline reported under the control's name would misidentify which arm was short.
    /// </summary>
    internal const string IncompleteBaselineSupport = "IncompleteBaselineSupport";

    private static AttentionDiagnostic Diagnose(
        string name, IReadOnlyList<double> predictor, IReadOnlyList<double> outcome)
    {
        // ONE Spearman implementation in the codebase (spec 140's RankCorrelation, extended in spec 169 with
        // the interval-free shape this screen needs — its windows overlap, so it makes no confidence claim and
        // a genuine |ρ| = 1 must stay usable rather than collapsing an interval nobody asked for).
        var rho = RankCorrelation.ComputeRho(predictor, outcome);
        return rho.IsDefined
            ? AttentionDiagnostic.Defined(name, rho.Rho)
            // Never NaN: an undefined coefficient is reported by NAME.
            : AttentionDiagnostic.Undefined(name, rho.Reason.ToString());
    }

    // -----------------------------------------------------------------------------------------------------
    // Reads and helpers
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Reads the five AD-16 arms' persisted series once each, through the existing selector seam.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>>>>
        LoadSeriesAsync(IReadOnlyList<Company> companies, CancellationToken ct)
    {
        var wanted = new List<string>
            { AttentionArrivalScreen.PrimaryStrategyName, AttentionArrivalScreen.ControlStrategyName };
        wanted.AddRange(AttentionArrivalScreen.BaselineStrategyNames);

        var series = new Dictionary<string, IReadOnlyDictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>>>(
            StringComparer.Ordinal);

        foreach (var name in wanted)
        {
            ct.ThrowIfCancellationRequested();

            var definition = FindStrategy(name);
            if (definition is null)
            {
                // An unconfigured arm is simply absent; every consumer reports it as StrategyNotConfigured
                // rather than as an empty (and therefore falsely "supported") series.
                continue;
            }

            var store = _stores.ForStrategy(definition);
            var byCompany = new Dictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>>();
            foreach (var company in companies)
            {
                byCompany[company.Id] = await store
                    .ReadAllForCompanyAsync(company.Id, ct).ConfigureAwait(false);
            }

            series[name] = byCompany;
        }

        return series;
    }

    private ScoringStrategyDefinition? FindStrategy(string name) =>
        _strategies.Strategies.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The snapshot whose <c>WindowEndUtc</c> is EXACTLY the as-of instant. Exact, never nearest: a nearby
    /// snapshot was computed over a different window and would silently mismatch the metric windows. A
    /// duplicate (two snapshots at the same instant) resolves to the latest-created then highest id, so the
    /// choice is deterministic (AD-3).
    /// </summary>
    private static CompanyScoreSnapshot? SnapshotAt(
        IReadOnlyDictionary<Guid, IReadOnlyList<CompanyScoreSnapshot>> series,
        Guid companyId,
        DateTimeOffset asOf) =>
        series.TryGetValue(companyId, out var snapshots)
            ? snapshots
                .Where(s => s.WindowEndUtc == asOf)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ThenByDescending(s => s.Id)
                .FirstOrDefault()
            : null;

    private static AttentionCompanyExclusion Exclude(
        Company company,
        AttentionCompanyExclusionReason reason,
        AttentionCoverageResult? coverage = null) =>
        new(
            CompanyId: company.Id,
            Ticker: company.Ticker ?? string.Empty,
            Reason: reason,
            CoverageReason: coverage?.Reason ?? AttentionCoverageReason.Complete,
            CoverageDetail: coverage?.Disqualification ?? AttentionCheckpointDisqualification.None);

    private static AttentionCompanyExclusionReason Map(AttentionPublisherCountFailure failure) => failure switch
    {
        AttentionPublisherCountFailure.UnresolvedComparatorEvidence =>
            AttentionCompanyExclusionReason.UnresolvedComparatorEvidence,
        AttentionPublisherCountFailure.UnresolvedOutcomeEvidence =>
            AttentionCompanyExclusionReason.UnresolvedOutcomeEvidence,
        AttentionPublisherCountFailure.MissingComparatorPublisher =>
            AttentionCompanyExclusionReason.MissingComparatorPublisher,
        AttentionPublisherCountFailure.MissingOutcomePublisher =>
            AttentionCompanyExclusionReason.MissingOutcomePublisher,
        AttentionPublisherCountFailure.UnresolvedComparatorProvenance =>
            AttentionCompanyExclusionReason.UnresolvedComparatorProvenance,
        AttentionPublisherCountFailure.UnresolvedOutcomeProvenance =>
            AttentionCompanyExclusionReason.UnresolvedOutcomeProvenance,
        _ => AttentionCompanyExclusionReason.None,
    };

    /// <summary>Exclusion counts in a FIXED enum order (never a dictionary's iteration order), zeros omitted.</summary>
    private static IReadOnlyList<AttentionExclusionCount> CountExclusions(
        IReadOnlyList<AttentionCompanyExclusion> exclusions) =>
    [
        .. Enum.GetValues<AttentionCompanyExclusionReason>()
            .Where(r => r != AttentionCompanyExclusionReason.None)
            .Select(r => new AttentionExclusionCount(r.ToString(), exclusions.Count(e => e.Reason == r)))
            .Where(c => c.Count > 0),
    ];

    /// <summary>
    /// The median of the eligible daily δ values. Over an EVEN count it is the mean of the two central values
    /// (stated because the convention matters to a threshold test at exactly 0), and it is deterministic: the
    /// input is sorted, and no tie-break can change the answer. An empty input has no median.
    /// </summary>
    internal static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// Resolves cohort tickers to watched company ids. A declared ticker Radar does not watch is simply not
    /// present — it excludes nothing, which is correct and is NOT a contradiction (a cohort may legitimately
    /// name a company before it is seeded).
    /// </summary>
    private static HashSet<Guid> ResolveCohortCompanyIds(
        IReadOnlyList<ExcludedCohortMember> members, IReadOnlyList<Company> universe)
    {
        var byTicker = universe
            .Where(c => !string.IsNullOrWhiteSpace(c.Ticker))
            .GroupBy(c => c.Ticker!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).First(), StringComparer.OrdinalIgnoreCase);

        var ids = new HashSet<Guid>();
        foreach (var member in members)
        {
            if (byTicker.TryGetValue(member.Ticker.Trim(), out var company))
            {
                ids.Add(company.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// The cohort-vs-universe contradiction check: a declared ticker that resolves to a seeded company whose
    /// CIK is KNOWN and DIFFERENT. Returns the detail message, or null when the declaration is consistent.
    /// A company whose CIK cannot be derived is "cannot verify", never a contradiction — failing the whole
    /// evaluation over a feed shape Radar merely does not recognise would be a false alarm.
    /// </summary>
    private static string? FindCohortContradiction(
        IReadOnlyList<ExcludedCohortMember> members,
        IReadOnlyList<Company> universe,
        IReadOnlyList<CompanySourceFeed> feeds)
    {
        var ciks = CompanyCikIndex.Build(feeds);
        var byTicker = universe
            .Where(c => !string.IsNullOrWhiteSpace(c.Ticker))
            .GroupBy(c => c.Ticker!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).First(), StringComparer.OrdinalIgnoreCase);

        foreach (var member in members.OrderBy(m => m.Cohort, StringComparer.Ordinal)
            .ThenBy(m => m.Ticker, StringComparer.Ordinal))
        {
            if (!byTicker.TryGetValue(member.Ticker.Trim(), out var company)
                || !ciks.TryGetValue(company.Id, out var seededCik))
            {
                continue;
            }

            var declared = CompanyCikIndex.Normalize(member.Cik);
            if (declared is not null && !string.Equals(declared, seededCik, StringComparison.Ordinal))
            {
                return $"Cohort '{member.Cohort}' declares ticker '{member.Ticker}' with CIK '{member.Cik}', "
                    + $"but the seeded company with that ticker has CIK '{seededCik}'. Excluding the wrong "
                    + "company — or failing to exclude the right one — would silently violate AD-16's "
                    + "2026-07-31 amendment, so the primary status is suppressed until the declaration and "
                    + "the watch universe agree.";
            }
        }

        return null;
    }

    /// <summary>AD-16 §6's PRIMARY comparator: the trailing distinct-publisher count, in the outcome's own units.</summary>
    private const string PersistenceComparatorName = "baseline-attention-persistence";

    /// <summary>AD-16 §6's SECONDARY comparator: the stored v11 <c>AttentionScore</c>. Reported, never screened on.</summary>
    private const string SecondaryComparatorName = "baseline-attention-score";
}
