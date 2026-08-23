using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;

namespace Radar.Infrastructure.NewsRisk;

/// <summary>
/// The <see cref="IChatClient"/>-backed stage-2 direction judge (spec 185 §2). One instance is ONE judge
/// reader — bound to a single provider/model client at construction. Uses only
/// <c>Microsoft.Extensions.AI</c> abstractions (AD-5: no provider SDK types here); never throws for a
/// provider/parse failure (each becomes a typed <see cref="NewsJudgmentAnalysisFailure"/> the caller
/// persists); caller cancellation propagates.
/// <para>
/// The prompt receives EXACTLY the request contents — the company name/ticker plus the canonical fact
/// FAMILIES with typed content and size metadata. It never sees raw article prose, a headline, a Radar
/// score/rank/label, a price series, a future outcome or a prior judgment; the evaluation target is stated
/// verbatim and never varies per company. Changing this instruction text is a prompt-policy change: bump
/// <see cref="NewsJudgmentContract.PromptVersion"/> in the same change, which forks a new cohort.
/// </para>
/// </summary>
internal sealed class ChatNewsJudgmentAnalyzer : INewsJudgmentAnalyzer
{
    /// <summary>
    /// Fixed, deterministic system instruction carrying the §2 judgment contract. The FIXED rubric is
    /// verbatim ("the company's recent business trajectory" — Radar's founding question); the attribution
    /// weighting rule is a PROMPT rule, not post-hoc (a plaintiff-firm solicitation is a weaker basis than
    /// a confirmed filing; "may face" is weaker than "was charged"); and the vocabularies are rendered from
    /// the same closed sets the validator parses.
    /// </summary>
    internal static readonly string SystemInstruction =
        "You are Radar, a research assistant weighing TYPED FACTS about one public company. You receive "
            + "canonical fact families — one representative fact per family, with member and publisher "
            + "counts as metadata. Those counts measure how widely a claim was REPORTED (syndication), "
            + "never how many independent facts exist: a 40-outlet family is ONE claim. Your single, fixed "
            + "question is: the company's recent business trajectory. You defend no thesis, see no score "
            + "and receive no price data. Rules: "
            + "(1) Weigh each fact by its Attribution and AssertionStatus: a confirmed-filing outranks a "
            + "reported fact, which outranks an alleged, solicited or speculative one; a plaintiff-firm "
            + "solicitation is a weaker basis than a regulator's confirmed filing, and \"may face\" is "
            + "weaker than \"was charged\". "
            + "(2) Findings are CHALLENGE-ONLY: record only facts that challenge the trajectory, each "
            + "citing the FactIds (from the supplied set, verbatim) that support it. Do not invent "
            + "supportive findings — BusinessTrajectory carries the balance. "
            + "(3) Whenever EVERY fact supporting a finding is alleged, solicited or speculative, you MUST "
            + "fill AttributionCaveat stating that basis (e.g. \"based solely on a plaintiff-firm "
            + "solicitation\"). "
            + "(4) Never give investment instructions or advice language; the Rationale is bounded and "
            + "factual. "
            + "Return: BusinessTrajectory (\"Improving\" | \"Deteriorating\" | \"Mixed\" | \"Unknown\" — a "
            + "factual read over the families); ChallengeStrength (0-100, or null when you record no "
            + "findings); Findings, each with: Category (one of: "
            + string.Join(", ", Enum.GetNames<NewsRiskCategory>())
            + "); Severity (Low | Medium | High); Confidence (number in [0,1]); FactIds (one or more "
            + "supplied fact ids); AttributionCaveat (required per rule 3, otherwise optional); and a "
            + "short factual Rationale.";

    private readonly IChatClient _chatClient;
    private readonly NewsJudgmentReaderIdentity _identity;
    private readonly ILogger<ChatNewsJudgmentAnalyzer> _logger;

    public ChatNewsJudgmentAnalyzer(
        IChatClient chatClient, NewsJudgmentReaderIdentity identity, ILogger<ChatNewsJudgmentAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClient = chatClient;
        _identity = identity;
        _logger = logger;
    }

    public async Task<NewsJudgmentAnalysisOutcome> AnalyzeAsync(
        NewsJudgmentAnalysisRequest request, CancellationToken ct)
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
                .GetResponseAsync<NewsJudgmentModelResponse>(messages, cancellationToken: ct)
                .ConfigureAwait(false);

            var rawHash = HashText(response.Text ?? string.Empty);
            if (!response.TryGetResult(out var candidate) || candidate is null)
            {
                _logger.LogWarning(
                    "News-judgment reader {Reader} produced no parseable typed response for company {Company}.",
                    _identity.Name,
                    request.CompanyName);
                return new NewsJudgmentAnalysisOutcome(
                    NewsJudgmentAnalysisFailure.ParseError,
                    Response: null,
                    RawResponseHash: rawHash,
                    FailureDetail: "no typed NewsJudgmentModelResponse could be parsed from the model response");
            }

            return new NewsJudgmentAnalysisOutcome(
                NewsJudgmentAnalysisFailure.None, candidate, rawHash, FailureDetail: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "News-judgment reader {Reader} provider call failed for company {Company}.",
                _identity.Name,
                request.CompanyName);
            return new NewsJudgmentAnalysisOutcome(
                NewsJudgmentAnalysisFailure.ProviderError,
                Response: null,
                RawResponseHash: null,
                FailureDetail: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The supplied family list — exactly the typed-fact fields the judge may weigh (spec 185 §1), each
    /// labelled with its citable FactId. No raw prose, no headline, no URL, no publisher name.
    /// </summary>
    internal static string BuildUserMessage(NewsJudgmentAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            request.Ticker is { Length: > 0 } ticker
                ? $"Company: {request.CompanyName} ({ticker})"
                : $"Company: {request.CompanyName}");
        sb.AppendLine(
            "Canonical fact families follow. Cite FactIds from this set only. Member/publisher counts "
                + "measure syndicated REPORTING of one claim, never independent facts.");
        sb.AppendLine();
        foreach (var family in request.Families)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"FactId: {family.RepresentativeFactId:D}"));
            sb.AppendLine("EventTypes: " + string.Join(", ", family.EventTypes));
            sb.AppendLine("Statement: " + family.Statement);
            if (family.TemporalScope is { Length: > 0 } scope)
            {
                sb.AppendLine("TemporalScope: " + scope);
            }

            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Attribution: {family.Attribution} · AssertionStatus: {family.AssertionStatus} · "
                    + $"ExtractionConfidence: {family.Confidence:0.00}"));
            sb.AppendLine("Citations: " + string.Join(" | ", family.Citations.Select(c => $"\"{c}\"")));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Reported by {family.MemberCount} syndicated cop(ies) across "
                    + $"{family.DistinctPublisherCount} publisher(s) — one claim."));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
