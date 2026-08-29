using Microsoft.Extensions.Logging;

using Radar.Application.NewsRisk;
using Radar.Application.NewsTyping;
using Radar.Application.Pipeline;
using Radar.Application.Prices;
using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Reports;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.NewsRisk;
using Radar.Infrastructure.NewsTyping;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// SPEC 201 §1 at the STORE seam, over the real stores and the real <see cref="GracefulFileWriter"/>: every
/// sibling store that used to return a path regardless of the write outcome (or log an unconditional
/// "written" line) now REPORTS the outcome, and a failed write emits NO Information success line.
/// <para>
/// The failure double is the established one (spec 193/195): the store root is pointed at an existing FILE,
/// so <c>Directory.CreateDirectory</c> throws and the writer degrades gracefully. Each failing case is paired
/// with a success control so "Failed" is never vacuously true. Mutation: revert one store's gate — return
/// the path unconditionally, or log "written" before checking the bool — and that store's test goes red.
/// </para>
/// </summary>
public sealed class DurableWriteClaimTests : IDisposable
{
    private static readonly DateTimeOffset Instant = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public DurableWriteClaimTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "radar-durable-claim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    // ------------------------------------------------------------------ FilePipelineRunStore

    [Fact]
    public async Task PipelineRunStore_FailedWrite_ReportsFailed_AndLogsNoSuccessLine()
    {
        var logger = new CapturingLogger<FilePipelineRunStore>();
        var store = new FilePipelineRunStore(
            new FilePipelineRunStoreOptions { RootDirectory = await BlockedRootAsync("runs") }, logger);

        var result = await store.WriteAsync(RunRecord(), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.False(result.Written);
        Assert.False(File.Exists(result.Path));
        AssertNoSuccessLine(logger.Entries, "Wrote pipeline run record");
    }

    [Fact]
    public async Task PipelineRunStore_SuccessfulWrite_ReportsWritten()
    {
        var logger = new CapturingLogger<FilePipelineRunStore>();
        var store = new FilePipelineRunStore(
            new FilePipelineRunStoreOptions { RootDirectory = Path.Combine(_tempDir, "runs") }, logger);

        var result = await store.WriteAsync(RunRecord(), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, result.Outcome);
        Assert.True(File.Exists(result.Path));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Wrote pipeline run record", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ FileReportWriter

    [Fact]
    public async Task ReportWriter_FailedWrite_ReportsFailed_AndLogsNoSuccessLine()
    {
        var logger = new CapturingLogger<FileReportWriter>();
        var writer = new FileReportWriter(
            new FileReportWriterOptions { RootDirectory = await BlockedRootAsync("reports") }, logger);

        var result = await writer.WriteAsync(Report(), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(result.Path));
        AssertNoSuccessLine(logger.Entries, "Wrote weekly report");
    }

    [Fact]
    public async Task ReportWriter_SuccessfulWrite_ReportsWritten()
    {
        var logger = new CapturingLogger<FileReportWriter>();
        var writer = new FileReportWriter(
            new FileReportWriterOptions { RootDirectory = Path.Combine(_tempDir, "reports") }, logger);

        var result = await writer.WriteAsync(Report(), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, result.Outcome);
        Assert.True(File.Exists(result.Path));
    }

    // ------------------------------------------------------------------ FileScoringConfigStore (both sites)

    [Fact]
    public async Task ScoringConfigStore_FailedConfigWrite_ReportsFailed_AndLogsNoSuccessLine()
    {
        var logger = new CapturingLogger<FileScoringConfigStore>();
        var store = new FileScoringConfigStore(
            new FileScoringConfigStoreOptions { RootDirectory = await BlockedRootAsync("configs") }, logger);

        var result = await store.WriteIfNewAsync(Config(), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(result.Path));
        AssertNoSuccessLine(logger.Entries, "Wrote effective scoring config");
    }

    [Fact]
    public async Task ScoringConfigStore_SerializationFailure_ReportsFailed_NotAPath()
    {
        // The second failure branch of WriteIfNewAsync: JSON serialization throws (NaN weight) BEFORE the
        // writer is reached. Pre-201 this returned the path exactly like a success.
        var logger = new CapturingLogger<FileScoringConfigStore>();
        var store = new FileScoringConfigStore(
            new FileScoringConfigStoreOptions { RootDirectory = Path.Combine(_tempDir, "configs") }, logger);

        var result = await store.WriteIfNewAsync(
            Config(new ScoringWeights { AttentionHalfSaturation = double.NaN }), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(result.Path));
        AssertNoSuccessLine(logger.Entries, "Wrote effective scoring config");
    }

    [Fact]
    public async Task ScoringConfigStore_ExistingConfig_ReportsAlreadyAvailable_AndWrittenIsTrue()
    {
        // Insert-if-new: the SECOND call skips the write. Since spec 202 §1 that skip is the distinct
        // AlreadyAvailable outcome ("found it there", not "wrote it now") — but Written stays true, because
        // the content-addressed file demonstrably exists, which is the only thing the caller is asking.
        var logger = new CapturingLogger<FileScoringConfigStore>();
        var store = new FileScoringConfigStore(
            new FileScoringConfigStoreOptions { RootDirectory = Path.Combine(_tempDir, "configs") }, logger);
        var config = Config();

        var first = await store.WriteIfNewAsync(config, CancellationToken.None);
        var second = await store.WriteIfNewAsync(config, CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, first.Outcome);
        Assert.Equal(DurableWriteOutcome.AlreadyAvailable, second.Outcome);
        Assert.True(second.Written);
        Assert.Equal(first.Path, second.Path);
        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public async Task ScoringConfigStore_FailedStrategyRecordWrite_ReportsFailed_AndLogsNoSuccessLine()
    {
        var logger = new CapturingLogger<FileScoringConfigStore>();
        var store = new FileScoringConfigStore(
            new FileScoringConfigStoreOptions { RootDirectory = await BlockedRootAsync("configs2") }, logger);

        var result = await store.RecordStrategyFingerprintAsync(
            "momentum", "radar-scoring-fp-aaaa", CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(result.Path));
        AssertNoSuccessLine(logger.Entries, "Recorded strategy");
    }

    [Fact]
    public async Task ScoringConfigStore_SuccessfulStrategyRecordWrite_ReportsWritten()
    {
        var logger = new CapturingLogger<FileScoringConfigStore>();
        var store = new FileScoringConfigStore(
            new FileScoringConfigStoreOptions { RootDirectory = Path.Combine(_tempDir, "configs3") }, logger);

        var result = await store.RecordStrategyFingerprintAsync(
            "momentum", "radar-scoring-fp-aaaa", CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, result.Outcome);
        Assert.True(File.Exists(result.Path));
    }

    // ------------------------------------------------------------------ FilePriceHistoryStore

    [Fact]
    public async Task PriceHistoryStore_FailedWrite_ReportsFailed_AndLogsNoSuccessLine()
    {
        var logger = new CapturingLogger<FilePriceHistoryStore>();
        var store = new FilePriceHistoryStore(
            new FilePriceHistoryStoreOptions { RootDirectory = await BlockedRootAsync("prices") }, logger);

        var result = await store.WriteAsync(History("MRCY"), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(result.Path));
        AssertNoSuccessLine(logger.Entries, "price bar(s)");
    }

    [Fact]
    public async Task PriceHistoryStore_BlankTicker_ReportsFailed_NeverAPathAsProof()
    {
        var logger = new CapturingLogger<FilePriceHistoryStore>();
        var store = new FilePriceHistoryStore(
            new FilePriceHistoryStoreOptions { RootDirectory = Path.Combine(_tempDir, "prices") }, logger);

        var result = await store.WriteAsync(History("   "), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.StartsWith(_tempDir, result.Path, StringComparison.Ordinal);
        Assert.False(File.Exists(result.Path));
    }

    [Fact]
    public async Task PriceHistoryStore_SuccessfulWrite_ReportsWritten()
    {
        var logger = new CapturingLogger<FilePriceHistoryStore>();
        var store = new FilePriceHistoryStore(
            new FilePriceHistoryStoreOptions { RootDirectory = Path.Combine(_tempDir, "prices") }, logger);

        var result = await store.WriteAsync(History("MRCY"), CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Written, result.Outcome);
        Assert.True(File.Exists(result.Path));
    }

    // ------------------------------------------------------------------ FileEfficacyArtifactStore (three sites)

    [Fact]
    public async Task EfficacyArtifactStore_FailedWrites_ReportFailedPerFile_AndLogNoSuccessLine()
    {
        var logger = new CapturingLogger<FileEfficacyArtifactStore>();
        var store = new FileEfficacyArtifactStore(
            new FileEfficacyArtifactStoreOptions { RootDirectory = await BlockedRootAsync("efficacy") }, logger);

        var perCompany = await store.WriteAsync("MRCY", "<svg/>", "a,b\n", CancellationToken.None);
        var leaderboard = await store.WriteLeaderboardAsync("csv\n", "# md\n", CancellationToken.None);
        var paired = await store.WritePairedComparisonAsync("c\n", "# m\n", "b\n", CancellationToken.None);

        Assert.Equal(2, perCompany.NotPersistedCount);
        Assert.Equal(DurableWriteOutcome.Failed, perCompany.Svg.Outcome);
        Assert.Equal(DurableWriteOutcome.Failed, perCompany.Csv.Outcome);
        Assert.Equal(2, leaderboard.NotPersistedCount);
        Assert.Equal(3, paired.NotPersistedCount);
        AssertNoSuccessLine(logger.Entries, "Wrote ");
    }

    [Fact]
    public async Task EfficacyArtifactStore_BlankTicker_ReportsBothFilesFailed()
    {
        var logger = new CapturingLogger<FileEfficacyArtifactStore>();
        var store = new FileEfficacyArtifactStore(
            new FileEfficacyArtifactStoreOptions { RootDirectory = Path.Combine(_tempDir, "efficacy") }, logger);

        var paths = await store.WriteAsync("   ", "<svg/>", "a\n", CancellationToken.None);

        Assert.Equal(2, paths.NotPersistedCount);
        Assert.False(File.Exists(paths.SvgPath));
        Assert.False(File.Exists(paths.CsvPath));
    }

    [Fact]
    public async Task EfficacyArtifactStore_SuccessfulWrites_ReportZeroNotPersisted()
    {
        var logger = new CapturingLogger<FileEfficacyArtifactStore>();
        var store = new FileEfficacyArtifactStore(
            new FileEfficacyArtifactStoreOptions { RootDirectory = Path.Combine(_tempDir, "efficacy") }, logger);

        var perCompany = await store.WriteAsync("MRCY", "<svg/>", "a,b\n", CancellationToken.None);
        var leaderboard = await store.WriteLeaderboardAsync("csv\n", "# md\n", CancellationToken.None);
        var paired = await store.WritePairedComparisonAsync("c\n", "# m\n", "b\n", CancellationToken.None);

        Assert.Equal(0, perCompany.NotPersistedCount);
        Assert.Equal(0, leaderboard.NotPersistedCount);
        Assert.Equal(0, paired.NotPersistedCount);
        Assert.True(File.Exists(perCompany.SvgPath));
        Assert.True(File.Exists(paired.BlocksCsvPath));
    }

    // ------------------------------------------------------------------ FileDenominatorAuditArtifactStore

    [Fact]
    public async Task DenominatorAuditArtifactStore_FailedWrites_ReportFailedPerFile_AndLogNoSuccessLine()
    {
        var logger = new CapturingLogger<FileDenominatorAuditArtifactStore>();
        var store = new FileDenominatorAuditArtifactStore(
            new FileDenominatorAuditArtifactStoreOptions { RootDirectory = await BlockedRootAsync("audit") },
            logger);

        var paths = await store.WriteAsync("csv\n", "# md\n", CancellationToken.None);

        Assert.Equal(2, paths.NotPersistedCount);
        AssertNoSuccessLine(logger.Entries, "Wrote score-move denominator audit");
    }

    [Fact]
    public async Task DenominatorAuditArtifactStore_SuccessfulWrites_ReportZeroNotPersisted()
    {
        var logger = new CapturingLogger<FileDenominatorAuditArtifactStore>();
        var store = new FileDenominatorAuditArtifactStore(
            new FileDenominatorAuditArtifactStoreOptions { RootDirectory = Path.Combine(_tempDir, "audit") },
            logger);

        var paths = await store.WriteAsync("csv\n", "# md\n", CancellationToken.None);

        Assert.Equal(0, paths.NotPersistedCount);
        Assert.True(File.Exists(paths.MarkdownPath));
    }

    // ------------------------------------------------------------------ FileAttentionArrivalArtifactStore

    [Fact]
    public async Task AttentionArrivalArtifactStore_FailedWrites_ReportFailedPerFile_AndLogNoSuccessLine()
    {
        var logger = new CapturingLogger<FileAttentionArrivalArtifactStore>();
        var store = new FileAttentionArrivalArtifactStore(
            new FileAttentionArrivalArtifactStoreOptions { RootDirectory = await BlockedRootAsync("attention") },
            logger);

        var paths = await store.WriteAsync("{}", "a\n", "# md\n", CancellationToken.None);

        Assert.Equal(3, paths.NotPersistedCount);
        AssertNoSuccessLine(logger.Entries, "Wrote attention-arrival screen");
    }

    [Fact]
    public async Task AttentionArrivalArtifactStore_SuccessfulWrites_ReportZeroNotPersisted()
    {
        var logger = new CapturingLogger<FileAttentionArrivalArtifactStore>();
        var store = new FileAttentionArrivalArtifactStore(
            new FileAttentionArrivalArtifactStoreOptions { RootDirectory = Path.Combine(_tempDir, "attention") },
            logger);

        var paths = await store.WriteAsync("{}", "a\n", "# md\n", CancellationToken.None);

        Assert.Equal(0, paths.NotPersistedCount);
        Assert.True(File.Exists(paths.JsonPath));
    }

    // ------------------------------------------------------------------ FileNewsRiskArtifactStore

    [Fact]
    public async Task NewsRiskArtifactStore_FailedWrites_LogNoWrittenLine_AndNameTheFailure()
    {
        var logger = new CapturingLogger<FileNewsRiskArtifactStore>();
        var store = new FileNewsRiskArtifactStore(
            new FileNewsRiskArtifactStoreOptions { RootDirectory = await BlockedRootAsync("news-risk") }, logger);

        await store.WriteLiveAsync("2026-08-29", "# live\n", LiveDocument(), CancellationToken.None);
        await store.WriteEvaluationAsync("# eval\n", "a,b\n", CancellationToken.None);
        await store.WriteFailedAsync("2026-08-29", "reason", CancellationToken.None);

        AssertNoSuccessLine(logger.Entries, "artifact written");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("News-risk live artifact NOT", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("News-risk evaluation artifact NOT", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("FAILED artifact could NOT be written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewsRiskArtifactStore_SuccessfulWrites_LogTheWrittenLine()
    {
        var logger = new CapturingLogger<FileNewsRiskArtifactStore>();
        var store = new FileNewsRiskArtifactStore(
            new FileNewsRiskArtifactStoreOptions { RootDirectory = Path.Combine(_tempDir, "news-risk") }, logger);

        await store.WriteLiveAsync("2026-08-29", "# live\n", LiveDocument(), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("News-risk live artifact written", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ FileNewsTypingArtifactStore

    [Fact]
    public async Task NewsTypingArtifactStore_FailedWrites_LogNoWrittenLine_AndNameTheFailure()
    {
        var logger = new CapturingLogger<FileNewsTypingArtifactStore>();
        var store = new FileNewsTypingArtifactStore(
            new FileNewsTypingArtifactStoreOptions { RootDirectory = await BlockedRootAsync("news-typing") }, logger);

        await store.WriteDecompositionAsync("2026-08-29", "# md\n", DecompositionDocument(), CancellationToken.None);
        await store.WriteFailedAsync("2026-08-29", "reason", CancellationToken.None);

        AssertNoSuccessLine(logger.Entries, "artifact written");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Attention-decomposition artifact NOT", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("FAILED artifact could NOT be written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewsTypingArtifactStore_SuccessfulWrite_LogsTheWrittenLine()
    {
        var logger = new CapturingLogger<FileNewsTypingArtifactStore>();
        var store = new FileNewsTypingArtifactStore(
            new FileNewsTypingArtifactStoreOptions { RootDirectory = Path.Combine(_tempDir, "news-typing") }, logger);

        await store.WriteDecompositionAsync("2026-08-29", "# md\n", DecompositionDocument(), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Attention-decomposition artifact written", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertNoSuccessLine(
        IEnumerable<(LogLevel Level, string Message)> entries, string successFragment)
    {
        Assert.DoesNotContain(
            entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains(successFragment, StringComparison.Ordinal));
    }

    /// <summary>The spec-193/195 failure double: a FILE where the root directory should be.</summary>
    private async Task<string> BlockedRootAsync(string name)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, "not a directory");
        return path;
    }

    private static PipelineRunRecord RunRecord() => new(
        Id: Guid.NewGuid(),
        CreatedAtUtc: Instant,
        Collectors: ["sec-edgar"],
        EvidenceCollected: 1,
        EvidenceNew: 1,
        SignalsExtracted: 1,
        SignalsValid: 1,
        SignalsApproved: 1,
        SignalsNeedingReview: 0,
        CompaniesScored: 1,
        SourcesChecked: 1,
        SourcesFailed: 0,
        ReportId: null);

    private static RadarReport Report() => new(
        Id: Guid.NewGuid(),
        ReportType: "Weekly",
        Title: "Radar Weekly",
        PeriodStartUtc: Instant.AddDays(-7),
        PeriodEndUtc: Instant,
        MarkdownContent: "# Radar Weekly\n",
        CreatedAtUtc: Instant);

    private static EffectiveScoringConfig Config(ScoringWeights? weights = null) => new(
        Fingerprint: "radar-scoring-fp-201test",
        EngineVersion: "engine-test",
        FormulaVersion: "radar-formula-v8",
        Weights: weights ?? new ScoringWeights(),
        AttentionDescriptor: "attn",
        SignalSourceDescriptor: "rules=test;",
        InsiderMaterialityDescriptor: "insider",
        MediaCollapseDescriptor: "media-collapse-v2",
        Window: TimeSpan.FromDays(30));

    private static PriceHistory History(string ticker) => new(
        ticker,
        "yahoo-chart-v8",
        Instant,
        [new PriceBar(new DateOnly(2026, 8, 28), 10m, 11m, 9m, 10m, 10m, 100)]);

    private static NewsRiskLiveDocument LiveDocument() => new(
        SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
        RunId: Guid.NewGuid(),
        SelectionAsOfUtc: Instant,
        Caveat: NewsRiskLiveDocument.LiveCaveat,
        Readers: ["reader (provider:model)"],
        Diagnostic: null,
        Companies: [],
        GeneratedAtUtc: Instant);

    private static NewsTypingDecompositionDocument DecompositionDocument() => new(
        SchemaVersion: NewsTypingDecompositionDocument.CurrentSchemaVersion,
        RunId: Guid.NewGuid(),
        WindowStartUtc: Instant.AddDays(-30),
        WindowEndUtc: Instant,
        Caveat: "caveat",
        Readers: ["reader (provider:model)"],
        CaptureProvenThisRun: true,
        Companies: [],
        ObservationsWithoutCompany: 0,
        GeneratedAtUtc: Instant);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
