using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Fda;
using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.Hiring;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Patents;
using Radar.Infrastructure.Rss;
using Radar.Infrastructure.Sec;
using Radar.Infrastructure.Sources;
using Radar.Infrastructure.Trademarks;
using Radar.Infrastructure.UsaSpending;

namespace Radar.Infrastructure.Collectors;

/// <summary>
/// The ONE table that re-derives which collector retrieved a piece of LEGACY evidence — evidence collected
/// before spec 146 began recording the producing collector (spec 151).
///
/// <para><b>WHY THIS IS RECOVERY, NOT FABRICATION.</b> The attribution was deterministic at collection time
/// and simply was not persisted. Each collector writes an exclusive metadata key from its own mapper, so the
/// producing collector is still recoverable from fields every accrued record carries. That is categorically
/// different from synthesising evidence, which the standing rule (and spec 145) forbids: nothing new is
/// created, no text is invented, no score input is manufactured. But it IS an inference, and it is never
/// allowed to look like a recorded fact — see <see cref="CollectorAttribution"/>.</para>
///
/// <para><b>WHY IT LIVES IN INFRASTRUCTURE.</b> The table maps to collector NAMES, and the one authoritative
/// list of those is <see cref="RadarCollectorNames"/> (spec 147), which re-exports each collector class's own
/// <c>Name</c> const. Application cannot reference Infrastructure, and hand-rolling a second name list here
/// would be exactly the duplication CLAUDE.md's reuse-over-copy rule forbids: the two copies would drift and
/// only one would get the next fix. Same reasoning for the metadata MARKER keys — each is the collector's own
/// <c>MetadataMarkerKey</c> const, referenced rather than re-typed, so renaming a key in the collector
/// renames it here by construction.</para>
///
/// <para><b>THE MARKER RULE, AND WHY IT IS NOT THE OBVIOUS RULE.</b> The obvious discriminator is
/// <see cref="EvidenceItem.SourceType"/>, and for most collectors it is nearly sufficient. It is NOT
/// sufficient, and pre-flight validation over the live store (6,388 raw evidence files, 2026-07-27) proved
/// it twice:
/// <list type="number">
/// <item><c>NewsArticle</c> is emitted by BOTH <c>newssearch</c> (3,360 records) and the GDELT <c>news</c>
/// collector (5 records). A <c>SourceType ⇒ newssearch</c> rule would have MISATTRIBUTED those five to the
/// wrong collector — silently, and with no way for a reader to notice. The marker rule gets them right; the
/// regression is pinned by a test.</item>
/// <item><c>Filing</c> is emitted by THREE collectors (<c>sec-edgar</c>, <c>sec-form4</c>,
/// <c>sec-13dg</c>), 2,683 records between them.</item>
/// </list>
/// Two candidate discriminators were rejected on evidence. <c>metadata.secFeedUrl</c> does not work: all
/// three SEC collectors write <c>feed.Url</c>, which is the same <c>data.sec.gov/submissions/CIK*.json</c>
/// shape for each. <c>metadata.form</c> works empirically but is CONFIG-DEPENDENT — it discriminates only
/// because <c>Radar:Sec:Forms</c> happens to exclude Form 4 and 13D/G today, so a future operator adding
/// <c>"4"</c> to that list would silently corrupt every historical inference. The marker keys used here come
/// from each collector's own mapper and depend on no configuration at all.</para>
///
/// <para><b>VALIDATED vs MERELY REASONED — stated explicitly, because the split matters.</b> The inference
/// was run over the 341 live records that DO carry recorded attribution, ignoring their recorded value:
/// <b>341/341 agree, zero disagreements, and zero of the 6,388 files are ambiguous</b>. But those 341 are a
/// small, skewed sample:
/// <list type="bullet">
/// <item><b>Genuinely ground-truth validated:</b> <c>newssearch</c> (337 records), <c>sec-form4</c> (2),
/// <c>RssPressReleaseCollector</c> (2).</item>
/// <item><b>Only REASONED</b> (no recorded exemplar exists): <c>sec-edgar</c> (1,160 records),
/// <c>sec-13dg</c> (850), <c>usaspending</c> (21), the GDELT <c>news</c> collector (5), and the five
/// collectors with zero accrued records (<c>fda</c>, <c>hiring-ats</c>, <c>patents</c>, <c>trademarks</c>,
/// <c>LocalFileEvidenceCollector</c>). <c>sec-form4</c> and <c>sec-13dg</c> are additionally corroborated by
/// their marker keys appearing on 100% of their live records.</item>
/// </list>
/// That is uncomfortable precisely where it matters: a <c>filings-led</c> strategy's channels are
/// <c>sec-form4</c> and <c>sec-13dg</c>, so the least-validated mappings carry the most weight in the
/// experiment this recovery exists to enable. Hence the marker rule rather than the form rule, and hence the
/// per-channel inferred/recorded counts in the v9 breakdown.</para>
///
/// <para><b>NOTHING IS PERSISTED AND NO EVIDENCE FILE IS REWRITTEN — and a side index was considered and
/// rejected.</b> Spec 151 §3 suggested a side index keyed by <c>contentHash</c> as the way to avoid mutating
/// 6k historical files. Avoiding the mutation is right; the index is not the way to get it. Attribution here
/// is a PURE FUNCTION of <see cref="EvidenceItem.SourceType"/> and the evidence's own metadata bag — fields
/// already materialised in memory at scoring time, on the very object being scored. A side index would
/// therefore be a materialized cache of a function whose inputs are already in hand: it adds a file to keep
/// in sync with an append-only store, a regeneration step whenever the store grows or the table is corrected,
/// and a staleness mode in which the index and the evidence disagree and the index silently wins. Deriving on
/// read has none of those: it persists no new state, cannot drift from the store because it IS the store's
/// content, is reversible by deleting this class, and needs no backfill or migration. It satisfies AD-8
/// (append-only) and AD-1 more strongly than an index would — the accrued 6,047 files are read and never
/// touched. The cost is recomputation per scored signal, which is a dictionary lookup over an already-parsed
/// metadata bag.</para>
///
/// <para>Pure and deterministic (AD-3): no clock, no randomness, no I/O, no configuration.</para>
/// </summary>
internal static class LegacyCollectorAttributionInference
{
    /// <summary>
    /// One collector's exclusive metadata marker: if this key is present (with a non-blank value) on evidence
    /// of the owning <see cref="EvidenceSourceType"/>, that collector produced it.
    /// </summary>
    private sealed record MarkerRule(string MetadataKey, string CollectorName);

