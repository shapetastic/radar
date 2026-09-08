using Radar.Application.Filings;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Spec 215 §1 — the deterministic verbatim verification that stands between the model's reported-metrics
/// list and the ledger. Every branch is pinned: the three drop classes, the four containment checks, the
/// truncation boundary, the blank rules and the prior-pair rule. The scan is code, not the model's word.
/// </summary>
public sealed class ReportedMetricVerifierTests
{
    private const string Body =
        "Argan, Inc. Reports Second Quarter Fiscal 2027 Results. Revenues of $384.0 million, up 68% from "
        + "$227.0 million in the prior-year quarter. Project backlog of $2.518 billion as of July 31, 2026. "
        + "Cash, cash equivalents and investments of $671.6 million.";

    private static ReportedMetricWire Wire(
        string? metric = "Revenue",
        string? value = "384.0",
        string? unit = "million",
        string? period = "second quarter of fiscal 2027",
        string? priorValue = null,
        string? priorPeriod = null,
        string? quote = "Revenues of $384.0 million, up 68% from $227.0 million in the prior-year quarter.") =>
        new(metric, value, unit, period, priorValue, priorPeriod, quote);

    [Fact]
    public void AVerbatimEntry_IsKept_AsStated_WithZeroDrops()
    {
        var result = ReportedMetricVerifier.Verify([Wire()], Body);

        var reading = Assert.Single(result.Verified);
        Assert.Equal(ReportedMetric.Revenue, reading.Metric);
        Assert.Equal("384.0", reading.Value);
        Assert.Equal("million", reading.Unit);
        Assert.Equal("second quarter of fiscal 2027", reading.Period);
        Assert.Null(reading.PriorValue);
        Assert.Null(reading.PriorPeriod);
        Assert.Equal(
            "Revenues of $384.0 million, up 68% from $227.0 million in the prior-year quarter.", reading.Quote);
        Assert.Equal(0, result.DroppedUnrecognised);
        Assert.Equal(0, result.DroppedUnverified);
        Assert.Equal(0, result.DroppedDuplicate);
        Assert.Equal(0, result.PriorPairsDroppedIncomplete);
    }

    [Fact]
    public void APriorPair_IsKept_OnlyWhenBothHalvesAreStated_AndThePriorValueIsInTheQuote()
    {
        var both = ReportedMetricVerifier.Verify(
            [Wire(priorValue: "227.0", priorPeriod: "prior-year quarter")], Body);
        var reading = Assert.Single(both.Verified);
        Assert.Equal("227.0", reading.PriorValue);
        Assert.Equal("prior-year quarter", reading.PriorPeriod);
        Assert.Equal(0, both.PriorPairsDroppedIncomplete);

        // Half a comparison is not kept: a prior value with no period cannot be placed in time. The entry
        // stays (its current value verified) and the discarded half is COUNTED, in either orientation.
        var valueOnly = ReportedMetricVerifier.Verify([Wire(priorValue: "227.0")], Body);
        var halved = Assert.Single(valueOnly.Verified);
        Assert.Null(halved.PriorValue);
        Assert.Null(halved.PriorPeriod);
        Assert.Equal(1, valueOnly.PriorPairsDroppedIncomplete);
        Assert.Equal(0, valueOnly.DroppedUnverified);

        var periodOnly = ReportedMetricVerifier.Verify([Wire(priorPeriod: "prior-year quarter")], Body);
        Assert.Single(periodOnly.Verified);
        Assert.Equal(1, periodOnly.PriorPairsDroppedIncomplete);

        // A prior value the quote does not contain fails the entry — it is NOT silently dropped to null.
        var invented = ReportedMetricVerifier.Verify(
            [Wire(priorValue: "199.0", priorPeriod: "prior-year quarter")], Body);
        Assert.Empty(invented.Verified);
        Assert.Equal(1, invented.DroppedUnverified);
    }

    [Theory]
    [InlineData("Guidance")]
    [InlineData("Ebitda")]
    [InlineData("3")]
    public void AMetricOutsideTheClosedSet_IsDroppedUnrecognised(string metric)
    {
        var result = ReportedMetricVerifier.Verify([Wire(metric: metric)], Body);

        Assert.Empty(result.Verified);
        Assert.Equal(1, result.DroppedUnrecognised);
        Assert.Equal(0, result.DroppedUnverified);
    }

    [Fact]
    public void KebabAndCaseVariants_OfAClosedMetric_StillParse()
    {
        // The shared digit-rejecting token parser accepts the enum name in any casing and kebab form.
        var result = ReportedMetricVerifier.Verify(
            [Wire(metric: "revenue"), Wire(metric: "free-cash-flow", value: "671.6", quote: "Cash, cash equivalents and investments of $671.6 million.")],
            Body);

        Assert.Equal(2, result.Verified.Count);
        Assert.Equal(ReportedMetric.Revenue, result.Verified[0].Metric);
        Assert.Equal(ReportedMetric.FreeCashFlow, result.Verified[1].Metric);
    }

    [Fact]
    public void ABlankMetricValuePeriodOrQuote_OrANullEntry_IsDroppedUnverified()
    {
        var result = ReportedMetricVerifier.Verify(
            [
                null,
                Wire(metric: " "),
                Wire(value: ""),
                Wire(period: null),
                Wire(quote: "   "),
            ],
            Body);

        Assert.Empty(result.Verified);
        Assert.Equal(5, result.DroppedUnverified);
        Assert.Equal(0, result.DroppedUnrecognised);
    }

