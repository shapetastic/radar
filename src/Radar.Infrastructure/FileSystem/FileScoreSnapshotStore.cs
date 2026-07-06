using System.Text.Json;

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

        // Provenance guard: every link must belong to this snapshot. Persisting a mismatched link would
        // write an internally inconsistent file and silently break the score→signal/evidence trace.
        foreach (var link in links)
        {
            if (link.ScoreSnapshotId != snapshot.Id)
            {
                throw new ArgumentException(
                    $"Link {link.Id} targets snapshot {link.ScoreSnapshotId}, not snapshot {snapshot.Id}; refusing to persist a mismatched pair.",
                    nameof(links));
            }
        }

        var path = Path.Combine(
            _options.RootDirectory,
            snapshot.CompanyId.ToString(),
            snapshot.Id + ".json");

        var json = Serialize(snapshot, links);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Wrote score snapshot {SnapshotId} for company {CompanyId} to {Path}.",
                snapshot.Id,
                snapshot.CompanyId,
                path);
        }

        return path;
    }

    public async Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
        Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct)
    {
        var files = EnumerateCompanyFiles(companyId);
        if (files is null)
        {
            return null;
        }

        // Deterministic (AD-3): among snapshots strictly before beforeUtc, we want the newest by
        // CreatedAtUtc, tie-broken by Id (both descending). Track the single best candidate in one
        // pass rather than materialising and sorting the whole history — same result, no list
        // allocation, and cost stays linear as a company's snapshot history grows.
        CompanyScoreSnapshot? best = null;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var parsed = await TryReadSnapshotAsync(file, companyId, ct).ConfigureAwait(false);
            if (parsed is null)
            {
                continue;
            }

            // Only snapshots strictly before beforeUtc are eligible.
            if (parsed.CreatedAtUtc >= beforeUtc)
            {
                continue;
            }

            // Keep this candidate only if it is strictly newer than the current best, tie-broken
            // by Id descending — mirrors the previous OrderByDescending(CreatedAtUtc).ThenByDescending(Id).
            if (best is null
                || parsed.CreatedAtUtc > best.CreatedAtUtc
                || (parsed.CreatedAtUtc == best.CreatedAtUtc && parsed.Id.CompareTo(best.Id) > 0))
            {
                best = parsed;
            }
        }

        return best;
    }

    public async Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(
        Guid companyId, CancellationToken ct)
    {
        var files = EnumerateCompanyFiles(companyId);
        if (files is null)
        {
            return [];
        }

        var snapshots = new List<CompanyScoreSnapshot>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var parsed = await TryReadSnapshotAsync(file, companyId, ct).ConfigureAwait(false);
            if (parsed is not null)
            {
                snapshots.Add(parsed);
            }
        }

        // Deterministic (AD-3): ascending by CreatedAtUtc, tie-broken by Id.
        snapshots.Sort(static (a, b) =>
        {
            var byCreated = a.CreatedAtUtc.CompareTo(b.CreatedAtUtc);
            return byCreated != 0 ? byCreated : a.Id.CompareTo(b.Id);
        });

        return snapshots;
    }

    /// <summary>
    /// Enumerates a company's snapshot files. WriteAsync stores each snapshot flat under
    /// <c>{RootDirectory}/{companyId}/{snapshotId}.json</c>, so all of a company's snapshots live directly in
    /// this directory. Returns <c>null</c> when the directory is missing or unenumerable (degrade to "no
    /// snapshots"); an enumeration failure logs a warning.
    /// </summary>
    private List<string>? EnumerateCompanyFiles(Guid companyId)
    {
        var companyDir = Path.Combine(_options.RootDirectory, companyId.ToString());
        if (!Directory.Exists(companyDir))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(companyDir, "*.json", SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to enumerate score-snapshot files in '{CompanyDir}'; returning no snapshots.",
                companyDir);
            return null;
        }
    }

    /// <summary>
    /// Reads and parses a single snapshot file (read text → deserialize → null/CompanyId guards →
    /// reconstruct scalar fields). The shared per-file parse both read methods route through (reuse over
    /// copy). Returns <c>null</c> when the file is a JSON <c>null</c>, carries a foreign CompanyId, or is
    /// unreadable/malformed (each logged + skipped, never thrown); cancellation propagates. The Links are
    /// intentionally left empty (scalar fields only) — this is NOT dropped provenance: the current report's
    /// evidence links still come from the in-memory repo, unchanged.
    /// </summary>
    private async Task<CompanyScoreSnapshot?> TryReadSnapshotAsync(
        string file, Guid companyId, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<ScoreSnapshotFile>(text, RadarFileStoreJson.Options);
            if (parsed is null)
            {
                // A JSON literal `null` deserializes to a null record — treat it as a malformed
                // entry so operators can spot corrupted snapshot files.
                _logger.LogWarning(
                    "Score-snapshot file '{File}' contained a null snapshot; skipping.", file);
                return null;
            }

            // Guard the method contract: this directory is keyed by companyId, but a mis-filed or
            // hand-copied JSON could carry a different CompanyId. Returning it would attribute
            // another company's scores to this one and corrupt the week-over-week deltas, so warn
            // and skip rather than trust the file's location.
            if (parsed.CompanyId != companyId)
            {
                _logger.LogWarning(
                    "Score-snapshot file '{File}' has CompanyId {FileCompanyId} but is filed under {CompanyId}; skipping.",
                    file,
                    parsed.CompanyId,
                    companyId);
                return null;
            }

            return new CompanyScoreSnapshot(
                Id: parsed.SnapshotId,
                CompanyId: parsed.CompanyId,
                ScoringVersion: parsed.ScoringVersion,
                TrajectoryScore: parsed.TrajectoryScore,
                OpportunityScore: parsed.OpportunityScore,
                AttentionScore: parsed.AttentionScore,
                EvidenceConfidenceScore: parsed.EvidenceConfidenceScore,
                SignalVelocityScore: parsed.SignalVelocityScore,
                Explanation: parsed.Explanation,
                ComponentJson: parsed.ComponentJson,
                WindowStartUtc: parsed.WindowStartUtc,
                WindowEndUtc: parsed.WindowEndUtc,
                CreatedAtUtc: parsed.CreatedAtUtc,
                // Old-format files lack this property and deserialize to null (default System.Text.Json
                // tolerates missing members). A null stamp is treated as "not comparable".
                ScoringConfigVersion: parsed.ScoringConfigVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // One unreadable/malformed snapshot file must not break the whole read.
            _logger.LogWarning(ex, "Failed to read score-snapshot file '{File}'; skipping.", file);
            return null;
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
            ScoringConfigVersion: snapshot.ScoringConfigVersion,
            Links: [.. links.Select(l => new ScoreEvidenceLinkFile(
                LinkId: l.Id,
                ScoreSnapshotId: l.ScoreSnapshotId,
                SignalId: l.SignalId,
                EvidenceId: l.EvidenceId,
                ContributionReason: l.ContributionReason,
                ContributionWeight: l.ContributionWeight))]);

        return JsonSerializer.Serialize(file, RadarFileStoreJson.Options);
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
        // Whole scoring-generation stamp (distinct from ScoringVersion). Trailing + nullable so old-format
        // files that lack the property deserialize to null → treated as not comparable → "(scoring updated)".
        string? ScoringConfigVersion,
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
