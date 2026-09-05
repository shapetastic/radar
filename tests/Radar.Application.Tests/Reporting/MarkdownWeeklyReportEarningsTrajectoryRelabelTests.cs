using Radar.Application.Reporting;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 167 — the weekly report's display-only relabel of the stored <see cref="SignalType.GuidanceChange"/>
/// member as <c>EarningsTrajectory</c>, plus the one header legend line explaining the literal token where it
/// appears inside stored evidence-line provenance text.
/// <para>
/// The two <c>Pre167*</c> pins below are character-for-character captures of what the UNMODIFIED (pre-167)
/// renderer produced for the shared golden model and its GuidanceChange-augmented variant — captured by
/// running those models through the renderer BEFORE the spec-167 change was made, so the before/after guards
/// here are genuine byte-level comparisons (mirroring the spec-150 <c>PreSpec150Golden</c> approach): apart
/// from the mapped "Why noticed" token and the one legend line, the report is byte-identical to pre-167.
/// </para>
/// </summary>
public sealed class MarkdownWeeklyReportEarningsTrajectoryRelabelTests
{
    // The exact legend line spec 167 adds under the header caveats (beside the notedness line). Must stay
    // in sync with MarkdownWeeklyReportRenderer.AppendDisclaimers; producer-neutral by design — the
    // deterministic spec-57 form is an earnings-FILING marker, not a trajectory read.
    private const string LegendLine =
        "> \"GuidanceChange\" in evidence lines is a historical earnings-release signal type — either a "
        + "deterministic Neutral earnings-filing marker or an AI earnings-trajectory read; it does not by "
        + "itself mean the company issued or changed guidance.";

