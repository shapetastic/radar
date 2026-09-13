using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy.EvidenceConfidence;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.Efficacy.EvidenceConfidence;

/// <summary>
/// The generator's contract (the spec-218 posture): it writes the triple, it reports a write that did not
/// land, and NOTHING it can hit aborts the surrounding run — including the reporter failing CLOSED on a store
/// that cannot serve links, which is logged as an error and not rethrown.
/// </summary>
public sealed class EvidenceConfidenceDistributionGeneratorTests
{
    private static EvidenceConfidenceTestUniverse Universe()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("AAA", 60, 30, [EvidenceConfidenceTestUniverse.HighFiling]);
        u.AddCompany("BBB", 40, 10, [EvidenceConfidenceTestUniverse.MediumPressRelease]);
        return u;
    }

    private static EvidenceConfidenceDistributionGenerator Generator(
        EvidenceConfidenceDistributionReporter reporter,
        IEvidenceConfidenceArtifactStore store,
        ILogger<EvidenceConfidenceDistributionGenerator>? logger = null) =>
        new(
            reporter,
            new EvidenceConfidenceDistributionRenderer(),
            store,
            logger ?? NullLogger<EvidenceConfidenceDistributionGenerator>.Instance);

    [Fact]
    public async Task Generate_WritesTheArtifactTriple()
    {
        var store = new RecordingEvidenceConfidenceArtifactStore();

        await Generator(Universe().Reporter(), store).GenerateAsync(CancellationToken.None);

        var written = Assert.Single(store.Written);
        Assert.Contains(EvidenceConfidenceDistributionReporter.ArtifactVersion, written.Json, StringComparison.Ordinal);
        Assert.Contains(EvidenceConfidenceDistributionReporter.ArtifactVersion, written.Markdown, StringComparison.Ordinal);
        Assert.StartsWith("ticker,", written.Csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedArtifactWrite_IsNonFatal_AndTheRunContinues()
    {
        var store = new RecordingEvidenceConfidenceArtifactStore(failWrites: true);

        await Generator(Universe().Reporter(), store).GenerateAsync(CancellationToken.None);

        Assert.Single(store.Written);
    }

    [Fact]
    public async Task AStoreThatCannotServeLinks_FailsClosedInTheReporter_AndTheGeneratorLogsWithoutRethrowing()
    {
        var u = Universe();
        // A plain scalar store: not an IScoreSnapshotLinkReader, so the reporter throws with a named reason.
        var selector = new FakeStrategyScoreSnapshotStoreSelector()
            .With(ScoringStrategySet.DefaultStrategyName, new FakeScoreSnapshotFileStore());
        var reporter = u.Reporter(selector);

        var direct = await Assert.ThrowsAsync<InvalidOperationException>(() => reporter.BuildAsync(CancellationToken.None));
        Assert.Contains("IScoreSnapshotLinkReader", direct.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(FakeScoreSnapshotFileStore), direct.Message, StringComparison.Ordinal);

        var logger = new CapturingGeneratorLogger();
        var store = new RecordingEvidenceConfidenceArtifactStore();
        await Generator(reporter, store, logger).GenerateAsync(CancellationToken.None);

        Assert.Empty(store.Written);
        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("no artifact was written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AThrowingArtifactStore_DoesNotAbortTheRun()
    {
        await Generator(Universe().Reporter(), new ThrowingArtifactStore()).GenerateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_Propagates_RatherThanBeingSwallowedAsAFailedReport()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Generator(Universe().Reporter(), new RecordingEvidenceConfidenceArtifactStore()).GenerateAsync(cts.Token));
    }

    private sealed class ThrowingArtifactStore : IEvidenceConfidenceArtifactStore
    {
        public Task<EvidenceConfidenceArtifactPaths> WriteAsync(string json, string csv, string markdown, CancellationToken ct) =>
            throw new IOException("disk gone");
    }

    private sealed class CapturingGeneratorLogger : ILogger<EvidenceConfidenceDistributionGenerator>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
