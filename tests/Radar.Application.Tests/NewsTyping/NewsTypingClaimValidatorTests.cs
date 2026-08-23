using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 181 §2: mechanical validation mirrors the spec-179 rules — exact ordinal citation matching against
/// the SUPPLIED fields only, closed-vocabulary token parsing with named drop reasons, confidence bounds,
/// fail-closed all-invalid handling — while the liberal-extraction contract makes ZERO emitted facts a
/// completed typing, not a failure. <c>DerivedPrimaryType</c> is derived deterministically, never authored.
/// </summary>
public sealed class NewsTypingClaimValidatorTests
{
    private static NewsTypingValidationResult Validate(
        NewsTypingModelResponse response, NewsTypingInputObservation? input = null) =>
        NewsTypingClaimValidator.Validate(
            response, input ?? NewsTypingTestData.Input(), NewsTypingTestData.CohortKey);

    [Fact]
    public void ValidFact_WithExactCitation_IsAccepted()
    {
        var result = Validate(new NewsTypingModelResponse("CompanySpecific", [NewsTypingTestData.Fact()]));

        Assert.Equal(NewsTypingStatus.Typed, result.Status);
        Assert.Equal(NewsTypingRelevance.CompanySpecific, result.Relevance);
        var fact = Assert.Single(result.Facts);
        Assert.Equal([NewsEventType.EarningsOrGuidance], fact.EventTypes);
        Assert.Equal(NewsFactAttribution.Publisher, fact.Attribution);
        Assert.Equal(NewsFactAssertionStatus.Reported, fact.AssertionStatus);
        Assert.Equal(0.9, fact.Confidence);
        Assert.Equal(1, result.FactsAccepted);
        Assert.Equal(0, result.FactsDropped);
        Assert.Equal(NewsEventType.EarningsOrGuidance, result.DerivedPrimaryType);
    }

    [Fact]
    public void ParaphrasedCitation_IsDroppedIndividually_AndFactSurvivesOnVerifiedRemainder()
    {
        // The stage-1 omission-bias guard: unlike spec 179, one bad citation does not kill a fact that
        // still carries verified support — the invalid citation is removed and NAMED.
        var fact = NewsTypingTestData.Fact(
            citations: ["the loss got bigger this quarter", "widens quarterly loss to $5 million"]);

        var result = Validate(new NewsTypingModelResponse("CompanySpecific", [fact]));

        Assert.Equal(NewsTypingStatus.Typed, result.Status);
        var accepted = Assert.Single(result.Facts);
        Assert.Equal(["widens quarterly loss to $5 million"], accepted.Citations);
        Assert.Contains(
            result.FactDropReasons,
            r => r.Contains("citation-not-exact-substring-of-supplied-text", StringComparison.Ordinal));
    }

    [Fact]
    public void FactWithZeroValidCitations_IsDropped_WithNamedReason()
    {
        var fact = NewsTypingTestData.Fact(citations: ["completely invented excerpt"]);

        var result = Validate(new NewsTypingModelResponse("CompanySpecific", [fact]));

        Assert.Equal(NewsTypingStatus.ValidationFailed, result.Status);
        Assert.Empty(result.Facts);
        Assert.Contains(result.FactDropReasons, r => r.Contains("no-valid-citation", StringComparison.Ordinal));
    }

    [Fact]
    public void CitationAgainstOmittedField_IsNotSuppliedText()
    {
        // The description is omitted for this observation, so text that would match it is uncitable.
        var input = NewsTypingTestData.Input(description: null);
        var fact = NewsTypingTestData.Fact(citations: ["shares fell 11.8% in trading"]);

        var result = NewsTypingClaimValidator.Validate(
            new NewsTypingModelResponse("CompanySpecific", [fact]), input, NewsTypingTestData.CohortKey);

        Assert.Equal(NewsTypingStatus.ValidationFailed, result.Status);
    }