    private static readonly Guid GuidanceSignalId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid GuidanceEvidenceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // Byte-exact pre-167 rendering of MarkdownWeeklyReportGoldenModel.Create(strategies: null), captured
    // from the unmodified renderer (it is also, verifiably, the original spec-150 pin — spec 150's report
    // shape held unchanged until this slice).
    private static readonly string Pre167Golden = string.Join("\n",
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

    // The stored evidence-link reason text of the GuidanceChange-augmented fixture — deliberately the
    // shape RadarScoreFormulaV8 authors at scoring time, containing the literal token. Provenance: it
    // must render byte-verbatim; the display mapping must never reach it.
    private const string StoredProvenanceReason = "GuidanceChange (Positive), strength 8, confidence 0.90";

    // Byte-exact pre-167 rendering of CreateGuidanceModel() (the shared golden model with one
    // GuidanceChange signal + its evidence link added to the Acme entry), captured from the unmodified
    // renderer. Differs from Pre167Golden by exactly the two added Acme lines.
    private static readonly string Pre167GuidanceGolden = Pre167Golden
        .Replace(
            "  - [Acme lands major customer](https://acme.example/news) — Acme Feed: Customer win raised "
                + "trajectory.\n",
            "  - [Acme lands major customer](https://acme.example/news) — Acme Feed: Customer win raised "
                + "trajectory.\n"
                + "  - [Q2 earnings 8-K](https://sec.example/8-k) — SEC EDGAR: " + StoredProvenanceReason + "\n",
            StringComparison.Ordinal)
        .Replace(
            "  - CustomerWin (Positive): Multi-year agreement announced.\n",
            "  - CustomerWin (Positive): Multi-year agreement announced.\n"
                + "  - GuidanceChange (Positive): Directional earnings read: revenue and EPS improved.\n",
            StringComparison.Ordinal);

    /// <summary>
    /// The shared golden model with one GuidanceChange signal (and its evidence link, whose stored
    /// contribution reason contains the literal token) appended to the Acme entry. Identical to the
    /// fixture the pre-167 capture was run over.
    /// </summary>
    private static WeeklyReportModel CreateGuidanceModel()
    {
        var model = MarkdownWeeklyReportGoldenModel.Create(strategies: null);
        var acme = model.Entries[0];

        var evidence = new List<ReportEvidenceRef>(acme.Evidence)
        {
            new(
                EvidenceId: GuidanceEvidenceId,
                SignalId: GuidanceSignalId,
                SourceName: "SEC EDGAR",
                SourceUrl: "https://sec.example/8-k",
                Title: "Q2 earnings 8-K",
                ContributionReason: StoredProvenanceReason),
        };
        var signals = new List<ReportSignalRef>(acme.Signals)
        {
            new(
                GuidanceSignalId, SignalType.GuidanceChange, SignalDirection.Positive,
                "Directional earnings read: revenue and EPS improved."),
        };

        var entries = new List<WeeklyReportEntry>(model.Entries)
        {
            [0] = acme with { Evidence = evidence, Signals = signals },
        };

        return model with { Entries = entries };
    }

    // The spec-209 legend line that follows the spec-167 one in every report (its own guard lives in
    // MarkdownWeeklyReportInsiderActivityTests); carried here so the pre-167 captures still produce the
    // EXPECTED current bytes. Must stay in sync with MarkdownWeeklyReportRenderer.AppendDisclaimers.
    private const string Spec209LegendLine =
        "> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is a routine "
        + "or planned filing, not a discretionary transaction.";

    // Inserts the spec-167 legend line at its one sanctioned position (directly under the notedness
    // caveat) into a captured pre-167 document — followed by the spec-209 legend line, which is the only
    // other header delta since the capture — producing the EXPECTED current bytes.
    private static string WithLegendInserted(string pre167Document)
    {
        const string Anchor = "a research signal, not a valuation.\n";
        var anchorIndex = pre167Document.IndexOf(Anchor, StringComparison.Ordinal);
        Assert.True(anchorIndex >= 0, "The notedness caveat must be present in the pre-167 capture.");
        return pre167Document.Insert(
            anchorIndex + Anchor.Length, LegendLine + "\n" + Spec209LegendLine + "\n");
    }

    private static int CountOccurrences(string haystack, string needle) =>
        haystack.Split(needle, StringSplitOptions.None).Length - 1;

    [Fact]
    public void GoldenModel_TheLegendLine_IsTheOnlyDifferenceFromPre167()
    {
        // The shared golden model carries no GuidanceChange signal, so the ONLY sanctioned delta vs the
        // captured pre-167 bytes is the legend line. Full-string equality proves nothing else moved.
        var output = new MarkdownWeeklyReportRenderer().Render(
            MarkdownWeeklyReportGoldenModel.Create(strategies: null));

        Assert.Equal(WithLegendInserted(Pre167Golden), output);
    }

    [Fact]
    public void GuidanceModel_OnlyTheMappedTokenAndTheLegendLine_DifferFromPre167()
    {
        // Expected = the captured pre-167 bytes + the legend line + exactly ONE token swap on the
        // renderer-owned "Why noticed" line. The stored evidence-line provenance is deliberately NOT
        // rewritten in the expectation — equality therefore also proves it rendered byte-verbatim.
        const string Pre167WhyNoticedLine =
            "  - GuidanceChange (Positive): Directional earnings read: revenue and EPS improved.\n";
        const string MappedWhyNoticedLine =
            "  - EarningsTrajectory (Positive): Directional earnings read: revenue and EPS improved.\n";

        Assert.Equal(1, CountOccurrences(Pre167GuidanceGolden, Pre167WhyNoticedLine));
        var expected = WithLegendInserted(Pre167GuidanceGolden)
            .Replace(Pre167WhyNoticedLine, MappedWhyNoticedLine, StringComparison.Ordinal);

        var output = new MarkdownWeeklyReportRenderer().Render(CreateGuidanceModel());

        Assert.Equal(expected, output);
    }

    [Fact]
    public void StoredEvidenceLinkReason_RendersByteVerbatim_TheMappingNeverReachesIt()
    {
        var output = new MarkdownWeeklyReportRenderer().Render(CreateGuidanceModel());

        // The full evidence line, including the literal stored token, exactly as authored at scoring time.
        Assert.Contains(
            "  - [Q2 earnings 8-K](https://sec.example/8-k) — SEC EDGAR: " + StoredProvenanceReason + "\n",
            output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EarningsTrajectory (Positive), strength 8", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WhyNoticed_MapsGuidanceChangeToEarningsTrajectory_AndLeavesEveryOtherMemberUnchanged()
    {
        var signals = Enum.GetValues<SignalType>()
            .Select(type => new ReportSignalRef(
                Guid.NewGuid(), type, SignalDirection.Positive, $"reason for {type}"))
            .ToList();
        var snap = new ScoreSnapshotBuilder().Build();
        var entry = new WeeklyReportEntry(
            CompanyId: snap.CompanyId,
            CompanyName: "Acme Corp",
            Ticker: "ACME",
            ScoreSnapshotId: snap.Id,
            Snapshot: snap,
            Action: RadarReportAction.Investigate,
            Rationale: "Deterministic rationale.",
            Rank: 1,
            Evidence: [],
            Signals: signals);
        var model = new WeeklyReportModel(
            Title: "Radar Weekly",
            PeriodStartUtc: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEndUtc: new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero),
            GeneratedAtUtc: new DateTimeOffset(2026, 6, 8, 9, 30, 0, TimeSpan.Zero),
            Entries: [entry],
            SignalsNeedingReview: []);

        var output = new MarkdownWeeklyReportRenderer().Render(model);

        Assert.Contains("  - EarningsTrajectory (Positive): reason for GuidanceChange", output,
            StringComparison.Ordinal);
        // The enum member itself never reaches a renderer-owned type site ("GuidanceChange (" cannot
        // occur elsewhere here: this fixture has no evidence text and the legend says "GuidanceChange").
        Assert.DoesNotContain("GuidanceChange (", output, StringComparison.Ordinal);
        // AMENDED BY SPEC 209: InsiderBuying is the SECOND relabelled member (rendered "InsiderActivity",
        // and — unlike GuidanceChange — its exact token is also rewritten inside stored reason text); its
        // own guard lives in MarkdownWeeklyReportInsiderActivityTests. Every remaining member is unchanged.
        foreach (var type in Enum.GetValues<SignalType>()
                     .Where(t => t is not SignalType.GuidanceChange and not SignalType.InsiderBuying))
        {
            Assert.Contains($"  - {type} (Positive): reason for {type}", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LegendLine_IsPresentExactlyOnce()
    {
        var output = new MarkdownWeeklyReportRenderer().Render(CreateGuidanceModel());

        Assert.Equal(1, CountOccurrences(output, LegendLine));
    }

    [Fact]
    public void ActionPolicy_DecisionsAreUnchanged_ForFixturesContainingGuidanceChangeSignals()
    {
        // The policy consumes the SignalType ENUM (never the display string), so spec 167 must not move
        // any decision. Pin the two decision paths a GuidanceChange signal can influence or accompany:
        var policy = new WeeklyReportActionPolicyV1();

        // 1. Corroboration floor: sub-Watch opportunity, under-followed, trajectory at/above neutral, and
        //    GuidanceChange counts as one of the two DISTINCT positive types — exactly as pre-167.
        var floored = policy.Decide(new ReportActionContext(
            Current: new ScoreSnapshotBuilder()
                .WithOpportunityScore(30)
                .WithTrajectoryScore(55)
                .WithEvidenceConfidenceScore(80)
                .Build(),
            Previous: null,
            ContributingSignals:
            [
                new ReportSignalRef(
                    Guid.NewGuid(), SignalType.GuidanceChange, SignalDirection.Positive, "earnings read"),
                new ReportSignalRef(
                    Guid.NewGuid(), SignalType.CustomerWin, SignalDirection.Positive, "customer win"),
            ],
            FollowingTier: FollowingTier.Small));

        Assert.Equal(RadarReportAction.Watch, floored.Action);
        Assert.Contains("2 corroborating positive signal types", floored.Rationale, StringComparison.Ordinal);

        // 2. Steady-state Investigate with a GuidanceChange signal present.
        var investigate = policy.Decide(new ReportActionContext(
            Current: new ScoreSnapshotBuilder()
                .WithOpportunityScore(70)
                .WithTrajectoryScore(60)
                .WithEvidenceConfidenceScore(80)
                .Build(),
            Previous: null,
            ContributingSignals:
            [
                new ReportSignalRef(
                    Guid.NewGuid(), SignalType.GuidanceChange, SignalDirection.Positive, "earnings read"),
            ]));

        Assert.Equal(RadarReportAction.Investigate, investigate.Action);

        // The display token is a renderer concern only; it must never leak into a policy rationale.
        Assert.DoesNotContain("EarningsTrajectory", floored.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("EarningsTrajectory", investigate.Rationale, StringComparison.Ordinal);
    }
}
