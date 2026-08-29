using Radar.Application.Filings;

namespace Radar.Application.NewsRisk;

/// <summary>The §6-validated projection of one model response, with the full drop accounting the spec requires.</summary>
public sealed record NewsRiskValidationResult(
    NewsRiskAssessmentStatus Status,
    int? RiskScore,
    IReadOnlyList<NewsRiskCategory> Categories,
    IReadOnlyList<NewsRiskValidatedClaim> Claims,
    string? Rationale,
    int ClaimsTotal,
    int ClaimsAccepted,
    int ClaimsDropped,
    IReadOnlyList<string> ClaimDropReasons);

/// <summary>
/// Mechanical §6 validation (spec 179), pure and deterministic. "Archived text" means EXACTLY the fields
/// supplied to the model for each observation — headline, <c>descriptionText</c> when supplied, permitted
/// extracted body when supplied — never raw HTML, metadata, URLs or omitted fields. Rules:
/// <list type="bullet">
/// <item>every cited observation id must have been supplied;</item>
/// <item>every excerpt must be an EXACT ordinal substring of at least one supplied text field for that
/// observation — deliberately strict, with NO normalization before matching; model whitespace normalization
/// may drop real claims, and the drop counts/reasons exist precisely so that rate is measurable;</item>
/// <item>enum tokens, score, severity and confidence must be in range;</item>
/// <item>the advice-language guard passes on the RATIONALE (Radar-surfaced free text — a rationale carrying
/// advice language is blanked and the event recorded; verbatim excerpts are quoted third-party source text
/// and are cited, not authored, so the guard does not police them); and</item>
/// <item><see cref="NewsRiskAssessmentKind.ThesisChallenged"/> must retain at least one supported category —
/// when EVERY claim fails, the result is <see cref="NewsRiskAssessmentStatus.ValidationFailed"/>, never
/// <see cref="NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText"/> (fail closed: an unverifiable warning is
/// not evidence of safety).</item>
/// </list>
/// </summary>
public static class NewsRiskClaimValidator
{
    public static NewsRiskValidationResult Validate(
        NewsRiskModelResponse response, IReadOnlyList<NewsRiskInputArticle> suppliedArticles)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(suppliedArticles);

        var suppliedById = suppliedArticles.ToDictionary(a => a.ObservationId);
        var dropReasons = new List<string>();

        // Rationale: bounded, trimmed, and scrubbed through the ONE shared advice-language guard.
        var rationale = response.Rationale?.Trim();
        if (!string.IsNullOrEmpty(rationale) && AdviceLanguageGuard.ContainsAdviceLanguage(rationale))
        {
            dropReasons.Add("rationale-advice-language: rationale contained advice language and was dropped");
            rationale = null;
        }

        if (!TryParseToken<NewsRiskAssessmentKind>(response.Assessment, out var kind))
        {
            return Failed(
                rationale,
                dropReasons,
                $"assessment-token-invalid: '{response.Assessment}' is not a defined assessment",
                // Spec 201 §4: an absent claims array is not "zero claims" — total is what was supplied.
                total: response.Claims?.Count ?? 0);
        }

        // Spec 201 §4: a MISSING claims array is a malformed response and fails with a named reason. It is
        // not "zero claims" — the schema requires the array, and reading its absence as an empty list would
        // let a response that omitted its evidence validate as "no claims made" (a ThesisChallenged with a
        // missing array would fail closed anyway; a NoRiskFoundInSuppliedText would NOT, and would render as
        // a clean read on a response that never stated its claims).
        if (response.Claims is null)
        {
            return Failed(
                rationale,
                dropReasons,
                "claims-array-missing: the response carried no claims array (a missing array is not zero claims)",
                total: 0);
        }

        var rawClaims = response.Claims;
        var accepted = new List<NewsRiskValidatedClaim>();
        for (var i = 0; i < rawClaims.Count; i++)
        {
            var claim = rawClaims[i];
            if (claim is null)
            {
                dropReasons.Add($"claim[{i}] null-claim");
                continue;
            }

            if (!TryParseToken<NewsRiskCategory>(claim.Category, out var category))
            {
                dropReasons.Add($"claim[{i}] category-invalid: '{claim.Category}'");
                continue;
            }

            if (!TryParseToken<NewsRiskSeverity>(claim.Severity, out var severity))
            {
                dropReasons.Add($"claim[{i}] severity-invalid: '{claim.Severity}'");
                continue;
            }

            if (claim.Confidence is not { } confidence || confidence is < 0.0 or > 1.0
                || double.IsNaN(confidence))
            {
                dropReasons.Add($"claim[{i}] confidence-out-of-range: '{claim.Confidence}'");
                continue;
            }

            var citedIds = new List<Guid>();
            var citedOk = true;
            foreach (var rawId in claim.ObservationIds ?? [])
            {
                if (!Guid.TryParse(rawId, out var id) || !suppliedById.ContainsKey(id))
                {
                    dropReasons.Add($"claim[{i}] cited-observation-not-supplied: '{rawId}'");
                    citedOk = false;
                    break;
                }

                citedIds.Add(id);
            }

            if (!citedOk)
            {
                continue;
            }

            if (citedIds.Count == 0)
            {
                dropReasons.Add($"claim[{i}] no-cited-observation");
                continue;
            }

            var excerpts = (claim.Excerpts ?? []).Where(e => !string.IsNullOrEmpty(e)).ToList();
            if (excerpts.Count == 0)
            {
                dropReasons.Add($"claim[{i}] no-excerpt");
                continue;
            }

            // EXACT ordinal substring of at least one SUPPLIED text field of at least one CITED
            // observation. No normalization before matching, per §6 — a near-miss is a recorded drop.
            var allExcerptsSupported = true;
            foreach (var excerpt in excerpts)
            {
                var supported = citedIds.Any(id => ExcerptIsSuppliedText(excerpt, suppliedById[id]));
                if (!supported)
                {
                    dropReasons.Add(
                        $"claim[{i}] excerpt-not-exact-substring-of-supplied-text: "
                            + $"'{Truncate(excerpt, 120)}'");
                    allExcerptsSupported = false;
                    break;
                }
            }

            if (!allExcerptsSupported)
            {
                continue;
            }

            accepted.Add(new NewsRiskValidatedClaim(category, severity, confidence, citedIds, excerpts));
        }

