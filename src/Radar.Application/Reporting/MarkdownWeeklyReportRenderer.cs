namespace Radar.Application.Reporting;

using System.Globalization;
using System.Text;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.Domain.Reports;
using Radar.Domain.Signals;

/// <summary>
/// Pure, deterministic renderer that turns a fully-assembled <see cref="WeeklyReportModel"/> into
/// markdown. No clock, no I/O, no repositories: the same model always renders byte-identical output
/// (invariant culture, <c>\n</c> line endings, model-supplied ordering). This is where Radar's
/// output-language hard rule is enforced — only the six AD-9 ALLOWED labels render (Investigate,
/// Watch, Ignore, Needs more evidence, Thesis improving, Thesis deteriorating), the required
/// disclaimers are always present, and every entry carries its score-snapshot id plus attributed
/// evidence links so a reported company is reproducible from stored data.
/// <para>
/// Since spec 150 it also appends one PLAIN RANKED TABLE per configured scoring strategy after all of the
/// above, when (and only when) the model carries strategy sections. Those tables are scores only — no
/// labels, no evidence, no "why noticed" — and nothing in them is combined across strategies.
/// </para>
/// </summary>
public sealed class MarkdownWeeklyReportRenderer : IWeeklyReportRenderer
{
    private const char Lf = '\n';

    private static readonly IReadOnlySet<RadarReportAction> Allowed = new HashSet<RadarReportAction>
    {
        RadarReportAction.Investigate,
        RadarReportAction.Watch,
        RadarReportAction.Ignore,
        RadarReportAction.NeedsMoreEvidence,
        RadarReportAction.ThesisImproving,
        RadarReportAction.ThesisDeteriorating,
    };

    private static readonly IReadOnlyDictionary<RadarReportAction, string> DisplayLabels =
        new Dictionary<RadarReportAction, string>
        {
            [RadarReportAction.Investigate] = "Investigate",
            [RadarReportAction.Watch] = "Watch",
            [RadarReportAction.Ignore] = "Ignore",
            [RadarReportAction.NeedsMoreEvidence] = "Needs more evidence",
            [RadarReportAction.ThesisImproving] = "Thesis improving",
            [RadarReportAction.ThesisDeteriorating] = "Thesis deteriorating",
        };

    // Spec 167: display-only relabel of the stored GuidanceChange signal type. The stored token is a
    // taxonomy misnomer (spec-75 lineage): the AI filing reader classifies the business trajectory AS
    // REPORTED and is never asked whether guidance changed, and the deterministic spec-57 earnings-8-K
    // signal carries the same member for a plain earnings FILING — so printing "GuidanceChange" reads
    // as a guidance event that never happened. This is the ONE renderer-owned mapping site: every place
    // the renderer itself stringifies a SignalType routes through it. It must NEVER be applied to stored
    // provenance text (evidence-link reasons authored at scoring time render byte-verbatim) — the legend
    // line in AppendDisclaimers explains the literal token where it appears inside that stored text.
    private static string DisplaySignalType(SignalType type) =>
        type == SignalType.GuidanceChange ? "EarningsTrajectory" : type.ToString();

    // Short, purely descriptive gloss of the curated following tier (AD-9: a research statistic — how
    // covered the name already is — never a valuation or an advice word).
    private static readonly IReadOnlyDictionary<FollowingTier, string> FollowingTierNotes =
        new Dictionary<FollowingTier, string>
        {
            [FollowingTier.Small] = "under-followed",
            [FollowingTier.Mid] = "moderately followed",
            [FollowingTier.Large] = "widely followed",
            [FollowingTier.Mega] = "already broadly followed",
        };

