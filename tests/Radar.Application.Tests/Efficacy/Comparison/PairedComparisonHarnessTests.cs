using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The spec-155 paired, purged harness: joint support, per-date rhos over identical companies and outcomes,
/// the precommitted boundary, the greedy purge, the exact interval, the sign-test diagnostic and the PRICE
/// half of the AD-15 gate — each acceptance criterion pinned by a fixture. (The composite gate — price +
/// AD-16 prerequisite — is <c>Ad15ClaimGate</c>'s and has its own tests.)
/// </summary>
public sealed class PairedComparisonHarnessTests
{
    private static readonly PairedComparisonHarness Harness = new();

    // ------------------------------------------------------------------ joint / pairwise / marginal support

    [Fact]
    public void Compare_DisclosesMarginalPairwiseAndJointSupport_AndJointIsStrictlySmaller()
    {
        // primary: 4 companies × 3 dates = 12; baseline-a: 3 companies × 3 dates = 9;
        // baseline-b: 4 companies × 2 dates = 8; joint = 3 companies × 2 dates = 6.
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, [0, 21, 42]),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, [0, 21, 42], companyIndexes: [0, 1, 2]),
                PairedFixtures.Series("baseline-b", PairedFixtures.AntiAligned, [0, 21]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options());

        Assert.Equal(3, result.ArmsConsidered);
        Assert.Equal(["baseline-a", "baseline-b"], result.BaselineNames);

        Assert.Equal(12, result.MarginalSupports.Single(s => s.StrategyName == "primary").Support.Observations);
        Assert.Equal(9, result.MarginalSupports.Single(s => s.StrategyName == "baseline-a").Support.Observations);
        Assert.Equal(8, result.MarginalSupports.Single(s => s.StrategyName == "baseline-b").Support.Observations);

        Assert.Equal(9, result.PairwiseSupports.Single(s => s.BaselineName == "baseline-a").Support.Observations);
        Assert.Equal(8, result.PairwiseSupports.Single(s => s.BaselineName == "baseline-b").Support.Observations);

        Assert.Equal(new PairedSupport(6, 3, 2), result.JointSupport);
        Assert.All(result.MarginalSupports, s =>
            Assert.True(result.JointSupport.Observations < s.Support.Observations));
    }

    [Fact]
    public void Compare_ZeroBaselines_IsAnHonestNoBaselinesResultNotAThrow()
    {
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(3)),
                PairedFixtures.Series("other-arm", PairedFixtures.AntiAligned, PairedFixtures.Spaced(3)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        Assert.Empty(result.Baselines);
        Assert.Equal(2, result.ArmsConsidered);
        Assert.Equal(PairedSupport.Empty, result.JointSupport);
        Assert.False(result.SatisfiesPriceGate);
        Assert.Contains(result.PriceGateReasons, r => r.Code == Ad15GateReasonCodes.NoBaselines);
    }

    // ------------------------------------------------------------------------------ per-date rho discipline

    [Fact]
    public void Compare_PerDateRho_UsesOnlyJointCompanies_AnExtraPrimaryCompanyNeverEnters()
    {
        // Company 3 exists ONLY in the primary, scored so that including it would destroy the perfect
        // alignment (highest score on the worst-return company). ρ must still be exactly +1 over the joint
        // three companies — proof the extra company never entered the cross-section.
        var result = Harness.Compare(
            [
                PairedFixtures.Series(
                    "primary", (c, d) => c == 3 ? 100 : PairedFixtures.Aligned(c, d), [0, 21]),
                PairedFixtures.Series(
                    "baseline-a", PairedFixtures.AntiAligned, [0, 21], companyIndexes: [0, 1, 2]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options());

        Assert.Equal(2, result.CandidateDates.Count);
        Assert.All(result.CandidateDates, d =>
        {
            Assert.Equal(3, d.Companies);
            Assert.Equal(1.0, d.PrimaryRho, 12);
            Assert.Equal(2.0, Assert.Single(d.Baselines).Delta, 12);
        });
    }

    [Fact]
    public void Compare_TooFewCompanies_DropsTheDateWithItsReason()
    {
        // baseline-a lacks company 3 on day 42 only ⇒ joint has 3 < 4 companies there.
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, [0, 21, 42]),
                PairedFixtures.Series(
                    "baseline-a",
                    (c, d) => c == 3 && d == 42 ? null : PairedFixtures.AntiAligned(c, d),
                    [0, 21, 42]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(minimumCompaniesPerDate: 4));

        var dropped = Assert.Single(result.DroppedDates);
        Assert.Equal(PairedFixtures.AsOf(42), dropped.Date);
        Assert.Equal(PairedDateDropReason.TooFewCompanies, dropped.Reason);
        Assert.Null(dropped.BaselineName);
        Assert.DoesNotContain(result.CandidateDates, d => d.Date == PairedFixtures.AsOf(42));
    }

    [Fact]
    public void Compare_ConstantPrimary_DropsTheDateWithItsReason()
    {
        var result = Harness.Compare(
            [
                PairedFixtures.Series(
                    "primary", (c, d) => d == 21 ? 50 : PairedFixtures.Aligned(c, d), [0, 21, 42]),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, [0, 21, 42]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options());

        var dropped = Assert.Single(result.DroppedDates);
        Assert.Equal(PairedFixtures.AsOf(21), dropped.Date);
        Assert.Equal(PairedDateDropReason.ConstantPrimary, dropped.Reason);
    }

    [Fact]
    public void Compare_ConstantBaseline_DropsTheDateForTheWholeFamilyNamingTheBaseline()
    {
        // Two baselines; only baseline-a is constant on day 21 — the date still drops for BOTH (all deltas
        // must use the same dates) and the record names the offender.
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, [0, 21, 42]),
                PairedFixtures.Series(
                    "baseline-a",
                    (c, d) => d == 21 ? 50 : PairedFixtures.AntiAligned(c, d),
                    [0, 21, 42]),
                PairedFixtures.Series("baseline-b", PairedFixtures.AntiAligned, [0, 21, 42]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options());

        var dropped = Assert.Single(result.DroppedDates);
        Assert.Equal(PairedDateDropReason.ConstantBaseline, dropped.Reason);
        Assert.Equal("baseline-a", dropped.BaselineName);

        // The whole family lost the date: baseline-b's admitted deltas exclude day 21 too.
        Assert.All(result.Baselines, b =>
            Assert.DoesNotContain(b.AdmittedDeltas, x => x.Date == PairedFixtures.AsOf(21)));
    }

    [Fact]
    public void Compare_ConstantOutcome_DropsTheDateWithItsReason()
    {
        // On day 21 the joint cross-section is exactly the two flat-price companies (4, 5): their forward
        // returns are both exactly 0, so the outcome vector is constant while the scores are not.
        static int? Membership(int c, int d) => d == 21
            ? (c is 4 or 5 ? PairedFixtures.Aligned(c, d) : null)
            : (c is 4 or 5 ? null : PairedFixtures.Aligned(c, d));

        static int? BaselineMembership(int c, int d) => d == 21
            ? (c is 4 or 5 ? PairedFixtures.AntiAligned(c, d) : null)
            : (c is 4 or 5 ? null : PairedFixtures.AntiAligned(c, d));

        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", Membership, [0, 21, 42], companyIndexes: [0, 1, 2, 3, 4, 5]),
                PairedFixtures.Series(
                    "baseline-a", BaselineMembership, [0, 21, 42], companyIndexes: [0, 1, 2, 3, 4, 5]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options());

        var dropped = Assert.Single(result.DroppedDates);
        Assert.Equal(PairedFixtures.AsOf(21), dropped.Date);
        Assert.Equal(PairedDateDropReason.ConstantOutcome, dropped.Reason);
    }

    [Fact]
    public void Compare_AnInconsistentOutcomeAcrossArms_IsDroppedAndCountedNeverChosenBetween()
    {
        // baseline-a carries DIFFERENT bars for company 0 (slope 0.3 vs the primary's 0.5), so its forward
        // return for company 0 disagrees on every date: those observations leave the joint set with their
        // own counter, and no arm's value is silently preferred.
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, [0, 21, 42]),
                PairedFixtures.Series(
                    "baseline-a",
                    PairedFixtures.AntiAligned,
                    [0, 21, 42],
                    barsOverride: c => PairedFixtures.Bars(c, slopeOverride: c == 0 ? 0.3m : null)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options());

        Assert.Equal(3, result.InconsistentOutcomeObservationsDropped);
        Assert.Equal(3, result.JointSupport.DistinctCompanies);
        Assert.All(result.CandidateDates, d => Assert.Equal(3, d.Companies));
    }

    // ---------------------------------------------------------------------------- boundary / claim path

    [Fact]
    public void Compare_NullBoundary_IsExploratoryAndTheGateCanNeverPass()
    {
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: null));

        Assert.Null(result.FirstEligibleAsOf);
        Assert.False(result.SatisfiesPriceGate);
        Assert.Contains(
            result.PriceGateReasons, r => r.Code == Ad15GateReasonCodes.NoPrecommittedBoundary);

        // Everything is still rendered/evaluated — exploratory, not silent: the deltas and interval exist
        // even though no claim is expressible from them.
        var baseline = Assert.Single(result.Baselines);
        Assert.Equal(7, baseline.AdmittedDeltas.Count);
        Assert.True(baseline.Interval.IsDefined);
    }

    [Fact]
    public void Compare_DatesBeforeTheBoundary_AreDevelopmentDataAndNeverEnterTheClaimBlocks()
    {
        var boundary = PairedFixtures.AsOf(63);
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: boundary));

        // Dates 0/21/42 are development data; 63/84/105/126 are eligible.
        Assert.Equal(3, result.DevelopmentDateCount);
        Assert.Equal(4, result.AdmittedBlocks.Count);
        Assert.All(result.AdmittedBlocks, b => Assert.True(b.Date >= boundary));
        Assert.All(result.Baselines, b => Assert.All(b.AdmittedDeltas, x => Assert.True(x.Date >= boundary)));

        // 4 admitted blocks < 6 ⇒ the interval is honestly insufficient rather than relaxed.
        var baseline = Assert.Single(result.Baselines);
        Assert.False(baseline.Interval.IsDefined);
        Assert.Equal(MedianIntervalUndefinedReason.InsufficientPurgedBlocks, baseline.Interval.Reason);
        Assert.False(result.SatisfiesPriceGate);
        var reason = Assert.Single(
            result.PriceGateReasons, r => r.Code == Ad15GateReasonCodes.InsufficientPurgedBlocks);
        // The rendered text of the migrated reason is byte-identical to the pre-170 string.
        Assert.Equal(
            "baseline 'baseline-a': insufficient-purged-blocks (admitted 4, need at least 6 at 95%)",
            reason.Render());
    }

    // ----------------------------------------------------------------------------------------- the purge

    [Fact]
    public void Compare_DenseDailyDates_PurgesGreedilyAndCountsEverySkipAsOverlappingOutcomeWindow()
    {
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Daily(60), weekdaysOnly: true),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Daily(60), weekdaysOnly: true),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        Assert.Equal(
            [PairedFixtures.AsOf(0), PairedFixtures.AsOf(21), PairedFixtures.AsOf(42)],
            result.AdmittedBlocks.Select(b => b.Date).ToList());

        Assert.Equal(57, result.DroppedDates.Count(d =>
            d.Reason == PairedDateDropReason.OverlappingOutcomeWindow));

        // Observed intervals with REALISTIC (weekday-only) entry/exit bars: consecutive admitted blocks'
        // observed price intervals are provably disjoint, not just their nominal windows.
        for (var i = 1; i < result.AdmittedBlocks.Count; i++)
        {
            Assert.True(result.AdmittedBlocks[i - 1].ObservedExit < result.AdmittedBlocks[i].ObservedEntry);
        }

        // And each block's observed window sits strictly inside its nominal (d, d+h] window.
        Assert.All(result.AdmittedBlocks, b =>
        {
            Assert.True(b.ObservedEntry > b.Date);
            Assert.True(b.ObservedExit <= b.Date.AddDays(PairedFixtures.HorizonDays));
        });
    }

    // ------------------------------------------------------------------------- interval sensitivity (AC)

    [Fact]
    public void Compare_ChangingUnadmittedOverlappingDates_CannotChangeTheInterval_ChangingAnAdmittedDateCan()
    {
        // Dense daily dates over 127 days ⇒ admitted blocks at 0, 21, …, 126 (7 blocks).
        var daily = PairedFixtures.Daily(127);
        var admittedOffsets = Enumerable.Range(0, 7).Select(i => i * 21).ToHashSet();

        static StrategyScoreSeries Primary(Func<int, int, int?> score, IReadOnlyList<int> dates) =>
            PairedFixtures.Series("primary", score, dates);

        PairedStrategyComparison Run(Func<int, int, int?> primaryScore) => Harness.Compare(
            [
                Primary(primaryScore, daily),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, daily),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        var baselineFixture = Run(PairedFixtures.Aligned);

        // Perturb EVERY unadmitted (overlapping, purged) date: anti-align the primary there.
        var unadmittedPerturbed = Run(
            (c, d) => admittedOffsets.Contains(d) ? PairedFixtures.Aligned(c, d) : PairedFixtures.AntiAligned(c, d));

        // Perturb ONE admitted date (day 42): anti-align the primary there only.
        var admittedPerturbed = Run(
            (c, d) => d == 42 ? PairedFixtures.AntiAligned(c, d) : PairedFixtures.Aligned(c, d));

        var baseInterval = Assert.Single(baselineFixture.Baselines).Interval;
        var unadmittedInterval = Assert.Single(unadmittedPerturbed.Baselines).Interval;
        var admittedInterval = Assert.Single(admittedPerturbed.Baselines).Interval;

        Assert.True(baseInterval.IsDefined);
        Assert.Equal(baseInterval, unadmittedInterval);        // unadmitted data is inert — byte-identical

        Assert.NotEqual(baseInterval, admittedInterval);       // one admitted date moved the interval
        Assert.Equal(2.0, baseInterval.Lower, 12);
        Assert.Equal(0.0, admittedInterval.Lower, 12);
    }

    // ------------------------------------------------------------------------- AD-15 gate (price half)

    [Fact]
    public void Compare_PriceGateTrue_WhenEveryBaselineClearsWithAPositiveLowerBoundAndBoundaryPresent()
    {
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series("baseline-b", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        Assert.True(result.SatisfiesPriceGate);
        Assert.Empty(result.PriceGateReasons);
        Assert.Equal(2, result.Baselines.Count);
        Assert.All(result.Baselines, b =>
        {
            Assert.True(b.ClearsGate);
            Assert.Equal(2.0, b.MedianDelta!.Value, 12);
            Assert.True(b.Interval.IsDefined);
            Assert.True(b.Interval.Lower > 0.0);
        });
    }

    [Fact]
    public void Compare_GateFalse_WhenOneBaselineOfSeveralFails()
    {
        // baseline-b tracks the primary exactly ⇒ every delta 0 ⇒ median 0, lower bound 0 — it fails, so
        // the FAMILY fails even though baseline-a is cleared decisively.
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series("baseline-b", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        Assert.False(result.SatisfiesPriceGate);
        Assert.True(result.Baselines.Single(b => b.BaselineName == "baseline-a").ClearsGate);
        Assert.False(result.Baselines.Single(b => b.BaselineName == "baseline-b").ClearsGate);
        Assert.All(result.PriceGateReasons, r => Assert.Equal("baseline-b", r.BaselineName));
    }

    [Fact]
    public void Compare_MarginalGapExceedsBaselineSpread_ButPairedIntervalIncludesZero_GateFalse()
    {
        // The spec's required fixture: under the SUPERSEDED rule this would have "cleared" — the primary's
        // pooled marginal rho beats both (identical) baselines by far more than their zero spread — but the
        // exact paired interval includes zero, so the amended gate refuses it.
        var dates = PairedFixtures.Spaced(8);
        int? Alternating(int c, int d) => (d / PairedFixtures.HorizonDays) % 2 == 0
            ? PairedFixtures.Aligned(c, d)
            : PairedFixtures.AntiAligned(c, d);

        var primarySeries = PairedFixtures.Series("primary", Alternating, dates);
        var baselineA = PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, dates);
        var baselineB = PairedFixtures.Series("baseline-b", PairedFixtures.AntiAligned, dates);

        var result = Harness.Compare(
            [primarySeries, baselineA, baselineB],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        // The marginal (pooled) picture, computed the leaderboard's way over the SAME observations.
        static double PooledRho(StrategyScoreSeries series)
        {
            var set = StrategyObservationBuilder.Build(
                series, PairedFixtures.HorizonDays, PairedFixtures.ExitToleranceDays);
            var rho = RankCorrelation.ComputeRho(
                [.. set.Usable.Select(o => o.Score)],
                [.. set.Usable.Select(o => o.ForwardReturn)]);
            Assert.True(rho.IsDefined);
            return rho.Rho;
        }

        var primaryMarginal = PooledRho(primarySeries);
        var baselineAMarginal = PooledRho(baselineA);
        var baselineBMarginal = PooledRho(baselineB);
        var spread = Math.Abs(baselineAMarginal - baselineBMarginal);

        // Identical baselines ⇒ spread exactly 0; the marginal gap is decisively larger.
        Assert.Equal(0.0, spread, 12);
        Assert.True(primaryMarginal - Math.Max(baselineAMarginal, baselineBMarginal) > spread);

        // …and yet the paired interval includes zero, so no baseline clears and the gate is false.
        Assert.False(result.SatisfiesPriceGate);
        Assert.All(result.Baselines, b =>
        {
            Assert.True(b.Interval.IsDefined);
            Assert.True(b.Interval.Lower <= 0.0 && b.Interval.Upper >= 0.0);
            Assert.False(b.ClearsGate);
        });
        Assert.Contains(result.PriceGateReasons, r =>
            r.Code == Ad15GateReasonCodes.IntervalLowerBoundNotPositive);
    }

    [Fact]
    public void Compare_NotPredeclaredPrimary_GateFalseWithItsOwnReason()
    {
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
            ],
            "primary",
            primaryWasPredeclared: false,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf, configuredPrimary: ""));

        Assert.False(result.SatisfiesPriceGate);
        Assert.Contains(result.PriceGateReasons, r => r.Code == Ad15GateReasonCodes.NoPredeclaredPrimary);
        Assert.False(result.PrimaryWasPredeclared);
    }

    // ------------------------------------------------------------------------ sign test / zero deltas

    [Fact]
    public void Compare_SignTest_DropsZeroDeltasFromItsEffectiveNOnly_TheIntervalKeepsThem()
    {
        // baseline-a mirrors the primary exactly on day 42 (delta 0) and is anti-aligned elsewhere
        // (delta 2): the interval is computed over ALL 7 deltas, the sign test over the 6 non-zero ones.
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
                PairedFixtures.Series(
                    "baseline-a",
                    (c, d) => d == 42 ? PairedFixtures.Aligned(c, d) : PairedFixtures.AntiAligned(c, d),
                    PairedFixtures.Spaced(7)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        var baseline = Assert.Single(result.Baselines);

        Assert.Equal(7, baseline.Interval.BlockCount);        // zeros stay in the interval's n
        Assert.Equal(0.0, baseline.Interval.Lower, 12);       // [0, 2] — the zero is real data

        Assert.Equal(6, baseline.SignTest.EffectiveN);        // …but leave the sign test's N
        Assert.Equal(1, baseline.SignTest.ZeroDeltasDropped);
        Assert.Equal(6, baseline.SignTest.PositiveDeltas);
        Assert.Equal(2.0 / 64.0, baseline.SignTest.PValue, 15);
    }

    // ----------------------------------------------------------------------------- fail-fast contracts

    [Fact]
    public void Compare_PrimaryNotAmongStrategies_FailsFastNamingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.Compare(
            [PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, [0])],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options()));

        Assert.Contains("'primary'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PairedPrimaryStrategy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_PrimaryThatIsABaseline_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Harness.Compare(
            [
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, [0]),
                PairedFixtures.Series("other", PairedFixtures.Aligned, [0]),
            ],
            "baseline-a",
            primaryWasPredeclared: true,
            PairedFixtures.Options(configuredPrimary: "baseline-a")));

        Assert.Contains("baseline", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_IsDeterministic()
    {
        PairedStrategyComparison Run() => Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Daily(50)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Daily(50)),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        var renderer = new PairedComparisonRenderer();
        static Ad15ClaimVerdict Verdict(PairedStrategyComparison result) => Ad15ClaimGate.Evaluate(
            result.SatisfiesPriceGate,
            result.PriceGateReasons,
            Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.ClearsNecessaryScreen));

        var first = Run();
        var second = Run();
        Assert.Equal(renderer.RenderCsv(first, Verdict(first)), renderer.RenderCsv(second, Verdict(second)));
        Assert.Equal(
            renderer.RenderMarkdown(first, Verdict(first)),
            renderer.RenderMarkdown(second, Verdict(second)));
        Assert.Equal(renderer.RenderBlocksCsv(first), renderer.RenderBlocksCsv(second));
    }

    // ----------------------------------------------------------------- spec 170: exact-instant pairing

    [Fact]
    public void Compare_SameDayObservationsWithDifferentInstants_AreNotPaired_AndTheKeysAreCounted()
    {
        // A partial rerun: on day 21 the primary's four companies were re-scored at 09:00 while baseline-a
        // still carries midnight snapshots. Same calendar date, different knowledge cutoffs ⇒ NOT paired.
        var rerunInstant = PairedFixtures.InstantOf(21).AddHours(9);
        var result = Harness.Compare(
            [
                PairedFixtures.Series(
                    "primary",
                    PairedFixtures.Aligned,
                    [0, 21, 42],
                    instant: (_, d) => d == 21 ? rerunInstant : PairedFixtures.InstantOf(d)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, [0, 21, 42]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        // Day 21 vanishes from the joint support entirely — the mismatch never falls back to date pairing.
        Assert.Equal(2, result.JointSupport.DistinctAsOfDates);
        Assert.DoesNotContain(result.CandidateDates, d => d.Date == PairedFixtures.AsOf(21));

        // …and the four (company, 2026-01-22) keys are counted as mismatched, in KEY units.
        Assert.Equal(4, result.ObservationsWithMismatchedAsOfInstant);
        Assert.Equal(0, result.ObservationsWithoutAsOfInstant);
    }

    [Fact]
    public void Compare_ObservationWithoutAnInstant_NeverEntersTheClaimPath_AndIsCounted()
    {
        // The primary's company 0 carries NO instant on day 21 (the legacy-point shape). Even though
        // baseline-a has a same-day observation for it, the pair must NOT form — a legacy point is exactly
        // the case where the two arms' knowledge cutoffs are unverifiable.
        var result = Harness.Compare(
            [
                PairedFixtures.Series(
                    "primary",
                    PairedFixtures.Aligned,
                    [0, 21, 42],
                    instant: (c, d) => c == 0 && d == 21 ? null : PairedFixtures.InstantOf(d)),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, [0, 21, 42]),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        Assert.Equal(1, result.ObservationsWithoutAsOfInstant);
        Assert.Equal(0, result.ObservationsWithMismatchedAsOfInstant);

        // Day 21's joint cross-section holds only the three companies whose instants exist and match.
        var day21 = Assert.Single(result.CandidateDates, d => d.Date == PairedFixtures.AsOf(21));
        Assert.Equal(3, day21.Companies);

        // The marginal (date-projection) support still counts the instant-less observation — the
        // descriptive side is untouched.
        Assert.Equal(
            12, result.MarginalSupports.Single(s => s.StrategyName == "primary").Support.Observations);
    }

    [Fact]
    public void Compare_EligibleJointSupport_IsTheBoundaryRestrictedSupport_AndEmptyWithoutABoundary()
    {
        StrategyScoreSeries[] Arms() =>
        [
            PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Spaced(7)),
            PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Spaced(7)),
        ];

        // Boundary at day 63: dates 0/21/42 are development, 63/84/105/126 are eligible — 4 dates ×
        // 4 companies = 16 eligible joint observations beside the all-history 28.
        var bounded = Harness.Compare(
            Arms(),
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.AsOf(63)));
        Assert.Equal(new PairedSupport(28, 4, 7), bounded.JointSupport);
        Assert.Equal(new PairedSupport(16, 4, 4), bounded.EligibleJointSupport);

        // No boundary ⇒ the eligible support is EMPTY, never the all-history figure.
        var unbounded = Harness.Compare(
            Arms(),
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: null));
        Assert.Equal(new PairedSupport(28, 4, 7), unbounded.JointSupport);
        Assert.Equal(PairedSupport.Empty, unbounded.EligibleJointSupport);
    }

    [Fact]
    public void Compare_EveryArmSharingOneInstantPerDay_KeepsTheAdmittedBlockSetOfTheDateProjection()
    {
        // Spec 170 §2.2: only the INTERSECTION becomes exact; block grouping, purging and the boundary stay
        // on the calendar date. With one shared instant per day, the admitted-block set is unchanged from
        // the dense-daily fixture the purge test pins.
        var result = Harness.Compare(
            [
                PairedFixtures.Series("primary", PairedFixtures.Aligned, PairedFixtures.Daily(60), weekdaysOnly: true),
                PairedFixtures.Series("baseline-a", PairedFixtures.AntiAligned, PairedFixtures.Daily(60), weekdaysOnly: true),
            ],
            "primary",
            primaryWasPredeclared: true,
            PairedFixtures.Options(firstEligibleAsOf: PairedFixtures.FirstAsOf));

        Assert.Equal(
            [PairedFixtures.AsOf(0), PairedFixtures.AsOf(21), PairedFixtures.AsOf(42)],
            result.AdmittedBlocks.Select(b => b.Date).ToList());
        Assert.Equal(0, result.ObservationsWithoutAsOfInstant);
        Assert.Equal(0, result.ObservationsWithMismatchedAsOfInstant);
    }
}
