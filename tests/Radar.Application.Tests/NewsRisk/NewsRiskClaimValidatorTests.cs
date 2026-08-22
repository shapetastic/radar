using Radar.Application.NewsRisk;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §6: mechanical validation — exact ordinal substring matching over EXACTLY the supplied fields
/// (no normalization before matching), enum/score/severity/confidence ranges, the advice-language guard,
/// and the fail-closed rule that an all-claims-failed ThesisChallenged becomes ValidationFailed and NEVER
/// NoRiskFoundInSuppliedText.
/// </summary>
public sealed class NewsRiskClaimValidatorTests
{
    private static readonly Guid ObsWithAll = Guid.NewGuid();
    private static readonly Guid ObsHeadlineOnly = Guid.NewGuid();

    private static readonly IReadOnlyList<NewsRiskInputArticle> Supplied =
    [
        NewsRiskTestData.Article(
            ObsWithAll,
            "Acme flags going concern doubt",
            description: "The company said substantial doubt exists about its ability to continue.",
            body: "Auditors cited a covenant breach and a shrinking cash runway."),
        NewsRiskTestData.Article(ObsHeadlineOnly, "Acme announces new dilutive share offering"),
    ];

    private static NewsRiskModelClaim Claim(
        string category = "LiquidityOrGoingConcern",
        string severity = "High",
        double? confidence = 0.9,
        string[]? observationIds = null,
        string[]? excerpts = null) => new(
        Category: category,
        Severity: severity,
        Confidence: confidence,
        ObservationIds: observationIds ?? [ObsWithAll.ToString("D")],
        Excerpts: excerpts ?? ["going concern doubt"]);

    private static NewsRiskModelResponse Response(
        string assessment = "ThesisChallenged", int? score = 70, params NewsRiskModelClaim[] claims) => new(
        Assessment: assessment,
        RiskScore: score,
        Categories: ["LiquidityOrGoingConcern"],
        Claims: claims,
        Rationale: "Coverage describes a going-concern statement.");

    [Fact]
    public void ExactSubstrings_OfHeadlineDescriptionAndBody_AllAccept()
    {
        var result = NewsRiskClaimValidator.Validate(
            Response(claims:
            [
                Claim(excerpts: ["going concern doubt"]),
                Claim(excerpts: ["substantial doubt exists"]),
                Claim(category: "DebtOrCovenant", excerpts: ["covenant breach"]),
            ]),
            Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, result.Status);
        Assert.Equal(3, result.ClaimsAccepted);
        Assert.Equal(0, result.ClaimsDropped);
        Assert.Equal(70, result.RiskScore);
        Assert.Equal(
            new[] { NewsRiskCategory.LiquidityOrGoingConcern, NewsRiskCategory.DebtOrCovenant },
            result.Categories);
    }

    [Fact]
    public void WhitespaceNormalizedExcerpt_IsRejected_AndTheDropRateIsMeasurable()
    {
        // "going  concern" (two spaces) is not an exact ordinal substring — deliberately strict, no
        // normalization before matching; the drop is counted and named so the rate is measurable.
        var result = NewsRiskClaimValidator.Validate(
            Response(claims:
            [
                Claim(excerpts: ["going  concern doubt"]),
                Claim(excerpts: ["going concern doubt"]),
            ]),
            Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, result.Status);
        Assert.Equal(2, result.ClaimsTotal);
        Assert.Equal(1, result.ClaimsAccepted);
        Assert.Equal(1, result.ClaimsDropped);
        Assert.Contains(result.ClaimDropReasons, r => r.Contains("excerpt-not-exact-substring", StringComparison.Ordinal));
    }

    [Fact]
    public void CitationOfAnOmittedField_IsRejected()
    {
        // The text exists in ANOTHER observation's body, but the cited observation supplied only a
        // headline — omitted fields are not citable text.
        var result = NewsRiskClaimValidator.Validate(
            Response(claims:
            [
                Claim(observationIds: [ObsHeadlineOnly.ToString("D")], excerpts: ["covenant breach"]),
            ]),
            Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ClaimDropReasons, r => r.Contains("excerpt-not-exact-substring", StringComparison.Ordinal));
    }

    [Fact]
    public void FabricatedObservationId_IsRejected()
    {
        var result = NewsRiskClaimValidator.Validate(
            Response(claims: [Claim(observationIds: [Guid.NewGuid().ToString("D")])]),
            Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ClaimDropReasons, r => r.Contains("cited-observation-not-supplied", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NotACategory", "High", 0.9)]
    [InlineData("LiquidityOrGoingConcern", "Catastrophic", 0.9)]
    [InlineData("LiquidityOrGoingConcern", "High", 1.5)]
    [InlineData("LiquidityOrGoingConcern", "High", -0.1)]
    [InlineData("3", "High", 0.9)] // numeric tokens must not parse as enum members
    public void OutOfRangeEnumOrConfidence_DropsTheClaim(string category, string severity, double confidence)
    {
        var result = NewsRiskClaimValidator.Validate(
            Response(claims: [Claim(category: category, severity: severity, confidence: confidence)]),
            Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ValidationFailed, result.Status);
        Assert.Equal(1, result.ClaimsDropped);
    }

    [Fact]
    public void AllClaimsFailing_IsValidationFailed_NeverNoRiskFound()
    {
        var result = NewsRiskClaimValidator.Validate(
            Response(claims: [Claim(excerpts: ["text that appears nowhere"])]),
            Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ValidationFailed, result.Status);
        Assert.NotEqual(NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText, result.Status);
        Assert.Null(result.RiskScore);
        Assert.Empty(result.Categories);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(null)]
    public void ThesisChallengedWithOutOfRangeOrMissingScore_IsValidationFailed(int? score)
    {
        var result = NewsRiskClaimValidator.Validate(Response(score: score, claims: [Claim()]), Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ClaimDropReasons, r => r.Contains("risk-score-out-of-range", StringComparison.Ordinal));
    }

    [Fact]
    public void InsufficientContent_IsNeverScored()
    {
        var result = NewsRiskClaimValidator.Validate(
            Response(assessment: "InsufficientContent", score: 5), Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.InsufficientContent, result.Status);
        Assert.Null(result.RiskScore);
    }

    [Fact]
    public void NoRiskFound_CoercesScoreAndClaimsAway()
    {
        var result = NewsRiskClaimValidator.Validate(
            Response(assessment: "NoRiskFoundInSuppliedText", score: 12, claims: [Claim()]), Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText, result.Status);
        Assert.Null(result.RiskScore);
        Assert.Empty(result.Claims);
    }

    [Fact]
    public void UnknownAssessmentToken_IsValidationFailed()
    {
        var result = NewsRiskClaimValidator.Validate(
            Response(assessment: "DefinitelyFine"), Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ClaimDropReasons, r => r.Contains("assessment-token-invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void AdviceLanguageInTheRationale_IsBlankedAndRecorded()
    {
        var response = Response(claims: [Claim()]) with
        {
            Rationale = "The coverage is negative, so sell the stock.",
        };

        var result = NewsRiskClaimValidator.Validate(response, Supplied);

        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, result.Status);
        Assert.Null(result.Rationale);
        Assert.Contains(result.ClaimDropReasons, r => r.Contains("rationale-advice-language", StringComparison.Ordinal));
    }
}
