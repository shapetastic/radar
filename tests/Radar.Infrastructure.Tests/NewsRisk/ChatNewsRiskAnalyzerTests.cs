using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Infrastructure.NewsRisk;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// Spec 179 §5: the analyzer's prompt carries company identity + ordered id-labelled SUPPLIED text only
/// (no URL/publisher, no score/rank/price/outcome), forbids advice recommendations, and provider/parse
/// failures degrade to typed outcomes instead of throwing.
/// </summary>
public sealed class ChatNewsRiskAnalyzerTests
{
    private static NewsRiskInputArticle Article(
        Guid id, string headline, string? description, string? body) => new(
        ObservationId: id,
        Headline: headline,
        DescriptionText: description,
        BodyText: body,
        Publisher: "Example Wire",
        Url: "https://example.com/secret-url-token",
        PublishedAtUtc: null,
        RetrievedAtUtc: DateTimeOffset.UtcNow,
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        PayloadHash: "ph",
        BodyContentHash: body is null ? null : "bh",
        BodyRetrievedAtUtc: body is null ? null : DateTimeOffset.UtcNow,
        BodyExtractorVersion: null,
        BodyRetrievalPolicy: null);

    [Fact]
    public void UserMessage_CarriesOnlySuppliedTextFields_LabelledByObservationId()
    {
        var withAll = Guid.NewGuid();
        var headlineOnly = Guid.NewGuid();
        var request = new NewsRiskAnalysisRequest(
            "Test Co",
            "TST",
            [
                Article(withAll, "Headline one", "Description one", "Body one"),
                Article(headlineOnly, "Headline two", null, null),
            ]);

        var message = ChatNewsRiskAnalyzer.BuildUserMessage(request);

        Assert.Contains("Test Co", message);
        Assert.Contains("TST", message);
        Assert.Contains($"[observation {withAll:D}]", message);
        Assert.Contains($"[observation {headlineOnly:D}]", message);
        Assert.Contains("HEADLINE: Headline one", message);
        Assert.Contains("DESCRIPTION: Description one", message);
        Assert.Contains("BODY: Body one", message);
        Assert.Contains("HEADLINE: Headline two", message);
        // Omitted fields are not rendered — they are not citable text.
        Assert.Equal(1, message.Split("DESCRIPTION:").Length - 1);
        Assert.Equal(1, message.Split("BODY:").Length - 1);
        // Provenance-only fields never enter the prompt.
        Assert.DoesNotContain("secret-url-token", message);
        Assert.DoesNotContain("Example Wire", message);
    }

    [Fact]
    public void SystemInstruction_CarriesTheSpec179PromptRequirements()
    {
        var instruction = ChatNewsRiskAnalyzer.SystemInstruction;

        Assert.Contains("never recommend buying, selling, shorting or holding", instruction);
        Assert.Contains("FACILITY", instruction);
        Assert.Contains("HISTORICAL loss", instruction);
        Assert.Contains("claims, not verified facts", instruction);
        Assert.Contains("not automatically thesis-breaking", instruction);
        Assert.Contains("conflicting evidence visible", instruction);
        Assert.Contains("InsufficientContent", instruction);
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("host unreachable");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task ProviderFailure_DegradesToATypedOutcome_NeverThrows()
    {
        var analyzer = new ChatNewsRiskAnalyzer(
            new ThrowingChatClient(),
            new NewsRiskReaderIdentity("test", "test-provider", "model-a"),
            NullLogger<ChatNewsRiskAnalyzer>.Instance);

        var outcome = await analyzer.AnalyzeAsync(
            new NewsRiskAnalysisRequest("Test Co", "TST", [Article(Guid.NewGuid(), "h", null, null)]),
            CancellationToken.None);

        Assert.Equal(NewsRiskAnalysisFailure.ProviderError, outcome.Failure);
        Assert.Null(outcome.Response);
        Assert.Contains("HttpRequestException", outcome.FailureDetail);
    }
}
