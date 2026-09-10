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
    int ReferencesSkippedSupersededPolicy = 0,
    // SPEC 220 §3: the RESOLVABLE families per comparison-basis class BEFORE the budget cut (their sum is
    // FamiliesAvailable). Supplied-by-basis is Families[i].ComparisonBasis and withheld-by-basis is the
    // class-wise difference, so neither is a second field that could disagree. The builder always sets it;
    // `null` exists only because a trailing optional record member cannot default to a non-null instance.
    NewsJudgmentBasisCounts? FamiliesAvailableByBasis = null,
    // SPEC 221 §1/§3: the NON-BUSINESS accounting of the same resolvable families (non-business = confined to
    // NewsJudgmentContextOnlyEventTypes): how many were available before the cut; how many families declared NO
    // event type (NOT demoted — "we cannot tell" is not "we can reject" — and counted so a stage-1 labelling gap
    // is visible); and how many non-business families the retained v2 order would have placed inside the
    // budget but v3 did not. The builder always sets all three; `null` exists only for the trailing-optional
    // shape and means NOT RECORDED, never a fabricated 0. Supplied non-business is not a field: it is derived
    // from Families (NewsJudgmentSuppliedBasisProfile.Of), so it can never disagree with them.
    int? FamiliesNonBusinessAvailable = null,
    int? FamiliesWithNoEventTypesAvailable = null,
    int? FamiliesNonBusinessDemotedBySelection = null);

/// <summary>
/// Deterministic judge-input assembly (spec 185 §1/§5). Pure — no clock, no I/O:
/// <list type="bullet">
/// <item>selects one company's families from one stage-1 cohort and joins each family's
/// <c>RepresentativeFactId</c> to its validated fact FIRST; a family whose representative cannot be
/// resolved is skipped and is not counted as resolvable (defensive — the representative is definitionally
/// a member fact);</item>
/// <item>(spec 220 §1, <see cref="NewsJudgmentFamilyOrdering"/>) classifies each resolvable representative
/// ONCE with the existing <see cref="StatementComparisonClassifier"/> and orders by CLASS rank — since spec
/// 221 §1 (<c>family-ordering-v3</c>) business <c>StatedComparison</c> = <c>Event</c>, then business
/// <c>LevelOnly</c>, then business <c>NotQuantified</c>, then every NON-BUSINESS family (confined to
/// <see cref="NewsJudgmentContextOnlyEventTypes"/>) whatever its basis — then <c>MemberCount</c> descending,
/// then (spec 219 §2) <c>DistinctPublisherCount</c> descending, then <c>FamilyId</c> ascending (AD-3), so the
/// cap below is stable. <b>Superseded twice:</b> until spec 220 the primary key was <c>MemberCount</c>
/// (syndication volume), which filled a bounded read with boilerplate; under spec 220's v2 a share-price move
/// ranked with the revenue comparisons because its WORDING carries a comparison. Within one class the order
/// is unchanged;</item>
/// <item>caps at <c>maxFamiliesPerJudgment</c> (spec 219 §2: the BREADTH cohort passes its own, much
/// smaller, <c>MaxFamiliesPerBreadthJudgment</c> here — the ordering and the cap mechanism are the same
/// code, only the bound differs); a cap that removed families makes the bundle
/// <see cref="NewsJudgmentFamilyBundle.Capped"/> — recorded, never silent, and the remainder is counted on
/// <see cref="NewsJudgmentInputBundle.FamiliesAvailable"/> and, per basis class, on
/// <see cref="NewsJudgmentInputBundle.FamiliesAvailableByBasis"/>;</item>
/// <item>hashes the ORDERED supplied family set (<see cref="ComputeFamilySetHash(IReadOnlyList{NewsJudgmentInputFamily}, IReadOnlyList{NewsJudgmentReferenceValue})"/>) — the per-judgment
/// cache identity input, modelled on the spec-179 input-bundle hash;</item>
/// <item>(spec 215 §2) projects the company's reported-metrics ledger through
/// <see cref="ReferenceValueProjector"/> against the SUPPLIED statements, after the cap, so a reference
/// value is offered only for a metric the judge will actually see named.</item>
/// </list>
/// </summary>
public static class NewsJudgmentInputBuilder
{
    /// <remarks>
    /// <b>SPEC 220 §1 — the basis-first order moves the SUPPLIED SET for any bounded read whose
    /// most-syndicated families were not its most directional, and that is correct</b>: a different supplied
    /// set is a different judge input and earns a fresh judgment, never a reused verdict made over other
    /// facts. The <c>ordering=</c> segment of the cohort key forks every judgment regardless, so no pre-220
    /// verdict is reused under v2. The classification is computed ONCE per family here and reused for the
    /// supplied <see cref="NewsJudgmentInputFamily.ComparisonBasis"/> line, so what orders a family and what
    /// the judge is told about it can never disagree.
    /// <para>
    /// <b>SPEC 219 §2 — the DistinctPublisherCount tie-break (now the third key, within a basis class) can
    /// move an accrued family-set hash, and that is correct.</b> It refines an order that was already total on <c>FamilyId</c>, so nothing becomes
    /// non-deterministic; but where two families tie on <c>MemberCount</c> AND the cap bites between them,
    /// the SUPPLIED SET can differ from what the pre-219 order would have supplied. A different supplied set
    /// is a different judge input, so it hashes differently and earns a fresh judgment — a RE-JUDGMENT, not
    /// a silent reuse of a verdict made over other facts. The family-set hash already covers this by
    /// construction: nothing new is folded into it, and a run whose supplied set is unchanged reuses its
    /// cached verdict exactly as before.
    /// </remarks>
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