    [Fact]
    public void AValueNotInTheQuote_IsDroppedUnverified()
    {
        var result = ReportedMetricVerifier.Verify([Wire(value: "384")], Body); // "384" is not in "$384.0"? — it IS a substring

        // "384" IS an ordinal substring of "$384.0 million", so it verifies; the digits must be absent to fail.
        Assert.Single(result.Verified);

        var absent = ReportedMetricVerifier.Verify([Wire(value: "385.0")], Body);
        Assert.Empty(absent.Verified);
        Assert.Equal(1, absent.DroppedUnverified);
    }

    [Fact]
    public void AUnitNotInTheQuote_IsDroppedUnverified_AndABlankUnitIsAllowed()
    {
        var wrongUnit = ReportedMetricVerifier.Verify([Wire(unit: "billion")], Body);
        Assert.Empty(wrongUnit.Verified);
        Assert.Equal(1, wrongUnit.DroppedUnverified);

        var noUnit = ReportedMetricVerifier.Verify([Wire(unit: "")], Body);
        var reading = Assert.Single(noUnit.Verified);
        Assert.Equal(string.Empty, reading.Unit);
    }

    [Fact]
    public void AQuoteNotInTheBody_IsDroppedUnverified()
    {
        var result = ReportedMetricVerifier.Verify(
            [Wire(quote: "Revenues of $384.0 million, a record for the company.")], Body);

        Assert.Empty(result.Verified);
        Assert.Equal(1, result.DroppedUnverified);
    }

    [Fact]
    public void AQuotePastTheTruncationCap_CannotVerify_AndIsCounted()
    {
        // The caller passes the TRUNCATED body; a quote whose text lies past the cap is not in it. The
        // release's backlog figure therefore shows up as a counted gap, never as a silent zero.
        var truncated = FilingAnalyzerPrompt.Truncate(Body, 150); // the revenue sentence ends at 138; the backlog one starts at 139
        var result = ReportedMetricVerifier.Verify(
            [
                Wire(),
                Wire(
                    metric: "Backlog",
                    value: "2.518",
                    unit: "billion",
                    period: "as of July 31, 2026",
                    quote: "Project backlog of $2.518 billion as of July 31, 2026."),
            ],
            truncated);

        var reading = Assert.Single(result.Verified);
        Assert.Equal(ReportedMetric.Revenue, reading.Metric);
        Assert.Equal(1, result.DroppedUnverified);
    }

    [Fact]
    public void WhitespaceRuns_AreCollapsed_OnBothSides_BeforeMatching()
    {
        // A line break inside a sentence of the release must not defeat a quote the model returned on one
        // line (and vice versa): the shared collapser normalises both.
        var brokenBody = Body.Replace("$384.0 million, up", "$384.0\n   million,\tup", StringComparison.Ordinal);
        var result = ReportedMetricVerifier.Verify([Wire(quote: "Revenues of  $384.0 million,\nup 68%")], brokenBody);

        var reading = Assert.Single(result.Verified);
        Assert.Equal("Revenues of $384.0 million, up 68%", reading.Quote);
    }

    [Fact]
    public void ARepeatedMetricAndPeriod_IsDroppedDuplicate_KeepingTheFirst()
    {
        var result = ReportedMetricVerifier.Verify([Wire(), Wire(value: "384")], Body);

        var reading = Assert.Single(result.Verified);
        Assert.Equal("384.0", reading.Value);
        Assert.Equal(1, result.DroppedDuplicate);
        Assert.Equal(0, result.DroppedUnverified);
    }

    [Fact]
    public void TheSameMetricForTwoPeriods_IsTwoRecords()
    {
        var result = ReportedMetricVerifier.Verify(
            [
                Wire(),
                Wire(value: "227.0", period: "prior-year quarter"),
            ],
            Body);

        Assert.Equal(2, result.Verified.Count);
        Assert.Equal(0, result.DroppedDuplicate);
    }

    [Fact]
    public void ANullList_VerifiesAsNothing_WithMeasuredZeroDrops()
    {
        var result = ReportedMetricVerifier.Verify(null, Body);

        Assert.Empty(result.Verified);
        Assert.Equal(0, result.DroppedUnrecognised);
        Assert.Equal(0, result.DroppedUnverified);
        Assert.Equal(0, result.DroppedDuplicate);
        Assert.Equal(0, result.PriorPairsDroppedIncomplete);
    }

    [Fact]
    public void TheInstruction_NamesEveryClosedMetric_AndNothingElse()
    {
        // The prompt's metric list is a compile-time constant (so the seam alias stays a const); this pins
        // it to the enum so the two cannot drift.
        var instruction = ChatFilingAnalyzer.ReportedMetricsInstruction;
        var expected = string.Join(", ", Enum.GetNames<ReportedMetric>());

        Assert.Contains("(exactly one of: " + expected + " —", instruction, StringComparison.Ordinal);
        Assert.Contains("never compute, never infer", instruction, StringComparison.Ordinal);
        Assert.Contains("the prior-period figure ONLY if the release itself states it", instruction, StringComparison.Ordinal);
    }
}
