using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 187 §1 — <c>news-judgment-v2</c>: the trajectory must CITE the supplied facts that establish it,
/// and a judged response must carry a bounded factual rationale.
/// <para>
/// The fixtures are named for the FIRST live judged run's failures (2026-08-24, run
/// <c>976d0f20</c>), because that run is the evidence this slice exists to answer. Each test states which
/// seam actually closes the failure: some are MECHANICAL (this validator refuses the response), and some
/// are PROMPT-CONTRACT (the instruction states the rule; the pinned prompt test in
/// <c>ChatNewsJudgmentAnalyzerTests</c> is the guard). No unit test can guarantee a future model judgment —
/// the live artifact remains the evidence of actual behaviour.
/// </para>
/// </summary>
public sealed class NewsJudgmentTrajectoryEvidenceTests
{
    private static NewsJudgmentInputFamily ContextFamily(
        NewsEventType type,
        NewsFactAssertionStatus assertionStatus = NewsFactAssertionStatus.Reported,
        Guid? factId = null) =>
        NewsJudgmentTestData.Family(factId: factId, assertionStatus: assertionStatus) with
        {
            EventTypes = [type],
        };

    // ── The provenance rules ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Improving")]
    [InlineData("Deteriorating")]
    [InlineData("Mixed")]
    public void DirectionalTrajectory_WithNoCitedEvidence_IsValidationFailed(string trajectory)
    {
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: trajectory, strength: null, findings: [], trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-evidence-missing"));
        Assert.Empty(result.TrajectoryFactIds);
    }

