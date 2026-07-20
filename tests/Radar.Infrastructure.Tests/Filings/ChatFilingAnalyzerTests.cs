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

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveMaxInputLength_DegradesToUnknown_WithoutCallingModel(int maxInputLength)
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.8,"rationale":"x"}""");
        var analyzer = Build(client, maxInputLength: maxInputLength);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        AssertUnknown(result);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task RationaleWithAdviceLanguage_IsScrubbed_DirectionRetained()
    {
        // The model ignored the prompt and emitted advice language; the rationale must be dropped defensively.
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.8,"rationale":"Strong quarter — a guaranteed safe bet, buy now."}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        Assert.Equal(FilingDirection.Improving, result.Direction);
        Assert.Equal(0.8m, result.Confidence);
        Assert.Equal(string.Empty, result.Rationale);
    }

    [Fact]
    public async Task LegitimateReleaseTerms_AreNotScrubbedAsAdvice()
    {
        // "buyback" / "seller" contain banned substrings but are not advice — whole-word matching keeps them.
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.7,"rationale":"Announced a share buyback; a top seller drove growth."}""");
        var analyzer = Build(client);

        var result = await analyzer.AnalyzeAsync("some release text", CancellationToken.None);

        Assert.Equal(FilingDirection.Improving, result.Direction);
        Assert.Equal("Announced a share buyback; a top seller drove growth.", result.Rationale);
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

    [Fact]
    public void SystemInstruction_ContainsProfitabilityVersusGrowthRule()
    {
        // Spec 116: the prompt is the behavioural contract — the earnings read must weigh REPORTED
        // profitability/margin/cash-burn against REPORTED top-line growth, and a record top line paired with a
        // deeply negative/deteriorating gross margin (or guidance cut, or heavy cash burn) is Mixed, NOT Improving.
        var prompt = ChatFilingAnalyzer.SystemInstruction;

        Assert.Contains("profitability", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gross margin", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cash burn", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mixed (materially both), NOT Improving", prompt, StringComparison.Ordinal);
        // The rule must not be a blanket downgrade — genuinely one-sided strong releases stay Improving.
        Assert.Contains("not a bearish bias", prompt, StringComparison.OrdinalIgnoreCase);
        // A profitability-driven Mixed call must name the fact in the rationale.
        Assert.Contains("must name that fact", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemInstruction_KeepsAsReportedNotConsensusClause()
    {
        var prompt = ChatFilingAnalyzer.SystemInstruction;

        Assert.Contains("AS REPORTED", prompt, StringComparison.Ordinal);
        Assert.Contains("NOT a beat-vs-consensus judgement", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemInstruction_KeepsNoAdviceLanguageClause()
    {
        var prompt = ChatFilingAnalyzer.SystemInstruction;

        Assert.Contains("NOT investment advice", prompt, StringComparison.Ordinal);
        Assert.Contains("NO advice language", prompt, StringComparison.Ordinal);
        Assert.Contains("\"buy\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"sell\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"guaranteed\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"safe bet\"", prompt, StringComparison.Ordinal);
        // The Unknown-on-ambiguity guardrail must also survive the spec-116 rewrite.
        Assert.Contains("return Unknown with a low", prompt, StringComparison.Ordinal);
    }

    private static void AssertUnknown(FilingSentiment result)
    {
        Assert.Equal(FilingDirection.Unknown, result.Direction);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal(string.Empty, result.Rationale);
    }
}
