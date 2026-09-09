using Radar.Application.Filings;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsRisk;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// Spec 215 §2 — the v5 prompt CONTRACT for reference values: rule (12) stated as a RULE, the two new
/// return-clause lists, and the user-message block rendered AFTER the families in exactly the spec's
/// shape — and rendered NOT AT ALL when no reference was projected, so a reference-free judgment's message
/// is byte-identical to the v4 one.
/// </summary>
public sealed class ChatNewsJudgmentAnalyzerReferenceTests
{
    private static readonly Guid ReferenceId = Guid.Parse("1e5a0000-0000-4000-8000-000000000001");

    private static NewsJudgmentInputFamily Family()
    {
        const string Statement = "Power projects lift Argan as backlog hits $2.5B";
        return new NewsJudgmentInputFamily(
            FamilyId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RepresentativeFactId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            EventTypes: [NewsEventType.EarningsOrGuidance],
            Statement: Statement,
            TemporalScope: "September 2026",
            Attribution: NewsFactAttribution.Publisher,
            AssertionStatus: NewsFactAssertionStatus.Reported,
            Confidence: 0.9,
            Citations: ["backlog hits $2.5B"],
            MemberCount: 3,
            DistinctPublisherCount: 3,
            ComparisonBasis: StatementComparisonClassifier.Classify(Statement, [NewsEventType.EarningsOrGuidance]));
    }

    private static NewsJudgmentReferenceValue Reference(
        string? priorValue = null,
        string? priorPeriod = null,
        NewsJudgmentReferenceKind kind = NewsJudgmentReferenceKind.Prior) => new(
        ReferenceId: ReferenceId,
        Metric: ReportedMetric.Backlog,
        Value: "2.929",
        Unit: "billion",
        Period: "as of January 31, 2026",
        PriorValue: priorValue,
        PriorPeriod: priorPeriod,
        FilingDateUtc: new DateTimeOffset(2026, 4, 9, 20, 0, 0, TimeSpan.Zero),
        Form: "8-K",
        Quote: "Project backlog of $2.929 billion as of January 31, 2026.",
        Kind: kind);

    [Fact]
    public void SystemInstruction_StatesRule12_ReferenceValuesAreAComparisonBasisNotNews()
    {
        var instruction = ChatNewsJudgmentAnalyzer.SystemInstruction;

        // SPEC 216 §1 restated it: a reference is the company's EARLIER statement, never the figure the
        // supplied fact itself quotes, and each one is labelled prior or stated-prior.
        Assert.Contains(
            "(12) Reference values are the company's own EARLIER statements of the same metric, and are "
                + "never the figure a supplied fact itself quotes. Each is labelled prior (a figure from an "
                + "earlier filing) or stated-prior (the comparison the newest release itself stated). When a "
                + "supplied fact quotes a metric with a reference value, read the DIRECTION from the "
                + "comparison and cite BOTH the fact and the ReferenceId. Never cite a ReferenceId as a "
                + "trajectory fact on its own — a reference value is a comparison basis, not news.",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains("TrajectoryReferenceIds (the supplied ReferenceIds", instruction, StringComparison.Ordinal);
        Assert.Contains("ReferenceIds (zero or more supplied ReferenceIds the finding compares against", instruction, StringComparison.Ordinal);
        // Rule 11 is carried forward unchanged.
        Assert.Contains("(11) A quantity stated as a LEVEL", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessage_RendersTheReferenceBlock_AfterTheFamilies_InTheSpecShape()
    {
        var message = ChatNewsJudgmentAnalyzer.BuildUserMessage(
            new NewsJudgmentAnalysisRequest("Argan", "AGX", [Family()], [Reference()]));

        const string Header = "Company-reported reference values (from SEC filings Radar read; cite by ReferenceId):";
        // SPEC 216 §1: the KIND sits after the metric, so the judge can never read a stated-prior as an
        // independent earlier filing (or either as the current value).
        const string Line =
            "ReferenceId: 1e5a0000-0000-4000-8000-000000000001 · Backlog · prior · 2.929 billion · as of January 31, 2026 · "
            + "stated in 8-K filed 2026-04-09 · \"Project backlog of $2.929 billion as of January 31, 2026.\"";
        Assert.Contains(Header, message, StringComparison.Ordinal);
        Assert.Contains(Line, message, StringComparison.Ordinal);
        // AFTER the families, never before.
        Assert.True(message.IndexOf(Header, StringComparison.Ordinal) > message.IndexOf("FactId: ", StringComparison.Ordinal));
        Assert.Equal(1, message.Split("ReferenceId: ").Length - 1);
    }

    [Fact]
    public void UserMessage_AppendsThePriorPair_OnlyWhenTheReleaseStatedOne()
    {
        Assert.Equal(
            "ReferenceId: 1e5a0000-0000-4000-8000-000000000001 · Backlog · prior · 2.929 billion (prior 2.640 billion, as of "
                + "January 31, 2025) · as of January 31, 2026 · stated in 8-K filed 2026-04-09 · "
                + "\"Project backlog of $2.929 billion as of January 31, 2026.\"",
            ChatNewsJudgmentAnalyzer.ReferenceValueLine(Reference("2.640", "as of January 31, 2025")));
        Assert.DoesNotContain(
            "(prior ", ChatNewsJudgmentAnalyzer.ReferenceValueLine(Reference()), StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessage_LabelsAStatedPriorReference_AsStatedPrior()
    {
        // SPEC 216 §1 — the newest filing's own stated comparison is projectable as its own reference, and
        // the judge is told which kind it is seeing.
        Assert.Contains(
            "· Backlog · stated-prior · 2.929 billion ·",
            ChatNewsJudgmentAnalyzer.ReferenceValueLine(
                Reference(kind: NewsJudgmentReferenceKind.StatedPrior)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessage_WithoutReferences_IsByteIdenticalToTheReferenceFreeForm()
    {
        var family = Family();
        var none = ChatNewsJudgmentAnalyzer.BuildUserMessage(new NewsJudgmentAnalysisRequest("Argan", "AGX", [family]));
        var empty = ChatNewsJudgmentAnalyzer.BuildUserMessage(new NewsJudgmentAnalysisRequest("Argan", "AGX", [family], []));

        Assert.Equal(none, empty);
        Assert.DoesNotContain("reference", none, StringComparison.OrdinalIgnoreCase);
    }
}