        // Spec 220 §1: resolve FIRST, then classify each resolvable representative ONCE — the basis is both the
        // primary ordering key and the line the judge is shown, so it must be one computation, not two.
        var resolved = new List<(FactFamilyRecord Family, NewsTypingFactRef Fact, NewsFactComparisonBasis Basis)>();
        foreach (var family in cohortFamilies.Where(f => f.CompanyId == companyId))
        {
            if (!factsById.TryGetValue(family.RepresentativeFactId, out var fact))
            {
                // Defensive: the representative is by construction a member fact of this cohort's window.
                // An unresolvable one is dropped rather than invented; it never reaches the judge and is not
                // counted as resolvable.
                continue;
            }

            resolved.Add((
                family,
                fact,
                StatementComparisonClassifier.Classify(fact.Fact.Statement, fact.Fact.EventTypes)));
        }

        var ordered = resolved
            // Spec 221 §1 (family-ordering-v3): the business class that can carry a direction fills the budget
            // first and a NON-BUSINESS family (confined to the context-only event types) goes last, whatever
            // its basis. Everything after this key is the pre-220 order, byte-identical within a class.
            .OrderBy(r => NewsJudgmentFamilyOrdering.ClassRank(r.Basis, r.Fact.Fact.EventTypes))
            .ThenByDescending(r => r.Family.MemberCount)
            // Spec 219 §2: DistinctPublisherCount is the next key, so the cap's choice among equally
            // corroborated families is total and reproducible rather than settled by an id.
            .ThenByDescending(r => r.Family.DistinctPublisherCount)
            .ThenBy(r => r.Family.FamilyId)
            .ToList();

        var resolvable = ordered.Count;

        // Spec 221 §3: what the v3 demotion actually did to THIS read — the non-business families the retained
        // family-ordering-v2 key (basis rank, then the same tie-breaks) would have placed inside the budget and
        // v3 did not. Counted, never inferred later: it is the only measure of the demotion's bite.
        var suppliedUnderV3 = ordered
            .Take(maxFamiliesPerJudgment)
            .Select(r => r.Family.FamilyId)
            .ToHashSet();
        var nonBusinessDemotedBySelection = resolved
            .OrderBy(r => NewsJudgmentFamilyOrdering.BasisRank(r.Basis))
            .ThenByDescending(r => r.Family.MemberCount)
            .ThenByDescending(r => r.Family.DistinctPublisherCount)
            .ThenBy(r => r.Family.FamilyId)
            .Take(maxFamiliesPerJudgment)
            .Count(r => NewsJudgmentFamilyOrdering.IsNonBusiness(r.Fact.Fact.EventTypes)
                && !suppliedUnderV3.Contains(r.Family.FamilyId));
        var supplied = new List<NewsJudgmentInputFamily>(Math.Min(resolvable, maxFamiliesPerJudgment));
        foreach (var (family, fact, basis) in ordered)
        {
            if (supplied.Count >= maxFamiliesPerJudgment)
            {
                break; // the remainder is counted: `resolvable` and the per-basis breakdown cover every family
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
                // Spec 214 §1: the classifier's read of exactly the statement and event types the judge sees —
                // since spec 220 §1 computed ONCE above (before ordering) and reused, never re-classified.
                ComparisonBasis: basis,
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
            ReferencesSkippedSupersededPolicy: projection.ReferencesSkippedSupersededPolicy,
            // Spec 220 §3: EVERY resolvable family, before the cut, per basis class — so what the budget
            // withheld can be stated per class rather than as one undifferentiated number.
            FamiliesAvailableByBasis: NewsJudgmentBasisCounts.Of(ordered.Select(r => r.Basis)),
            // Spec 221 §3: the non-business and untyped accounting of the same resolvable families.
            FamiliesNonBusinessAvailable: ordered.Count(r => NewsJudgmentFamilyOrdering.IsNonBusiness(r.Fact.Fact.EventTypes)),
            FamiliesWithNoEventTypesAvailable: ordered.Count(r => r.Fact.Fact.EventTypes.Count == 0),
            FamiliesNonBusinessDemotedBySelection: nonBusinessDemotedBySelection);
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
