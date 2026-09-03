using System.Globalization;

using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The JSON side of the attention-decomposition artifact (spec 181 §5) — one document per TYPING PASS, named
/// by as-of instant + run id (spec 208; it was one per run DAY until the 2026-09-01 same-day overwrite),
/// mirrored by the rendered markdown.
/// <para>
/// Spec 189 §3 adds the run's CAPTURE INFLOW (<see cref="NewsObservationBatchId"/> +
/// <see cref="ObservationsCapturedThisRun"/>) and the AUTHORITATIVE pass-wide
/// <see cref="ReaderSummaries"/>, all TRAILING and nullable so a v1–v3 reader is unaffected. Inflow beside
/// spend is the number the capacity decision turns on: the 2026-08-24 baseline captured 252 new observations
/// against a 200-call budget, and no artifact said so.
/// </para>
/// </summary>
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
    DateTimeOffset GeneratedAtUtc,
    // Spec 189 §3: the spec-177 observation batch this run captured, and how many NEW observation files it
    // wrote. Both are NULLABLE and fail closed: a standalone (no-run) invocation has no batch, and an
    // unreadable/absent batch manifest records `null` — NEVER a timestamp-derived estimate, which would look
    // like a measurement while being a guess.
    Guid? NewsObservationBatchId = null,
    int? ObservationsCapturedThisRun = null,
    // Spec 189 §3: one AUTHORITATIVE pass-wide summary per extractor cohort. A reviewer must not have to
    // reconstruct a pass-wide call budget by summing the current window's company rows.
    IReadOnlyList<NewsTypingDecompositionReaderSummary>? ReaderSummaries = null)
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
    /// <para>
    /// Bumped to <c>-v4</c> by spec 189 §3 for the ADDITIVE capture-inflow fields, the authoritative pass-wide
    /// <see cref="ReaderSummaries"/> and the three per-cohort diagnostics
    /// (<see cref="NewsTypingDecompositionCohort.RetrySelected"/>,
    /// <see cref="NewsTypingDecompositionCohort.ProviderCallsAttempted"/>,
    /// <see cref="NewsTypingDecompositionCohort.RetryableFailuresThisRun"/>). Nothing existing changed
    /// meaning — unlike the v3 bump, which also corrected <c>UntypedRemaining</c>.
    /// </para>
    /// Readers are by-NAME, so a v1/v2/v3 consumer reads a v4 document unchanged (asserted), and existing
    /// v1–v3 artifacts on disk stay readable and untouched.
    /// </summary>
    public const string CurrentSchemaVersion = "news-typing-decomposition-v4";

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
/// <para>
/// <b>Spec 189 §3 completes the picture with three more DIAGNOSTICS</b> (again, not partition members).
/// <c>RetrySelected</c> is the THIRD lane, and it had been missing: the live 2026-08-24 pass allocated 100
/// candidate + 99 general + 1 retry, and without a retry column that reads as an unused slot rather than as
/// the retry it was. <c>ProviderCallsAttempted</c> is what the pass actually SPENT on this company —
/// deliberately a different number from the selection counts, because a refused attempt reservation is a
/// selection that never became a call. <c>RetryableFailuresThisRun</c> counts the in-window observations
/// that ended this pass with a provider/parse/validation, reservation-refusal or unpersisted-outcome failure
/// and have NOT exhausted their budget — so "degraded today, still eligible" is named separately from both
/// ordinary backlog and permanent exhaustion.
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
    int GeneralSelected = 0,
    int RetrySelected = 0,
    int ProviderCallsAttempted = 0,
    int RetryableFailuresThisRun = 0);