    public string Render(WeeklyReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Enforce the output-language hard rule and provenance invariants before emitting
        // anything: a disallowed label (anything outside the six AD-9 labels) must never reach
        // the report, and the
        // cited score-snapshot id / company id must match the attached snapshot the scores are
        // read from — otherwise the markdown would cite one snapshot while showing another's
        // scores, breaking reproducibility.
        foreach (var entry in model.Entries)
        {
            if (!Allowed.Contains(entry.Action))
            {
                throw new InvalidOperationException(
                    $"Entry for '{entry.CompanyName}' has disallowed report action '{entry.Action}'.");
            }

            if (entry.ScoreSnapshotId != entry.Snapshot.Id)
            {
                throw new InvalidOperationException(
                    $"Entry for '{entry.CompanyName}' cites score snapshot '{entry.ScoreSnapshotId}' "
                    + $"but the attached snapshot is '{entry.Snapshot.Id}'.");
            }

            if (entry.CompanyId != entry.Snapshot.CompanyId)
            {
                throw new InvalidOperationException(
                    $"Entry for '{entry.CompanyName}' has company id '{entry.CompanyId}' "
                    + $"but the attached snapshot belongs to company '{entry.Snapshot.CompanyId}'.");
            }
        }

        // The same provenance invariant, applied to the per-strategy tables (spec 150): a row's cited
        // snapshot id and company id must match the snapshot its scores are read from, or the table would
        // print one snapshot's numbers under another's citation.
        if (model.Strategies is { Count: > 0 } strategies)
        {
            foreach (var section in strategies)
            {
                foreach (var row in section.Rows)
                {
                    if (row.ScoreSnapshotId != row.Snapshot.Id)
                    {
                        throw new InvalidOperationException(
                            $"Strategy '{section.StrategyName}' row for '{row.CompanyName}' cites score "
                            + $"snapshot '{row.ScoreSnapshotId}' but the attached snapshot is "
                            + $"'{row.Snapshot.Id}'.");
                    }

                    if (row.CompanyId != row.Snapshot.CompanyId)
                    {
                        throw new InvalidOperationException(
                            $"Strategy '{section.StrategyName}' row for '{row.CompanyName}' has company id "
                            + $"'{row.CompanyId}' but the attached snapshot belongs to company "
                            + $"'{row.Snapshot.CompanyId}'.");
                    }
                }
            }
        }

        var sb = new StringBuilder();

        AppendHeading(sb, model);
        AppendDisclaimers(sb);
        AppendLiveStrategyLeaders(sb, model);
        AppendHighestOpportunity(sb, model);
        AppendThesisSection(sb, model, RadarReportAction.ThesisImproving, "Thesis improving");
        AppendThesisSection(sb, model, RadarReportAction.ThesisDeteriorating, "Thesis deteriorating");
        AppendNamedActionSection(sb, model, RadarReportAction.Watch, "Watch");
        AppendNamedActionSection(sb, model, RadarReportAction.Ignore, "Ignore / Low signal");
        AppendSignalsNeedingReview(sb, model);
        AppendCollectionSummary(sb, model);
        AppendCollectionHealth(sb, model);
        AppendRecentRuns(sb, model);
        AppendStrategySections(sb, model);

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
        sb.Append("> Notedness (measured Attention + curated following tier) discounts a company's ")
            .Append("Opportunity so already-followed names surface lower — a research signal, not a valuation.")
            .Append(Lf);
        // Spec 167: the literal "GuidanceChange" token still appears inside STORED evidence-line
        // provenance text (authored at scoring time and rendered byte-verbatim), so the legend explains
        // it rather than rewriting it. Producer-neutral on purpose: the deterministic spec-57 form is an
        // earnings-FILING marker, not a trajectory read.
        sb.Append("> \"GuidanceChange\" in evidence lines is a historical earnings-release signal type ")
            .Append("— either a deterministic Neutral earnings-filing marker or an AI earnings-trajectory ")
            .Append("read; it does not by itself mean the company issued or changed guidance.")
            .Append(Lf);
        sb.Append(Lf);
    }

    // Spec 176: the compact live summary — AT MOST this many rows per strategy. A PRESENTATION constant,
    // deliberately not a configuration knob and not a scoring threshold: the full per-strategy tables below
    // (spec 150) keep the report's MaxItems cap, this section only answers "what is each arm saying now".
    private const int LiveLeadersPerStrategy = 5;

    // Spec 176 §1 — the fixed honesty wording, pinned by tests. Live scores are a different STATE from a
    // descriptive efficacy observation and from AD-15 claim support, and the rendered language must never
    // conflate them.
    private const string LiveLeadersNoForwardPriceLine =
        "Live scores are shown immediately and are never gated on a future price. Forward outcomes are "
            + "required only to evaluate the strategy later; these rankings are not efficacy results.";

    private const string LiveLeadersNoCrossStrategyLine =
        "Scores and score magnitudes are comparable only within the same strategy. Repeated company names "
            + "across arms are not a consensus signal.";

    private const string LiveLeadersComparatorLine =
        "Comparators are displayed to diagnose what the research arms may merely be reproducing. A "
            + "comparator leader is not a Radar candidate.";

