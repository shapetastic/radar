using System.Globalization;
using System.Text;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Radar.Application.Identity;
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
    /// <c>news-judgment-prompt-v5</c> by spec 215 §2 (to <c>v4</c> by spec 214 §2, to <c>v3</c> by spec 197 §2.1, to <c>v2</c> by spec 187 §1). The FIXED rubric is verbatim ("the company's recent
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
    /// <b>The v4 rule (11)</b> (spec 214 §2) exists because the 2026-09-07 judgment read Argan as
    /// <c>Improving</c> on "backlog hits $2.5B" — a LEVEL, not a trend (the backlog had fallen 14% over the
    /// year, and nothing Radar supplied could have said so). Rule 3 covers ABSENCE of facts; rule 11 covers
    /// the presence of a number that carries no direction. Each family now also renders its deterministic
    /// <c>ComparisonBasis</c> line (<see cref="StatementComparisonClassifier"/>), so the judge is told which
    /// supplied facts CAN be cited as trajectory support rather than left to infer it.
    /// </para>
    /// <para>
    /// <b>The v5 rule (12)</b> (spec 215 §2) exists because rule 11 can only stop the judge treating a level
    /// as a trend — it cannot make the judge RIGHT about the trend, because until spec 215 Radar held no
    /// prior value to compare against. The user message now renders, after the families, the
    /// company-reported reference values projected from the reported-metrics ledger for the metrics the
    /// supplied statements name (<see cref="ReferenceValueProjector"/>), each with its own citable
    /// ReferenceId; rule 12 tells the judge to read the direction from the comparison and cite BOTH the
    /// fact and the reference, and never to cite a reference as a trajectory fact on its own — a reference
    /// value is a comparison basis, not news.
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
            + "(11) A quantity stated as a LEVEL — a balance such as backlog, cash, debt, headcount, "
            + "capacity — establishes NO direction by itself, however large. Only a supplied fact that "
            + "states the comparison (prior value, change, record, beat/miss) or an EVENT fact (an order, "
            + "award, contract, launch, financing) can be cited in TrajectoryFactIds. Each family carries "
            + "a ComparisonBasis line: StatedComparison and Event facts may be cited as trajectory "
            + "support; a LevelOnly or NotQuantified fact may be cited in a finding's FactIds as context, "
            + "never as trajectory support. Answer Unknown ONLY when no supplied fact is StatedComparison "
            + "or Event — a numberless comparison (\"backlog declined\") or a numberless event (\"the FDA "
            + "approved the product\") beside a quantified level still establishes direction; when that "
            + "is the case, say in the Rationale which levels you set aside. "
            + "(12) Reference values are the company's own prior statements of the same metric. When a "
            + "supplied fact quotes a metric with a reference value, read the DIRECTION from the "
            + "comparison and cite BOTH the fact and the ReferenceId. Never cite a ReferenceId as a "
            + "trajectory fact on its own — a reference value is a comparison basis, not news. "
            + "Return: BusinessTrajectory (\"Improving\" | \"Deteriorating\" | \"Mixed\" | "
            + "\"Unknown\" — a factual read over the families); TrajectoryFactIds (the supplied FactIds "
            + "that establish that trajectory, each the COMPLETE 36-character value; at least one for "
            + "Improving, Deteriorating or Mixed; EMPTY for Unknown; no duplicates); TrajectoryReferenceIds "
            + "(the supplied ReferenceIds you read that trajectory's direction against, each the COMPLETE "
            + "36-character value copied character for character; EMPTY when you used none); "
            + "ChallengeStrength (0-100, or null when you record no "
            + "findings); Findings, each with: Category (one of: "
            + string.Join(", ", Enum.GetNames<NewsRiskCategory>())
            + "); Severity (Low | Medium | High); Confidence (number in [0,1]); FactIds (one or more "
            + "supplied fact ids, each the COMPLETE 36-character value); ReferenceIds (zero or more supplied "
            + "ReferenceIds the finding compares against, each the COMPLETE 36-character value); "
            + "AttributionCaveat (required per rule 8, otherwise optional); and a "
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
            sb.AppendLine("ComparisonBasis: " + ComparisonBasisLine(family.ComparisonBasis));
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

        // Spec 215 §2: the company-reported reference block, AFTER the families and ONLY when the ledger
        // projected any — a company whose releases Radar has not yet read gets a byte-identical message.
        // Values are rendered AS STATED (no arithmetic), each line citable by its ReferenceId.
        if (request.References is { Count: > 0 } references)
        {
            sb.AppendLine(
                "Company-reported reference values (from SEC filings Radar read; cite by ReferenceId):");
            foreach (var reference in references)
            {
                sb.AppendLine(ReferenceValueLine(reference));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// One reference line, exactly the spec-215 shape:
    /// <c>ReferenceId: {id} · {Metric} · {value} {unit} · {period} · stated in {form} filed {yyyy-MM-dd} · "{quote}"</c>,
    /// with the prior pair appended as <c>(prior {priorValue} {unit}, {priorPeriod})</c> only when the
    /// release stated one. The unit is omitted when blank; the filing date is UTC (AD-3).
    /// </summary>
    internal static string ReferenceValueLine(NewsJudgmentReferenceValue reference)
    {
        var unit = reference.Unit.Length > 0 ? " " + reference.Unit : string.Empty;
        var prior = reference.PriorValue is { Length: > 0 } priorValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" (prior {priorValue}{unit}, {reference.PriorPeriod})")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ReferenceId: {reference.ReferenceId:D} · {reference.Metric} · {reference.Value}{unit}{prior} · "
                + $"{reference.Period} · stated in {reference.Form} filed {reference.FilingDateUtc:yyyy-MM-dd} · "
                + $"\"{reference.Quote}\"");
    }

    /// <summary>
    /// Spec 214 §1 — the per-family basis line, rendered from the deterministic classifier's value. The
    /// LevelOnly form carries its gloss ("a stated level, not a trend") because that is the one the judge
    /// is being told NOT to cite as trajectory support; the others are the bare token. Exhaustive over the
    /// enum: an undefined value is a programming error, never a silently blank line.
    /// </summary>
    internal static string ComparisonBasisLine(NewsFactComparisonBasis basis) => basis switch
    {
        NewsFactComparisonBasis.StatedComparison => "StatedComparison",
        NewsFactComparisonBasis.LevelOnly => "LevelOnly — a stated level, not a trend",
        NewsFactComparisonBasis.Event => "Event",
        NewsFactComparisonBasis.NotQuantified => "NotQuantified",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Undefined comparison basis."),
    };

    private static string HashText(string text) => CanonicalHash.Sha256Hex(text);
}
