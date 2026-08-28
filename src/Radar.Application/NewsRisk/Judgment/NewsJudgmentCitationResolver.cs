namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The CLOSED vocabulary of ways one raw FactId citation can fail to resolve against the supplied
/// representative-fact set (spec 197 §2.1). Every member is a DISTINCT named reason: "the model invented an
/// id", "the model shortened an id past the point of uniqueness" and "the model sent something that is not
/// an id at all" are different facts about the provider, and collapsing them would hide the very pressure
/// this slice exists to measure.
/// </summary>
public enum NewsJudgmentCitationFailure
{
    /// <summary>
    /// The token is neither a parseable GUID nor a hexadecimal, hyphen-free prefix candidate — a paraphrase,
    /// a hyphenated fragment, a non-hex string, or a hex run longer than a complete id. The DEGRADED zero
    /// value (the spec-182 convention): a default-constructed rejection must never read as a milder failure.
    /// </summary>
    Malformed = 0,

    /// <summary>
    /// A complete, parseable GUID that is not in the supplied representative-fact set — an invented or
    /// out-of-scope id.
    /// </summary>
    NotSupplied,

    /// <summary>
    /// A hyphen-free hexadecimal token shorter than
    /// <see cref="NewsJudgmentCitationResolver.MinimumPrefixLength"/> characters. Too little identity to
    /// expand safely, whether or not it happens to match.
    /// </summary>
    PrefixTooShort,

    /// <summary>
    /// A well-formed prefix candidate that prefixes NO supplied representative FactId (a suffix, an
    /// interior substring, or simply an id that was never supplied).
    /// </summary>
    PrefixUnmatched,

    /// <summary>
    /// A well-formed prefix candidate that prefixes TWO OR MORE supplied representative FactIds. Radar
    /// never picks the first collision.
    /// </summary>
    PrefixAmbiguous,
}

/// <summary>
/// One raw citation's resolution. The invariant "<see cref="Resolved"/> ⟺ <see cref="Failure"/> is null" is
/// enforced by the private constructor plus the two factories, so an accepted resolution carrying a failure
/// reason (or a rejection carrying a usable FactId) is unrepresentable.
/// </summary>
public sealed record NewsJudgmentCitationResolution
{
    private NewsJudgmentCitationResolution(
        bool resolved, Guid factId, bool expanded, NewsJudgmentCitationFailure? failure)
    {
        Resolved = resolved;
        FactId = factId;
        Expanded = expanded;
        Failure = failure;
    }

    /// <summary>Whether the token resolved to exactly one supplied representative FactId.</summary>
    public bool Resolved { get; }

    /// <summary>The resolved FactId. <see cref="Guid.Empty"/> on a rejection — never a guess.</summary>
    public Guid FactId { get; }

    /// <summary>
    /// Whether resolution required DETERMINISTIC PREFIX EXPANSION (spec 197 §2.2's measured quantity)
    /// rather than the token already being a complete supplied GUID. Always <c>false</c> on a rejection.
    /// </summary>
    public bool Expanded { get; }

    /// <summary>The named failure class, or <c>null</c> when <see cref="Resolved"/>.</summary>
    public NewsJudgmentCitationFailure? Failure { get; }

    /// <summary>The stable reason CODE a drop reason is composed from. Empty when resolved.</summary>
    public string ReasonCode => Failure is { } failure
        ? NewsJudgmentCitationResolver.ReasonCodeFor(failure)
        : string.Empty;

    /// <summary>The clause following the quoted token in a drop reason. Empty when resolved.</summary>
    public string ReasonDetail => Failure is { } failure
        ? NewsJudgmentCitationResolver.ReasonDetailFor(failure)
        : string.Empty;

    internal static NewsJudgmentCitationResolution Accepted(Guid factId, bool expanded) =>
        new(resolved: true, factId, expanded, failure: null);

    internal static NewsJudgmentCitationResolution Rejected(NewsJudgmentCitationFailure failure) =>
        new(resolved: false, Guid.Empty, expanded: false, failure);
}

