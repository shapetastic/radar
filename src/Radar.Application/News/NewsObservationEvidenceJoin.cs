using Radar.Application.NewsTyping;
using Radar.Domain.Evidence;

namespace Radar.Application.News;

/// <summary>
/// SPEC 197 §1.2 — how ONE observation was disposed of by the join. Total: every supplied observation maps
/// to exactly one member, so no caller needs a fallback branch and no outcome is expressible as "null".
/// <para>
/// The three JOINED members name WHICH tier of the deterministic ladder resolved the observation, because
/// "resolved by exact URL + headline + publication instant" and "resolved because one headline happened to
/// be unique in the store" are different strengths of evidence and pooling them would hide a regression in
/// the strong tiers behind the weak one's count.
/// </para>
/// <para>
/// <see cref="NoMatch"/> is deliberately the zero value (the house rule that the DEGRADED state is the
/// default), so a <c>default</c> disposition can never read as a resolved article.
/// </para>
/// </summary>
public enum NewsObservationEvidenceDisposition
{
    /// <summary>No tier found a candidate — including a blank key, a null company id, or evidence Radar never collected.</summary>
    NoMatch = 0,

    /// <summary>
    /// A tier found two or more evidence items, or a key claimed by two or more companies. The ladder STOPS
    /// here: ambiguity never falls through to a weaker key, because falling through would make the ambiguity
    /// disappear rather than resolve it.
    /// </summary>
    Ambiguous = 1,

    /// <summary>Tier 1: exact ordinal article URL + normalized headline + equal publication instant.</summary>
    ExactArticleInstant = 2,

    /// <summary>Tier 2: exact ordinal article URL + normalized headline, with no usable publication instant on one side.</summary>
    ExactArticleUrl = 3,

    /// <summary>Tier 3: the pre-197 rule — a normalized headline that exactly one evidence item and one company carry.</summary>
    UniqueHeadlineFallback = 4,
}

/// <summary>
/// How one pass's observations were disposed of by the join (spec 191 §1's per-run measurement, widened by
/// spec 197 §1.2 to name the ROUTE). Every supplied observation contributes to EXACTLY ONE bucket, so
/// <c>ExactArticleInstant + ExactArticleUrl + UniqueHeadlineFallback + UnjoinedNoMatch + UnjoinedAmbiguous</c>
/// equals the observation count.
/// <para>
/// <see cref="Joined"/> is DERIVED from the three route counts rather than stored beside them: the spec's
/// conservation identity ("<c>Joined</c> equals the sum of the three routes") is then structurally
/// unbreakable instead of being an invariant a future edit could violate silently. It keeps its pre-197
/// meaning for every existing reader.
/// </para>
/// </summary>
/// <param name="ExactArticleInstant">Observations resolved by tier 1 — exact URL + headline + publication instant.</param>
/// <param name="ExactArticleUrl">Observations resolved by tier 2 — exact URL + headline, no usable instant on one side.</param>
/// <param name="UniqueHeadlineFallback">Observations resolved by tier 3 — the pre-197 unique-normalized-headline rule.</param>
/// <param name="UnjoinedNoMatch">Observations no tier matched — including a blank key or a null company id.</param>
/// <param name="UnjoinedAmbiguous">Observations whose strongest matching tier held two or more evidence items, or two or more companies.</param>
public sealed record NewsObservationEvidenceJoinCounts(
    int ExactArticleInstant,
    int ExactArticleUrl,
    int UniqueHeadlineFallback,
    int UnjoinedNoMatch,
    int UnjoinedAmbiguous)
{
    /// <summary>The empty measurement — a pass that supplied no observation.</summary>
    public static readonly NewsObservationEvidenceJoinCounts Empty = new(0, 0, 0, 0, 0);

    /// <summary>Observations resolved by ANY tier. Derived, so it can never disagree with the routes beside it.</summary>
    public int Joined => ExactArticleInstant + ExactArticleUrl + UniqueHeadlineFallback;

    /// <summary>Every supplied observation, by the conservation identity above.</summary>
    public int Observations => Joined + UnjoinedNoMatch + UnjoinedAmbiguous;

    /// <summary>The count for one disposition — the projection that makes the partition checkable by member.</summary>
    public int For(NewsObservationEvidenceDisposition disposition) => disposition switch
    {
        NewsObservationEvidenceDisposition.ExactArticleInstant => ExactArticleInstant,
        NewsObservationEvidenceDisposition.ExactArticleUrl => ExactArticleUrl,
        NewsObservationEvidenceDisposition.UniqueHeadlineFallback => UniqueHeadlineFallback,
        NewsObservationEvidenceDisposition.Ambiguous => UnjoinedAmbiguous,
        _ => UnjoinedNoMatch,
    };
}

