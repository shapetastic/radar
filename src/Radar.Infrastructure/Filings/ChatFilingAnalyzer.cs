using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Domain.Filings;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// The config-selected <see cref="IChatClient"/>-backed <see cref="IFilingAnalyzer"/>. Given an earnings-release
/// plain text (spec 73's output), it truncates the input to <see cref="FilingAnalyzerOptions.MaxInputLength"/>
/// characters, asks the model — via <c>Microsoft.Extensions.AI</c>'s typed <c>GetResponseAsync&lt;T&gt;</c>
/// structured-output extension — for a directional read AS REPORTED (improving vs deteriorating trajectory,
/// NOT a beat-vs-consensus claim), then <b>validates</b> the result into a known-good <see cref="FilingSentiment"/>:
/// direction coerced to a defined enum value, confidence clamped to [0,1], rationale bounded. A malformed/empty/
/// failed AI response degrades to <see cref="FilingSentiment.Unknown"/> and never throws; only genuine caller
/// cancellation propagates. Uses only <c>Microsoft.Extensions.AI</c> abstractions — no provider SDK (AD-5).
/// </summary>
internal sealed class ChatFilingAnalyzer : IFilingAnalyzer
{
    /// <summary>
    /// Upper bound on the surfaced rationale length (transparency-only text is never unbounded). Internal so
    /// the spec-115 filing-read debug store enforces the SAME bound rather than pasting a second 500.
    /// </summary>
    internal const int MaxRationaleLength = 500;

    /// <summary>
    /// Fixed, deterministic system instruction. States the task, forbids advice language, weighs REPORTED
    /// profitability/margin/cash-burn against REPORTED top-line growth (spec 116 — a record top line with a
    /// deeply negative or deteriorating gross margin, a guidance cut, or heavy cash burn is Mixed, not
    /// Improving), and instructs the model to return Unknown/low-confidence when the text is ambiguous,
    /// boilerplate, or lacks results. Internal so tests can guard the behavioural contract as a string.
    /// </summary>
    internal const string SystemInstruction =
        "You are Radar, a research assistant. You are given the plain text of a company's earnings-release "
            + "press release. Classify the business trajectory the release DESCRIBES AS REPORTED — this is NOT a "
            + "beat-vs-consensus judgement (there is no analyst-consensus feed) — into exactly one of: "
            + "Improving (record bookings, organic growth, raised outlook), Deteriorating (revenue decline, "
            + "guidance cut, impairment), Mixed (materially both), or Unknown. "
            + "Weigh REPORTED profitability, gross margin, and cash burn against REPORTED top-line growth — a "
            + "strong top line alone does not make the trajectory Improving. In particular: record or growing "
            + "revenue paired with a deeply negative or deteriorating gross margin, or a guidance cut, or heavy "
            + "cash burn or dilution, is Mixed (materially both), NOT Improving. This is not a bearish bias — a "
            + "release reporting strong growth alongside solid or improving profitability is still Improving; "
            + "Mixed is only for genuinely two-sided results. "
            + "Return a confidence in [0,1] and a single-sentence rationale that quotes or paraphrases the "
            + "release; when a profitability, margin, or cash-burn fact drives a Mixed classification, the "
            + "rationale must name that fact. "
            + "This is NOT investment advice: the rationale must contain NO advice language whatsoever — never "
            + "\"buy\", \"sell\", \"hold\", \"guaranteed\", \"safe bet\", price targets, or any recommendation. "
            + "When the text is ambiguous, boilerplate, or lacks reported results, return Unknown with a low "
            + "confidence rather than manufacturing a directional read.";

    private readonly IChatClient _chatClient;
    private readonly FilingAnalyzerOptions _options;
    private readonly ILogger<ChatFilingAnalyzer> _logger;

    public ChatFilingAnalyzer(
        IChatClient chatClient,
        FilingAnalyzerOptions options,
        ILogger<ChatFilingAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClient = chatClient;
        _options = options;
        _logger = logger;
    }

    public async Task<FilingSentiment> AnalyzeAsync(string? earningsReleaseText, CancellationToken ct)
    {
        // Never call the model on empty text — an empty release carries no directional read.
        if (string.IsNullOrWhiteSpace(earningsReleaseText))
        {
            return FilingSentiment.Unknown;
        }

        // Truncate FIRST (cost/latency control): the headline beat/miss bullets are at the top of an EX-99.1
        // release, so a leading substring is the right cap. A non-positive cap (misconfiguration) would make
        // the substring throw, breaking the never-throw contract — degrade to Unknown instead.
        var max = _options.MaxInputLength;
        if (max <= 0)
        {
            _logger.LogWarning(
                "Filing analyzer MaxInputLength is non-positive ({MaxInputLength}); returning Unknown.", max);
            return FilingSentiment.Unknown;
        }

        var text = earningsReleaseText.Length > max ? earningsReleaseText[..max] : earningsReleaseText;

        var messages = new[]
        {
            new ChatMessage(ChatRole.System, SystemInstruction),
            new ChatMessage(ChatRole.User, text),
        };

        try
        {
            var response = await _chatClient
                .GetResponseAsync<FilingSentiment>(messages, cancellationToken: ct)
                .ConfigureAwait(false);

            if (!response.TryGetResult(out var candidate) || candidate is null)
            {
                _logger.LogWarning(
                    "Filing analyzer could not parse a typed FilingSentiment from the model response; "
                        + "returning Unknown.");
                return FilingSentiment.Unknown;
            }

            return Validate(candidate);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; do not degrade it to Unknown.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Filing analyzer model call failed; returning Unknown.");
            return FilingSentiment.Unknown;
        }
    }

    /// <summary>
    /// Coerces a candidate into a known-good <see cref="FilingSentiment"/>: direction validated to a defined
    /// enum value (else Unknown/0), confidence clamped to [0,1], rationale trimmed, bounded, and scrubbed of
    /// any advice language the model may have surfaced despite the system prompt.
    /// </summary>
    private FilingSentiment Validate(FilingSentiment candidate)
    {
        var direction = Enum.IsDefined(candidate.Direction) ? candidate.Direction : FilingDirection.Unknown;

        var confidence = direction == FilingDirection.Unknown
            ? 0m
            : Math.Clamp(candidate.Confidence, 0m, 1m);

        var rationale = candidate.Rationale?.Trim() ?? string.Empty;
        if (rationale.Length > MaxRationaleLength)
        {
            rationale = rationale[..MaxRationaleLength];
        }

        // Radar must never surface advice language (the shared AdviceLanguageGuard — the system prompt already
        // forbids it, but a model can ignore instructions, so the rationale is scrubbed defensively). If the
        // model emitted it anyway, drop the rationale rather than passing it through — the directional read
        // itself is not advice and is retained.
        if (rationale.Length > 0 && AdviceLanguageGuard.ContainsAdviceLanguage(rationale))
        {
            _logger.LogWarning(
                "Filing analyzer rationale contained advice language; dropping the rationale.");
            rationale = string.Empty;
        }

        return new FilingSentiment(direction, confidence, rationale);
    }
}
