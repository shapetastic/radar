using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsTyping;

namespace Radar.Infrastructure.Tests.NewsTyping;

/// <summary>
/// Spec 181 §2/§4: the extractor's prompt carries the ticker + ONE observation's supplied fields only (no
/// URL/publisher, no score/rank/price), the system instruction carries the §2 contract VERBATIM (liberal
/// extraction, preserve-don't-judge, exact citations, no cross-observation identifiers, no directional
/// question), and provider failures degrade to typed outcomes instead of throwing.
/// </summary>
public sealed class ChatNewsTypingExtractorTests
{
    private static NewsTypingInputObservation Observation(
        string headline = "Test Co widens quarterly loss",
        string? description = "Shares fell 11.8% in trading.",
        string? body = null) => new(
        ObservationId: new Guid("11111111-1111-1111-1111-111111111111"),
        Headline: headline,
        DescriptionText: description,
        BodyText: body,
        Publisher: "Example Wire",
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        PayloadHash: "ph",
        FirstObservedAtUtc: new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
        CompanyId: null,
        Ticker: "TST");

    [Fact]
    public void UserMessage_CarriesOnlyTheSuppliedFields_Tagged()
    {
        var message = ChatNewsTypingExtractor.BuildUserMessage(
            new NewsTypingExtractionRequest("TST", Observation(body: "Full body text here.")));

        Assert.Contains("Company ticker: TST", message);
        Assert.Contains("HEADLINE: Test Co widens quarterly loss", message);
        Assert.Contains("DESCRIPTION: Shares fell 11.8% in trading.", message);
        Assert.Contains("BODY: Full body text here.", message);
        // Provenance-only fields never enter the prompt.
        Assert.DoesNotContain("Example Wire", message);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", message);
    }

    [Fact]
    public void UserMessage_OmitsAbsentFields_TheyAreNotCitableText()
    {
        var message = ChatNewsTypingExtractor.BuildUserMessage(
            new NewsTypingExtractionRequest(null, Observation(description: null)));

        Assert.DoesNotContain("DESCRIPTION:", message);
        Assert.DoesNotContain("BODY:", message);
        Assert.Contains("Company: (no ticker attribution)", message);
    }

    [Fact]
    public void SystemInstruction_CarriesTheStage1ContractVerbatim()
    {
        var instruction = ChatNewsTypingExtractor.SystemInstruction;

        // The §2 preservation clause, exactly.
        Assert.Contains(
            "Preserve actors, quantities, periods, comparisons, negation, modality and attribution "
                + "exactly; do not assign investment direction, severity or materiality",
            instruction);
        Assert.Contains("EXTRACT LIBERALLY", instruction);
        Assert.Contains("do not invent identifiers", instruction);
        Assert.Contains("COPIED EXACTLY", instruction);
        Assert.Contains("InsufficientContent", instruction);
        // The closed vocabularies are enumerated from the SAME sets the validator parses.
        Assert.Contains("MarketReaction", instruction);
        Assert.Contains("plaintiff-firm", instruction);
        Assert.Contains("confirmed-filing", instruction);
    }

    [Fact]
    public void SystemInstruction_AsksNoDirectionalQuestion()
    {
        // Stage 1 never asks for a verdict: no Positive/Negative answer tokens, no severity/materiality
        // OUTPUT field. (The instruction legitimately NAMES direction/severity in its prohibition clause.)
        var instruction = ChatNewsTypingExtractor.SystemInstruction;

        Assert.DoesNotContain("ThesisChallenged", instruction);
        Assert.DoesNotContain("RiskScore", instruction);
        Assert.DoesNotContain("Severity (", instruction);
        Assert.DoesNotContain("\"Positive\"", instruction);
        Assert.DoesNotContain("\"Negative\"", instruction);
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
        var extractor = new ChatNewsTypingExtractor(
            new ThrowingChatClient(),
            new NewsTypingReaderIdentity("test", "test-provider", "model-a"),
            NullLogger<ChatNewsTypingExtractor>.Instance);

        var outcome = await extractor.ExtractAsync(
            new NewsTypingExtractionRequest("TST", Observation()), CancellationToken.None);

        Assert.Equal(NewsTypingExtractionFailure.ProviderError, outcome.Failure);
        Assert.Null(outcome.Response);
        Assert.Contains("HttpRequestException", outcome.FailureDetail);
    }
}
