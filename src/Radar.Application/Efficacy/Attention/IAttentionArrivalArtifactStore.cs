using Radar.Application.Storage;

namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// The per-file outcomes of the attention-arrival triple (spec 201 §1): each member is the shared
/// <see cref="DurableWriteResult"/> — the attempted path plus whether the content reached it. The
/// <c>*Path</c> projections keep the pre-201 shape; a path is never evidence that the file exists.
/// </summary>
public sealed record AttentionArrivalArtifactPaths(
    DurableWriteResult Json, DurableWriteResult Csv, DurableWriteResult Markdown)
{
    public string JsonPath => Json.Path;

    public string CsvPath => Csv.Path;

    public string MarkdownPath => Markdown.Path;

    public int NotPersistedCount =>
        (Json.Written ? 0 : 1) + (Csv.Written ? 0 : 1) + (Markdown.Written ? 0 : 1);
}

/// <summary>
/// The persistence seam for the AD-16 attention-arrival screen artifacts (spec 169): writes
/// <c>data/efficacy/attention-arrival-screen.{json,csv,md}</c>.
/// <para>
/// Best-effort (AD-8): a disk failure logs, never throws, and is reported on the returned per-file outcomes. It cannot
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
