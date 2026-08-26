using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.News;

/// <summary>
/// The prospectively designated presentation cohort whose judgments may contribute DIRECTION (spec 191 §3).
/// Resolved once at composition, exactly as the leaders marker resolves it, so the scored cohort and the
/// displayed cohort cannot drift apart.
/// </summary>
public sealed record NewsDirectionalReadOptions(string PresentationCohortKey);

/// <summary>
/// The deterministic mapping from a judged business trajectory to a signal direction (spec 191 §2). Small,
/// separately testable and visibly constant: <c>Improving → Positive</c>, <c>Deteriorating → Negative</c>,
/// and <c>Mixed</c>/<c>Unknown</c> → no direction at all (genuine both-ways evidence is not a direction, and
/// a judge that declined to call has not called).
/// <para>
/// ⚠ These magnitudes are the news analogue of the AI filing read's <c>str</c>/<c>nov</c>/<c>minconf</c>, but
/// unlike those they are hashed into NO fingerprint — see <see cref="INewsDirectionalReadSource"/>'s remarks.
/// </para>
/// </summary>
internal static class NewsTrajectorySignalRules
{
    /// <summary>The Neutral <c>MediaAttention</c> strength the extractor has always emitted — the floor a directional read builds on.</summary>
    internal const int BaseStrength = 4;

    /// <summary>The maximum number of judge findings that contribute to strength.</summary>
    internal const int MaxFindingContribution = 3;

    /// <summary>The bonus for a judgment whose stage-1 typing was COMPLETE (nothing deferred, nothing failed).</summary>
    internal const int CompleteTypingBonus = 1;

    /// <summary>The direction, or <c>null</c> when the trajectory carries none.</summary>
    internal static SignalDirection? DirectionFor(NewsJudgmentTrajectory trajectory) => trajectory switch
    {
        NewsJudgmentTrajectory.Improving => SignalDirection.Positive,
        NewsJudgmentTrajectory.Deteriorating => SignalDirection.Negative,
        NewsJudgmentTrajectory.Mixed => null,
        NewsJudgmentTrajectory.Unknown => null,
        _ => null,
    };

    /// <summary>
    /// Strength, scaled by the judge's finding count and typing completeness (spec 191 §2), clamped to the
    /// domain range. Range 4–8: the base is today's Neutral strength, so a directional read is never WEAKER
    /// than the attention event it replaces.
    /// <para>
    /// A supportive <c>Improving</c> read legitimately carries ZERO findings — spec 185's findings are
    /// CHALLENGE-only, so an improving trajectory has nothing to list — and therefore lands at the base
    /// strength unless typing was complete. That is intended: an unchallenged improving read is a real but
    /// modest input, not a thesis on its own.
    /// </para>
    /// </summary>
    internal static int StrengthFor(int findingCount, bool typingComplete) => Math.Clamp(
        BaseStrength
            + Math.Min(Math.Max(findingCount, 0), MaxFindingContribution)
            + (typingComplete ? CompleteTypingBonus : 0),
        1,
        10);
}

