using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Radar.Application.NewsRisk;

namespace Radar.Infrastructure.NewsRisk;

/// <summary>
/// The <see cref="IChatClient"/>-backed news-risk analyzer (spec 179 §5). One instance is ONE reader — it is
/// bound to a single provider/model client at construction. Uses only <c>Microsoft.Extensions.AI</c>
/// abstractions (AD-5: no provider SDK types here); never throws for a provider/parse failure (each becomes a
/// typed <see cref="NewsRiskAnalysisFailure"/> the caller persists); caller cancellation propagates.
/// <para>
/// The prompt receives EXACTLY the request contents — company name/ticker plus ordered id-labelled article
/// text. It never sees a Radar score, rank or label, a price, a future outcome, or any uncited company
/// background. Changing this instruction text is a prompt-policy change: bump
/// <see cref="NewsRiskAnalysisContract.PromptVersion"/> in the same change, which forks a new cohort.
/// </para>
/// </summary>
internal sealed class ChatNewsRiskAnalyzer : INewsRiskAnalyzer
{
    /// <summary>
    /// Fixed, deterministic system instruction carrying every §5 prompt requirement. The "never recommend"
    /// clause is an instruction ABOUT forbidden output, not advice itself; the §6 validator additionally
    /// scrubs the surfaced rationale through the shared advice-language guard.
    /// </summary>
    internal const string SystemInstruction =
        "You are Radar, a research assistant reading third-party news coverage of one public company. "
            + "Assess ONLY risks supported by the supplied text: financing, dilution, solvency/going-concern, "
            + "cash runway, debt/covenant, delisting/reverse-split, execution/missed-milestone, guidance "
            + "credibility, unit-economics/margin, regulatory/legal, customer-concentration or "
            + "governance/related-party risks. Rules: "
            + "(1) Distinguish a financing FACILITY (capacity that may never be drawn) from an actual "
            + "issuance/dilution event, and a HISTORICAL loss from a current going-concern statement. "
            + "(2) Management/company statements are claims, not verified facts — attribute them as such. "
            + "(3) Ordinary negative words are not automatically thesis-breaking; assess substance. "
            + "(4) Keep conflicting evidence visible: if the supplied text both supports and undercuts a "
            + "risk, cite both sides. "
            + "(5) Every claim must cite the observation ids of the supplied articles it rests on, and every "
            + "excerpt must be COPIED EXACTLY, character for character, from the supplied headline, "
            + "description or body text — do not paraphrase inside an excerpt. "
            + "(6) If the supplied text is too thin or ambiguous to assess, return Assessment "
            + "\"InsufficientContent\" with no risk score — never a low score. "
            + "(7) This is NOT investment advice: never recommend buying, selling, shorting or holding, and "
            + "use no advice language anywhere. "
            + "Return: Assessment (\"ThesisChallenged\" | \"NoRiskFoundInSuppliedText\" | "
            + "\"InsufficientContent\"); RiskScore (integer 0..100, only when the text was sufficient AND the "
            + "assessment is ThesisChallenged, else null); Categories (from exactly: LiquidityOrGoingConcern, "
            + "DilutionOrFinancingDependence, DebtOrCovenant, DelistingOrReverseSplit, "
            + "ExecutionOrMissedMilestone, GuidanceCredibility, UnitEconomicsOrMargin, "
            + "RegulatoryOrLegalSetback, CustomerOrRevenueConcentration, GovernanceOrRelatedParty, "
            + "OtherSpecifiedRisk); Claims (each with category, severity Low|Medium|High, confidence in "
            + "[0,1], the cited observationIds, and exact excerpts); and a short factual Rationale.";

    private readonly IChatClient _chatClient;
    private readonly NewsRiskReaderIdentity _identity;
    private readonly ILogger<ChatNewsRiskAnalyzer> _logger;

    public ChatNewsRiskAnalyzer(
        IChatClient chatClient, NewsRiskReaderIdentity identity, ILogger<ChatNewsRiskAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClient = chatClient;
        _identity = identity;
        _logger = logger;
    }

    public async Task<NewsRiskAnalysisOutcome> AnalyzeAsync(
        NewsRiskAnalysisRequest request, CancellationToken ct)
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
                .GetResponseAsync<NewsRiskModelResponse>(messages, cancellationToken: ct)
                .ConfigureAwait(false);

            var rawHash = HashText(response.Text ?? string.Empty);
            if (!response.TryGetResult(out var candidate) || candidate is null)
            {
                _logger.LogWarning(
                    "News-risk reader {Reader} produced no parseable typed response for {Company}.",
                    _identity.Name,
                    request.CompanyName);
                return new NewsRiskAnalysisOutcome(
                    NewsRiskAnalysisFailure.ParseError,
                    Response: null,
                    RawResponseHash: rawHash,
                    FailureDetail: "no typed NewsRiskModelResponse could be parsed from the model response");
            }

            return new NewsRiskAnalysisOutcome(
                NewsRiskAnalysisFailure.None, candidate, rawHash, FailureDetail: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "News-risk reader {Reader} provider call failed for {Company}.",
                _identity.Name,
                request.CompanyName);
            return new NewsRiskAnalysisOutcome(
                NewsRiskAnalysisFailure.ProviderError,
                Response: null,
                RawResponseHash: null,
                FailureDetail: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ordered, id-labelled input text — headline always, description/body ONLY when supplied (the §6
    /// "archived text" definition is exactly the fields rendered here). URLs and publishers are deliberately
    /// absent: they are provenance, not citable text.
    /// </summary>
    internal static string BuildUserMessage(NewsRiskAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("Company: ").Append(request.CompanyName);
        if (!string.IsNullOrWhiteSpace(request.Ticker))
        {
            sb.Append(" (ticker ").Append(request.Ticker).Append(')');
        }

        sb.AppendLine();
        sb.AppendLine(
            "Articles, newest first. Cite ONLY these observation ids; quote excerpts EXACTLY from the "
                + "supplied text.");
        foreach (var article in request.Articles)
        {
            sb.AppendLine();
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture, $"[observation {article.ObservationId:D}]"));
            sb.AppendLine("HEADLINE: " + article.Headline);
            if (article.DescriptionText is not null)
            {
                sb.AppendLine("DESCRIPTION: " + article.DescriptionText);
            }

            if (article.BodyText is not null)
            {
                sb.AppendLine("BODY: " + article.BodyText);
            }
        }

        return sb.ToString();
    }

    private static string HashText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