/// <summary>One resolved evidence ↔ observation pairing.</summary>
public sealed record NewsObservationEvidenceMatch(Guid EvidenceId, Guid CompanyId, Guid ObservationId);

/// <summary>
/// SPEC 197 §1.2 — one observation's typed outcome: the disposition, plus the match when (and only when) it
/// joined. The invariant "<see cref="Match"/> is non-null ⟺ <see cref="Disposition"/> is a joined route" is
/// enforced by construction (private constructor + factories, the spec-196 <c>AttentionSourceResolution</c>
/// precedent), so a caller can never read a match out of an ambiguous outcome or lose one from a joined one.
/// </summary>
public sealed record NewsObservationEvidenceResolution
{
    private NewsObservationEvidenceResolution(
        NewsObservationEvidenceDisposition disposition, NewsObservationEvidenceMatch? match)
    {
        Disposition = disposition;
        Match = match;
    }

    /// <summary>The shared no-match outcome — no tier found a candidate.</summary>
    public static NewsObservationEvidenceResolution NoMatch { get; } =
        new(NewsObservationEvidenceDisposition.NoMatch, null);

    /// <summary>The shared ambiguous outcome — a tier matched, but not to exactly one article and one company.</summary>
    public static NewsObservationEvidenceResolution Ambiguous { get; } =
        new(NewsObservationEvidenceDisposition.Ambiguous, null);

    /// <summary>What the ladder concluded about this observation.</summary>
    public NewsObservationEvidenceDisposition Disposition { get; }

    /// <summary>The resolved pairing, or <c>null</c> when the observation did not join.</summary>
    public NewsObservationEvidenceMatch? Match { get; }

    /// <summary>Whether this observation resolved to exactly one evidence item of exactly one company.</summary>
    public bool IsJoined => Match is not null;

    /// <summary>Builds a JOINED outcome. The route must be one of the three joined dispositions.</summary>
    public static NewsObservationEvidenceResolution Joined(
        NewsObservationEvidenceDisposition route, NewsObservationEvidenceMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (route is not (NewsObservationEvidenceDisposition.ExactArticleInstant
            or NewsObservationEvidenceDisposition.ExactArticleUrl
            or NewsObservationEvidenceDisposition.UniqueHeadlineFallback))
        {
            throw new ArgumentOutOfRangeException(
                nameof(route), route, "A joined resolution must name one of the three joined routes.");
        }

        return new NewsObservationEvidenceResolution(route, match);
    }
}

