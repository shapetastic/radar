namespace Radar.Application.Reporting;

using Radar.Application.Scoring;

/// <summary>
/// The label lines in effect for one rendered report (spec 212 §4): which arm's labels the narrative
/// carries, the <see cref="LabelThresholds"/> they were minted at, whether those lines were configured
/// explicitly on the arm or fell back to <see cref="LabelThresholds.Default"/>, whether the arm holds the
/// narrative as the effective LEAD (operating calls) or as the storage primary BY DEFAULT (no operating call
/// declared), and the policy version that applied them. The renderer states all of it in exactly one
/// place, from this record — never from a constant — so a reader can tell a label under
/// <c>disclosure-led-v11 (20 / 15)</c> from one under <c>default (60 / 40)</c>.
/// <para>
/// <c>null</c> on <see cref="WeeklyReportModel.Labels"/> means no label was minted at all — the StopAll
/// state, where no arm holds the front page and no company is labelled — and renders no banner.
/// </para>
/// </summary>
/// <param name="StrategyName">The arm whose labels the narrative carries.</param>
/// <param name="Thresholds">The Investigate / Watch lines applied.</param>
/// <param name="Explicit">True when the arm's config set <c>Labels</c>; false when <see cref="LabelThresholds.Default"/> applied because it was omitted.</param>
/// <param name="LeadDeclared">True when the arm is the effective Lead from operating calls; false when it is the storage primary by default (no operating call declared).</param>
/// <param name="PolicyVersion">The <see cref="IReportActionPolicy.Version"/> that mapped the lines to labels.</param>
public sealed record ReportLabelLines(
    string StrategyName,
    LabelThresholds Thresholds,
    bool Explicit,
    bool LeadDeclared,
    string PolicyVersion)
{
    /// <summary>The name the report was labelled on: non-blank, or the banner would name nothing.</summary>
    public string StrategyName { get; } = string.IsNullOrWhiteSpace(StrategyName)
        ? throw new ArgumentException("StrategyName must be non-empty.", nameof(StrategyName))
        : StrategyName;

    public LabelThresholds Thresholds { get; } =
        Thresholds ?? throw new ArgumentNullException(nameof(Thresholds));

    public string PolicyVersion { get; } = string.IsNullOrWhiteSpace(PolicyVersion)
        ? throw new ArgumentException("PolicyVersion must be non-empty.", nameof(PolicyVersion))
        : PolicyVersion;
}
