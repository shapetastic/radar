namespace Radar.Application.Pipeline;

/// <summary>
/// The as-of instant a standalone <see cref="ScoreOnlyPipelineRunner"/> pass scores at (spec 144).
/// <c>null</c> — the default — means "now", read from the injected <see cref="TimeProvider"/>.
/// <para>
/// The composition root parses the configured value, so <c>IConfiguration</c> never crosses into
/// <c>Radar.Application</c>. A PAST instant is rejected by the runner rather than honoured: scoring the
/// live series at a historical instant is a replay, and replay has its own read-only, replay-scoped path
/// (spec 139).
/// </para>
/// </summary>
public sealed class ScoringPassOptions
{
    /// <summary>The scoring instant; <c>null</c> ⇒ the current time from the injected clock.</summary>
    public DateTimeOffset? AsOfUtc { get; init; }
}