/// <summary>
/// The concrete <see cref="INewsDirectionalReadSource"/> (spec 191): it joins one news evidence item back to
/// its point-in-time observation (<see cref="NewsObservationEvidenceJoin"/>), looks the observation's company
/// up in the ADMITTED judgment index, and maps the judged business trajectory to a signal direction.
/// <para>
/// It lives in <c>Radar.Application.News</c> — beside <see cref="NewsObservationMigration"/>, which likewise
/// reads both evidence and observations — and NOT in <c>Radar.Application.SignalExtraction</c>, whose type
/// graph two reflection guards forbid from reaching the observation archive and the news-risk subsystem. The
/// seam it implements carries Domain types only, so the guards stay exactly as strict as they were.
/// </para>
/// <para>
/// <b>Admission (spec 191 §3), every condition required.</b> A judgment contributes direction only when its
/// <see cref="NewsJudgmentRecord.CohortKey"/> is the configured presentation cohort (ordinal), its status is
/// <see cref="NewsJudgmentStatus.Judged"/>, it carries a non-null trajectory, and it was created at or before
/// the PREPARED run instant. LATEST admitted judgment per company wins, ties break on the lowest
/// <see cref="NewsJudgmentRecord.JudgmentId"/> (AD-3).
/// </para>
/// <para>
/// <b>Point-in-time honesty, and per-RUN freshness.</b> The index is built by <see cref="PrepareAsync"/> at
/// the run's own captured <c>asOfUtc</c> — never from a clock read of this class's own — and the predicate is
/// <c>CreatedAtUtc &lt;= asOfUtc</c> (spec 136). Preparing again with the SAME instant is a no-op; preparing
/// with a DIFFERENT instant REBUILDS. That rebuild is load-bearing: with <c>Radar:RunOnce=false</c> the
/// Worker runs the pipeline repeatedly in ONE process and each run's post-pipeline judgment step writes new
/// judgments, so a once-per-instance index would freeze the news read at whatever existed during the first
/// run. <see cref="TryReadAsync"/> before any prepare FAILS CLOSED (returns <c>null</c>) rather than building
/// implicitly, because an implicit build would invent an as-of instant of its own.
/// </para>
/// <para>
/// <b>Provenance is mandatory.</b> A read is produced only when the judgment id, the cohort key and the
/// matched observation id can ALL be recorded; otherwise the source returns <c>null</c> and the extractor
/// emits exactly today's Neutral signal.
/// </para>
/// </summary>
public sealed class NewsDirectionalReadSource : INewsDirectionalReadSource
{
    private readonly INewsObservationArchive _archive;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly INewsJudgmentStore _judgmentStore;
    private readonly NewsDirectionalReadOptions _options;
    private readonly ILogger<NewsDirectionalReadSource> _logger;

    // Guards index construction. Deliberately not disposed (the source is not IDisposable): it is a container
    // singleton whose lifetime is the process, exactly as FileSignalStore's hydration gate is.
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    // The CURRENT run's prepared state, or null when never prepared. Published as ONE immutable reference so
    // a concurrent reader can never observe a join from one run beside an admitted-judgment index from
    // another; volatile so that publication is visible without taking the gate on the read path.
    private volatile PreparedIndex? _index;

    public NewsDirectionalReadSource(
        INewsObservationArchive archive,
        IEvidenceRepository evidenceRepository,
        INewsJudgmentStore judgmentStore,
        NewsDirectionalReadOptions options,
        ILogger<NewsDirectionalReadSource> logger)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(judgmentStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _archive = archive;
        _evidenceRepository = evidenceRepository;
        _judgmentStore = judgmentStore;
        _options = options;
        _logger = logger;
    }

    public async Task PrepareAsync(DateTimeOffset asOfUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Idempotent within one run: re-preparing at the same instant must not re-hydrate the stores.
        if (_index is { } current && current.AsOfUtc == asOfUtc)
        {
            return;
        }

        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_index is { } existing && existing.AsOfUtc == asOfUtc)
            {
                return;
            }

            var observations = await _archive.GetAllAsync(ct).ConfigureAwait(false);
            var allEvidence = await _evidenceRepository.GetAllAsync(ct).ConfigureAwait(false);
            var newsEvidence = allEvidence
                .Where(e => e.SourceType == EvidenceSourceType.NewsArticle)
                .ToList();
            var judgments = await _judgmentStore.GetAllAsync(ct).ConfigureAwait(false);

            var join = NewsObservationEvidenceJoin.Build(observations, newsEvidence);
            var admittedRecords = BuildAdmittedIndex(judgments, asOfUtc, out var admittedByCompany);
            _index = new PreparedIndex(asOfUtc, join, admittedByCompany);

            // ONE aggregated line per prepare (the spec-145 aggregation precedent): the spec-191 §1
            // acceptance criterion is that the join rate is REPORTED per run, not merely computed.
            _logger.LogInformation(
                "News directional read index prepared: join joined={Joined}, unjoined-no-match={NoMatch}, "
                    + "unjoined-ambiguous={Ambiguous} over {Observations} archived observation(s) and "
                    + "{NewsEvidence} news evidence item(s); {AdmittedJudgments} admitted judgment(s) "
                    + "covering {Companies} company/companies from presentation cohort {CohortKey} "
                    + "as of {AsOfUtc}.",
                join.Counts.Joined,
                join.Counts.UnjoinedNoMatch,
                join.Counts.UnjoinedAmbiguous,
                observations.Count,
                newsEvidence.Count,
                admittedRecords,
                admittedByCompany.Count,
                _options.PresentationCohortKey,
                asOfUtc.ToString("O", CultureInfo.InvariantCulture));
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public Task<NewsDirectionalRead?> TryReadAsync(EvidenceItem evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(evidence);

        return Task.FromResult(Read(evidence));
    }

