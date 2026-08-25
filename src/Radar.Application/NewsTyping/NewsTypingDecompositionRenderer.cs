using System.Globalization;
using System.Text;

namespace Radar.Application.NewsTyping;

/// <summary>
/// Pure, deterministic markdown rendering of the attention-decomposition document (spec 181 §5). Every
/// reader × capture-mode cohort renders its OWN breakdown, labelled by reader name, exact model id and
/// capture mode; no merged distribution, majority type or combined verdict exists anywhere. The same-event
/// family count renders BESIDE the raw count, never instead of it.
/// </summary>
public static class NewsTypingDecompositionRenderer
{
    public static string RenderMarkdown(NewsTypingDecompositionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();
        var recordsSpec189Diagnostics = RecordsSpec189Diagnostics(document);
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"# Attention decomposition — {document.GeneratedAtUtc:yyyy-MM-dd}"));
        sb.AppendLine();
        sb.AppendLine("> " + document.Caveat);
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Run: `{(document.RunId?.ToString("D") ?? "(none)")}` · window "
                + $"{FormatInstant(document.WindowStartUtc)} → {FormatInstant(document.WindowEndUtc)} · "
                + $"readers: {string.Join("; ", document.Readers)}"));
        sb.AppendLine(document.CaptureProvenThisRun switch
        {
            true => "Capture this run: proven.",
            false => "Capture this run: NOT proven (the batch manifest records failures).",
            null => "Capture this run: unknown (no batch manifest was resolvable).",
        });
        if (document.ObservationsWithoutCompany > 0)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{document.ObservationsWithoutCompany} window observation(s) carry no company "
                    + $"attribution and appear in no company section."));
        }

        // Spec 189 §3: INFLOW beside spend. "252 captured against a 200-call budget" is the fact the
        // capacity decision turns on, and until this line existed no artifact stated it. Fail-closed: an
        // unresolvable batch renders "not recorded", never a guessed number — and a re-rendered pre-v4
        // artifact, which carries no capture fields at ALL, says so rather than reporting "(none)" (which
        // would claim the run genuinely had no batch).
        sb.AppendLine(recordsSpec189Diagnostics
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Observation capture this run: batch "
                    + $"`{document.NewsObservationBatchId?.ToString("D") ?? "(none)"}` · new observations "
                    + $"{document.ObservationsCapturedThisRun?.ToString(CultureInfo.InvariantCulture)
                        ?? "not recorded"}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Observation capture this run: not recorded (schema {document.SchemaVersion})"));

        sb.AppendLine();
        AppendReaderSummaries(sb, document);

        if (document.Companies.Count == 0)
        {
            sb.AppendLine("No company had a window observation.");
        }

        foreach (var company in document.Companies)
        {
            sb.AppendLine($"## {company.Ticker ?? company.CompanyId.ToString("D")}");
            sb.AppendLine();
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Observations in window: {company.ObservationsInWindow}"));
            if (company.Incomplete)
            {
                sb.AppendLine(
                    "**INCOMPLETE** — " + string.Join("; ", company.IncompleteReasons));
            }

            sb.AppendLine();

            foreach (var cohort in company.Cohorts)
            {
                sb.AppendLine(
                    $"### Reader {cohort.ReaderName} ({cohort.Provider}:{cohort.ModelId}) — "
                        + $"{cohort.CaptureMode}");
                sb.AppendLine();
                // Spec 186 §2: exhaustion is a PERMANENT hole, never a backlog — rendered only when it
                // happened, so it reads as an exception rather than as noise.
                var exhausted = cohort.RetryExhausted > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $" · retries exhausted {cohort.RetryExhausted}")
                    : string.Empty;
                // Spec 187 §3: a hosted call spent with no durable outcome is a COST fact, rendered only
                // when it happened so it reads as the exception it is — never folded into the backlog.
                var orphaned = cohort.ReservedWithoutOutcome > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $" · reserved without outcome {cohort.ReservedWithoutOutcome}")
                    : string.Empty;
                // Spec 187 §2: the first-attempt LANE SPLIT for this company's in-window observations,
                // rendered only when this pass actually selected some — so a company whose work was all
                // deferred (or already complete) reads unchanged, and "the leaders we were about to judge
                // were typed first" is a visible number rather than a claim.
                // Spec 189 §3 completes the triple with the RETRY lane and renders what the pass actually
                // SPENT beside what it selected — a refused reservation is a selection that never became a
                // call, so the two numbers are deliberately allowed to differ.
                // A re-rendered pre-v4 artifact recorded the two spec-187 lanes but NOT the retry lane or
                // the call count, which deserialize as 0 — and a defaulted 0 is not a measured 0. It
                // therefore renders the two numbers it actually has and NAMES the rest as unrecorded.
                var lanes = cohort.CandidatePrioritySelected > 0
                    || cohort.GeneralSelected > 0
                    || cohort.RetrySelected > 0
                    ? recordsSpec189Diagnostics
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $" · selected this pass: {cohort.RetrySelected} retry, "
                                + $"{cohort.CandidatePrioritySelected} judgment-candidate priority, "
                                + $"{cohort.GeneralSelected} general ({cohort.ProviderCallsAttempted} "
                                + $"provider call(s) made)")
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $" · selected this pass: {cohort.CandidatePrioritySelected} "
                                + $"judgment-candidate priority, {cohort.GeneralSelected} general (retry "
                                + $"lane and provider calls not recorded in {document.SchemaVersion})")
                    : string.Empty;
                // Spec 189 §3: a retryable failure is NAMED, separately from backlog and from exhaustion —
                // it degraded this run's read and the observation is still eligible.
                var retryable = cohort.RetryableFailuresThisRun > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $" · retryable failures this run {cohort.RetryableFailuresThisRun}")
                    : string.Empty;
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Typed {cohort.ObservationsTyped} · insufficient-content "
                        + $"{cohort.ObservationsInsufficientContent} · untyped remaining "
                        + $"{cohort.UntypedRemaining} · same-event families "
                        + $"{cohort.FamilyCount}{exhausted}{retryable}{orphaned}{lanes}"));
                sb.AppendLine();
                if (cohort.Types.Count > 0)
                {
                    sb.AppendLine("| Event type | Observations | Publishers | Families |");
                    sb.AppendLine("| --- | ---: | ---: | ---: |");
                    foreach (var row in cohort.Types)
                    {
                        sb.AppendLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"| {row.EventType} | {row.ObservationCount} | {row.PublisherBreadth} "
                                + $"| {row.FamilyCount} |"));
                    }

                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Spec 189 §3: the AUTHORITATIVE pass-wide budget table, one row per extractor cohort. Rendered ABOVE
    /// the company sections precisely because a reviewer must not have to reconstruct a pass-wide call budget
    /// by summing the current window's company rows — and the note under it states, rather than implies, that
    /// the two populations differ for named reasons.
    /// <para>
    /// Omitted entirely when the document carries no summaries (a v1–v3 artifact re-rendered, or a pass with
    /// no reader), so an absent measurement never renders as a table of zeroes.
    /// </para>
    /// </summary>
    private static void AppendReaderSummaries(
        StringBuilder sb, NewsTypingDecompositionDocument document)
    {
        if (document.ReaderSummaries is not { Count: > 0 } summaries)
        {
            return;
        }

        sb.AppendLine("### Typing pass totals (pass-wide, authoritative for the call budget)");
        sb.AppendLine();
        sb.AppendLine(
            "| Reader | Retry | Candidate | General | Calls | Completed | Provider | Parse | Validation "
                + "| Refused | Write-failed | Exhausted | Reserved w/o outcome | Untyped remaining |");
        sb.AppendLine(
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: "
                + "| ---: |");
        foreach (var summary in summaries)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {summary.ReaderName} ({summary.Provider}:{summary.ModelId}) | {summary.RetrySelected} "
                    + $"| {summary.CandidatePrioritySelected} | {summary.GeneralSelected} "
                    + $"| {summary.ProviderCallsAttempted} | {summary.CompletedOutcomesPersisted} "
                    + $"| {summary.ProviderFailures} | {summary.ParseFailures} "
                    + $"| {summary.ValidationFailures} | {summary.ReservationsRefused} "
                    + $"| {summary.OutcomeWritesFailed} | {summary.RetryExhausted} "
                    + $"| {summary.ReservedWithoutOutcome} | {summary.UntypedRemaining} |"));
        }

        sb.AppendLine();
        sb.AppendLine(
            "The three lane columns are SELECTIONS (disjoint); `Calls` is what the pass actually spent after "
                + "durable-reservation refusals. These totals are PASS-WIDE and the company rows below are a "
                + "WINDOW statement, so they may legitimately differ — a selected legacy-backlog observation "
                + "sits outside the window, and an observation with no company attribution appears in no "
                + "company section.");
        sb.AppendLine();
    }

    /// <summary>
    /// Whether this document's schema actually RECORDS spec 189 §3's additive diagnostics — the capture
    /// inflow fields and the per-cohort <c>RetrySelected</c> / <c>ProviderCallsAttempted</c> counters, all
    /// of which landed in <c>-v4</c>. Re-rendering an accrued v1–v3 artifact deserializes them as 0 (and as
    /// null), and spec 187 §7's rule holds here too: a measured zero and an unmeasured zero are different
    /// facts, so the older document names them unrecorded instead of claiming "0 provider call(s) made".
    /// <para>
    /// The KNOWN pre-v4 tags are the closed set, rather than "equals the current tag": those are the
    /// artifacts actually known to lack the fields, whereas defaulting an unrecognised tag to "not
    /// recorded" would silently hide a real measurement the day the tag next moves.
    /// </para>
    /// </summary>
    private static bool RecordsSpec189Diagnostics(NewsTypingDecompositionDocument document) =>
        !PreSpec189SchemaVersions.Contains(document.SchemaVersion);

    private static readonly HashSet<string> PreSpec189SchemaVersions = new(StringComparer.Ordinal)
    {
        "news-typing-decomposition-v1",
        "news-typing-decomposition-v2",
        "news-typing-decomposition-v3",
    };

    private static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
