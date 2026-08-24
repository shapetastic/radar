using System.Globalization;

using Radar.Application.Identity;
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

    // ── Spec 187 §1: the v2 prompt CONTRACT ─────────────────────────────────────────────────────────
    //
    // These tests establish the CONTRACT the judge is given. They deliberately do NOT claim that a unit
    // test guarantees future model judgment — no unit test can. The live artifact
    // (data/news-risk/…/news-risk-live-*.md) remains the evidence of actual behaviour; what is pinned here
    // is that Radar ASKED for the right thing, in rules rather than in commentary.

    [Fact]
    public void SystemInstruction_IsPinned_SoAWordingChangeCannotStayInsidePromptV2()
    {
        // A prompt edit that keeps the same PromptVersion would silently pool incomparable judgments in one
        // cohort. This hash is the change-detector: if it fails, either revert the wording or bump
        // NewsJudgmentContract.PromptVersion (which forks the cohort) in the SAME change.
        const string Pinned = "c77ec39fca69c58764eea509d8cdba46622f147e12475a2d453e52470f789013";
        var actual = CanonicalHash.Sha256Hex(ChatNewsJudgmentAnalyzer.SystemInstruction);
        var matchesPin = string.Equals(Pinned, actual, StringComparison.Ordinal);

        Assert.True(
            matchesPin,
            $"The stage-2 judge system instruction changed (pinned {Pinned}, actual {actual}). A wording "
                + "change may NOT stay inside news-judgment-prompt-v2: bump "
                + "NewsJudgmentContract.PromptVersion in the SAME change — it forks the stage-2 cohort key, "
                + "so old and new judgments can never be reused for each other or pooled — and update this "
                + "pin to the new hash. If the change was unintended, revert it.");
    }

    [Fact]
    public void SystemInstruction_NamesEveryContextOnlyEventType_SoTheSetAndThePromptCannotDriftApart()
    {
        // The context-only SET (Application) and the prompt's rule (5) are NOT one rendered list, and cannot
        // be: the prompt states a deliberately BROADER context class, naming institutional holdings/trades
        // and conference attendance, for which taxonomy v1 has no token (spec 187 §1's KGS note). What IS
        // enforceable — and enforced here — is the containment direction that actually matters: every
        // context-only MEMBER must be named in the instruction the model is given.
        //
        // Matching rule: no member is named by its taxonomy token in the prompt (the instruction speaks
        // plain English), so each member declares its prompt wording in
        // NewsJudgmentContextOnlyEventTypes.PromptPhrases, and the assertion is a case-INSENSITIVE
        // substring match of that declared phrase against the instruction — case-insensitive because a
        // phrase may open a sentence ("Share-price moves …") while reading lower-case in the table.
        var instruction = ChatNewsJudgmentAnalyzer.SystemInstruction;
        var phrases = NewsJudgmentContextOnlyEventTypes.PromptPhrases;

        // Every member declares wording, and nothing else does: adding a member without declaring its
        // prompt phrase fails HERE, before the substring check can pass vacuously.
        Assert.Equal(
            NewsJudgmentContextOnlyEventTypes.Members.Order().ToArray(),
            phrases.Keys.Order().ToArray());

        foreach (var member in NewsJudgmentContextOnlyEventTypes.Members)
        {
            var phrase = phrases[member];
            Assert.True(
                instruction.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                $"Context-only event type {member} declares the prompt phrase \"{phrase}\", but the stage-2 "
                    + "judge system instruction does not contain it. The validator would drop facts of a "
                    + "kind the model was never told to treat as context. Add the wording to rule (5) of "
                    + "ChatNewsJudgmentAnalyzer.SystemInstruction (or correct the declared phrase in "
                    + "NewsJudgmentContextOnlyEventTypes.PromptPhrases), then bump "
                    + "NewsJudgmentContract.PromptVersion in the SAME change — a prompt edit may not stay "
                    + "inside the current cohort — and update the pinned instruction hash above.");
        }
    }

    [Fact]
    public void SystemInstruction_StatesTheV2GroundingRules_AsRules()
    {
        var instruction = ChatNewsJudgmentAnalyzer.SystemInstruction;

        // Make the call the supplied BUSINESS facts support, even when it may be wrong (the MNRO shape:
        // v1's "a directional choice is required" produced a label its own rationale disowned).
        Assert.Contains(
            "Make the best directional call the supplied BUSINESS facts support",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains("even when that call may later prove wrong", instruction, StringComparison.Ordinal);

        // Mixed vs Unknown, stated as a rule — Unknown is honest, not a last resort.
        Assert.Contains("genuinely pull in opposing directions", instruction, StringComparison.Ordinal);
        Assert.Contains(
            "do not establish a direction at all", instruction, StringComparison.Ordinal);

        // The CASS/WDFC shapes: absence is never evidence, in either direction.
        Assert.Contains(
            "Absence of adverse evidence is NOT evidence of improvement",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "absence of positive evidence is NOT evidence of deterioration",
            instruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never infer a direction from what the supplied facts fail to mention",
            instruction,
            StringComparison.Ordinal);

        // Marker mechanics are not a reason to choose a trajectory; the model neither sees nor controls them.
        Assert.Contains("marker", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "neither see nor control presentation policy", instruction, StringComparison.Ordinal);

        // The YORW/KGS shapes: price moves, ratings, index changes, institutional holdings/trades,
        // conference attendance and promotional coverage are CONTEXT, never business trajectory.
        Assert.Contains("Share-price moves", instruction, StringComparison.Ordinal);
        Assert.Contains("analyst targets or ratings", instruction, StringComparison.Ordinal);
        Assert.Contains("index changes", instruction, StringComparison.Ordinal);
        Assert.Contains("institutional holdings or trades", instruction, StringComparison.Ordinal);
        Assert.Contains("conference attendance", instruction, StringComparison.Ordinal);
        Assert.Contains("promotional or listicle coverage", instruction, StringComparison.Ordinal);

        // Syndication breadth is corroboration of REPORTING (kept from v1) and weak assertion cannot
        // establish direction alone (the EOSE shape).
        Assert.Contains("never how many independent facts", instruction, StringComparison.Ordinal);
        Assert.Contains(
            "does not establish the overall direction on its own", instruction, StringComparison.Ordinal);

        // The new response contract.
        Assert.Contains("TrajectoryFactIds", instruction, StringComparison.Ordinal);
        Assert.Contains("EMPTY", instruction, StringComparison.Ordinal);
        Assert.Contains("REQUIRED non-blank factual Rationale", instruction, StringComparison.Ordinal);
        Assert.Contains(
            NewsJudgmentValidator.MaxRationaleLength.ToString(CultureInfo.InvariantCulture),
            instruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessage_NamesTrajectoryFactIds_SoCitationsComeFromTheSuppliedSetOnly()
    {
        var message = ChatNewsJudgmmentUserMessage(Family());

        Assert.Contains(
            "Cite FactIds from this set only — in TrajectoryFactIds", message, StringComparison.Ordinal);
    }

    private static string ChatNewsJudgmmentUserMessage(NewsJudgmentInputFamily family) =>
        ChatNewsJudgmentAnalyzer.BuildUserMessage(
            new NewsJudgmentAnalysisRequest("Eos Energy", "EOSE", [family]));
}
