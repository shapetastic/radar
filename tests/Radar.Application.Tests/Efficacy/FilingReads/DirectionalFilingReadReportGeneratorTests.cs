using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Efficacy.FilingReads;

using static Radar.Application.Tests.Efficacy.FilingReads.FilingReadTestFakes;

namespace Radar.Application.Tests.Efficacy.FilingReads;

/// <summary>
/// The generator's contract: it writes the triple, it reports a write that did not land, and NOTHING it can
/// hit aborts the surrounding run — a read-side measurement must never damage the record it reports on.
/// </summary>
public sealed class DirectionalFilingReadReportGeneratorTests
{
    [Fact]
    public async Task Generate_WritesTheArtifactTriple()
    {
        var store = new RecordingDirectionalFilingReadArtifactStore();
        var generator = Generator(store);

        await generator.GenerateAsync(CancellationToken.None);

        var written = Assert.Single(store.Written);
        Assert.Contains("directional-filing-read-distribution-v1", written.Json, StringComparison.Ordinal);
        Assert.Contains("directional-filing-read-distribution-v1", written.Markdown, StringComparison.Ordinal);
        Assert.Contains("accession,", written.Csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedArtifactWrite_IsNonFatal_AndTheRunContinues()
    {
        var store = new RecordingDirectionalFilingReadArtifactStore(failWrites: true);
        var generator = Generator(store);

        // No throw: the write failure is logged as a warning, and the surrounding run is untouched.
        await generator.GenerateAsync(CancellationToken.None);

        Assert.Single(store.Written);
    }

    [Fact]
    public async Task AFailingArtifactStore_DoesNotAbortTheRun()
    {
        var generator = new DirectionalFilingReadReportGenerator(
            Reporter(FakeAnalyzedFilingReadCorpus.Of(Directional("a"))),
            new DirectionalFilingReadRenderer(),
            new ThrowingArtifactStore(),
            NullLogger<DirectionalFilingReadReportGenerator>.Instance);

        await generator.GenerateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_Propagates_RatherThanBeingSwallowedAsAFailedReport()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var generator = Generator(new RecordingDirectionalFilingReadArtifactStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => generator.GenerateAsync(cts.Token));
    }

    private static DirectionalFilingReadReportGenerator Generator(
        IDirectionalFilingReadArtifactStore store) =>
        new(
            Reporter(FakeAnalyzedFilingReadCorpus.Of(Directional("a"))),
            new DirectionalFilingReadRenderer(),
            store,
            NullLogger<DirectionalFilingReadReportGenerator>.Instance);

    private sealed class ThrowingArtifactStore : IDirectionalFilingReadArtifactStore
    {
        public Task<DirectionalFilingReadArtifactPaths> WriteAsync(
            string json, string csv, string markdown, CancellationToken ct) =>
            throw new IOException("disk gone");
    }
}
