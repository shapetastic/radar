namespace Radar.Application.Efficacy.Attention;

/// <summary>The written attention-arrival artifact paths (best-effort; returned even when a write degraded).</summary>
public sealed record AttentionArrivalArtifactPaths(string JsonPath, string CsvPath, string MarkdownPath);

/// <summary>
/// The persistence seam for the AD-16 attention-arrival screen artifacts (spec 169): writes
/// <c>data/efficacy/attention-arrival-screen.{json,csv,md}</c>.
/// <para>
/// Best-effort (AD-8): a disk failure logs and returns the attempted paths rather than throwing. It cannot
/// affect scores or the pipeline's durable evidence — this is a read-side report, and a failed report must
/// never be able to damage the record it reports on.
/// </para>
/// <para>
/// It exists as its own seam rather than as a third method on <c>IEfficacyArtifactStore</c> because these
/// artifacts belong to a different question (attention arrival, AD-16) than the price-efficacy ones (AD-14)
/// and are written by a separately gated generator.
/// </para>
/// </summary>
public interface IAttentionArrivalArtifactStore
{
    Task<AttentionArrivalArtifactPaths> WriteAsync(
        string json, string csv, string markdown, CancellationToken ct);
}
