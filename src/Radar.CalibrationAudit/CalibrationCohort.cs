using Radar.Application.Filings;
using Radar.Infrastructure.Filings;

namespace Radar.CalibrationAudit;

/// <summary>One scoped-cohort row: the sealed model answer for an accession, read through the production cache.</summary>
public sealed record CohortRow(
    string Accession,
    string AccessionSha256,
    AnalyzedFilingRecord Record,
    string CacheFile);

/// <summary>
/// A legacy ROOT-level cache file excluded from the cohort (reason <c>legacy-scope</c>), with the
/// active-scope outcome beside it so an outcome CONFLICT (stale legacy read disagreeing with the active
/// model-scoped read) is detected and NAMED rather than silently shadowed.
/// </summary>
public sealed record LegacyExclusion(
    string Accession,
    string CacheFile,
    string? LegacyOutcome,
    string? ActiveOutcome,
    bool OutcomeConflict);

/// <summary>A cache file that exists on disk but the production cache read degraded to a miss (stale version / malformed).</summary>
public sealed record UnreadableCacheFile(string Accession, string CacheFile, string Location);

public sealed record CalibrationCohort(
    string ScopeSegment,
    IReadOnlyList<CohortRow> Rows,
    IReadOnlyList<LegacyExclusion> LegacyExclusions,
    IReadOnlyList<UnreadableCacheFile> UnreadableFiles);

/// <summary>
/// Builds the model-scoped calibration cohort (spec 162 Phase A) from the analyzed-filing cache. The scoped
/// directory is <c>{cacheRoot}/{scopeSegment}</c> where the segment was derived through the PRODUCTION
/// <c>AddFileAnalyzedFilingCache</c> scoping logic (never re-derived here); every record is read back
/// through the production <see cref="FileAnalyzedFilingCache"/> (its consistency + cache-version guards
/// included), so a file this reader rejects is reported as unreadable rather than trusted. Legacy
/// ROOT-level files are EXCLUDED from the cohort and listed with reason <c>legacy-scope</c>; duplicate
/// accessions inside the scoped cohort are an ERROR (fail loudly — the worksheet must be a function of
/// accession). Strictly read-only.
/// </summary>
public static class CalibrationCohortBuilder
{
    public static async Task<CalibrationCohort> BuildAsync(
        string cacheRoot,
        string scopeSegment,
        IAnalyzedFilingCache scopedCache,
        IAnalyzedFilingCache legacyRootCache,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeSegment);
        ArgumentNullException.ThrowIfNull(scopedCache);
        ArgumentNullException.ThrowIfNull(legacyRootCache);

        var scopedDirectory = Path.Combine(cacheRoot, scopeSegment);
        if (!Directory.Exists(scopedDirectory))
        {
            throw new InvalidOperationException(
                $"Model-scoped analyzed-filing cache directory '{scopedDirectory}' does not exist. "
                    + "Check --cache-root and --model-identity: the scope segment is derived from the model "
                    + "identity through the production cache scoping logic, so a wrong identity resolves to a "
                    + "directory that was never written.");
        }

        var unreadable = new List<UnreadableCacheFile>();

        // --- Scoped cohort -------------------------------------------------------------------------------
        var scopedFiles = Directory.EnumerateFiles(scopedDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToList();

        // Duplicate accessions within the scoped worksheet are an error (spec 162). Files are keyed by
        // sanitized accession, so a duplicate can only arise from casing/sanitization drift — either way the
        // worksheet would stop being a function of accession, which is fatal for the label join.
        var duplicates = scopedFiles
            .GroupBy(static f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Duplicate accession(s) in the model-scoped cache cohort — the sealed worksheet must contain "
                    + $"exactly one row per accession: {string.Join(", ", duplicates)}");
        }

        var rows = new List<CohortRow>(scopedFiles.Count);
        var scopedByAccession = new Dictionary<string, AnalyzedFilingRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in scopedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var accession = Path.GetFileNameWithoutExtension(file);

            // The production read path: same file resolution, same consistency/version guards. A null here
            // for a file that exists means the production reader would NOT trust this record — report it,
            // never silently drop it.
            var record = await scopedCache.TryGetAsync(accession, ct).ConfigureAwait(false);
            if (record is null)
            {
                unreadable.Add(new UnreadableCacheFile(accession, file, "scoped"));
                continue;
            }

            rows.Add(new CohortRow(accession, AccessionHash.HexOf(accession), record, file));
            scopedByAccession[accession] = record;
        }

        // Deterministic worksheet order: SHA-256(accession) hex ascending — the study's one ordering key.
        rows.Sort(static (a, b) => string.CompareOrdinal(a.AccessionSha256, b.AccessionSha256));

        // --- Legacy ROOT-level files: excluded, listed, conflicts named -----------------------------------
        var legacyExclusions = new List<LegacyExclusion>();
        foreach (var file in Directory.EnumerateFiles(cacheRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var accession = Path.GetFileNameWithoutExtension(file);

            var legacyRecord = await legacyRootCache.TryGetAsync(accession, ct).ConfigureAwait(false);
            if (legacyRecord is null)
            {
                unreadable.Add(new UnreadableCacheFile(accession, file, "legacy-root"));
                continue;
            }

            var active = scopedByAccession.GetValueOrDefault(accession);
            legacyExclusions.Add(new LegacyExclusion(
                accession,
                file,
                legacyRecord.Outcome.ToString(),
                active?.Outcome.ToString(),
                OutcomeConflict: active is not null && active.Outcome != legacyRecord.Outcome));
        }

        return new CalibrationCohort(scopeSegment, rows, legacyExclusions, unreadable);
    }
}
