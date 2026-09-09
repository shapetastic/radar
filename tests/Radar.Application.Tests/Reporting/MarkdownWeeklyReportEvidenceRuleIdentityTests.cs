using Radar.Application.Lifecycle;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Scoring;
using Radar.TestSupport;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// SPEC 217 §3 — the operating-call table's evidence line CITES the rule identities the leaderboard artifact
/// was produced under, so `observation-eligibility-v2` is stamped on the Lead-call evidence lines and not
/// only on the artifacts.
/// <para>
/// The identities are carried OFF THE ARTIFACT (<see cref="RankedEvidence"/>), never read from a code
/// constant: the efficacy artifacts are written by a PREVIOUS run, so a constant here would assert today's
/// rules over yesterday's numbers. A pre-217 artifact states neither and the line says exactly that.
/// </para>
/// </summary>
public sealed class MarkdownWeeklyReportEvidenceRuleIdentityTests
{
    private static readonly DateTimeOffset PeriodStart = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static RankedEvidence Ranked(string? eligibility, string? excess) =>
        new(Rank: 1, OutOfSampleRho: 0.1234, Lower95: 0.0100, Upper95: 0.2300, Observations: 72)
        {
            ObservationEligibilityVersion = eligibility,
            ExcessRuleVersion = excess,
        };

    private static string Render(RankedEvidence ranked)
    {
        var snapshot = new ScoreSnapshotBuilder().Build();
        var section = new StrategyReportSection(
            StrategyName: "default",
            FormulaVersion: "radar-formula-v8",
            ScoringConfigVersion: "radar-scoring-fp-test",
            IsPrimary: true,
            CompaniesScored: 1,
            CompaniesWithLinkedEvidence: 1,
            Rows: [new StrategyReportRow(1, snapshot.CompanyId, "Acme", "ACME", snapshot.Id, snapshot)]);

        var lifecycle = new StrategyLifecycleReportModel(
            ResolvedOperatingCalls.None("no operating call is declared"),
            [new StrategyLifecycleStatusLine("default", StrategyEvidenceStatus.RankedStatus(ranked))]);

        var model = new WeeklyReportModel(
            Title: "Radar Weekly",
            PeriodStartUtc: PeriodStart,
            PeriodEndUtc: PeriodEnd,
            GeneratedAtUtc: PeriodEnd,
            Entries: [],
            SignalsNeedingReview: [],
            Strategies: [section, section with { StrategyName = "second", IsPrimary = false }],
            Lifecycle: lifecycle);

        return new MarkdownWeeklyReportRenderer().Render(model);
    }

    [Fact]
    public void TheEvidenceLine_CitesTheIdentitiesTheArtifactStated()
    {
        var markdown = Render(Ranked("observation-eligibility-v2", "excess-vs-universe-v2"));

        Assert.Contains(
            "[eligibility observation-eligibility-v2; benchmark excess-vs-universe-v2]",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APreSpec217Artifact_SaysSo_RatherThanDefaultingToTodaysRules()
    {
        // The whole reason the identities ride the artifact: a stale artifact must DECLARE itself. Printing
        // today's constants over numbers produced under yesterday's rules is the false-claim failure mode.
        var markdown = Render(Ranked(null, null));

        Assert.Contains(
            "[eligibility not stated (pre-217 artifact); benchmark not stated (pre-217 artifact)]",
            markdown,
            StringComparison.Ordinal);
        Assert.DoesNotContain("observation-eligibility-v2", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArtifactStatingAnOLDERRule_IsQuotedVerbatim_NeverRelabelled()
    {
        // If the artifact on disk was produced under the v1 benchmark rule, the report says v1. Silently
        // relabelling it as v2 would attach today's rule identity to numbers it did not produce.
        var markdown = Render(Ranked("observation-eligibility-v1", "excess-vs-universe-v1"));

        Assert.Contains(
            "[eligibility observation-eligibility-v1; benchmark excess-vs-universe-v1]",
            markdown,
            StringComparison.Ordinal);
    }
}
