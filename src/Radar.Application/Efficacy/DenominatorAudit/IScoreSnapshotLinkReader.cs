using Radar.Domain.Scoring;

namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>One persisted score snapshot together with its stored <see cref="ScoreEvidenceLink"/>s.</summary>
public sealed record ScoreSnapshotWithLinks(
    CompanyScoreSnapshot Snapshot,
    IReadOnlyList<ScoreEvidenceLink> Links);

/// <summary>
/// Read-only access to a company's persisted score snapshots WITH their evidence links (spec 172).
/// <para>
/// The scalar snapshot read seam deliberately leaves <c>Links</c> empty (the current report's links come from
/// the in-run repository, unchanged), but the persisted snapshot files have always carried the links — the
/// provenance chain score → signal → evidence. The denominator audit needs those links for HISTORICAL
/// snapshots, which no in-run store can serve, so this interface exposes the link-bearing read.
/// </para>
/// <para>
/// <b>Not a second path to the score files.</b> Following spec 142's recorded pattern ("the repository IS the
/// file store"), the file-backed snapshot store implements this interface IN ADDITION to the scalar read seam:
/// one file format definition, one deserializer, one skip-don't-throw rule set. A malformed file is logged and
/// skipped, never thrown; a missing directory returns an empty list; cancellation propagates. Read-only —
/// nothing here writes.
/// </para>
/// </summary>
public interface IScoreSnapshotLinkReader
{
    /// <summary>
    /// All persisted snapshots for the company with their links hydrated, ascending by CreatedAtUtc then Id
    /// (AD-3) — the same deterministic order as the scalar read.
    /// </summary>
    Task<IReadOnlyList<ScoreSnapshotWithLinks>> ReadAllWithLinksForCompanyAsync(
        Guid companyId, CancellationToken ct);
}
