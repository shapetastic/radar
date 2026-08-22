using System.Text.Json.Serialization;

using Radar.Application.Filings;
using Radar.Infrastructure.Filings;

namespace Radar.CalibrationAudit;

/// <summary>
/// The shadow read's FOUR-VALUE forced-choice vocabulary (spec 164). Deliberately NOT
/// <c>Radar.Domain.Filings.FilingDirection</c>: production supports only
/// <c>Unknown/Improving/Deteriorating/Mixed</c> and <c>ChatFilingAnalyzer.Validate</c> degrades an
/// unrecognised direction to <c>Unknown</c>/confidence 0 — a shadow <c>Neutral</c> routed through it would
/// silently become <c>Unknown/0</c> and every recovery/false-alarm rate downstream would be garbage. There is
/// no <c>Unknown</c> member here <b>by design</b>: the forced-choice prompt has no abstain path, so a
/// response that does not name one of these four is a PARSE FAILURE (recorded as such), never a direction.
/// </summary>
public enum ShadowFilingDirection
{
    /// <summary>The reported results/outlook describe an improving trajectory. Maps to label <c>Positive</c>.</summary>
    Improving,

    /// <summary>The reported results/outlook describe a deteriorating trajectory. Maps to label <c>Negative</c>.</summary>
    Deteriorating,

    /// <summary>Materially two-sided. Maps to label <c>Mixed</c>.</summary>
    Mixed,

    /// <summary>The release genuinely describes no directional change (NOT "could not tell"). Maps to label <c>Neutral</c>.</summary>
    Neutral,
}

/// <summary>
/// The console-local validated shadow read: direction is one of the four forced-choice values, confidence is
/// clamped to [0,1], and the rationale is trimmed, bounded to the SAME
/// <c>ChatFilingAnalyzer.MaxRationaleLength</c> production applies (referenced, not re-stated) and scrubbed
/// through the SHARED <c>AdviceLanguageGuard</c> — Radar must never surface advice language, in a research
/// artifact as much as in a report. <see cref="RawResponse"/> is the model's untouched response text, kept so
/// every recorded read can be re-checked against what actually came back.
/// </summary>
public sealed record ShadowFilingSentiment(
    ShadowFilingDirection Direction,
    decimal Confidence,
    string Rationale,
    string RawResponse);

/// <summary>
/// The structured-output DTO the model fills in. <see cref="Direction"/> is a plain <c>string</c> on purpose:
/// an unrecognised token must surface as a parse failure the console REPORTS, not as a silently-coerced enum
/// value. The console parses and validates this itself — it never routes a shadow response through the
/// production <c>ChatFilingAnalyzer</c> validation path.
/// </summary>
public sealed record ShadowFilingSentimentResponse
{
    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }
}

/// <summary>
/// Parses + validates a <see cref="ShadowFilingSentimentResponse"/> into a <see cref="ShadowFilingSentiment"/>.
/// The ONE place the shadow vocabulary is recognised on the C# side (the analyzer script owns the
/// shadow↔label vocabulary MAPPING; this owns only token recognition).
/// </summary>
public static class ShadowFilingSentimentParser
{
    /// <summary>
    /// True and a validated read when <paramref name="candidate"/> names one of the four forced-choice
    /// directions (ordinal, case-insensitive, trimmed); false with a reason otherwise. A blank/absent/unknown
    /// direction is a parse failure — never <c>Neutral</c>, which is a substantive answer.
    /// </summary>
    public static bool TryParse(
        ShadowFilingSentimentResponse? candidate,
        string rawResponse,
        out ShadowFilingSentiment sentiment,
        out string failureReason)
    {
        sentiment = null!;

        if (candidate is null)
        {
            failureReason = "the model response carried no typed shadow sentiment object";
            return false;
        }

        var token = candidate.Direction?.Trim() ?? string.Empty;
        if (token.Length == 0)
        {
            failureReason = "the model response carried no 'direction' value (the forced-choice prompt has no abstain path)";
            return false;
        }

        // Enum.TryParse also accepts NUMERIC text ("0" would parse as Improving), which is not a direction
        // the model was asked for — require a purely alphabetic token before parsing.
        if (!token.All(char.IsLetter)
            || !Enum.TryParse<ShadowFilingDirection>(token, ignoreCase: true, out var direction)
            || !Enum.IsDefined(direction))
        {
            failureReason =
                $"direction '{token}' is not one of Improving/Deteriorating/Mixed/Neutral "
                    + "(an unrecognised token is a parse failure, never a direction)";
            return false;
        }

        var confidence = Math.Clamp(candidate.Confidence, 0m, 1m);

        var rationale = candidate.Rationale?.Trim() ?? string.Empty;
        if (rationale.Length > ChatFilingAnalyzer.MaxRationaleLength)
        {
            rationale = rationale[..ChatFilingAnalyzer.MaxRationaleLength];
        }

        // The shared production guard (reuse over copy). A model can ignore the instruction; drop the
        // rationale rather than surfacing advice language. The DIRECTION is not advice and is retained.
        if (rationale.Length > 0 && AdviceLanguageGuard.ContainsAdviceLanguage(rationale))
        {
            rationale = string.Empty;
        }

        sentiment = new ShadowFilingSentiment(direction, confidence, rationale, rawResponse);
        failureReason = string.Empty;
        return true;
    }
}
