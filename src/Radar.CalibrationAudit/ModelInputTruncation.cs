using Radar.Infrastructure.Filings;

namespace Radar.CalibrationAudit;

/// <summary>
/// The EXACT model-input truncation <c>ChatFilingAnalyzer</c> applies before its model call (spec 162):
/// the LEADING <c>MaxInputLength</c>-character substring, applied FIRST. Since spec 164 this DELEGATES to
/// the shared <see cref="FilingAnalyzerPrompt.Truncate"/> — the one definition the production analyzer
/// itself now truncates through — instead of restating the expression, so the two cannot drift. Parity with
/// the production analyzer is still not assumed: <c>ModelInputTruncationParityTests</c> runs the real
/// <c>ChatFilingAnalyzer</c> against a capturing fake <c>IChatClient</c> and compares the captured user
/// message byte-for-byte against this method's output, so a change to the analyzer's truncation shape still
/// fails loudly.
/// </summary>
public static class ModelInputTruncation
{
    /// <summary>
    /// Applies the production truncation. <paramref name="maxInputLength"/> must be positive — the
    /// production analyzer degrades a non-positive cap to "no model call at all", so there is no valid
    /// model input to reproduce in that case (this console fails loudly instead of inventing one).
    /// </summary>
    public static (string Text, bool Truncated) Apply(string text, int maxInputLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxInputLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInputLength),
                maxInputLength,
                "MaxInputLength must be positive: the production analyzer never calls the model with a "
                    + "non-positive cap, so no model-input text exists to reproduce.");
        }

        // The exact ChatFilingAnalyzer truncation, via the shared definition it now routes through.
        var truncated = text.Length > maxInputLength;
        return (FilingAnalyzerPrompt.Truncate(text, maxInputLength), truncated);
    }
}