/// <summary>
/// SPEC 197 §2.1 — the ONE citation resolver, used by BOTH the trajectory-evidence gate and the per-finding
/// citation loop. There is deliberately no second copy: two resolvers would let a token be "citable enough"
/// for a trajectory and "invented" for a finding, and the expansion count below would then have two
/// disagreeing definitions.
/// <para>
/// <b>Why it exists.</b> Five of nineteen judgments on baseline run
/// <c>0b48b865-76b8-4485-996c-9b9139b694aa</c> failed validation for exactly one reason: the judge cited an
/// EIGHT-CHARACTER PREFIX of a supplied FactId (e.g. <c>11e52ee0</c>) instead of the complete 36-character
/// value. Those responses carried real, grounded findings that were then never examined. The prompt now
/// states the rule explicitly (see <c>ChatNewsJudgmentAnalyzer.SystemInstruction</c>), but prompt wording is
/// not a recovery mechanism — this is.
/// </para>
/// <para>
/// <b>It is NOT fuzzy inference.</b> A prefix is expanded only when the SCOPED supplied set — the families
/// actually handed to the judge for this one company — contains exactly ONE representative FactId whose
/// canonical 32-character <c>N</c> rendering it prefixes. Zero matches, two-or-more matches, a token below
/// <see cref="MinimumPrefixLength"/>, a suffix/interior substring and any other malformed token each fail
/// with their own named reason. The resolver never consults the global fact store, never selects the first
/// collision and never relaxes the supplied-set rule: the referent is deterministic or the citation fails.
/// </para>
/// <para>
/// <b>Distinctness is the CALLER's job, and must be applied AFTER expansion</b> (rule 5): a complete GUID
/// and its own prefix in one list are ONE citation, so the trajectory gate adds to its <c>seen</c> set using
/// the EXPANDED value.
/// </para>
/// <para>
/// One instance is scoped to ONE <c>Validate</c> call and is NOT thread-safe: it accumulates
/// <see cref="ExpansionCount"/> across both call sites, which is what makes "raw citation occurrences
/// expanded across trajectory plus findings" a single number produced in a single place rather than two
/// counters that can drift.
/// </para>
/// </summary>
public sealed class NewsJudgmentCitationResolver
{
    /// <summary>
    /// The shortest hexadecimal run that may be expanded (spec 197 §2.1). Eight characters is 32 bits of
    /// identity, which is what the live provider actually emitted, and — measured over the five live
    /// failures — resolved to exactly one supplied fact for ALL 44 distinct tokens against supplied sets of
    /// 24-35 families, with zero unmatched and zero ambiguous. Below it the token carries too little
    /// identity to expand at all, and is rejected as <see cref="NewsJudgmentCitationFailure.PrefixTooShort"/>
    /// EVEN WHEN it happens to match uniquely: a short match in a small supplied set is coincidence, not
    /// evidence, and a larger set would silently start selecting the wrong fact.
    /// </summary>
    public const int MinimumPrefixLength = 8;

    /// <summary>
    /// The canonical <c>N</c> rendering's length — a token this long (or longer) is never a prefix
    /// candidate, because 32 hex characters already parse as a complete GUID.
    /// </summary>
    private const int CanonicalLength = 32;

    /// <summary>
    /// The supplied representative FactIds, with their canonical lowercase <c>N</c> renderings, in
    /// supplied order (AD-3).
    /// </summary>
    private readonly (Guid Id, string Canonical)[] _supplied;

    private readonly HashSet<Guid> _suppliedIds;

    public NewsJudgmentCitationResolver(IEnumerable<Guid> suppliedFactIds)
    {
        ArgumentNullException.ThrowIfNull(suppliedFactIds);

        _supplied = [.. suppliedFactIds.Distinct().Select(id => (Id: id, Canonical: id.ToString("N")))];
        _suppliedIds = [.. _supplied.Select(s => s.Id)];
    }

    /// <summary>
    /// How many RAW CITATION OCCURRENCES this validation deterministically expanded, across trajectory and
    /// findings alike (spec 197 §2.2). Counted at the single point of expansion, so it counts occurrences
    /// rather than distinct ids, and it includes expansions performed before a LATER, unrelated validation
    /// error failed the response — the pressure was real whether or not the response survived.
    /// </summary>
    public int ExpansionCount { get; private set; }

    /// <summary>The stable reason CODE for one failure class. Callers prefix it with their own scope.</summary>
    public static string ReasonCodeFor(NewsJudgmentCitationFailure failure) => failure switch
    {
        NewsJudgmentCitationFailure.NotSupplied => "fact-not-supplied",
        NewsJudgmentCitationFailure.PrefixTooShort => "fact-id-prefix-too-short",
        NewsJudgmentCitationFailure.PrefixUnmatched => "fact-id-prefix-unmatched",
        NewsJudgmentCitationFailure.PrefixAmbiguous => "fact-id-prefix-ambiguous",
        _ => "fact-id-malformed",
    };

