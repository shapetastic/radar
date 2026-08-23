using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.Lifecycle;

/// <summary>
/// Spec 184 §4, asserted on the TYPE GRAPH (the EfficacyReadOnlyGuardrailTests pattern): the status/call
/// layer lives OUTSIDE the scoring closure. Neither the scoring namespace nor the pipeline namespace may
/// reach a <c>Radar.Application.Lifecycle</c> type — a call changes reader-facing prominence only, never a
/// score, a snapshot, a series identity or a fingerprint. Positive controls pin that the guard is not
/// vacuous: the REPORTING side (builder + renderer + model) genuinely consumes the lifecycle types.
/// </summary>
public sealed class StrategyLifecycleBoundaryTests
{
    private const string ScoringNamespace = "Radar.Application.Scoring";
    private const string PipelineNamespace = "Radar.Application.Pipeline";
    private const string LifecycleNamespace = "Radar.Application.Lifecycle";

    [Fact]
    public void ScoringTypeGraph_CanNeverReachALifecycleType()
    {
        AssertNoLeak(ScoringNamespace, typeof(ScoringInput));
    }

    [Fact]
    public void PipelineTypeGraph_CanNeverReachALifecycleType()
    {
        // The pipeline reaches the report through IWeeklyReportBuilder/WeeklyReportResult only. Keeping
        // calls/statuses OFF StrategyReportSection (they live on the renderer-facing model instead) is
        // what makes this hold — a future "just add the call to the section record" edit fails here.
        AssertNoLeak(PipelineNamespace, typeof(PipelineOptions));
    }

    [Fact]
    public void ReportingSide_DoesReachLifecycle_SoTheGuardIsNotVacuous()
    {
        var reaches = TypeGraphClosure
            .TransitiveClosure([typeof(WeeklyReportBuilder), typeof(WeeklyReportModel)])
            .Any(t => t.Namespace is not null
                && t.Namespace.StartsWith(LifecycleNamespace, StringComparison.Ordinal));

        Assert.True(
            reaches,
            "The weekly report builder/model are supposed to consume the spec-184 lifecycle types; if they "
                + "stopped, the boundary guards above would pass while proving nothing.");
    }

    private static void AssertNoLeak(string rootNamespace, Type mustContain)
    {
        var assembly = typeof(ScoringInput).Assembly;
        var roots = assembly.GetTypes()
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(rootNamespace, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(roots);
        Assert.Contains(mustContain, roots);

        var leaks = TypeGraphClosure.TransitiveClosure(roots)
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith(LifecycleNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Spec 184 §4: no lifecycle (status/call) type may be reachable from {rootNamespace}, but "
                + "these are: " + string.Join(", ", leaks));
    }
}
