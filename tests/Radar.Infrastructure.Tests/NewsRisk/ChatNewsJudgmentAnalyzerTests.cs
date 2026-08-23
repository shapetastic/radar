using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsRisk;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// Spec 185 §1/§2 — the judge PROMPT is part of the contract: the fixed rubric verbatim, the attribution
/// weighting and caveat obligations as PROMPT rules, syndication counts stated as corroboration of
/// reporting, and a user message carrying EXACTLY the typed-fact fields — no raw prose, no headline, no
/// URL, no publisher, no score/rank/label/price. Changing the instruction text obliges a
/// <see cref="NewsJudgmentContract.PromptVersion"/> bump (a new cohort).
/// </summary>
public sealed class ChatNewsJudgmentAnalyzerTests
{
    private static NewsJudgmentInputFamily Family(
        string statement = "A plaintiff law firm announced an investigation into the company.") => new(
        FamilyId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        RepresentativeFactId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        EventTypes: [NewsEventType.RegulatoryOrLegal],
        Statement: statement,
        TemporalScope: "August 2026",
        Attribution: NewsFactAttribution.PlaintiffFirm,
        AssertionStatus: NewsFactAssertionStatus.Solicited,
        Confidence: 0.9,
        Citations: ["announced an investigation"],
        MemberCount: 40,
        DistinctPublisherCount: 25);

    [Fact]
    public void SystemInstruction_CarriesTheFixedRubricVerbatim_AndTheAttributionRules()
    {
        var instruction = ChatNewsJudgmentAnalyzer.SystemInstruction;

        // The one fixed evaluation target (spec 185 §2) — never a per-company thesis.
        Assert.Contains("the company's recent business trajectory", instruction, StringComparison.Ordinal);
        // Attribution weighting is a PROMPT rule, not post-hoc.
        Assert.Contains("plaintiff-firm", instruction, StringComparison.Ordinal);
        Assert.Contains("\"may face\"", instruction, StringComparison.Ordinal);
        Assert.Contains("AttributionCaveat", instruction, StringComparison.Ordinal);
        // Syndication counts corroborate REPORTING, never independent facts.
        Assert.Contains("never how many independent facts", instruction, StringComparison.Ordinal);
        // Challenge-only findings; the balance axis is the trajectory.
        Assert.Contains("CHALLENGE-ONLY", instruction, StringComparison.Ordinal);
        Assert.Contains("BusinessTrajectory", instruction, StringComparison.Ordinal);
        // The closed vocabularies are rendered from the same sets the validator parses.
        Assert.Contains("RegulatoryOrLegalSetback", instruction, StringComparison.Ordinal);
        Assert.Contains("\"Improving\" | \"Deteriorating\" | \"Mixed\" | \"Unknown\"", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessage_CarriesTheTypedFactFieldsOnly_OneEntryPerFamily()
    {
        var family = Family();
        var message = ChatNewsJudgmmentUserMessage(family);

        Assert.Contains("Company: Eos Energy (EOSE)", message, StringComparison.Ordinal);
        Assert.Contains("FactId: " + family.RepresentativeFactId.ToString("D"), message, StringComparison.Ordinal);
        Assert.Contains("Statement: " + family.Statement, message, StringComparison.Ordinal);
        Assert.Contains("TemporalScope: August 2026", message, StringComparison.Ordinal);
        Assert.Contains(
            "Attribution: PlaintiffFirm · AssertionStatus: Solicited", message, StringComparison.Ordinal);
        Assert.Contains(
            "Reported by 40 syndicated cop(ies) across 25 publisher(s) — one claim.",
            message, StringComparison.Ordinal);

        // ONE FactId line per family: the 40-outlet story reaches the judge exactly once.
        Assert.Equal(
            1,
            message.Split("FactId: ", StringSplitOptions.None).Length - 1);

        // No provenance the judge must not weigh: no URL, no publisher name, no headline label.
        Assert.DoesNotContain("http", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HEADLINE", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Syndicated Outlet", message, StringComparison.Ordinal);
    }

    private static string ChatNewsJudgmmentUserMessage(NewsJudgmentInputFamily family) =>
        ChatNewsJudgmentAnalyzer.BuildUserMessage(
            new NewsJudgmentAnalysisRequest("Eos Energy", "EOSE", [family]));
}
