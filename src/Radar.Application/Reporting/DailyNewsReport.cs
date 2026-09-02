using System.Globalization;
using System.Text;

using Radar.Application.News;
using Radar.Domain.Signals;

namespace Radar.Application.Reporting;

/// <summary>
/// One judged directional news signal, resolved back from the durable signal store for the run's daily news
/// view. Every row is provenance-bearing: it names the signal, its evidence and the stage-2 judgment whose
/// cited read supplied the direction — a row can always be walked back to the article Radar actually read.
/// </summary>
public sealed record DailyNewsReportRow(
    Guid SignalId,
    Guid EvidenceId,
    Guid JudgmentId,
    Guid CompanyId,
    string CompanyName,
    SignalDirection Direction,
    int Strength,
    decimal Confidence,
    // The judge's business-trajectory token from the signal's metadata envelope, or null when the envelope
    // does not carry one. Rendered as "not recorded" — never defaulted to a real-looking value.
    string? JudgedTrajectory,
    // The signal's supporting excerpt's first line — the cited article text, not a summary Radar wrote.
    string Headline);

/// <summary>
/// The per-run DAY view of judged directional news (maintainer request, 2026-09-02): which companies had
/// judged-improving or judged-deteriorating news minted by THIS run, listed with the materializer's own
/// accounting so nothing judged is dropped silently. Deliberately NOT a score: it persists no number, feeds
/// nothing downstream, and is not comparable across days as a series — it exists because the strategy score
/// is a slow 60-day accrual instrument and "what was today's judged news?" deserves its own honest surface
/// rather than a twitchier score.
/// </summary>
public sealed record DailyNewsReport(
    // Null = the pipeline composition recorded no run id - rendered as "not recorded", never invented.
    Guid? RunId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DailyNewsReportRow> Rows,
    // The judgment-signal materializer's accounting for the pass that minted these rows — considered /
    // eligible / materialized / write-failed / named skips. Carried whole so the rendered report states the
    // same counts the run logged, never a re-derived approximation.
    NewsJudgmentSignalMaterializationSummary Accounting,
    // Signals the materializer reported as minted or already present that this read could NOT resolve from
    // the durable store under this run's judgment ids. Non-zero is worth an operator's glance (a failed
    // durable write, or a judgment reused from a prior run whose signal carries the original judgment id) —
    // it is counted here rather than silently narrowing the table.
    int MaterializedNotResolved);

/// <summary>
/// Renders the daily news view as markdown. Pure and deterministic (AD-3): no clock, no randomness, no I/O —
/// ordering, formatting and escaping are all fixed by the input.
/// </summary>
public static class DailyNewsReportRenderer
{
    public static string Render(DailyNewsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.Append("# Radar Daily News — ")
            .AppendLine(report.GeneratedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.Append("Run: ").Append(report.RunId?.ToString("D") ?? "not recorded").Append(" · generated ")
            .Append(report.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .AppendLine("Z");
        sb.AppendLine();
        sb.AppendLine("> Not financial advice. For research only. Human review required.");
        sb.AppendLine("> Day view: judged directional news minted by THIS run only. Direction and strength come from");
        sb.AppendLine("> the stage-2 news judgment that cited each article. This is not a score, feeds nothing");
        sb.AppendLine("> downstream, and is not comparable across days as a series; most coverage on most days is");
        sb.AppendLine("> neutral attention and deliberately does not appear here.");
        sb.AppendLine();
        sb.AppendLine("## Judged directional news");
        sb.AppendLine();

        if (report.Rows.Count == 0)
        {
            sb.AppendLine("No judged directional news was minted by this run. The accounting below says why");
            sb.AppendLine("(no eligible judgments, or every judgment was non-directional).");
        }
        else
        {
            sb.AppendLine("| direction | company | strength | confidence | judged trajectory | cited headline |");
            sb.AppendLine("| --- | --- | ---: | ---: | --- | --- |");
            foreach (var row in Ordered(report.Rows))
            {
                sb.Append("| ").Append(row.Direction)
                    .Append(" | ").Append(Escape(row.CompanyName))
                    .Append(" | ").Append(row.Strength.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(row.Confidence.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(Escape(row.JudgedTrajectory ?? "not recorded"))
                    .Append(" | ").Append(Escape(row.Headline))
                    .AppendLine(" |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Accounting");
        sb.AppendLine();
        var a = report.Accounting;
        sb.Append("- judgments considered ").Append(a.JudgmentsConsidered)
            .Append(" · eligible ").Append(a.Eligible)
            .Append(" · signals materialized ").Append(a.Materialized)
            .Append(" · already materialized ").Append(a.AlreadyMaterialized)
            .AppendLine();
        sb.Append("- validation rejected ").Append(a.ValidationRejected)
            .Append(" · write failed ").Append(a.WriteFailed)
            .Append(" · prior-version occupied ").Append(a.PriorVersionOccupied)
            .AppendLine();
        var skips = a.DescribeSkips();
        sb.Append("- skips: ").AppendLine(skips.Length == 0 ? "none" : skips);
        if (report.MaterializedNotResolved > 0)
        {
            sb.Append("- ⚠ ").Append(report.MaterializedNotResolved)
                .AppendLine(" materialized signal(s) could not be resolved from the durable store under this"
                    + " run's judgment ids (a failed durable write, or a reused judgment carrying its original"
                    + " id). Counted here rather than silently narrowing the table above.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Positive rows first, then Negative; strength descending within each; company name then signal id as
    /// total-order tiebreaks so the same rows always render identically (AD-3).
    /// </summary>
    private static IEnumerable<DailyNewsReportRow> Ordered(IReadOnlyList<DailyNewsReportRow> rows) =>
        rows.OrderBy(r => r.Direction == SignalDirection.Positive ? 0 : 1)
            .ThenByDescending(r => r.Strength)
            .ThenBy(r => r.CompanyName, StringComparer.Ordinal)
            .ThenBy(r => r.SignalId);

    /// <summary>One line, table-safe: newlines and pipes collapsed, long cited text truncated with an ellipsis.</summary>
    private static string Escape(string value)
    {
        var oneLine = value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal).Trim();
        const int max = 160;
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
    }
}
