using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spec 186 §3 — the SEMANTIC gate-verdict identity that replaced the artifact's filesystem mtime as the
/// thing a human operating-call override binds to. The properties that make the fix a fix: an identical
/// re-computation keeps the id (so a declared override survives the daily efficacy re-write and a
/// copy/restore), new ADMITTED evidence mints a new one, an AD-16 prerequisite transition ALONE mints a new
/// one (the case a date anchor would miss — no paired as-of date changes), no verdict means no id, and the
/// value is machine-independent and wall-clock-free (AD-3), pinned by value.
/// </summary>
public sealed class GateVerdictIdentityTests
{
    private static readonly DateOnly Boundary = new(2026, 1, 1);

    private static PairedComparisonOptions Options(DateOnly? firstEligibleAsOf) =>
        new("primary", firstEligibleAsOf, 2, new StrategyComparisonOptions(21, 0.30, 20, 4));

    /// <summary>
    /// A hand-built comparison result — deliberately NOT harness-derived, so this file pins the identity
    /// rather than the price fixtures' arithmetic.
    /// </summary>
    private static PairedStrategyComparison Result(
        bool predeclared = true,
        bool withBoundary = true,
        int blocks = 2,
        bool satisfiesPriceGate = true,
        IReadOnlyList<Ad15GateReason>? priceReasons = null)
    {
        var dates = Enumerable.Range(0, blocks).Select(i => Boundary.AddDays(i * 21)).ToList();

        return new PairedStrategyComparison(
            PrimaryStrategyName: "primary",
            PrimaryWasPredeclared: predeclared,
            FirstEligibleAsOf: withBoundary ? Boundary : null,
            ArmsConsidered: 3,
            BaselineNames: ["baseline-a"],
            MarginalSupports: [],
            PairwiseSupports: [],
            JointSupport: new PairedSupport(24, 4, 6),
            EligibleJointSupport: new PairedSupport(24, 4, 6),
            InconsistentOutcomeObservationsDropped: 0,
            ObservationsWithoutAsOfInstant: 0,
            ObservationsWithMismatchedAsOfInstant: 0,
            CandidateDates:
            [
                .. dates.Select(d => new PairedCandidateDate(
                    d, 4, 0.5, [new PairedBaselineRho("baseline-a", -0.25, 0.75)])),
            ],
            DroppedDates: [],
            DevelopmentDateCount: 0,
            AdmittedBlocks: [.. dates.Select(d => new PairedAdmittedBlock(d, d.AddDays(1), d.AddDays(20)))],
            Baselines:
            [
                new BaselinePairedResult(
                    "baseline-a",
                    [.. dates.Select(d => new PairedDelta(d, 0.75))],
                    MedianDelta: 0.75,
                    Interval: new ExactMedianIntervalResult(
                        IsDefined: true,
                        Lower: 0.5,
                        Upper: 1.0,
                        LowerOrderStatistic: 1,
                        AchievedCoverage: 0.96875,
                        BlockCount: blocks,
                        Reason: MedianIntervalUndefinedReason.None),
                    SignTest: SignTestResult.Undefined(0, SignTestUndefinedReason.NoNonZeroDeltas),
                    ClearsGate: satisfiesPriceGate),
            ],
            SatisfiesPriceGate: satisfiesPriceGate,
            PriceGateReasons: priceReasons ?? [],
            Options: Options(withBoundary ? Boundary : null));
    }

    private static Ad15ClaimVerdict Verdict(PairedStrategyComparison result, Ad16ScreenOutcome outcome) =>
        Ad15ClaimGate.Evaluate(
            result.SatisfiesPriceGate, result.PriceGateReasons, Ad15AttentionPrerequisite.For(outcome));

    // ---- stability -------------------------------------------------------------------------------------

    [Fact]
    public void IdenticalInput_YieldsTheSameId_HoweverOftenItIsRecomputed()
    {
        // The whole point: efficacy rewrites its artifacts every run. An identical re-computation — and
        // therefore an identical rewrite, and therefore a copy/restore of the written file — must not move
        // the id, or a valid override expires after one run (the spec-184 defect).
        var first = GateVerdictIdentity.Compute(
            Result(), Verdict(Result(), Ad16ScreenOutcome.ClearsNecessaryScreen));
        var second = GateVerdictIdentity.Compute(
            Result(), Verdict(Result(), Ad16ScreenOutcome.ClearsNecessaryScreen));

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void TheId_IsPinnedByValue_SoItIsMachineIndependentAndWallClockFree()
    {
        // A hard-coded expected digest is the practical proof of AD-3: any clock, path, mtime, machine name
        // or run id in the canonical string would make this fail on the next run or the next machine.
        var result = Result();

        Assert.Equal(
            "6e5480aeb82d39b899c5b67b7c35469d1c852421a8306a11b269bd4d10c52944",
            GateVerdictIdentity.Compute(result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen)));
    }

    [Fact]
    public void TheCanonicalString_CarriesTheContract_AndNoRunProvenance()
    {
        var result = Result();
        var canonical = GateVerdictIdentity.CanonicalString(
            result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));

