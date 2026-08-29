using Radar.Application.News;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// SPEC 201 §2: <see cref="FeedTargetRelevance"/> is the ONE relevance predicate for both query-driven news
/// collectors. Spec 200's six adversarial headline pins (three rejected, three accepted, over the repaired
/// UTMD / ITIC / ESQ production feed identities) are pinned HERE on the shared type — with and without the
/// newssearch publisher-suffix hook — and separately through BOTH collectors' public surfaces
/// (<c>NewsAttentionCollectorTests</c>, <c>GdeltNewsCollectorTests</c>), so a relevance fix can no longer
/// land in one collector and silently miss the other.
/// </summary>
public sealed class FeedTargetRelevanceTests
{
    // The exact production seed shapes (spec 199/200): UTMD keeps its ticker token; ITIC and ESQ are
    // phrase-only because the unanchored Contains would match "cr-itic-" and "Esq-uire".
    private static readonly QueryFeedTarget Utmd = new("Utah Medical Products", "UTMD");
    private static readonly QueryFeedTarget Itic = new("Investors Title Company", null);
    private static readonly QueryFeedTarget Esq = new("Esquire Financial", null);

    private static QueryFeedTarget TargetFor(string ticker) => ticker switch
    {
        "UTMD" => Utmd,
        "ITIC" => Itic,
        "ESQ" => Esq,
        _ => throw new ArgumentOutOfRangeException(nameof(ticker), ticker, "Not a spec-200 fixture ticker."),
    };

    public static TheoryData<string, string> Spec200RejectedHeadlines => new()
    {
        { "UTMD", "University of Utah Medical School opens a new centre" },
        { "ITIC", "Investors title technology as their top theme" },
        { "ESQ", "Esquire names its people of the year" },
    };

    public static TheoryData<string, string> Spec200AcceptedHeadlines => new()
    {
        { "UTMD", "Utah Medical Products reports quarterly results" },
        { "ITIC", "Investors Title Company declares a dividend" },
        { "ESQ", "Esquire Financial expands litigation banking" },
    };

    [Theory]
    [MemberData(nameof(Spec200RejectedHeadlines))]
    public void SharedRule_RejectsTheAdversarialHeadline_WithAndWithoutThePublisherSuffixHook(
        string ticker, string headline)
    {
        var target = TargetFor(ticker);

        // GDELT shape: no suffix, no hook.
        Assert.False(FeedTargetRelevance.IsRelevant(headline, target));

        // Newssearch shape: Google News appends " - Publisher", stripped by the per-caller hook.
        Assert.False(FeedTargetRelevance.IsRelevant(
            headline + " - Reuters", target, GoogleNewsHeadline.StripPublisherSuffix));
    }

    [Theory]
    [MemberData(nameof(Spec200AcceptedHeadlines))]
    public void SharedRule_AcceptsTheIssuerHeadline_WithAndWithoutThePublisherSuffixHook(
        string ticker, string headline)
    {
        var target = TargetFor(ticker);

        Assert.True(FeedTargetRelevance.IsRelevant(headline, target));
        Assert.True(FeedTargetRelevance.IsRelevant(
            headline + " - Reuters", target, GoogleNewsHeadline.StripPublisherSuffix));
    }

    [Fact]
    public void PublisherSuffixHook_IsWhatStopsAPublisherNameFromMatching()
    {
        // The divergent edge, isolated: a publisher whose name contains the ticker matches WITHOUT the hook
        // and is rejected WITH it. This is why the strip stays a per-caller hook rather than shared core.
        var target = new QueryFeedTarget("Rocket Lab", "RKLB");
        const string title = "Space stocks drift lower - RKLB Daily";

        Assert.True(FeedTargetRelevance.IsRelevant(title, target));
        Assert.False(FeedTargetRelevance.IsRelevant(title, target, GoogleNewsHeadline.StripPublisherSuffix));
    }

    [Fact]
    public void WhitespaceNormalisation_LetsSpacedPunctuationMatch()
    {
        // GDELT spaces out punctuation; both sides are normalised, so "( MRCY )" contains MRCY and
        // "Mercury Systems , Inc ." contains the phrase.
        var target = new QueryFeedTarget("Mercury Systems", "MRCY");

        Assert.True(FeedTargetRelevance.IsRelevant("Defense movers : ( MRCY ) climbs", target));
        Assert.True(FeedTargetRelevance.IsRelevant("Mercury  Systems , Inc . wins award", target));
        Assert.False(FeedTargetRelevance.IsRelevant("MASSPHOTON Launches Mercury Water System", target));
        Assert.False(FeedTargetRelevance.IsRelevant(null, target));
        Assert.False(FeedTargetRelevance.IsRelevant("   ", target));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   \t \n ", "")]
    [InlineData("  one\ttime \r\n litigation   settlement ", "one time litigation settlement")]
    [InlineData("already clean", "already clean")]
    public void NormalizeWhitespace_CollapsesEveryRunToOneSpace_AndTrims(string? input, string expected)
    {
        Assert.Equal(expected, FeedTargetRelevance.NormalizeWhitespace(input));
    }
}