    private NewsDirectionalRead? Read(EvidenceItem evidence)
    {
        if (evidence.SourceType != EvidenceSourceType.NewsArticle)
        {
            return null;
        }

        // FAIL CLOSED: never build implicitly (see the interface remarks).
        if (_index is not { } index)
        {
            return null;
        }

        if (index.Join.TryMatch(evidence.Id) is not { } match)
        {
            return null;
        }

        if (!index.AdmittedByCompany.TryGetValue(match.CompanyId, out var admitted))
        {
            return null;
        }

        if (NewsTrajectorySignalRules.DirectionFor(admitted.Trajectory) is not { } direction)
        {
            return null;
        }

        // Provenance is MANDATORY: without all three the signal is not emitted directionally (spec 191 §2).
        // DEFENCE IN DEPTH, not dead code — do not delete it as unreachable. FileNewsJudgmentStore already
        // skips a malformed record at hydration, but this seam is defined over the INTERFACE: an alternative
        // store, or an in-memory double, carries no such guarantee. A directional signal that cannot be
        // traced back to its judgment and its observation is precisely what spec 191 forbids, so the check
        // belongs where the signal is minted rather than only where one implementation reads files.
        if (admitted.JudgmentId == Guid.Empty
            || match.ObservationId == Guid.Empty
            || string.IsNullOrWhiteSpace(admitted.CohortKey))
        {
            return null;
        }

        return new NewsDirectionalRead(
            Direction: direction,
            Strength: NewsTrajectorySignalRules.StrengthFor(
                admitted.FindingCount, admitted.TypingComplete),
            ObservationId: match.ObservationId,
            JudgmentId: admitted.JudgmentId,
            JudgmentCohortKey: admitted.CohortKey,
            TrajectoryToken: NewsJudgmentMarkerPolicy.TrajectoryToken(admitted.Trajectory));
    }

    /// <summary>
    /// The admitted-judgment index: presentation cohort + <c>Judged</c> + a non-null trajectory +
    /// <c>CreatedAtUtc &lt;= asOfUtc</c>, latest per company, ties on the lowest judgment id (AD-3).
    /// </summary>
    /// <returns>How many records were ADMITTED (before the latest-wins collapse), for the run report.</returns>
    private int BuildAdmittedIndex(
        IReadOnlyList<NewsJudgmentRecord> judgments,
        DateTimeOffset asOfUtc,
        out Dictionary<Guid, AdmittedJudgment> index)
    {
        index = [];
        var admitted = 0;
        foreach (var record in judgments)
        {
            if (!string.Equals(record.CohortKey, _options.PresentationCohortKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (record.Status != NewsJudgmentStatus.Judged || record.BusinessTrajectory is not { } trajectory)
            {
                continue;
            }

            if (record.CreatedAtUtc > asOfUtc)
            {
                continue;
            }

            admitted++;
            var candidate = new AdmittedJudgment(
                JudgmentId: record.JudgmentId,
                CohortKey: record.CohortKey,
                Trajectory: trajectory,
                FindingCount: record.Findings.Count,
                TypingComplete: record.TypingCompleteness == NewsTypingCompleteness.Complete,
                CreatedAtUtc: record.CreatedAtUtc);

            if (!index.TryGetValue(record.CompanyId, out var existing) || Wins(candidate, existing))
            {
                index[record.CompanyId] = candidate;
            }
        }

        return admitted;
    }

    private static bool Wins(AdmittedJudgment candidate, AdmittedJudgment existing) =>
        candidate.CreatedAtUtc > existing.CreatedAtUtc
            || (candidate.CreatedAtUtc == existing.CreatedAtUtc
                && candidate.JudgmentId.CompareTo(existing.JudgmentId) < 0);

    /// <summary>ONE run's immutable prepared state, published atomically.</summary>
    private sealed record PreparedIndex(
        DateTimeOffset AsOfUtc,
        NewsObservationEvidenceJoin Join,
        Dictionary<Guid, AdmittedJudgment> AdmittedByCompany);

    private sealed record AdmittedJudgment(
        Guid JudgmentId,
        string CohortKey,
        NewsJudgmentTrajectory Trajectory,
        int FindingCount,
        bool TypingComplete,
        DateTimeOffset CreatedAtUtc);
}
