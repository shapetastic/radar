using Radar.Application.Filings;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Spec 215 §1 as CORRECTED BY SPEC 216 §3 — the deterministic verification that stands between the
/// model's reported-metrics list and the ledger. Every branch is pinned: the drop classes, the containment
/// checks, the truncation boundary, the blank rules, the prior-pair rule, and the three things
/// <c>reported-metrics-v1</c> did NOT verify — that the quote NAMES the labelled metric, that the period
/// is the release's own wording, and that the value is a WHOLE token ASSOCIATED with the metric rather
/// than merely co-present in the same sentence. The scan is code, not the model's word.
/// </summary>
public sealed class ReportedMetricVerifierTests
{
    private const string Body =
        "Argan, Inc. Reports Second Quarter Fiscal 2027 Results. "
        + "Revenues for the second quarter of fiscal 2027 were $384.0 million, up 68% from $227.0 million "
        + "in the prior-year quarter. "
        + "Project backlog of $2.518 billion as of July 31, 2026. "
        + "Cash, cash equivalents and investments of $671.6 million as of July 31, 2026. "
        + "Free cash flow for the second quarter of fiscal 2027 was $40.2 million. "
        + "Revenues for the six months ended July 31, 2026 were $712.4 million.";

    private const string RevenueQuote =
        "Revenues for the second quarter of fiscal 2027 were $384.0 million, up 68% from $227.0 million "
        + "in the prior-year quarter.";

    private const string BacklogQuote = "Project backlog of $2.518 billion as of July 31, 2026.";