/// <summary>
/// The derived-on-read join between a <see cref="NewsObservationRecord"/> and the
/// <see cref="EvidenceSourceType.NewsArticle"/> <see cref="EvidenceItem"/> that came from the same article
/// (spec 191 §1, strengthened by spec 197 §1.1).
/// <para>
/// <b>The two records DO share keys, and spec 197 corrected the claim that they do not.</b> The news
/// collector writes the same article URL onto the observation's
/// <see cref="NewsObservationRecord.GoogleLandingUrl"/> and the evidence's
/// <see cref="EvidenceItem.SourceUrl"/>, the same headline onto <see cref="NewsObservationRecord.Headline"/>
/// and <see cref="EvidenceItem.Title"/>, and the same publication instant onto both. Measured over the live
/// store before this ladder was built: <b>all 3,194 observations have an exact ordinal URL twin among the
/// 14,574 news evidence records (100 %), and every one of those evidence records carries a non-blank
/// <c>sourceUrl</c>, <c>publishedAt</c> and <c>title</c></b>. Tier 1 is therefore universally eligible
/// rather than a rarely-firing branch, which is what makes putting it first worthwhile.
/// </para>
/// <para>
/// <b>The ladder, in order, fail-closed at every step:</b>
/// <list type="number">
/// <item><b>Exact article instant</b> — non-blank ordinal-equal URL, non-blank equal normalized
/// headline/title, and both publication instants present and equal as INSTANTS;</item>
/// <item><b>Exact article URL</b> — non-blank ordinal-equal URL plus the same normalized headline/title,
/// used only when tier 1 found no candidate (an absent or restated timestamp on one side);</item>
/// <item><b>Unique-headline fallback</b> — the pre-197 rule, used only when neither stronger tier found a
/// candidate.</item>
/// </list>
/// Each tier requires <b>exactly one</b> evidence item and <b>exactly one distinct company</b>. A tier that
/// finds two or more of either makes the observation <see cref="NewsObservationEvidenceDisposition.Ambiguous"/>
/// and STOPS: <b>zero candidates may fall through; ambiguity may not</b>, because falling through to a
/// weaker key would make ambiguity disappear rather than resolve it. A blank URL or an absent instant
/// records no equality fact at all, so such an observation simply cannot enter that tier and falls through
/// to the next.
/// </para>
/// <para>
/// <b>Nothing is persisted.</b> This is a pure function of two stores already in memory, following spec
/// 151's recorded precedent that a derived-on-read function beats a materialized side index that can
/// silently drift, needs a regeneration step and has a staleness mode where the index wins. It is also
/// deliberately NOT fuzzy: no URL canonicalization, tracking-parameter stripping, redirect following,
/// casefolding or timestamp tolerance — every one of those widens IDENTITY and would need its own measured
/// evidence (spec 197 §6).
/// </para>
/// <para>
/// The headline key is <see cref="NewsTextNormalization.Normalize"/> applied to the observation's
/// <see cref="NewsObservationRecord.Headline"/> and to the evidence's <see cref="EvidenceItem.Title"/> —
/// the SAME primitive the fact layer's claim key uses (extracted, never copied).
/// </para>
/// <para>
/// The remaining fail-closed rules, unchanged by spec 197:
/// <list type="bullet">
/// <item>a blank normalized key never joins (no tier can be formed without it), and an observation with a
/// null <see cref="NewsObservationRecord.CompanyId"/> never joins — Radar cannot tell WHICH company such an
/// article belongs to, and guessing is the failure mode this exists to prevent. Both are no-match;</item>
/// <item>the same key claimed by two or more companies is ambiguous for ALL of them. This is what makes "a
/// same-headline article belonging to a DIFFERENT company never joins" true rather than merely likely, and
/// it holds at every tier — one company's verdict is never attached to a multi-company observation merely
/// because its URL is exact; and</item>
/// <item>when several observations share one joined key — the same article captured by two feeds or two
/// capture modes, which is expected and benign — the reported
/// <see cref="NewsObservationEvidenceMatch.ObservationId"/> is the LOWEST observation id by
/// <see cref="Guid.CompareTo(Guid)"/> ordinal ordering. Deterministic (AD-3): the choice never depends on
/// enumeration order or on the clock.</item>
/// </list>
/// </para>
/// </summary>
public sealed class NewsObservationEvidenceJoin
{
    private readonly Dictionary<Guid, NewsObservationEvidenceMatch> _byEvidenceId;
    private readonly Dictionary<Guid, NewsObservationEvidenceResolution> _byObservationId;

    private NewsObservationEvidenceJoin(
        Dictionary<Guid, NewsObservationEvidenceMatch> byEvidenceId,
        Dictionary<Guid, NewsObservationEvidenceResolution> byObservationId,
        NewsObservationEvidenceJoinCounts counts)
    {
        _byEvidenceId = byEvidenceId;
        _byObservationId = byObservationId;
        Counts = counts;
    }

    /// <summary>The per-run join measurement (spec 191 §1) — measured over OBSERVATIONS, not over evidence.</summary>
    public NewsObservationEvidenceJoinCounts Counts { get; }

    /// <summary>
    /// Builds the join. <paramref name="newsEvidence"/> must already be filtered to
    /// <see cref="EvidenceSourceType.NewsArticle"/> items — the caller owns that filter, because the
    /// AMBIGUITY rule counts evidence and a non-news item sharing a title would silently make a genuine
    /// single match look ambiguous.
    /// </summary>
    public static NewsObservationEvidenceJoin Build(
        IReadOnlyList<NewsObservationRecord> observations,
        IReadOnlyList<EvidenceItem> newsEvidence)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(newsEvidence);

