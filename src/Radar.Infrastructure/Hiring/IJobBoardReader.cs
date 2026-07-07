namespace Radar.Infrastructure.Hiring;

/// <summary>
/// Reads one company's public ATS job board and normalizes it to a <see cref="JobBoardResult"/>. One
/// implementation per platform (<see cref="GreenhouseBoardReader"/>, <see cref="LeverBoardReader"/> — the
/// two JSON shapes differ), keyed by <see cref="Platform"/> in the <see cref="HiringBoardCollector"/>'s
/// platform→reader map. Mirrors the established non-SEC reader seam (typed <c>HttpClient</c> +
/// <c>System.Text.Json</c> behind an internal interface returning a typed outcome; all HTTP/JSON stays in
/// Infrastructure, AD-5).
/// </summary>
internal interface IJobBoardReader
{
    /// <summary>The platform token this reader serves, as it appears in the feed token (e.g. "greenhouse").</summary>
    string Platform { get; }

    /// <summary>
    /// The resolved board API URL for <paramref name="boardToken"/> — the exact URL
    /// <see cref="ReadAsync"/> fetches. Exposed so the collector can stamp it as the evidence
    /// <c>SourceUrl</c> without duplicating URL construction (one builder, one provenance URL).
    /// </summary>
    string BoardUrl(string boardToken);

    /// <summary>
    /// Fetches and parses the board. Never throws for source-side problems — every failure mode comes back
    /// as a typed <see cref="JobBoardReadResult"/>; only genuine caller cancellation propagates.
    /// </summary>
    Task<JobBoardReadResult> ReadAsync(string boardToken, CancellationToken ct);
}
