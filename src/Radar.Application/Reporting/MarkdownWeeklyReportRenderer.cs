namespace Radar.Application.Reporting;

using System.Globalization;
using System.Text;
using Radar.Domain.Reports;

/// <summary>
/// Pure, deterministic renderer that turns a fully-assembled <see cref="WeeklyReportModel"/> into
/// markdown. No clock, no I/O, no repositories: the same model always renders byte-identical output
/// (invariant culture, <c>\n</c> line endings, model-supplied ordering). This is where Radar's
/// output-language hard rule is enforced — only the five ALLOWED labels render, the required
/// disclaimers are always present, and every entry carries its score-snapshot id plus attributed
/// evidence links so a reported company is reproducible from stored data.
/// </summary>
public sealed class MarkdownWeeklyReportRenderer : IWeeklyReportRenderer
{
    private const char Lf = '\n';

    private static readonly IReadOnlySet<RadarReportAction> Allowed = new HashSet<RadarReportAction>
    {
        RadarReportAction.Investigate,
        RadarReportAction.Watch,
        RadarReportAction.NeedsMoreEvidence,
        RadarReportAction.ThesisImproving,
        RadarReportAction.ThesisDeteriorating,
    };

    private static readonly IReadOnlyDictionary<RadarReportAction, string> DisplayLabels =
        new Dictionary<RadarReportAction, string>
        {
            [RadarReportAction.Investigate] = "Investigate",
            [RadarReportAction.Watch] = "Watch",
            [RadarReportAction.NeedsMoreEvidence] = "Needs more evidence",
            [RadarReportAction.ThesisImproving] = "Thesis improving",
            [RadarReportAction.ThesisDeteriorating] = "Thesis deteriorating",
        };

    public string Render(WeeklyReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Enforce the output-language hard rule before emitting anything: a disallowed label
        // (e.g. Ignore) must never reach the report.
        foreach (var entry in model.Entries)
        {
            if (!Allowed.Contains(entry.Action))
            {
                throw new InvalidOperationException(
                    $"Entry for '{entry.CompanyName}' has disallowed report action '{entry.Action}'.");
            }
        }

        var sb = new StringBuilder();

        AppendHeading(sb, model);
        AppendDisclaimers(sb);
        AppendHighestOpportunity(sb, model);
        AppendThesisSection(sb, model, RadarReportAction.ThesisImproving, "Thesis improving");
        AppendThesisSection(sb, model, RadarReportAction.ThesisDeteriorating, "Thesis deteriorating");
        AppendSignalsNeedingReview(sb, model);

        return sb.ToString();
    }

    private static void AppendHeading(StringBuilder sb, WeeklyReportModel model)
    {
        sb.Append("# ").Append(model.Title).Append(Lf);
        sb.Append("Period: ")
            .Append(model.PeriodStartUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" → ")
            .Append(model.PeriodEndUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" (UTC)")
            .Append(Lf);
        sb.Append("Generated: ")
            .Append(model.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append('Z')
            .Append(Lf);
        sb.Append(Lf);
    }

    private static void AppendDisclaimers(StringBuilder sb)
    {
        sb.Append("> Not financial advice.").Append(Lf);
        sb.Append("> For research only.").Append(Lf);
        sb.Append("> Human review required.").Append(Lf);
        sb.Append(Lf);
    }

    private static void AppendHighestOpportunity(StringBuilder sb, WeeklyReportModel model)
    {
        sb.Append("## Highest opportunity").Append(Lf);
        sb.Append(Lf);

        foreach (var entry in model.Entries)
        {
            AppendEntry(sb, entry);
        }
    }

    private static void AppendEntry(StringBuilder sb, WeeklyReportEntry entry)
    {
        sb.Append("### ")
            .Append(entry.Rank.ToString(CultureInfo.InvariantCulture))
            .Append(". ")
            .Append(entry.CompanyName);
        if (!string.IsNullOrEmpty(entry.Ticker))
        {
            sb.Append(" (").Append(entry.Ticker).Append(')');
        }
        sb.Append(Lf);

        sb.Append("- Label: ").Append(DisplayLabels[entry.Action]).Append(Lf);

        var snap = entry.Snapshot;
        sb.Append("- Opportunity ")
            .Append(snap.OpportunityScore.ToString(CultureInfo.InvariantCulture))
            .Append(" · Trajectory ")
            .Append(snap.TrajectoryScore.ToString(CultureInfo.InvariantCulture))
            .Append(" · Attention ")
            .Append(snap.AttentionScore.ToString(CultureInfo.InvariantCulture))
            .Append(" · Evidence ")
            .Append(snap.EvidenceConfidenceScore.ToString(CultureInfo.InvariantCulture))
            .Append(" · Velocity ")
            .Append(snap.SignalVelocityScore.ToString(CultureInfo.InvariantCulture))
            .Append(Lf);

        sb.Append("- Why: ").Append(entry.Rationale).Append(Lf);
        sb.Append("- Score snapshot: ")
            .Append(entry.ScoreSnapshotId.ToString())
            .Append(Lf);

        sb.Append("- Evidence:").Append(Lf);
        if (entry.Evidence.Count == 0)
        {
            sb.Append("  - (no linked evidence)").Append(Lf);
        }
        else
        {
            foreach (var ev in entry.Evidence)
            {
                AppendEvidence(sb, ev);
            }
        }

        sb.Append(Lf);
    }

    private static void AppendEvidence(StringBuilder sb, ReportEvidenceRef ev)
    {
        sb.Append("  - ");
        if (!string.IsNullOrEmpty(ev.SourceUrl))
        {
            sb.Append('[').Append(ev.Title).Append("](").Append(ev.SourceUrl).Append(')');
        }
        else
        {
            sb.Append(ev.Title);
        }
        sb.Append(" — ").Append(ev.SourceName).Append(": ").Append(ev.ContributionReason).Append(Lf);
    }

    private static void AppendThesisSection(
        StringBuilder sb, WeeklyReportModel model, RadarReportAction action, string header)
    {
        var matches = new List<WeeklyReportEntry>();
        foreach (var entry in model.Entries)
        {
            if (entry.Action == action)
            {
                matches.Add(entry);
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        sb.Append("## ").Append(header).Append(Lf);
        sb.Append(Lf);
        foreach (var entry in matches)
        {
            sb.Append("- ").Append(entry.CompanyName);
            if (!string.IsNullOrEmpty(entry.Ticker))
            {
                sb.Append(" (").Append(entry.Ticker).Append(')');
            }
            sb.Append(" (#")
                .Append(entry.Rank.ToString(CultureInfo.InvariantCulture))
                .Append(')')
                .Append(Lf);
        }
        sb.Append(Lf);
    }

    private static void AppendSignalsNeedingReview(StringBuilder sb, WeeklyReportModel model)
    {
        if (model.SignalsNeedingReview.Count == 0)
        {
            return;
        }

        sb.Append("## Signals needing review").Append(Lf);
        sb.Append(Lf);
        foreach (var signal in model.SignalsNeedingReview)
        {
            sb.Append("- ")
                .Append(signal.CompanyMention)
                .Append(": ")
                .Append(signal.Summary)
                .Append(" (signal ")
                .Append(signal.SignalId.ToString())
                .Append(')')
                .Append(Lf);
        }
        sb.Append(Lf);
    }
}