    /// <summary>
    /// <see cref="EvidenceSourceType"/> ⇒ the collectors that emit it, each identified by a metadata key ONLY
    /// it writes. Every key is verified present on 100% of that collector's live records. A source type absent
    /// from this map has no inference rule at all and resolves to unattributed — deliberately: the accrued
    /// store contains none of them, and inventing a rule for a case with no evidence behind it is the
    /// over-confidence this table is built to avoid.
    /// </summary>
    private static readonly IReadOnlyDictionary<EvidenceSourceType, IReadOnlyList<MarkerRule>> Markers =
        new Dictionary<EvidenceSourceType, IReadOnlyList<MarkerRule>>
        {
            // Two collectors, and the reason the marker rule exists at all (see the class remarks).
            [EvidenceSourceType.NewsArticle] =
            [
                new(NewsAttentionCollector.MetadataMarkerKey, RadarCollectorNames.NewsSearch),
                new(GdeltNewsCollector.MetadataMarkerKey, RadarCollectorNames.GdeltNews),
            ],
            [EvidenceSourceType.PressRelease] =
            [
                new(RssPressReleaseCollector.MetadataMarkerKey, RadarCollectorNames.Rss),
            ],
            [EvidenceSourceType.GovernmentContract] =
            [
                new(UsaSpendingContractCollector.MetadataMarkerKey, RadarCollectorNames.UsaSpending),
            ],
            // Three collectors; the third is resolved by elimination (see Eliminations).
            [EvidenceSourceType.Filing] =
            [
                new(SecForm4Collector.MetadataMarkerKey, RadarCollectorNames.SecForm4),
                new(Sec13DGCollector.MetadataMarkerKey, RadarCollectorNames.Sec13DG),
            ],
            [EvidenceSourceType.RegulatoryApproval] =
            [
                new(FdaClearanceCollector.MetadataMarkerKey, RadarCollectorNames.Fda),
            ],
            [EvidenceSourceType.JobPosting] =
            [
                new(HiringBoardCollector.MetadataMarkerKey, RadarCollectorNames.HiringAts),
            ],
            [EvidenceSourceType.Patent] =
            [
                new(PatentActivityCollector.MetadataMarkerKey, RadarCollectorNames.Patents),
            ],
            [EvidenceSourceType.Trademark] =
            [
                new(TrademarkActivityCollector.MetadataMarkerKey, RadarCollectorNames.Trademarks),
            ],
            [EvidenceSourceType.LocalFile] =
            [
                new(LocalFileEvidenceCollector.MetadataMarkerKey, RadarCollectorNames.LocalFile),
            ],
        };

