using Radar.Application.Efficacy.FilingReads;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.Efficacy.FilingReads;

/// <summary>
/// The closed adverse vocabulary is a declaration, so its properties are asserted rather than assumed: it is
/// versioned, it is closed, it matches on WORD boundaries, and every member carries a justification a reader
/// will see rendered in the artifact.
/// </summary>
public sealed class FilingReadDisagreementVocabularyTests
{
    [Fact]
    public void TheVocabulary_IsVersioned_AndEveryMemberCarriesAJustification()
    {
        Assert.Equal("filing-read-disagreement-v1", FilingReadDisagreementVocabulary.Version);
        Assert.NotEmpty(FilingReadDisagreementVocabulary.NegativePhrases);
        Assert.All(
            FilingReadDisagreementVocabulary.NegativePhrases,
            p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Phrase));
                Assert.False(string.IsNullOrWhiteSpace(p.Justification));
                Assert.Equal(p.Phrase.ToLowerInvariant(), p.Phrase);
            });
        Assert.All(
            FilingReadDisagreementVocabulary.NegativeEventTypes,
            e => Assert.False(string.IsNullOrWhiteSpace(e.Justification)));
    }

    [Fact]
    public void OnlyTheDefinitionallyAdverseEventType_IsDeclared()
    {
        var declared = Assert.Single(FilingReadDisagreementVocabulary.NegativeEventTypes);
        Assert.Equal(NewsEventType.ShortSellerOrCritique, declared.EventType);
    }

    [Theory]
    [InlineData("Revenue declined nine percent.", true)]
    [InlineData("The company completed its mission ahead of schedule.", false)]
    [InlineData("Shares dropped after the release.", true)]
    [InlineData("Backlog rose on a large customer win.", false)]
    [InlineData("A write-down of goodwill was recorded.", true)]
    public void PhraseMatching_IsWordBoundaryAnchored_AndCaseInsensitive(
        string statement, bool expectedMatch)
    {
        var fact = Fact(statement, NewsEventType.MarketReaction);
        Assert.Equal(expectedMatch, FilingReadDisagreementVocabulary.TryMatch(fact) is not null);
        Assert.Equal(
            expectedMatch,
            FilingReadDisagreementVocabulary.TryMatch(Fact(statement.ToUpperInvariant(),
                NewsEventType.MarketReaction)) is not null);
    }

    [Fact]
    public void AMatch_NamesExactlyWhatMatched_AndANonMatchIsNullRatherThanAnEmptyMatch()
    {
        var match = FilingReadDisagreementVocabulary.TryMatch(
            Fact("A short seller alleged fraud.", NewsEventType.ShortSellerOrCritique));

        Assert.NotNull(match);
        Assert.Equal(
            [nameof(NewsEventType.ShortSellerOrCritique)], match!.MatchedEventTypes);
        Assert.Contains("short seller", match.MatchedPhrases);
        Assert.Contains("fraud", match.MatchedPhrases);

        // A non-match is null: a caller can never count an empty match record as a match.
        Assert.Null(FilingReadDisagreementVocabulary.TryMatch(
            Fact("The board approved a new facility.", NewsEventType.ProductOrTechnology)));
    }

    private static NewsTypingValidatedFact Fact(string statement, NewsEventType type) => new(
        FactId: Guid.NewGuid(),
        EventTypes: [type],
        Statement: statement,
        TemporalScope: null,
        Attribution: NewsFactAttribution.Publisher,
        AssertionStatus: NewsFactAssertionStatus.Reported,
        Confidence: 0.9,
        Citations: []);
}
