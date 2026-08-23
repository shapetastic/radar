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
/// One deterministic fact family (spec 181 §4): the same claim about the same company, however many
/// syndicated copies asserted it. <see cref="FamilyId"/> is derived from builder version + company + the
/// canonical-claim key (the EARLIEST member's normalized statement) — deliberately NOT from the member list,
/// so a later-arriving member joins the SAME family id at the next checkpoint instead of minting a sibling.
/// Consequence, stated honestly: if a later checkpoint admits an EARLIER member (backlog typing reaching an
/// older observation), the representative — and therefore the id — can move; the checkpoint snapshots make
/// that visible rather than hiding it.
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
