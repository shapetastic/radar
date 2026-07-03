using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Filings;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

public sealed class ChatFilingAnalyzerTests
{
    private static ChatFilingAnalyzer Build(FakeChatClient client, int maxInputLength = 12000)
        => new(client, new FilingAnalyzerOptions { MaxInputLength = maxInputLength }, NullLogger<ChatFilingAnalyzer>.Instance);

    [Fact]
    public async Task ValidSentiment_IsMappedThrough_Unchanged()
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.8,"rationale":"Record bookings and raised outlook."}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("Q3 results: record bookings.", CancellationToken.None);

        Assert.Equal(FilingDirection.Improving, result.Direction);
        Assert.Equal(0.8m, result.Confidence);
        Assert.Equal("Record bookings and raised outlook.", result.Rationale);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Confidence_AboveOne_IsClampedToOne()
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":1.7,"rationale":"x"}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        Assert.Equal(FilingDirection.Improving, result.Direction);
        Assert.Equal(1.0m, result.Confidence);
    }

    [Fact]
    public async Task Confidence_BelowZero_IsClampedToZero()
    {
        var client = new FakeChatClient(
            """{"direction":"Deteriorating","confidence":-0.4,"rationale":"x"}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        Assert.Equal(FilingDirection.Deteriorating, result.Direction);
        Assert.Equal(0m, result.Confidence);
    }

    [Fact]
    public async Task UndefinedDirection_IsCoercedToUnknown_WithZeroConfidence()
    {
        // Numeric enum 99 deserializes to (FilingDirection)99; Enum.IsDefined coerces it to Unknown.
        var client = new FakeChatClient(
            """{"direction":99,"confidence":0.7,"rationale":"x"}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        Assert.Equal(FilingDirection.Unknown, result.Direction);
        Assert.Equal(0m, result.Confidence);
    }

    [Fact]
    public async Task ProviderException_DegradesToUnknown_NoThrow()
    {
        var client = new FakeChatClient(string.Empty, throwOnCall: new InvalidOperationException("provider down"));
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        AssertUnknown(result);
    }

    [Fact]
    public async Task EmptyResponseText_DegradesToUnknown_NoThrow()
    {
        var client = new FakeChatClient("   ");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        AssertUnknown(result);
    }

    [Fact]
    public async Task UnparseableResponse_DegradesToUnknown_NoThrow()
    {
        var client = new FakeChatClient("not json");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        AssertUnknown(result);
    }

    [Fact]
    public async Task UnknownDirectionString_DegradesToUnknown_NoThrow()
    {
        // An unknown enum STRING throws JsonException inside TryGetResult → false → Unknown.
        var client = new FakeChatClient(
            """{"direction":"Sideways","confidence":0.5,"rationale":"x"}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        AssertUnknown(result);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.8,"rationale":"x"}""");
        var analyzer = Build(client);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync("non-empty release text so the short-circuit does not mask cancel", cts.Token));
    }

    [Fact]
    public async Task Input_IsTruncated_BeforeTheModelCall()
    {
        const string sentinel = "SENTINEL_TAIL_MUST_BE_DROPPED";
        var head = new string('A', 40);
        var input = head + sentinel; // sentinel begins at index 40; MaxInputLength 50 cuts it off.
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.5,"rationale":"x"}""");
        var analyzer = Build(client, maxInputLength: 50);

        _ = await analyzer.AnalyzeAsync(input, CancellationToken.None);

        var combined = string.Concat(client.CapturedMessages.Select(m => m.Text));
        Assert.DoesNotContain(sentinel, combined, StringComparison.Ordinal);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task EmptyInput_ShortCircuits_WithoutCallingModel()
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.8,"rationale":"x"}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("", CancellationToken.None);

        AssertUnknown(result);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task NullInput_ShortCircuits_WithoutCallingModel()
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.8,"rationale":"x"}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync(null!, CancellationToken.None);

        AssertUnknown(result);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Rationale_ContainsNoAdviceLanguage()
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.8,"rationale":"Record bookings and raised full-year outlook."}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        foreach (var banned in new[] { "buy", "sell", "guaranteed", "safe bet" })
        {
            Assert.DoesNotContain(banned, result.Rationale, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertUnknown(FilingSentiment result)
    {
        Assert.Equal(FilingDirection.Unknown, result.Direction);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal(string.Empty, result.Rationale);
    }
}