    [Fact]
    public void UnknownTrajectory_WithCitedEvidence_IsValidationFailed()
    {
        // Unknown means "the supplied facts established no direction"; citing evidence FOR that non-claim
        // is a contradiction, not extra provenance.
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Unknown",
            strength: null,
            findings: [],
            trajectoryFactIds: [family.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-evidence-with-unknown"));
    }

    [Fact]
    public void UnknownTrajectory_WithNoCitedEvidence_IsTheHonestJudgedRead()
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.Solicited);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Unknown", strength: null, findings: [], trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentTrajectory.Unknown, result.BusinessTrajectory);
        Assert.Empty(result.TrajectoryFactIds);
    }

    [Fact]
    public void TrajectoryFactNotSupplied_IsValidationFailed()
    {
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            findings: [], strength: null, trajectoryFactIds: [Guid.NewGuid().ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-fact-not-supplied"));
    }

    [Fact]
    public void UnparseableTrajectoryFactId_IsValidationFailed_NeverCoerced()
    {
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            findings: [], strength: null, trajectoryFactIds: ["the-first-fact"]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-fact-not-supplied"));
    }

    [Fact]
    public void DuplicateTrajectoryFactId_IsValidationFailed_AfterOrdinalPreservingNormalization()
    {
        // Two SPELLINGS of one id are one id: Guid parsing is the normalization, so casing and formatting
        // cannot smuggle a second citation past the distinctness rule.
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var id = family.RepresentativeFactId;
        var response = NewsJudgmentTestData.Response(
            findings: [],
            strength: null,
            trajectoryFactIds: [id.ToString("D").ToUpperInvariant(), id.ToString("B")]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-fact-duplicate"));
    }

    [Fact]
    public void CitedTrajectoryEvidence_IsProjectedOntoTheValidatedResult()
    {
        var strong = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            trajectoryFactIds: [strong.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [strong]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(strong.RepresentativeFactId, Assert.Single(result.TrajectoryFactIds));
    }

    // ── The rationale rule (LENGTH is spec 192's; see NewsJudgmentRationaleLengthTests) ───────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingRationale_IsValidationFailed_NeverACleanLookingZeroFindingRead(string? rationale)
    {
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: rationale,
            trajectoryFactIds: [family.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        // Spec 192 §1 leaves this rule alone, so the reason string is pinned BYTE-IDENTICALLY: only the
        // LENGTH rule moved, and a genuinely absent explanation must keep failing exactly as it did.
        Assert.Equal(
            "rationale-missing: a judged response requires a non-blank factual rationale",
            Assert.Single(result.FindingDropReasons));
        Assert.Null(result.Rationale);
        // Nothing survived to be measured, and 0 says exactly that (never "not recorded", which on a
        // persisted record means "written before spec 192").
        Assert.Equal(0, result.RationaleLength);
        Assert.False(result.RationaleOverSoftLimit);
    }

    [Fact]
    public void RationaleExactlyAtTheSoftBound_IsAcceptedAndNotFlagged()
    {
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: new string('x', NewsJudgmentValidator.MaxRationaleLength),
            trajectoryFactIds: [family.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentValidator.MaxRationaleLength, result.RationaleLength);
        Assert.False(result.RationaleOverSoftLimit); // the soft bound is inclusive
    }

    // ── The live failure shapes (spec 187 §1's explicit list) ────────────────────────────────────────

    [Fact]
    public void MnroShaped_NeutralEsgEvidence_WithAnAdmittedlyNonDeterioratingRationale_CannotPass()
    {
        // MECHANICAL. The live v1 record's own rationale said the supplied ESG fact was neutral and did not
        // evidence deterioration, then labelled the trajectory Deteriorating "because the instruction
        // required a directional choice". Under v2 that response cannot pass with OMITTED trajectory ids.
        // The complementary half — that `Unknown` is the correct answer when direction is not established,
        // rather than an escape hatch — is a PINNED PROMPT RULE, not something a validator can decide.
        var esg = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Reported,
            statement: "The company published its annual sustainability report.") with
        {
            EventTypes = [NewsEventType.OtherSpecified],
        };
        var response = NewsJudgmentTestData.Response(
            trajectory: "Deteriorating",
            strength: null,
            findings: [],
            rationale: "The supplied ESG disclosure is neutral and does not evidence deterioration.",
            trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [esg]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-evidence-missing"));
    }

    [Fact]
    public void CassShaped_DirectionInferredFromMissingContext_CannotPass()
    {
        // MECHANICAL for the provenance half: the live v1 record inferred deterioration from the ABSENCE of
        // positive context, so there was no supplied fact it could cite. An absence is not a supplied fact
        // and there is no id for it — the response therefore fails on missing trajectory evidence. That
        // absence is never evidence in EITHER direction is stated as a pinned PROMPT rule.
        var routine = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Reported,
            statement: "The company scheduled its quarterly results call.");
        var response = NewsJudgmentTestData.Response(
            trajectory: "Deteriorating",
            strength: null,
            findings: [],
            rationale: "No positive business developments were supplied for the period.",
            trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [routine]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-evidence-missing"));
    }

    [Fact]
    public void WdfcShaped_ImprovingByDefaultBecauseAdverseEvidenceWasAbsent_CannotPass()
    {
        // MECHANICAL. "Improving because nothing bad was supplied" has no directional fact to cite, so it
        // cannot pass this validator at all. The rule that absence of adverse evidence is not evidence of
        // improvement is stated as a pinned PROMPT rule — the validator enforces its consequence.
        var routine = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Reported,
            statement: "The company appointed a new regional sales manager.") with
        {
            EventTypes = [NewsEventType.ManagementOrGovernance],
        };
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: "No adverse developments appear among the supplied facts.",
            trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [routine]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-evidence-missing"));
    }

    [Fact]
    public void KgsShaped_AnInstitutionalHoldingIsPromptDeclaredContext_NotAFabricatedTextRule()
    {
        // PROMPT-CONTRACT, deliberately. Taxonomy v1 has no ownership-context token — an institutional
        // investment is typed by whichever member the extractor judged closest — so a mechanical rule here
        // would have to classify TEXT, which is exactly the brittle keyword rule spec 187 §1 forbids. The
        // contract instead NAMES institutional holdings/trades as context that does not establish the
        // investee's business trajectory on its own. This test pins that the rule is stated; the pinned
        // instruction hash keeps it from being quietly removed.
        Assert.Contains(
            NewsEventType.AnalystOrRatingAction, NewsJudgmentContextOnlyEventTypes.Members);
        Assert.DoesNotContain(
            NewsEventType.MergerAcquisitionOrStake, NewsJudgmentContextOnlyEventTypes.Members);
    }

    [Fact]
    public void YorwShaped_APureMarketReactionFamily_CannotEstablishDeteriorating()
    {
        // MECHANICAL (rule 5): a 52-week share-price low is a price-move report, not a business trajectory.
        var priceMove = ContextFamily(NewsEventType.MarketReaction);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Deteriorating",
            strength: null,
            findings: [],
            rationale: "The shares touched a 52-week low.",
            trajectoryFactIds: [priceMove.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [priceMove]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FindingDropReasons,
            r => r.Contains("trajectory-" + NewsJudgmentValidator.NonBusinessContextOnlyReason));
    }

    [Fact]
    public void YorwShaped_ABusinessExecutionFindingOnAPureMarketReaction_IsDroppedIndividually()
    {
        // MECHANICAL (rule 6): the finding is dropped by its own NAMED reason — a share-price fall is not
        // an ExecutionOrMissedMilestone. Because it was the only finding, the response then fails closed
        // under the pre-existing all-findings-invalid rule.
        var priceMove = ContextFamily(NewsEventType.MarketReaction);
        var business = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: 40,
            findings:
            [
                NewsJudgmentTestData.Finding(
                    priceMove.RepresentativeFactId, category: "ExecutionOrMissedMilestone"),
            ],
            rationale: "Filed results improved; the shares nonetheless fell.",
            trajectoryFactIds: [business.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [business, priceMove]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FindingDropReasons,
            r => r.Contains(NewsJudgmentValidator.NonBusinessContextOnlyReason));
    }

    [Fact]
    public void AFindingCitingOneBusinessFactBesideAPriceMove_Survives()
    {
        // The rule is "ENTIRELY confined": one supplied business fact behind the reaction is enough, so the
        // guard removes context-only findings without removing legitimate ones.
        var priceMove = ContextFamily(NewsEventType.MarketReaction);
        var business = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "Deteriorating",
            strength: 55,
            findings:
            [
                new NewsJudgmentModelFinding(
                    "ExecutionOrMissedMilestone",
                    "Medium",
                    0.7,
                    [
                        business.RepresentativeFactId.ToString("D"),
                        priceMove.RepresentativeFactId.ToString("D"),
                    ],
                    AttributionCaveat: null),
            ],
            rationale: "Filed results missed the prior guidance; the shares fell on the release.",
            trajectoryFactIds: [business.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [business, priceMove]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void EoseShaped_ASolicitedLegalFactCannotEstablishTheOverallDirection()
    {
        // MECHANICAL: the live v1 record's ONLY finding rested on a plaintiff-law-firm solicitation, and
        // the same weak fact silently drove the uncited trajectory axis. Naming it now fails with
        // trajectory-assertion-too-weak.
        var solicitation = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Solicited,
            attribution: NewsFactAttribution.PlaintiffFirm) with
        {
            EventTypes = [NewsEventType.RegulatoryOrLegal],
        };
        var response = NewsJudgmentTestData.Response(
            trajectory: "Deteriorating",
            strength: 60,
            findings:
            [
                NewsJudgmentTestData.Finding(
                    solicitation.RepresentativeFactId,
                    caveat: "Based solely on a plaintiff-firm solicitation; no filing is confirmed."),
            ],
            rationale: "A plaintiff firm announced an investigation.",
            trajectoryFactIds: [solicitation.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [solicitation]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FindingDropReasons,
            r => r.Contains(NewsJudgmentValidator.TrajectoryAssertionTooWeakReason));
    }

    [Fact]
    public void EoseShaped_TheSameSolicitedFactStillCarriesACaveatedChallengeUnderAnUnknownTrajectory()
    {
        // The other half of the EOSE rule, and the reason it is not simply "discard weak evidence": weak
        // evidence may CHALLENGE with a caveat while establishing no overall direction.
        var solicitation = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Solicited,
            attribution: NewsFactAttribution.PlaintiffFirm) with
        {
            EventTypes = [NewsEventType.RegulatoryOrLegal],
        };
        var response = NewsJudgmentTestData.Response(
            trajectory: "Unknown",
            strength: 60,
            findings:
            [
                NewsJudgmentTestData.Finding(
                    solicitation.RepresentativeFactId,
                    caveat: "Based solely on a plaintiff-firm solicitation; no filing is confirmed."),
            ],
            rationale: "A plaintiff firm announced an investigation; nothing else was supplied.",
            trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [solicitation]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentTrajectory.Unknown, result.BusinessTrajectory);
        Assert.Single(result.Findings);
        Assert.Empty(result.TrajectoryFactIds);
    }

    [Fact]
    public void ChallengeFindings_NeedNotCiteTheSameFactsAsTheTrajectory()
    {
        // Deliberate (spec 187 §1): an overall Improving read may still carry a specific caveated challenge.
        var improving = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var challenge = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Reported,
            statement: "A regulator opened a review of one product line.") with
        {
            EventTypes = [NewsEventType.RegulatoryOrLegal],
        };
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: 35,
            findings: [NewsJudgmentTestData.Finding(challenge.RepresentativeFactId)],
            rationale: "Filed results improved while one product line entered regulatory review.",
            trajectoryFactIds: [improving.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [improving, challenge]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(improving.RepresentativeFactId, Assert.Single(result.TrajectoryFactIds));
        Assert.Equal(challenge.RepresentativeFactId, Assert.Single(Assert.Single(result.Findings).FactIds));
    }

    [Fact]
    public void ZeroFindingDeteriorating_RemainsLegitimate_WhenItCitesTheBusinessFacts()
    {
        // Spec 186 §1's marker behaviour is UNCHANGED: this is exactly the record that renders
        // "⚠ challenged (business-trajectory-deteriorating)". v2 only requires it to be evidenced.
        var decline = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling,
            statement: "The company filed results showing revenue down 18% and a wider loss.");
        var response = NewsJudgmentTestData.Response(
            trajectory: "Deteriorating",
            strength: null,
            findings: [],
            rationale: "Filed results show revenue down 18% with a wider loss.",
            trajectoryFactIds: [decline.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [decline]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentTrajectory.Deteriorating, result.BusinessTrajectory);
        Assert.Empty(result.Findings);
        Assert.Equal(decline.RepresentativeFactId, Assert.Single(result.TrajectoryFactIds));
    }

    [Fact]
    public void AssertionStatusAndEventTypes_AreReadFromTheSuppliedRepresentativeFact_Only()
    {
        // Conservative by design (spec 187 §1): the validator reasons only over what the judge was SHOWN.
        // A family whose representative is Speculative cannot be upgraded by an unprovided member — there
        // is no member data on the supplied type at all, so the rule holds structurally.
        var speculative = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Speculative, memberCount: 40) with
        {
            DistinctPublisherCount = 25,
        };
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: "Widely reported speculation about a possible contract.",
            trajectoryFactIds: [speculative.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [speculative]);

        // 40 syndicated copies of a speculative claim are still one speculative claim.
        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FindingDropReasons,
            r => r.Contains(NewsJudgmentValidator.TrajectoryAssertionTooWeakReason));
    }

    [Fact]
    public void ContextOnlySet_IsTheSameSetForBothRules_AndAdmitsAnyOtherEventType()
    {
        Assert.Equal(
            [
                NewsEventType.AnalystOrRatingAction,
                NewsEventType.MarketReaction,
                NewsEventType.IndexOrTradingMechanics,
                NewsEventType.PromotionalOrListicle,
            ],
            NewsJudgmentContextOnlyEventTypes.Members);

        Assert.True(NewsJudgmentContextOnlyEventTypes.IsConfinedTo(
            [NewsEventType.MarketReaction, NewsEventType.PromotionalOrListicle]));
        Assert.False(NewsJudgmentContextOnlyEventTypes.IsConfinedTo(
            [NewsEventType.MarketReaction, NewsEventType.EarningsOrGuidance]));

        // "We cannot tell" must not read as "we can reject": a type-less family is not context-only.
        Assert.False(NewsJudgmentContextOnlyEventTypes.IsConfinedTo([]));
    }
}