/// <summary>
/// Spec 189 §3: ONE pass-wide summary per extractor cohort — the durable artifact equivalent of the
/// bounded/final log totals, and the AUTHORITATIVE view of how the per-run hosted-call budget was allocated
/// and spent.
/// <para>
/// <b>These totals are pass-wide; the per-company rows are a WINDOW statement.</b> They may legitimately
/// differ, and the difference is never silently called equality. Named reasons: a selected observation from
/// the legacy BACKLOG sits outside the checkpoint window and appears in no company row; and an observation
/// with no company attribution appears in no company section at all (the document's
/// <c>ObservationsWithoutCompany</c> counts those). When a reviewer needs "what did this pass spend", this
/// record answers it; when they need "how well is this company covered", the company rows do.
/// </para>
/// <para>
/// <c>RetrySelected + CandidatePrioritySelected + GeneralSelected</c> is what the queue ALLOCATED (the three
/// lanes are disjoint, so no observation is counted twice); <c>ProviderCallsAttempted</c> is what was
/// actually spent after durable-reservation races/refusals. The distinction is intentional — equating them
/// would hide exactly the storage failures <c>ReservationsRefused</c> / <c>OutcomeWritesFailed</c> /
/// <c>ReservedWithoutOutcome</c> exist to surface.
/// </para>
/// <para>
/// <c>UntypedRemaining</c> keeps spec 187 §4's meaning — STILL ELIGIBLE work a later run can drain, with
/// exhausted observations excluded and unpersisted outcomes included. Diagnostics only: nothing here is
/// hashed into any cohort key, fact, family, score or fingerprint.
/// </para>
/// </summary>
public sealed record NewsTypingDecompositionReaderSummary(
    string ReaderName,
    string Provider,
    string ModelId,
    string CohortKey,
    int RetrySelected,
    int CandidatePrioritySelected,
    int GeneralSelected,
    int ProviderCallsAttempted,
    int CompletedOutcomesPersisted,
    int ProviderFailures,
    int ParseFailures,
    int ValidationFailures,
    int ReservationsRefused,
    int OutcomeWritesFailed,
    int RetryExhausted,
    int ReservedWithoutOutcome,
    int UntypedRemaining);

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
/// The ONE naming rule for the attention-decomposition artifact (spec 208): the durable identity is the
/// run's as-of INSTANT plus its run id, never the as-of date alone. Pure and deterministic so the live pair,
/// the FAILED variant and the tests cannot drift from each other.
/// <para>
/// Shape: <c>attention-decomposition-{yyyyMMdd'T'HHmmss'Z'}-{runId:D}</c> (UTC, invariant culture, no
/// milliseconds — the family-checkpoint / observation-archive instant convention, so the artifacts sort and
/// correlate with the run records on sight). When the run id is absent the name is instant-only; the STORE
/// owns the Warning for that case, this helper only names. Until spec 208 the name was
/// <c>attention-decomposition-{yyyy-MM-dd}</c>, which let the 2026-09-01 21:46Z run overwrite the 02:50Z
/// run's artifact (run 3 of spec 200 §5).
/// </para>
/// </summary>
public static class NewsTypingArtifactNames
{
    public const string Prefix = "attention-decomposition-";

    public const string FailedSuffix = "-FAILED";

    public const string InstantFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The as-of instant as the pinned <c>yyyyMMdd'T'HHmmss'Z'</c> token (UTC, invariant culture).</summary>
    public static string Instant(DateTimeOffset asOfUtc) =>
        asOfUtc.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>attention-decomposition-{instant}-{runId:D}</c>, or <c>attention-decomposition-{instant}</c> when
    /// <paramref name="runId"/> is absent. No extension.
    /// </summary>
    public static string BaseName(DateTimeOffset asOfUtc, Guid? runId) =>
        runId is { } id
            ? Prefix + Instant(asOfUtc) + "-" + id.ToString("D")
            : Prefix + Instant(asOfUtc);

    /// <summary><see cref="BaseName"/> + <c>-FAILED</c>. No extension.</summary>
    public static string FailedBaseName(DateTimeOffset asOfUtc, Guid? runId) =>
        BaseName(asOfUtc, runId) + FailedSuffix;
}

/// <summary>
/// The typing artifact write seam (spec 181 §5), implemented in Infrastructure over the shared graceful
/// writer: <c>{root}/live/{NewsTypingArtifactNames.BaseName}.md|.json</c>, plus a NAMED failed artifact at
/// <c>{root}/live/{NewsTypingArtifactNames.FailedBaseName}.md</c> — a typing failure never rolls back or
/// relabels the already-durable Radar run. Both writes take the run's as-of instant and run id EXPLICITLY
/// (spec 208) so two same-day runs can never share a path.
/// </summary>
public interface INewsTypingArtifactStore
{
    Task WriteDecompositionAsync(
        DateTimeOffset asOfUtc,
        Guid? runId,
        string markdown,
        NewsTypingDecompositionDocument document,
        CancellationToken ct);

    Task WriteFailedAsync(DateTimeOffset asOfUtc, Guid? runId, string reason, CancellationToken ct);
}
