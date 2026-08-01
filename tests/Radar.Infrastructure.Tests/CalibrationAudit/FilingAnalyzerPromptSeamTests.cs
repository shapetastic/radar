using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Tests.Filings;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 164: the instruction seam is <b>byte-identical by default</b>. <see cref="FilingAnalyzerPrompt"/>
/// was extracted out of <see cref="ChatFilingAnalyzer"/> so the shadow-read research pass can REPLACE the
/// system instruction without a second copy of the prompt assembly; production passes no instruction, so its
/// assembled prompt — instruction bytes, message roles and order, truncation rule — must be exactly what it
/// was before the extraction.
/// <para>
/// The instruction is pinned here as a full string (plus its length and SHA-256), captured from the
/// pre-change source. A change to the production prompt is a legitimate act — but it is an AI-descriptor /
/// scoring-fingerprint-moving act, so it must be a conscious update of this pin, never a silent side effect
/// of a refactor.
/// </para>
/// </summary>
public sealed class FilingAnalyzerPromptSeamTests
{
    /// <summary>The production system instruction as it stood BEFORE the spec-164 extraction.</summary>
    private const string PreSpec164ProductionInstruction =
        """
        You are Radar, a research assistant. You are given the plain text of a company's earnings-release press release. Classify the business trajectory the release DESCRIBES AS REPORTED — this is NOT a beat-vs-consensus judgement (there is no analyst-consensus feed) — into exactly one of: Improving (record bookings, organic growth, raised outlook), Deteriorating (revenue decline, guidance cut, impairment), Mixed (materially both), or Unknown. Weigh REPORTED profitability, gross margin, and cash burn against REPORTED top-line growth — a strong top line alone does not make the trajectory Improving. In particular: when record or growing revenue coexists with a deeply negative or deteriorating gross margin, with a guidance cut, or with heavy cash burn or dilution, the trajectory is Mixed (materially both), NOT Improving. This is not a bearish bias — a release reporting strong growth alongside solid or improving profitability is still Improving; Mixed is only for genuinely two-sided results. Return a confidence in [0,1] and a single-sentence rationale that quotes or paraphrases the release; when a profitability, margin, or cash-burn fact drives a Mixed classification, the rationale must name that fact. This is NOT investment advice: the rationale must contain NO advice language whatsoever — never "buy", "sell", "hold", "guaranteed", "safe bet", price targets, or any recommendation. When the text is ambiguous, boilerplate, or lacks reported results, return Unknown with a low confidence rather than manufacturing a directional read.
        """;

    private const int PreSpec164InstructionLength = 1544;

    private const string PreSpec164InstructionSha256 =
        "71622fcba90f5c3e213eacbf485a225b9b074f52530f153eddd395b458282e43";

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void ProductionInstruction_IsUnchanged_ByTheExtraction()
    {
        Assert.Equal(PreSpec164ProductionInstruction, ChatFilingAnalyzer.SystemInstruction, StringComparer.Ordinal);
        Assert.Equal(PreSpec164InstructionLength, ChatFilingAnalyzer.SystemInstruction.Length);
        Assert.Equal(PreSpec164InstructionSha256, Sha256Hex(ChatFilingAnalyzer.SystemInstruction));

        // The seam's default IS the analyzer's constant (a const alias), so the two cannot diverge.
        Assert.Equal(ChatFilingAnalyzer.SystemInstruction, FilingAnalyzerPrompt.DefaultSystemInstruction, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(50)]      // Truncates.
    [InlineData(4999)]    // Truncates by exactly one character.
    [InlineData(5000)]    // Exactly the input length — no truncation.
    [InlineData(12000)]   // The production default cap.
    public async Task ProductionAnalyzer_AssemblesTheSamePrompt_AsTheSharedSeam(int maxInputLength)
    {
        var input = string.Concat(Enumerable.Range(0, 500).Select(i => $"para{i}: results improved. "))[..5000];

        var client = new FakeChatClient("""{"direction":"Improving","confidence":0.5,"rationale":"x"}""");
        var analyzer = new ChatFilingAnalyzer(
            client,
            new FilingAnalyzerOptions { MaxInputLength = maxInputLength },
            NullLogger<ChatFilingAnalyzer>.Instance);

        _ = await analyzer.AnalyzeAsync(input, CancellationToken.None);

        Assert.Equal(1, client.CallCount);
        var captured = client.CapturedMessages;

        // Message shape: exactly two messages, System then User, in that order.
        Assert.Equal(2, captured.Count);
        Assert.Equal(ChatRole.System, captured[0].Role);
        Assert.Equal(ChatRole.User, captured[1].Role);
        Assert.Equal(PreSpec164ProductionInstruction, captured[0].Text, StringComparer.Ordinal);

        // The shared seam, called the way production calls it, reproduces the captured prompt exactly.
        var seam = FilingAnalyzerPrompt.Build(input, maxInputLength);
        Assert.Equal(2, seam.Length);
        Assert.Equal(captured[0].Role, seam[0].Role);
        Assert.Equal(captured[1].Role, seam[1].Role);
        Assert.Equal(captured[0].Text, seam[0].Text, StringComparer.Ordinal);
        Assert.Equal(captured[1].Text, seam[1].Text, StringComparer.Ordinal);
    }

    [Fact]
    public void Override_REPLACES_TheProductionInstruction_NeverAppendsToIt()
    {
        const string shadow = "SHADOW INSTRUCTION: you must return a direction; there is no abstain path.";

        var messages = FilingAnalyzerPrompt.Build("Revenue grew 40%.", 12000, shadow);

        Assert.Equal(2, messages.Length);
        Assert.Equal(shadow, messages[0].Text, StringComparer.Ordinal);

        // Not a single fragment of the production prompt survives — appending would send the model the
        // production "return Unknown when ambiguous" rule alongside "no abstain", measuring the contradiction.
        Assert.DoesNotContain("return Unknown", messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Radar, a research assistant.", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal("Revenue grew 40%.", messages[1].Text, StringComparer.Ordinal);
    }

    [Fact]
    public void NonPositiveCap_Throws_RatherThanComposingAnEmptyPrompt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FilingAnalyzerPrompt.Build("text", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FilingAnalyzerPrompt.Truncate("text", -1));
    }
}
