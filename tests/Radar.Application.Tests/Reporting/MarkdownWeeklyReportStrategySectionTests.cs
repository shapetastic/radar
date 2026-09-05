using Radar.Application.Reporting;
using Radar.Domain.Scoring;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 150 — the per-strategy plain ranked tables, at the renderer boundary.
/// <para>
/// The first test is the load-bearing one: with no strategy sections (a single-strategy run, i.e. every
/// deployment that never configured <c>Radar:Strategies</c>) the WHOLE rendered document must be
/// byte-identical to the pre-150 output. <see cref="PreSpec150Golden"/> is a character-for-character copy
/// of what the pre-change renderer produced for <see cref="MarkdownWeeklyReportGoldenModel"/>, captured by
/// running that model through the unmodified renderer — so this is a genuine before/after comparison rather
/// than a restatement of current behaviour.
/// </para>
/// </summary>
public sealed class MarkdownWeeklyReportStrategySectionTests
{
    private static readonly string[] ForbiddenWords = ["buy", "sell", "guaranteed", "safe bet"];

    // The exact pre-spec-150 rendering of MarkdownWeeklyReportGoldenModel.Create(). Lines are joined with
    // '\n' (the renderer's only line ending) and the two trailing empty entries reproduce its trailing
    // blank line, so this is the literal byte sequence — not an approximation of it.
    // AMENDED BY SPEC 167: the header now carries one additional legend line (the "GuidanceChange"
    // gloss, directly under the notedness caveat). That line is part of EVERY report, so it belongs in
    // this pin too; spec 167's own before/after guard lives in
    // MarkdownWeeklyReportEarningsTrajectoryRelabelTests, whose pre-167 pin was captured from the
    // unmodified renderer. AMENDED AGAIN BY SPEC 209: a second legend line (the "InsiderActivity" gloss,
    // directly under the GuidanceChange one) is likewise part of EVERY report and belongs here too; its own
    // before/after guard lives in MarkdownWeeklyReportInsiderActivityTests. Everything else here is
    // byte-unchanged.
    private static readonly string PreSpec150Golden = string.Join("\n",
    [
        "# Radar Weekly — 2026-06-01 to 2026-06-08",
        "Period: 2026-06-01 → 2026-06-08 (UTC)",
        "Generated: 2026-06-08 09:30Z",
        "",
        "> Not financial advice.",
        "> For research only.",
        "> Human review required.",
        "> Notedness (measured Attention + curated following tier) discounts a company's Opportunity so "
            + "already-followed names surface lower — a research signal, not a valuation.",
        "> \"GuidanceChange\" in evidence lines is a historical earnings-release signal type — either a "
            + "deterministic Neutral earnings-filing marker or an AI earnings-trajectory read; it does "
            + "not by itself mean the company issued or changed guidance.",
        "> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is a routine "
            + "or planned filing, not a discretionary transaction.",
        "",
        "## Highest opportunity",
        "",
        "### 1. Acme Dynamics (ACME)",
        "- Label: Investigate",
        "- Opportunity 71 · Trajectory 64 · Attention 22 · Evidence 80 · Velocity 55 (Opportunity +11, "
            + "Trajectory +0 vs last run)",
        "- **Notedness:** Attention 22 · Following: Small (under-followed)",
        "- Why: Trajectory improving on corroborated evidence.",
        "- Score snapshot: 22222222-2222-2222-2222-222222222222",
        "- Evidence:",
        "  - [Acme lands major customer](https://acme.example/news) — Acme Feed: Customer win raised "
            + "trajectory.",
        "- Why noticed:",
        "  - CustomerWin (Positive): Multi-year agreement announced.",
        "",
        "### 2. Borealis Systems",
        "- Label: Watch",
        "- Opportunity 40 · Trajectory 30 · Attention 70 · Evidence 80 · Velocity 55 (first snapshot)",
        "- **Notedness:** Attention 70 · Following: Mega (already broadly followed)",
        "- Why: Thin corroboration; keep observing.",
        "- Score snapshot: 44444444-4444-4444-4444-444444444444",
        "- Evidence:",
        "  - (no linked evidence)",
        "",
        "## Watch",
        "",
        "- Borealis Systems (#2)",
        "",
        "## Signals needing review",
        "",
        "- Northwind Robotics: Unverified expansion claim. — EscalateToHuman: low-quality source. (signal "
            + "77777777-7777-7777-7777-777777777777)",
        "",
        "## Collection summary",
        "",
        "Radar checked 4 source(s) this run; 1 could not be read.",
        "- Acme Feed (https://acme.example/rss): HTTP 503",
        "",
        "## Collection health",
        "",
        "- [Warning] rss: declared 10, reached 8 — Two declared feeds never reached a collector.",
        "",
        "## Recent runs",
        "",
        "- 2026-06-07 14:00Z — collectors: rss, sec — new evidence 12 · approved 7 · companies 43 · "
            + "sources 4/1 failed",
        "",
        "",
    ]);