    /// <summary>
    /// The ONE elimination rule: <c>sec-edgar</c> writes no exclusive metadata key of its own, so
    /// <c>Filing</c> evidence carrying NEITHER of the other two SEC collectors' markers is its.
    /// <para>
    /// This is only sound because the set of <c>Filing</c> emitters is CLOSED at three, and the other two are
    /// distinguishable — both verified in code rather than assumed. It is additionally checked structurally:
    /// <see cref="SecEdgarFilingCollector"/> hard-filters every fetched filing against
    /// <c>Radar:Sec:Forms</c> (<c>if (!_forms.Contains(filing.Form)) continue;</c>), which the shipped
    /// configuration sets to <c>8-K/10-Q/10-K</c>, and empirically its live records contain only those three
    /// forms while <c>sec-form4</c> contains only <c>4</c> and <c>sec-13dg</c> only <c>SC 13D/13G(/A)</c>.
    /// The elimination does NOT depend on that configuration, though — that is exactly why the discriminator
    /// is the other two collectors' markers rather than the form string itself.
    /// </para>
    /// <para>
    /// A fourth <c>Filing</c> collector would silently break this. Guard: any new collector emitting
    /// <see cref="EvidenceSourceType.Filing"/> must either declare an exclusive marker key here or this rule
    /// must be removed — there is a test pinning the closed set.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<EvidenceSourceType, string> Eliminations =
        new Dictionary<EvidenceSourceType, string>
        {
            [EvidenceSourceType.Filing] = RadarCollectorNames.SecEdgar,
        };

    /// <summary>
    /// Which collectors this table can name, per <see cref="EvidenceSourceType"/> — markers plus the
    /// elimination rule, Ordinal-ordered. Exposed for the anti-drift tests, which pin two things this table
    /// cannot enforce on itself: that EVERY shipped collector is covered (so adding a collector without a
    /// marker key fails a test rather than silently un-attributing its evidence), and that the
    /// <see cref="EvidenceSourceType.Filing"/> set is still CLOSED at the three collectors the elimination
    /// rule assumes.
    /// </summary>
    public static IReadOnlyDictionary<EvidenceSourceType, IReadOnlyList<string>> CoverageBySourceType { get; } =
        BuildCoverage();

    private static IReadOnlyDictionary<EvidenceSourceType, IReadOnlyList<string>> BuildCoverage()
    {
        var coverage = new Dictionary<EvidenceSourceType, List<string>>();

        foreach (var (sourceType, rules) in Markers)
        {
            coverage[sourceType] = rules.Select(r => r.CollectorName).ToList();
        }

        foreach (var (sourceType, collectorName) in Eliminations)
        {
            if (!coverage.TryGetValue(sourceType, out var names))
            {
                coverage[sourceType] = names = [];
            }

            names.Add(collectorName);
        }

        return coverage.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)[.. kvp.Value.OrderBy(n => n, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Re-derives the collector name for <paramref name="evidence"/>, or returns <c>null</c> when it cannot be
    /// established. Never guesses and never throws.
    /// <para>
    /// Resolution order: (1) the source type must have a rule set — an unknown source type resolves to
    /// <c>null</c>; (2) exactly one of that set's exclusive markers must be present — <b>two contradictory
    /// markers resolve to <c>null</c></b>, because a record claiming to be from two collectors is evidence
    /// that the table's premise is wrong for it, not an invitation to pick one; (3) failing any marker, the
    /// source type's elimination rule applies if it has one; (4) otherwise <c>null</c>.
    /// </para>
    /// </summary>
    public static string? Infer(EvidenceItem? evidence)
    {
        if (evidence is null || !Markers.TryGetValue(evidence.SourceType, out var rules))
        {
            return null;
        }

        // The shared envelope reader (skip-don't-throw): malformed/absent metadata yields an empty bag, which
        // falls through to the elimination rule or to null — never an exception on a scoring path.
        EvidenceMetadata.TryRead(evidence.MetadataJson, out var metadata, out _);

        string? matched = null;
        foreach (var rule in rules)
        {
            if (!metadata.TryGetValue(rule.MetadataKey, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (matched is not null)
            {
                // Contradictory markers: two collector-exclusive keys on one record. Unattributed, not a
                // coin flip.
                return null;
            }

            matched = rule.CollectorName;
        }

        if (matched is not null)
        {
            return matched;
        }

        return Eliminations.TryGetValue(evidence.SourceType, out var eliminated) ? eliminated : null;
    }
}
