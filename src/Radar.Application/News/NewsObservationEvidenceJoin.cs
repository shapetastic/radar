using Radar.Application.NewsTyping;
using Radar.Domain.Evidence;

namespace Radar.Application.News;

/// <summary>
/// How one pass's observations were disposed of by the join (spec 191 §1's per-run measurement). Every
/// supplied observation contributes to EXACTLY ONE bucket, so <c>Joined + UnjoinedNoMatch +
/// UnjoinedAmbiguous</c> equals the observation count.
/// </summary>
/// <param name="Joined">Observations whose normalized headline resolved to exactly one news evidence item and one company.</param>
/// <param name="UnjoinedNoMatch">Observations with no matching evidence — including a blank key or a null company id.</param>
/// <param name="UnjoinedAmbiguous">Observations whose key matched two or more evidence items, or whose key is claimed by two or more companies.</param>
public sealed record NewsObservationEvidenceJoinCounts(
    int Joined, int UnjoinedNoMatch, int UnjoinedAmbiguous);

/// <summary>One resolved evidence ↔ observation pairing.</summary>
public sealed record NewsObservationEvidenceMatch(Guid EvidenceId, Guid CompanyId, Guid ObservationId);

/// <summary>
/// The derived-on-read join between a <see cref="NewsObservationRecord"/> and the
/// <see cref="EvidenceSourceType.NewsArticle"/> <see cref="EvidenceItem"/> that came from the same article
/// (spec 191 §1). The two records share no key: an observation carries <c>companyId</c>/<c>headline</c>
/// while spec 145 made evidence identity the normalized <b>title+body</b> hash, so a title-only join is a
/// heuristic — and is therefore FAIL-CLOSED at every step.
/// <para>
/// <b>Nothing is persisted.</b> This is a pure function of two stores already in memory, following spec
/// 151's recorded precedent that a derived-on-read function beats a materialized side index that can
/// silently drift, needs a regeneration step and has a staleness mode where the index wins.
/// </para>
/// <para>
/// The key is <see cref="NewsTextNormalization.Normalize"/> applied to the observation's
/// <see cref="NewsObservationRecord.Headline"/> and to the evidence's <see cref="EvidenceItem.Title"/> —
/// the SAME primitive the fact layer's claim key uses (extracted, never copied). In production those two
/// strings are the same source value: the news collector maps one article's title into both.
/// </para>
/// <para>
/// The rules, all fail-closed:
/// <list type="bullet">
/// <item>a blank normalized key never joins, and an observation with a null
/// <see cref="NewsObservationRecord.CompanyId"/> never joins (both count as no-match — Radar cannot tell
/// WHICH company such an article belongs to, and guessing is the failure mode this exists to prevent);</item>
/// <item>a key joins only when <b>exactly one</b> news evidence item carries it. Zero ⇒ no-match; two or
/// more ⇒ ambiguous, never a guess (an ambiguous join would attach one article's direction to another
/// article's evidence);</item>
/// <item>a key joins only when <b>exactly one distinct company</b> claims it among the observations. Two or
/// more ⇒ ambiguous. This is the spec's "a candidate matches only within the same company" rule made
/// fail-closed, and it is what makes "a same-headline article belonging to a DIFFERENT company never
/// joins" true rather than merely likely; and</item>
/// <item>when several observations share one joined key — the same article captured by two feeds or two
/// capture modes, which is expected and benign — the reported <see cref="NewsObservationEvidenceMatch.ObservationId"/>
/// is the LOWEST observation id by <see cref="Guid.CompareTo(Guid)"/> ordinal ordering. Deterministic
/// (AD-3): the choice never depends on enumeration order or on the clock.</item>
/// </list>
/// </para>
/// </summary>
public sealed class NewsObservationEvidenceJoin
{
    private readonly Dictionary<Guid, NewsObservationEvidenceMatch> _byEvidenceId;

    private NewsObservationEvidenceJoin(
        Dictionary<Guid, NewsObservationEvidenceMatch> byEvidenceId,
        NewsObservationEvidenceJoinCounts counts)
    {
        _byEvidenceId = byEvidenceId;
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

        // Evidence side: normalized title -> the ids carrying it. A key held by 2+ items is ambiguous.
        var evidenceByKey = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var evidence in newsEvidence)
        {
            var key = NewsTextNormalization.Normalize(evidence.Title ?? string.Empty);
            if (key.Length == 0)
            {
                continue;
            }

            if (!evidenceByKey.TryGetValue(key, out var ids))
            {
                ids = [];
                evidenceByKey[key] = ids;
            }

            ids.Add(evidence.Id);
        }

        // Observation side: normalized headline -> the distinct companies claiming it, and the lowest
        // observation id per (key, company). A key claimed by 2+ companies is ambiguous for ALL of them.
        var companiesByKey = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var lowestObservationByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (observation.CompanyId is not { } companyId)
            {
                continue;
            }

            var key = NewsTextNormalization.Normalize(observation.Headline ?? string.Empty);
            if (key.Length == 0)
            {
                continue;
            }

            if (!companiesByKey.TryGetValue(key, out var companies))
            {
                companies = [];
                companiesByKey[key] = companies;
            }

            companies.Add(companyId);

            if (!lowestObservationByKey.TryGetValue(key, out var current)
                || observation.ObservationId.CompareTo(current) < 0)
            {
                lowestObservationByKey[key] = observation.ObservationId;
            }
        }

        var byEvidenceId = new Dictionary<Guid, NewsObservationEvidenceMatch>();
        var joinedKeys = new HashSet<string>(StringComparer.Ordinal);
        var ambiguousKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, companies) in companiesByKey)
        {
            if (!evidenceByKey.TryGetValue(key, out var evidenceIds) || evidenceIds.Count == 0)
            {
                continue;   // no-match
            }

            if (evidenceIds.Count > 1 || companies.Count > 1)
            {
                ambiguousKeys.Add(key);
                continue;
            }

            joinedKeys.Add(key);
            byEvidenceId[evidenceIds[0]] = new NewsObservationEvidenceMatch(
                EvidenceId: evidenceIds[0],
                CompanyId: companies.Single(),
                ObservationId: lowestObservationByKey[key]);
        }

        // Counts are per OBSERVATION (the spec's pilot counted observations): each supplied observation
        // falls in exactly one bucket, and a blank-key / null-company observation is a no-match.
        var joined = 0;
        var ambiguous = 0;
        var noMatch = 0;
        foreach (var observation in observations)
        {
            var key = observation.CompanyId is null
                ? string.Empty
                : NewsTextNormalization.Normalize(observation.Headline ?? string.Empty);

            if (key.Length > 0 && joinedKeys.Contains(key))
            {
                joined++;
            }
            else if (key.Length > 0 && ambiguousKeys.Contains(key))
            {
                ambiguous++;
            }
            else
            {
                noMatch++;
            }
        }

        return new NewsObservationEvidenceJoin(
            byEvidenceId,
            new NewsObservationEvidenceJoinCounts(joined, noMatch, ambiguous));
    }

    /// <summary>The match for one evidence id, or <c>null</c> when that evidence did not join.</summary>
    public NewsObservationEvidenceMatch? TryMatch(Guid evidenceId) =>
        _byEvidenceId.GetValueOrDefault(evidenceId);
}
