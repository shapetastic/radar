namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The spec-140 comparison harness: given each strategy's persisted score series (already joined to its
/// company's price bars) it produces a ranked <see cref="StrategyLeaderboard"/>.
/// <para>
/// <b>THE METRIC IS DEFINED HERE, and that is not a contradiction of "reuse the 101/108 definition".</b> Specs
/// 101/108 emit no numeric efficacy metric at all — they produce a per-company JOIN of score snapshots to the
/// price bar AT-OR-BEFORE the score date, rendered as an SVG + CSV for a human to look at. What is reused, and
/// must not be reinvented, is that join and its inputs (company universe, persisted score history, the price
/// reference store). The forward-horizon NUMBER is new in this slice because none existed, and it deliberately
/// uses the opposite side of the score date: 101/108 plots price at-or-before D (correct for a chart, since a
/// future bar would be a look-ahead artefact in a picture), while efficacy has to ask what happened AFTER D.
/// </para>
/// <para>
/// <b>Which score.</b> <c>OpportunityScore</c> — the component the weekly report ranks by, the component the
/// per-company efficacy chart plots by default, and (since spec 146) where a <c>radar-formula-v9</c> strategy's
/// channel composite lands. Comparing strategies on anything else would judge them on a number nobody acts on.
/// </para>
/// <para>
/// <b>Hold-out discipline is structural, not advisory.</b> The distinct as-of dates across ALL strategies are
/// split chronologically ONCE — an index partition of a sorted distinct list, so the two windows are disjoint
/// by construction and every strategy is judged on the same calendar. Ranking is computed inside this type on
/// the IN-SAMPLE window only; the caller receives an already-ordered list and never gets the raw material to
/// rank on the out-of-sample window it is supposed to be held to.
/// </para>
/// <para>
/// <b>Pure (AD-3).</b> No clock, no randomness, no I/O, no logging — same inputs yield a byte-identical
/// leaderboard. Observations are ordered by (as-of date, company id) before any summation, because
/// floating-point addition is not associative and an order-dependent ρ would break that guarantee.
/// </para>
/// <para>
/// <b>AD-14.</b> Everything here reads scoring OUTPUT (persisted snapshot components) and price. Nothing it
/// produces is written back into evidence, signals or a score.
/// </para>
/// </summary>
public sealed class StrategyComparisonHarness
{
    /// <param name="benchmark">
    /// The frozen-universe benchmark (spec 183). The pooled correlation is computed over EXCESS returns; a
    /// raw-usable observation whose excess does not exist is excluded with the named, counted reason
    /// (<c>BenchmarkUnavailable</c> / <c>NotInBenchmarkUniverse</c>) — never silently fed its raw return.
    /// <c>null</c> means the universe could not be loaded, so EVERY observation is <c>BenchmarkUnavailable</c>
    /// and the leaderboard's benchmark provenance is null (the renderer states it). Required — there is no
    /// overload without it, so no caller can accidentally produce a raw-pooled leaderboard again.
    /// </param>
    public StrategyLeaderboard Compare(
        IReadOnlyList<StrategyScoreSeries> strategies,
        StrategyComparisonOptions options,
        UniverseBenchmark? benchmark)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(options);

