using Radar.Application.Filings;
using Radar.Application.NewsRisk.Judgment;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 215 §2 as CORRECTED BY SPEC 216 §1 — the pure projection (<c>reference-projection-v2</c>) of a
/// company's ledger into the judge input: selection by the closed metric-phrase table under the
/// classifier's whole-word boundary rule, the STRUCTURAL eligibility rules (the newest accession per
/// metric is never a reference; its own stated prior pair is, separately; a filing later than the news is
/// excluded; only the current policy's files are read), most-recent-first ordering, the declared caps with
/// a COUNTED remainder, and the empty cases.
/// </summary>
public sealed class ReferenceValueProjectorTests
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Filed = new(2026, 9, 3, 20, 5, 0, TimeSpan.Zero);

    /// <summary>
    /// The default observation instant for a supplied family: AFTER every fixture filing date, so the
    /// spec-216 later-than-fact guard is satisfied unless a test deliberately breaks it.
    /// </summary>
    private static readonly DateTimeOffset Observed = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static ReportedMetricRecord Ledger(
        ReportedMetric metric,
        string period,
        DateTimeOffset? filed = null,
        string accession = "0001049521-26-000011",
        string? priorValue = null,
        string? priorPeriod = null,
        string? policy = null) => new(
        Id: ReportedMetricRecord.IdentityFor(accession, metric, period, policy ?? ReportedMetricsPolicy.Version),
        CompanyId: Company,
        Accession: accession,
        EvidenceId: Guid.NewGuid(),
        FilingDateUtc: filed ?? Filed,
        Form: "8-K",
        Metric: metric,
        Value: "2.518",
        Unit: "billion",
        Period: period,
        PriorValue: priorValue,
        PriorPeriod: priorPeriod,
        Quote: "Project backlog of $2.518 billion as of July 31, 2026.",
        ReaderIdentity: "openai:deepseek",
        Verification: ReportedMetricVerification.Verbatim,
        Policy: policy ?? ReportedMetricsPolicy.Version);

    private static NewsJudgmentInputFamily Family(string statement, DateTimeOffset? observedAtUtc = null) =>
        NewsJudgmentTestData.Family(statement: statement, observedAtUtc: observedAtUtc ?? Observed);

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
        // Two accessions per metric, so the OLDER one is eligible while the newest is the current value.
        var ledger = new[]
        {
            Ledger(ReportedMetric.Backlog, "as of April 30, 2026", Filed.AddMonths(-3), "0001049521-26-000005"),
            Ledger(ReportedMetric.Backlog, "as of July 31, 2026"),
            Ledger(ReportedMetric.Revenue, "first quarter of fiscal 2027", Filed.AddMonths(-3), "0001049521-26-000005"),
            Ledger(ReportedMetric.Revenue, "second quarter of fiscal 2027"),
            Ledger(ReportedMetric.TotalDebt, "as of July 31, 2026"),
        };

        var projection = ReferenceValueProjector.Project(
            [Family("Power projects lift Argan as backlog hits $2.5B"), Family("Q2 Revenue $384.0M")], ledger);

        // Only the OLDER accession's rows project; the newest accession is the current value and is
        // excluded (twice — once per named metric it reported).
        // Both eligible rows come from the SAME (older) accession, so their relative order is the
        // deterministic ReferenceId tie-break rather than anything meaningful; the SET is what matters.
        Assert.Equal(
            [
                (ReportedMetric.Revenue, "first quarter of fiscal 2027"),
                (ReportedMetric.Backlog, "as of April 30, 2026"),
            ],
            projection.References.Select(r => (r.Metric, r.Period)).OrderBy(x => x.Item1).ToList());
        Assert.Equal(2, projection.ReferencesExcludedNewest);
        Assert.Equal(0, projection.ReferenceValuesOmitted);
        Assert.Equal(0, projection.ReferencesExcludedLaterThanFact);
        Assert.Equal(0, projection.ReferencesSkippedSupersededPolicy);

        // Every projected line carries the ledger record's own id, so the judge's citation resolves to it.
        Assert.Contains(ledger[0].Id, projection.References.Select(r => r.ReferenceId));
        Assert.All(projection.References, r => Assert.Equal(NewsJudgmentReferenceKind.Prior, r.Kind));
        Assert.All(projection.References, r => Assert.Equal("8-K", r.Form));
        Assert.All(projection.References, r => Assert.Equal("2.518", r.Value));
    }

    [Fact]
    public void Project_OneAccessionOnly_ProjectsNothing_AndCountsTheExclusion()
    {
        // SPEC 216 §1, case (a) — THE DEFECT. The collection pass writes the newest release's metrics
        // BEFORE the judge runs in the same pass, so v1 handed a fact quoting "backlog $2.5B" the same
        // $2.5B from the same release as its "reference". A metric reported ONCE is never a reference:
        // that is the honest state of a young ledger.
        var projection = ReferenceValueProjector.Project(
            [Family("Power projects lift Argan as backlog hits $2.5B")],
            [Ledger(ReportedMetric.Backlog, "as of July 31, 2026")]);

        Assert.Empty(projection.References);
        Assert.Equal(1, projection.ReferencesExcludedNewest);
    }

    [Fact]
    public void Project_TheArticleTheDayAfterTheFiling_StillProjectsNothing()
    {
        // SPEC 216 §1, case (b) — the case the round-1 review named. A release routinely precedes its own
        // coverage by hours or a day, so a TIMESTAMP rule ("filing date strictly earlier than the article")
        // would have admitted the current value here. The STRUCTURAL rule does not.
        var filed = new DateTimeOffset(2026, 9, 3, 20, 5, 0, TimeSpan.Zero);
        var projection = ReferenceValueProjector.Project(
            [Family("backlog hits $2.5B", filed.AddDays(1))],
            [Ledger(ReportedMetric.Backlog, "as of July 31, 2026", filed)]);

        Assert.Empty(projection.References);
        Assert.Equal(1, projection.ReferencesExcludedNewest);
    }

    [Fact]
    public void Project_TwoAccessions_ProjectOnlyTheOlder_WithSameDayOrderingByAccession()
    {
        // SPEC 216 §1, case (c). Same-day filings are ordered by ACCESSION (ordinal, which is chronological
        // within a filer's sequence), so "the newest" is deterministic without a clock.
        var sameDay = new[]
        {
            Ledger(ReportedMetric.Backlog, "as of April 30, 2026", Filed, "0001049521-26-000005"),
            Ledger(ReportedMetric.Backlog, "as of July 31, 2026", Filed, "0001049521-26-000011"),
        };

        var projection = ReferenceValueProjector.Project([Family("backlog hits $2.5B")], sameDay);

        var reference = Assert.Single(projection.References);
        Assert.Equal("as of April 30, 2026", reference.Period);
        Assert.Equal(1, projection.ReferencesExcludedNewest);
    }

    [Fact]
    public void Project_AStatedPriorPairFromTheNewestFiling_ProjectsAsItsOwnReference()
    {
        // SPEC 216 §1, case (d). The newest record's own VERIFIED prior pair is the company's own prior
        // statement — by construction not the current value — so it projects, as StatedPrior, with its own
        // ReferenceId and the PRIOR figure as its value.
        var newest = Ledger(
            ReportedMetric.Backlog,
            "as of July 31, 2026",
            priorValue: "2.929",
            priorPeriod: "as of January 31, 2026");

        var projection = ReferenceValueProjector.Project([Family("backlog hits $2.5B")], [newest]);

        var reference = Assert.Single(projection.References);
        Assert.Equal(NewsJudgmentReferenceKind.StatedPrior, reference.Kind);
        Assert.Equal(newest.StatedPriorIdentity, reference.ReferenceId);
        Assert.NotEqual(newest.Id, reference.ReferenceId);
        Assert.Equal("2.929", reference.Value);
        Assert.Equal("as of January 31, 2026", reference.Period);
        Assert.Null(reference.PriorValue);
        Assert.Null(reference.PriorPeriod);

        // The current value is still excluded and counted — the pair is projected BESIDE it, not instead.
        Assert.Equal(1, projection.ReferencesExcludedNewest);

        // A HALF-stated pair is never a reference.
        Assert.Empty(
            ReferenceValueProjector.Project(
                [Family("backlog hits $2.5B")],
                [Ledger(ReportedMetric.Backlog, "as of July 31, 2026", priorValue: "2.929")]).References);
    }

    [Fact]
    public void Project_TwoRowsOfOneReleaseSharingAPriorPeriod_AreTwoDISTINCTStatedPriorReferences()
    {
        // The stated-prior identity must be INJECTIVE over records. One release routinely states a metric
        // for two periods against the SAME stated prior period ("the quarter" and "the six months", both
        // "compared with the prior-year period"): both rows belong to the newest accession, so both
        // project their own StatedPrior reference. Keyed on (policy, accession, metric, PRIOR period) those
        // two collapse onto ONE ReferenceId — two figures under one id, which the id-keyed lookups in
        // NewsJudgmentValidator/NewsJudgmentGenerator throw on, and which would leave "which figure was
        // supplied" unrecoverable from the persisted ReferenceIds. Keyed on the record's own Id they cannot.
        var quarter = Ledger(
            ReportedMetric.Revenue,
            "second quarter of fiscal 2027",
            priorValue: "227.0",
            priorPeriod: "prior-year period");
        var halfYear = Ledger(
            ReportedMetric.Revenue,
            "first six months of fiscal 2027",
            priorValue: "441.0",
            priorPeriod: "prior-year period");

        Assert.NotEqual(quarter.Id, halfYear.Id);
        Assert.Equal(quarter.Accession, halfYear.Accession);
        Assert.Equal(quarter.PriorPeriod, halfYear.PriorPeriod);

        var projection = ReferenceValueProjector.Project(
            [Family("Revenues of $384.0 million")], [quarter, halfYear]);

        Assert.Equal(2, projection.References.Count);
        Assert.All(
            projection.References, r => Assert.Equal(NewsJudgmentReferenceKind.StatedPrior, r.Kind));
        Assert.Equal(2, projection.References.Select(r => r.ReferenceId).Distinct().Count());
        Assert.Contains(projection.References, r => r.ReferenceId == quarter.StatedPriorIdentity);
        Assert.Contains(projection.References, r => r.ReferenceId == halfYear.StatedPriorIdentity);

        // …and the id-keyed lookup the collision would have thrown in now succeeds.
        Assert.Equal(2, projection.References.ToDictionary(r => r.ReferenceId).Count);
    }

    [Fact]
    public void Project_AFilingLaterThanTheFact_IsExcluded_AndCounted()
    {
        // SPEC 216 §1, case (e) — the secondary guard: a newer filing may never be compared BACKWARDS
        // against older news. Two accessions, so the older one would otherwise be eligible.
        var news = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var ledger = new[]
        {
            Ledger(ReportedMetric.Backlog, "as of April 30, 2026", news.AddDays(1), "0001049521-26-000005"),
            Ledger(ReportedMetric.Backlog, "as of July 31, 2026", news.AddDays(60), "0001049521-26-000011"),
        };

        var projection = ReferenceValueProjector.Project([Family("backlog hits $2.5B", news)], ledger);

        Assert.Empty(projection.References);
        Assert.Equal(1, projection.ReferencesExcludedNewest);
        Assert.Equal(1, projection.ReferencesExcludedLaterThanFact);

        // A family whose observation instant is NOT RECORDED can exclude nothing — null never fabricates a
        // comparison.
        var unrecorded = ReferenceValueProjector.Project(
            [Family("backlog hits $2.5B") with { ObservedAtUtc = null }], ledger);
        Assert.Single(unrecorded.References);
        Assert.Equal(0, unrecorded.ReferencesExcludedLaterThanFact);
    }

    [Fact]
    public void Project_ReadsOnlyTheCurrentPolicysFiles_AndCountsTheSupersededOnes()
    {
        // SPEC 216 §5 — a superseded-policy record was admitted by a verification rule no longer in force;
        // mixing it in would put a figure the current policy would have REJECTED in front of the judge
        // under the current policy's name. It is skipped and COUNTED, never deleted.
        var ledger = new[]
        {
            Ledger(ReportedMetric.Backlog, "as of April 30, 2026", Filed.AddMonths(-3), "0001049521-26-000005"),
            Ledger(ReportedMetric.Backlog, "as of July 31, 2026"),
            Ledger(
                ReportedMetric.Backlog,
                "as of January 31, 2026",
                Filed.AddMonths(-6),
                "0001049521-26-000001",
                policy: "reported-metrics-v1"),
        };

        var projection = ReferenceValueProjector.Project([Family("backlog hits $2.5B")], ledger);

        var reference = Assert.Single(projection.References);
        Assert.Equal("as of April 30, 2026", reference.Period);
        Assert.Equal(1, projection.ReferencesSkippedSupersededPolicy);
    }

    [Fact]
    public void Project_CapsPerMetricAndPerJudgment_AndCountsTheRemainder()
    {
        // Seven accessions ⇒ the newest is the current value and SIX are eligible, of which the cap admits
        // MaxPerMetric and counts the rest.
        var backlogs = Enumerable.Range(0, 7)
            .Select(i => Ledger(
                ReportedMetric.Backlog, $"as of period {i}", Filed.AddMonths(-i), $"0001049521-26-00{i:00}1"))
            .ToList();
        var perMetric = ReferenceValueProjector.Project([Family("backlog hits $2.5B")], backlogs);
        Assert.Equal(ReferenceValueProjector.MaxPerMetric, perMetric.References.Count);
        Assert.Equal(6 - ReferenceValueProjector.MaxPerMetric, perMetric.ReferenceValuesOmitted);
        Assert.Equal(1, perMetric.ReferencesExcludedNewest);
        Assert.Equal("as of period 1", perMetric.References[0].Period); // most recent ELIGIBLE first

        // Every metric named, five accessions each ⇒ 9×4 = 36 eligible, 16 admitted, 20 counted.
        var everything = Enum.GetValues<ReportedMetric>()
            .SelectMany(m => Enumerable.Range(0, 5)
                .Select(i => Ledger(m, $"period {i}", Filed.AddDays(-i), $"0001049521-26-0{(int)m:00}{i}1")))
            .ToList();
        var statement = string.Join(
            " ",
            ReferenceValueProjector.MetricPhrases.Values.Select(p => p[0]));
        var perJudgment = ReferenceValueProjector.Project([Family(statement)], everything);
        Assert.Equal(ReferenceValueProjector.MaxPerJudgment, perJudgment.References.Count);
        Assert.Equal(9, perJudgment.ReferencesExcludedNewest);
        Assert.Equal(36 - ReferenceValueProjector.MaxPerJudgment, perJudgment.ReferenceValuesOmitted);
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
    public void TheVersionToken_IsV2_AndJoinsTheJudgmentCohortKeyAfterComparison()
    {
        // SPEC 216 §1 moved it: the eligibility rules are part of the projection's identity, so a v1 and a
        // v2 judge do not see the same references and must not share a cohort.
        Assert.Equal("reference-projection-v2", ReferenceValueProjector.Version);
        var key = NewsJudgmentContract.CohortKey("openai", "judge-model", "stage1-key");
        Assert.EndsWith(
            "|comparison=" + StatementComparisonClassifier.Version + "|references=reference-projection-v2",
            key,
            StringComparison.Ordinal);
    }
}
