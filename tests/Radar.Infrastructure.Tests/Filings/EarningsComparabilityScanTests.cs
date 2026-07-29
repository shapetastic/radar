using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Unit coverage for the spec-160 deterministic comparability scan: verbatim, case-insensitive,
/// whitespace-normalised phrase containment over two fixed tables (cap-triggering vs diagnostic-only), with a
/// versioned rule-structure identity and a canonical policy string.
/// </summary>
public sealed class EarningsComparabilityScanTests
{
    [Fact]
    public void Version_IsPinned()
    {
        // The scan's rule-STRUCTURE identity (parallel to KeywordSignalExtractor.RuleSetVersion). It is folded
        // into the scoring fingerprint (cmpscan=) and into every cache record's policy string, so changing
        // either phrase table MUST bump it — this pin makes a table edit without a bump a conscious failure.
        Assert.Equal("cmpscan-v1", EarningsComparabilityScan.Version);
    }

    [Fact]
    public void Policy_ComposesVersionAndCap_InvariantG29()
    {
        Assert.Equal("cmpscan-v1;cap=0.65", EarningsComparabilityScan.Policy(0.65m));
        Assert.Equal("cmpscan-v1;cap=0.5", EarningsComparabilityScan.Policy(0.5m));
        Assert.Equal("cmpscan-v1;cap=1", EarningsComparabilityScan.Policy(1.0m));
    }

    [Fact]
    public void Scan_CapTriggeringPhrases_AreReturnedInTableOrder_Distinct()
    {
        // Deliberately out of table order in the text; the result is ordered by the scanner's table, and a
        // phrase appearing twice in the text is reported once.
        var markers = EarningsComparabilityScan.Scan(
            "A bad debt recovery, an impairment charge, another impairment, and a litigation settlement.");

        Assert.Equal(["impairment", "litigation settlement", "bad debt recovery"], markers.CapTriggering);
        Assert.Empty(markers.DiagnosticOnly);
    }

    [Fact]
    public void Scan_IsCaseInsensitive()
    {
        var markers = EarningsComparabilityScan.Scan("DISCONTINUED OPERATIONS and a Gain On Sale of assets.");

        Assert.Equal(["discontinued operations", "gain on sale"], markers.CapTriggering);
    }

    [Fact]
    public void Scan_NormalizesWhitespace_PhraseMatchesAcrossLineBreaksAndRuns()
    {
        // The stripped HTML body can break a phrase across newlines/tabs/multiple spaces — the scan collapses
        // every whitespace run to a single space before matching.
        var markers = EarningsComparabilityScan.Scan("results reflect a litigation\r\n\t   settlement payment");

        Assert.Equal(["litigation settlement"], markers.CapTriggering);
    }

    [Fact]
    public void Scan_HyphenIsNotWhitespace_OneTimeVariantsAreSeparatePhrases()
    {
        // "one-time" and "one time" are both in the table precisely because a hyphen is NOT whitespace and is
        // not normalised away — each spelling matches only its own entry.
        Assert.Equal(["one-time"], EarningsComparabilityScan.Scan("a one-time gain").CapTriggering);
        Assert.Equal(["one time"], EarningsComparabilityScan.Scan("a one time gain").CapTriggering);
    }

    [Fact]
    public void Scan_DiagnosticOnlyPhrases_AreRecordedSeparately_AndNeverInCapList()
    {
        var markers = EarningsComparabilityScan.Scan(
            "Income from continuing operations rose after the company sold its distribution arm; "
                + "the sale of its warehouse and the sale of the fleet closed in June.");

        Assert.Empty(markers.CapTriggering);
        Assert.Equal(
            ["continuing operations", "sale of its", "sale of the", "sold its"],
            markers.DiagnosticOnly);
    }

    [Fact]
    public void Scan_CleanText_ReturnsTwoEmptyLists()
    {
        var markers = EarningsComparabilityScan.Scan(
            "Revenue rose 40% on strong demand and the company raised full-year guidance.");

        Assert.Empty(markers.CapTriggering);
        Assert.Empty(markers.DiagnosticOnly);
    }

    [Fact]
    public void Scan_NonGaapAndAdjusted_AreDeliberatelyNotMarkers()
    {
        // Essentially every release contains non-GAAP reconciliation boilerplate; treating it as a marker
        // would cap everything (a constant re-scaling of every AI read is a Strength edit wearing a costume).
        var markers = EarningsComparabilityScan.Scan(
            "A reconciliation of non-GAAP measures is provided; adjusted EPS was $1.10.");

        Assert.Empty(markers.CapTriggering);
        Assert.Empty(markers.DiagnosticOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Scan_NullOrBlankBody_ReturnsTwoEmptyLists(string? body)
    {
        var markers = EarningsComparabilityScan.Scan(body);

        Assert.Empty(markers.CapTriggering);
        Assert.Empty(markers.DiagnosticOnly);
    }

    [Fact]
    public void Scan_SingularAndPluralSecuritiesLoss_BothReport_WhenPluralPresent()
    {
        // "securities losses" contains "securities loss", so the plural text reports BOTH table entries —
        // verbatim containment, honestly reported (ordered, distinct; no de-overlapping cleverness).
        var markers = EarningsComparabilityScan.Scan("the quarter absorbed securities losses of $3.6 million");

        Assert.Equal(["securities loss", "securities losses"], markers.CapTriggering);
    }
}
