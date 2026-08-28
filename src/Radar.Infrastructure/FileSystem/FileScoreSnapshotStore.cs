using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy.DenominatorAudit;
using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Scoring;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk mirror of a <see cref="CompanyScoreSnapshot"/> together with the
/// <see cref="ScoreEvidenceLink"/>s that trace it back to the contributing signals/evidence. Writes one
/// JSON file per snapshot to <c>{RootDirectory}/{companyId}/{snapshotId}.json</c> (or to the name a
/// <see cref="FileScoreSnapshotStoreOptions.SnapshotFileName"/> selector supplies), grouping by company so
/// a single company's score history is trivial to browse once multiple runs accumulate. All file I/O is
/// confined to Infrastructure; the Application sees only <see cref="IScoreSnapshotFileStore"/>. Disk
/// failures degrade gracefully (return the attempted path, marked
/// <see cref="DurableWriteOutcome.Failed"/>) and never crash the run; the in-memory score repository copy
/// still exists — but the failure is no longer SILENT (spec 193 §1): the typed outcome lets the scoring
/// pass count a snapshot that never reached disk instead of reporting it as durably stored.
/// <para>
/// <b>Where the failure is REPORTED is per-instance</b>, chosen by
/// <see cref="FileScoreSnapshotStoreOptions.FailureLogging"/> (spec 195 §1) — never a class-wide constant,
/// because this store has two kinds of consumer and only one of them owns an aggregate. On the instances
/// <c>ScoringPass</c> writes through (the <c>AddFileScoreStore</c> registration and
/// <see cref="StrategyScopedScoreSnapshotFileStoreFactory"/>) the mode is
/// <see cref="GracefulFileWriteFailureLogging.CallerAggregates"/>, so that pass's one aggregated Warning
/// replaces the per-file Warnings instead of being added to them. A replay-scoped instance
/// (<see cref="ReplayScopedScoreSnapshotFileStoreFactory"/>) keeps the default
/// <see cref="GracefulFileWriteFailureLogging.Immediate"/>: <c>ReplayRunner</c> discards the write result
/// and counts every as-of point as written, so the per-file Warning is the ONLY report a failed replay
/// write has.
/// </para>
/// </summary>
/// <remarks>
/// <b>Overwrite-allowed (upsert-by-Id, last-write-wins).</b> This deliberately DIFFERS from the
/// insert-only <see cref="FileRawEvidenceStore"/>: AD-1 immutability governs <i>evidence only</i>.
/// Score snapshots are upsert-by-Id, so an existing file for the same snapshot id is overwritten rather
/// than skipped. This is intentional — do not re-flag it as an AD-1 violation.
/// <para>
/// <b>Also the link-bearing read (spec 172).</b> This store ADDITIONALLY implements
/// <see cref="IScoreSnapshotLinkReader"/> — spec 142's "the repository IS the file store" pattern, applied
/// to the score files: the persisted format has always carried the evidence links, so the denominator
/// audit's link read lives on the SAME class, one format definition, one deserializer, one skip-don't-throw
/// rule set. The scalar reads keep their deliberately-empty <c>Links</c> posture, unchanged.
/// </para>
/// </remarks>
public sealed class FileScoreSnapshotStore : IScoreSnapshotFileStore, IScoreSnapshotLinkReader
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

    public async Task<DurableWriteResult> WriteAsync(
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
            ResolveFileName(snapshot));

        // Spec 148: the existence PROBE has to happen here — before anything is written, while the target
        // path is the only thing computed — but the observer must only be told once the replacement has
        // actually HAPPENED. Serialization or the graceful disk-failure path can still abandon the write, and
        // an aggregated "OVERWROTE N as-of point(s)" warning must not assert a replacement that never
        // occurred. Only ever wired by the replay-scoped factory, whose as-of-keyed names make a same-label
        // re-run overwrite in place; on the live/forward path the observer is null and no probe is even made.
        var willReplaceExisting = _options.OnSnapshotOverwritten is not null && File.Exists(path);

        var json = Serialize(snapshot, links);

        // Spec 195 §1: the mode is the INSTANCE's, never a class-wide constant. A ScoringPass-owned store
        // is CallerAggregates, because that pass emits ONE aggregated "{ScoreSnapshotsNotPersisted} score
        // snapshot(s) could NOT be durably persisted" Warning for exactly these failures (without it the
        // batch path logged N per-file Warnings PLUS that aggregate). A replay-scoped store keeps the
        // default Immediate, because ReplayRunner discards this result and has no aggregate to substitute.
        var written = await GracefulFileWriter
            .TryWriteAllTextAsync(
                path,
                json,
                _logger,
                ct,
                encoding: null,
                failureLogging: _options.FailureLogging)
            .ConfigureAwait(false);
        if (written)
        {
            // The write succeeded, so the earlier file really is gone (upsert-by-Id / last-write-wins,
            // unchanged). Now, and only now, is the observer told. Spec 193 does not touch this: the
            // overwrite observer must still fire ONLY on a successful write.
            if (willReplaceExisting)
            {
                _options.OnSnapshotOverwritten!(snapshot);
            }

            _logger.LogInformation(
                "Wrote score snapshot {SnapshotId} for company {CompanyId} to {Path}.",
                snapshot.Id,
                snapshot.CompanyId,
                path);
        }

        // Spec 193 §1: the attempted path, plus whether anything reached it. A failed write leaves the
        // in-memory score repository copy (so the run completes) but nothing on disk — the scoring pass
        // counts it rather than reporting the snapshot as durably stored.
        return DurableWriteResult.From(path, written);
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
    /// All persisted snapshots for the company WITH their evidence links hydrated (spec 172's
    /// <see cref="IScoreSnapshotLinkReader"/>), ascending by CreatedAtUtc then Id — the SAME deterministic
    /// order (AD-3) and the SAME per-file parse (guards, logging, skip-don't-throw) as
    /// <see cref="ReadAllForCompanyAsync"/>; only the projection differs. Read-only; a missing directory
    /// returns an empty list; cancellation propagates.
    /// </summary>
    public async Task<IReadOnlyList<ScoreSnapshotWithLinks>> ReadAllWithLinksForCompanyAsync(
        Guid companyId, CancellationToken ct)
    {
        var files = EnumerateCompanyFiles(companyId);
        if (files is null)
        {
            return [];
        }

        var snapshots = new List<ScoreSnapshotWithLinks>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var parsed = await TryReadFileAsync(file, companyId, ct).ConfigureAwait(false);
            if (parsed is not null)
            {
                snapshots.Add(new ScoreSnapshotWithLinks(ToSnapshot(parsed), ToLinks(parsed)));
            }
        }

        // Deterministic (AD-3): ascending by CreatedAtUtc, tie-broken by Id — same rule as the scalar read.
        snapshots.Sort(static (a, b) =>
        {
            var byCreated = a.Snapshot.CreatedAtUtc.CompareTo(b.Snapshot.CreatedAtUtc);
            return byCreated != 0 ? byCreated : a.Snapshot.Id.CompareTo(b.Snapshot.Id);
        });

        return snapshots;
    }

    /// <summary>
    /// The leaf file name for a snapshot: <c>{snapshotId}.json</c> unless the options supply a deterministic
    /// selector (<see cref="FileScoreSnapshotStoreOptions.SnapshotFileName"/> — replay, spec 139). The guard
    /// is a programming-error check, not graceful degradation: a selector returning a path would silently
    /// write outside the company directory, so it throws rather than degrading.
    /// <para>
    /// It reuses the SHARED <see cref="StorageSegmentName"/> rule rather than a bespoke check. A
    /// <c>Path.GetFileName(name) == name</c> test looks equivalent but accepts <c>"."</c> and <c>".."</c>,
    /// both of which resolve to the company DIRECTORY rather than a file inside it — the shared rule already
    /// rejects those alongside blank, untrimmed and separator-bearing names, and keeping one implementation
    /// means a future fix to it cannot miss this call site.
    /// </para>
    /// </summary>
    private string ResolveFileName(CompanyScoreSnapshot snapshot)
    {
        if (_options.SnapshotFileName is not { } selector)
        {
            return snapshot.Id + ".json";
        }

        var name = selector(snapshot);
        if (!StorageSegmentName.IsUsable(name))
        {
            throw new InvalidOperationException(
                $"FileScoreSnapshotStoreOptions.SnapshotFileName returned '{name}', which is not a usable file "
                    + $"name; the selector names ONE file inside the company directory, so {StorageSegmentName.Rule}.");
        }

        return name;
    }

    /// <summary>
    /// Enumerates a company's snapshot files. WriteAsync stores each snapshot flat under
    /// <c>{RootDirectory}/{companyId}/</c> (named by <see cref="ResolveFileName"/>), so all of a company's
    /// snapshots live directly in this directory. Returns <c>null</c> when the directory is missing or
    /// unenumerable (degrade to "no snapshots"); an enumeration failure logs a warning.
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
    /// Reads and parses a single snapshot file, scalar fields only. Routes through
    /// <see cref="TryReadFileAsync"/> (the ONE per-file parse — reuse over copy) and projects the scalar
    /// snapshot. The Links are intentionally left empty on this path — this is NOT dropped provenance: the
    /// current report's evidence links still come from the in-memory repo, unchanged, and the link-bearing
    /// projection is <see cref="ReadAllWithLinksForCompanyAsync"/>.
    /// </summary>
    private async Task<CompanyScoreSnapshot?> TryReadSnapshotAsync(
        string file, Guid companyId, CancellationToken ct)
    {
        var parsed = await TryReadFileAsync(file, companyId, ct).ConfigureAwait(false);
        return parsed is null ? null : ToSnapshot(parsed);
    }

    /// <summary>
    /// The shared per-file parse every read method routes through (read text → deserialize →
    /// null/CompanyId guards). Returns <c>null</c> when the file is a JSON <c>null</c>, carries a foreign
    /// CompanyId, or is unreadable/malformed (each logged + skipped, never thrown); cancellation propagates.
    /// </summary>
    private async Task<ScoreSnapshotFile?> TryReadFileAsync(
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

            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // One unreadable/malformed snapshot file must not break the whole read.
            _logger.LogWarning(ex, "Failed to read score-snapshot file '{File}'; skipping.", file);
            return null;
        }
    }

    /// <summary>Projects the persisted shape onto the scalar domain snapshot (Links deliberately empty).</summary>
    private static CompanyScoreSnapshot ToSnapshot(ScoreSnapshotFile parsed) =>
        new(
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
            ScoringConfigVersion: parsed.ScoringConfigVersion,
            // Same posture (spec 137): a pre-existing snapshot file has no strategyName property, so it
            // deserializes to null — read as the primary/legacy strategy.
            StrategyName: parsed.StrategyName,
            // Same posture again (spec 141): a file written before the collectionProvenance property
            // existed deserializes to null — "what was collected then is unknown", which is honest and
            // affects nothing (the field is recorded, never hashed, never a comparability input).
            CollectionProvenance: parsed.CollectionProvenance);

    /// <summary>
    /// Projects the persisted link shapes back onto the domain record (spec 172). Defensive nulls only for
    /// hand-edited files: a missing <c>links</c> property reads as an empty list and a missing reason as an
    /// empty string — never a throw, matching the store's skip-don't-throw posture.
    /// </summary>
    private static IReadOnlyList<ScoreEvidenceLink> ToLinks(ScoreSnapshotFile parsed)
    {
        if (parsed.Links is not { Count: > 0 } persisted)
        {
            return [];
        }

        var links = new List<ScoreEvidenceLink>(persisted.Count);
        foreach (var link in persisted)
        {
            links.Add(new ScoreEvidenceLink(
                Id: link.LinkId,
                ScoreSnapshotId: link.ScoreSnapshotId,
                SignalId: link.SignalId,
                EvidenceId: link.EvidenceId,
                ContributionReason: link.ContributionReason ?? string.Empty,
                ContributionWeight: link.ContributionWeight));
        }

        return links;
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
                ContributionWeight: l.ContributionWeight))],
            StrategyName: snapshot.StrategyName,
            CollectionProvenance: snapshot.CollectionProvenance);

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
        IReadOnlyList<ScoreEvidenceLinkFile> Links,
        // Human-readable strategy identity (spec 137), carried alongside the opaque ScoringConfigVersion.
        // Trailing + nullable with a default, exactly as ScoringConfigVersion was added: pre-existing files
        // that lack the property deserialize to null → read as the primary/legacy strategy. Property order on
        // disk is irrelevant (System.Text.Json maps by name).
        string? StrategyName = null,
        // What was collected on the run that produced this snapshot (spec 141): the enabled-collector
        // descriptor, recorded verbatim and hashed into nothing. Trailing + nullable with a default, the same
        // posture as the two stamps above: pre-existing files lack the property and deserialize to null.
        string? CollectionProvenance = null);

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