    /// <summary>The clause a drop reason appends after the quoted token, naming what went wrong.</summary>
    public static string ReasonDetailFor(NewsJudgmentCitationFailure failure) => failure switch
    {
        NewsJudgmentCitationFailure.NotSupplied =>
            "is not a supplied representative fact id",
        NewsJudgmentCitationFailure.PrefixTooShort =>
            $"is shorter than the {MinimumPrefixLength}-character minimum for a recoverable FactId prefix; "
                + "copy the complete 36-character FactId exactly as supplied",
        NewsJudgmentCitationFailure.PrefixUnmatched =>
            "prefixes no supplied representative fact id (a suffix or interior fragment is never expanded); "
                + "copy the complete 36-character FactId exactly as supplied",
        NewsJudgmentCitationFailure.PrefixAmbiguous =>
            "prefixes more than one supplied representative fact id, so it has no deterministic referent; "
                + "copy the complete 36-character FactId exactly as supplied",
        _ =>
            "is not a FactId: it parses neither as a complete GUID nor as a hyphen-free hexadecimal prefix "
                + "of one; copy the complete 36-character FactId exactly as supplied",
    };

    /// <summary>
    /// Resolves one raw citation, fail-closed, in the rule order spec 197 §2.1 states:
    /// <list type="number">
    /// <item>a parseable GUID is accepted ONLY when it is in the supplied set;</item>
    /// <item>otherwise the token must be 8-31 ASCII hexadecimal characters with NO hyphens, and an
    /// ordinal-ignore-case PREFIX of the canonical 32-character <c>N</c> rendering of exactly one supplied
    /// representative FactId;</item>
    /// <item>exactly one match expands to that full GUID (and increments
    /// <see cref="ExpansionCount"/>);</item>
    /// <item>zero matches, two-or-more matches, a too-short prefix, a suffix/substring and any other
    /// malformed token each fail with their own named reason.</item>
    /// </list>
    /// </summary>
    public NewsJudgmentCitationResolution Resolve(string? rawFactId)
    {
        var token = rawFactId?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return NewsJudgmentCitationResolution.Rejected(NewsJudgmentCitationFailure.Malformed);
        }

        // Rule 1. Guid.TryParse IS the ordinal-preserving normalization: every spelling of one id (D, N, B,
        // P, any casing) canonicalises onto the same value, so "distinct" cannot be defeated by formatting.
        // A complete 32-character hex run parses here too, which is precisely why the prefix window below
        // stops at 31.
        if (Guid.TryParse(token, out var parsed))
        {
            return _suppliedIds.Contains(parsed)
                ? NewsJudgmentCitationResolution.Accepted(parsed, expanded: false)
                : NewsJudgmentCitationResolution.Rejected(NewsJudgmentCitationFailure.NotSupplied);
        }

        if (!IsHexadecimalWithoutHyphens(token))
        {
            return NewsJudgmentCitationResolution.Rejected(NewsJudgmentCitationFailure.Malformed);
        }

        if (token.Length < MinimumPrefixLength)
        {
            return NewsJudgmentCitationResolution.Rejected(NewsJudgmentCitationFailure.PrefixTooShort);
        }

        if (token.Length >= CanonicalLength)
        {
            // A hex run at or beyond a complete id's length that did NOT parse is malformed, not a prefix.
            return NewsJudgmentCitationResolution.Rejected(NewsJudgmentCitationFailure.Malformed);
        }

        var matched = Guid.Empty;
        var matches = 0;
        foreach (var (id, canonical) in _supplied)
        {
            if (!canonical.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches++;
            if (matches > 1)
            {
                // Rule 4: two or more referents means NO referent. Never select the first collision.
                return NewsJudgmentCitationResolution.Rejected(
                    NewsJudgmentCitationFailure.PrefixAmbiguous);
            }

            matched = id;
        }

        if (matches == 0)
        {
            return NewsJudgmentCitationResolution.Rejected(NewsJudgmentCitationFailure.PrefixUnmatched);
        }

        ExpansionCount++;
        return NewsJudgmentCitationResolution.Accepted(matched, expanded: true);
    }

    /// <summary>
    /// ASCII hexadecimal ONLY, with no hyphens, braces, whitespace or Unicode digits. Deliberately explicit
    /// rather than <c>char.IsAsciiHexDigit</c>-adjacent helpers that also admit other digit categories: a
    /// FactId prefix is a run of <c>0-9a-fA-F</c> and nothing else.
    /// </summary>
    private static bool IsHexadecimalWithoutHyphens(string token)
    {
        foreach (var c in token)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
