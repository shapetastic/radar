using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.CalibrationAudit;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Tests.Filings;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 162: the calibration audit's <see cref="ModelInputTruncation"/> must reproduce the EXACT model
/// input <see cref="ChatFilingAnalyzer"/> sends — the blinded labeler judges the read "given its input",
/// so a one-character divergence would invalidate the study. Parity is asserted here STRUCTURALLY: the
/// REAL production analyzer runs against a capturing fake <see cref="IChatClient"/> and the captured user
/// message is compared byte-for-byte against the helper's output. If the analyzer's truncation ever
/// changes shape, this fails and the helper must move in the same change.
/// </summary>
public sealed class ModelInputTruncationParityTests
{
    private static string CapturedUserText(FakeChatClient client)
    {
        var user = client.CapturedMessages.Single(m => m.Role == ChatRole.User);
        return user.Text;
    }

    private static async Task<string> ProductionModelInputAsync(string input, int maxInputLength)
    {
        var client = new FakeChatClient(
            """{"direction":"Improving","confidence":0.5,"rationale":"x"}""");
        var analyzer = new ChatFilingAnalyzer(
            client,
            new FilingAnalyzerOptions { MaxInputLength = maxInputLength },
            NullLogger<ChatFilingAnalyzer>.Instance);

        _ = await analyzer.AnalyzeAsync(input, CancellationToken.None);
        Assert.Equal(1, client.CallCount);
        return CapturedUserText(client);
    }

    [Theory]
    [InlineData(50)]     // Far below the input length → truncated.
    [InlineData(4999)]   // One below → truncated by exactly one char.
    [InlineData(5000)]   // Exactly the input length → NOT truncated.
    [InlineData(5001)]   // One above → NOT truncated.
    [InlineData(12000)]  // The production default cap.
    public async Task ModelInput_Matches_TheProductionAnalyzersTruncation(int maxInputLength)
    {
        // Non-repeating content so an off-by-one can never accidentally match.
        var input = string.Concat(Enumerable.Range(0, 500).Select(i => $"para{i}: results improved. "))[..5000];

        var production = await ProductionModelInputAsync(input, maxInputLength);
        var (audit, truncated) = ModelInputTruncation.Apply(input, maxInputLength);

        Assert.Equal(production, audit);
        Assert.Equal(input.Length > maxInputLength, truncated);
    }

    [Fact]
    public async Task Truncation_IsLeadingSubstring_TruncateFirst()
    {
        const string sentinel = "SENTINEL_TAIL";
        var input = new string('A', 100) + sentinel;

        var production = await ProductionModelInputAsync(input, 100);
        var (audit, truncated) = ModelInputTruncation.Apply(input, 100);

        Assert.Equal(production, audit);
        Assert.True(truncated);
        Assert.Equal(new string('A', 100), audit);
        Assert.DoesNotContain(sentinel, audit, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPositiveCap_Throws_InsteadOfInventingAModelInput()
    {
        // The production analyzer never calls the model with a non-positive cap, so there is no model
        // input to reproduce — the audit fails loudly rather than writing an unfaithful exhibit.
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelInputTruncation.Apply("text", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelInputTruncation.Apply("text", -1));
    }
}
