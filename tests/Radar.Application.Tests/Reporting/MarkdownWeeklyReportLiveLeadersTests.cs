using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Scoring;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 176 — the compact <c>## Live strategy leaders</c> section at the renderer boundary. It is a second
/// RENDERING of the first five spec-150 rows per strategy (never a second construction), rendered after the
/// standing disclaimers and before <c>## Highest opportunity</c>, grouped by the CARRIED
/// <see cref="StrategyPurpose"/> — never by name inference — with the pinned honesty wording and the exact
/// per-row <c>WindowEndUtc</c> scoring cutoff.
/// </summary>
public sealed class MarkdownWeeklyReportLiveLeadersTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 6, 7, 22, 15, 0, TimeSpan.Zero);

    // Deliberately a DIFFERENT instant (date AND time) from WindowEnd, so a renderer that accidentally
    // formatted CreatedAtUtc would produce a visibly different string and fail the cutoff assertions.
    private static readonly DateTimeOffset CreatedAt = new(2026, 6, 8, 3, 40, 0, TimeSpan.Zero);

    private static CompanyScoreSnapshot Snapshot(
        Guid id,
        Guid companyId,
        int opportunity = 70,
        DateTimeOffset? windowEndUtc = null) =>
        new(
            Id: id,
            CompanyId: companyId,
            ScoringVersion: "radar-formula-v8",
            TrajectoryScore: 60,
            OpportunityScore: opportunity,
            AttentionScore: 20,
            EvidenceConfidenceScore: 80,
            SignalVelocityScore: 50,
            Explanation: "Deterministic explanation.",
            ComponentJson: "{}",
            WindowStartUtc: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            WindowEndUtc: windowEndUtc ?? WindowEnd,
            CreatedAtUtc: CreatedAt);

    private static StrategyReportRow Row(
        int rank, string company, string? ticker, CompanyScoreSnapshot snapshot) =>
        new(rank, snapshot.CompanyId, company, ticker, snapshot.Id, snapshot);

    private static StrategyReportSection Section(
        string name,
        bool isPrimary,
        IReadOnlyList<StrategyReportRow> rows,
        StrategyPurpose purpose = StrategyPurpose.Research) =>
        new(
            StrategyName: name,
            FormulaVersion: "radar-formula-v8",
            ScoringConfigVersion: "radar-scoring-fp-aaaaaaaaaaaa",
            IsPrimary: isPrimary,
            CompaniesScored: rows.Count,
            CompaniesWithLinkedEvidence: rows.Count,
            Rows: rows)
        {
            Purpose = purpose,
        };

    private static IReadOnlyList<StrategyReportRow> Rows(int count, int topOpportunity = 90)
    {
        var rows = new List<StrategyReportRow>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(Row(i + 1, $"Company {i + 1}", "TIC",
                Snapshot(Guid.NewGuid(), Guid.NewGuid(), opportunity: topOpportunity - i)));
        }

        return rows;
    }

    private static string Render(params StrategyReportSection[] sections) =>
        new MarkdownWeeklyReportRenderer().Render(
            MarkdownWeeklyReportGoldenModel.Create([.. sections]));

    /// <summary>The rendered live-leaders section (up to the first pre-existing "##" heading after it).</summary>
    private static string LiveSectionOf(string markdown)
    {
        var start = markdown.IndexOf("## Live strategy leaders", StringComparison.Ordinal);
        Assert.True(start >= 0, "The live strategy leaders section must render.");
        var end = markdown.IndexOf("## Highest opportunity", StringComparison.Ordinal);
        Assert.True(end > start, "Highest opportunity must follow the live summary.");
        return markdown[start..end];
    }

    [Fact]
    public void RendersAfterTheDisclaimers_AndBeforeHighestOpportunity()
    {
        var markdown = Render(
            Section("default", isPrimary: true, Rows(1)),
            Section("filings-led", isPrimary: false, Rows(1)));

        var disclaimers = markdown.IndexOf("> Human review required.", StringComparison.Ordinal);
        var live = markdown.IndexOf("## Live strategy leaders", StringComparison.Ordinal);
        var highest = markdown.IndexOf("## Highest opportunity", StringComparison.Ordinal);

        Assert.True(disclaimers >= 0 && live > disclaimers,
            "The live summary renders after the standing disclaimers.");
        Assert.True(highest > live, "The live summary renders before Highest opportunity.");

        // The full spec-150 tables stay in their existing location, AFTER all existing content.
        var fullTables = markdown.IndexOf("## Strategy: default", StringComparison.Ordinal);
        var recentRuns = markdown.IndexOf("## Recent runs", StringComparison.Ordinal);
        Assert.True(fullTables > recentRuns, "The spec-150 tables stay appended after existing content.");
    }

    [Fact]
    public void NullStrategies_RendersNoLiveSection_AndStaysByteIdenticalToTheGoldenPin()
    {
        // The single-strategy report (Strategies == null) must not change AT ALL — the whole-document
        // byte pin lives in MarkdownWeeklyReportStrategySectionTests.PreSpec150Golden and must still pass
        // unmodified; this test pins the specific absence.
        var markdown = new MarkdownWeeklyReportRenderer().Render(
            MarkdownWeeklyReportGoldenModel.Create(strategies: null));

        Assert.DoesNotContain("## Live strategy leaders", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("### Research arms", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ResearchAndComparators_GroupByCarriedPurpose_OneCombinedTablePerSubsection()
    {
        var markdown = Render(
            Section("default", isPrimary: true, Rows(1)),
            Section("baseline-activity-only", isPrimary: false, Rows(1),
                purpose: StrategyPurpose.Comparator),
            Section("filings-led", isPrimary: false, Rows(1)),
            Section("disclosure-led-v10-control", isPrimary: false, Rows(1),
                purpose: StrategyPurpose.Comparator));

        var live = LiveSectionOf(markdown);

        var research = live.IndexOf("### Research arms", StringComparison.Ordinal);
        var comparators = live.IndexOf("### Comparators — diagnostic only", StringComparison.Ordinal);
        Assert.True(research >= 0 && comparators > research,
            "Research arms render first, comparators second.");

        // Exactly one combined table per subsection: two header lines in the whole live section.
        Assert.Equal(
            2,
            live.Split("| strategy | rank | company | ticker | Opportunity | as-of UTC |").Length - 1);

        // Comparators land in the comparator table (after its heading), in configured order.
        var comparatorPart = live[comparators..];
        var baselineIdx = comparatorPart.IndexOf("| baseline-activity-only |", StringComparison.Ordinal);
        var controlIdx = comparatorPart.IndexOf("| disclosure-led-v10-control |", StringComparison.Ordinal);
        Assert.True(baselineIdx >= 0 && controlIdx > baselineIdx,
            "Comparator strategies render in configured order.");
        // …and NOT in the research table.
        var researchPart = live[research..comparators];
        Assert.DoesNotContain("baseline-activity-only", researchPart, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryRendersFirstInTheResearchTable_AndIsLabelledPrimaryResearch()
    {
        // The primary is deliberately NOT first in the model here: the renderer's stable partition must
        // put it first regardless, and label it so a reader can tell which arm owns the narrative below.
        var markdown = Render(
            Section("filings-led", isPrimary: false, Rows(1)),
            Section("default", isPrimary: true, Rows(1)));

        var live = LiveSectionOf(markdown);
        var primaryIdx = live.IndexOf("| default (primary research) | 1 |", StringComparison.Ordinal);
        var otherIdx = live.IndexOf("| filings-led | 1 |", StringComparison.Ordinal);
        Assert.True(primaryIdx >= 0 && otherIdx > primaryIdx, "The primary renders first.");
    }

    [Fact]
    public void AtMostFiveRowsPerStrategy_AndFewerRowsAreNeverPadded()
    {
        var markdown = Render(
            Section("default", isPrimary: true, Rows(7)),
            Section("filings-led", isPrimary: false, Rows(2, topOpportunity: 40)));

        var live = LiveSectionOf(markdown);

        // Seven ranked rows exist on the section; the live table shows exactly the first five.
        Assert.Contains("| default (primary research) | 5 |", live, StringComparison.Ordinal);
        Assert.DoesNotContain("| default (primary research) | 6 |", live, StringComparison.Ordinal);
        Assert.DoesNotContain("| default (primary research) | 7 |", live, StringComparison.Ordinal);

        // Two rows render two rows — nothing is manufactured to reach five.
        Assert.Contains("| filings-led | 2 |", live, StringComparison.Ordinal);
        Assert.DoesNotContain("| filings-led | 3 |", live, StringComparison.Ordinal);

        // The FULL spec-150 table below still shows all seven rows (the cap is presentation-only, scoped
        // to the compact summary).
        var fullTables = markdown[markdown.IndexOf("## Strategy: default", StringComparison.Ordinal)..];
        Assert.Contains("| 7 | Company 7 |", fullTables, StringComparison.Ordinal);
    }

    [Fact]
    public void AsOfColumn_RendersTheSnapshotsExactWindowEndUtc_NeverCreatedAtUtc()
    {
        var markdown = Render(
            Section("default", isPrimary: true,
                [Row(1, "Acme Dynamics", "AEHR", Snapshot(Guid.NewGuid(), Guid.NewGuid(), 29))]),
            Section("filings-led", isPrimary: false,
                [
                    Row(1, "Borealis Systems", "BOR", Snapshot(
                        Guid.NewGuid(), Guid.NewGuid(), 55,
                        windowEndUtc: new DateTimeOffset(2026, 6, 6, 8, 5, 0, TimeSpan.Zero))),
                ]));

        var live = LiveSectionOf(markdown);

        // Exact instant, including the TIME, formatted invariantly as yyyy-MM-dd HH:mmZ.
        Assert.Contains(
            "| default (primary research) | 1 | Acme Dynamics | AEHR | 29 | 2026-06-07 22:15Z |",
            live, StringComparison.Ordinal);
        // Two rows with different knowledge cutoffs are visibly different — never one synchronized table.
        Assert.Contains(
            "| filings-led | 1 | Borealis Systems | BOR | 55 | 2026-06-06 08:05Z |",
            live, StringComparison.Ordinal);
        // A CreatedAtUtc substitute (2026-06-08 03:40) must be caught.
        Assert.DoesNotContain("2026-06-08 03:40Z", live, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArm_IsRetainedWithTheExplicitEmptyMessage_NeverOmitted()
    {
        var markdown = Render(
            Section("default", isPrimary: true, Rows(1)),
            Section("filings-led", isPrimary: false, []));

        Assert.Contains(
            "| filings-led | — | No evidence-linked live scores in this report window. | — | — | — |",
            LiveSectionOf(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void HonestyWording_IsPinnedVerbatim_InItsSubsection()
    {
        var markdown = Render(
            Section("default", isPrimary: true, Rows(1)),
            Section("baseline-activity-only", isPrimary: false, Rows(1),
                purpose: StrategyPurpose.Comparator));

        var live = LiveSectionOf(markdown);

        const string NoForwardPrice =
            "Live scores are shown immediately and are never gated on a future price. Forward outcomes are "
                + "required only to evaluate the strategy later; these rankings are not efficacy results.";
        const string NoCrossStrategy =
            "Scores and score magnitudes are comparable only within the same strategy. Repeated company "
                + "names across arms are not a consensus signal.";
        const string ComparatorCaveat =
            "Comparators are displayed to diagnose what the research arms may merely be reproducing. A "
                + "comparator leader is not a Radar candidate.";

        Assert.Contains(NoForwardPrice, live, StringComparison.Ordinal);
        Assert.Contains(NoCrossStrategy, live, StringComparison.Ordinal);
        Assert.Contains(ComparatorCaveat, live, StringComparison.Ordinal);

        // The first two sit inside the RESEARCH subsection; the caveat inside the COMPARATOR one.
        var research = live.IndexOf("### Research arms", StringComparison.Ordinal);
        var comparators = live.IndexOf("### Comparators — diagnostic only", StringComparison.Ordinal);
        Assert.InRange(live.IndexOf(NoForwardPrice, StringComparison.Ordinal), research, comparators);
        Assert.InRange(live.IndexOf(NoCrossStrategy, StringComparison.Ordinal), research, comparators);
        Assert.True(live.IndexOf(ComparatorCaveat, StringComparison.Ordinal) > comparators);
    }

    [Fact]
    public void NoComparators_OmitsTheComparatorSubsectionEntirely()
    {
        // An all-Research configuration (every deployment that never marks a comparator) renders no empty
        // comparator shell — "no comparators configured" is not "every comparator arm was empty".
        var markdown = Render(
            Section("default", isPrimary: true, Rows(1)),
            Section("filings-led", isPrimary: false, Rows(1)));

        var live = LiveSectionOf(markdown);
        Assert.Contains("### Research arms", live, StringComparison.Ordinal);
        Assert.DoesNotContain("### Comparators", live, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCrossStrategyComposition_NoMergedRankAndNoLabels()
    {
        // Spec 176 §5: the same company leading two arms renders as two independent rank-1 rows — no
        // consensus count, no merged rank, no agreement badge, and no label on any live row.
        var sharedCompany = Guid.NewGuid();
        var markdown = Render(
            Section("default", isPrimary: true,
                [Row(1, "Eos Energy", "EOSE", Snapshot(Guid.NewGuid(), sharedCompany, 44))]),
            Section("filings-led", isPrimary: false,
                [Row(1, "Eos Energy", "EOSE", Snapshot(Guid.NewGuid(), sharedCompany, 61))]));

        var live = LiveSectionOf(markdown);

        Assert.Contains("| default (primary research) | 1 | Eos Energy | EOSE | 44 |",
            live, StringComparison.Ordinal);
        Assert.Contains("| filings-led | 1 | Eos Energy | EOSE | 61 |", live, StringComparison.Ordinal);
        Assert.DoesNotContain("- Label:", live, StringComparison.Ordinal);
        Assert.DoesNotContain("Investigate", live, StringComparison.Ordinal);
        Assert.DoesNotContain("Watch", live, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "buy", "sell", "guaranteed", "safe bet" })
        {
            Assert.DoesNotContain(forbidden, live, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PipeInACompanyNameOrTicker_IsEscapedThroughTheSharedHelper()
    {
        var markdown = Render(
            Section("default", isPrimary: true,
                [Row(1, "Acme | Dynamics", "AC|ME", Snapshot(Guid.NewGuid(), Guid.NewGuid(), 70))]),
            Section("filings-led", isPrimary: false, []));

        Assert.Contains(
            @"| default (primary research) | 1 | Acme \| Dynamics | AC\|ME | 70 |",
            LiveSectionOf(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTicker_RendersAnEmDashCell()
    {
        var markdown = Render(
            Section("default", isPrimary: true,
                [Row(1, "Acme Dynamics", null, Snapshot(Guid.NewGuid(), Guid.NewGuid(), 70))]),
            Section("filings-led", isPrimary: false, []));

        Assert.Contains(
            "| default (primary research) | 1 | Acme Dynamics | — | 70 |",
            LiveSectionOf(markdown), StringComparison.Ordinal);
    }
}
