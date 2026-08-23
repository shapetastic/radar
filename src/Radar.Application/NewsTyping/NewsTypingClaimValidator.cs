using Radar.Application.Identity;

namespace Radar.Application.NewsTyping;

/// <summary>The validated projection of one typing response, with the full drop accounting spec 181 §2 requires.</summary>
public sealed record NewsTypingValidationResult(
    NewsTypingStatus Status,
    NewsTypingRelevance? Relevance,
    NewsEventType? DerivedPrimaryType,
    IReadOnlyList<NewsTypingValidatedFact> Facts,
    int FactsTotal,
    int FactsAccepted,
    int FactsDropped,
    IReadOnlyList<string> FactDropReasons);

/// <summary>
/// Mechanical validation of one typing response (spec 181 §2, mirroring the spec-179 §6 validator), pure and
/// deterministic. "Supplied text" means EXACTLY the fields handed to the model for THIS observation —
/// headline, description when supplied, permitted archived body when supplied. Rules:
/// <list type="bullet">
/// <item>the relevance token must parse against the closed vocabulary — an unparseable relevance is
/// <see cref="NewsTypingStatus.ValidationFailed"/>, never a silent default;</item>
/// <item>every <c>EventTypes</c> entry must token-parse against taxonomy v1, and attribution/assertion
/// tokens against their closed vocabularies — an unknown token drops THAT fact with a named reason;</item>
/// <item>confidence must be a number in [0,1];</item>
/// <item>every citation must be an EXACT ordinal substring of a supplied field — no normalization before
/// matching, so the citation-drop rate is measurable. Unlike spec 179 (which drops the whole claim on one
/// bad excerpt), an invalid citation is dropped INDIVIDUALLY and the fact survives on its remaining
/// verified citations — the stage-1 omission-bias guard: a fact with verified support must reach stage 2.
/// A fact with ZERO valid citations is dropped with a named reason;</item>
/// <item>the model emitting facts of which NONE survives is
/// <see cref="NewsTypingStatus.ValidationFailed"/>; emitting ZERO facts with a parsed relevance is a
/// completed typing with an empty fact list (liberal extraction makes "nothing to extract" a legitimate
/// answer, not a failure);</item>
/// <item><c>DerivedPrimaryType</c> is DERIVED here (never authored by the model): the event type with the
/// greatest summed fact confidence over the accepted facts, ties broken by taxonomy declaration order.</item>
/// </list>
/// Fact ids are minted deterministically from cohort + observation + payload + the fact's WIRE index (stable
/// regardless of which sibling facts were dropped); the model never authors identifiers.
/// </summary>
public static class NewsTypingClaimValidator
{
    public static NewsTypingValidationResult Validate(
        NewsTypingModelResponse response, NewsTypingInputObservation input, string cohortKey)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortKey);

        var dropReasons = new List<string>();
        var rawFacts = response.Facts ?? [];

        if (!NewsTypingTokens.TryParse<NewsTypingRelevance>(response.Relevance, out var relevance))
        {
            dropReasons.Add(
                $"relevance-token-invalid: '{response.Relevance}' is not a defined relevance");
            return new NewsTypingValidationResult(
                NewsTypingStatus.ValidationFailed,
                Relevance: null,
                DerivedPrimaryType: null,
                Facts: [],
                FactsTotal: rawFacts.Count,
                FactsAccepted: 0,
                FactsDropped: rawFacts.Count,
                FactDropReasons: dropReasons);
        }

        var accepted = new List<NewsTypingValidatedFact>();
        for (var i = 0; i < rawFacts.Count; i++)
        {
            var fact = rawFacts[i];
            if (fact is null)
            {
                dropReasons.Add($"fact[{i}] null-fact");
                continue;
            }

            var rawTypes = (fact.EventTypes ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (rawTypes.Count == 0)
            {
                dropReasons.Add($"fact[{i}] no-event-type");
                continue;
            }

            var types = new List<NewsEventType>();
            var typesOk = true;
            foreach (var token in rawTypes)
            {
                if (!NewsEventTaxonomy.TryParse(token, out var type))
                {
                    dropReasons.Add($"fact[{i}] event-type-invalid: '{token}'");
                    typesOk = false;
                    break;
                }

                if (!types.Contains(type))
                {
                    types.Add(type);
                }
            }

            if (!typesOk)
            {
                continue;
            }

            var statement = fact.Statement?.Trim();
            if (string.IsNullOrEmpty(statement))
            {
                dropReasons.Add($"fact[{i}] no-statement");
                continue;
            }

            if (!NewsTypingTokens.TryParse<NewsFactAttribution>(fact.Attribution, out var attribution))
            {
                dropReasons.Add($"fact[{i}] attribution-invalid: '{fact.Attribution}'");
                continue;
            }

            if (!NewsTypingTokens.TryParse<NewsFactAssertionStatus>(
                fact.AssertionStatus, out var assertionStatus))
            {
                dropReasons.Add($"fact[{i}] assertion-status-invalid: '{fact.AssertionStatus}'");
                continue;
            }

            if (fact.Confidence is not { } confidence || confidence is < 0.0 or > 1.0
                || double.IsNaN(confidence))
            {
                dropReasons.Add($"fact[{i}] confidence-out-of-range: '{fact.Confidence}'");
                continue;
            }

            // Citations: each verified individually against the supplied text; invalid ones are dropped
            // and NAMED, the fact survives on its verified remainder, and zero verified citations drops
            // the fact (an uncitable fact must never reach stage 2 as if it were supported).
            var validCitations = new List<string>();
            foreach (var citation in (fact.Citations ?? []).Where(c => !string.IsNullOrEmpty(c)))
            {
                if (CitationIsSuppliedText(citation, input))
                {
                    validCitations.Add(citation);
                }
                else
                {
                    dropReasons.Add(
                        $"fact[{i}] citation-not-exact-substring-of-supplied-text: "
                            + $"'{Truncate(citation, 120)}'");
                }
            }

            if (validCitations.Count == 0)
            {
                dropReasons.Add($"fact[{i}] no-valid-citation");
                continue;
            }

            accepted.Add(new NewsTypingValidatedFact(
                FactId: FactIdFor(cohortKey, input.ObservationId, input.PayloadHash, i),
                EventTypes: types,
                Statement: statement,
                TemporalScope: NullIfBlank(fact.TemporalScope),
                Attribution: attribution,
                AssertionStatus: assertionStatus,
                Confidence: confidence,
                Citations: validCitations));
        }

        var dropped = rawFacts.Count - accepted.Count;
        if (rawFacts.Count > 0 && accepted.Count == 0)
        {
            // The model asserted facts and NONE survived: fail closed — never a silent default type.
            return new NewsTypingValidationResult(
                NewsTypingStatus.ValidationFailed,
                Relevance: relevance,
                DerivedPrimaryType: null,
                Facts: [],
                FactsTotal: rawFacts.Count,
                FactsAccepted: 0,
                FactsDropped: dropped,
                FactDropReasons: dropReasons);
        }

        var status = relevance == NewsTypingRelevance.InsufficientContent
            ? NewsTypingStatus.InsufficientContent
            : NewsTypingStatus.Typed;
        return new NewsTypingValidationResult(
            status,
            Relevance: relevance,
            DerivedPrimaryType: DerivePrimaryType(accepted),
            Facts: accepted,
            FactsTotal: rawFacts.Count,
            FactsAccepted: accepted.Count,
            FactsDropped: dropped,
            FactDropReasons: dropReasons);
    }

    /// <summary>
    /// The ONE <c>DerivedPrimaryType</c> rule (spec 181 §2): the event type with the greatest summed
    /// confidence over the accepted facts (a fact contributes its confidence to EACH of its event types),
    /// ties broken by taxonomy declaration order. Display only — never authored by the model, never a
    /// scoring input. Null when no fact survived.
    /// </summary>
    public static NewsEventType? DerivePrimaryType(IReadOnlyList<NewsTypingValidatedFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Count == 0)
        {
            return null;
        }

        var sums = new Dictionary<NewsEventType, double>();
        foreach (var fact in facts)
        {
            foreach (var type in fact.EventTypes)
            {
                sums[type] = sums.GetValueOrDefault(type) + fact.Confidence;
            }
        }

        // Max summed confidence; tie-break = lowest declaration order (the enum value).
        return sums
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => (int)kv.Key)
            .First().Key;
    }

    /// <summary>The deterministic fact identity: cohort + observation + payload + WIRE index. The model never authors ids.</summary>
    public static Guid FactIdFor(string cohortKey, Guid observationId, string payloadHash, int wireIndex) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:news-typing-fact:{cohortKey}:{observationId:D}:{payloadHash}:{wireIndex}");

    /// <summary>
    /// Whether <paramref name="citation"/> is an exact ordinal substring of a field ACTUALLY supplied for
    /// this observation: the headline, the description when supplied, the permitted body when supplied.
    /// Omitted fields — and everything else (URL, publisher, metadata) — are not citable text.
    /// </summary>
    private static bool CitationIsSuppliedText(string citation, NewsTypingInputObservation input) =>
        input.Headline.Contains(citation, StringComparison.Ordinal)
        || (input.DescriptionText is not null
            && input.DescriptionText.Contains(citation, StringComparison.Ordinal))
        || (input.BodyText is not null
            && input.BodyText.Contains(citation, StringComparison.Ordinal));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