    private static ReportedMetricWire Wire(
        string? metric = "Revenue",
        string? value = "384.0",
        string? unit = "million",
        string? period = "second quarter of fiscal 2027",
        string? priorValue = null,
        string? priorPeriod = null,
        string? quote = RevenueQuote) =>
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
        Assert.Equal(RevenueQuote, reading.Quote);
        Assert.Equal(0, result.DroppedUnrecognised);
        Assert.Equal(0, result.DroppedUnverified);
        Assert.Equal(0, result.DroppedDuplicate);
        Assert.Equal(0, result.PriorPairsDroppedIncomplete);
        Assert.Equal(0, result.DroppedMetricNotInQuote);
        Assert.Equal(0, result.DroppedPeriodNotInQuote);
        Assert.Equal(0, result.DroppedFragment);
        Assert.Equal(0, result.DroppedNotAssociated);
        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.DroppedTotal);
    }

    // ---- SPEC 216 §3, rule 4: the quote must NAME the labelled metric ------------------------------

    [Fact]
    public void ACashFigureLabelledBacklog_IsDroppedMetricNotInQuote()
    {
        // THE v1 DEFECT. The metric LABEL was trusted, so a cash figure labelled Backlog verified and
        // reached the judge as a company-reported backlog — and the metric is what the projector matches on.
        var result = ReportedMetricVerifier.Verify(
            [
                Wire(
                    metric: "Backlog",
                    value: "671.6",
                    unit: "million",
                    period: "as of July 31, 2026",
                    quote: "Cash, cash equivalents and investments of $671.6 million as of July 31, 2026."),
            ],
            Body);

        Assert.Empty(result.Verified);
        Assert.Equal(1, result.DroppedMetricNotInQuote);
        Assert.Equal(0, result.DroppedUnverified);
    }

    [Theory]
    // "cash flow" is FreeCashFlow's territory: the longest phrase wins, so "cash" cannot claim it.
    [InlineData(
        "CashAndInvestments",
        "52.0",
        "Cash flow from operations for the second quarter of fiscal 2027 was $52.0 million.")]
    // "cost of sales" is an EXCLUSION: it consumes the "sales" inside it, so Revenue is not named.
    [InlineData(
        "Revenue",
        "290.0",
        "Cost of sales for the second quarter of fiscal 2027 was $290.0 million.")]
    // "gross profit" is not "gross margin".
    [InlineData(
        "GrossMargin",
        "94.0",
        "Gross profit for the second quarter of fiscal 2027 was $94.0 million.")]
    // "net debt" is an EXCLUSION: it consumes the "debt" inside it.
    [InlineData(
        "TotalDebt",
        "12.0",
        "Net debt at the second quarter of fiscal 2027 was $12.0 million.")]
    public void TheNegativeSynonymCases_DoNotVerify(string metric, string value, string quote)
    {
        // SPEC 216 §3's mandatory negative cases. Whole-PHRASE matching with the longest synonym winning,
        // never a substring of a longer phrase.
        var result = ReportedMetricVerifier.Verify(
            [Wire(metric: metric, value: value, quote: quote)], quote);

        Assert.Empty(result.Verified);
        Assert.Equal(1, result.DroppedMetricNotInQuote);
    }

    [Fact]
    public void TheSynonymTable_IsWholeWord_AndTheLongestPhraseWins()
    {
        Assert.True(ReportedMetricSynonyms.Names("Project backlog of $2.5B", ReportedMetric.Backlog));
        Assert.True(ReportedMetricSynonyms.Names("Net sales rose", ReportedMetric.Revenue));
        Assert.True(ReportedMetricSynonyms.Names("Cash and cash equivalents", ReportedMetric.CashAndInvestments));

        // Longest-wins across the WHOLE table, and the exclusions consume what they contain.
        Assert.False(ReportedMetricSynonyms.Names("Cash flow was strong", ReportedMetric.CashAndInvestments));
        Assert.True(ReportedMetricSynonyms.Names("Cash flow was strong", ReportedMetric.FreeCashFlow));
        Assert.False(ReportedMetricSynonyms.Names("Cost of sales rose", ReportedMetric.Revenue));
        Assert.False(ReportedMetricSynonyms.Names("Net debt fell", ReportedMetric.TotalDebt));
        Assert.False(ReportedMetricSynonyms.Names("Gross profit rose", ReportedMetric.GrossMargin));

        // …and a phrase never hits inside another word.
        Assert.False(ReportedMetricSynonyms.Names("Backlogged orders", ReportedMetric.Backlog));
        Assert.False(ReportedMetricSynonyms.Names("The cashier programme", ReportedMetric.CashAndInvestments));
    }

    // ---- SPEC 216 §3, rule 5: the period must be the release's own wording -------------------------

    [Fact]
    public void APeriodNotInTheQuote_IsDroppedPeriodNotInQuote()
    {
        // v1 required only a NON-BLANK period, so an invented one passed — and the period is half of the
        // ledger record's identity.
        var result = ReportedMetricVerifier.Verify([Wire(period: "Q2 FY27")], Body);

        Assert.Empty(result.Verified);
        Assert.Equal(1, result.DroppedPeriodNotInQuote);
        Assert.Equal(0, result.DroppedUnverified);
    }

    // ---- SPEC 216 §3, rule 6: whole tokens ---------------------------------------------------------

    [Fact]
    public void ANumericFragment_DoesNotVerify_InsideABiggerNumber()
    {
        // v1's `quote.Contains(value)` was a substring test, so "384" verified inside "384.0".
        var fragment = ReportedMetricVerifier.Verify([Wire(value: "384")], Body);
        Assert.Empty(fragment.Verified);
        Assert.Equal(1, fragment.DroppedFragment);

        // …and the complete token still does.
        Assert.Single(ReportedMetricVerifier.Verify([Wire(value: "384.0")], Body).Verified);

        // A thousands-separated number is one token too: "384" is not inside "3,384".
        const string Quote = "Revenues for the second quarter of fiscal 2027 were $3,384 million.";
        var grouped = ReportedMetricVerifier.Verify([Wire(value: "384", quote: Quote)], Quote);
        Assert.Empty(grouped.Verified);
        Assert.Equal(1, grouped.DroppedFragment);
        Assert.Single(ReportedMetricVerifier.Verify([Wire(value: "3,384", quote: Quote)], Quote).Verified);
    }

    [Fact]
    public void AnAbsentValueOrUnit_IsDroppedFragment_AndABlankUnitIsAllowed()
    {
        var absent = ReportedMetricVerifier.Verify([Wire(value: "385.0")], Body);
        Assert.Empty(absent.Verified);
        Assert.Equal(1, absent.DroppedFragment);

        var wrongUnit = ReportedMetricVerifier.Verify([Wire(unit: "billion")], Body);
        Assert.Empty(wrongUnit.Verified);
        Assert.Equal(1, wrongUnit.DroppedFragment);

        var noUnit = ReportedMetricVerifier.Verify([Wire(unit: "")], Body);
        var reading = Assert.Single(noUnit.Verified);
        Assert.Equal(string.Empty, reading.Unit);
    }

    [Fact]
    public void ASymbolicUnit_VerifiesBesideItsDigits()
    {
        // The whole-token rule constrains an edge only in its OWN character class: "$" is symbolic on both
        // sides, so it verifies inside "$0.79" — the rule must not make a currency unit unverifiable.
        const string Quote = "Diluted EPS for the second quarter of fiscal 2027 was $0.79.";
        var result = ReportedMetricVerifier.Verify(
            [Wire(metric: "DilutedEps", value: "0.79", unit: "$", quote: Quote)], Quote);

        var reading = Assert.Single(result.Verified);
        Assert.Equal("$", reading.Unit);
    }

    // ---- SPEC 216 §3, rule 7: association, not co-presence -----------------------------------------

    [Fact]
    public void CoPresenceInOneSentence_IsNotAssociation_TheReviewersExample()
    {
        // "cash was $100m and backlog was $2.5bn" must not let a Backlog entry with value 100 pass: the
        // value must be the NEAREST verified-shape number to the metric synonym.
        const string Quote =
            "For the second quarter of fiscal 2027 cash was $100m and backlog was $2.5bn.";

        var wrong = ReportedMetricVerifier.Verify(
            [Wire(metric: "Backlog", value: "100", unit: "", quote: Quote)], Quote);
        Assert.Empty(wrong.Verified);
        Assert.Equal(1, wrong.DroppedNotAssociated);

        // …while the figure that IS beside the metric verifies, from the very same sentence.
        var right = ReportedMetricVerifier.Verify(
            [Wire(metric: "Backlog", value: "2.5", unit: "", quote: Quote)], Quote);
        Assert.Single(right.Verified);

        var cash = ReportedMetricVerifier.Verify(
            [Wire(metric: "CashAndInvestments", value: "100", unit: "", quote: Quote)], Quote);
        Assert.Single(cash.Verified);
    }

    [Fact]
    public void ADecimalPoint_IsNeverAFragmentBoundary_SoOneSentenceStaysOneFragment()
    {
        // Splitting on every '.' would break the very values being verified. Both figures verify from ONE
        // fragment, each against its own metric.
        const string Quote =
            "Diluted EPS of $0.79 and revenues of $384.0 million for the second quarter of fiscal 2027.";

        Assert.Equal([Quote.TrimEnd('.')], ReportedMetricVerifier.Fragments(Quote));

        var result = ReportedMetricVerifier.Verify(
            [
                Wire(metric: "DilutedEps", value: "0.79", unit: "$", quote: Quote),
                Wire(metric: "Revenue", value: "384.0", unit: "million", quote: Quote),
            ],
            Quote);

        Assert.Equal(2, result.Verified.Count);
        Assert.Equal(0, result.DroppedNotAssociated);
    }

    [Fact]
    public void ATwoRowTable_SplitsOnItsRows_SoEachRowCarriesItsOwnMetric()
    {
        // Fragment boundaries apply in ORDER OF PREFERENCE: the newline is present, so the rows split on
        // newlines ONLY and a row's double-spaced cells stay together with their metric.
        const string Quote =
            "Revenue (second quarter of fiscal 2027)   $384.0 million\n"
            + "Backlog (as of July 31, 2026)   $2.518 billion";

        Assert.Equal(
            [
                "Revenue (second quarter of fiscal 2027) $384.0 million",
                "Backlog (as of July 31, 2026) $2.518 billion",
            ],
            ReportedMetricVerifier.Fragments(Quote));

        var rows = ReportedMetricVerifier.Verify(
            [
                Wire(quote: Quote),
                Wire(
                    metric: "Backlog",
                    value: "2.518",
                    unit: "billion",
                    period: "as of July 31, 2026",
                    quote: Quote),
            ],
            Quote);
        Assert.Equal(2, rows.Verified.Count);

        // …and a value from the OTHER row cannot verify this row's metric, even though both are in the quote.
        var crossed = ReportedMetricVerifier.Verify(
            [
                Wire(
                    metric: "Backlog",
                    value: "384.0",
                    unit: "million",
                    period: "as of July 31, 2026",
                    quote: Quote),
            ],
            Quote);
        Assert.Empty(crossed.Verified);
        Assert.Equal(1, crossed.DroppedNotAssociated);
    }

    [Theory]
    [InlineData("a; b", 2)]
    [InlineData("a | b", 2)]
    [InlineData("a  b", 2)]
    [InlineData("a\nb", 2)]
    [InlineData("Revenue was $384.0 million. Backlog was $2.5 billion.", 2)]
    [InlineData("Revenue was $384.0 million and backlog was $2.5 billion.", 1)]
    public void FragmentBoundaries_AreTheDeclaredSet_AndOnlyTheHighestPreferenceOneApplies(
        string quote, int expected)
    {
        Assert.Equal(expected, ReportedMetricVerifier.Fragments(quote).Count);
    }

    // ---- the pre-existing rules, unchanged ---------------------------------------------------------

    [Fact]
    public void APriorPair_IsKept_OnlyWhenBothHalvesAreStated_AndBothAreInTheQuote()
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
        Assert.Equal(1, invented.DroppedFragment);

        // The same holds for the prior PERIOD: the prompt asks for it exactly as printed, so a period the
        // quote never states fails the entry rather than persisting an invented placement in time.
        var inventedPeriod = ReportedMetricVerifier.Verify(
            [Wire(priorValue: "227.0", priorPeriod: "Q2 FY26")], Body);
        Assert.Empty(inventedPeriod.Verified);
        Assert.Equal(1, inventedPeriod.DroppedFragment);
        Assert.Equal(0, inventedPeriod.PriorPairsDroppedIncomplete);
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
            [
                Wire(metric: "revenue"),
                Wire(
                    metric: "free-cash-flow",
                    value: "40.2",
                    quote: "Free cash flow for the second quarter of fiscal 2027 was $40.2 million."),
            ],
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
    public void AQuoteNotInTheBody_IsDroppedUnverified()
    {
        var result = ReportedMetricVerifier.Verify(
            [Wire(quote: "Revenues for the second quarter of fiscal 2027 were a record for the company.")],
            Body);

        Assert.Empty(result.Verified);
        Assert.Equal(1, result.DroppedUnverified);
    }

    [Fact]
    public void AQuotePastTheTruncationCap_CannotVerify_AndIsCounted()
    {
        // The caller passes the TRUNCATED body; a quote whose text lies past the cap is not in it. The
        // release's backlog figure therefore shows up as a counted gap, never as a silent zero.
        var truncated = FilingAnalyzerPrompt.Truncate(
            Body, Body.IndexOf("Project backlog", StringComparison.Ordinal));
        var result = ReportedMetricVerifier.Verify(
            [
                Wire(),
                Wire(
                    metric: "Backlog",
                    value: "2.518",
                    unit: "billion",
                    period: "as of July 31, 2026",
                    quote: BacklogQuote),
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
        // line: the shared collapser normalises both.
        var brokenBody = Body.Replace("$384.0 million, up", "$384.0\n   million,\tup", StringComparison.Ordinal);
        var result = ReportedMetricVerifier.Verify(
            [Wire(quote: "Revenues for the second quarter of fiscal 2027 were $384.0 million, up 68%")],
            brokenBody);

        var reading = Assert.Single(result.Verified);
        Assert.Equal(
            "Revenues for the second quarter of fiscal 2027 were $384.0 million, up 68%", reading.Quote);
    }

    [Fact]
    public void ARepeatedMetricAndPeriod_IsDroppedDuplicate_KeepingTheFirst()
    {
        var result = ReportedMetricVerifier.Verify([Wire(), Wire()], Body);

        var reading = Assert.Single(result.Verified);
        Assert.Equal("384.0", reading.Value);
        Assert.Equal(1, result.DroppedDuplicate);
        Assert.Equal(0, result.DroppedUnverified);
    }

    [Fact]
    public void TheSameMetricForTwoPeriods_IsTwoRecords()
    {
        // Each period is carried by its OWN sentence, because spec 216 §3 requires the value to be the
        // nearest number to the metric it is filed under: a prior-year figure sitting further down the
        // current-quarter sentence belongs in PriorValue/PriorPeriod, not in a second record.
        var result = ReportedMetricVerifier.Verify(
            [
                Wire(),
                Wire(
                    value: "712.4",
                    period: "six months ended July 31, 2026",
                    quote: "Revenues for the six months ended July 31, 2026 were $712.4 million."),
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
        Assert.Equal(0, result.DroppedMetricNotInQuote);
        Assert.Equal(0, result.DroppedPeriodNotInQuote);
        Assert.Equal(0, result.DroppedFragment);
        Assert.Equal(0, result.DroppedNotAssociated);
        Assert.Equal(0, result.Examined);
    }

    [Fact]
    public void EveryExaminedEntry_LandsInExactlyOneClass_SoNothingIsDiscardedUncounted()
    {
        // The accounting invariant behind spec 216 §6's >50% bound: Examined partitions into Verified plus
        // the drop classes, with no entry falling between them.
        var result = ReportedMetricVerifier.Verify(
            [
                Wire(),                                                      // verified
                null,                                                        // unverified
                Wire(metric: "Ebitda"),                                      // unrecognised
                Wire(quote: "Revenues of $1.00 million were reported."),     // unverified (quote not in body)
                Wire(period: "Q2 FY27"),                                     // period not in quote
                Wire(value: "384"),                                          // fragment
                Wire(metric: "Backlog", value: "384.0"),                     // metric not in quote
                Wire(),                                                      // duplicate
            ],
            Body);

        Assert.Equal(8, result.Examined);
        Assert.Single(result.Verified);
        Assert.Equal(7, result.DroppedTotal);
        Assert.Equal(2, result.DroppedUnverified);
        Assert.Equal(1, result.DroppedUnrecognised);
        Assert.Equal(1, result.DroppedPeriodNotInQuote);
        Assert.Equal(1, result.DroppedFragment);
        Assert.Equal(1, result.DroppedMetricNotInQuote);
        Assert.Equal(1, result.DroppedDuplicate);
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
