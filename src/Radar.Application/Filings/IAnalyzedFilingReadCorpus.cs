namespace Radar.Application.Filings;

/// <summary>
/// One hydrated analyzed-filing read record plus the name of the file it came from. The file NAME (never a
/// full path) is carried deliberately: an artifact rendered from this corpus must be byte-identical across
/// machines, and an absolute path is machine-specific.
/// </summary>
public sealed record AnalyzedFilingCorpusEntry(AnalyzedFilingRecord Record, string SourceFileName);

/// <summary>
/// The WHOLE accrued analyzed-filing read corpus — every hydrated record PLUS a named count for every file
/// enumeration could not hand back. Nothing is dropped without being counted (CLAUDE.md): each failure mode
/// is its own field, never an "other" bucket, and a count that could not be taken is <c>null</c> ("not
/// recorded"), never a fabricated <c>0</c>.
/// </summary>
/// <param name="Entries">The hydrated records, ordered by accession (ordinal) so a re-read is deterministic (AD-3).</param>
/// <param name="ModelSegment">
/// The model-identity sub-directory segment the enumeration read under (spec 118), or <c>null</c> when none
/// is configured (records live at the cache root).
/// </param>
/// <param name="CorpusDirectoryExists">
/// Whether the directory the current segment resolves to exists at all. <c>false</c> with zero entries is a
/// MEASURED absence ("nothing has been read yet"), not a read failure.
/// </param>
/// <param name="EnumerationFailed">
/// Whether the directory listing itself failed (permissions/IO). When <c>true</c>, <see cref="FilesScanned"/>
/// and every per-file count below describe only what was reached before the failure — they are NOT a
/// complete accounting, and no caller may report them as one.
/// </param>
/// <param name="FilesScanned">How many <c>*.json</c> files inside the current segment were opened.</param>
/// <param name="UnreadableOrUnparseableFiles">Files that could not be read or did not deserialize into a record.</param>
/// <param name="OutsideCurrentModelSegmentFiles">
/// <c>*.json</c> files sitting at the cache ROOT that the CURRENT model segment does not reach — reads taken
/// under an earlier layout or an earlier model. They are real accrued reads and must never vanish from the
/// accounting just because this run's segment cannot see them. <c>0</c> when no segment is configured (no
/// file can then be outside it — a structural zero, stated as such); <c>null</c> when the root itself could
/// not be enumerated, i.e. NOT COUNTED.
/// </param>
/// <param name="FileNameAccessionMismatchFiles">
/// Files whose stored accession does not match the filename key they were found under: the record is not
/// trustworthy for the key it is filed at, exactly as the per-accession lookup treats it.
/// </param>
/// <param name="OutcomeSignalMismatchFiles">
/// Files whose <c>Outcome</c> and <c>Signal</c> disagree (a produced signal carrying none, or a confirmed
/// no-signal carrying one) — the same semantic-consistency rule the per-accession lookup applies.
/// </param>
public sealed record AnalyzedFilingCorpus(
    IReadOnlyList<AnalyzedFilingCorpusEntry> Entries,
    string? ModelSegment,
    bool CorpusDirectoryExists,
    bool EnumerationFailed,
    int FilesScanned,
    int UnreadableOrUnparseableFiles,
    int? OutsideCurrentModelSegmentFiles,
    int FileNameAccessionMismatchFiles,
    int OutcomeSignalMismatchFiles)
{
    /// <summary>How many records hydrated — the denominator every distribution over this corpus states.</summary>
    public int RecordsHydrated => Entries.Count;

    /// <summary>
    /// Files inside the current segment that were scanned but yielded no record. Excludes
    /// <see cref="OutsideCurrentModelSegmentFiles"/>, which were never scanned in the first place.
    /// </summary>
    public int FilesExcluded =>
        UnreadableOrUnparseableFiles + FileNameAccessionMismatchFiles + OutcomeSignalMismatchFiles;
}

/// <summary>
/// The read-only ENUMERATION seam over the accrued analyzed-filing read corpus, deliberately separate from
/// <see cref="IAnalyzedFilingCache"/>: the cache answers "what did we conclude about THIS accession" for the
/// pipeline, this answers "what has the reader produced across everything" for a read-side measurement.
/// Keeping them apart means no measurement consumer can reach a cache WRITE.
/// <para>
/// Same posture as the cache itself (spec 107, an AD-14 analogue): reference/operational data, NEVER
/// evidence, never a signal source, never a collector, and never a scoring/fingerprint input. Reading it
/// changes no score and produces no signal — it only reports what the reader already produced.
/// </para>
/// </summary>
public interface IAnalyzedFilingReadCorpus
{
    /// <summary>
    /// Enumerates every accrued read record plus the named exclusion counts. Best-effort (AD-8): a bad file
    /// is COUNTED and skipped rather than thrown; caller cancellation propagates.
    /// </summary>
    Task<AnalyzedFilingCorpus> ReadAllAsync(CancellationToken ct);
}