    [Fact]
    public void BodyText_WhenSupplied_IsCitable()
    {
        var input = NewsTypingTestData.Input(body: "The SEC opened a formal investigation on Tuesday.");
        var fact = NewsTypingTestData.Fact(
            eventTypes: ["RegulatoryOrLegal"],
            attribution: "regulator",
            assertionStatus: "confirmed-filing",
            citations: ["opened a formal investigation"]);

        var result = NewsTypingClaimValidator.Validate(
            new NewsTypingModelResponse("CompanySpecific", [fact]), input, NewsTypingTestData.CohortKey);

        Assert.Equal(NewsTypingStatus.Typed, result.Status);
        Assert.Single(result.Facts);
    }

    [Fact]
    public void UnknownEventTypeToken_DropsTheFact_NamingTheToken()
    {
        var fact = NewsTypingTestData.Fact(eventTypes: ["EarningsOrGuidance", "MemeSqueeze"]);

        var result = Validate(new NewsTypingModelResponse("CompanySpecific", [fact]));

        Assert.Equal(NewsTypingStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FactDropReasons, r => r.Contains("event-type-invalid: 'MemeSqueeze'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("plaintiff-firm", NewsFactAttribution.PlaintiffFirm)]
    [InlineData("short-seller", NewsFactAttribution.ShortSeller)]
    [InlineData("other-specified", NewsFactAttribution.OtherSpecified)]
    [InlineData("Regulator", NewsFactAttribution.Regulator)]
    public void AttributionTokens_ParseTheClosedVocabulary(string token, NewsFactAttribution expected)
    {
        var result = Validate(new NewsTypingModelResponse(
            "CompanySpecific", [NewsTypingTestData.Fact(attribution: token)]));

        Assert.Equal(expected, Assert.Single(result.Facts).Attribution);
    }

    [Theory]
    [InlineData("confirmed-filing", NewsFactAssertionStatus.ConfirmedFiling)]
    [InlineData("alleged", NewsFactAssertionStatus.Alleged)]
    [InlineData("solicited", NewsFactAssertionStatus.Solicited)]
    public void AssertionStatusTokens_ParseTheClosedVocabulary(string token, NewsFactAssertionStatus expected)
    {
        var result = Validate(new NewsTypingModelResponse(
            "CompanySpecific", [NewsTypingTestData.Fact(assertionStatus: token)]));

        Assert.Equal(expected, Assert.Single(result.Facts).AssertionStatus);
    }

    [Theory]
    [InlineData("shareholder")]
    [InlineData("2")]
    [InlineData(null)]
    public void UnknownAttributionToken_DropsTheFact(string? token)
    {
        var result = Validate(new NewsTypingModelResponse(
            "CompanySpecific", [NewsTypingTestData.Fact(attribution: token)]));

        Assert.Equal(NewsTypingStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FactDropReasons, r => r.Contains("attribution-invalid", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(null)]
    public void ConfidenceOutOfRange_DropsTheFact(double? confidence)
    {
        var result = Validate(new NewsTypingModelResponse(
            "CompanySpecific", [NewsTypingTestData.Fact(confidence: confidence)]));

        Assert.Equal(NewsTypingStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FactDropReasons, r => r.Contains("confidence-out-of-range", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidRelevanceToken_IsValidationFailed_NeverASilentDefault()
    {
        var result = Validate(new NewsTypingModelResponse("VeryRelevant", [NewsTypingTestData.Fact()]));

        Assert.Equal(NewsTypingStatus.ValidationFailed, result.Status);
        Assert.Null(result.Relevance);
        Assert.Contains(
            result.FactDropReasons, r => r.Contains("relevance-token-invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroEmittedFacts_WithParsedRelevance_IsTypedWithEmptyFacts()
    {
        // The liberal-extraction contract (spec 181): "nothing to extract" is a legitimate completed
        // answer — ONLY "the model emitted facts and all were invalid" fails.
        var result = Validate(new NewsTypingModelResponse("NotAboutThisCompany", Facts: []));

        Assert.Equal(NewsTypingStatus.Typed, result.Status);
        Assert.Empty(result.Facts);
        Assert.Null(result.DerivedPrimaryType);
        Assert.Equal(0, result.FactsTotal);
    }

    [Fact]
    public void AllEmittedFactsInvalid_IsValidationFailed()
    {
        var result = Validate(new NewsTypingModelResponse(
            "CompanySpecific",
            [
                NewsTypingTestData.Fact(eventTypes: ["Bogus"]),
                NewsTypingTestData.Fact(citations: ["invented"]),
            ]));

        Assert.Equal(NewsTypingStatus.ValidationFailed, result.Status);
        Assert.Equal(2, result.FactsTotal);
        Assert.Equal(2, result.FactsDropped);
    }

    [Fact]
    public void InsufficientContentRelevance_IsCompletedInsufficientContent_KeepingValidFacts()
    {
        var result = Validate(new NewsTypingModelResponse("InsufficientContent", [NewsTypingTestData.Fact()]));

        Assert.Equal(NewsTypingStatus.InsufficientContent, result.Status);
        Assert.Single(result.Facts);
    }

    [Fact]
    public void DerivedPrimaryType_IsGreatestSummedConfidence()
    {
        var facts = new[]
        {
            NewsTypingTestData.Fact(eventTypes: ["EarningsOrGuidance"], confidence: 0.4),
            NewsTypingTestData.Fact(
                eventTypes: ["MarketReaction"],
                confidence: 0.5,
                citations: ["shares fell 11.8%"]),
            NewsTypingTestData.Fact(
                eventTypes: ["MarketReaction"],
                confidence: 0.3,
                citations: ["shares fell 11.8%"]),
        };

        var result = Validate(new NewsTypingModelResponse("CompanySpecific", facts));

        // MarketReaction sums to 0.8 > EarningsOrGuidance 0.4.
        Assert.Equal(NewsEventType.MarketReaction, result.DerivedPrimaryType);
    }

    [Fact]
    public void DerivedPrimaryType_TieBreaksOnTaxonomyDeclarationOrder()
    {
        // MarketReaction (index 8) and EarningsOrGuidance (index 0) tie at 0.5 exactly — the earlier
        // declared member wins, deterministically.
        var facts = new[]
        {
            NewsTypingTestData.Fact(eventTypes: ["MarketReaction"], confidence: 0.5, citations: ["shares fell 11.8%"]),
            NewsTypingTestData.Fact(eventTypes: ["EarningsOrGuidance"], confidence: 0.5),
        };

        var result = Validate(new NewsTypingModelResponse("CompanySpecific", facts));

        Assert.Equal(NewsEventType.EarningsOrGuidance, result.DerivedPrimaryType);
    }

    [Fact]
    public void FactIds_AreDeterministic_AndStableUnderSiblingDrops()
    {
        // The id derives from the WIRE index: the surviving fact at index 1 keeps the same id whether or
        // not index 0 survived, so ids never shift as validation drops siblings.
        var badThenGood = new[]
        {
            NewsTypingTestData.Fact(eventTypes: ["Bogus"]),
            NewsTypingTestData.Fact(),
        };
        var goodAlone = new[]
        {
            NewsTypingTestData.Fact(statement: "different sibling", citations: ["Test Co"]),
            NewsTypingTestData.Fact(),
        };

        var first = Validate(new NewsTypingModelResponse("CompanySpecific", badThenGood));
        var second = Validate(new NewsTypingModelResponse("CompanySpecific", goodAlone));

        Assert.Equal(
            Assert.Single(first.Facts).FactId,
            second.Facts.Single(f => f.Statement != "different sibling").FactId);
        Assert.Equal(
            NewsTypingClaimValidator.FactIdFor(
                NewsTypingTestData.CohortKey,
                NewsTypingTestData.Input().ObservationId,
                NewsTypingTestData.Input().PayloadHash,
                1),
            Assert.Single(first.Facts).FactId);
    }
}
