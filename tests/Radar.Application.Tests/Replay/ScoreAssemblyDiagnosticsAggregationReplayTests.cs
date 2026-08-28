using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Application.Replay;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Replay;

/// <summary>
/// SPEC 197 §5.2 item 13 — moving the score-assembly Warnings out of the shared engine must NOT make replay
/// silent, and must not simply relocate the flood.
/// <para>
/// Replay is the worst case for the pre-197 shape: strategies × as-of points × companies, so a real series
/// would have produced thousands of repeated lines. It therefore routes through the SAME
/// <c>ScoreAssemblyDiagnosticsAggregator</c> the forward pass uses, emitting at most ONE Warning per category
/// for the COMPLETE invocation — with the distinct as-of count as a fourth honesty axis, because a replay
/// legitimately spans many instants and an incidence count summed over them would otherwise read as a count
/// of distinct signals.
/// </para>
/// <para>
/// <b>MUTATION PROOFS, run rather than asserted:</b> removing <c>ScoringEngine</c>'s diagnostic return,
/// removing <c>ReplayRunner</c>'s <c>Record</c>/<c>LogAggregates</c> calls, or restoring either engine
/// Warning each turns <see cref="Replay_AffectedAcrossAsOfPoints_EmitsExactlyOneWarningPerCategory"/> red.
/// The replay's snapshots are unaffected in every case — <c>ReplayRunnerTests</c>'s replay⊆forward
/// field-for-field assertions stay green.
/// </para>
/// </summary>
public sealed class ScoreAssemblyDiagnosticsAggregationReplayTests
{
    private static readonly DateTimeOffset D = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ReplayRunnerCategory = "Radar.Application.Replay.ReplayRunner";
    private const string ScoringEngineCategory = "Radar.Application.Scoring.ScoringEngine";

    /// <summary>Two as-of points over one affected company: two lines total, not two per point.</summary>
    [Fact]
    public async Task Replay_AffectedAcrossAsOfPoints_EmitsExactlyOneWarningPerCategory()
    {
        using var harness = ReplayTestHarness.Create(TwoPointPlan());
        var companyId = await harness.SeedCompanyAsync();

        // A signal whose evidence resolves to nothing — the accrued shape spec 145 heals only forward.
        await harness.SeedSignalAsync(
            companyId, SignalType.CustomerWin, D.AddDays(-5), D.AddDays(-5), persistEvidence: false);

        // An accrued spec-191 directional news signal: a direction inherited from a judgment that never read
        // this article, which the read-side transform must score as Neutral media attention.
        await harness.SeedSignalAsync(
            companyId,
            SignalType.MediaAttention,
            D.AddDays(-4),
            D.AddDays(-4),
            direction: SignalDirection.Negative,
            metadataJson: LegacyEnvelope());

        // A perfectly healthy signal, so "affected" is never everything.
        await harness.SeedSignalAsync(
            companyId, SignalType.ProductLaunch, D.AddDays(-3), D.AddDays(-3));

        var result = await harness.ReplayRunner.RunAsync(CancellationToken.None);

        // 2 as-of points × 1 strategy × 1 company.
        Assert.Equal(2, result.SnapshotsWritten);

        // The engine that produced the counts emits NO Warning of either category.
        Assert.DoesNotContain(
            harness.Logs.Entries,
            e => e.Category == ScoringEngineCategory && e.Level == LogLevel.Warning);

        var warnings = harness.Logs.Entries
            .Where(e => e.Category == ReplayRunnerCategory && e.Level == LogLevel.Warning)
            .Select(e => e.Message)
            .ToList();

        // EXACTLY two for the complete invocation — never N × M × as-of. (A fresh label overwrites nothing,
        // so spec 148's same-label overwrite Warning is legitimately absent.)
        Assert.Equal(2, warnings.Count);

        var unresolved = Assert.Single(
            warnings, m => m.Contains("could not be resolved", StringComparison.Ordinal));
        var neutralized = Assert.Single(
            warnings, m => m.Contains("neutralized", StringComparison.Ordinal));

        // One unresolvable signal, re-evaluated at two as-of instants ⇒ 2 INCIDENCES over 2 evaluations,
        // 1 distinct company, 1 distinct strategy, 2 distinct as-of instants.
        Assert.Contains(
            "Replay 'run': 2 signal-evaluation incidence(s) were dropped", unresolved, StringComparison.Ordinal);
        Assert.Contains(
            "across 2 affected strategy-company evaluation(s), 1 distinct company/companies and 1 distinct "
                + "strateg(ies) over 2 as-of instant(s).",
            unresolved,
            StringComparison.Ordinal);
        Assert.Contains(
            "per-evaluation distinct-evidence-id counts SUM to 2", unresolved, StringComparison.Ordinal);

        // The neutralization axes stay separate, and the previous/velocity window is honestly reported as
        // zero rather than pooled into the current window's count.
        Assert.Contains(
            "Replay 'run': neutralized 2 accrued spec-191 inherited news direction(s) and 0 unverifiable "
                + "judgment-signal envelope(s) in the current window (and 0 / 0 in the previous/velocity "
                + "window)",
            neutralized,
            StringComparison.Ordinal);
        Assert.Contains(
            "across 2 affected strategy-company evaluation(s), 1 distinct company/companies and 1 distinct "
                + "strateg(ies) over 2 as-of instant(s).",
            neutralized,
            StringComparison.Ordinal);
    }

    /// <summary>§5.2 item 13's negative half: an unaffected replay emits NEITHER line.</summary>
    [Fact]
    public async Task Replay_WithNothingToReport_EmitsNeitherWarning()
    {
        using var harness = ReplayTestHarness.Create(TwoPointPlan());
        var companyId = await harness.SeedCompanyAsync();

        await harness.SeedSignalAsync(
            companyId, SignalType.CustomerWin, D.AddDays(-5), D.AddDays(-5));

        await harness.ReplayRunner.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(
            harness.Logs.Entries,
            e => e.Level == LogLevel.Warning
                && (e.Message.Contains("could not be resolved", StringComparison.Ordinal)
                    || e.Message.Contains("neutralized", StringComparison.Ordinal)));
    }

    private static ReplayPlan TwoPointPlan() =>
        new("run", ReplaySeries.Create(D, D.AddDays(1), TimeSpan.FromDays(1)));

    /// <summary>The accrued spec-191 envelope: judgment/cohort/observation provenance, no version token.</summary>
    private static string LegacyEnvelope() => EvidenceMetadata.Compose(
        new Dictionary<string, string>
        {
            [NewsDirectionalSignalMetadata.JudgmentIdKey] = "9c8f7e6d-3333-4c33-9333-cccccccccccc",
            [NewsDirectionalSignalMetadata.JudgmentCohortKeyKey] = "judge|p|s|stage1|families",
            [NewsDirectionalSignalMetadata.ObservationIdKey] = "1a2b3c4d-4444-4d44-9444-dddddddddddd",
            [NewsDirectionalSignalMetadata.TrajectoryKey] = "Deteriorating",
        },
        []);
}
