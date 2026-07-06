namespace Radar.Application.Efficacy;

/// <summary>
/// The opt-in efficacy-report step (AD-14 read side): builds the score↔price dataset and writes a per-company
/// SVG + CSV. Runs as a Worker step DISTINCT from and OUTSIDE <c>IRadarPipeline</c> — it reads score history +
/// price and writes only efficacy artifacts; it never feeds evidence, signals, or scoring.
/// </summary>
public interface IEfficacyReportGenerator
{
    Task GenerateAsync(CancellationToken ct);
}
