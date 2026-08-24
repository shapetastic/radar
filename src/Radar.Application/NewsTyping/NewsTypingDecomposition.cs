using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>The JSON side of the attention-decomposition artifact (spec 181 §5) — one document per run day, mirrored by the rendered markdown.</summary>
public sealed record NewsTypingDecompositionDocument(
    string SchemaVersion,
    Guid? RunId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    string Caveat,
    IReadOnlyList<string> Readers,
    bool? CaptureProvenThisRun,
    IReadOnlyList<NewsTypingDecompositionCompany> Companies,
    int ObservationsWithoutCompany,
    DateTimeOffset GeneratedAtUtc)
{
    /// <summary>
    /// The decomposition schema tag. Bumped to <c>-v2</c> by spec 186 §2 for the ADDITIVE per-cohort
    /// <see cref="NewsTypingDecompositionCohort.RetryExhausted"/> count, and to <c>-v3</c> by spec 187 for
    /// the additive <see cref="NewsTypingDecompositionCohort.ReservedWithoutOutcome"/> counter (§3) AND —
    /// the reason a bump was owed rather than merely tidy — the CORRECTED meaning of
    /// <see cref="NewsTypingDecompositionCohort.UntypedRemaining"/> (§4), which no longer double-counts an
    /// exhausted observation as backlog.
    /// <para>
    /// Spec 187 §2's candidate/general selection counters
    /// (<see cref="NewsTypingDecompositionCohort.CandidatePrioritySelected"/> /
    /// <see cref="NewsTypingDecompositionCohort.GeneralSelected"/>) have now LANDED in this SAME v3
    /// document: the spec attributes the single bump jointly to §2 and §4, so the tag stays at v3 rather
    /// than moving twice for one release.
    /// </para>
    /// Readers are by-NAME, so a v1/v2 consumer reads a v3 document unchanged (asserted), and existing v1/v2
    /// artifacts on disk stay readable and untouched.
    /// </summary>
    public const string CurrentSchemaVersion = "news-typing-decomposition-v3";

    /// <summary>The §5 caveat, VERBATIM per the spec — carried by every decomposition artifact.</summary>
    public const string Caveat181 =
        "Event typing describes what coverage was about. It is not a sentiment, a risk assessment or a "
            + "score input, and a type distribution is not a recommendation.";
}

/// <summary>
/// One company's decomposition: the window's raw observation count, the honest completeness marking (a
/// company with unproven capture or a typing backlog is INCOMPLETE, never silently partial), and every
/// reader × capture-mode cohort's own breakdown — side by side, never merged.
/// </summary>
public sealed record NewsTypingDecompositionCompany(
    Guid CompanyId,
    string? Ticker,
    int ObservationsInWindow,
    bool Incomplete,
    IReadOnlyList<string> IncompleteReasons,
    IReadOnlyList<NewsTypingDecompositionCohort> Cohorts);

/// <summary>
/// One (reader cohort × capture mode) breakdown for one company. Capture modes carry different epistemic
/// weight (spec 177), so they are never pooled — a <c>LegacyHeadlineOnly</c> distribution beside a
/// <c>ProspectiveRss</c> one is the honesty the separation exists for. <c>RetryExhausted</c> (spec 186 §2)
/// counts this cohort's in-window observations that have spent their typing attempts and left selection:
/// they are a PERMANENT hole in the cohort, not a backlog that a later run drains.
///
/// <para>
/// <b>The partition (spec 187 §4).</b> <c>ObservationsTyped + ObservationsInsufficientContent</c> (the
/// completed outcomes, split by status) <c>+ UntypedRemaining + RetryExhausted</c> reconciles EXACTLY to
/// this cohort's eligible in-window observation population. The four sets are DISJOINT: before 187 an
/// exhausted observation was counted as backlog AND as exhausted, so the row over-stated recoverable work
/// and the company rendered both incomplete reasons for one observation. <c>UntypedRemaining</c> now means
/// "still eligible for a future first attempt or retry" — work a later run can actually drain.
/// </para>
/// <para>
/// <c>ReservedWithoutOutcome</c> (spec 187 §3) is a DIAGNOSTIC, deliberately NOT a partition member: it
/// counts the durable pre-call attempt RESERVATIONS over this cohort's in-window observations that hold no
/// linked outcome record (a crash, a cancellation, or an outcome write that returned <c>false</c>). Such an
/// observation is still either untyped-eligible or exhausted, so it is already counted above; this column
/// says WHY a hosted call was spent with nothing to show for it, rather than letting the loss look like
/// ordinary backlog.
/// </para>
/// <para>
/// <c>CandidatePrioritySelected</c> and <c>GeneralSelected</c> (spec 187 §2) are likewise DIAGNOSTICS, not
/// partition members: they count how many of THIS company's in-window observations this pass selected via
/// the round-robin judgment-candidate lane and via the global first-attempt queue respectively. A selected
/// observation is already counted above (as a completed outcome, as untyped-eligible or as exhausted);
/// these two columns say WHICH lane paid for the call, so "the leaders we were about to judge were typed
/// first" is visible rather than asserted. The cohort's pass-wide totals (window and backlog alike) ride
/// <c>NewsTypingCohortRunResult</c>; both are projected from the SAME per-observation lane record.
/// </para>
/// </summary>
public sealed record NewsTypingDecompositionCohort(
    string ReaderName,
    string Provider,
    string ModelId,
    string CohortKey,
    NewsObservationCaptureMode CaptureMode,
    int ObservationsTyped,
    int ObservationsInsufficientContent,
    int UntypedRemaining,
    int FamilyCount,
    IReadOnlyList<NewsTypingDecompositionTypeRow> Types,
    int RetryExhausted,
    int ReservedWithoutOutcome = 0,
    int CandidatePrioritySelected = 0,
    int GeneralSelected = 0);

/// <summary>
/// One event type's row: how many typed observations carried it as <c>DerivedPrimaryType</c>, the distinct
/// publisher breadth among them, and the same-event FAMILY count beside the raw count — so 40 syndicated
/// copies of one financing story render as one family. The family count shares the row's basis: only
/// families containing one of these observations' facts of this type are counted, never a cohort family
/// that merely mentions the type from observations primary-typed elsewhere.
/// </summary>
public sealed record NewsTypingDecompositionTypeRow(
    NewsEventType EventType,
    int ObservationCount,
    int PublisherBreadth,
    int FamilyCount);

/// <summary>
/// The typing artifact write seam (spec 181 §5), implemented in Infrastructure over the shared graceful
/// writer: <c>{root}/live/attention-decomposition-{asOfDate}.md|.json</c>, plus a NAMED failed artifact —
/// a typing failure never rolls back or relabels the already-durable Radar run.
/// </summary>
public interface INewsTypingArtifactStore
{
    Task WriteDecompositionAsync(
        string asOfDateToken,
        string markdown,
        NewsTypingDecompositionDocument document,
        CancellationToken ct);

    Task WriteFailedAsync(string asOfDateToken, string reason, CancellationToken ct);
}