        // The three tier indexes are built over ALL records independently of which tier each observation
        // ultimately resolves at. That is deliberate and load-bearing for the cross-company rule: an
        // observation that resolved at tier 1 must still COUNT as a company claiming the weaker tiers' keys,
        // otherwise a sibling observation could join a key its neighbour also claims.
        var exactInstant = new TierIndex<(string Url, string Title, DateTime InstantUtc)>();
        var exactUrl = new TierIndex<(string Url, string Title)>();
        var headline = new TierIndex<string>();

        foreach (var evidence in newsEvidence)
        {
            var title = NewsTextNormalization.Normalize(evidence.Title ?? string.Empty);
            if (title.Length == 0)
            {
                // No headline key ⇒ this evidence can never match at ANY tier (all three require it), so it
                // enters no index and cannot make a genuine single match look ambiguous.
                continue;
            }

            headline.AddEvidence(title, evidence.Id);

            // The URL bytes AS PERSISTED (spec 197 §6: no canonicalization, no trimming of the value used
            // for comparison — a blank value simply records no equality fact).
            var url = evidence.SourceUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            exactUrl.AddEvidence((url, title), evidence.Id);

            if (evidence.PublishedAtUtc is { } published)
            {
                // Compared as an INSTANT (UtcDateTime), never as the offset-bearing DateTimeOffset:
                // 2026-06-30T07:00:00+00:00 and 2026-06-30T09:00:00+02:00 are the SAME moment written two
                // ways, and DateTimeOffset equality already agrees — but the dictionary KEY must too, and a
                // tuple of DateTimeOffset would hash the offset as well as the moment.
                exactInstant.AddEvidence((url, title, published.UtcDateTime), evidence.Id);
            }
        }

        foreach (var observation in observations)
        {
            if (observation.CompanyId is not { } companyId)
            {
                continue;
            }

            var title = NewsTextNormalization.Normalize(observation.Headline ?? string.Empty);
            if (title.Length == 0)
            {
                continue;
            }

            headline.AddObservation(title, companyId, observation.ObservationId);

            var url = observation.GoogleLandingUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            exactUrl.AddObservation((url, title), companyId, observation.ObservationId);

            if (observation.PublishedAtUtc is { } published)
            {
                exactInstant.AddObservation(
                    (url, title, published.UtcDateTime), companyId, observation.ObservationId);
            }
        }

        var byObservationId = new Dictionary<Guid, NewsObservationEvidenceResolution>();
        var joinedRoutes = new HashSet<(int Rank, NewsObservationEvidenceMatch Match)>();
        var exactInstantCount = 0;
        var exactUrlCount = 0;
        var fallbackCount = 0;
        var ambiguous = 0;
        var noMatch = 0;

        foreach (var observation in observations)
        {
            var resolution = Resolve(observation, exactInstant, exactUrl, headline);

            switch (resolution.Disposition)
            {
                case NewsObservationEvidenceDisposition.ExactArticleInstant:
                    exactInstantCount++;
                    break;
                case NewsObservationEvidenceDisposition.ExactArticleUrl:
                    exactUrlCount++;
                    break;
                case NewsObservationEvidenceDisposition.UniqueHeadlineFallback:
                    fallbackCount++;
                    break;
                case NewsObservationEvidenceDisposition.Ambiguous:
                    ambiguous++;
                    break;
                default:
                    noMatch++;
                    break;
            }

            if (resolution.Match is { } match)
            {
                joinedRoutes.Add(((int)resolution.Disposition, match));
            }

            // SPEC 194 §1.2 — the REVERSE index, populated for EVERY observation, not only for the lowest-id
            // representative the forward match reports. A cited fact's source observation is whichever
            // observation the typing pass happened to type; if only the representative resolved, a citation
            // would fail provenance resolution for a reason that is an artifact of the representative rule
            // rather than a real gap in the evidence chain.
            //
            // Two supplied records sharing ONE observation id but disagreeing about their outcome fail
            // CLOSED to no-match, so the index cannot depend on which of them was enumerated first (AD-3).
            // The archive mints one record per id, so this is defence in depth rather than an expected path.
            byObservationId[observation.ObservationId] =
                byObservationId.TryGetValue(observation.ObservationId, out var existing)
                    && existing != resolution
                    ? NewsObservationEvidenceResolution.NoMatch
                    : resolution;
        }

