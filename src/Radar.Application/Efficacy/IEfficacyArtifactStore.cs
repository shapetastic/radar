using Radar.Application.Storage;

namespace Radar.Application.Efficacy;

/// <summary>
/// The per-file outcomes of one efficacy-artifact write (spec 201 §1). Each member is the shared
/// <see cref="DurableWriteResult"/>: the ATTEMPTED path plus whether the content actually reached it. The
/// <c>*Path</c> projections keep the pre-201 shape for readers that only need the target path — a path is
/// never evidence that the file exists; the outcome is.
/// </summary>
public sealed record EfficacyArtifactPaths(DurableWriteResult Svg, DurableWriteResult Csv)
{
    public string SvgPath => Svg.Path;

    public string CsvPath => Csv.Path;

    /// <summary>How many of the two files did NOT land — a measured count, never inferred from the paths.</summary>
    public int NotPersistedCount => (Svg.Written ? 0 : 1) + (Csv.Written ? 0 : 1);
}

/// <summary>The per-file outcomes of the strategy-leaderboard pair (spec 201 §1; see <see cref="EfficacyArtifactPaths"/>).</summary>
public sealed record StrategyLeaderboardPaths(DurableWriteResult Csv, DurableWriteResult Markdown)
{
    public string CsvPath => Csv.Path;

    public string MarkdownPath => Markdown.Path;

    public int NotPersistedCount => (Csv.Written ? 0 : 1) + (Markdown.Written ? 0 : 1);
}

/// <summary>The per-file outcomes of the paired-comparison triple (spec 201 §1; see <see cref="EfficacyArtifactPaths"/>).</summary>
public sealed record PairedComparisonPaths(
    DurableWriteResult Csv, DurableWriteResult Markdown, DurableWriteResult BlocksCsv)
{
    public string CsvPath => Csv.Path;

    public string MarkdownPath => Markdown.Path;

    public string BlocksCsvPath => BlocksCsv.Path;

    public int NotPersistedCount =>
        (Csv.Written ? 0 : 1) + (Markdown.Written ? 0 : 1) + (BlocksCsv.Written ? 0 : 1);
}

/// <summary>
/// The persistence seam for the per-company efficacy artifacts (AD-14 read side): writes the SVG + CSV under
/// <c>data/efficacy/{ticker}.{svg,csv}</c>. Best-effort (AD-8): a disk failure logs and never throws — but
/// since spec 201 §1 the outcome is REPORTED per file rather than implied by a returned path, so a caller
/// can no longer read the return value as proof of storage. It writes ONLY efficacy artifacts — never
/// evidence/signal/score.
/// </summary>
public interface IEfficacyArtifactStore
{
    Task<EfficacyArtifactPaths> WriteAsync(string ticker, string svg, string csv, CancellationToken ct);

    /// <summary>
    /// Writes the spec-140 strategy-vs-price leaderboard to <c>data/efficacy/strategy-leaderboard.{csv,md}</c>
    /// — ONE artifact per run (it compares strategies, not companies), alongside the per-company files rather
    /// than replacing any of them. Same best-effort posture as <see cref="WriteAsync"/> (AD-8): a disk failure
    /// logs, never throws, and is reported on the returned outcomes.
    /// </summary>
    Task<StrategyLeaderboardPaths> WriteLeaderboardAsync(string csv, string markdown, CancellationToken ct);

    /// <summary>
    /// Writes the spec-155 paired, purged strategy comparison to
    /// <c>data/efficacy/strategy-paired-comparison.{csv,md}</c>, plus the spec-170 per-block rows to
    /// <c>data/efficacy/strategy-paired-comparison-blocks.csv</c> — SEPARATE artifacts from the leaderboard,
    /// because the leaderboard is descriptive and this is the only result that can support the amended AD-15
    /// claim; sharing a file would let one overwrite the other's meaning. The blocks file is its own CSV
    /// (never a <c>recordType</c> discriminator in the summary CSV) so the summary keeps one homogeneous row
    /// per baseline for its existing readers. Same best-effort posture as <see cref="WriteAsync"/> (AD-8): a
    /// disk failure logs, never throws, and is reported on the returned outcomes.
    /// </summary>
    Task<PairedComparisonPaths> WritePairedComparisonAsync(
        string csv, string markdown, string blocksCsv, CancellationToken ct);
}
