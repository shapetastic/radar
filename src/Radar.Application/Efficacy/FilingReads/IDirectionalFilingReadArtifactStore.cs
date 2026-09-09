using Radar.Application.Storage;

namespace Radar.Application.Efficacy.FilingReads;

/// <summary>
/// The per-file outcomes of the spec-218 artifact triple, following the spec-201 §1 rule the attention-arrival
/// store already follows: each member is the shared <see cref="DurableWriteResult"/> — the attempted path plus
/// whether the content actually reached it. A path is never proof that a file exists.
/// </summary>
public sealed record DirectionalFilingReadArtifactPaths(
    DurableWriteResult Json, DurableWriteResult Csv, DurableWriteResult Markdown)
{
    public string JsonPath => Json.Path;

    public string CsvPath => Csv.Path;

    public string MarkdownPath => Markdown.Path;

    public int NotPersistedCount =>
        (Json.Written ? 0 : 1) + (Csv.Written ? 0 : 1) + (Markdown.Written ? 0 : 1);
}

/// <summary>
/// The persistence seam for the spec-218 directional-filing-read measurement:
/// <c>data/efficacy/directional-filing-reads.{json,csv,md}</c>.
/// <para>
/// Best-effort (AD-8): a disk failure logs, never throws, and is reported on the returned per-file outcomes.
/// This is a read-side report; a failed report must never be able to damage the record it reports on.
/// </para>
/// <para>
/// Its own seam rather than another method on an existing artifact store, for the same reason spec 169 gave:
/// these artifacts answer a different question (what the AI filing read produces) than the price-efficacy
/// ones, and are written by a separately gated generator.
/// </para>
/// </summary>
public interface IDirectionalFilingReadArtifactStore
{
    Task<DirectionalFilingReadArtifactPaths> WriteAsync(
        string json, string csv, string markdown, CancellationToken ct);
}