    // Spec 176: the live strategy leaders — a SECOND RENDERING of the first few spec-150 rows per strategy,
    // never a second construction of them (every value is read off the row's already-guarded current
    // snapshot). Rendered immediately after the standing disclaimers and BEFORE "## Highest opportunity",
    // and only when the model carries strategy sections — a single-strategy run (Strategies == null) stays
    // byte-identical to the pre-150 report.
    //
    // Deliberately NOT here (spec 176 §5): no merged/cross-strategy rank, no consensus score or count, no
    // agreement badges, no labels for non-primary strategies, no cross-formula score threshold, and no
    // movement/previous-score data (that would need the cross-run file-store read path — a later,
    // separately bounded feature).
    private static void AppendLiveStrategyLeaders(StringBuilder sb, WeeklyReportModel model)
    {
        var strategies = model.Strategies;
        if (strategies is null || strategies.Count == 0)
        {
            return;
        }

        // Grouping uses the CARRIED Purpose only — never a name/prefix/formula inference (spec 176 §2).
        // Within each group the model's configured order is preserved; the research group additionally puts
        // the primary first (a stable partition, mirroring the builder's own ordering rule).
        var research = new List<StrategyReportSection>(strategies.Count);
        research.AddRange(strategies.Where(s => s.Purpose == StrategyPurpose.Research && s.IsPrimary));
        research.AddRange(strategies.Where(s => s.Purpose == StrategyPurpose.Research && !s.IsPrimary));
        var comparators = strategies.Where(s => s.Purpose == StrategyPurpose.Comparator).ToList();

        sb.Append("## Live strategy leaders").Append(Lf);
        sb.Append(Lf);

        if (research.Count > 0)
        {
            sb.Append("### Research arms").Append(Lf);
            sb.Append(Lf);
            sb.Append(LiveLeadersNoForwardPriceLine).Append(Lf);
            sb.Append(Lf);
            sb.Append(LiveLeadersNoCrossStrategyLine).Append(Lf);
            sb.Append(Lf);
            AppendLiveLeadersTable(sb, research);
        }

        if (comparators.Count > 0)
        {
            sb.Append("### Comparators — diagnostic only").Append(Lf);
            sb.Append(Lf);
            sb.Append(LiveLeadersComparatorLine).Append(Lf);
            sb.Append(Lf);
            AppendLiveLeadersTable(sb, comparators);
        }
    }

