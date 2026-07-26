using Radar.Application.Storage;

namespace Radar.Application.Replay;

/// <summary>
/// One replay run, fully resolved (spec 139): a human-readable <see cref="Label"/> and the
/// <see cref="ReplaySeries"/> of historical as-of instants to score at.
/// <para>
/// The label is what makes replay output <b>clearly labelled</b> rather than merely isolated. Replay
/// snapshots are a hypothesis, not history: they must be trivially distinguishable from the forward efficacy
/// series (which is sacred, spec 101/108), and two different replays over the same store must be
/// distinguishable from each other. The label is therefore the top directory segment of the replay output,
/// which is exactly why it is validated here — at construction, once — against the shared
/// <see cref="StorageSegmentName"/> rule the scoring-strategy names already use. A label that could escape
/// its root is rejected before any scoring happens.
/// </para>
/// </summary>
/// <param name="Label">The replay run's identity, also its output directory segment.</param>
/// <param name="Series">The ascending, validated as-of instants to score at.</param>
public sealed record ReplayPlan(string Label, ReplaySeries Series)
{
    /// <inheritdoc cref="ReplayPlan" />
    public string Label { get; } = ValidateLabel(Label);

    /// <inheritdoc cref="ReplayPlan" />
    public ReplaySeries Series { get; } =
        Series ?? throw new ArgumentNullException(nameof(Series), "A replay plan needs an as-of series.");

    private static string ValidateLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException(
                "A replay run needs a non-blank Label; it identifies the run and names its output directory, "
                    + "so an unlabelled replay could not be told apart from another one.",
                nameof(label));
        }

        if (!StorageSegmentName.IsUsable(label))
        {
            throw new ArgumentException(
                $"'{label}' is not a usable replay Label; it is used verbatim as a storage directory segment, "
                    + $"so {StorageSegmentName.Rule}.",
                nameof(label));
        }

        return label;
    }
}
