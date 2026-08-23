using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 181 §3: taxonomy v1 is declared, hashed and immutable by convention. The hash pin is a
/// CHANGE-DETECTOR: any member add/remove/rename/reorder fails here, and the only green fixes are revert or
/// declare <c>news-event-taxonomy-v2</c> (new enum, new version, new hash — cohorts never pool across
/// versions).
/// </summary>
public sealed class NewsEventTaxonomyTests
{
    [Fact]
    public void TaxonomyV1_HashAndCanonicalString_ArePinned()
    {
        Assert.Equal("news-event-taxonomy-v1", NewsEventTaxonomy.TaxonomyVersion);
        Assert.Equal(
            "radar:news-event-taxonomy-v1:EarningsOrGuidance|MergerAcquisitionOrStake|FinancingOrDilution"
                + "|ProductOrTechnology|ContractOrCustomerWin|RegulatoryOrLegal|ManagementOrGovernance"
                + "|AnalystOrRatingAction|MarketReaction|IndexOrTradingMechanics|ShortSellerOrCritique"
                + "|DividendOrBuyback|PromotionalOrListicle|OtherSpecified",
            NewsEventTaxonomy.CanonicalString);
        Assert.Equal(
            "078f53452ac8bf28526f29704f5d06a345bfae3b7bcbbf54661a2a8193555f5c",
            NewsEventTaxonomy.TaxonomyHash);
    }

    [Fact]
    public void TaxonomyV1_HasExactlyTheFourteenSpecMembers_InDeclarationOrder()
    {
        Assert.Equal(
            [
                NewsEventType.EarningsOrGuidance,
                NewsEventType.MergerAcquisitionOrStake,
                NewsEventType.FinancingOrDilution,
                NewsEventType.ProductOrTechnology,
                NewsEventType.ContractOrCustomerWin,
                NewsEventType.RegulatoryOrLegal,
                NewsEventType.ManagementOrGovernance,
                NewsEventType.AnalystOrRatingAction,
                NewsEventType.MarketReaction,
                NewsEventType.IndexOrTradingMechanics,
                NewsEventType.ShortSellerOrCritique,
                NewsEventType.DividendOrBuyback,
                NewsEventType.PromotionalOrListicle,
                NewsEventType.OtherSpecified,
            ],
            NewsEventTaxonomy.Members);
    }

    [Theory]
    [InlineData("EarningsOrGuidance", NewsEventType.EarningsOrGuidance)]
    [InlineData("marketreaction", NewsEventType.MarketReaction)]
    [InlineData(" ShortSellerOrCritique ", NewsEventType.ShortSellerOrCritique)]
    public void TryParse_AcceptsMemberTokens_CaseInsensitively(string token, NewsEventType expected)
    {
        Assert.True(NewsEventTaxonomy.TryParse(token, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3")]
    [InlineData("Earnings")]
    [InlineData("SomethingElse")]
    public void TryParse_RejectsUnknownBlankAndNumericTokens(string? token)
    {
        Assert.False(NewsEventTaxonomy.TryParse(token, out _));
    }

    [Fact]
    public void CohortKey_FoldsPromptSchemaAndTaxonomyVersions()
    {
        // Taxonomy IS a cohort dimension (spec 181 §3): the key carries all three contract versions.
        var key = NewsTypingContract.CohortKey("openai", "deepseek-ai/DeepSeek-V4-Flash");

        Assert.Equal(
            "openai:deepseek-ai/DeepSeek-V4-Flash|news-typing-prompt-v1|news-typing-schema-v1"
                + "|news-event-taxonomy-v1",
            key);
    }
}