        Assert.StartsWith(GateVerdictIdentity.CanonicalPrefix, canonical, StringComparison.Ordinal);
        Assert.Contains(Ad15GateReasonCodes.VocabularyVersion, canonical, StringComparison.Ordinal);
        Assert.Contains("2026-01-01", canonical, StringComparison.Ordinal);       // the declared boundary
        Assert.Contains("clears-necessary-screen", canonical, StringComparison.Ordinal);

        // Nothing about the RUN may enter the identity.
        Assert.DoesNotContain(Environment.MachineName, canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, canonical);
        Assert.DoesNotContain("strategy-paired-comparison", canonical, StringComparison.Ordinal);
    }

    // ---- re-arming -------------------------------------------------------------------------------------

    [Fact]
    public void ANewAdmittedOutcomeBlock_YieldsANewId()
    {
        var before = Result(blocks: 2);
        var after = Result(blocks: 3);

        Assert.NotEqual(
            GateVerdictIdentity.Compute(before, Verdict(before, Ad16ScreenOutcome.Miss)),
            GateVerdictIdentity.Compute(after, Verdict(after, Ad16ScreenOutcome.Miss)));
    }

    [Fact]
    public void AnAd16PrerequisiteTransitionAlone_YieldsANewId_WithNoPairedEvidenceChange()
    {
        // The case round 1's date anchor missed: the paired evidence is byte-identical (same blocks, same
        // deltas, same as-of dates) and ONLY the attention prerequisite moved. Both outcomes satisfy the
        // prerequisite and both verdicts qualify — so this is not a pass/fail flip, it is a different
        // verdict resting on a different prerequisite, and an override must not silently carry over.
        var result = Result();

        var miss = GateVerdictIdentity.Compute(result, Verdict(result, Ad16ScreenOutcome.Miss));
        var clears = GateVerdictIdentity.Compute(
            result, Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen));

        Assert.NotEmpty(miss);
        Assert.NotEmpty(clears);
        Assert.NotEqual(miss, clears);
    }

    [Fact]
    public void APrerequisiteThatBecomesCalculated_TurnsNoVerdictIntoAVerdict()
    {
        var result = Result();

        // Pending: the composite gate could not evaluate ⇒ no verdict ⇒ no id to override.
        Assert.Equal(
            GateVerdictIdentity.None,
            GateVerdictIdentity.Compute(result, Verdict(result, Ad16ScreenOutcome.Pending)));

        Assert.NotEmpty(GateVerdictIdentity.Compute(result, Verdict(result, Ad16ScreenOutcome.Miss)));
    }

    // ---- when a verdict exists at all ------------------------------------------------------------------

    [Fact]
    public void AMeritOnlyFailure_IsAVerdict_AndCarriesAnId()
    {
        var reasons = new[]
        {
            new Ad15GateReason(Ad15GateReasonCodes.MedianPairedDeltaNotPositive, baselineName: "baseline-a"),
        };
        var result = Result(satisfiesPriceGate: false, priceReasons: reasons);
        var verdict = Verdict(result, Ad16ScreenOutcome.Miss);

        Assert.False(verdict.Qualifies);
        Assert.True(GateVerdictIdentity.VerdictExists(result, verdict));
        Assert.NotEmpty(GateVerdictIdentity.Compute(result, verdict));
    }

    [Fact]
    public void AnAccrualFailure_IsNotAVerdict_AndCarriesNoId()
    {
        var reasons = new[]
        {
            new Ad15GateReason(Ad15GateReasonCodes.InsufficientPurgedBlocks, baselineName: "baseline-a"),
        };
        var result = Result(satisfiesPriceGate: false, priceReasons: reasons);
        var verdict = Verdict(result, Ad16ScreenOutcome.Miss);

        Assert.False(GateVerdictIdentity.VerdictExists(result, verdict));
        Assert.Equal(GateVerdictIdentity.None, GateVerdictIdentity.Compute(result, verdict));
    }

    [Theory]
    [InlineData(false, true)]   // no predeclared primary
    [InlineData(true, false)]   // no precommitted boundary
    public void AnExploratoryArtifact_ExpressesNoVerdict(bool predeclared, bool hasBoundary)
    {
        var result = Result(predeclared: predeclared, withBoundary: hasBoundary);
        var verdict = Verdict(result, Ad16ScreenOutcome.ClearsNecessaryScreen);

        Assert.False(GateVerdictIdentity.VerdictExists(result, verdict));
        Assert.Equal(GateVerdictIdentity.None, GateVerdictIdentity.Compute(result, verdict));
    }

    [Fact]
    public void ADifferentMeritReasonCode_YieldsANewId()
    {
        var median = Result(
            satisfiesPriceGate: false,
            priceReasons:
            [
                new Ad15GateReason(
                    Ad15GateReasonCodes.MedianPairedDeltaNotPositive, baselineName: "baseline-a"),
            ]);
        var interval = Result(
            satisfiesPriceGate: false,
            priceReasons:
            [
                new Ad15GateReason(
                    Ad15GateReasonCodes.IntervalLowerBoundNotPositive, baselineName: "baseline-a"),
            ]);

        Assert.NotEqual(
            GateVerdictIdentity.Compute(median, Verdict(median, Ad16ScreenOutcome.Miss)),
            GateVerdictIdentity.Compute(interval, Verdict(interval, Ad16ScreenOutcome.Miss)));
    }
}
