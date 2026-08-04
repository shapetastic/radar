using System.Globalization;

using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The spec-155 paired, date-blocked, purged comparison: does the predeclared primary composite track the
/// shared outcome more closely than EVERY predeclared baseline, on identical companies, dates and outcomes?
/// <para>
/// <b>Why the marginal leaderboard cannot answer this.</b> Marginal ρs are computed over each strategy's own
/// support, which can differ (the 2026-07-28 backtest had one arm at 0 in-sample observations while another
/// had 182), and the spread between baselines is not an uncertainty estimate of any difference. So this
/// harness (1) intersects admitted observations across the primary AND every baseline into ONE joint support,
/// (2) forms one paired delta per date per baseline — companies are NEVER pooled across dates — and
/// (3) purges mechanically overlapping forward windows before any inference.
/// </para>
/// <para>
/// <b>The intersection is EXACT on the scoring instant (spec 170 §2).</b> Two arms' same-day observations
/// pair only when their <c>WindowEndUtc</c> instants are identical — after a partial rerun, two same-day
/// snapshots can represent DIFFERENT knowledge cutoffs, and pairing them by calendar date would attribute to
/// strategy difference what is actually a difference in what each arm could see. The calendar date
/// (<c>DateOnly.FromDateTime(instant.UtcDateTime)</c>) is still what groups blocks, drives the purge and is
/// compared against the boundary — the purge is a 21-day rule, not a sub-second one. An observation with no
/// recorded instant fails CLOSED out of the claim path (counted, never date-paired); a (company, date) key
/// whose arms share no common instant is counted as mismatched and not paired.
/// </para>
/// <para>
/// <b>Daily date blocks are not independent, and no code here claims they are.</b> Blocking by date removes
/// the contemporaneous cross-company nuisance only; the purge then removes the known forward-window overlap;
/// what remains is an interval CONDITIONAL on the predeclared model that purged blocks are independent draws
/// from a stable distribution — a limitation every renderer states beside the interval.
/// </para>
/// <para>
/// <b>The PRICE gate is computed HERE</b>, inside the harness, so a caller cannot re-derive a friendlier one:
/// <c>SatisfiesPriceGate</c> is true only when the primary was predeclared, the boundary was precommitted,
/// and every baseline's purged median delta is positive with an exact-interval lower bound strictly above
/// zero. It is deliberately NOT named "qualifies" — AD-15's gate is COMPOSITE (spec 170), and the AD-16
/// attention prerequisite is judged by <c>Ad15ClaimGate</c> outside this type, which never sees an Attention
/// type. Requiring the primary to clear every FIXED baseline is an intersection-union claim and needs no
/// Bonferroni correction; choosing the best of several composite arms after seeing results is a different
/// act, which is why the arms-considered count is a result field.
/// </para>
/// <para>
/// <b>Pure (AD-3).</b> No clock, no randomness, no I/O, no logging; same inputs yield a byte-identical
/// result. It consumes the SAME <see cref="StrategyObservationBuilder"/> admissions the marginal leaderboard
/// uses — one read, two projections — so the two artifacts can never disagree about which observations exist.
/// </para>
/// </summary>
public sealed class PairedComparisonHarness
{
    /// <summary>
    /// The spec-154 control convention: every baseline strategy is prefixed <c>baseline-</c> (ordinal), so
    /// the baseline family is identified by NAME, never by a special-cased type.
    /// </summary>
    public const string BaselineNamePrefix = "baseline-";

    public PairedStrategyComparison Compare(
        IReadOnlyList<StrategyScoreSeries> strategies,
        string primaryStrategyName,
        bool primaryWasPredeclared,
        PairedComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryStrategyName);
        ArgumentNullException.ThrowIfNull(options);

        var horizonDays = options.Comparison.ForwardHorizonDays;
        var exitToleranceDays = options.Comparison.ExitToleranceDays;

