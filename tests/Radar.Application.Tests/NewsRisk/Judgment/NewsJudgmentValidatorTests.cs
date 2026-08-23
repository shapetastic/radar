using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 185 §2 — mechanical validation of the judge response: closed-vocabulary token parsing, supplied
/// FactId citation, the attribution-caveat rule (attribution must DEMONSTRABLY change judgments), the
/// advice-language guard, the all-invalid ⇒ ValidationFailed fail-closed rule, and the zero-findings
/// supportive read. Validation is MemberCount-blind by construction — nothing here reads family size.
/// </summary>
public sealed class NewsJudgmentValidatorTests
{
    [Fact]
    public void ValidResponse_WithOneSupportedFinding_IsJudged()
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId)]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentTrajectory.Deteriorating, result.BusinessTrajectory);
        Assert.Equal(60, result.ChallengeStrength);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(NewsRiskCategory.RegulatoryOrLegalSetback, finding.Category);
        Assert.Equal(NewsRiskSeverity.High, finding.Severity);
        Assert.Equal(family.RepresentativeFactId, Assert.Single(finding.FactIds));
    }

    [Fact]
    public void KebabCaseCategoryToken_ParsesIntoTheReusedSpec179Vocabulary()
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            findings:
            [
                NewsJudgmentTestData.Finding(
                    family.RepresentativeFactId, category: "regulatory-or-legal-setback", severity: "high"),
            ]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(
            NewsRiskCategory.RegulatoryOrLegalSetback, Assert.Single(result.Findings).Category);
    }

    [Fact]
    public void InvalidTrajectoryToken_IsValidationFailed()
    {
        var family = NewsJudgmentTestData.Family();
        var response = NewsJudgmentTestData.Response(trajectory: "ToTheMoon");

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Null(result.BusinessTrajectory);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("trajectory-token-invalid"));
    }

    [Fact]
    public void NumericTrajectoryToken_IsRejected_NeverCoerced()
    {
        var result = NewsJudgmentValidator.Validate(
            NewsJudgmentTestData.Response(trajectory: "2"), [NewsJudgmentTestData.Family()]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
    }

    [Fact]
    public void AllFindingsInvalid_IsValidationFailed_NeverTheNoChallengeRead()
    {
        // Spec 185 §2, verbatim: a response whose findings are ALL invalid is ValidationFailed and renders
        // `? unassessed` — never "no challenge found in supplied facts".
        var family = NewsJudgmentTestData.Family();
        var response = NewsJudgmentTestData.Response(
            findings:
            [
                NewsJudgmentTestData.Finding(Guid.NewGuid()), // not supplied
                NewsJudgmentTestData.Finding(family.RepresentativeFactId, category: "NotACategory"),
                NewsJudgmentTestData.Finding(family.RepresentativeFactId, confidence: 1.5),
            ]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(3, result.FindingsTotal);
        Assert.Equal(3, result.FindingsDropped);
        Assert.Equal(3, result.FindingDropReasons.Count);
    }

    [Fact]
    public void ZeroEmittedFindings_WithParsedTrajectory_IsTheSupportiveRead()
    {
        // `BusinessTrajectory=Improving` with zero findings IS the supportive read (spec 185 §2) — status
        // Judged, no findings, strength normalized to null whatever the model sent.
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving", strength: 85, findings: []);

        var result = NewsJudgmentValidator.Validate(response, [NewsJudgmentTestData.Family()]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentTrajectory.Improving, result.BusinessTrajectory);
        Assert.Empty(result.Findings);
        Assert.Null(result.ChallengeStrength);
    }

    [Fact]
    public void CitedFactNotSupplied_DropsTheFinding_WithANamedReason()
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            findings:
            [
                NewsJudgmentTestData.Finding(family.RepresentativeFactId),
                NewsJudgmentTestData.Finding(Guid.NewGuid()),
            ]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Single(result.Findings);
        Assert.Equal(1, result.FindingsDropped);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("cited-fact-not-supplied"));
    }

    [Fact]
    public void FindingWithNoFactIds_IsDropped()
    {
        var family = NewsJudgmentTestData.Family();
        var response = NewsJudgmentTestData.Response(
            findings:
            [
                new NewsJudgmentModelFinding("RegulatoryOrLegalSetback", "High", 0.8, [], null),
            ]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("no-cited-fact"));
    }

    // ── The attribution-caveat rule: attribution demonstrably changes judgments (spec 185 §2) ──────────

    [Theory]
    [InlineData(NewsFactAssertionStatus.Alleged)]
    [InlineData(NewsFactAssertionStatus.Solicited)]
    [InlineData(NewsFactAssertionStatus.Speculative)]
    public void AllegedOnlyFinding_WithoutACaveat_IsDropped(NewsFactAssertionStatus belowReported)
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: belowReported);
        var response = NewsJudgmentTestData.Response(
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId, caveat: null)]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status); // its only finding dropped
        Assert.Contains(result.FindingDropReasons, r => r.Contains("missing-attribution-caveat"));
    }

    [Fact]
    public void AllegedOnlyFinding_WithACaveat_Survives()
    {
        var family = NewsJudgmentTestData.Family(
            assertionStatus: NewsFactAssertionStatus.Solicited,
            attribution: NewsFactAttribution.PlaintiffFirm);
        var response = NewsJudgmentTestData.Response(
            findings:
            [
                NewsJudgmentTestData.Finding(
                    family.RepresentativeFactId,
                    caveat: "Based solely on a plaintiff-firm solicitation; no filing is confirmed."),
            ]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("plaintiff-firm solicitation", finding.AttributionCaveat);
    }

    [Theory]
    [InlineData(NewsFactAssertionStatus.ConfirmedFiling)]
    [InlineData(NewsFactAssertionStatus.Reported)]
    [InlineData(NewsFactAssertionStatus.Announced)]
    public void AtOrAboveReportedSupport_NeedsNoCaveat(NewsFactAssertionStatus atOrAbove)
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: atOrAbove);
        var response = NewsJudgmentTestData.Response(
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId, caveat: null)]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void OneConfirmedFactAmongAllegedSupport_LiftsTheCaveatObligation()
    {
        // The rule is "EVERY supporting fact below reported": one confirmed filing in the support set means
        // the finding does not rest solely on allegation.
        var alleged = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.Alleged);
        var confirmed = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            findings:
            [
                new NewsJudgmentModelFinding(
                    "RegulatoryOrLegalSetback",
                    "High",
                    0.8,
                    [alleged.RepresentativeFactId.ToString("D"), confirmed.RepresentativeFactId.ToString("D")],
                    AttributionCaveat: null),
            ]);

        var result = NewsJudgmentValidator.Validate(response, [alleged, confirmed]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void AllegedVsConfirmedFixtures_ProduceDifferentOutcomes()
    {
        // The spec's acceptance shape: the SAME caveat-less finding over alleged-only support drops, over
        // confirmed-filing support survives — attribution demonstrably changes the judgment.
        var factId = Guid.NewGuid();
        var response = NewsJudgmentTestData.Response(
            findings: [NewsJudgmentTestData.Finding(factId, caveat: null)]);

        var overAlleged = NewsJudgmentValidator.Validate(
            response,
            [NewsJudgmentTestData.Family(factId: factId, assertionStatus: NewsFactAssertionStatus.Alleged)]);
        var overConfirmed = NewsJudgmentValidator.Validate(
            response,
            [
                NewsJudgmentTestData.Family(
                    factId: factId, assertionStatus: NewsFactAssertionStatus.ConfirmedFiling),
            ]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, overAlleged.Status);
        Assert.Equal(NewsJudgmentStatus.Judged, overConfirmed.Status);
        Assert.Single(overConfirmed.Findings);
    }

    // ── Advice-language guard ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RationaleWithAdviceLanguage_IsBlankedAndCounted()
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId)],
            rationale: "You should buy this stock now.");

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Null(result.Rationale);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("rationale-advice-language"));
    }

    [Fact]
    public void CaveatWithAdviceLanguage_IsBlanked_AndTheAllegedOnlyFindingThenDrops()
    {
        // A blanked caveat on an alleged-only finding leaves the finding without its obligatory caveat, so
        // the finding drops — never a challenge whose stated basis was advice language.
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.Alleged);
        var response = NewsJudgmentTestData.Response(
            findings:
            [
                NewsJudgmentTestData.Finding(
                    family.RepresentativeFactId, caveat: "Sell now before the lawsuit lands."),
            ]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("caveat-advice-language"));
        Assert.Contains(result.FindingDropReasons, r => r.Contains("missing-attribution-caveat"));
    }

    // ── Strength / confidence ranges ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(null)]
    public void SurvivingFindings_WithInvalidChallengeStrength_AreValidationFailed(int? strength)
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            strength: strength,
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId)]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.Contains("challenge-strength-out-of-range"));
        // The whole response failed, so the record is internally consistent: no accepted findings
        // survive it, and the drop reason (not FindingsAccepted) carries the pre-failure count.
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.FindingsAccepted);
        Assert.Equal(result.FindingsTotal, result.FindingsDropped);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void OutOfRangeConfidence_DropsTheFinding(double confidence)
    {
        var family = NewsJudgmentTestData.Family();
        var response = NewsJudgmentTestData.Response(
            findings: [NewsJudgmentTestData.Finding(family.RepresentativeFactId, confidence: confidence)]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Contains(result.FindingDropReasons, r => r.Contains("confidence-out-of-range"));
    }

    // ── Family size is structurally invisible to validation ──────────────────────────────────────────

    [Fact]
    public void Validation_IsMemberCountBlind_IdenticalOverAFortyMemberAndASingleMemberFamily()
    {
        var factId = Guid.NewGuid();
        var single = NewsJudgmentTestData.Family(
            factId: factId, assertionStatus: NewsFactAssertionStatus.ConfirmedFiling, memberCount: 1);
        var syndicated = single with { MemberCount = 40, DistinctPublisherCount = 25 };
        var response = NewsJudgmentTestData.Response(
            findings: [NewsJudgmentTestData.Finding(factId)]);

        var overSingle = NewsJudgmentValidator.Validate(response, [single]);
        var overSyndicated = NewsJudgmentValidator.Validate(response, [syndicated]);

        // Field-for-field identical: syndication volume multiplies nothing at the validation seam.
        Assert.Equal(overSingle.Status, overSyndicated.Status);
        Assert.Equal(overSingle.BusinessTrajectory, overSyndicated.BusinessTrajectory);
        Assert.Equal(overSingle.ChallengeStrength, overSyndicated.ChallengeStrength);
        Assert.Equal(overSingle.FindingsTotal, overSyndicated.FindingsTotal);
        Assert.Equal(overSingle.FindingsAccepted, overSyndicated.FindingsAccepted);
        Assert.Equal(overSingle.FindingsDropped, overSyndicated.FindingsDropped);
        var a = Assert.Single(overSingle.Findings);
        var b = Assert.Single(overSyndicated.Findings);
        Assert.Equal(a.Category, b.Category);
        Assert.Equal(a.Severity, b.Severity);
        Assert.Equal(a.Confidence, b.Confidence);
        Assert.Equal(a.FactIds, b.FactIds);
    }
}
