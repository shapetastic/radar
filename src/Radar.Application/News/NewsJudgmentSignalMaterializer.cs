using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Identity;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.News;

/// <summary>
/// SPEC 194 §1.2 — materializes the ONE grounded directional news signal a validated presentation-cohort
/// judgment owns, invoked by the Worker immediately AFTER the judgment pass and BEFORE the news-risk live
/// artifact is built. It performs no model call, re-ranks no candidates and reads no score.
/// </summary>
public interface INewsJudgmentSignalMaterializer
{
    /// <summary>
    /// Materializes signals for <paramref name="judgment"/>'s eligible records against the EXACT
    /// <paramref name="typing"/> instance the judge consumed. Never throws for one company's failure
    /// (it is counted and the pass continues); caller cancellation propagates.
    /// </summary>
    Task<NewsJudgmentSignalMaterializationSummary> MaterializeAsync(
        NewsJudgmentRunResult judgment, NewsTypingRunResult typing, CancellationToken ct);
}

/// <summary>
/// The concrete materializer (spec 194 §1.2).
///
/// <para>
/// <b>The defect this replaces, stated once so the class is legible without the spec.</b> Spec 191 gave news
/// a direction at EXTRACTION time by pairing the article being extracted with the company's LATEST admitted
/// judgment. By the live stage order that judgment had necessarily been produced from EARLIER articles and
/// had never read this one, so ONE judged call was inherited by every later headline the company collected —
/// multiplying a single verdict into N units of directional mass, N being the company's news volume. That is
/// the news-volume size proxy spec 191 existed to remove, wearing a provenance envelope that made it read as
/// grounded. §1.1 deleted that producer. This class is the replacement, and the whole correction is the
/// direction of the arrow: the JUDGMENT creates its own signal, anchored to the evidence the judgment
/// actually CITED, and no other article ever borrows it.
/// </para>
/// <para>
/// <b>Eligibility is all-or-nothing, deliberately.</b> A record contributes a direction only when it is from
/// the prospectively designated presentation cohort, completed as <c>Judged</c>, carries a directional
/// trajectory, cites at least one fact, and EVERY cited fact resolves through the stage-1 fact index to a
/// source observation which resolves through <see cref="NewsObservationEvidenceJoin"/> to exactly one news
/// evidence item belonging to the SAME company. A partially resolvable citation set is not full provenance:
/// scoring the resolvable part would silently rest a company-level verdict on a subset of the evidence that
/// produced it, and no consumer downstream could tell.  The named skip is the honest answer.
/// </para>
/// <para>
/// <b>One signal per judgment.</b> Not one per citation (that would re-multiply a single verdict,
/// differently) and not one per later article (that was the 191 defect). Its id is a pure function of the
/// judgment id, so re-running the same judgment is an idempotent no-op: an existing signal is
/// <c>AlreadyMaterialized</c>, never reviewed again and never overwritten.
/// </para>
/// <para>
/// <b>Knowledge time is NOW, never the judgment's.</b> <see cref="Signal.CreatedAtUtc"/> is the
/// materialization instant from the injected <see cref="TimeProvider"/>, even when the judgment was reused
/// from an earlier run: a reused old judgment did not create a durable signal in the past, and backdating it
/// would let a spec-136 replay at an earlier as-of see a signal Radar demonstrably did not have. The
/// consequence — that a judgment's direction reaches the score one run after it reaches the marker — is the
/// honest lag the spec chose over concealing it. <see cref="Signal.ObservedAtUtc"/> is a different fact and
/// comes from the anchor evidence's own publication/collection instant.
/// </para>
/// <para>
/// It reads two stores (<see cref="INewsObservationArchive"/>, <see cref="IEvidenceRepository"/>) ONCE per
/// pass, and only when at least one record survived the cheap gates, so a run whose judgments are all
/// <c>Mixed</c> touches neither. Nothing is persisted but the signal, its review and their durable mirror,
/// through the SAME repositories and file store collection writes to — never a second signal path.
/// </para>
/// </summary>
public sealed class NewsJudgmentSignalMaterializer : INewsJudgmentSignalMaterializer
{
    private readonly INewsObservationArchive _archive;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly ISignalRepository _signalRepository;
    private readonly ISignalReviewRepository _signalReviewRepository;
    private readonly ISignalFileStore _signalFileStore;
    private readonly ISignalReviewer _reviewer;
    private readonly NewsJudgmentOptions _options;
    private readonly NewsJudgmentReaderSet _judges;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NewsJudgmentSignalMaterializer> _logger;

