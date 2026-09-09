using Radar.Domain.Companies;

namespace Radar.Application.Acquisitions;

/// <summary>
/// SPEC 217 §2 — the PURE, run-time projection of the append-only acquisitions store into "which companies
/// are under a pending acquisition, and from when". Built ONCE per run/artifact from ONE store read and
/// shared by every consumer (scoring, the report, the efficacy comparison), so a snapshot's
/// <c>companyStatusAtScoring</c>, the report's banner and the efficacy exclusion can never disagree about
/// the same company.
/// <para>
/// <b>Deterministic (AD-3).</b> No clock, no I/O, no randomness: the answer is a function of the records
/// and the instant the caller supplies. When a company has more than one recognised agreement the EARLIEST
/// announcement wins — the first announced deal is the one that closed the thesis, and a later amendment
/// must not reopen a window that was already pinned.
/// </para>
/// <para>
/// <b>Nothing is discarded silently.</b> <see cref="Unreadable"/> carries the store's own count of files it
/// could not parse, so a consumer that read fewer acquisitions than exist can say so.
/// </para>
/// </summary>
public sealed class PendingAcquisitions
{
    /// <summary>
    /// The INERT projection: no acquisitions store is composed at all. Its empty record set is "not
    /// measured", never "measured zero" — <see cref="RecognitionAvailable"/> is false and consumers that
    /// would otherwise state an absence stay silent.
    /// </summary>
    public static readonly PendingAcquisitions None =
        new(AcquisitionStoreReadResult.Empty, recognitionAvailable: false);

    private readonly Dictionary<Guid, PendingAcquisitionRecord> _byCompany = [];
    private readonly HashSet<Guid> _evidenceIds = [];

    /// <param name="recognitionAvailable">
    /// Whether an acquisitions STORE was actually read. <c>false</c> is the inert projection
    /// (<see cref="None"/>): no store is composed, so "no company is pending" was never MEASURED and must
    /// not be rendered as though it were — "nothing is pending" and "we did not look" are different facts
    /// (CLAUDE.md: a defaulted zero must never render as a measured zero).
    /// </param>
    public PendingAcquisitions(AcquisitionStoreReadResult read, bool recognitionAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(read);

        // A read that FAILED is never "available", whatever the caller passes: its emptiness was not
        // measured, so no consumer may state an absence from it. The two ways to be unavailable — no store
        // composed, and a store that could not be enumerated — collapse to the same honest answer here.
        RecognitionAvailable = recognitionAvailable && read.Readable;
        Unreadable = read.Unreadable;

        foreach (var record in read.Records)
        {
            _evidenceIds.Add(record.EvidenceId);

            if (!_byCompany.TryGetValue(record.CompanyId, out var incumbent)
                || Beats(record, incumbent))
            {
                _byCompany[record.CompanyId] = record;
            }
        }

        Records = [.. _byCompany.Values
            .OrderBy(r => r.AnnouncedOnUtc)
            .ThenBy(r => r.CompanyId)];
    }

    /// <summary>
    /// True when this projection came from a real acquisitions-store read. False for <see cref="None"/>:
    /// the recognition path is not composed at all, so an EMPTY <see cref="Records"/> is "not measured",
    /// not "measured zero". Consumers that would otherwise state an absence (the report's
    /// <c>## Acquisitions pending</c> section) must stay silent instead.
    /// </summary>
    public bool RecognitionAvailable { get; }

    /// <summary>How many acquisitions files the store could not parse on the read behind this projection.</summary>
    public int Unreadable { get; }

    /// <summary>
    /// One record per company under a recognised pending acquisition, ordered by announcement then company
    /// id (AD-3). This is what the report's <c>## Acquisitions pending</c> section renders.
    /// </summary>
    public IReadOnlyList<PendingAcquisitionRecord> Records { get; }

    /// <summary>True when ANY company is under a recognised pending acquisition.</summary>
    public bool Any => _byCompany.Count > 0;

