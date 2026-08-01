using Microsoft.Extensions.AI;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// The ONE definition of the filing-read prompt assembly: truncate the release text to the analyzer's input
/// cap (leading substring, applied FIRST — the headline bullets sit at the top of an EX-99.1), then compose
/// the two-message <c>[System, User]</c> conversation. Extracted from <see cref="ChatFilingAnalyzer"/> (spec
/// 164, reuse over copy) so the production analyzer and the read-only shadow-read research pass
/// (<c>Radar.CalibrationAudit --shadow-read</c>) assemble their prompts through the same code — a second
/// pasted copy would silently drift and the shadow measurement would then measure the drift.
/// <para>
/// <b>Byte-identical by default.</b> <paramref name="systemInstruction"/> defaults to
/// <see cref="DefaultSystemInstruction"/>, which IS <see cref="ChatFilingAnalyzer.SystemInstruction"/> (a
/// <c>const</c> alias, so the two cannot diverge). Production never passes the parameter, so its assembled
/// prompt — instruction bytes, message order and roles, truncation rule — is exactly what it was before the
/// extraction (asserted by <c>FilingAnalyzerPromptSeamTests</c>).
/// </para>
/// <para>
/// When a caller DOES pass an instruction it <b>replaces</b> the production one; it is never appended. The
/// production instruction explicitly permits and instructs <c>Unknown</c> for ambiguous text, so appending a
/// "no abstain" rule would send the model contradictory instructions and measure the contradiction rather
/// than the prompt (spec 164).
/// </para>
/// </summary>
internal static class FilingAnalyzerPrompt
{
    /// <summary>
    /// The production system instruction — the default when no override is supplied. A <c>const</c> alias of
    /// <see cref="ChatFilingAnalyzer.SystemInstruction"/> (which stays the declaration site), so "the default
    /// is the exact current prompt" holds by construction rather than by copy.
    /// </summary>
    internal const string DefaultSystemInstruction = ChatFilingAnalyzer.SystemInstruction;

    /// <summary>
    /// The analyzer's truncation: the LEADING <paramref name="maxInputLength"/>-character substring, in the
    /// original expression shape (<c>text.Length &gt; max ? text[..max] : text</c>). Also the definition the
    /// calibration audit's <c>ModelInputTruncation</c> reproduces the archived model input with.
    /// </summary>
    internal static string Truncate(string text, int maxInputLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxInputLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInputLength),
                maxInputLength,
                "MaxInputLength must be positive: a non-positive cap has no model input to compose.");
        }

        return text.Length > maxInputLength ? text[..maxInputLength] : text;
    }

    /// <summary>
    /// Assembles the filing-read conversation: the system instruction followed by the truncated release text
    /// as the user message. The order and roles are part of the production contract.
    /// </summary>
    internal static ChatMessage[] Build(
        string earningsReleaseText,
        int maxInputLength,
        string systemInstruction = DefaultSystemInstruction)
    {
        ArgumentNullException.ThrowIfNull(earningsReleaseText);
        ArgumentException.ThrowIfNullOrEmpty(systemInstruction);

        var text = Truncate(earningsReleaseText, maxInputLength);

        return
        [
            new ChatMessage(ChatRole.System, systemInstruction),
            new ChatMessage(ChatRole.User, text),
        ];
    }
}