    private static CompanyScoreSnapshot Snapshot(
        Guid id,
        Guid companyId,
        int opportunity = 70,
        int trajectory = 60,
        int attention = 20,
        int evidence = 80,
        int velocity = 50) =>
        new(
            Id: id,
            CompanyId: companyId,
            ScoringVersion: "radar-formula-v8",
            TrajectoryScore: trajectory,
            OpportunityScore: opportunity,
            AttentionScore: attention,
            EvidenceConfidenceScore: evidence,
            SignalVelocityScore: velocity,
            Explanation: "Deterministic explanation.",
            ComponentJson: "{}",
            WindowStartUtc: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero),
            CreatedAtUtc: new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));

    private static StrategyReportRow Row(
        int rank, string company, string? ticker, CompanyScoreSnapshot snapshot) =>
        new(rank, snapshot.CompanyId, company, ticker, snapshot.Id, snapshot);

    private static StrategyReportSection Section(
        string name,
        bool isPrimary,
        IReadOnlyList<StrategyReportRow> rows,
        string? fingerprint = "radar-scoring-fp-aaaaaaaaaaaa",
        string formula = "radar-formula-v8",
        int? companiesScored = null,
        int? withLinkedEvidence = null) =>
        new(
            StrategyName: name,
            FormulaVersion: formula,
            ScoringConfigVersion: fingerprint,
            IsPrimary: isPrimary,
            CompaniesScored: companiesScored ?? rows.Count,
            CompaniesWithLinkedEvidence: withLinkedEvidence ?? rows.Count,
            Rows: rows);

    [Fact]
    public void SingleStrategyRun_RendersByteIdenticalPreSpec150Markdown()
    {
        var output = new MarkdownWeeklyReportRenderer().Render(
            MarkdownWeeklyReportGoldenModel.Create(strategies: null));

        Assert.Equal(PreSpec150Golden, output);
        Assert.DoesNotContain("## Strategy:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStrategyList_RendersNoStrategySection()
    {
        var output = new MarkdownWeeklyReportRenderer().Render(
            MarkdownWeeklyReportGoldenModel.Create(strategies: []));

        Assert.Equal(PreSpec150Golden, output);
    }

    [Fact]
    public void StrategySections_RenderOneTablePerStrategy_AfterAllExistingContent()
    {
        var primarySnap = Snapshot(Guid.NewGuid(), Guid.NewGuid(), opportunity: 71, trajectory: 64,
            attention: 22, evidence: 80, velocity: 55);
        var otherSnap = Snapshot(Guid.NewGuid(), Guid.NewGuid(), opportunity: 33);

        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true, [Row(1, "Acme Dynamics", "ACME", primarySnap)],
                fingerprint: "radar-scoring-fp-111111111111"),
            Section("filings-led", isPrimary: false, [Row(1, "Borealis Systems", "BOR", otherSnap)],
                fingerprint: "radar-scoring-fp-222222222222", formula: "radar-formula-v9"),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains(
            "## Strategy: default (radar-formula-v8) — primary (the series reported above)",
            output, StringComparison.Ordinal);
        Assert.Contains("## Strategy: filings-led (radar-formula-v9)", output, StringComparison.Ordinal);
        Assert.Contains(
            "Fingerprint: radar-scoring-fp-111111111111 · 1 company scored · 1 with linked evidence",
            output, StringComparison.Ordinal);
        Assert.Contains(
            "| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |",
            output, StringComparison.Ordinal);
        Assert.Contains("| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |",
            output, StringComparison.Ordinal);
        Assert.Contains("| 1 | Acme Dynamics | ACME | 71 | 64 | 22 | 80 | 55 |",
            output, StringComparison.Ordinal);

        // Appended AFTER everything that already existed.
        Assert.True(
            output.IndexOf("## Recent runs", StringComparison.Ordinal)
                < output.IndexOf("## Strategy: default", StringComparison.Ordinal),
            "Strategy sections must be appended after the existing content.");
        Assert.True(
            output.IndexOf("## Strategy: default", StringComparison.Ordinal)
                < output.IndexOf("## Strategy: filings-led", StringComparison.Ordinal),
            "Strategy sections must render in model order (primary first).");
    }

    [Fact]
    public void HonestyLine_RendersUnderTheFirstStrategySectionOnly()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true, [Row(1, "Acme", "ACME", Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
            Section("filings-led", isPrimary: false, [Row(1, "Acme", "ACME", Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        const string Honesty = "These are independent scorings of the SAME collection pass.";
        var firstIndex = output.IndexOf(Honesty, StringComparison.Ordinal);
        Assert.True(firstIndex > 0);
        Assert.Equal(-1, output.IndexOf(Honesty, firstIndex + 1, StringComparison.Ordinal));
        Assert.Contains("data/efficacy/strategy-leaderboard.md, not this table.",
            output, StringComparison.Ordinal);

        // It sits inside the FIRST section, before the second one starts.
        Assert.True(
            firstIndex > output.IndexOf("## Strategy: default", StringComparison.Ordinal)
                && firstIndex < output.IndexOf("## Strategy: filings-led", StringComparison.Ordinal));
    }

    [Fact]
    public void NullFingerprint_RendersUnstampedRatherThanAnEmptyField()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true, [], fingerprint: null, companiesScored: 3,
                withLinkedEvidence: 0),
            Section("filings-led", isPrimary: false, []),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains("Fingerprint: (unstamped) · 3 companies scored · 0 with linked evidence",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_IsStatedInTheHeader_NeverSilent()
    {
        var rows = new List<StrategyReportRow>();
        for (var i = 0; i < 2; i++)
        {
            rows.Add(Row(i + 1, $"Company {i}", "TIC", Snapshot(Guid.NewGuid(), Guid.NewGuid())));
        }

        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true, rows, companiesScored: 9, withLinkedEvidence: 7),
            Section("filings-led", isPrimary: false, rows, companiesScored: 9, withLinkedEvidence: 2),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains(
            "· 9 companies scored · 7 with linked evidence · showing top 2",
            output, StringComparison.Ordinal);
        // The second section was NOT truncated (2 with evidence, 2 rows) so it says nothing about a cap.
        Assert.Contains(
            "· 9 companies scored · 2 with linked evidence\n",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTicker_RendersAnEmDashCell()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true,
                [Row(1, "Acme Dynamics", null, Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
            Section("filings-led", isPrimary: false,
                [Row(1, "Acme Dynamics", "", Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains("| 1 | Acme Dynamics | — | 70 | 60 | 20 | 80 | 50 |",
            output, StringComparison.Ordinal);
        // Both the null and the empty ticker render the same cell.
        Assert.Equal(
            2,
            output.Split("| 1 | Acme Dynamics | — | 70 | 60 | 20 | 80 | 50 |", StringSplitOptions.None)
                .Length - 1);
    }

    [Fact]
    public void PipeInCompanyNameOrTicker_IsEscaped_SoTheRowKeepsEightColumns()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Guid.NewGuid());
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true, [Row(1, "Acme | Dynamics", "AC|ME", snapshot)]),
            Section("filings-led", isPrimary: false, []),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains(@"| 1 | Acme \| Dynamics | AC\|ME | 70 | 60 | 20 | 80 | 50 |",
            output, StringComparison.Ordinal);

        // The rendered row still has exactly the 8 columns the header declares: count unescaped pipes.
        var row = output
            .Split('\n')
            .Single(l => l.StartsWith("| 1 | Acme", StringComparison.Ordinal));
        var unescaped = 0;
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == '|' && (i == 0 || row[i - 1] != '\\'))
            {
                unescaped++;
            }
        }

        Assert.Equal(9, unescaped); // 8 columns ⇒ 9 delimiters
    }

    [Fact]
    public void LineBreakInCompanyNameOrTicker_CollapsesToASpace_SoTheRowStaysOneTableRow()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Guid.NewGuid());
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true, [Row(1, "Acme\r\nDynamics", "AC\nME", snapshot)]),
            Section("filings-led", isPrimary: false, []),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains("| 1 | Acme Dynamics | AC ME | 70 | 60 | 20 | 80 | 50 |",
            output, StringComparison.Ordinal);

        // The whole row survives as ONE line: a raw newline would have broken it out of the table.
        var row = output
            .Split('\n')
            .Single(l => l.StartsWith("| 1 | Acme", StringComparison.Ordinal));
        Assert.EndsWith("| 50 |", row.TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderCounts_AgreeInNumber_SoASingleCompanyDoesNotReadAsCompanies()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true,
                [Row(1, "Acme Dynamics", "ACME", Snapshot(Guid.NewGuid(), Guid.NewGuid()))],
                companiesScored: 1, withLinkedEvidence: 1,
                fingerprint: "radar-scoring-fp-111111111111"),
            Section("filings-led", isPrimary: false, [], companiesScored: 0, withLinkedEvidence: 0,
                fingerprint: "radar-scoring-fp-222222222222"),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains("· 1 company scored · 1 with linked evidence", output, StringComparison.Ordinal);
        // Zero takes the plural, as English does.
        Assert.Contains("· 0 companies scored · 0 with linked evidence", output, StringComparison.Ordinal);
        Assert.DoesNotContain("1 companies scored", output, StringComparison.Ordinal);
    }

    [Fact]
    public void StrategySections_CarryNoLabels_NoEvidence_NoWhyNoticed_AndNoAdviceVocabulary()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("filings-led", isPrimary: false,
                [Row(1, "Acme Dynamics", "ACME", Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
            Section("narrative-led", isPrimary: false,
                [Row(1, "Borealis Systems", "BOR", Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
        ]);

        var output = new MarkdownWeeklyReportRenderer().Render(model);
        var sectionsOnly = output[output.IndexOf("## Strategy: filings-led", StringComparison.Ordinal)..];

        Assert.DoesNotContain("- Label:", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Investigate", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Watch", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Ignore", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Needs more evidence", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Thesis improving", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Thesis deteriorating", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("- Evidence:", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("- Why noticed:", sectionsOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("- Score snapshot:", sectionsOnly, StringComparison.Ordinal);

        foreach (var forbidden in ForbiddenWords)
        {
            Assert.DoesNotContain(forbidden, sectionsOnly, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Renderer_IsDeterministic_SameModelRendersTheSameBytesTwice()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("default", isPrimary: true,
                [Row(1, "Acme Dynamics", "ACME", Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
            Section("filings-led", isPrimary: false,
                [Row(1, "Borealis Systems", null, Snapshot(Guid.NewGuid(), Guid.NewGuid()))]),
        ]);

        var renderer = new MarkdownWeeklyReportRenderer();

        Assert.Equal(renderer.Render(model), renderer.Render(model));
    }

    [Fact]
    public void Row_CitingADifferentSnapshot_Throws()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Guid.NewGuid());
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("filings-led", isPrimary: false,
            [
                new StrategyReportRow(
                    Rank: 1,
                    CompanyId: snapshot.CompanyId,
                    CompanyName: "Acme Dynamics",
                    Ticker: "ACME",
                    ScoreSnapshotId: Guid.NewGuid(),
                    Snapshot: snapshot),
            ]),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MarkdownWeeklyReportRenderer().Render(model));
        Assert.Contains("filings-led", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_CitingADifferentCompany_Throws()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Guid.NewGuid());
        var model = MarkdownWeeklyReportGoldenModel.Create(
        [
            Section("filings-led", isPrimary: false,
            [
                new StrategyReportRow(
                    Rank: 1,
                    CompanyId: Guid.NewGuid(),
                    CompanyName: "Acme Dynamics",
                    Ticker: "ACME",
                    ScoreSnapshotId: snapshot.Id,
                    Snapshot: snapshot),
            ]),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MarkdownWeeklyReportRenderer().Render(model));
        Assert.Contains("belongs to company", ex.Message, StringComparison.Ordinal);
    }
}