        // The forward (evidence → observation) index. Built from the joined set in a fixed order —
        // strongest route first, then by evidence, company and representative observation id — and with
        // first-write-wins, so the same inputs in a different order produce the same index (AD-3). A
        // collision is possible only when two DIFFERENT keys of the same company resolve to one evidence
        // item (e.g. one capture carries the article URL and a sibling capture does not); the stronger
        // route's representative is then the one reported.
        var byEvidenceId = new Dictionary<Guid, NewsObservationEvidenceMatch>();
        foreach (var (_, match) in joinedRoutes
            .OrderBy(static entry => entry.Rank)
            .ThenBy(static entry => entry.Match.EvidenceId)
            .ThenBy(static entry => entry.Match.CompanyId)
            .ThenBy(static entry => entry.Match.ObservationId))
        {
            byEvidenceId.TryAdd(match.EvidenceId, match);
        }

        return new NewsObservationEvidenceJoin(
            byEvidenceId,
            byObservationId,
            new NewsObservationEvidenceJoinCounts(
                ExactArticleInstant: exactInstantCount,
                ExactArticleUrl: exactUrlCount,
                UniqueHeadlineFallback: fallbackCount,
                UnjoinedNoMatch: noMatch,
                UnjoinedAmbiguous: ambiguous));
    }

    /// <summary>The match for one evidence id, or <c>null</c> when that evidence did not join.</summary>
    public NewsObservationEvidenceMatch? TryMatch(Guid evidenceId) =>
        _byEvidenceId.GetValueOrDefault(evidenceId);

    /// <summary>
    /// SPEC 197 §1.2 — the REVERSE direction, typed: what the ladder concluded about one OBSERVATION.
    /// Total — an observation this join never saw resolves as
    /// <see cref="NewsObservationEvidenceDisposition.NoMatch"/>, which is the honest fail-closed answer
    /// (Radar has no evidence pairing for it) and never <c>null</c>.
    /// <para>
    /// The caller that must distinguish "Radar collected no evidence for this article" from "Radar
    /// deliberately refused an ambiguous identity" reads <see cref="NewsObservationEvidenceResolution.Disposition"/>;
    /// the pre-197 <see cref="TryMatchByObservation"/> shape is retained for callers that only need the
    /// match.
    /// </para>
    /// </summary>
    public NewsObservationEvidenceResolution Resolve(Guid observationId) =>
        _byObservationId.GetValueOrDefault(observationId, NewsObservationEvidenceResolution.NoMatch);

    /// <summary>
    /// SPEC 194 §1.2 — the REVERSE direction: the match for one OBSERVATION id, or <c>null</c> when that
    /// observation did not join. The judgment-signal materializer needs observation → evidence (it starts
    /// from the fact ids the judge CITED, which resolve to observations), whereas the forward lookup exists
    /// for the evidence → observation direction.
    /// <para>
    /// <b>Every</b> observation on a joined key resolves here, not just the lowest-id representative the
    /// forward match reports — a cited fact's own observation is whichever one the typing pass typed, and
    /// failing to resolve it because a SIBLING capture of the same article sorted lower would be a
    /// provenance gap invented by a tie-break rule.
    /// </para>
    /// <para>
    /// The returned instance is the very same <see cref="NewsObservationEvidenceMatch"/> the forward lookup
    /// hands out when the same key won there, so its
    /// <see cref="NewsObservationEvidenceMatch.ObservationId"/> is the KEY'S lowest-id representative and is
    /// NOT necessarily <paramref name="observationId"/>. That is deliberate: the representative rule is the
    /// join's one definition of "which observation stands for this article", and duplicating it into a
    /// second per-observation shape would be a second answer to the same question. Callers that need the
    /// observation they asked about already hold it.
    /// </para>
    /// </summary>
    public NewsObservationEvidenceMatch? TryMatchByObservation(Guid observationId) =>
        Resolve(observationId).Match;

    /// <summary>
    /// Walks the ladder for ONE observation. The <c>??</c> chain IS the precedence rule: a tier returns
    /// <c>null</c> only when it found ZERO candidates (fall through), and returns a resolution — joined or
    /// <see cref="NewsObservationEvidenceDisposition.Ambiguous"/> — the moment it found any, which stops the
    /// chain.
    /// </summary>
    private static NewsObservationEvidenceResolution Resolve(
        NewsObservationRecord observation,
        TierIndex<(string Url, string Title, DateTime InstantUtc)> exactInstant,
        TierIndex<(string Url, string Title)> exactUrl,
        TierIndex<string> headline)
    {
        if (observation.CompanyId is null)
        {
            return NewsObservationEvidenceResolution.NoMatch;
        }

        var title = NewsTextNormalization.Normalize(observation.Headline ?? string.Empty);
        if (title.Length == 0)
        {
            return NewsObservationEvidenceResolution.NoMatch;
        }

        var url = observation.GoogleLandingUrl;
        var hasUrl = !string.IsNullOrWhiteSpace(url);

        NewsObservationEvidenceResolution? resolved = null;

        if (hasUrl && observation.PublishedAtUtc is { } published)
        {
            resolved = exactInstant.Resolve(
                (url, title, published.UtcDateTime),
                NewsObservationEvidenceDisposition.ExactArticleInstant);
        }

        if (resolved is null && hasUrl)
        {
            resolved = exactUrl.Resolve(
                (url, title), NewsObservationEvidenceDisposition.ExactArticleUrl);
        }

        resolved ??= headline.Resolve(title, NewsObservationEvidenceDisposition.UniqueHeadlineFallback);

        return resolved ?? NewsObservationEvidenceResolution.NoMatch;
    }

    /// <summary>
    /// One rung of the ladder: the evidence carrying each key, the distinct companies claiming it, and the
    /// lowest observation id per key. Generic over the key so the three tiers share ONE implementation of
    /// the counting/ambiguity/representative rules rather than three copies that could drift.
    /// <para>
    /// The key is a value tuple of ordinally-compared strings (and, for tier 1, a UTC
    /// <see cref="DateTime"/>), which is injective by construction — unlike a delimiter-joined composite
    /// string, where a URL containing the delimiter could impersonate a different key.
    /// </para>
    /// </summary>
    private sealed class TierIndex<TKey>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, List<Guid>> _evidence = [];
        private readonly Dictionary<TKey, HashSet<Guid>> _companies = [];
        private readonly Dictionary<TKey, Guid> _lowestObservation = [];

        // One resolution instance per key, so every observation on a joined key hands back the SAME
        // NewsObservationEvidenceMatch the forward lookup holds — the representative rule keeps exactly one
        // definition and one object, instead of N structurally-equal copies of it.
        private readonly Dictionary<TKey, NewsObservationEvidenceResolution> _resolved = [];

        public void AddEvidence(TKey key, Guid evidenceId)
        {
            if (!_evidence.TryGetValue(key, out var ids))
            {
                ids = [];
                _evidence[key] = ids;
            }

            if (!ids.Contains(evidenceId))
            {
                ids.Add(evidenceId);
            }
        }

        public void AddObservation(TKey key, Guid companyId, Guid observationId)
        {
            if (!_companies.TryGetValue(key, out var companies))
            {
                companies = [];
                _companies[key] = companies;
            }

            companies.Add(companyId);

            if (!_lowestObservation.TryGetValue(key, out var current)
                || observationId.CompareTo(current) < 0)
            {
                _lowestObservation[key] = observationId;
            }
        }

        /// <summary>
        /// <c>null</c> ⇒ ZERO candidates, the only outcome the ladder may fall through on. Otherwise the
        /// terminal outcome for this observation: the joined match, or ambiguous.
        /// </summary>
        public NewsObservationEvidenceResolution? Resolve(
            TKey key, NewsObservationEvidenceDisposition route)
        {
            if (!_evidence.TryGetValue(key, out var evidenceIds) || evidenceIds.Count == 0)
            {
                return null;
            }

            if (_resolved.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // The observation being resolved added itself to this index, so both lookups always succeed.
            var companies = _companies[key];
            var resolution = evidenceIds.Count > 1 || companies.Count > 1
                ? NewsObservationEvidenceResolution.Ambiguous
                : NewsObservationEvidenceResolution.Joined(
                    route,
                    new NewsObservationEvidenceMatch(
                        EvidenceId: evidenceIds[0],
                        CompanyId: companies.Single(),
                        ObservationId: _lowestObservation[key]));

            _resolved[key] = resolution;
            return resolution;
        }
    }
}
