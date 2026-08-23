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

        sb.AppendLine();

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
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Typed {cohort.ObservationsTyped} · insufficient-content "
                        + $"{cohort.ObservationsInsufficientContent} · untyped remaining "
                        + $"{cohort.UntypedRemaining} · same-event families "
                        + $"{cohort.FamilyCount}{exhausted}"));
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

    private static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
