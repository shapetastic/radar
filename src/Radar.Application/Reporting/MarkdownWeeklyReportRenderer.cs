namespace Radar.Application.Reporting;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Radar.Application.Lifecycle;
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
    // the renderer itself stringifies a SignalType routes through it.
    //
    // Spec 209 adds a second relabel, InsiderBuying -> "InsiderActivity": the stored member covers every
    // Form 4 (a planned disposition stream renders as Neutral rows of it), so the literal token is an
    // inverted label over a disposition. Spec 167's stance that this mapping "must NEVER be applied to
    // stored provenance text" is SUPERSEDED for that ONE exact token only: DisplayProvenanceText rewrites
    // the whole-word InsiderBuying inside stored evidence-link reasons and signal reasons at render time
    // (both paths the token reaches the reader), because a legend cannot un-invert a label the reader sees
    // eleven times. The GuidanceChange stance is unchanged — its stored text still renders byte-verbatim
    // and the legend line in AppendDisclaimers explains the literal token where it appears.
    private static string DisplaySignalType(SignalType type) =>
        type switch
        {
            SignalType.GuidanceChange => "EarningsTrajectory",
            SignalType.InsiderBuying => "InsiderActivity",
            _ => type.ToString(),
        };

    // Spec 209: the presentation-only seam over STORED text (evidence-link contribution reasons and signal
    // reasons, both authored at scoring time). Exactly one whole-word token is rewritten; every other byte
    // renders verbatim. The stored JSON is never touched — accrued signals keep deserializing unchanged.
    private static readonly Regex StoredInsiderTypeToken =
        new(@"\bInsiderBuying\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string DisplayProvenanceText(string stored) =>
        StoredInsiderTypeToken.Replace(stored, "InsiderActivity");

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
        // Spec 209: the insider channel's rendered name. Deliberately does NOT quote the stored token (the
        // report-language rule forbids its substring); the legend explains what a Neutral row of it is.
        sb.Append("> \"InsiderActivity\" rows are SEC Form 4 insider filings of any kind; a Neutral row is ")
            .Append("a routine or planned filing, not a discretionary transaction.")
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

    // Spec 185 §4 — the semantic-read honesty wording, pinned by tests. Every leader row carries exactly
    // one of the three states; the narrow no-challenge wording is never a clean bill and the sentence says
    // so, because silent ignorance is the failure the marker column exists to end.
    private const string LiveLeadersSemanticReadLine =
        "Semantic read: '⚠ challenged' means the designated judgment cohort recorded at least one validated "
            + "challenge finding; '? unassessed (reason)' means no completed validated judgment exists for "
            + "that row; '· no challenge found in supplied facts' comes only from a completed validated "
            + "judgment and is a statement about the supplied typed facts, never a clean bill for the "
            + "company.";

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

        // Spec 184: the operating-call layer. Only ever present in a multi-strategy composition; a null
        // Lifecycle renders byte-identically to the pre-184 section (direct-model callers, and the whole
        // single-strategy path, never reach here with one).
        var lifecycle = model.Lifecycle;
        List<StrategyReportSection>? stopped = null;
        if (lifecycle is not null)
        {
            AppendOperatingCallBlock(sb, lifecycle);
            AppendCallsAndEvidenceStatus(sb, lifecycle, strategies);
            AppendStaleGateOverrides(sb, lifecycle);

            if (lifecycle.Calls.HasDeclaredCalls && !lifecycle.Calls.StopAll)
            {
                // Lead first, then Trials in configured order, then DoNotLead; Stop arms move to the
                // diagnostic appendix below — never hidden, never unlabelled (spec 184 §2).
                var ordered = new List<StrategyReportSection>(research.Count);
                ordered.AddRange(research.Where(s => CallFor(lifecycle, s) == OperatingCall.Lead));
                ordered.AddRange(research.Where(s => CallFor(lifecycle, s) == OperatingCall.Trial));
                ordered.AddRange(research.Where(s => CallFor(lifecycle, s) == OperatingCall.DoNotLead));
                stopped = research.Where(s => CallFor(lifecycle, s) == OperatingCall.Stop).ToList();
                research = ordered;
            }
        }

        if (research.Count > 0)
        {
            sb.Append("### Research arms").Append(Lf);
            sb.Append(Lf);
            sb.Append(LiveLeadersNoForwardPriceLine).Append(Lf);
            sb.Append(Lf);
            sb.Append(LiveLeadersNoCrossStrategyLine).Append(Lf);
            sb.Append(Lf);
            sb.Append(LiveLeadersSemanticReadLine).Append(Lf);
            sb.Append(Lf);
            AppendLiveLeadersTable(sb, research, lifecycle, model.NewsJudgment);
        }

        if (stopped is { Count: > 0 })
        {
            sb.Append("### Stopped arms — diagnostic appendix").Append(Lf);
            sb.Append(Lf);
            sb.Append("Stopped arms remain fully visible: a stop is a recorded decision, not a deletion. ")
                .Append("Their complete per-strategy tables also remain below.")
                .Append(Lf);
            sb.Append(Lf);
            AppendLiveLeadersTable(sb, stopped, lifecycle, model.NewsJudgment);
        }

        if (comparators.Count > 0)
        {
            sb.Append("### Comparators — diagnostic only").Append(Lf);
            sb.Append(Lf);
            sb.Append(LiveLeadersComparatorLine).Append(Lf);
            sb.Append(Lf);
            AppendLiveLeadersTable(sb, comparators, lifecycle, model.NewsJudgment);
        }

        AppendJudgmentProvenance(sb, model.NewsJudgment, research, stopped, comparators);
    }

    // Spec 186 §1 — the traceability claim, made TRUE rather than asserted: every marker rendered above
    // comes from ONE persisted judgment record, so the record's id is stated here, once per company across
    // all three tables, with the judgments-store root stated ONCE (never per row). Display-only: no score,
    // rank, ordering, label or snapshot is read or moved, and a model carrying no judgment ids (a null
    // model, the pending placeholder, or a directly-composed marker map) renders nothing at all.
    private static void AppendJudgmentProvenance(
        StringBuilder sb,
        NewsJudgmentMarkerReportModel? newsJudgment,
        params IReadOnlyList<StrategyReportSection>?[] tables)
    {
        if (newsJudgment?.Markers is not { Count: > 0 } markers)
        {
            return;
        }

        // Table order, then the model's section order, then row order — the SAME traversal the tables
        // above render (AD-3); a company surfaced by several arms is cited once, under its first
        // appearance, because the marker is a per-company judgment, not a per-row one.
        var lines = new List<string>();
        var seen = new HashSet<Guid>();
        foreach (var table in tables)
        {
            if (table is null)
            {
                continue;
            }

            foreach (var section in table)
            {
                for (var i = 0; i < section.Rows.Count && i < LiveLeadersPerStrategy; i++)
                {
                    var row = section.Rows[i];
                    if (!seen.Add(row.CompanyId))
                    {
                        continue;
                    }

                    if (markers.TryGetValue(row.CompanyId, out var marker)
                        && marker.JudgmentId is { } judgmentId)
                    {
                        lines.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"- {row.CompanyName} — judgment `{judgmentId:D}` · {marker.CellText}"));
                    }
                }
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        sb.Append("### Judgment provenance — diagnostic appendix").Append(Lf);
        sb.Append(Lf);
        sb.Append("Every semantic-read marker above is derived by policy from ONE persisted judgment ")
            .Append("record; its id is stated here so the marker is traceable to the record that produced ")
            .Append("it — trajectory, rationale, consumed families and all five completeness dimensions.")
            .Append(Lf);
        sb.Append(Lf);
        sb.Append("Judgments store root: ")
            .Append(newsJudgment.JudgmentStoreRoot is { Length: > 0 } root
                ? "`" + root + "`"
                : "not recorded by this run")
            .Append(Lf);
        sb.Append(Lf);
        foreach (var line in lines)
        {
            sb.Append(line).Append(Lf);
        }

        sb.Append(Lf);
    }

    /// <summary>The effective call for a section's strategy, or null (comparators, undeclared layer).</summary>
    private static OperatingCall? CallFor(StrategyLifecycleReportModel lifecycle, StrategyReportSection s) =>
        lifecycle.Calls.For(s.StrategyName)?.Call;

    // Spec 184 §2: the operating-call banner. Exactly one of three states — an explicit Lead (with the
    // call, basis, as-of, review-by and resolution rule rendered), an explicit "no lead — StopAll"
    // diagnostic banner (declared or the predeclared zero-Lead fallback), or the honest statement that no
    // call has been declared at all.
    private static void AppendOperatingCallBlock(StringBuilder sb, StrategyLifecycleReportModel lifecycle)
    {
        var calls = lifecycle.Calls;

        sb.Append("### Operating call").Append(Lf);
        sb.Append(Lf);

        if (!calls.HasDeclaredCalls)
        {
            sb.Append("No operating call is declared (").Append(calls.UndeclaredReason)
                .Append("). Narrative prominence remains with the storage-primary strategy by default. ")
                .Append("A call is a maintainer decision recorded in data/strategy-operating-calls.json ")
                .Append("and journaled in docs/strategy-lifecycle.md.")
                .Append(Lf);
            sb.Append(Lf);
            return;
        }

        if (calls.StopAll)
        {
            sb.Append("**No lead — StopAll.** ").Append(calls.StopAllReason).Append(Lf);
            sb.Append(Lf);
            sb.Append("No arm holds reader-facing prominence: the sections below are a diagnostic view. ")
                .Append("The next Lead call is made explicitly by a human and journaled in ")
                .Append("docs/strategy-lifecycle.md.")
                .Append(Lf);
            sb.Append(Lf);
            return;
        }

        var lead = calls.For(calls.LeadStrategyName!);
        sb.Append("**Lead: ").Append(calls.LeadStrategyName)
            .Append("** — the Lead arm governs the narrative sections and action labels of this report. ")
            .Append("A call is a declared, falsifiable decision, not an efficacy result; it changes ")
            .Append("prominence only, never a score.")
            .Append(Lf);

        if (lead is { Provenance: ResolvedCallProvenance.GateDefault, GateVerdict: { } gateVerdict })
        {
            // The Lead came from the GATE DEFAULT (GatePassed → Lead), not from the declared call — which,
            // when present, may say something else entirely (e.g. Trial). State the actual provenance.
            sb.Append("- Call: Lead · actor gate-default (the AD-15 composite gate passed for this arm; ")
                .Append("gate verdict id ")
                .Append(gateVerdict.VerdictId.Length > 0 ? gateVerdict.VerdictId : "(unavailable)")
                .Append(')')
                .Append(Lf);
            if (lead.Declared is { } overridden)
            {
                sb.Append("- Declared call (superseded by the gate default): ")
                    .Append(overridden.Call.ToString())
                    .Append(" · ").Append(ActorToken(overridden.Actor))
                    .Append(" · as of ").Append(Utc(overridden.AsOfUtc))
                    .Append(" — ").Append(overridden.Basis)
                    .Append(Lf);
            }
        }
        else if (lead?.Declared is { } declared)
        {
            sb.Append("- Call: Lead · actor ").Append(ActorToken(declared.Actor))
                .Append(" · as of ").Append(Utc(declared.AsOfUtc))
                .Append(" · review by ").Append(Utc(declared.ReviewByUtc))
                .Append(Lf);
            sb.Append("- Basis: ").Append(declared.Basis).Append(Lf);
            if (!string.IsNullOrWhiteSpace(declared.ResolutionRule))
            {
                sb.Append("- Resolution rule: ").Append(declared.ResolutionRule).Append(Lf);
            }

            if (declared.Resolution is { } resolution)
            {
                sb.Append("- Resolution: ").Append(resolution.Outcome)
                    .Append(" at ").Append(Utc(resolution.ResolvedAtUtc))
                    .Append(" — evidence: ").Append(resolution.EvidenceRef)
                    .Append(Lf);
            }
        }

        sb.Append(Lf);
    }

    // Spec 184 §1+§2: one row per arm carrying its effective call (with provenance and, for DoNotLead and
    // Trial arms, the declared basis) and its computed evidence status. Comparators are listed too — they
    // carry no call, ever, but their descriptive status is not hidden.
    private static void AppendCallsAndEvidenceStatus(
        StringBuilder sb, StrategyLifecycleReportModel lifecycle, IReadOnlyList<StrategyReportSection> strategies)
    {
        sb.Append("### Calls and evidence status").Append(Lf);
        sb.Append(Lf);
        sb.Append("Evidence status is computed, descriptive and never a verdict; the call is a recorded ")
            .Append("decision. The two are stated side by side and never merged.")
            .Append(Lf);
        sb.Append(Lf);
        sb.Append("| strategy | operating call | evidence status |").Append(Lf);
        sb.Append("| --- | --- | --- |").Append(Lf);

        foreach (var section in strategies)
        {
            var call = section.Purpose == StrategyPurpose.Comparator
                ? null
                : lifecycle.Calls.For(section.StrategyName);
            var status = lifecycle.StatusFor(section.StrategyName);

            sb.Append("| ").Append(EscapeTableCell(section.StrategyName))
                .Append(" | ")
                .Append(section.Purpose == StrategyPurpose.Comparator
                    ? "— (comparator; carries no call)"
                    : EscapeTableCell(FormatCall(call)))
                .Append(" | ")
                .Append(status is null ? "—" : EscapeTableCell(FormatStatus(status)))
                .Append(" |")
                .Append(Lf);
        }

        sb.Append(Lf);
    }

    // Spec 186 §3: a declared gate override that no longer binds to the current verdict is REPORTED, never
    // silently dropped — one line per stale override naming the arm, the id it bound to and the id the
    // artifact now carries. Emitted only when one exists, so a report with no stale override is
    // byte-identical to the pre-186 output.
    private static void AppendStaleGateOverrides(
        StringBuilder sb, StrategyLifecycleReportModel lifecycle)
    {
        var stale = lifecycle.Calls.StaleOverrides;
        if (stale.Count == 0)
        {
            return;
        }

        sb.Append("### Stale gate override").Append(Lf);
        sb.Append(Lf);
        sb.Append("A declared override binds to ONE gate verdict by id. The verdict below has changed ")
            .Append("since the override was declared, so the gate default re-armed — new evidence should ")
            .Append("re-open the call. Re-declare the override against the current id in ")
            .Append("data/strategy-operating-calls.json, or let the gate default stand.")
            .Append(Lf);
        sb.Append(Lf);

        foreach (var entry in stale)
        {
            sb.Append("- ").Append(entry.StrategyName)
                .Append(": overridesVerdictId ")
                .Append(entry.BoundVerdictId.Length > 0 ? entry.BoundVerdictId : "(none declared)")
                .Append(" no longer matches the current gate verdict id ")
                .Append(entry.CurrentVerdictId.Length > 0
                    ? entry.CurrentVerdictId
                    : "(unavailable — the paired artifact records no verdict identity; re-run efficacy to refresh it)")
                .Append('.')
                .Append(Lf);
        }

        sb.Append(Lf);
    }

    private static string FormatCall(ResolvedStrategyCall? call)
    {
        if (call is null)
        {
            return "—";
        }

        var text = call.Call.ToString();
        switch (call.Provenance)
        {
            case ResolvedCallProvenance.GateDefault:
                text += call.GateVerdict is { } v
                    ? $" (gate default: the AD-15 composite gate {(v.Passed ? "passed" : "failed")} for this arm)"
                    : " (gate default)";
                break;
            case ResolvedCallProvenance.ImplicitTrial:
                text += " (no declared call)";
                break;
            default:
                if (call.Declared is { } declared)
                {
                    text += $" ({ActorToken(declared.Actor)}, as of {Utc(declared.AsOfUtc)})";
                    if (declared.Call is OperatingCall.DoNotLead or OperatingCall.Trial or OperatingCall.Stop)
                    {
                        text += $" — {declared.Basis}";
                    }
                }

                break;
        }

        if (call.Declared?.Resolution is { } resolution)
        {
            text += $" · resolved {resolution.Outcome} at {Utc(resolution.ResolvedAtUtc)}"
                + $" — evidence: {resolution.EvidenceRef}";
        }

        return text;
    }

    /// <summary>
    /// Renders one computed evidence status (spec 184 §1). <c>Ranked</c> always carries its numbers (the
    /// type makes numberless-Ranked unrepresentable); a CI spanning zero renders the SENTENCE "no evidence
    /// of discrimination yet"; an unreadable artifact renders "Accruing (evidence unavailable)". A gate
    /// status renders the descriptive leaderboard numbers BESIDE it when they exist — descriptive and
    /// confirmatory facts are orthogonal and both rendered.
    /// </summary>
    private static string FormatStatus(StrategyEvidenceStatus status)
    {
        if (status.EvidenceUnavailable)
        {
            return "Accruing (evidence unavailable)";
        }

        return status.Kind switch
        {
            StrategyEvidenceStatusKind.Accruing =>
                status.Detail is { Length: > 0 } d ? $"Accruing — {d}" : "Accruing",
            StrategyEvidenceStatusKind.Ranked => "Ranked " + RankedNumbers(status.Ranked!),
            StrategyEvidenceStatusKind.GatePending =>
                "Gate pending (the precommitted AD-15 composite gate has not yet evaluated)"
                    + WithDetailAndNumbers(status),
            StrategyEvidenceStatusKind.GatePassed =>
                "Gate passed (AD-15 composite gate)" + WithDetailAndNumbers(status),
            StrategyEvidenceStatusKind.GateFailed =>
                "Gate failed (AD-15 composite gate, evaluated on its merits)" + WithDetailAndNumbers(status),
            _ => "Accruing",
        };
    }

    private static string WithDetailAndNumbers(StrategyEvidenceStatus status)
    {
        var text = string.Empty;
        if (status.Detail is { Length: > 0 } detail)
        {
            text += $" — {detail}";
        }

        if (status.Ranked is { } ranked)
        {
            text += $"; descriptive: ranked {RankedNumbers(ranked)}";
        }

        return text;
    }

    private static string RankedNumbers(RankedEvidence ranked)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"#{ranked.Rank} — out-of-sample rho {ranked.OutOfSampleRho:0.0000} (95% CI "
                + $"{ranked.Lower95:0.0000} to {ranked.Upper95:0.0000}) over {ranked.Observations} observation(s)");
        if (ranked.CiSpansZero)
        {
            // A sentence, not a verdict (spec 184 §1): noise is never converted into pass/fail ahead of
            // the precommitted gates.
            text += " — no evidence of discrimination yet";
        }

        return text;
    }

    private static string ActorToken(OperatingCallActor actor) =>
        actor == OperatingCallActor.GateDefault ? "gate-default" : "human";

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// The parenthesised arm annotation in the live-leaders strategy cell. Pre-184 semantics (no lifecycle,
    /// or no declared calls): the primary is "(primary research)" because it genuinely owns the narrative.
    /// With declared calls: the LEAD owns the narrative, so it is "(lead)" (plus "· storage primary" when
    /// it is also the storage primary) and a non-lead storage primary is "(storage primary)" — a series
    /// identity, not a prominence claim. Under StopAll no arm owns the narrative, so the storage primary is
    /// annotated as such and nothing is annotated "lead".
    /// </summary>
    private static string ArmAnnotation(StrategyReportSection section, StrategyLifecycleReportModel? lifecycle)
    {
        if (lifecycle is null || !lifecycle.Calls.HasDeclaredCalls)
        {
            return section.IsPrimary ? " (primary research)" : string.Empty;
        }

        var isLead = lifecycle.Calls.LeadStrategyName is { } lead
            && string.Equals(lead, section.StrategyName, StringComparison.OrdinalIgnoreCase);

        if (isLead)
        {
            return section.IsPrimary ? " (lead · storage primary)" : " (lead)";
        }

        return section.IsPrimary ? " (storage primary)" : string.Empty;
    }

    // ONE combined table per subsection: per strategy, at most its first LiveLeadersPerStrategy existing
    // spec-150 rows (already ranked, already evidence-filtered per spec 53 — fewer than five means fewer
    // rows, never manufactured ones). A strategy with zero surfaced rows is RETAINED with an explicit empty
    // message: an empty experimental arm is a result, not grounds to omit the arm.
    private static void AppendLiveLeadersTable(
        StringBuilder sb,
        IReadOnlyList<StrategyReportSection> sections,
        StrategyLifecycleReportModel? lifecycle,
        NewsJudgmentMarkerReportModel? newsJudgment)
    {
        // Spec 185 §4: the semantic-read marker is a MANDATORY column on EVERY leader row (research,
        // stopped-appendix and comparator tables alike). MarkerCellFor is total — including over a null
        // marker model and a company the candidate budget never selected — so an absent marker is
        // unrepresentable by construction.
        sb.Append("| strategy | rank | company | ticker | Opportunity | as-of UTC | semantic read |")
            .Append(Lf);
        sb.Append("| --- | ---: | --- | --- | ---: | --- | --- |").Append(Lf);

        foreach (var section in sections)
        {
            // The primary is labelled so a reader can tell which arm owns the narrative below (spec 176 §2:
            // "a valid primary is labelled primary research"). A Comparator primary cannot exist — the
            // strategy set rejects it at startup — so the label never contradicts the subsection.
            //
            // Spec 184: once calls are DECLARED, narrative ownership belongs to the LEAD, so the annotation
            // vocabulary changes: the lead arm is "(lead)" and the storage primary — now a series-identity
            // fact only — is "(storage primary)". Without declared calls the pre-184 label stands, because
            // its ownership claim is still true.
            var strategyCell = EscapeTableCell(section.StrategyName)
                + ArmAnnotation(section, lifecycle);

            if (section.Rows.Count == 0)
            {
                // Not a leader row (no company exists to assess), but the column count must hold.
                sb.Append("| ")
                    .Append(strategyCell)
                    .Append(" | — | No evidence-linked live scores in this report window. | — | — | — | — |")
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
                    .Append("Z | ")
                    // Spec 185 §4: exactly one of the three semantic-read states, derived by policy from
                    // the designated presentation cohort — the model never chooses presentation, and a
                    // missing/failed/stale judgment renders `? unassessed` with its reason, never nothing.
                    .Append(EscapeTableCell(
                        NewsJudgmentMarkerReportModel.MarkerCellFor(newsJudgment, row.CompanyId)))
                    .Append(" |")
                    .Append(Lf);
            }
        }

        sb.Append(Lf);
    }

    private static void AppendHighestOpportunity(StringBuilder sb, WeeklyReportModel model)
    {
        sb.Append("## Highest opportunity").Append(Lf);
        sb.Append(Lf);

        // Spec 184: under StopAll no arm holds narrative prominence, so no company narrative is built at
        // all — stated rather than left as a silently empty section.
        if (model.Entries.Count == 0 && model.Lifecycle is { Calls.StopAll: true })
        {
            sb.Append("No narrative entries: no lead — StopAll. See the operating-call banner above and ")
                .Append("the per-strategy diagnostic tables below.")
                .Append(Lf);
            sb.Append(Lf);
            return;
        }

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

        // Spec 209: the Form 4 aggregate behind this snapshot, inside its exact window. Null means no Form 4
        // evidence is linked at all, and nothing is printed — never a fabricated "0 filings".
        if (entry.InsiderActivity is { } insider)
        {
            AppendInsiderActivity(sb, insider);
        }

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

    // Spec 209: ONE line, fixed clause order, only non-zero buckets after the filing count. Every value is
    // what the store captured: a null value renders "not captured", never 0; the mixed bucket carries no
    // value at all (its persisted magnitude is Math.Max(purchase, sale) — neither net nor total); the span
    // is stated only when it was computed over the whole dated plan set. Invariant culture throughout, and
    // no wording here may contain the forbidden report-language substrings.
    private static void AppendInsiderActivity(StringBuilder sb, InsiderActivitySummary insider)
    {
        var clauses = new List<string>
        {
            Plural(insider.FilingCount, "filing"),
        };

        if (insider.PlannedDispositionCount > 0)
        {
            var clause = Plural(insider.PlannedDispositionCount, "planned-disposition filing");
            if (insider.PlannedDispositionSpanDays is { } span)
            {
                clause += " across " + Plural(span, "day");
            }
            else if (insider.PlannedDispositionUndatedCount > 0)
            {
                clause += " (span not established: "
                    + insider.PlannedDispositionUndatedCount.ToString(CultureInfo.InvariantCulture)
                    + " undated)";
            }

            clauses.Add(clause);
            clauses.Add("transaction value not captured");
        }

        if (insider.DiscretionaryPurchaseCount > 0)
        {
            clauses.Add(
                Plural(insider.DiscretionaryPurchaseCount, "discretionary purchase filing")
                + ", purchase value "
                + FormatCapturedValue(
                    insider.DiscretionaryPurchaseValue, insider.DiscretionaryPurchaseValueNotCapturedCount));
        }

        if (insider.DiscretionarySaleCount > 0)
        {
            clauses.Add(
                Plural(insider.DiscretionarySaleCount, "discretionary sale filing")
                + ", sale value "
                + FormatCapturedValue(
                    insider.DiscretionarySaleValue, insider.DiscretionarySaleValueNotCapturedCount));
        }

        if (insider.MixedCount > 0)
        {
            clauses.Add(
                Plural(insider.MixedCount, "mixed purchase-and-sale filing") + "; split and total not captured");
        }

        if (insider.NoDiscretionaryTransactionsCount > 0)
        {
            clauses.Add(
                insider.NoDiscretionaryTransactionsCount.ToString(CultureInfo.InvariantCulture)
                + " with no discretionary transactions");
        }

        if (insider.UnknownClassificationCount > 0)
        {
            clauses.Add(
                insider.UnknownClassificationCount.ToString(CultureInfo.InvariantCulture)
                + " with classification not captured");
        }

        if (insider.UnrecognisedClassificationCount > 0)
        {
            clauses.Add(
                insider.UnrecognisedClassificationCount.ToString(CultureInfo.InvariantCulture)
                + " with unrecognised classification");
        }

        if (insider.OutsideWindowCount > 0)
        {
            clauses.Add(
                insider.OutsideWindowCount.ToString(CultureInfo.InvariantCulture) + " outside the window");
        }

        sb.Append("- Insider activity (Form 4, this window): ")
            .Append(string.Join("; ", clauses))
            .Append(Lf);
    }

    // "$X" when every filing's value was captured; "not captured" when none was; "$X (k not captured)"
    // for the partial case — a partial sum is never presented as the whole.
    private static string FormatCapturedValue(decimal? value, int notCapturedCount)
    {
        if (value is not { } captured)
        {
            return "not captured";
        }

        var text = "$" + captured.ToString("N0", CultureInfo.InvariantCulture);
        return notCapturedCount > 0
            ? text + " (" + notCapturedCount.ToString(CultureInfo.InvariantCulture) + " not captured)"
            : text;
    }

    private static string Plural(int count, string singular) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + singular + (count == 1 ? string.Empty : "s");

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

            var reason = DisplayProvenanceText(signal.Reason.Trim());
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
        sb.Append(" — ")
            .Append(ev.SourceName)
            .Append(": ")
            .Append(DisplayProvenanceText(ev.ContributionReason))
            .Append(Lf);
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
            AppendStrategySection(sb, section, isFirst: first, model.Lifecycle);
            first = false;
        }
    }

    private static void AppendStrategySection(
        StringBuilder sb, StrategyReportSection section, bool isFirst, StrategyLifecycleReportModel? lifecycle)
    {
        sb.Append("## Strategy: ")
            .Append(section.StrategyName)
            .Append(" (")
            .Append(section.FormulaVersion)
            .Append(')');

        // Spec 184: once calls are declared, the narrative above follows the LEAD arm, so the pre-150
        // "primary (the series reported above)" suffix would be FALSE whenever lead ≠ storage primary.
        // The wording is therefore call-aware; without declared calls (or without a lifecycle at all) it is
        // byte-identical to pre-184.
        if (lifecycle is { Calls.HasDeclaredCalls: true })
        {
            var calls = lifecycle.Calls;
            var isLead = calls.LeadStrategyName is { } lead
                && string.Equals(lead, section.StrategyName, StringComparison.OrdinalIgnoreCase);
            if (isLead)
            {
                sb.Append(section.IsPrimary
                    ? " — lead · storage primary (the series reported above)"
                    : " — lead (the series reported above)");
            }
            else if (section.IsPrimary)
            {
                sb.Append(calls.StopAll
                    ? " — storage primary (series identity only; no lead — StopAll)"
                    : " — storage primary (series identity only; the narrative above follows the lead)");
            }
        }
        else if (section.IsPrimary)
        {
            // So a reader can tell which series the narrative sections above describe.
            sb.Append(" — primary (the series reported above)");
        }
        sb.Append(Lf);

        // Spec 184 §1: the computed evidence status, restated on the strategy's own table so the full
        // table and the status can never be read apart.
        if (lifecycle?.StatusFor(section.StrategyName) is { } status)
        {
            sb.Append("Evidence status: ").Append(FormatStatus(status)).Append(Lf);
        }

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
