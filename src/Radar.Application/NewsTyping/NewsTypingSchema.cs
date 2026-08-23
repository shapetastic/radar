namespace Radar.Application.NewsTyping;

/// <summary>
/// The CLOSED per-observation relevance vocabulary (spec 181 §2). <see cref="InsufficientContent"/> is a
/// statement about the supplied text, never a low anything — headline-only legacy capture is EXPECTED to
/// produce more of it, and that honesty is what capture-mode cohort separation preserves.
/// </summary>
public enum NewsTypingRelevance
{
    CompanySpecific = 0,
    SectorOrMacroContext,
    NotAboutThisCompany,
    InsufficientContent,
}

/// <summary>
/// WHO asserts a fact (spec 181 §2) — first-class because stage 2 never sees the prose: an SEC investigation
/// vs a plaintiff-firm shareholder solicitation must be distinguishable from the fact record alone. Closed
/// token vocabulary: <c>company | regulator | plaintiff-firm | publisher | analyst | exchange |
/// short-seller | other-specified</c>, parsed by <see cref="NewsTypingTokens"/>.
/// </summary>
public enum NewsFactAttribution
{
    Company = 0,
    Regulator,
    PlaintiffFirm,
    Publisher,
    Analyst,
    Exchange,
    ShortSeller,
    OtherSpecified,
}

/// <summary>
/// The epistemic status of a fact's assertion (spec 181 §2): a confirmed filing vs a publisher report vs an
/// allegation vs a solicitation vs "may face" speculation vs a company announcement. Closed token
/// vocabulary: <c>confirmed-filing | reported | alleged | solicited | speculative | announced</c>.
/// </summary>
public enum NewsFactAssertionStatus
{
    ConfirmedFiling = 0,
    Reported,
    Alleged,
    Solicited,
    Speculative,
    Announced,
}

/// <summary>
/// The WIRE shape of the model's structured response (spec 181 §2) — deliberately all strings/numbers (the
/// spec-179 rule), so an out-of-vocabulary value arrives as data the validator can NAME in a drop reason
/// instead of being silently coerced by enum deserialization. Nothing here is persisted as-is: only the
/// validated projection is. The shape deliberately carries NO direction, severity, materiality, sentiment or
/// score member and no fact/family identifier (identifiers are minted deterministically by the validator;
/// families are the separate §4 pass) — enforced structurally by a reflection guard test.
/// </summary>
public sealed record NewsTypingModelResponse(
    string? Relevance,
    IReadOnlyList<NewsTypingModelFact>? Facts);

/// <summary>One raw model fact: event-type/attribution/assertion tokens, the preserved statement, temporal scope, confidence and verbatim citations.</summary>
public sealed record NewsTypingModelFact(
    IReadOnlyList<string>? EventTypes,
    string? Statement,
    string? TemporalScope,
    string? Attribution,
    string? AssertionStatus,
    double? Confidence,
    IReadOnlyList<string>? Citations);

/// <summary>
/// A validated fact: taxonomy-typed, token-parsed attribution/assertion, confidence in [0,1], and only
/// citations that are exact ordinal substrings of the supplied text. <see cref="FactId"/> is minted
/// DETERMINISTICALLY by the validator (cohort + observation + payload + wire index) — the model never
/// authors identifiers.
/// </summary>
public sealed record NewsTypingValidatedFact(
    Guid FactId,
    IReadOnlyList<NewsEventType> EventTypes,
    string Statement,
    string? TemporalScope,
    NewsFactAttribution Attribution,
    NewsFactAssertionStatus AssertionStatus,
    double Confidence,
    IReadOnlyList<string> Citations);

/// <summary>
/// The ONE parser for the closed <see cref="NewsFactAttribution"/>/<see cref="NewsFactAssertionStatus"/>
/// token vocabularies: the spec's kebab-case tokens (<c>plaintiff-firm</c>, <c>confirmed-filing</c>) and the
/// bare enum names both parse; anything else — including pure digits — is the caller's named drop reason.
/// </summary>
public static class NewsTypingTokens
{
    public static bool TryParse<TEnum>(string? token, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (trimmed.All(char.IsAsciiDigit))
        {
            return false;
        }

        // Kebab/snake tokens collapse onto the PascalCase enum name ("plaintiff-firm" → "plaintifffirm"
        // matches "PlaintiffFirm" case-insensitively); a numeric token was already rejected above.
        var collapsed = trimmed.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return collapsed.Length > 0
            && Enum.TryParse(collapsed, ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }
}
