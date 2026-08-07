using Radar.Application.Efficacy.DenominatorAudit;

namespace Radar.Application.Tests.Efficacy.DenominatorAudit;

/// <summary>
/// Pins the spec-172 rendered artifacts: determinism (byte-identical output over identical input), the
/// non-negotiable honesty statements (non-independence, the size/coverage confound, the named degeneracies,
/// which rho floor applies), the empty-bin rendering, and the CSV escaping route.
/// </summary>
public sealed class ScoreMoveDenominatorAuditRendererTests
{
    private static readonly Guid CompanyA = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static DenominatorObservation Observation(
        string strategy, int deltaOpportunity, int directionalCount, int linkCount) =>
        new(
            StrategyName: strategy,
            CompanyId: CompanyA,
            AsOfDate: new DateOnly(2026, 8, 5),
            DeltaOpportunity: deltaOpportunity,
            DeltaTrajectory: deltaOpportunity - 1,
            LinkCount: linkCount,
            DirectionalCount: directionalCount);

    private static DenominatorAuditReport Report(string strategy = "default")
    {
        var observations = new[]
        {
            Observation(strategy, deltaOpportunity: -21, directionalCount: 1, linkCount: 9),
            Observation(strategy, deltaOpportunity: 17, directionalCount: 1, linkCount: 28),
            Observation(strategy, deltaOpportunity: 3, directionalCount: 5, linkCount: 30),
        };

        return new DenominatorAuditReport(
            [ScoreMoveDenominatorAudit.Compute(strategy, 4, 2, observations)]);
    }

    [Fact]
    public void Rendering_IsDeterministic_ByteIdenticalOverIdenticalInput()
    {
        var renderer = new ScoreMoveDenominatorAuditRenderer();
        var report = Report();

        Assert.Equal(renderer.RenderCsv(report), renderer.RenderCsv(report));
        Assert.Equal(renderer.RenderMarkdown(report), renderer.RenderMarkdown(report));

        // And across two independently-computed reports over the same input — the whole compute + render
        // path is a pure function of the observations (AD-3: no wall-clock, no randomness).
        var recomputed = Report();
        Assert.Equal(renderer.RenderCsv(report), renderer.RenderCsv(recomputed));
        Assert.Equal(renderer.RenderMarkdown(report), renderer.RenderMarkdown(recomputed));
    }

    [Fact]
    public void BothArtifacts_CarryTheHonestyStatements()
    {
        var renderer = new ScoreMoveDenominatorAuditRenderer();
        var report = Report();
        var csv = renderer.RenderCsv(report);
        var markdown = renderer.RenderMarkdown(report);

        foreach (var artifact in new[] { csv, markdown })
        {
            Assert.Contains("Observations are NOT independent", artifact, StringComparison.Ordinal);
            Assert.Contains("dispersion, not significance", artifact, StringComparison.Ordinal);
            Assert.Contains(
                "cannot separate \"thin evidence amplifies moves\" from \"small companies move more\"",
                artifact,
                StringComparison.Ordinal);
            Assert.Contains("floor is 2 observations", artifact, StringComparison.Ordinal);
            Assert.Contains("not financial advice", artifact, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Markdown_StatesThePairingRule_ConsecutiveSnapshotsNotCalendarDays()
    {
        var markdown = new ScoreMoveDenominatorAuditRenderer().RenderMarkdown(Report());

        Assert.Contains("consecutive SNAPSHOTS, not consecutive calendar days", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void DegenerateCoefficient_RendersItsNamedReason_NeverNaN()
    {
        // A single observation is below ComputeRho's floor of 2.
        var report = new DenominatorAuditReport(
        [
            ScoreMoveDenominatorAudit.Compute(
                "thin", 1, 1, [Observation("thin", 10, 1, 5)]),
        ]);

        var renderer = new ScoreMoveDenominatorAuditRenderer();
        var csv = renderer.RenderCsv(report);
        var markdown = renderer.RenderMarkdown(report);

        foreach (var artifact in new[] { csv, markdown })
        {
            Assert.Contains("not defined: too-few-observations (n=1)", artifact, StringComparison.Ordinal);
        }

        // No rendered VALUE is ever NaN. (The method prose legitimately contains the word "NaN" — in the
        // sentence promising it never appears — so the check targets the value-bearing lines.)
        var valueLines = csv.Split('\n').Where(l => l.Contains("rhoAbsDeltaOpportunityVs", StringComparison.Ordinal))
            .Concat(markdown.Split('\n').Where(l => l.Contains("Spearman rho", StringComparison.Ordinal)));
        Assert.All(valueLines, line => Assert.DoesNotContain("NaN", line, StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantCountVector_RendersTheSharedVocabularyToken_WithTheVectorNamed()
    {
        var observations = new[]
        {
            Observation("flat", 3, 2, 5),
            Observation("flat", 9, 2, 6),
        };
        var report = new DenominatorAuditReport(
            [ScoreMoveDenominatorAudit.Compute("flat", 2, 1, observations)]);

        var markdown = new ScoreMoveDenominatorAuditRenderer().RenderMarkdown(report);

        Assert.Contains(
            "constant-returns (the count vector has no rank variance)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBin_IsRenderedAsAnEmptyRow_NeverDropped()
    {
        // The fixture has DirectionalCount 1 and 5 only: bins 0, 2 and 3 are empty.
        var markdown = new ScoreMoveDenominatorAuditRenderer().RenderMarkdown(Report());

        Assert.Contains("| 0 | 0 |  |  |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 2 | 0 |  |  |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 3 | 0 |  |  |", markdown, StringComparison.Ordinal);
        // The populated bins render their statistics.
        Assert.Contains("| 1 | 2 | 19.0 | 21.0 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 4+ | 1 | 3.0 | 3.0 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_CarriesOneRowPerObservation_WithAbsDeltaAndBothCounts()
    {
        var csv = new ScoreMoveDenominatorAuditRenderer().RenderCsv(Report());

        Assert.Contains(
            "strategy,companyId,asOfDate,deltaOpportunity,deltaTrajectory,absDeltaOpportunity,linkCount,directionalCount",
            csv,
            StringComparison.Ordinal);
        Assert.Contains(
            "default,aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa,2026-08-05,-21,-22,21,9,1",
            csv,
            StringComparison.Ordinal);
        Assert.Contains(
            "default,aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa,2026-08-05,17,16,17,28,1",
            csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_EscapesAStrategyNameContainingACommaThroughTheSharedRule()
    {
        var csv = new ScoreMoveDenominatorAuditRenderer().RenderCsv(Report("a,b"));

        Assert.Contains(
            "\"a,b\",aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa,2026-08-05,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_StatesTheHypothesisDirection()
    {
        var markdown = new ScoreMoveDenominatorAuditRenderer().RenderMarkdown(Report());

        Assert.Contains(
            "A NEGATIVE rho is the hypothesis: fewer directional signals, larger moves.",
            markdown,
            StringComparison.Ordinal);
    }
}
