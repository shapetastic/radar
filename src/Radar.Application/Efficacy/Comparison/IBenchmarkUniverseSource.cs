namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The read seam over the committed frozen benchmark-universe artifact (spec 183 §1). Returns <c>null</c> when
/// the artifact is missing, unparseable or fails its content-hash integrity check — the consumers then record
/// every excess observation as <c>BenchmarkUnavailable</c> (named and counted, never a silent fallback to raw
/// returns) and the rendered artifacts state that the universe could not be loaded.
/// </summary>
public interface IBenchmarkUniverseSource
{
    Task<BenchmarkUniverse?> ReadAsync(CancellationToken ct);
}
