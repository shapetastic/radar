using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Radar.Application.Scoring;
using Radar.Domain.Scoring;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk mirror of a <see cref="CompanyScoreSnapshot"/> together with the
/// <see cref="ScoreEvidenceLink"/>s that trace it back to the contributing signals/evidence. Writes one
/// JSON file per snapshot to <c>{RootDirectory}/{companyId}/{snapshotId}.json</c>, grouping by company so
/// a single company's score history is trivial to browse once multiple runs accumulate. All file I/O is
/// confined to Infrastructure; the Application sees only <see cref="IScoreSnapshotFileStore"/>. Disk
/// failures degrade gracefully (warn + return the attempted path) and never crash the run; the in-memory
/// score repository copy still exists.
/// </summary>
/// <remarks>
/// <b>Overwrite-allowed (upsert-by-Id, last-write-wins).</b> This deliberately DIFFERS from the
/// insert-only <see cref="FileRawEvidenceStore"/>: AD-1 immutability governs <i>evidence only</i>.
/// Score snapshots are upsert-by-Id, so an existing file for the same snapshot id is overwritten rather
/// than skipped. This is intentional — do not re-flag it as an AD-1 violation.
/// </remarks>
public sealed class FileScoreSnapshotStore : IScoreSnapshotFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // No enums on the score records today, but keep the converter for consistency with the other
        // file stores so any future enum fields render as readable string names, never integer ordinals.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FileScoreSnapshotStoreOptions _options;
    private readonly ILogger<FileScoreSnapshotStore> _logger;

    public FileScoreSnapshotStore(
        FileScoreSnapshotStoreOptions options,
        ILogger<FileScoreSnapshotStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<string> WriteAsync(
        CompanyScoreSnapshot snapshot,
        IReadOnlyList<ScoreEvidenceLink> links,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(links);

        var path = Path.Combine(
            _options.RootDirectory,
            snapshot.CompanyId.ToString(),
            snapshot.Id + ".json");

        var json = Serialize(snapshot, links);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Overwrite-allowed (upsert-by-Id, last-write-wins): unlike the insert-only evidence store,
            // score snapshots are NOT immutable (AD-1 covers evidence only), so we do not guard on
            // File.Exists.
            await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Wrote score snapshot {SnapshotId} for company {CompanyId} to {Path}.",
                snapshot.Id,
                snapshot.CompanyId,
                path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk hiccup must not crash the run; the in-memory score repository copy still exists.
            _logger.LogWarning(
                ex,
                "Failed to write score snapshot {SnapshotId} to {Path}; skipping.",
                snapshot.Id,
                path);
            return path;
        }
    }

    private static string Serialize(CompanyScoreSnapshot snapshot, IReadOnlyList<ScoreEvidenceLink> links)
    {
        var file = new ScoreSnapshotFile(
            SnapshotId: snapshot.Id,
            CompanyId: snapshot.CompanyId,
            ScoringVersion: snapshot.ScoringVersion,
            TrajectoryScore: snapshot.TrajectoryScore,
            OpportunityScore: snapshot.OpportunityScore,
            AttentionScore: snapshot.AttentionScore,
            EvidenceConfidenceScore: snapshot.EvidenceConfidenceScore,
            SignalVelocityScore: snapshot.SignalVelocityScore,
            Explanation: snapshot.Explanation,
            ComponentJson: snapshot.ComponentJson,
            WindowStartUtc: snapshot.WindowStartUtc,
            WindowEndUtc: snapshot.WindowEndUtc,
            CreatedAtUtc: snapshot.CreatedAtUtc,
            Links: [.. links.Select(l => new ScoreEvidenceLinkFile(
                LinkId: l.Id,
                ScoreSnapshotId: l.ScoreSnapshotId,
                SignalId: l.SignalId,
                EvidenceId: l.EvidenceId,
                ContributionReason: l.ContributionReason,
                ContributionWeight: l.ContributionWeight))]);

        return JsonSerializer.Serialize(file, SerializerOptions);
    }

    /// <summary>
    /// The persisted score-snapshot shape. Property names render camelCase via the serializer options
    /// (<c>snapshotId</c>, <c>companyId</c>, …). Carries the company id, the five component scores, the
    /// explanation, the raw <c>componentJson</c> breakdown (persisted as-is), the scoring window bounds,
    /// and the <c>links</c> that trace the score back to contributing signals/evidence (provenance).
    /// </summary>
    private sealed record ScoreSnapshotFile(
        Guid SnapshotId,
        Guid CompanyId,
        string ScoringVersion,
        int TrajectoryScore,
        int OpportunityScore,
        int AttentionScore,
        int EvidenceConfidenceScore,
        int SignalVelocityScore,
        string Explanation,
        string ComponentJson,
        DateTimeOffset WindowStartUtc,
        DateTimeOffset WindowEndUtc,
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<ScoreEvidenceLinkFile> Links);

    /// <summary>
    /// The persisted score-evidence link shape. Its <c>scoreSnapshotId</c> traces back to the parent
    /// snapshot and its <c>signalId</c>/<c>evidenceId</c> trace back to the contributing signal/evidence
    /// (the sacred provenance chain).
    /// </summary>
    private sealed record ScoreEvidenceLinkFile(
        Guid LinkId,
        Guid ScoreSnapshotId,
        Guid SignalId,
        Guid EvidenceId,
        string ContributionReason,
        int ContributionWeight);
}
