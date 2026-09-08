using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Application.NewsTyping;
using Radar.Domain.Filings;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// The WIRE shape of the filing read's structured response (spec 215 §1). <c>Direction</c> is a string
/// (the spec-179 all-strings rule: an out-of-vocabulary token arrives as data the validator coerces to
/// Unknown rather than a deserialization exception), <c>Confidence</c> stays a JSON number (the shape every
/// accrued live response has carried since spec 74 — the schema the typed extension emits derives from
/// this type, so changing it would change what the model is asked for), and <c>ReportedMetrics</c> is the
/// all-strings metric list <see cref="ReportedMetricVerifier"/> verifies. Nothing here is persisted as-is.
/// </summary>
internal sealed record FilingReadModelResponse(
    string? Direction,
    decimal? Confidence,
    string? Rationale,
    IReadOnlyList<ReportedMetricWire?>? ReportedMetrics = null);

/// <summary>
/// The config-selected <see cref="IChatClient"/>-backed <see cref="IFilingAnalyzer"/>. Given an earnings-release
/// plain text (spec 73's output), it truncates the input to <see cref="FilingAnalyzerOptions.MaxInputLength"/>
/// characters, asks the model — via <c>Microsoft.Extensions.AI</c>'s typed <c>GetResponseAsync&lt;T&gt;</c>
/// structured-output extension — for a directional read AS REPORTED (improving vs deteriorating trajectory,
/// NOT a beat-vs-consensus claim), then <b>validates</b> the result into a known-good <see cref="FilingSentiment"/>:
/// direction parsed from the closed vocabulary, confidence clamped to [0,1], rationale bounded. Since spec 215
/// the same call also returns the metrics the release STATES, which are verified verbatim by
/// <see cref="ReportedMetricVerifier"/> against the truncated body before anything is kept. A malformed/empty/
/// failed AI response degrades to <see cref="FilingRead.Unknown"/> and never throws; only genuine caller
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
    /// The DIRECTIONAL half of the system instruction — byte-identical to the pre-215 production
    /// instruction. States the task, forbids advice language, weighs REPORTED profitability/margin/cash-burn
    /// against REPORTED top-line growth (spec 116 — a record top line that coexists with a deeply negative or
    /// deteriorating gross margin, a guidance cut, or heavy cash burn is Mixed, not Improving), and instructs
    /// the model to return Unknown/low-confidence when the text is ambiguous, boilerplate, or lacks results.
    /// This is the instruction a read with <see cref="FilingAnalyzerOptions.ExtractReportedMetrics"/> = false
    /// is sent.
    /// </summary>
    internal const string SentimentInstruction =
        "You are Radar, a research assistant. You are given the plain text of a company's earnings-release "
            + "press release. Classify the business trajectory the release DESCRIBES AS REPORTED — this is NOT a "
            + "beat-vs-consensus judgement (there is no analyst-consensus feed) — into exactly one of: "
            + "Improving (record bookings, organic growth, raised outlook), Deteriorating (revenue decline, "
            + "guidance cut, impairment), Mixed (materially both), or Unknown. "
            + "Weigh REPORTED profitability, gross margin, and cash burn against REPORTED top-line growth — a "
            + "strong top line alone does not make the trajectory Improving. In particular: when record or "
            + "growing revenue coexists with a deeply negative or deteriorating gross margin, with a guidance "
            + "cut, or with heavy cash burn or dilution, the trajectory is Mixed (materially both), NOT "
            + "Improving. This is not a bearish bias — a "
            + "release reporting strong growth alongside solid or improving profitability is still Improving; "
            + "Mixed is only for genuinely two-sided results. "
            + "Return a confidence in [0,1] and a single-sentence rationale that quotes or paraphrases the "
            + "release; when a profitability, margin, or cash-burn fact drives a Mixed classification, the "
            + "rationale must name that fact. "
            + "This is NOT investment advice: the rationale must contain NO advice language whatsoever — never "
            + "\"buy\", \"sell\", \"hold\", \"guaranteed\", \"safe bet\", price targets, or any recommendation. "
            + "When the text is ambiguous, boilerplate, or lacks reported results, return Unknown with a low "
            + "confidence rather than manufacturing a directional read.";

    /// <summary>
    /// SPEC 215 §1 — the reported-metrics paragraph appended to <see cref="SentimentInstruction"/> when
    /// <see cref="FilingAnalyzerOptions.ExtractReportedMetrics"/> is true. It asks for the figures the
    /// release STATES, with the period they are stated for, and the prior-period figure ONLY if the release
    /// itself states it — never computed, never inferred — against the closed metric list (the enum names
    /// are spelled here as a compile-time constant and a test asserts they equal
    /// <see cref="ReportedMetric"/>'s members exactly). The model's word is not trusted: every entry is
    /// verified verbatim by <see cref="ReportedMetricVerifier"/> before it is kept.
    /// </summary>
    internal const string ReportedMetricsInstruction =
        "Also return reportedMetrics: the figures the release STATES, with the period they are stated for, "
            + "and the prior-period figure ONLY if the release itself states it; never compute, never infer. "
            + "Each entry has: metric (exactly one of: Revenue, NetIncome, DilutedEps, GrossMargin, "
            + "OperatingIncome, Backlog, CashAndInvestments, TotalDebt, FreeCashFlow — omit any figure that "
            + "is not one of these); value (the figure exactly as printed, e.g. \"384.0\" or \"2.518\" or "
            + "\"24.5\"); unit (the unit token exactly as printed beside it, e.g. \"million\", \"billion\", "
            + "\"%\", or an empty string when none is printed); period (the period the figure is stated FOR, "
            + "as the release words it — a quarter such as \"second quarter of fiscal 2027\" or a date such as "
            + "\"as of July 31, 2026\"); priorValue and priorPeriod (only when the release states the "
            + "prior-period figure for the same metric in the same passage, exactly as printed; otherwise "
            + "omit both); and quote (the exact sentence or fragment of the release, copied character for "
            + "character, that contains the value). Return an empty reportedMetrics list when the release "
            + "states none of these metrics.";

    /// <summary>
    /// Fixed, deterministic system instruction: <see cref="SentimentInstruction"/> followed by
    /// <see cref="ReportedMetricsInstruction"/> (spec 215 §1). Internal so tests can guard the behavioural
    /// contract as a string; a <c>const</c> so <see cref="FilingAnalyzerPrompt.DefaultSystemInstruction"/>
    /// can alias it by construction.
    /// </summary>
    internal const string SystemInstruction = SentimentInstruction + " " + ReportedMetricsInstruction;

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

    public async Task<FilingRead> AnalyzeAsync(string? earningsReleaseText, CancellationToken ct)
    {
        // Never call the model on empty text — an empty release carries no directional read.
        if (string.IsNullOrWhiteSpace(earningsReleaseText))
        {
            return FilingRead.Unknown;
        }

        // Truncate FIRST (cost/latency control): the headline beat/miss bullets are at the top of an EX-99.1
        // release, so a leading substring is the right cap. A non-positive cap (misconfiguration) would make
        // the substring throw, breaking the never-throw contract — degrade to Unknown instead.
        var max = _options.MaxInputLength;
        if (max <= 0)
        {
            _logger.LogWarning(
                "Filing analyzer MaxInputLength is non-positive ({MaxInputLength}); returning Unknown.", max);
            return FilingRead.Unknown;
        }

        // The SHARED prompt assembly (spec 164). With extraction ON (the default) the assembled instruction
        // IS FilingAnalyzerPrompt.DefaultSystemInstruction; with it OFF the pre-215 directional instruction
        // is sent unchanged — the reported-metrics paragraph is omitted and nothing is examined or returned
        // (the structured-output schema derived from FilingReadModelResponse still advertises the list; a
        // model that fills it anyway is ignored, by construction of the branch below).
        var extract = _options.ExtractReportedMetrics;
        var messages = extract
            ? FilingAnalyzerPrompt.Build(earningsReleaseText, max)
            : FilingAnalyzerPrompt.Build(earningsReleaseText, max, SentimentInstruction);

        try
        {
            var response = await _chatClient
                .GetResponseAsync<FilingReadModelResponse>(messages, cancellationToken: ct)
                .ConfigureAwait(false);

            if (!response.TryGetResult(out var candidate) || candidate is null)
            {
                _logger.LogWarning(
                    "Filing analyzer could not parse a typed filing read from the model response; "
                        + "returning Unknown.");
                return FilingRead.Unknown;
            }

            // A direction token outside the closed vocabulary means the model did not follow the contract
            // at all — the response is not trusted (the pre-215 behaviour, where enum deserialization
            // rejected the whole payload): Unknown, no rationale, no metrics examined. A well-formed
            // "Unknown" token is a legitimate read and keeps its rationale.
            if (!NewsTypingTokens.TryParse<FilingDirection>(candidate.Direction, out var direction))
            {
                _logger.LogWarning(
                    "Filing analyzer returned an undefined direction token; returning Unknown.");
                return FilingRead.Unknown;
            }

            var sentiment = Validate(candidate, direction);
            if (!extract)
            {
                return FilingRead.WithoutMetrics(sentiment);
            }

            // Spec 215 §1: the model's list is verified against EXACTLY the text it was shown — the same
            // Truncate the prompt assembly applied — so a quote past the cap cannot verify and is counted.
            var metrics = ReportedMetricVerifier.Verify(
                candidate.ReportedMetrics, FilingAnalyzerPrompt.Truncate(earningsReleaseText, max));
            return new FilingRead(sentiment, metrics);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; do not degrade it to Unknown.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Filing analyzer model call failed; returning Unknown.");
            return FilingRead.Unknown;
        }
    }

    /// <summary>
    /// Coerces a candidate into a known-good <see cref="FilingSentiment"/>: the already-parsed direction
    /// (the digit-rejecting shared token parser, applied by the caller), confidence clamped to [0,1],
    /// rationale trimmed, bounded, and scrubbed of any advice language the model may have surfaced despite
    /// the system prompt.
    /// </summary>
    private FilingSentiment Validate(FilingReadModelResponse candidate, FilingDirection direction)
    {
        var confidence = direction == FilingDirection.Unknown
            ? 0m
            : Math.Clamp(candidate.Confidence ?? 0m, 0m, 1m);

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
