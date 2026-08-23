using System.Security.Cryptography;
using System.Text;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The CLOSED news-event-type vocabulary, version <c>news-event-taxonomy-v1</c> (spec 181 §3) — exactly the
/// reviewed strawman including <c>MarketReaction</c> (a stock falling after earnings is a price-move report,
/// not <see cref="IndexOrTradingMechanics"/>). Declaration ORDER is load-bearing twice: it is the canonical
/// string the taxonomy hash is computed over, and it is the deterministic tie-break for
/// <c>DerivedPrimaryType</c> derivation. Never reorder, rename, add or remove a member in place — the
/// taxonomy is IMMUTABLE BY CONVENTION (spec 141's rule applied here): any change is
/// <c>news-event-taxonomy-v2</c>, a new enum + version + hash, and cohorts never pool across versions.
/// </summary>
public enum NewsEventType
{
    EarningsOrGuidance = 0,
    MergerAcquisitionOrStake,
    FinancingOrDilution,
    ProductOrTechnology,
    ContractOrCustomerWin,
    RegulatoryOrLegal,
    ManagementOrGovernance,
    AnalystOrRatingAction,
    MarketReaction,
    IndexOrTradingMechanics,
    ShortSellerOrCritique,
    DividendOrBuyback,
    /// <summary>"Coverage that says nothing about the business" — identifying it is half this spec's value.</summary>
    PromotionalOrListicle,
    OtherSpecified,
}

/// <summary>
/// The ONE definition of taxonomy v1's identity (spec 181 §3): version token, canonical member string and
/// SHA-256 hash, all derived FROM the <see cref="NewsEventType"/> declaration so they cannot drift from the
/// enum. The hash is a cohort dimension (folded into every typing cohort key via
/// <see cref="NewsTypingContract"/>), never a scoring/fingerprint input. v1 is declared from the reviewed §3
/// strawman; the §3 ≥200-observation human audit runs AGAINST first typings (the tooling this slice ships) —
/// a revision it produces lands as <c>news-event-taxonomy-v2</c>, never as an edit here.
/// </summary>
public static class NewsEventTaxonomy
{
    public const string TaxonomyVersion = "news-event-taxonomy-v1";

    /// <summary>Every taxonomy member, in declaration order (the canonical order).</summary>
    public static readonly IReadOnlyList<NewsEventType> Members = Enum.GetValues<NewsEventType>();

    /// <summary>The canonical identity string: the version token plus the member names in declaration order.</summary>
    public static readonly string CanonicalString =
        "radar:" + TaxonomyVersion + ":" + string.Join("|", Members.Select(m => m.ToString()));

    /// <summary>Lowercase-hex SHA-256 of <see cref="CanonicalString"/> — pinned by test as a change-detector.</summary>
    public static readonly string TaxonomyHash =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalString)));

    /// <summary>
    /// Exact enum-name token parse (case-insensitive, never numeric — "3" must not become an event type),
    /// mirroring the spec-179 validator's rule. An unknown token is the CALLER's named drop reason.
    /// </summary>
    public static bool TryParse(string? token, out NewsEventType value)
    {
        value = default;
        return !string.IsNullOrWhiteSpace(token)
            && !token.Trim().All(char.IsAsciiDigit)
            && Enum.TryParse(token.Trim(), ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }
}