        var perStrategy = new List<StrategyObservationSet>(strategies.Count);
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            perStrategy.Add(StrategyObservationBuilder.Build(
                strategy, options.ForwardHorizonDays, options.ExitToleranceDays, benchmark));
        }

        // The pooled projection per strategy: the excess-defined observations. The date split, the metrics
        // and the coverage counts all run over THIS set — the leaderboard is the excess leaderboard, and an
        // excess-less observation is an exclusion (counted below), not a raw contribution.
        var pooledPerStrategy = new List<IReadOnlyList<StrategyObservation>>(perStrategy.Count);
        foreach (var strategy in perStrategy)
        {
            pooledPerStrategy.Add([.. strategy.Usable.Where(o => o.ExcessForwardReturn.HasValue)]);
        }

        var windows = SplitDates(pooledPerStrategy, options.HoldOutFraction);
        var inSampleDates = windows.InSample;

        var rows = new List<StrategyLeaderboardRow>();
        var dropped = new List<DroppedStrategy>();

        // Ranking candidates are accumulated with their in-sample ρ, then ordered — the ordering is applied
        // here and nowhere else, so an out-of-sample-ranked leaderboard is not expressible through this API.
        var candidates = new List<(
            double InSampleRho,
            string Name,
            StrategyWindowMetric In,
            StrategyWindowMetric Out,
            int Unusable,
            int Partial,
            int BenchmarkUnavailable,
            int NotInUniverse)>();

        for (var s = 0; s < perStrategy.Count; s++)
        {
            var strategy = perStrategy[s];

            var benchmarkUnavailable = 0;
            var notInUniverse = 0;
            foreach (var o in strategy.Usable)
            {
                if (o.ExcessForwardReturn.HasValue)
                {
                    continue;
                }

                if (o.ExcessUnavailableReason == BenchmarkExcessUnavailableReason.NotInBenchmarkUniverse)
                {
                    notInUniverse++;
                }
                else
                {
                    benchmarkUnavailable++;
                }
            }

            var inSample = new List<StrategyObservation>();
            var outOfSample = new List<StrategyObservation>();
            foreach (var o in pooledPerStrategy[s])
            {
                // Exactly one side by construction: membership of the in-sample date set is the whole rule.
                (inSampleDates.Contains(o.AsOf) ? inSample : outOfSample).Add(o);
            }

            var inMetric = Metric(inSample, options);
            var outMetric = Metric(outOfSample, options);

            if (inSample.Count < options.MinimumObservations)
            {
                dropped.Add(new DroppedStrategy(
                    strategy.StrategyName,
                    StrategyDropReason.InsufficientInSampleObservations,
                    inSample.Count,
                    outOfSample.Count,
                    RankCorrelationUndefinedReason.TooFewObservations));
                continue;
            }

            if (outOfSample.Count < options.MinimumObservations)
            {
                dropped.Add(new DroppedStrategy(
                    strategy.StrategyName,
                    StrategyDropReason.InsufficientOutOfSampleObservations,
                    inSample.Count,
                    outOfSample.Count,
                    RankCorrelationUndefinedReason.TooFewObservations));
                continue;
            }

            if (!inMetric.Correlation.IsDefined)
            {
                dropped.Add(new DroppedStrategy(
                    strategy.StrategyName,
                    StrategyDropReason.DegenerateInSampleMetric,
                    inSample.Count,
                    outOfSample.Count,
                    inMetric.Correlation.Reason));
                continue;
            }

            if (!outMetric.Correlation.IsDefined)
            {
                dropped.Add(new DroppedStrategy(
                    strategy.StrategyName,
                    StrategyDropReason.DegenerateOutOfSampleMetric,
                    inSample.Count,
                    outOfSample.Count,
                    outMetric.Correlation.Reason));
                continue;
            }

            candidates.Add((
                inMetric.Correlation.Rho,
                strategy.StrategyName,
                inMetric,
                outMetric,
                strategy.WithoutForwardPrice,
                strategy.PartialWindow,
                benchmarkUnavailable,
                notInUniverse));
        }

        // Best in-sample first; ties broken by name (Ordinal) so the order is total and deterministic.
        candidates.Sort(static (a, b) =>
        {
            var byRho = b.InSampleRho.CompareTo(a.InSampleRho);
            return byRho != 0 ? byRho : string.CompareOrdinal(a.Name, b.Name);
        });

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            rows.Add(new StrategyLeaderboardRow(
                i + 1, c.Name, c.In, c.Out, c.Unusable, c.Partial, c.BenchmarkUnavailable, c.NotInUniverse));
        }

        // Dropped strategies in a stable, name-ordered sequence (their input order is not meaningful).
        dropped.Sort(static (a, b) => string.CompareOrdinal(a.StrategyName, b.StrategyName));

        return new StrategyLeaderboard(
            StrategiesCompared: rows.Count,
            StrategiesConsidered: perStrategy.Count,
            Rows: rows,
            DroppedStrategies: dropped,
            Windows: windows.Summary,
            Options: options,
            Benchmark: BuildBenchmarkProvenance(benchmark, perStrategy, options));
    }

    /// <summary>
    /// The per-day benchmark coverage over every as-of date any strategy's RAW-usable observations touched —
    /// read from the SAME cached per-day computations the excess annotations consumed, so the provenance and
    /// the numbers cannot disagree. Null when no benchmark was supplied (rendered as "unavailable").
    /// </summary>
    private static LeaderboardBenchmarkProvenance? BuildBenchmarkProvenance(
        UniverseBenchmark? benchmark,
        IReadOnlyList<StrategyObservationSet> perStrategy,
        StrategyComparisonOptions options)
    {
        if (benchmark is null)
        {
            return null;
        }

        var dates = new SortedSet<DateOnly>();
        foreach (var strategy in perStrategy)
        {
            foreach (var o in strategy.Usable)
            {
                dates.Add(o.AsOf);
            }
        }

        var frozenOn = DateOnly.FromDateTime(benchmark.Universe.FrozenAtUtc.UtcDateTime);
        var days = new List<BenchmarkDayCoverage>(dates.Count);
        var preFreeze = 0;
        foreach (var date in dates)
        {
            var day = benchmark.DayAt(date, options.ForwardHorizonDays, options.ExitToleranceDays);
            days.Add(new BenchmarkDayCoverage(
                date,
                day.MemberCount,
                day.ResolvedCount,
                [.. day.Unresolved.Select(m => new BenchmarkMemberExclusion(m.Ticker, m.Reason))]));
            if (date < frozenOn)
            {
                preFreeze++;
            }
        }

        return new LeaderboardBenchmarkProvenance(
            benchmark.Universe.UniverseVersion,
            benchmark.Universe.ContentHash,
            benchmark.Universe.FrozenAtUtc,
            benchmark.Universe.Members.Count,
            days,
            preFreeze);
    }

    private static StrategyWindowMetric Metric(
        IReadOnlyList<StrategyObservation> observations, StrategyComparisonOptions options)
    {
        var scores = new List<double>(observations.Count);
        var returns = new List<double>(observations.Count);
        var companies = new HashSet<Guid>();
        var dates = new HashSet<DateOnly>();

        foreach (var o in observations)
        {
            scores.Add(o.Score);
            // The pooled metric consumes the EXCESS return (spec 183): only excess-defined observations reach
            // this method, so the value is present by construction.
            returns.Add(o.ExcessForwardReturn!.Value);
            companies.Add(o.CompanyId);
            dates.Add(o.AsOf);
        }

        return new StrategyWindowMetric(
            RankCorrelation.Compute(scores, returns, StrategyComparisonOptions.NormalQuantile95),
            new StrategyWindowCoverage(observations.Count, companies.Count, dates.Count));
    }

    private sealed record DateSplit(
        HashSet<DateOnly> InSample, StrategyComparisonWindows Summary);

    /// <summary>
    /// The ONE chronological split, over the distinct as-of dates of every strategy's POOLED (excess-defined)
    /// observations. It is computed once and shared, so two strategies are never judged on different calendars
    /// — and it is an index partition of a sorted distinct list, so no date can land on both sides.
    /// </summary>
    private static DateSplit SplitDates(
        IReadOnlyList<IReadOnlyList<StrategyObservation>> pooledPerStrategy, double holdOutFraction)
    {
        var all = new SortedSet<DateOnly>();
        foreach (var strategy in pooledPerStrategy)
        {
            foreach (var o in strategy)
            {
                all.Add(o.AsOf);
            }
        }

        var ordered = all.ToList();
        var n = ordered.Count;

        // Deterministic index: floor(n × (1 − holdOut)), nudged past a representation error such as
        // 10 × 0.7 = 6.999999999999999. Never clamped up into a non-empty window — an empty side is honest
        // "insufficient history", and manufacturing one observation to avoid it would be the opposite.
        var inSampleCount = (int)Math.Floor((n * (1.0 - holdOutFraction)) + 1e-9);
        inSampleCount = Math.Clamp(inSampleCount, 0, n);

        var inSample = new HashSet<DateOnly>();
        for (var i = 0; i < inSampleCount; i++)
        {
            inSample.Add(ordered[i]);
        }

        var summary = new StrategyComparisonWindows(
            TotalAsOfDates: n,
            InSampleAsOfDates: inSampleCount,
            OutOfSampleAsOfDates: n - inSampleCount,
            InSampleStart: inSampleCount > 0 ? ordered[0] : null,
            InSampleEnd: inSampleCount > 0 ? ordered[inSampleCount - 1] : null,
            OutOfSampleStart: inSampleCount < n ? ordered[inSampleCount] : null,
            OutOfSampleEnd: inSampleCount < n ? ordered[n - 1] : null);

        return new DateSplit(inSample, summary);
    }
}
