namespace Radar.CalibrationAudit;

/// <summary>
/// The EXACT model-input truncation <c>ChatFilingAnalyzer</c> applies before its model call (spec 162):
/// the LEADING <c>MaxInputLength</c>-character substring, applied FIRST, using the identical expression
/// shape (<c>text.Length &gt; max ? text[..max] : text</c>). Parity with the production analyzer is not
/// assumed — it is asserted by <c>ModelInputTruncationParityTests</c>, which runs the real
/// <c>ChatFilingAnalyzer</c> against a capturing fake <c>IChatClient</c> and compares the captured user
/// message byte-for-byte against this method's output. If the analyzer's truncation ever changes shape,
/// that test fails and this helper must be updated in the same change.
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

        // The exact ChatFilingAnalyzer expression: truncate FIRST, leading substring of MaxInputLength.
        var truncated = text.Length > maxInputLength;
        return (truncated ? text[..maxInputLength] : text, truncated);
    }
}
