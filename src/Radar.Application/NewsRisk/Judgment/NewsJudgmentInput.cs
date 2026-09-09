using System.Text;

using Radar.Application.Filings;
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
/// <para>
/// <see cref="ComparisonBasis"/> (spec 214 §1) is the deterministic <see cref="StatementComparisonClassifier"/>
/// read of <see cref="Statement"/> + <see cref="EventTypes"/>, computed at judge-INPUT time (never at
/// typing time, so the stage-1 cohort is untouched). It is rendered to the judge as one line per family
/// and persisted per consumed family on the judgment record.
/// </para>
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
    int DistinctPublisherCount,
    NewsFactComparisonBasis ComparisonBasis,
    // SPEC 216 §1: the family's EarliestObservedAtUtc — when this claim was first observed in the news.
    // It is NOT rendered to the judge and is NOT the eligibility test (the structural ledger-order rule
    // is); it is the secondary guard's input, so a reference filed AFTER every fact naming its metric is
    // never compared backwards against them, and so the reconciliation "which reference could this fact
    // have seen" is answerable from the persisted record. TRAILING and NULLABLE: `null` means NOT
    // RECORDED and can exclude nothing — never a fabricated instant.
    DateTimeOffset? ObservedAtUtc = null);

/// <summary>
/// One assembled judgment input: the ordered supplied families, the family-bundle completeness, the
/// available count, the family-set hash, and (spec 215 §2) the ordered company-reported reference values
/// projected from the ledger for the metrics the supplied statements name, with the COUNTED remainder the
/// projection caps left out. <see cref="References"/> is empty — never null — when the ledger holds
/// nothing the statements name.
/// </summary>
public sealed record NewsJudgmentInputBundle(
    IReadOnlyList<NewsJudgmentInputFamily> Families,
    NewsJudgmentFamilyBundle FamilyBundle,
    int FamiliesAvailable,
    string FamilySetHash,
    IReadOnlyList<NewsJudgmentReferenceValue> References,
    int ReferenceValuesOmitted,
    // SPEC 216 §1/§5: the projection's counted exclusions, carried onto the judgment record so a judgment
    // handed NO references can say WHY (a young ledger whose only accession is the current one, a filing
    // later than the news, a superseded-policy file) rather than being indistinguishable from a company
    // with no ledger at all.
    int ReferencesExcludedNewest = 0,
    int ReferencesExcludedLaterThanFact = 0,
    int ReferencesSkippedSupersededPolicy = 0);

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
/// <item>hashes the ORDERED supplied family set (<see cref="ComputeFamilySetHash(IReadOnlyList{NewsJudgmentInputFamily}, IReadOnlyList{NewsJudgmentReferenceValue})"/>) — the per-judgment
/// cache identity input, modelled on the spec-179 input-bundle hash;</item>
/// <item>(spec 215 §2) projects the company's reported-metrics ledger through
/// <see cref="ReferenceValueProjector"/> against the SUPPLIED statements, after the cap, so a reference
/// value is offered only for a metric the judge will actually see named.</item>
/// </list>
/// </summary>
public static class NewsJudgmentInputBuilder
{
    /// <param name="reportedMetrics">
    /// The company's reported-metrics ledger (spec 215 §2), or null/empty when none is registered or none
    /// is accrued — both project zero references and leave every family-set hash byte-identical.
    /// </param>
    public static NewsJudgmentInputBundle Build(
        Guid companyId,
        IReadOnlyList<FactFamilyRecord> cohortFamilies,
        IReadOnlyDictionary<Guid, NewsTypingFactRef> factsById,
        int maxFamiliesPerJudgment,
        IReadOnlyList<ReportedMetricRecord>? reportedMetrics = null)
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
                DistinctPublisherCount: family.DistinctPublisherCount,
                // Spec 214 §1: classified HERE, from exactly the statement and event types the judge sees.
                ComparisonBasis: StatementComparisonClassifier.Classify(
                    fact.Fact.Statement, fact.Fact.EventTypes),
                // Spec 216 §1: threaded from the family record, never re-derived.
                ObservedAtUtc: family.EarliestObservedAtUtc));
        }

        var projection = ReferenceValueProjector.Project(supplied, reportedMetrics ?? []);

        return new NewsJudgmentInputBundle(
            Families: supplied,
            FamilyBundle: resolvable > supplied.Count
                ? NewsJudgmentFamilyBundle.Capped
                : NewsJudgmentFamilyBundle.Complete,
            FamiliesAvailable: resolvable,
            FamilySetHash: ComputeFamilySetHash(supplied, projection.References),
            References: projection.References,
            ReferenceValuesOmitted: projection.ReferenceValuesOmitted,
            ReferencesExcludedNewest: projection.ReferencesExcludedNewest,
            ReferencesExcludedLaterThanFact: projection.ReferencesExcludedLaterThanFact,
            ReferencesSkippedSupersededPolicy: projection.ReferencesSkippedSupersededPolicy);
    }

    /// <summary>
    /// The ordered family-set hash the judgment cache keys on (spec 185 §3): SHA-256 over each supplied
    /// family's identity, representative fact, typed content (citations included) and size metadata — so a
    /// changed statement, an edited citation, a grown family, a re-typed representative or a reordering is a
    /// different cache entry, never a silent reuse.
    /// <para>
    /// Spec 214 deliberately does NOT fold <see cref="NewsJudgmentInputFamily.ComparisonBasis"/> in: it is
    /// a pure function of <c>Statement</c> + <c>EventTypes</c>, both already hashed, so adding it would
    /// change no distinctness and only move every accrued family-set hash. The classifier VERSION forks
    /// the cohort key instead (<see cref="NewsJudgmentContract.CohortKey"/>), which is where a table change
    /// belongs.
    /// </para>
    /// <para>
    /// <b>Spec 216 does NOT fold <see cref="NewsJudgmentInputFamily.ObservedAtUtc"/> in either</b>, and the
    /// test is the same one: the judge never sees it, and everything it can change — WHICH references
    /// project — is already hashed through the projected reference ids below. Folding it would move every
    /// accrued family-set hash for no gain in distinctness.
    /// </para>
    /// <para>
    /// <b>Spec 215 §2 DOES fold the projected reference ids in — and only when there are any.</b> The
    /// distinction from ComparisonBasis is exact: a reference value is EXTERNAL input the model sees, not
    /// a function of the already-hashed families, so the same families beside a grown ledger are a
    /// different judge input and must be a different cache entry (a re-judgment), never a silent reuse of
    /// a verdict made without the references. The <c>|refs:</c> segment is appended ONLY when the projected
    /// list is non-empty, so every accrued hash and every reference-free judgment is byte-identical to
    /// before (asserted by test) — the ledger is heal-forward, and until a company's first release is read
    /// nothing about its judgments moves.
    /// </para>
    /// </summary>
    public static string ComputeFamilySetHash(IReadOnlyList<NewsJudgmentInputFamily> families) =>
        ComputeFamilySetHash(families, []);

    /// <inheritdoc cref="ComputeFamilySetHash(IReadOnlyList{NewsJudgmentInputFamily})"/>
    public static string ComputeFamilySetHash(
        IReadOnlyList<NewsJudgmentInputFamily> families,
        IReadOnlyList<NewsJudgmentReferenceValue> references)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(references);

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

        if (references.Count > 0)
        {
            canonical.Append("|refs:");
            foreach (var reference in references)
            {
                canonical.Append(reference.ReferenceId.ToString("D")).Append(';');
            }
        }

        return CanonicalHash.Sha256Hex(canonical);
    }
}