    // ONE combined table per subsection: per strategy, at most its first LiveLeadersPerStrategy existing
    // spec-150 rows (already ranked, already evidence-filtered per spec 53 — fewer than five means fewer
    // rows, never manufactured ones). A strategy with zero surfaced rows is RETAINED with an explicit empty
    // message: an empty experimental arm is a result, not grounds to omit the arm.
    private static void AppendLiveLeadersTable(
        StringBuilder sb, IReadOnlyList<StrategyReportSection> sections)
    {
        sb.Append("| strategy | rank | company | ticker | Opportunity | as-of UTC |").Append(Lf);
        sb.Append("| --- | ---: | --- | --- | ---: | --- |").Append(Lf);

        foreach (var section in sections)
        {
            // The primary is labelled so a reader can tell which arm owns the narrative below (spec 176 §2:
            // "a valid primary is labelled primary research"). A Comparator primary cannot exist — the
            // strategy set rejects it at startup — so the label never contradicts the subsection.
            var strategyCell = EscapeTableCell(section.StrategyName)
                + (section.IsPrimary ? " (primary research)" : string.Empty);

            if (section.Rows.Count == 0)
            {
                sb.Append("| ")
                    .Append(strategyCell)
                    .Append(" | — | No evidence-linked live scores in this report window. | — | — | — |")
                    .Append(Lf);
                continue;
            }

            for (var i = 0; i < section.Rows.Count && i < LiveLeadersPerStrategy; i++)
            {
                var row = section.Rows[i];
                sb.Append("| ")
                    .Append(strategyCell)
                    .Append(" | ")
                    // The existing within-strategy rank — never recomputed, never merged across strategies.
                    .Append(row.Rank.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ")
                    .Append(EscapeTableCell(row.CompanyName))
                    .Append(" | ")
                    .Append(string.IsNullOrEmpty(row.Ticker) ? "—" : EscapeTableCell(row.Ticker))
                    .Append(" | ")
                    .Append(row.Snapshot.OpportunityScore.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ")
                    // The EXACT scoring cutoff: the snapshot's WindowEndUtc — deliberately not CreatedAtUtc
                    // and not the report date, so two rows with different knowledge cutoffs are visibly
                    // different rather than reading as one synchronized table. Normalized to UTC so the
                    // trailing Z is true even for a snapshot deserialized with a non-zero offset.
                    .Append(row.Snapshot.WindowEndUtc.ToUniversalTime().ToString(
                        "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    .Append("Z |")
                    .Append(Lf);
            }
        }

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
            .Append(FormatMovement(entry, snap))
            .Append(Lf);

        // Notedness = the two INPUTS behind the Opportunity notedness discount, surfaced verbatim:
        // the measured AttentionScore from the snapshot and the curated FollowingTier from the seed.
        // The discount multiplier itself is deliberately NOT recomputed here — that lives in the
        // versioned scoring formula; duplicating it in the renderer would let the two drift apart.
        sb.Append("- **Notedness:** Attention ")
            .Append(snap.AttentionScore.ToString(CultureInfo.InvariantCulture))
            .Append(" · Following: ")
            .Append(entry.FollowingTier.ToString());
        if (FollowingTierNotes.TryGetValue(entry.FollowingTier, out var tierNote))
        {
            sb.Append(" (").Append(tierNote).Append(')');
        }
        sb.Append(Lf);

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

        AppendSignals(sb, entry);

        sb.Append(Lf);
    }

    // Deterministic week-over-week movement clause appended to the score line. Descriptive metadata
    // only (never a label or advice): signed deltas of Opportunity/Trajectory against the entry's
    // previous snapshot, "no change" when both are flat, "first snapshot" when there is no prior, or
    // "scoring updated" when a prior snapshot exists but was produced by a different (incomparable)
    // scoring generation — in that case a numeric delta would be a fabricated company story.
    private static string FormatMovement(WeeklyReportEntry entry, Domain.Scoring.CompanyScoreSnapshot snap)
    {
        // A prior snapshot exists but is incomparable (scoring logic changed between runs). Render an
        // honest "(scoring updated)" instead of a numeric delta or "(first snapshot)" — a prior snapshot
        // *does* exist, it is just not comparable.
        if (entry.PreviousScoringChanged)
        {
            return " (scoring updated)";
        }

        // Previous scores are populated-or-null together (both come from the prior snapshot, or
        // neither does). Only render the movement clause when *both* are present; a single null
        // means there is no prior snapshot to compare against.
        if (entry.PreviousOpportunityScore is not int previousOpportunity
            || entry.PreviousTrajectoryScore is not int previousTrajectory)
        {
            return " (first snapshot)";
        }

        var opportunityDelta = snap.OpportunityScore - previousOpportunity;
        var trajectoryDelta = snap.TrajectoryScore - previousTrajectory;

        if (opportunityDelta == 0 && trajectoryDelta == 0)
        {
            return " (no change vs last run)";
        }

        return " (Opportunity "
            + FormatSignedDelta(snap.OpportunityScore, previousOpportunity)
            + ", Trajectory "
            + FormatSignedDelta(snap.TrajectoryScore, previousTrajectory)
            + " vs last run)";
    }

    private static string FormatSignedDelta(int current, int previous)
    {
        var delta = current - previous;
        return delta >= 0
            ? "+" + delta.ToString(CultureInfo.InvariantCulture)
            : delta.ToString(CultureInfo.InvariantCulture);
    }

    private static void AppendSignals(StringBuilder sb, WeeklyReportEntry entry)
    {
        if (entry.Signals.Count == 0)
        {
            return;
        }

        sb.Append("- Why noticed:").Append(Lf);
        foreach (var signal in entry.Signals)
        {
            sb.Append("  - ")
                .Append(DisplaySignalType(signal.Type))
                .Append(" (")
                .Append(signal.Direction.ToString())
                .Append(')');

            var reason = signal.Reason.Trim();
            if (reason.Length > 0)
            {
                sb.Append(": ").Append(reason);
            }

            sb.Append(Lf);
        }
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
        StringBuilder sb, WeeklyReportModel model, RadarReportAction action, string header) =>
        AppendNamedActionSection(sb, model, action, header);

    // Named roll-up of every entry whose action matches: company (ticker) (#rank), in model order.
    // Shared by the thesis sections and the "Ignore / Low signal" section so all three stay
    // byte-identical in shape. Omitted entirely when no entry matches.
    private static void AppendNamedActionSection(
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
                .Append(" — ")
                .Append(signal.ReviewReason)
                .Append(" (signal ")
                .Append(signal.SignalId.ToString())
                .Append(')')
                .Append(Lf);
        }
        sb.Append(Lf);
    }

    // Transparency footer: how many sources Radar checked this run and how many were unreadable,
    // plus one bullet per failed source (in the summary's deterministic order). Observational
    // metadata only — no labels, no scoring, no advice language. Omitted entirely when the model
    // carries no collection summary, preserving back-compat for direct-model callers.
    private static void AppendCollectionSummary(StringBuilder sb, WeeklyReportModel model)
    {
        var collection = model.Collection;
        if (collection is null)
        {
            return;
        }

        sb.Append("## Collection summary").Append(Lf);
        sb.Append(Lf);
        sb.Append("Radar checked ")
            .Append(collection.SourcesChecked.ToString(CultureInfo.InvariantCulture))
            .Append(" source(s) this run; ")
            .Append(collection.SourcesFailed.ToString(CultureInfo.InvariantCulture))
            .Append(" could not be read.")
            .Append(Lf);

        if (collection.Failures.Count > 0)
        {
            foreach (var failure in collection.Failures)
            {
                sb.Append("- ").Append(failure.SourceName);
                if (!string.IsNullOrWhiteSpace(failure.SourceUrl))
                {
                    sb.Append(" (").Append(failure.SourceUrl).Append(')');
                }
                sb.Append(": ").Append(failure.Reason).Append(Lf);
            }
        }

        sb.Append(Lf);
    }

    // Collection-health diagnostics: one bullet per collection-health warning (spec 98), in the
    // model-supplied deterministic order. Observational metadata only — no labels, no scoring, no advice
    // language, so it does not interact with the AD-9 allowed-label enforcement. Omitted entirely when
    // there are no warnings (a clean run adds nothing to the report), preserving back-compat for
    // direct-model callers that pass no health report.
    private static void AppendCollectionHealth(StringBuilder sb, WeeklyReportModel model)
    {
        if (model.Health is not { HasWarnings: true } health)
        {
            return;
        }

        sb.Append("## Collection health").Append(Lf);
        sb.Append(Lf);
        foreach (var w in health.Warnings)
        {
            sb.Append("- [")
                .Append(w.Severity.ToString())
                .Append("] ")
                .Append(w.FeedType)
                .Append(": declared ")
                .Append(w.DeclaredInSeed.ToString(CultureInfo.InvariantCulture))
                .Append(", reached ")
                .Append(w.ReachedCollectors.ToString(CultureInfo.InvariantCulture))
                .Append(" — ")
                .Append(w.Message)
                .Append(Lf);
        }
        sb.Append(Lf);
    }

    // Run-history footer: one bullet per recent run (newest-first, model order) with the run instant,
    // which collectors ran, and a glance at the run's counts. Observational metadata only — no labels,
    // no scoring, no advice language. Omitted entirely when the model carries no recent runs (null or
    // empty), preserving back-compat for direct-model callers.
    private static void AppendRecentRuns(StringBuilder sb, WeeklyReportModel model)
    {
        var recentRuns = model.RecentRuns;
        if (recentRuns is null || recentRuns.Count == 0)
        {
            return;
        }

        sb.Append("## Recent runs").Append(Lf);
        sb.Append(Lf);
        foreach (var run in recentRuns)
        {
            sb.Append("- ")
                .Append(run.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append("Z — collectors: ")
                .Append(run.Collectors.Count > 0 ? string.Join(", ", run.Collectors) : "(none)")
                .Append(" — new evidence ")
                .Append(run.EvidenceNew.ToString(CultureInfo.InvariantCulture))
                .Append(" · approved ")
                .Append(run.SignalsApproved.ToString(CultureInfo.InvariantCulture))
                .Append(" · companies ")
                .Append(run.CompaniesScored.ToString(CultureInfo.InvariantCulture))
                .Append(" · sources ")
                .Append(run.SourcesChecked.ToString(CultureInfo.InvariantCulture))
                .Append('/')
                .Append(run.SourcesFailed.ToString(CultureInfo.InvariantCulture))
                .Append(" failed")
                .Append(Lf);
        }
        sb.Append(Lf);
    }

    // Spec 150: one plain ranked table per configured scoring strategy, primary first, appended after ALL
    // existing content. Omitted entirely when the model carries no strategy sections (a single-strategy run,
    // i.e. every deployment that never configured Radar:Strategies), which is what keeps the pre-150 report
    // byte-identical.
    //
    // SCORES ONLY. No labels (Watch/Ignore/Investigate stay the primary's — a company labelled Watch under
    // one strategy and Ignore under another would read as Radar equivocating, which the output-language
    // rules do not contemplate), no evidence blocks, no "why noticed", and no advice vocabulary. Nothing
    // here is combined across strategies either: no disagreement metric, no merged ranking, no composite —
    // the reader compares by eye, and ranking strategies against price is spec 140's leaderboard.
    private static void AppendStrategySections(StringBuilder sb, WeeklyReportModel model)
    {
        var strategies = model.Strategies;
        if (strategies is null || strategies.Count == 0)
        {
            return;
        }

        var first = true;
        foreach (var section in strategies)
        {
            AppendStrategySection(sb, section, isFirst: first);
            first = false;
        }
    }

    private static void AppendStrategySection(
        StringBuilder sb, StrategyReportSection section, bool isFirst)
    {
        sb.Append("## Strategy: ")
            .Append(section.StrategyName)
            .Append(" (")
            .Append(section.FormulaVersion)
            .Append(')');
        if (section.IsPrimary)
        {
            // So a reader can tell which series the narrative sections above describe.
            sb.Append(" — primary (the series reported above)");
        }
        sb.Append(Lf);

        sb.Append("Fingerprint: ")
            .Append(string.IsNullOrWhiteSpace(section.ScoringConfigVersion)
                ? "(unstamped)"
                : section.ScoringConfigVersion)
            .Append(" · ")
            .Append(section.CompaniesScored.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(section.CompaniesScored == 1 ? "company" : "companies")
            .Append(" scored · ")
            .Append(section.CompaniesWithLinkedEvidence.ToString(CultureInfo.InvariantCulture))
            .Append(" with linked evidence");
        if (section.Truncated)
        {
            // Never silent (spec 125): when the report's MaxItems cap removed rows, the header says so.
            sb.Append(" · showing top ")
                .Append(section.Rows.Count.ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(Lf);
        sb.Append(Lf);

        if (isFirst)
        {
            // Spec 150 §4. A reader who eyeballs two rankings will otherwise infer a winner, which is the
            // multiple-comparisons trap arriving via the reader instead of the statistics.
            sb.Append("These are independent scorings of the SAME collection pass. Absolute scores are not ")
                .Append("comparable across strategies when the formulas differ, and a higher-looking table ")
                .Append("is not a better strategy. Ranking strategies against subsequent price movement is ")
                .Append("data/efficacy/strategy-leaderboard.md, not this table.")
                .Append(Lf);
            sb.Append(Lf);
        }

        sb.Append("| rank | company | ticker | Opportunity | Trajectory | Attention | Evidence | Velocity |")
            .Append(Lf);
        sb.Append("| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |").Append(Lf);

        foreach (var row in section.Rows)
        {
            var snap = row.Snapshot;
            sb.Append("| ")
                .Append(row.Rank.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(EscapeTableCell(row.CompanyName))
                .Append(" | ")
                .Append(string.IsNullOrEmpty(row.Ticker) ? "—" : EscapeTableCell(row.Ticker))
                .Append(" | ")
                .Append(snap.OpportunityScore.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(snap.TrajectoryScore.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(snap.AttentionScore.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(snap.EvidenceConfidenceScore.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(snap.SignalVelocityScore.ToString(CultureInfo.InvariantCulture))
                .Append(" |")
                .Append(Lf);
        }

        sb.Append(Lf);
    }

    // A markdown table cell is pipe-delimited, so an unescaped '|' in a company name or ticker would split
    // that row into extra columns and silently corrupt every value after it. A line break is the only other
    // character that can end a cell — and nothing in the Company domain type forbids one in a name or ticker
    // — so CR/LF collapse to a space rather than breaking the row out of the table entirely.
    private static string EscapeTableCell(string value)
    {
        var escaped = value.Contains('|', StringComparison.Ordinal)
            ? value.Replace("|", "\\|", StringComparison.Ordinal)
            : value;

        return escaped.Contains('\n', StringComparison.Ordinal)
            || escaped.Contains('\r', StringComparison.Ordinal)
            ? escaped
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
            : escaped;
    }
}
