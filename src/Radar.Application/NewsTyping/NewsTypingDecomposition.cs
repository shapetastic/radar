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
    /// <see cref="NewsTypingDecompositionCohort.RetryExhausted"/> count; readers are by-NAME, so a v1
    /// consumer reads a v2 document unchanged (asserted).
    /// </summary>
    public const string CurrentSchemaVersion = "news-typing-decomposition-v2";

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
    int RetryExhausted);

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