        var sets = new List<StrategyObservationSet>(strategies.Count);
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            sets.Add(StrategyObservationBuilder.Build(strategy, horizonDays, exitToleranceDays));
        }

        // Strategy names are unique case-insensitively (ScoringStrategySet's rule; ScoreSeriesKey's
        // comparison), so a single match is the invariant and two would be a defect worth throwing over.
        var primaryMatches = sets
            .Where(s => string.Equals(s.StrategyName, primaryStrategyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (primaryMatches.Count != 1)
        {
            throw new InvalidOperationException(
                $"The paired comparison's primary strategy '{primaryStrategyName}' matched "
                    + $"{primaryMatches.Count} of the compared strategies ("
                    + string.Join(", ", sets.Select(s => $"'{s.StrategyName}'"))
                    + "); the predeclared primary must name exactly one configured arm "
                    + "(Radar:Efficacy:Comparison:PairedPrimaryStrategy).");
        }

        var primary = primaryMatches[0];
        if (primary.StrategyName.StartsWith(BaselineNamePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The paired comparison's primary strategy '{primary.StrategyName}' is itself a baseline "
                    + $"(the '{BaselineNamePrefix}' prefix is the spec-154 control convention); a control "
                    + "exists to be compared against and cannot be the predeclared primary composite.");
        }

        // Baseline family in ordinal name order: deterministic regardless of configuration order, matching
        // the leaderboard's name-ordered drop list.
        var baselines = sets
            .Where(s => s.StrategyName.StartsWith(BaselineNamePrefix, StringComparison.Ordinal))
            .OrderBy(s => s.StrategyName, StringComparer.Ordinal)
            .ToList();
        var baselineNames = baselines.Select(b => b.StrategyName).ToList();

        var marginalSupports = sets
            .Select(s => new StrategyMarginalSupport(
                s.StrategyName, SupportOf(s.Usable), s.WithoutForwardPrice, s.PartialWindow))
            .ToList();

        // The claim path's arms: the primary and every baseline. Only these can pair, so only their
        // instant-less observations are "excluded from the claim path" rather than merely unused.
        var claimArms = new List<StrategyObservationSet>(baselines.Count + 1) { primary };
        claimArms.AddRange(baselines);
        var observationsWithoutAsOfInstant = claimArms.Sum(s => s.WithoutAsOfInstant);
        var observationsWithMismatchedAsOfInstant = CountMismatchedInstantKeys(claimArms);

        var primaryByInstant = ByInstantKey(primary);
        var baselinesByInstant = baselines.Select(ByInstantKey).ToList();

        var pairwiseSupports = new List<PairwiseIntersectionSupport>(baselines.Count);
        for (var b = 0; b < baselines.Count; b++)
        {
            var map = baselinesByInstant[b];
            var intersection = CollapseToOnePerCompanyDate(
                primary.UsableByInstant
                    .Where(o => map.ContainsKey((o.CompanyId, o.AsOfInstantUtc)))
                    .ToList());
            pairwiseSupports.Add(new PairwiseIntersectionSupport(
                baselines[b].StrategyName, SupportOf(intersection)));
        }

        // The JOINT intersection: EXACT (company, instant) keys present for the primary and EVERY baseline,
        // with the shared outcome required to be THE SAME number in every arm. All arms read one price
        // store, so a difference is a data defect — the observation is dropped with its own counter rather
        // than one arm's value being silently preferred.
        var joint = new List<StrategyInstantObservation>();
        var inconsistentOutcomes = 0;
        if (baselines.Count > 0)
        {
            foreach (var observation in primary.UsableByInstant)
            {
                var key = (observation.CompanyId, observation.AsOfInstantUtc);
                var presentEverywhere = true;
                var outcomesAgree = true;
                foreach (var baselineMap in baselinesByInstant)
                {
                    if (!baselineMap.TryGetValue(key, out var baselineObservation))
                    {
                        presentEverywhere = false;
                        break;
                    }

                    if (baselineObservation.ForwardReturn != observation.ForwardReturn)
                    {
                        outcomesAgree = false;
                    }
                }

                if (!presentEverywhere)
                {
                    continue;
                }

                if (!outcomesAgree)
                {
                    inconsistentOutcomes++;
                    continue;
                }

                joint.Add(observation);
            }
        }

        // One observation per (company, calendar date) in the block cross-sections: when a company-day
        // paired at more than one instant (a full same-day rerun mirrored across every arm), the LATEST
        // instant wins — the deterministic analogue of the builder's last-occurrence-wins rule.
        var collapsedJoint = CollapseToOnePerCompanyDate(joint);

        var jointSupport = SupportOf(collapsedJoint);

        // The claim's own support: the joint intersection restricted to as-of dates at or after the
        // precommitted boundary. No boundary ⇒ EMPTY, never the all-history figure (spec 170 §3).
        var eligibleJointSupport = options.FirstEligibleAsOf is { } eligibleBoundary
            ? SupportOf(collapsedJoint.Where(o => o.AsOf >= eligibleBoundary).ToList())
            : PairedSupport.Empty;

        // Group the joint observations by as-of date, ascending, companies ordered by id — the deterministic
        // cross-section every per-date ρ is computed over.
        var byDate = collapsedJoint
            .GroupBy(o => o.AsOf)
            .OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, Observations: g.OrderBy(o => o.CompanyId).ToList()))
            .ToList();

        var candidateDates = new List<PairedCandidateDate>();
        var droppedDates = new List<PairedDroppedDate>();
        var survivingObservations = new Dictionary<DateOnly, List<StrategyInstantObservation>>();

        foreach (var (date, observations) in byDate)
        {
            if (observations.Count < options.MinimumCompaniesPerDate)
            {
                droppedDates.Add(new PairedDroppedDate(
                    date, PairedDateDropReason.TooFewCompanies, BaselineName: null));
                continue;
            }

            var outcome = observations.Select(o => o.ForwardReturn).ToList();
            var primaryScores = observations.Select(o => o.Score).ToList();

            var primaryRho = RankCorrelation.ComputeRho(primaryScores, outcome);
            if (!primaryRho.IsDefined)
            {
                // ComputeRho checks the score side first, so a date where BOTH vectors are constant reads as
                // ConstantPrimary — the spec's stated precedence.
                droppedDates.Add(new PairedDroppedDate(
                    date,
                    primaryRho.Reason == RankCorrelationUndefinedReason.ConstantReturns
                        ? PairedDateDropReason.ConstantOutcome
                        : PairedDateDropReason.ConstantPrimary,
                    BaselineName: null));
                continue;
            }

            var baselineRhos = new List<PairedBaselineRho>(baselines.Count);
            string? constantBaseline = null;
            foreach (var (baseline, baselineMap) in baselines.Zip(baselinesByInstant))
            {
                var baselineScores = observations
                    .Select(o => baselineMap[(o.CompanyId, o.AsOfInstantUtc)].Score)
                    .ToList();
                var baselineRho = RankCorrelation.ComputeRho(baselineScores, outcome);
                if (!baselineRho.IsDefined)
                {
                    // The outcome vector already proved non-constant against the primary, so the only
                    // reachable degeneracy here is a constant baseline. The date drops for the WHOLE family
                    // — every baseline's deltas must use the same dates — naming the first offender in the
                    // deterministic name order.
                    constantBaseline = baseline.StrategyName;
                    break;
                }

                baselineRhos.Add(new PairedBaselineRho(
                    baseline.StrategyName, baselineRho.Rho, primaryRho.Rho - baselineRho.Rho));
            }

            if (constantBaseline is not null)
            {
                droppedDates.Add(new PairedDroppedDate(
                    date, PairedDateDropReason.ConstantBaseline, constantBaseline));
                continue;
            }

            candidateDates.Add(new PairedCandidateDate(
                date, observations.Count, primaryRho.Rho, baselineRhos));
            survivingObservations[date] = observations;
        }

        // The claim path: only dates at or after the precommitted boundary enter the purge. With no boundary
        // there is no claim path at all — everything below is then computed over every surviving date and
        // labelled exploratory, which is honest BECAUSE the gate can no longer pass.
        var boundary = options.FirstEligibleAsOf;
        var eligible = boundary is { } b2
            ? candidateDates.Where(d => d.Date >= b2).ToList()
            : candidateDates;
        var developmentDateCount = boundary is { } b3
            ? candidateDates.Count(d => d.Date < b3)
            : 0;

        // Purge on NOMINAL intervals (d, d+h] via the shared outcome-agnostic helper.
        var purge = OutcomeWindowPurge.Purge(
            [.. eligible.Select(d => new OutcomeWindowBlock(
                d.Date, d.Date, d.Date.AddDays(horizonDays)))]);
        foreach (var skipped in purge.Skipped)
        {
            droppedDates.Add(new PairedDroppedDate(
                skipped.Date, PairedDateDropReason.OverlappingOutcomeWindow, BaselineName: null));
        }

        droppedDates.Sort(static (a, b) =>
        {
            var byDropDate = a.Date.CompareTo(b.Date);
            return byDropDate != 0 ? byDropDate : a.Reason.CompareTo(b.Reason);
        });

        var admittedBlocks = BuildAdmittedBlocks(purge.Admitted, survivingObservations);
        VerifyObservedIntervalsDoNotOverlap(admittedBlocks);

        var admittedDates = admittedBlocks.Select(a => a.Date).ToHashSet();
        var admittedCandidates = eligible.Where(d => admittedDates.Contains(d.Date)).ToList();

        var baselineResults = new List<BaselinePairedResult>(baselines.Count);
        foreach (var baselineName in baselineNames)
        {
            var deltas = admittedCandidates
                .Select(d => new PairedDelta(
                    d.Date,
                    d.Baselines.Single(x =>
                        string.Equals(x.BaselineName, baselineName, StringComparison.Ordinal)).Delta))
                .ToList();
            var deltaValues = deltas.Select(x => x.Delta).ToList();

            var interval = ExactMedianInterval.Compute(deltaValues);
            var signTest = ExactSignTest.Compute(deltaValues);
            double? median = deltaValues.Count > 0 ? ExactMedianInterval.MedianOf(deltaValues) : null;

            var clears = interval.IsDefined
                && median is { } m && m > 0.0
                && interval.Lower > 0.0;

            baselineResults.Add(new BaselinePairedResult(
                baselineName, deltas, median, interval, signTest, clears));
        }

        var priceGateReasons = BuildPriceGateReasons(
            primaryWasPredeclared, boundary, baselines.Count, jointSupport, eligible.Count, baselineResults);

        return new PairedStrategyComparison(
            PrimaryStrategyName: primary.StrategyName,
            PrimaryWasPredeclared: primaryWasPredeclared,
            FirstEligibleAsOf: boundary,
            ArmsConsidered: sets.Count,
            BaselineNames: baselineNames,
            MarginalSupports: marginalSupports,
            PairwiseSupports: pairwiseSupports,
            JointSupport: jointSupport,
            EligibleJointSupport: eligibleJointSupport,
            InconsistentOutcomeObservationsDropped: inconsistentOutcomes,
            ObservationsWithoutAsOfInstant: observationsWithoutAsOfInstant,
            ObservationsWithMismatchedAsOfInstant: observationsWithMismatchedAsOfInstant,
            CandidateDates: candidateDates,
            DroppedDates: droppedDates,
            DevelopmentDateCount: developmentDateCount,
            AdmittedBlocks: admittedBlocks,
            Baselines: baselineResults,
            SatisfiesPriceGate: priceGateReasons.Count == 0,
            PriceGateReasons: priceGateReasons,
            Options: options);
    }

    private static Dictionary<(Guid CompanyId, DateTimeOffset Instant), StrategyInstantObservation>
        ByInstantKey(StrategyObservationSet set)
    {
        var map = new Dictionary<(Guid, DateTimeOffset), StrategyInstantObservation>(
            set.UsableByInstant.Count);
        foreach (var observation in set.UsableByInstant)
        {
            // The builder already de-duped on this key; a duplicate here would be a builder defect.
            map.Add((observation.CompanyId, observation.AsOfInstantUtc), observation);
        }

        return map;
    }

    /// <summary>
    /// Collapses a set of exact-instant observations to ONE per (company, calendar date), keeping the
    /// LATEST instant — the deterministic analogue of the builder's last-occurrence-wins same-day rule —
    /// then restores the deterministic (date, company) order. Identity when every company-day carries one
    /// instant, which is every normal full run.
    /// </summary>
    private static List<StrategyInstantObservation> CollapseToOnePerCompanyDate(
        IReadOnlyList<StrategyInstantObservation> observations)
    {
        var byCompanyDate = new Dictionary<(Guid, DateOnly), StrategyInstantObservation>(observations.Count);
        foreach (var observation in observations)
        {
            var key = (observation.CompanyId, observation.AsOf);
            if (!byCompanyDate.TryGetValue(key, out var existing)
                || observation.AsOfInstantUtc > existing.AsOfInstantUtc)
            {
                byCompanyDate[key] = observation;
            }
        }

        var collapsed = byCompanyDate.Values.ToList();
        collapsed.Sort(static (a, b) =>
        {
            var byDate = a.AsOf.CompareTo(b.AsOf);
            return byDate != 0 ? byDate : a.CompanyId.CompareTo(b.CompanyId);
        });

        return collapsed;
    }

    /// <summary>
    /// Counts (company, calendar-date) keys present in TWO OR MORE claim-path arms with NO instant common to
    /// every arm carrying the key — the keys that could not pair BECAUSE their instants differed (spec 170
    /// §2.3). A key one arm carries alone is not mismatched (it is merely not everywhere), and a key whose
    /// carriers share at least one instant paired at that instant.
    /// </summary>
    private static int CountMismatchedInstantKeys(IReadOnlyList<StrategyObservationSet> arms)
    {
        var perKey = new Dictionary<(Guid, DateOnly), List<HashSet<DateTimeOffset>>>();
        foreach (var arm in arms)
        {
            var armInstants = new Dictionary<(Guid, DateOnly), HashSet<DateTimeOffset>>();
            foreach (var observation in arm.UsableByInstant)
            {
                var key = (observation.CompanyId, observation.AsOf);
                if (!armInstants.TryGetValue(key, out var instants))
                {
                    instants = [];
                    armInstants[key] = instants;
                }

                instants.Add(observation.AsOfInstantUtc);
            }

            foreach (var (key, instants) in armInstants)
            {
                if (!perKey.TryGetValue(key, out var list))
                {
                    list = [];
                    perKey[key] = list;
                }

                list.Add(instants);
            }
        }

        var mismatched = 0;
        foreach (var armSets in perKey.Values)
        {
            if (armSets.Count < 2)
            {
                continue;
            }

            var common = new HashSet<DateTimeOffset>(armSets[0]);
            for (var i = 1; i < armSets.Count && common.Count > 0; i++)
            {
                common.IntersectWith(armSets[i]);
            }

            if (common.Count == 0)
            {
                mismatched++;
            }
        }

        return mismatched;
    }

    private static PairedSupport SupportOf(IReadOnlyList<StrategyObservation> observations)
    {
        var companies = new HashSet<Guid>();
        var dates = new HashSet<DateOnly>();
        foreach (var observation in observations)
        {
            companies.Add(observation.CompanyId);
            dates.Add(observation.AsOf);
        }

        return new PairedSupport(observations.Count, companies.Count, dates.Count);
    }

    private static PairedSupport SupportOf(IReadOnlyList<StrategyInstantObservation> observations)
    {
        var companies = new HashSet<Guid>();
        var dates = new HashSet<DateOnly>();
        foreach (var observation in observations)
        {
            companies.Add(observation.CompanyId);
            dates.Add(observation.AsOf);
        }

        return new PairedSupport(observations.Count, companies.Count, dates.Count);
    }

    private static List<PairedAdmittedBlock> BuildAdmittedBlocks(
        IReadOnlyList<OutcomeWindowBlock> admitted,
        IReadOnlyDictionary<DateOnly, List<StrategyInstantObservation>> survivingObservations)
    {
        var blocks = new List<PairedAdmittedBlock>(admitted.Count);
        foreach (var block in admitted)
        {
            var observations = survivingObservations[block.Date];
            var entry = observations.Min(o => o.EntryDate);
            var exit = observations.Max(o => o.ExitDate);
            blocks.Add(new PairedAdmittedBlock(block.Date, entry, exit));
        }

        return blocks;
    }

    /// <summary>
    /// The belt-and-braces observed-interval check: the nominal purge is conservative BECAUSE the forward
    /// return's exit rule never selects a bar after <c>d + h</c> (spec 152), so nominal non-overlap implies
    /// observed non-overlap — <c>exit_i ≤ d_i + h ≤ d_{i+1} &lt; entry_{i+1}</c>. Asserted anyway: if a
    /// future edit to the exit rule ever broke the implication, this throws — a violated assertion is a
    /// defect surfaced, not a silent claim.
    /// </summary>
    private static void VerifyObservedIntervalsDoNotOverlap(IReadOnlyList<PairedAdmittedBlock> admitted)
    {
        for (var i = 1; i < admitted.Count; i++)
        {
            var previous = admitted[i - 1];
            var current = admitted[i];
            if (previous.ObservedExit >= current.ObservedEntry)
            {
                throw new InvalidOperationException(
                    "Purged block observed price intervals overlap: block "
                        + $"{previous.Date:yyyy-MM-dd} exits at {previous.ObservedExit:yyyy-MM-dd} but block "
                        + $"{current.Date:yyyy-MM-dd} enters at {current.ObservedEntry:yyyy-MM-dd}. The "
                        + "nominal purge should make this impossible (the exit rule never passes d+h), so "
                        + "this is a defect in the forward-return window, not a data condition to tolerate.");
            }
        }
    }

    /// <summary>
    /// The PRICE half's reasons, as structured closed-coded records (spec 170 §1.2). The rendered text of
    /// every reason is byte-identical to the pre-170 strings — <see cref="Ad15GateReason.Render"/> reproduces
    /// them — so the artifact's human-readable output does not regress while the record becomes parseable.
    /// </summary>
    private static List<Ad15GateReason> BuildPriceGateReasons(
        bool primaryWasPredeclared,
        DateOnly? boundary,
        int baselineCount,
        PairedSupport jointSupport,
        int eligibleCandidateCount,
        IReadOnlyList<BaselinePairedResult> baselineResults)
    {
        var reasons = new List<Ad15GateReason>();
        if (!primaryWasPredeclared)
        {
            reasons.Add(new Ad15GateReason(Ad15GateReasonCodes.NoPredeclaredPrimary));
        }

        if (boundary is null)
        {
            reasons.Add(new Ad15GateReason(Ad15GateReasonCodes.NoPrecommittedBoundary));
        }

        if (baselineCount == 0)
        {
            reasons.Add(new Ad15GateReason(Ad15GateReasonCodes.NoBaselines));
        }
        else if (jointSupport.Observations == 0)
        {
            reasons.Add(new Ad15GateReason(Ad15GateReasonCodes.EmptyIntersection));
        }
        else if (eligibleCandidateCount == 0)
        {
            reasons.Add(new Ad15GateReason(Ad15GateReasonCodes.NoEligibleBlocks));
        }

        foreach (var baseline in baselineResults)
        {
            if (!baseline.Interval.IsDefined)
            {
                reasons.Add(new Ad15GateReason(
                    Ad15GateReasonCodes.InsufficientPurgedBlocks,
                    baseline.BaselineName,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"admitted {baseline.Interval.BlockCount}, need at least 6 at 95%")));
                continue;
            }

            if (baseline.MedianDelta is not { } median || median <= 0.0)
            {
                reasons.Add(new Ad15GateReason(
                    Ad15GateReasonCodes.MedianPairedDeltaNotPositive, baseline.BaselineName));
            }

            if (baseline.Interval.Lower <= 0.0)
            {
                reasons.Add(new Ad15GateReason(
                    Ad15GateReasonCodes.IntervalLowerBoundNotPositive, baseline.BaselineName));
            }
        }

        return reasons;
    }
}