    /// <summary>
    /// The evidence ids a recognition was made ON. The scoring-assembly supersede
    /// (<c>acq-supersede-v1</c>) uses exactly this set: only the filing that WAS recognised has its
    /// keyword read replaced, never every filing of the company.
    /// </summary>
    public IReadOnlySet<Guid> RecognisedEvidenceIds => _evidenceIds;

    /// <summary>The company's governing record, or null when it is not under a recognised acquisition.</summary>
    public PendingAcquisitionRecord? For(Guid companyId) =>
        _byCompany.TryGetValue(companyId, out var record) ? record : null;

    /// <summary>
    /// The company's record AS OF <paramref name="instant"/> — null before the announcement. Used to stamp
    /// <c>companyStatusAtScoring</c>, so a snapshot taken before the announcement is never retro-labelled.
    /// </summary>
    public PendingAcquisitionRecord? At(Guid companyId, DateTimeOffset instant) =>
        _byCompany.TryGetValue(companyId, out var record) && instant >= record.AnnouncedOnUtc
            ? record
            : null;

    /// <summary>
    /// The company's status as of <paramref name="instant"/>: <c>PendingAcquisition</c> from the
    /// announcement onward, otherwise <c>null</c> meaning "this projection has nothing to say" — NEVER
    /// <c>Active</c>, because the curated seed status is not this type's to assert (null means not
    /// recorded, never a default).
    /// </summary>
    public CompanyStatus? StatusAt(Guid companyId, DateTimeOffset instant) =>
        At(companyId, instant) is not null ? CompanyStatus.PendingAcquisition : null;

    /// <summary>
    /// The announcement DATE of the company's recognised acquisition, or null. The efficacy exclusion
    /// compares on calendar dates (its whole vocabulary is <see cref="DateOnly"/>), so the conversion lives
    /// here rather than at each call site.
    /// </summary>
    public DateOnly? AnnouncedOn(Guid companyId) =>
        _byCompany.TryGetValue(companyId, out var record)
            ? DateOnly.FromDateTime(record.AnnouncedOnUtc.UtcDateTime)
            : null;

    /// <summary>
    /// The earliest announcement wins; ties break on the accession (ordinal) so the survivor never depends
    /// on store enumeration order (AD-3).
    /// </summary>
    private static bool Beats(PendingAcquisitionRecord candidate, PendingAcquisitionRecord incumbent)
    {
        var byDate = candidate.AnnouncedOnUtc.CompareTo(incumbent.AnnouncedOnUtc);
        return byDate != 0
            ? byDate < 0
            : string.CompareOrdinal(candidate.Accession, incumbent.Accession) < 0;
    }
}

/// <summary>
/// The read seam every consumer of <see cref="PendingAcquisitions"/> resolves through. A composition
/// without an acquisitions store registers the inert <see cref="NoPendingAcquisitionSource"/>, so every
/// consumer's pre-217 behaviour is reproduced exactly rather than guarded at each call site.
/// </summary>
public interface IPendingAcquisitionSource
{
    Task<PendingAcquisitions> GetAsync(CancellationToken ct);
}

/// <summary>
/// The inert default (the <c>NullOperatingCallSource</c> precedent): no acquisitions store is composed, so
/// nothing is pending. Registered by the Application library so every existing composition resolves.
/// </summary>
public sealed class NoPendingAcquisitionSource : IPendingAcquisitionSource
{
    public Task<PendingAcquisitions> GetAsync(CancellationToken ct) =>
        Task.FromResult(PendingAcquisitions.None);
}

/// <summary>
/// The real source: ONE store read per call, projected. Registered wherever an
/// <see cref="IAcquisitionStore"/> is composed.
/// </summary>
public sealed class AcquisitionStorePendingSource : IPendingAcquisitionSource
{
    private readonly IAcquisitionStore _store;

    public AcquisitionStorePendingSource(IAcquisitionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<PendingAcquisitions> GetAsync(CancellationToken ct) =>
        new(await _store.GetAllAsync(ct).ConfigureAwait(false));
}