    public NewsJudgmentSignalMaterializer(
        INewsObservationArchive archive,
        IEvidenceRepository evidenceRepository,
        ISignalRepository signalRepository,
        ISignalReviewRepository signalReviewRepository,
        ISignalFileStore signalFileStore,
        ISignalReviewer reviewer,
        NewsJudgmentOptions options,
        NewsJudgmentReaderSet judges,
        TimeProvider timeProvider,
        ILogger<NewsJudgmentSignalMaterializer> logger)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalReviewRepository);
        ArgumentNullException.ThrowIfNull(signalFileStore);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _archive = archive;
        _evidenceRepository = evidenceRepository;
        _signalRepository = signalRepository;
        _signalReviewRepository = signalReviewRepository;
        _signalFileStore = signalFileStore;
        _reviewer = reviewer;
        _options = options;
        _judges = judges;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The deterministic id of the signal ONE judgment materializes (spec 194 §1.2). A pure function of the
    /// versioned materializer identity and the judgment id, so the same judgment can never mint a second
    /// signal — in this process, in a later run, or after a crash between the review and the durable write.
    /// Public because it IS the idempotency contract, not an implementation detail: a caller asking whether
    /// a judgment has already been materialized asks this, never a heuristic over stored signals.
    /// </summary>
    public static Guid SignalIdFor(Guid judgmentId) => DeterministicGuid.FromCanonicalString(
        "radar:news-judgment-signal:"
            + NewsDirectionalSignalMetadata.JudgmentSignalVersionValue
            + ":"
            + judgmentId.ToString("D"));

    public async Task<NewsJudgmentSignalMaterializationSummary> MaterializeAsync(
        NewsJudgmentRunResult judgment, NewsTypingRunResult typing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(judgment);
        ArgumentNullException.ThrowIfNull(typing);
        ct.ThrowIfCancellationRequested();

        var skips = new Dictionary<NewsJudgmentSignalSkipReason, int>();

        // Resolved through the SHARED presentation-cohort resolution the leaders marker uses, so the cohort
        // whose direction is SCORED and the cohort whose marker is DISPLAYED cannot drift. Unresolvable is
        // a PASS-level fact, counted once — not once per record, which would report a single configuration
        // condition as N separate provenance failures.
        if (NewsJudgmentPresentationCohort.TryResolve(_options, _judges, typing) is not { } presentation)
        {
            skips[NewsJudgmentSignalSkipReason.PresentationCohortUnresolved] = 1;
            var unresolved = new NewsJudgmentSignalMaterializationSummary(
                JudgmentsConsidered: judgment.Judgments.Count,
                Eligible: 0,
                Materialized: 0,
                AlreadyMaterialized: 0,
                ValidationRejected: 0,
                WriteFailed: 0,
                Skips: skips);
            LogSummary(unresolved, cohortKey: null);
            return unresolved;
        }

        // Phase 1 — the cheap gates, which read only fields already in memory and touch no store. Their
        // purpose is not speed for its own sake: a run whose judgments are all Mixed/Unknown (an ordinary
        // outcome) must not hydrate the whole observation archive and evidence store to conclude it has
        // nothing to do.
        var candidates = new List<NewsJudgmentRecord>();
        foreach (var record in judgment.Judgments)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.Equals(record.CohortKey, presentation.CohortKey, StringComparison.Ordinal))
            {
                Count(skips, NewsJudgmentSignalSkipReason.NotPresentationCohort);
                continue;
            }

            if (record.Status != NewsJudgmentStatus.Judged)
            {
                Count(skips, NewsJudgmentSignalSkipReason.NotJudged);
                continue;
            }

            // Mixed and Unknown are honest non-directions, not defects: genuine both-ways evidence is not a
            // direction, and a judge that declined to call has not called. DirectionFor already encodes
            // that, so this gate has exactly one definition of "directional".
            if (record.BusinessTrajectory is not { } trajectory
                || NewsTrajectorySignalRules.DirectionFor(trajectory) is null)
            {
                Count(skips, NewsJudgmentSignalSkipReason.NonDirectionalTrajectory);
                continue;
            }

            if (record.TrajectoryFactIds is not { Count: > 0 })
            {
                // A news-judgment-v2 Judged record with a directional trajectory always cites at least one
                // fact (the v2 validator requires it). A v1 record simply never recorded the field, and
                // `null` there means NOT RECORDED — which is exactly why it cannot ground a direction.
                Count(skips, NewsJudgmentSignalSkipReason.NoTrajectoryFactIds);
                continue;
            }

            candidates.Add(record);
        }

        if (candidates.Count == 0)
        {
            var nothing = new NewsJudgmentSignalMaterializationSummary(
                JudgmentsConsidered: judgment.Judgments.Count,
                Eligible: 0,
                Materialized: 0,
                AlreadyMaterialized: 0,
                ValidationRejected: 0,
                WriteFailed: 0,
                Skips: skips);
            LogSummary(nothing, presentation.CohortKey);
            return nothing;
        }

        // Phase 2 — the two store reads, ONCE. The join is derived on read (spec 151's recorded precedent:
        // a pure function beats a materialized side index that can drift and has a staleness mode where the
        // index silently wins); nothing about it is persisted.
        var observations = await _archive.GetAllAsync(ct).ConfigureAwait(false);
        var allEvidence = await _evidenceRepository.GetAllAsync(ct).ConfigureAwait(false);
        var newsEvidence = allEvidence
            .Where(e => e.SourceType == EvidenceSourceType.NewsArticle)
            .ToList();
        var join = NewsObservationEvidenceJoin.Build(observations, newsEvidence);
        var evidenceById = new Dictionary<Guid, EvidenceItem>(newsEvidence.Count);
        foreach (var evidence in newsEvidence)
        {
            evidenceById[evidence.Id] = evidence;
        }

        var materialized = 0;
        var alreadyMaterialized = 0;
        var validationRejected = 0;
        var writeFailed = 0;

        foreach (var record in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await MaterializeOneAsync(
                        record, presentation, join, evidenceById, skips, ct)
                    .ConfigureAwait(false);
                switch (outcome)
                {
                    case MaterializationOutcome.Materialized:
                        materialized++;
                        break;
                    case MaterializationOutcome.AlreadyMaterialized:
                        alreadyMaterialized++;
                        break;
                    case MaterializationOutcome.ValidationRejected:
                        validationRejected++;
                        break;
                    case MaterializationOutcome.WriteFailed:
                        writeFailed++;
                        break;
                    case MaterializationOutcome.Skipped:
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unexpected company failure must not stop the remaining judgments from materializing.
                // Counted on its own named axis, never folded into a provenance reason: "Radar could not
                // ground this direction" and "Radar hit a bug" are different facts.
                Count(skips, NewsJudgmentSignalSkipReason.UnexpectedFailure);
                _logger.LogError(
                    ex,
                    "News-judgment signal materialization failed unexpectedly for company {CompanyId} "
                        + "(judgment {JudgmentId}); the remaining judgments are unaffected.",
                    record.CompanyId,
                    record.JudgmentId);
            }
        }

        var summary = new NewsJudgmentSignalMaterializationSummary(
            JudgmentsConsidered: judgment.Judgments.Count,
            Eligible: candidates.Count,
            Materialized: materialized,
            AlreadyMaterialized: alreadyMaterialized,
            ValidationRejected: validationRejected,
            WriteFailed: writeFailed,
            Skips: skips);
        LogSummary(summary, presentation.CohortKey);
        return summary;
    }

    /// <summary>
    /// Resolves ONE eligible judgment's full provenance chain and, if it is complete, creates its signal.
    /// Every early return records a NAMED reason in <paramref name="skips"/> — the method never returns a
    /// silent "no".
    /// </summary>
    private async Task<MaterializationOutcome> MaterializeOneAsync(
        NewsJudgmentRecord record,
        NewsJudgmentPresentationCohortResolution presentation,
        NewsObservationEvidenceJoin join,
        IReadOnlyDictionary<Guid, EvidenceItem> evidenceById,
        Dictionary<NewsJudgmentSignalSkipReason, int> skips,
        CancellationToken ct)
    {
        // The cited facts, in the record's OWN persisted order — which the excerpt rule below depends on, so
        // it is preserved rather than sorted here. Duplicates collapse onto their first appearance.
        var factIds = DistinctInOrder(record.TrajectoryFactIds!);
        var facts = new List<NewsTypingFactRef>(factIds.Count);
        foreach (var factId in factIds)
        {
            if (!presentation.ExtractorCohort.FactsById.TryGetValue(factId, out var factRef))
            {
                Count(skips, NewsJudgmentSignalSkipReason.UnresolvedFact);
                return MaterializationOutcome.Skipped;
            }

            // A cited fact belonging to another company would attach one company's verdict to another
            // company's article — the exact failure the join's own single-company rule exists to prevent,
            // checked here too because the stage-1 fact index is a DIFFERENT source from the join.
            if (factRef.CompanyId != record.CompanyId)
            {
                Count(skips, NewsJudgmentSignalSkipReason.CompanyMismatch);
                return MaterializationOutcome.Skipped;
            }

            facts.Add(factRef);
        }

        var observationIds = DistinctInOrder([.. facts.Select(f => f.ObservationId)]);
        var evidenceIds = new List<Guid>(observationIds.Count);
        foreach (var observationId in observationIds)
        {
            // The REVERSE join direction (spec 194 §1.2): a cited fact names its observation, and the
            // observation must resolve to exactly one news evidence item. Every fail-closed rule the join
            // already enforces — blank key, no match, two evidence items, two companies — surfaces here as
            // a null match, which is why there is no second, looser join anywhere in this class.
            if (join.TryMatchByObservation(observationId) is not { } match)
            {
                Count(skips, NewsJudgmentSignalSkipReason.UnresolvedObservation);
                return MaterializationOutcome.Skipped;
            }

            if (match.CompanyId != record.CompanyId)
            {
                Count(skips, NewsJudgmentSignalSkipReason.CompanyMismatch);
                return MaterializationOutcome.Skipped;
            }

            if (!evidenceById.ContainsKey(match.EvidenceId))
            {
                // Defence in depth: the join is built FROM this same evidence list, so a joined evidence id
                // is always present. Treated as an unresolved observation rather than trusted, because a
                // signal referencing evidence this pass cannot read is a provenance claim Radar cannot make.
                Count(skips, NewsJudgmentSignalSkipReason.UnresolvedObservation);
                return MaterializationOutcome.Skipped;
            }

            if (!evidenceIds.Contains(match.EvidenceId))
            {
                evidenceIds.Add(match.EvidenceId);
            }
        }

        // The deterministic PRIMARY ANCHOR: the most recently observed cited article, ties broken on the
        // lowest evidence id (AD-3). "Observed" is `PublishedAtUtc ?? CollectedAtUtc` — the same instant
        // ExtractedSignalMapper stamps as a signal's ObservedAtUtc, because EvidenceItem carries no
        // ObservedAtUtc of its own and inventing a second definition of an evidence item's real
        // publication/collection instant is how two halves of one provenance chain start disagreeing.
        var anchor = evidenceIds
            .Select(id => evidenceById[id])
            .OrderByDescending(ObservedInstant)
            .ThenBy(e => e.Id)
            .First();

        // IDEMPOTENCY, checked BEFORE the review and before any write: an already-materialized signal is
        // never reviewed a second time (which would append a second immutable review record for one signal)
        // and never overwritten (the stored signal is the record of what Radar actually did).
        var signalId = SignalIdFor(record.JudgmentId);
        if (await _signalRepository.GetByIdAsync(signalId, ct).ConfigureAwait(false) is not null)
        {
            return MaterializationOutcome.AlreadyMaterialized;
        }

        // The supporting excerpt is a CITATION, taken in the validated facts' persisted order and, within a
        // fact, its own persisted citation order. It must survive the mapper's excerpt-in-evidence guard
        // against the ANCHOR evidence specifically — the excerpt's job is to be traceable in the evidence
        // the signal points at, and a citation drawn from a sibling cited article would not be.
        var excerpt = FirstCitationSupportedBy(anchor, facts);
        if (excerpt is null)
        {
            Count(skips, NewsJudgmentSignalSkipReason.ExcerptNotInEvidence);
            return MaterializationOutcome.Skipped;
        }

        var trajectory = record.BusinessTrajectory!.Value;
        var direction = NewsTrajectorySignalRules.DirectionFor(trajectory)!.Value;
        var trajectoryToken = NewsJudgmentMarkerPolicy.TrajectoryToken(trajectory);

        var extracted = new ExtractedSignal(
            // From the JUDGMENT record, never a fresh resolver guess: the judgment already knows which
            // company it judged, and re-resolving from evidence text could disagree with it.
            CompanyMention: record.CompanyName,
            SignalType: SignalType.MediaAttention.ToString(),
            Direction: direction.ToString(),
            Strength: NewsTrajectorySignalRules.StrengthFor(
                record.Findings.Count,
                record.TypingCompleteness == NewsTypingCompleteness.Complete),
            Novelty: NewsTrajectorySignalRules.Novelty,
            Confidence: NewsTrajectorySignalRules.Confidence,
            SupportingExcerpt: excerpt,
            Reason: ReasonFor(trajectoryToken),
            MetadataJson: NewsDirectionalSignalMetadata.ComposeJudgmentSignal(
                judgmentId: record.JudgmentId,
                judgmentCohortKey: record.CohortKey,
                trajectoryToken: trajectoryToken,
                trajectoryFactIds: factIds,
                sourceObservationIds: observationIds,
                citedEvidenceIds: evidenceIds));

        // CreatedAtUtc is NOW, never record.CreatedAtUtc — see the class remarks. A reused old judgment did
        // not create a durable signal in the past, and backdating would let replay see a signal Radar did
        // not yet have.
        var mapping = ExtractedSignalMapper.ToSignal(extracted, anchor, _timeProvider.GetUtcNow());
        if (!mapping.IsValid)
        {
            _logger.LogWarning(
                "Judgment-derived news signal for company {CompanyId} (judgment {JudgmentId}) failed "
                    + "validation and was NOT created: {Errors}",
                record.CompanyId,
                record.JudgmentId,
                string.Join("; ", mapping.Errors));
            return MaterializationOutcome.ValidationRejected;
        }

        // The id is set BEFORE review so that review.SignalId == signal.Id and the file store's
        // review→signal provenance guard holds. The mapper mints a fresh Guid; the deterministic id is what
        // makes this pass idempotent, so it replaces that rather than riding beside it.
        var signal = mapping.Signal! with
        {
            Id = signalId,
            CompanyId = record.CompanyId,
        };

        var reviewed = await _reviewer.ReviewAsync(signal, anchor, ct).ConfigureAwait(false);

        // The SAME repositories and durable store the collection pass writes through — never a second
        // signal path.
        await _signalRepository.AddAsync(reviewed.ReviewedSignal, ct).ConfigureAwait(false);
        await _signalReviewRepository.AddAsync(reviewed.Review, ct).ConfigureAwait(false);
        var durable = await _signalFileStore
            .WriteAsync(reviewed.ReviewedSignal, reviewed.Review, ct)
            .ConfigureAwait(false);

        if (durable.Outcome == DurableWriteOutcome.Failed)
        {
            // SPEC 193's truthful-outcome rule: counted, NOT reported as materialized. The in-memory index
            // keeps it (matching what the collection pass does with its own signals, so this process is
            // consistent with itself), but nothing reached disk — so the accrued history does not contain
            // it and the next process may safely retry, because no durable signal with that id exists to
            // collide with. No retry queue is added; the failure is recorded, not repaired.
            _logger.LogWarning(
                "The judgment-derived news signal for company {CompanyId} (judgment {JudgmentId}) could "
                    + "NOT be durably persisted. It is in this process's in-memory index only; the accrued "
                    + "signal history does not contain it and a later run may materialize it again.",
                record.CompanyId,
                record.JudgmentId);
            return MaterializationOutcome.WriteFailed;
        }

        return MaterializationOutcome.Materialized;
    }

    /// <summary>
    /// The first citation, in the validated facts' persisted order and each fact's own persisted citation
    /// order, that the mapper's excerpt guard accepts against <paramref name="anchor"/>. Returns
    /// <c>null</c> when none does — the caller then records <c>excerpt-not-in-evidence</c> and creates no
    /// signal, because a signal whose supporting text cannot be found in the evidence it points at is not
    /// provenance.
    /// </summary>
    private static string? FirstCitationSupportedBy(
        EvidenceItem anchor, IReadOnlyList<NewsTypingFactRef> facts)
    {
        foreach (var fact in facts)
        {
            foreach (var citation in fact.Fact.Citations)
            {
                var trimmed = (citation ?? string.Empty).Trim();
                if (trimmed.Length > 0
                    && ExtractedSignalMapper.IsExcerptSupportedByEvidence(anchor, trimmed))
                {
                    return trimmed;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The signal's reason: short, factual and advice-free (the house output-language rule). It names the
    /// judged trajectory and says where the direction came from, so the persisted record explains itself
    /// without a reader having to open the judgment store.
    /// </summary>
    private static string ReasonFor(string trajectoryToken) =>
        "Third-party news coverage (media attention); direction from the stage-2 news judgment that cited "
            + "this article — judged business trajectory: " + trajectoryToken;

    /// <summary>The evidence's real publication/collection instant — the same rule the signal mapper applies.</summary>
    private static DateTimeOffset ObservedInstant(EvidenceItem evidence) =>
        evidence.PublishedAtUtc ?? evidence.CollectedAtUtc;

    /// <summary>Distinct, preserving first-appearance order (AD-3 — never a hash-set enumeration order).</summary>
    private static IReadOnlyList<Guid> DistinctInOrder(IReadOnlyList<Guid> ids)
    {
        var seen = new HashSet<Guid>();
        var ordered = new List<Guid>(ids.Count);
        foreach (var id in ids)
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }

    private static void Count(
        Dictionary<NewsJudgmentSignalSkipReason, int> skips, NewsJudgmentSignalSkipReason reason) =>
        skips[reason] = skips.GetValueOrDefault(reason) + 1;

    /// <summary>ONE aggregated line per pass (the spec-145 aggregation precedent), never one per judgment.</summary>
    private void LogSummary(NewsJudgmentSignalMaterializationSummary summary, string? cohortKey)
    {
        var skipDetail = summary.DescribeSkips();
        _logger.LogInformation(
            "Judgment-derived news signals: {Considered} judgment(s) considered, {Eligible} eligible, "
                + "{Materialized} materialized, {AlreadyMaterialized} already materialized, "
                + "{ValidationRejected} validation-rejected, {WriteFailed} not durably persisted; "
                + "skips: {Skips}. Presentation cohort: {CohortKey}.",
            summary.JudgmentsConsidered,
            summary.Eligible,
            summary.Materialized,
            summary.AlreadyMaterialized,
            summary.ValidationRejected,
            summary.WriteFailed,
            skipDetail.Length > 0 ? skipDetail : "none",
            cohortKey ?? "(unresolved)");
    }

    /// <summary>
    /// What one candidate produced. <see cref="Skipped"/> means a NAMED provenance reason was already
    /// recorded by the resolution step, so the caller adds nothing further — the skip vocabulary is the
    /// single account of why, and duplicating it into this enum would create a second one.
    /// </summary>
    private enum MaterializationOutcome
    {
        Skipped,
        Materialized,
        AlreadyMaterialized,
        ValidationRejected,
        WriteFailed,
    }
}
