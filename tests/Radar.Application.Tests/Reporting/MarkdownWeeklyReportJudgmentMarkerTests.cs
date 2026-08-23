using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Scoring;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 185 §4 at the renderer boundary — the MANDATORY semantic-read column on every live-leaders row:
/// exactly one of the three states per row (an absent marker is unrepresentable), the honest
/// <c>no-judgment</c>/<c>judgment-pending</c>/<c>not-a-candidate</c> defaults, the pinned honesty sentence,
/// the marker column on comparator tables too, and the row's numbers byte-identical to the unmarked
/// baseline apart from the marker cell. The single-strategy report (no leaders section) stays byte-identical
/// — pinned by <c>MarkdownWeeklyReportStrategySectionTests.PreSpec150Golden</c>, which must not change.
/// </summary>
public sealed class MarkdownWeeklyReportJudgmentMarkerTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 6, 7, 22, 15, 0, TimeSpan.Zero);

    private static CompanyScoreSnapshot Snapshot(Guid id, Guid companyId, int opportunity) => new(
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
        WindowEndUtc: WindowEnd,
        CreatedAtUtc: WindowEnd);

    private static StrategyReportSection Section(
        string name, bool isPrimary, IReadOnlyList<StrategyReportRow> rows,
        StrategyPurpose purpose = StrategyPurpose.Research) => new(
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

    private static StrategyReportRow Row(int rank, Guid companyId, string company, string? ticker, int opportunity)
    {
        var snapshot = Snapshot(Guid.NewGuid(), companyId, opportunity);
        return new StrategyReportRow(rank, companyId, company, ticker, snapshot.Id, snapshot);
    }

    private static string Render(
        IReadOnlyList<StrategyReportSection> sections, NewsJudgmentMarkerReportModel? judgment) =>
        new MarkdownWeeklyReportRenderer().Render(
            MarkdownWeeklyReportGoldenModel.Create(sections) with { NewsJudgment = judgment });

    private static string LiveSectionOf(string markdown)
    {
        var start = markdown.IndexOf("## Live strategy leaders", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = markdown.IndexOf("## Highest opportunity", StringComparison.Ordinal);
        Assert.True(end > start);
        return markdown[start..end];
    }

    private static IEnumerable<string> SplitLines(string markdown) =>
        markdown.Split('\n');

    [Fact]
    public void NoJudgmentModel_EveryLeaderRowRendersUnassessedNoJudgment()
    {
        var live = LiveSectionOf(Render(
            [
                Section("default", true, [Row(1, Guid.NewGuid(), "Acme Dynamics", "AEHR", 29)]),
                Section("filings-led", false, [Row(1, Guid.NewGuid(), "Borealis", "BOR", 55)]),
            ],
            judgment: null));

        Assert.Contains(
            "| default (primary research) | 1 | Acme Dynamics | AEHR | 29 | 2026-06-07 22:15Z | "
                + "? unassessed (no-judgment) |",
            live, StringComparison.Ordinal);
        Assert.Contains(
            "| filings-led | 1 | Borealis | BOR | 55 | 2026-06-07 22:15Z | ? unassessed (no-judgment) |",
            live, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingModel_RendersJudgmentPending_OnEveryRow()
    {
        var live = LiveSectionOf(Render(
            [
                Section("default", true, [Row(1, Guid.NewGuid(), "Acme Dynamics", "AEHR", 29)]),
                Section("filings-led", false, []),
            ],
            NewsJudgmentMarkerReportModel.Pending));

        Assert.Contains("? unassessed (judgment-pending) |", live, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerMap_RendersChallengedNoChallengeAndNotACandidate_PerRow()
    {
        var challenged = Guid.NewGuid();
        var clean = Guid.NewGuid();
        var uncovered = Guid.NewGuid();
        var judgment = new NewsJudgmentMarkerReportModel(
            JudgmentPending: false,
            Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
            {
                [challenged] = new(
                    NewsJudgmentMarkerState.Challenged,
                    ChallengeSummary: "regulatory-or-legal-setback, high"),
                [clean] = new(NewsJudgmentMarkerState.NoChallengeFound, TypingIncomplete: true),
            });

        var live = LiveSectionOf(Render(
            [
                Section("default", true,
                    [
                        Row(1, challenged, "Eos Energy", "EOSE", 61),
                        Row(2, clean, "Acme Dynamics", "AEHR", 44),
                        Row(3, uncovered, "Borealis", "BOR", 12),
                    ]),
                Section("filings-led", false, []),
            ],
            judgment));

        Assert.Contains(
            "| default (primary research) | 1 | Eos Energy | EOSE | 61 | 2026-06-07 22:15Z | "
                + "⚠ challenged (regulatory-or-legal-setback, high) |",
            live, StringComparison.Ordinal);
        Assert.Contains(
            "| default (primary research) | 2 | Acme Dynamics | AEHR | 44 | 2026-06-07 22:15Z | "
                + "· no challenge found in supplied facts (typing incomplete) |",
            live, StringComparison.Ordinal);
        Assert.Contains(
            "| default (primary research) | 3 | Borealis | BOR | 12 | 2026-06-07 22:15Z | "
                + "? unassessed (not-a-candidate) |",
            live, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLeaderRow_CarriesExactlyOneMarker_AbsentIsUnrepresentable()
    {
        var live = LiveSectionOf(Render(
            [
                Section("default", true,
                    [
                        Row(1, Guid.NewGuid(), "Acme Dynamics", "AEHR", 29),
                        Row(2, Guid.NewGuid(), "Borealis", "BOR", 20),
                    ]),
                Section("comparator-arm", false, [Row(1, Guid.NewGuid(), "Quiet Co", "QUIE", 5)],
                    StrategyPurpose.Comparator),
            ],
            judgment: null));

        var rows = live.Split('\n')
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal)
                && !l.StartsWith("| strategy |", StringComparison.Ordinal)
                && !l.StartsWith("| --- |", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Contains(
            "? unassessed (no-judgment)", r, StringComparison.Ordinal));
    }

    [Fact]
    public void ComparatorTable_CarriesTheMarkerColumnToo()
    {
        var comparatorCompany = Guid.NewGuid();
        var live = LiveSectionOf(Render(
            [
                Section("default", true, [Row(1, Guid.NewGuid(), "Acme Dynamics", "AEHR", 29)]),
                Section("baseline-activity-only", false,
                    [Row(1, comparatorCompany, "Quiet Co", "QUIE", 5)], StrategyPurpose.Comparator),
            ],
            new NewsJudgmentMarkerReportModel(
                JudgmentPending: false, Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>())));

        var comparators = live[live.IndexOf("### Comparators", StringComparison.Ordinal)..];
        // Comparator sections are excluded from candidate selection (Research only), so their rows read
        // not-a-candidate — assessed under the same total marker rule, never blank.
        Assert.Contains(
            "| baseline-activity-only | 1 | Quiet Co | QUIE | 5 | 2026-06-07 22:15Z | "
                + "? unassessed (not-a-candidate) |",
            comparators, StringComparison.Ordinal);
    }

    [Fact]
    public void HonestySentence_IsPinnedVerbatim_InTheResearchSubsection()
    {
        var live = LiveSectionOf(Render(
            [
                Section("default", true, [Row(1, Guid.NewGuid(), "Acme Dynamics", "AEHR", 29)]),
                Section("filings-led", false, []),
            ],
            judgment: null));

        const string SemanticRead =
            "Semantic read: '⚠ challenged' means the designated judgment cohort recorded at least one "
                + "validated challenge finding; '? unassessed (reason)' means no completed validated "
                + "judgment exists for that row; '· no challenge found in supplied facts' comes only from "
                + "a completed validated judgment and is a statement about the supplied typed facts, never "
                + "a clean bill for the company.";

        Assert.Contains(SemanticRead, live, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerColumn_ChangesNothingElse_RowNumbersAreByteIdenticalApartFromTheMarkerCell()
    {
        var companyId = Guid.NewGuid();
        var sections = new[]
        {
            Section("default", true, [Row(1, companyId, "Eos Energy", "EOSE", 61)]),
            Section("filings-led", false, []),
        };

        var unmarked = Render(sections, judgment: null);
        var marked = Render(sections, new NewsJudgmentMarkerReportModel(
            JudgmentPending: false,
            Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
            {
                [companyId] = new(
                    NewsJudgmentMarkerState.Challenged, ChallengeSummary: "liquidity-or-going-concern, high"),
            }));

        // Same row prefix (rank/company/ticker/score/as-of untouched), different marker cell only.
        const string RowPrefix = "| default (primary research) | 1 | Eos Energy | EOSE | 61 | 2026-06-07 22:15Z | ";
        Assert.Contains(RowPrefix + "? unassessed (no-judgment) |", unmarked, StringComparison.Ordinal);
        Assert.Contains(
            RowPrefix + "⚠ challenged (liquidity-or-going-concern, high) |", marked, StringComparison.Ordinal);

        // Everything OUTSIDE the leader rows is byte-identical: replacing each document's marker cells
        // with a fixed token yields identical documents.
        static string Normalize(string markdown) => markdown
            .Replace("? unassessed (no-judgment)", "<marker>", StringComparison.Ordinal)
            .Replace("⚠ challenged (liquidity-or-going-concern, high)", "<marker>", StringComparison.Ordinal);
        Assert.Equal(Normalize(unmarked), Normalize(marked));
    }

    [Fact]
    public void DeterioratingZeroFindingsRow_RendersTheChallengeCell_NeverTheDot()
    {
        // Spec 186 §1: the live failure this fixes — a Deteriorating trajectory with zero challenge
        // findings used to render the reassuring dot on a leader row.
        var deteriorating = Guid.NewGuid();
        var live = LiveSectionOf(Render(
            [
                Section("default", true, [Row(1, deteriorating, "Eos Energy", "EOSE", 61)]),
            ],
            new NewsJudgmentMarkerReportModel(
                JudgmentPending: false,
                Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
                {
                    [deteriorating] = new(
                        NewsJudgmentMarkerState.Challenged,
                        ChallengeSummary: "business-trajectory-deteriorating",
                        Trajectory: "deteriorating"),
                })));

        Assert.Contains(
            "| default (primary research) | 1 | Eos Energy | EOSE | 61 | 2026-06-07 22:15Z | "
                + "⚠ challenged (business-trajectory-deteriorating) · trajectory deteriorating |",
            live, StringComparison.Ordinal);
        // No TABLE ROW carries the dot (the honesty sentence above the table legitimately quotes it).
        Assert.All(
            SplitLines(live).Where(l => l.StartsWith("| default", StringComparison.Ordinal)),
            l => Assert.DoesNotContain("· no challenge found", l, StringComparison.Ordinal));
    }

    [Fact]
    public void JudgedRows_RenderTheTrajectoryToken_InBothJudgedStates()
    {
        var challenged = Guid.NewGuid();
        var clean = Guid.NewGuid();
        var live = LiveSectionOf(Render(
            [
                Section("default", true,
                    [
                        Row(1, challenged, "Eos Energy", "EOSE", 61),
                        Row(2, clean, "Acme Dynamics", "AEHR", 44),
                    ]),
            ],
            new NewsJudgmentMarkerReportModel(
                JudgmentPending: false,
                Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
                {
                    [challenged] = new(
                        NewsJudgmentMarkerState.Challenged,
                        ChallengeSummary: "regulatory-or-legal-setback, high",
                        Trajectory: "mixed"),
                    [clean] = new(
                        NewsJudgmentMarkerState.NoChallengeFound, Trajectory: "improving"),
                })));

        Assert.Contains(
            "⚠ challenged (regulatory-or-legal-setback, high) · trajectory mixed |",
            live, StringComparison.Ordinal);
        Assert.Contains(
            "· no challenge found in supplied facts · trajectory improving |",
            live, StringComparison.Ordinal);
    }

    [Fact]
    public void JudgmentProvenanceAppendix_CitesEveryJudgedRowsRecord_AndTheStoreRootOnce()
    {
        // Spec 186 §1: the traceability claim is made TRUE — the marker text alone linked nothing.
        var challenged = Guid.NewGuid();
        var clean = Guid.NewGuid();
        var uncovered = Guid.NewGuid();
        var challengedJudgment = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var cleanJudgment = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var markdown = Render(
            [
                Section("default", true,
                    [
                        Row(1, challenged, "Eos Energy", "EOSE", 61),
                        Row(2, clean, "Acme Dynamics", "AEHR", 44),
                        Row(3, uncovered, "Borealis", "BOR", 12),
                    ]),
            ],
            new NewsJudgmentMarkerReportModel(
                JudgmentPending: false,
                Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
                {
                    [challenged] = new(
                        NewsJudgmentMarkerState.Challenged,
                        ChallengeSummary: "business-trajectory-deteriorating",
                        Trajectory: "deteriorating",
                        JudgmentId: challengedJudgment),
                    [clean] = new(
                        NewsJudgmentMarkerState.NoChallengeFound,
                        Trajectory: "improving",
                        JudgmentId: cleanJudgment),
                },
                JudgmentStoreRoot: "data/news-risk/judgments"));
        var live = LiveSectionOf(markdown);

        Assert.Contains("### Judgment provenance — diagnostic appendix", live, StringComparison.Ordinal);
        // The store root is stated ONCE, never per row.
        Assert.Equal(
            1,
            live.Split("Judgments store root: `data/news-risk/judgments`").Length - 1);
        Assert.Contains(
            "- Eos Energy — judgment `11111111-1111-1111-1111-111111111111` · "
                + "⚠ challenged (business-trajectory-deteriorating) · trajectory deteriorating",
            live, StringComparison.Ordinal);
        Assert.Contains(
            "- Acme Dynamics — judgment `22222222-2222-2222-2222-222222222222` · "
                + "· no challenge found in supplied facts · trajectory improving",
            live, StringComparison.Ordinal);
        // A row with no judgment record cites nothing — never an invented id.
        Assert.DoesNotContain("- Borealis — judgment", live, StringComparison.Ordinal);
    }

    [Fact]
    public void JudgmentProvenanceAppendix_IsAbsent_WhenNoMarkerCarriesAJudgmentId()
    {
        // Display-only and strictly additive: the pre-186 report shape is untouched whenever the markers
        // carry no record ids (a null model, the pending placeholder, or a direct marker map).
        var companyId = Guid.NewGuid();
        var sections = new[] { Section("default", true, [Row(1, companyId, "Eos Energy", "EOSE", 61)]) };

        Assert.DoesNotContain(
            "Judgment provenance", Render(sections, judgment: null), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Judgment provenance",
            Render(sections, NewsJudgmentMarkerReportModel.Pending),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Judgment provenance",
            Render(sections, new NewsJudgmentMarkerReportModel(
                JudgmentPending: false,
                Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
                {
                    [companyId] = new(NewsJudgmentMarkerState.NoChallengeFound, Trajectory: "mixed"),
                })),
            StringComparison.Ordinal);
    }

    [Fact]
    public void JudgmentProvenanceAppendix_StatesAnUnrecordedStoreRootHonestly()
    {
        var companyId = Guid.NewGuid();
        var live = LiveSectionOf(Render(
            [Section("default", true, [Row(1, companyId, "Eos Energy", "EOSE", 61)])],
            new NewsJudgmentMarkerReportModel(
                JudgmentPending: false,
                Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
                {
                    [companyId] = new(
                        NewsJudgmentMarkerState.NoChallengeFound,
                        Trajectory: "mixed",
                        JudgmentId: Guid.NewGuid()),
                })));

        Assert.Contains(
            "Judgments store root: not recorded by this run", live, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArmPlaceholderRow_KeepsTheColumnCount_WithAnEmDashMarkerCell()
    {
        var live = LiveSectionOf(Render(
            [
                Section("default", true, [Row(1, Guid.NewGuid(), "Acme Dynamics", "AEHR", 29)]),
                Section("filings-led", false, []),
            ],
            judgment: null));

        Assert.Contains(
            "| filings-led | — | No evidence-linked live scores in this report window. | — | — | — | — |",
            live, StringComparison.Ordinal);
    }
}
