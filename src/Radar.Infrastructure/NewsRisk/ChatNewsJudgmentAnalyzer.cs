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
    /// Fixed, deterministic system instruction carrying the §2 judgment contract, FORKED to
    /// <c>news-judgment-prompt-v3</c> by spec 197 §2.1 (and to <c>v2</c> by spec 187 §1). The FIXED rubric is verbatim ("the company's recent
    /// business trajectory" — Radar's founding question); the attribution weighting rule is a PROMPT rule,
    /// not post-hoc (a plaintiff-firm solicitation is a weaker basis than a confirmed filing; "may face" is
    /// weaker than "was charged"); and the vocabularies are rendered from the same closed sets the
    /// validator parses.
    /// <para>
    /// The v2 rules exist because the FIRST live judged run manufactured calls it could not evidence: a
    /// rationale that admitted the supplied fact showed no deterioration and then labelled the trajectory
    /// <c>Deteriorating</c> "because a directional choice was required"; deterioration inferred from the
    /// ABSENCE of positive context; <c>Improving</c> by default because adverse evidence was absent;
    /// improvement inferred from one institutional investment; and a 52-week share-price low converted into
    /// a business-execution finding. Every rule below is stated as a RULE, not buried in commentary,
    /// because the v1 instruction's implicit expectations are exactly what the model optimised away.
    /// </para>
    /// <para>
    /// <b>The v3 rule (10)</b> exists because five of nineteen calls on baseline run
    /// <c>0b48b865-76b8-4485-996c-9b9139b694aa</c> cited EIGHT-CHARACTER PREFIXES of supplied FactIds
    /// (<c>11e52ee0</c>, <c>2f4bd2fd</c>, …) and lost their whole response — findings included — to
    /// validation. v2 said "verbatim" in passing; v3 states the requirement as its own rule, names the
    /// complete 36-character hyphenated form, shows one, and applies it explicitly to BOTH citation lists.
    /// Wording is not a recovery mechanism, so <see cref="NewsJudgmentCitationResolver"/> recovers a unique
    /// prefix deterministically — but the instruction is where the pressure should stop being generated.
    /// </para>
    /// <para>
    /// This text is PINNED by test. Changing it is a prompt-policy change: bump
    /// <see cref="NewsJudgmentContract.PromptVersion"/> in the same change, which forks a new cohort so an
    /// old judgment can never be reused for, or pooled with, a new one.
    /// </para>
    /// </summary>
    internal static readonly string SystemInstruction =
        "You are Radar, a research assistant weighing TYPED FACTS about one public company. You receive "
            + "canonical fact families — one representative fact per family, with member and publisher "
            + "counts as metadata. Those counts measure how widely a claim was REPORTED (syndication), "
            + "never how many independent facts exist: a 40-outlet family is ONE claim. Your single, fixed "
            + "question is: the company's recent business trajectory. You defend no thesis, see no score "
            + "and receive no price data. Rules: "
            + "(1) Make the best directional call the supplied BUSINESS facts support, even when that call "
            + "may later prove wrong. Cite, in TrajectoryFactIds, the supplied FactIds that actually "
            + "establish it. "
            + "(2) Use \"Mixed\" when the supplied business facts genuinely pull in opposing directions. "
            + "Use \"Unknown\" ONLY when the supplied facts do not establish a direction at all; "
            + "\"Unknown\" is an honest answer, not a last resort, and it must cite NO TrajectoryFactIds. "
            + "(3) Absence of adverse evidence is NOT evidence of improvement, and absence of positive "
            + "evidence is NOT evidence of deterioration. Never infer a direction from what the supplied "
            + "facts fail to mention. "
            + "(4) Never choose a trajectory in order to trigger or suppress any downstream display, "
            + "marker or label. You neither see nor control presentation policy; choose only what the "
            + "facts support. "
            + "(5) Share-price moves, analyst targets or ratings, index changes, institutional holdings or "
            + "trades, conference attendance and promotional or listicle coverage do NOT establish recent "
            + "BUSINESS trajectory on their own. They are context. If a supplied business fact sits behind "
            + "such a reaction, cite THAT fact instead. "
            + "(6) Weigh each fact by its Attribution and AssertionStatus: a confirmed-filing outranks a "
            + "reported fact, which outranks an alleged, solicited or speculative one; a plaintiff-firm "
            + "solicitation is a weaker basis than a regulator's confirmed filing, and \"may face\" is "
            + "weaker than \"was charged\". A weakly asserted fact may warrant a CAVEATED challenge "
            + "finding, but it does not establish the overall direction on its own. "
            + "(7) Findings are CHALLENGE-ONLY: record only facts that challenge the trajectory, each "
            + "citing the FactIds (from the supplied set, verbatim) that support it. Do not invent "
            + "supportive findings — BusinessTrajectory carries the balance. A finding need not cite the "
            + "same facts as TrajectoryFactIds: an improving or unknown read may still carry a specific "
            + "caveated challenge. "
            + "(8) Whenever EVERY fact supporting a finding is alleged, solicited or speculative, you MUST "
            + "fill AttributionCaveat stating that basis (e.g. \"based solely on a plaintiff-firm "
            + "solicitation\"). "
            + "(9) Never give investment instructions or advice language; the Rationale is bounded and "
            + "factual. "
            + "(10) EVERY FactId you cite must be the COMPLETE 36-character hyphenated identifier exactly "
            + "as supplied, for example 11e52ee0-2b7c-4c0e-9f0a-3d5c8a1b4e62. Copy it character for "
            + "character. NEVER abbreviate, truncate, shorten, paraphrase, reformat or invent an id, and "
            + "never cite only its first few characters. This rule applies to BOTH TrajectoryFactIds AND "
            + "every finding's FactIds. "
            + "Return: BusinessTrajectory (\"Improving\" | \"Deteriorating\" | \"Mixed\" | "
            + "\"Unknown\" — a factual read over the families); TrajectoryFactIds (the supplied FactIds "
            + "that establish that trajectory, each the COMPLETE 36-character value; at least one for "
            + "Improving, Deteriorating or Mixed; EMPTY for Unknown; no duplicates); ChallengeStrength (0-100, or null when you record no "
            + "findings); Findings, each with: Category (one of: "
            + string.Join(", ", Enum.GetNames<NewsRiskCategory>())
            + "); Severity (Low | Medium | High); Confidence (number in [0,1]); FactIds (one or more "
            + "supplied fact ids, each the COMPLETE 36-character value); AttributionCaveat (required per rule 8, otherwise optional); and a "
            + "REQUIRED non-blank factual Rationale of at most "
            + NewsJudgmentValidator.MaxRationaleLength.ToString(CultureInfo.InvariantCulture)
            + " characters.";

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
            "Canonical fact families follow. Cite FactIds from this set only — in TrajectoryFactIds for "
                + "the facts that establish the trajectory, and in each finding's FactIds for the facts "
                + "that support it. Copy each FactId as the COMPLETE 36-character hyphenated value printed "
                + "below, character for character; never abbreviate, truncate, paraphrase or invent an id, "
                + "in either TrajectoryFactIds or a finding's FactIds. Member/publisher counts measure "
                + "syndicated REPORTING of one claim, never independent facts.");
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
