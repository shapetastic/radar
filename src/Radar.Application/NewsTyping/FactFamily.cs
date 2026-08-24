using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// One fact as the family builder consumes it: the validated fact's identity/content plus the observation
/// provenance the grouping key needs (company, first-observed instant, publisher, capture mode). Built by
/// the generator by joining completed typings to their archived observations — never by a model.
/// </summary>
public sealed record FactFamilyInputFact(
    Guid FactId,
    Guid CompanyId,
    IReadOnlyList<NewsEventType> EventTypes,
    string Statement,
    DateTimeOffset FirstObservedAtUtc,
    string Publisher,
    Guid ObservationId,
    NewsObservationCaptureMode CaptureMode);

/// <summary>
/// One deterministic fact family (spec 181 §4) as PROJECTED onto a checkpoint window (spec 186 §4): the
/// same claim about the same company, however many syndicated copies asserted it.
/// <para>
/// <see cref="FamilyId"/> is the episode's DURABLE identity — builder version + company + capture mode +
/// the first-ever member's UTC date + that member's sorted event types + that member's normalized statement
/// — computed by stage-1 segmentation over the FULL accrued fact history, deliberately NOT from the member
/// list and NOT from the checkpoint window. So a later-arriving member joins the SAME family id at the next
/// checkpoint instead of minting a sibling, an episode whose earliest member ages OUT of the window keeps
/// its id, and two temporally separate episodes asserting a byte-identical recurring claim never collide.
/// </para>
/// <para>
/// Everything else on this record — <see cref="RepresentativeFactId"/>,
/// <see cref="RepresentativeStatement"/>, <see cref="CanonicalClaimKey"/>, <see cref="EventTypes"/>,
/// <see cref="MemberFactIds"/>, <see cref="MemberCount"/>, <see cref="DistinctPublisherCount"/> and
/// <see cref="EarliestObservedAtUtc"/> — describes the IN-WINDOW members alone, so the representative is
/// always resolvable in the checkpoint's own fact index.
/// </para>
/// <para>
/// Consequence, stated honestly (the ONE remaining id-shift case): if a later checkpoint admits a member
/// temporally EARLIER than every member the episode had ever observed, the anchor — and therefore the id —
/// moves; the checkpoint snapshots make that visible rather than hiding it.
/// </para>
/// </summary>
public sealed record FactFamilyRecord(
    Guid FamilyId,
    Guid CompanyId,
    NewsObservationCaptureMode CaptureMode,
    Guid RepresentativeFactId,
    string RepresentativeStatement,
    string CanonicalClaimKey,
    IReadOnlyList<NewsEventType> EventTypes,
    IReadOnlyList<Guid> MemberFactIds,
    int MemberCount,
    int DistinctPublisherCount,
    DateTimeOffset EarliestObservedAtUtc);

/// <summary>
/// One persisted checkpoint family SET (spec 181 §4): the complete families over ALL qualifying validated
/// facts of exactly ONE extractor cohort in the checkpoint window. A later run writes a NEW snapshot, never
/// edits an old one; membership changes are visible as snapshot-to-snapshot differences.
/// <para>
/// <see cref="FactsConsidered"/> and <see cref="FactsWithoutCompany"/> keep their spec-181 WINDOW basis:
/// they count the facts of this checkpoint's window, not the full history stage-1 segmentation reads
/// (spec 186 §4). The snapshot is a statement about a window; a history-wide count here would answer a
/// different question under the same name.
/// </para>
/// <para>
/// <see cref="SchemaVersion"/> describes this FILE's shape and is deliberately NOT bumped by spec 186 §4:
/// no field was added, removed or re-meant. The builder change is recorded where it belongs — in
/// <see cref="BuilderIdentity"/>, which spec 181 §4 already made the cohort discriminator (cohorts never
/// pool across builder versions), so a v1 and a v2 snapshot are already unmistakable.
/// </para>
/// </summary>
public sealed record FactFamilySnapshot(
    string SchemaVersion,
    string BuilderIdentity,
    string CohortKey,
    DateTimeOffset CheckpointUtc,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<FactFamilyRecord> Families,
    int FactsConsidered,
    int FactsWithoutCompany)
{
    public const string CurrentSchemaVersion = "fact-family-snapshot-v1";
}

/// <summary>
/// The checkpoint family-snapshot writer (spec 181 §4), implemented in Infrastructure at
/// <c>{root}/families/{cohort-policy-segment}/{checkpointUtc:yyyyMMdd'T'HHmmss'Z'}.json</c>. Append-only by
/// construction: each checkpoint is its own timestamped file.
/// </summary>
public interface IFactFamilySnapshotStore
{
    /// <summary>Persists the snapshot. Never throws for a disk failure (Warning + false); cancellation propagates.</summary>
    Task<bool> WriteAsync(string policySegment, FactFamilySnapshot snapshot, CancellationToken ct);
}
