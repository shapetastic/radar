namespace Radar.Application.News;

/// <summary>
/// Declares whether the pass that captures news observations covers the FULL watch universe (spec 177 §5).
/// Registered by the composition root; follows the <c>CollectionPassOptions</c> convention (a
/// <c>TryAddSingleton</c> default so an unaware composition keeps the whole-universe behaviour).
/// <para>
/// This exists because the spec-161 company filter is applied at the seed source, so the collection pass
/// itself cannot tell a filtered universe from a small one — and a filtered collect pass may capture
/// observations but must NEVER establish the whole-universe prospective boundary. The Worker sets
/// <see cref="FullUniverse"/> from whether <c>Radar:Companies</c> is active.
/// </para>
/// </summary>
public sealed class NewsObservationCaptureOptions
{
    /// <summary>
    /// Defaults to <c>true</c>: an unfiltered composition covers the whole universe. The composition root
    /// registers <c>false</c> when a company filter restricts the pass.
    /// </summary>
    public bool FullUniverse { get; init; } = true;
}