        var total = rawClaims.Count;
        var dropped = total - accepted.Count;

        switch (kind)
        {
            case NewsRiskAssessmentKind.ThesisChallenged:
            {
                if (accepted.Count == 0)
                {
                    // Every claim failed (or none was made): fail closed. NEVER NoRiskFoundInSuppliedText.
                    return new NewsRiskValidationResult(
                        NewsRiskAssessmentStatus.ValidationFailed,
                        RiskScore: null,
                        Categories: [],
                        Claims: [],
                        Rationale: rationale,
                        ClaimsTotal: total,
                        ClaimsAccepted: 0,
                        ClaimsDropped: dropped,
                        ClaimDropReasons: dropReasons);
                }

                if (response.RiskScore is not { } score || score is < 0 or > 100)
                {
                    return Failed(
                        rationale,
                        dropReasons,
                        $"risk-score-out-of-range: '{response.RiskScore}'",
                        total,
                        accepted.Count);
                }

                // Categories are the DISTINCT categories of the ACCEPTED claims, in first-occurrence
                // order — a declared category no surviving claim supports is not retained.
                var categories = accepted.Select(c => c.Category).Distinct().ToList();
                return new NewsRiskValidationResult(
                    NewsRiskAssessmentStatus.ThesisChallenged,
                    RiskScore: score,
                    Categories: categories,
                    Claims: accepted,
                    Rationale: rationale,
                    ClaimsTotal: total,
                    ClaimsAccepted: accepted.Count,
                    ClaimsDropped: dropped,
                    ClaimDropReasons: dropReasons);
            }

            case NewsRiskAssessmentKind.NoRiskFoundInSuppliedText:
                // Score/claims are coerced away (a "no risk" with a score would be incoherent); the §7
                // fail-closed render gate is the CALLER's job (coverage/input sufficiency are not visible
                // here).
                return new NewsRiskValidationResult(
                    NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText,
                    RiskScore: null,
                    Categories: [],
                    Claims: [],
                    Rationale: rationale,
                    ClaimsTotal: total,
                    ClaimsAccepted: accepted.Count,
                    ClaimsDropped: dropped,
                    ClaimDropReasons: dropReasons);

            default:
                return new NewsRiskValidationResult(
                    NewsRiskAssessmentStatus.InsufficientContent,
                    RiskScore: null,
                    Categories: [],
                    Claims: [],
                    Rationale: rationale,
                    ClaimsTotal: total,
                    ClaimsAccepted: accepted.Count,
                    ClaimsDropped: dropped,
                    ClaimDropReasons: dropReasons);
        }
    }

    /// <summary>
    /// Whether <paramref name="excerpt"/> is an exact ordinal substring of a field ACTUALLY SUPPLIED for
    /// this article: the headline, the description when supplied, the permitted body when supplied. Omitted
    /// fields — and everything else (URL, publisher, metadata) — are not citable text.
    /// </summary>
    private static bool ExcerptIsSuppliedText(string excerpt, NewsRiskInputArticle article) =>
        article.Headline.Contains(excerpt, StringComparison.Ordinal)
        || (article.DescriptionText is not null
            && article.DescriptionText.Contains(excerpt, StringComparison.Ordinal))
        || (article.BodyText is not null
            && article.BodyText.Contains(excerpt, StringComparison.Ordinal));

    /// <summary>
    /// Spec 201 §4: the claim total is a REQUIRED, caller-measured argument — the pre-201 shape defaulted it
    /// from <c>Claims?.Count ?? 0</c>, which read a missing array as a measured zero.
    /// </summary>
    private static NewsRiskValidationResult Failed(
        string? rationale,
        List<string> dropReasons,
        string reason,
        int total,
        int accepted = 0)
    {
        dropReasons.Add(reason);
        var claimTotal = total;
        return new NewsRiskValidationResult(
            NewsRiskAssessmentStatus.ValidationFailed,
            RiskScore: null,
            Categories: [],
            Claims: [],
            Rationale: rationale,
            ClaimsTotal: claimTotal,
            ClaimsAccepted: accepted,
            ClaimsDropped: claimTotal - accepted,
            ClaimDropReasons: dropReasons);
    }

    /// <summary>Exact enum-name token parse (case-insensitive), never numeric — "3" must not become a category.</summary>
    private static bool TryParseToken<TEnum>(string? token, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return !string.IsNullOrWhiteSpace(token)
            && !token.Trim().All(char.IsAsciiDigit)
            && Enum.TryParse(token.Trim(), ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
