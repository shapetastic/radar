namespace Radar.Application.Pipeline;

/// <summary>
/// Runs the Radar pipeline once: collect → store evidence → extract → resolve → review → store signals
/// → score companies → (optionally) build the weekly report. Provider-independent; deterministic given
/// a fixed clock and fixed repository/source state.
/// </summary>
public interface IRadarPipeline
{
    Task<RadarPipelineResult> RunAsync(CancellationToken ct);
}
