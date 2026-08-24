using System.Text;

using Radar.Application.Identity;
using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// One canonical fact family as the judge consumes it (spec 185 §1): the representative fact's typed
/// content (event types, preserved statement, temporal scope, attribution, assertion status, confidence,
/// verbatim citations) plus family-size metadata. <see cref="MemberCount"/> and
/// <see cref="DistinctPublisherCount"/> are corroboration of REPORTING — however many syndicated copies
/// asserted the claim, it reaches the judge as ONE supplied fact (the 40-outlets problem must not be reborn
/// at the judgment seam). Deliberately NO raw article text, headline, score, rank, label or price member.
/// </summary>
public sealed record NewsJudgmentInputFamily(
    Guid FamilyId,
    Guid RepresentativeFactId,
    IReadOnlyList<NewsEventType> EventTypes,
    string Statement,
    string? TemporalScope,
    NewsFactAttribution Attribution,
    NewsFactAssertionStatus AssertionStatus,
    double Confidence,
    IReadOnlyList<string> Citations,
    int MemberCount,
    int DistinctPublisherCount);

/// <summary>One assembled judgment input: the ordered supplied families, the family-bundle completeness and the available count.</summary>
public sealed record NewsJudgmentInputBundle(
    IReadOnlyList<NewsJudgmentInputFamily> Families,
    NewsJudgmentFamilyBundle FamilyBundle,
    int FamiliesAvailable,
    string FamilySetHash);

/// <summary>
/// Deterministic judge-input assembly (spec 185 §1/§5). Pure — no clock, no I/O:
/// <list type="bullet">
/// <item>selects one company's families from one stage-1 cohort, ordered deterministically by
/// <c>MemberCount</c> descending then <c>FamilyId</c> ascending (AD-3), so the cap below is stable;</item>
/// <item>caps at <c>maxFamiliesPerJudgment</c>; a cap that removed families makes the bundle
/// <see cref="NewsJudgmentFamilyBundle.Capped"/> — recorded, never silent;</item>
/// <item>joins each family's <c>RepresentativeFactId</c> to its validated fact; a family whose
/// representative cannot be resolved is skipped and counted by the caller's logging (defensive — the
/// representative is definitionally a member fact);</item>
/// <item>hashes the ORDERED supplied family set (<see cref="ComputeFamilySetHash"/>) — the per-judgment
/// cache identity input, modelled on the spec-179 input-bundle hash.</item>
/// </list>
/// </summary>
public static class NewsJudgmentInputBuilder
{
    public static NewsJudgmentInputBundle Build(
        Guid companyId,
        IReadOnlyList<FactFamilyRecord> cohortFamilies,
        IReadOnlyDictionary<Guid, NewsTypingFactRef> factsById,
        int maxFamiliesPerJudgment)
    {
        ArgumentNullException.ThrowIfNull(cohortFamilies);
        ArgumentNullException.ThrowIfNull(factsById);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFamiliesPerJudgment, 1);

        var ordered = cohortFamilies
            .Where(f => f.CompanyId == companyId)
            .OrderByDescending(f => f.MemberCount)
            .ThenBy(f => f.FamilyId)
            .ToList();

        var supplied = new List<NewsJudgmentInputFamily>(Math.Min(ordered.Count, maxFamiliesPerJudgment));
        var resolvable = 0;
        foreach (var family in ordered)
        {
            if (!factsById.TryGetValue(family.RepresentativeFactId, out var fact))
            {
                // Defensive: the representative is by construction a member fact of this cohort's window.
                // An unresolvable one is dropped rather than invented; it never reaches the judge.
                continue;
            }

            resolvable++;
            if (supplied.Count >= maxFamiliesPerJudgment)
            {
                continue; // keep counting resolvable families so the Capped dimension is honest
            }

            supplied.Add(new NewsJudgmentInputFamily(
                FamilyId: family.FamilyId,
                RepresentativeFactId: family.RepresentativeFactId,
                EventTypes: fact.Fact.EventTypes,
                Statement: fact.Fact.Statement,
                TemporalScope: fact.Fact.TemporalScope,
                Attribution: fact.Fact.Attribution,
                AssertionStatus: fact.Fact.AssertionStatus,
                Confidence: fact.Fact.Confidence,
                Citations: fact.Fact.Citations,
                MemberCount: family.MemberCount,
                DistinctPublisherCount: family.DistinctPublisherCount));
        }

        return new NewsJudgmentInputBundle(
            Families: supplied,
            FamilyBundle: resolvable > supplied.Count
                ? NewsJudgmentFamilyBundle.Capped
                : NewsJudgmentFamilyBundle.Complete,
            FamiliesAvailable: resolvable,
            FamilySetHash: ComputeFamilySetHash(supplied));
    }

    /// <summary>
    /// The ordered family-set hash the judgment cache keys on (spec 185 §3): SHA-256 over each supplied
    /// family's identity, representative fact, typed content (citations included) and size metadata — so a
    /// changed statement, an edited citation, a grown family, a re-typed representative or a reordering is a
    /// different cache entry, never a silent reuse.
    /// </summary>
    public static string ComputeFamilySetHash(IReadOnlyList<NewsJudgmentInputFamily> families)
    {
        ArgumentNullException.ThrowIfNull(families);

        var canonical = new StringBuilder("radar:news-judgment-families:");
        foreach (var family in families)
        {
            canonical
                .Append(family.FamilyId.ToString("D"))
                .Append('|')
                .Append(family.RepresentativeFactId.ToString("D"))
                .Append('|')
                .Append(string.Join(',', family.EventTypes))
                .Append('|')
                .Append(family.Statement)
                .Append('|')
                .Append(family.TemporalScope ?? string.Empty)
                .Append('|')
                .Append(family.Attribution)
                .Append('|')
                .Append(family.AssertionStatus)
                .Append('|')
                .Append(family.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append('|')
                // Citations are free text; the count prefix + unit-separator join keeps boundaries
                // unambiguous however the citation strings themselves are shaped.
                .Append(family.Citations.Count)
                .Append('|')
                .Append(string.Join('\u001f', family.Citations))
                .Append('|')
                .Append(family.MemberCount)
                .Append('|')
                .Append(family.DistinctPublisherCount)
                .Append(';');
        }

        return CanonicalHash.Sha256Hex(canonical);
    }
}
