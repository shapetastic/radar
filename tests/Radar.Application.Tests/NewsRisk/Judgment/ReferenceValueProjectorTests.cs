using Radar.Application.Filings;
using Radar.Application.NewsRisk.Judgment;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 215 §2 — the pure projection (<c>reference-projection-v1</c>) of a company's ledger into the judge
/// input: selection by the closed metric-phrase table under the classifier's whole-word boundary rule,
/// most-recent-first ordering, the declared caps with a COUNTED remainder, and the empty cases.
/// </summary>
public sealed class ReferenceValueProjectorTests
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Filed = new(2026, 9, 3, 20, 5, 0, TimeSpan.Zero);

    private static ReportedMetricRecord Ledger(
        ReportedMetric metric,
        string period,
        DateTimeOffset? filed = null,
        string accession = "0001049521-26-000011") => new(
        Id: ReportedMetricRecord.IdentityFor(accession, metric, period),
        CompanyId: Company,
        Accession: accession,
        EvidenceId: Guid.NewGuid(),
        FilingDateUtc: filed ?? Filed,
        Form: "8-K",
        Metric: metric,
        Value: "2.518",
        Unit: "billion",
        Period: period,
        PriorValue: null,
        PriorPeriod: null,
        Quote: "Project backlog of $2.518 billion as of July 31, 2026.",
        ReaderIdentity: "openai:deepseek",
        Verification: ReportedMetricVerification.Verbatim,
        Policy: ReportedMetricsPolicy.Version);

    private static NewsJudgmentInputFamily Family(string statement) =>
        NewsJudgmentTestData.Family(statement: statement);

    [Theory]
    [InlineData("Power projects lift Argan as backlog hits $2.5B", ReportedMetric.Backlog)]
    [InlineData("Order book reached a new level", ReportedMetric.Backlog)]
    [InlineData("Q2 Revenue $384.0M", ReportedMetric.Revenue)]
    [InlineData("Sales came in at $384M", ReportedMetric.Revenue)]
    [InlineData("Net loss narrowed", ReportedMetric.NetIncome)]
    [InlineData("Beats Expectations By $1.12 EPS", ReportedMetric.DilutedEps)]
    [InlineData("Gross margin was 24%", ReportedMetric.GrossMargin)]
    [InlineData("Operating income of $12M", ReportedMetric.OperatingIncome)]
    [InlineData("Cash and cash equivalents of $671.6M", ReportedMetric.CashAndInvestments)]
    [InlineData("Net debt fell", ReportedMetric.TotalDebt)]
    [InlineData("Free cash flow of $40M", ReportedMetric.FreeCashFlow)]
    public void NamesMetric_MatchesTheClosedTable_AsWholeWords(string statement, ReportedMetric metric)
    {
        Assert.True(ReferenceValueProjector.NamesMetric(statement, metric));
    }

    [Theory]
    [InlineData("Backlogged orders are being cleared", ReportedMetric.Backlog)] // "backlogged" is not "backlog"
    [InlineData("The cashier program launched", ReportedMetric.CashAndInvestments)]
    [InlineData("A debt-free balance sheet", ReportedMetric.TotalDebt)] // hyphenated compound is one token
    [InlineData("Revenues of $384.0 million", ReportedMetric.Backlog)]
    [InlineData("", ReportedMetric.Revenue)]
    public void NamesMetric_NeverHitsInsideAnotherWordOrCompound(string statement, ReportedMetric metric)
    {
        Assert.False(ReferenceValueProjector.NamesMetric(statement, metric));
    }

    [Fact]
    public void MetricsNamedIn_ReturnsEveryMetricInEnumOrder()
    {
        Assert.Equal(
            [ReportedMetric.Revenue, ReportedMetric.NetIncome, ReportedMetric.Backlog],
            ReferenceValueProjector.MetricsNamedIn("Record revenue, net income and backlog"));
        Assert.Empty(ReferenceValueProjector.MetricsNamedIn("The company will present at a conference"));
        Assert.Empty(ReferenceValueProjector.MetricsNamedIn(null));
    }

    [Fact]
    public void TheTable_CoversEveryClosedMetric()
    {
        Assert.Equal(
            Enum.GetValues<ReportedMetric>().Order().ToArray(),
            ReferenceValueProjector.MetricPhrases.Keys.Order().ToArray());
        Assert.All(ReferenceValueProjector.MetricPhrases.Values, phrases => Assert.NotEmpty(phrases));
    }

    [Fact]
    public void Project_SelectsOnlyMetricsNamedInASuppliedStatement_MostRecentFirst()
    {
        var ledger = new[]
        {
            Ledger(ReportedMetric.Backlog, "as of April 30, 2026", Filed.AddMonths(-3), "0001049521-26-000005"),
            Ledger(ReportedMetric.Backlog, "as of July 31, 2026"),
            Ledger(ReportedMetric.Revenue, "second quarter of fiscal 2027"),
            Ledger(ReportedMetric.TotalDebt, "as of July 31, 2026"),
        };

        var projection = ReferenceValueProjector.Project(
            [Family("Power projects lift Argan as backlog hits $2.5B"), Family("Q2 Revenue $384.0M")], ledger);

        Assert.Equal(
            [
                (ReportedMetric.Backlog, "as of July 31, 2026"),
                (ReportedMetric.Revenue, "second quarter of fiscal 2027"),
                (ReportedMetric.Backlog, "as of April 30, 2026"),
            ],
            projection.References.Select(r => (r.Metric, r.Period)).ToList());
        Assert.Equal(0, projection.ReferenceValuesOmitted);
        // Every projected line carries the ledger record's own id, so the judge's citation resolves to it.
        Assert.Equal(ledger[1].Id, projection.References[0].ReferenceId);
        Assert.Equal("8-K", projection.References[0].Form);
        Assert.Equal("2.518", projection.References[0].Value);
    }

    [Fact]
    public void Project_CapsPerMetricAndPerJudgment_AndCountsTheRemainder()
    {
        var backlogs = Enumerable.Range(0, 6)
            .Select(i => Ledger(
                ReportedMetric.Backlog, $"as of period {i}", Filed.AddMonths(-i), $"0001049521-26-00{i:00}1"))
            .ToList();
        var perMetric = ReferenceValueProjector.Project([Family("backlog hits $2.5B")], backlogs);
        Assert.Equal(ReferenceValueProjector.MaxPerMetric, perMetric.References.Count);
        Assert.Equal(6 - ReferenceValueProjector.MaxPerMetric, perMetric.ReferenceValuesOmitted);
        Assert.Equal("as of period 0", perMetric.References[0].Period); // most recent first

        // Every metric named, four each ⇒ 36 candidates, 16 admitted, 20 counted.
        var everything = Enum.GetValues<ReportedMetric>()
            .SelectMany(m => Enumerable.Range(0, 4)
                .Select(i => Ledger(m, $"period {i}", Filed.AddDays(-i), $"0001049521-26-0{(int)m:00}{i}1")))
            .ToList();
        var statement = string.Join(
            " ",
            ReferenceValueProjector.MetricPhrases.Values.Select(p => p[0]));
        var perJudgment = ReferenceValueProjector.Project([Family(statement)], everything);
        Assert.Equal(ReferenceValueProjector.MaxPerJudgment, perJudgment.References.Count);
        Assert.Equal(everything.Count - ReferenceValueProjector.MaxPerJudgment, perJudgment.ReferenceValuesOmitted);
    }

    [Fact]
    public void Project_IsEmpty_ForAnEmptyLedger_OrStatementsNamingNoLedgerMetric()
    {
        Assert.Same(
            ReferenceValueProjection.Empty,
            ReferenceValueProjector.Project([Family("backlog hits $2.5B")], []));

        var projection = ReferenceValueProjector.Project(
            [Family("The company will present at a conference")], [Ledger(ReportedMetric.Backlog, "as of July 31, 2026")]);
        Assert.Empty(projection.References);
        Assert.Equal(0, projection.ReferenceValuesOmitted);
    }

    [Fact]
    public void TheVersionToken_IsV1_AndJoinsTheJudgmentCohortKeyAfterComparison()
    {
        Assert.Equal("reference-projection-v1", ReferenceValueProjector.Version);
        var key = NewsJudgmentContract.CohortKey("openai", "judge-model", "stage1-key");
        Assert.EndsWith(
            "|comparison=" + StatementComparisonClassifier.Version + "|references=reference-projection-v1",
            key,
            StringComparison.Ordinal);
    }
}
