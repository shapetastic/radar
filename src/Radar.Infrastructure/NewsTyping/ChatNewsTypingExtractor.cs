using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Radar.Application.NewsTyping;

namespace Radar.Infrastructure.NewsTyping;

/// <summary>
/// The <see cref="IChatClient"/>-backed news-typing extractor (spec 181 §2/§4). One instance is ONE reader —
/// bound to a single provider/model client at construction. Uses only <c>Microsoft.Extensions.AI</c>
/// abstractions (AD-5: no provider SDK types here); never throws for a provider/parse failure (each becomes
/// a typed <see cref="NewsTypingExtractionFailure"/> the caller persists); caller cancellation propagates.
/// <para>
/// The prompt receives EXACTLY the request contents — the company ticker plus ONE observation's supplied
/// text fields, tagged so citations can be validated against them. It never sees a Radar score, rank or
/// label, a price, a future outcome, another observation, or any uncited company background — and it asks
/// NO directional question (spec 181 §2: the extractor withholds the VERDICT, not the factual direction).
/// Changing this instruction text is a prompt-policy change: bump
/// <see cref="NewsTypingContract.PromptVersion"/> in the same change, which forks a new cohort.
/// </para>
/// </summary>
internal sealed class ChatNewsTypingExtractor : INewsTypingExtractor
{
    /// <summary>
    /// Fixed, deterministic system instruction carrying the §2 extraction contract. The preservation clause
    /// is VERBATIM from the spec; the vocabularies are rendered from the same closed sets the validator
    /// parses, so an out-of-vocabulary answer is a measurable drop, never a silent coercion.
    /// </summary>
    internal static readonly string SystemInstruction =
        "You are Radar, a research assistant reading ONE piece of third-party news coverage of one public "
            + "company. Extract the factual events the supplied text asserts. Rules: "
            + "(1) EXTRACT LIBERALLY: record every fact a diligent analyst might find pertinent — a later, "
            + "separate stage filters; you never pre-judge materiality. "
            + "(2) Preserve actors, quantities, periods, comparisons, negation, modality and attribution "
            + "exactly; do not assign investment direction, severity or materiality. \"Loss widened from X "
            + "to Y\", \"shares fell 11.8%\" and \"a plaintiff law firm announced an investigation\" are "
            + "facts — keep their words; never add Positive/Negative or any judgment of your own. "
            + "(3) You see ONE observation at a time: do not invent identifiers, do not reference other "
            + "articles, and give each fact only the fields asked for. "
            + "(4) Every fact must carry citations COPIED EXACTLY, character for character, from the "
            + "supplied HEADLINE, DESCRIPTION or BODY text — do not paraphrase inside a citation. "
            + "(5) Attribute WHO asserts each fact and its epistemic status: an SEC action is not a "
            + "plaintiff-firm solicitation, a confirmed filing is not a publisher report, \"may face\" is "
            + "not \"was charged\". "
            + "(6) If the supplied text is too thin to type, return Relevance \"InsufficientContent\" — "
            + "still extract any facts it does support. "
            + "Return: Relevance (\"CompanySpecific\" | \"SectorOrMacroContext\" | \"NotAboutThisCompany\" "
            + "| \"InsufficientContent\"); Facts, each with: EventTypes (one or more from exactly: "
            + string.Join(", ", NewsEventTaxonomy.Members)
            + "); Statement (the fact, preserving the source's actors/quantities/negation/modality); "
            + "TemporalScope (the period/date the fact concerns, when stated); Attribution (one of: "
            + "company, regulator, plaintiff-firm, publisher, analyst, exchange, short-seller, "
            + "other-specified); AssertionStatus (one of: confirmed-filing, reported, alleged, solicited, "
            + "speculative, announced); Confidence (number in [0,1]); Citations (exact substrings of the "
            + "supplied text).";

    private readonly IChatClient _chatClient;
    private readonly NewsTypingReaderIdentity _identity;
    private readonly ILogger<ChatNewsTypingExtractor> _logger;

    public ChatNewsTypingExtractor(
        IChatClient chatClient, NewsTypingReaderIdentity identity, ILogger<ChatNewsTypingExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClient = chatClient;
        _identity = identity;
        _logger = logger;
    }

    public async Task<NewsTypingExtractionOutcome> ExtractAsync(
        NewsTypingExtractionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemInstruction),
            new(ChatRole.User, BuildUserMessage(request)),
        ];

        try
        {
            var response = await _chatClient
                .GetResponseAsync<NewsTypingModelResponse>(messages, cancellationToken: ct)
                .ConfigureAwait(false);

            var rawHash = HashText(response.Text ?? string.Empty);
            if (!response.TryGetResult(out var candidate) || candidate is null)
            {
                _logger.LogWarning(
                    "News-typing reader {Reader} produced no parseable typed response for observation {ObservationId}.",
                    _identity.Name,
                    request.Observation.ObservationId);
                return new NewsTypingExtractionOutcome(
                    NewsTypingExtractionFailure.ParseError,
                    Response: null,
                    RawResponseHash: rawHash,
                    FailureDetail: "no typed NewsTypingModelResponse could be parsed from the model response");
            }

            return new NewsTypingExtractionOutcome(
                NewsTypingExtractionFailure.None, candidate, rawHash, FailureDetail: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "News-typing reader {Reader} provider call failed for observation {ObservationId}.",
                _identity.Name,
                request.Observation.ObservationId);
            return new NewsTypingExtractionOutcome(
                NewsTypingExtractionFailure.ProviderError,
                Response: null,
                RawResponseHash: null,
                FailureDetail: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The single observation's supplied text — headline always, description/body ONLY when supplied (the
    /// validator's "supplied text" definition is exactly the fields rendered here). URLs and publishers are
    /// deliberately absent: they are provenance, not citable text.
    /// </summary>
    internal static string BuildUserMessage(NewsTypingExtractionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            request.Ticker is { Length: > 0 } ticker
                ? "Company ticker: " + ticker
                : "Company: (no ticker attribution)");
        sb.AppendLine(
            "One observation follows. Cite EXACT substrings of the supplied text only.");
        sb.AppendLine();
        sb.AppendLine("HEADLINE: " + request.Observation.Headline);
        if (request.Observation.DescriptionText is not null)
        {
            sb.AppendLine("DESCRIPTION: " + request.Observation.DescriptionText);
        }

        if (request.Observation.BodyText is not null)
        {
            sb.AppendLine("BODY: " + request.Observation.BodyText);
        }

        return sb.ToString();
    }

    private static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
