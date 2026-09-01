using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Pipeline;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Pipeline;

/// <summary>
/// SPEC 206 §3 — the load-bearing same-process sequence, through the REAL production shape: one
/// <see cref="FileRawEvidenceStore"/> serving as BOTH <see cref="IEvidenceRepository"/> and
/// <see cref="IRawEvidenceStore"/> (spec 142), driven by a real <see cref="CollectionPass"/>.
/// <list type="number">
/// <item>Pass 1: the raw write FAILS ⇒ the item is excluded from all downstream work (no extraction, no
/// signal) and counted once — and, crucially, it is stranded NOWHERE: not in the hydrated in-memory index
/// where the pre-206 <c>AddIfNewAsync</c>-first ordering suppressed every same-process retry.</item>
/// <item>Pass 2, SAME process, disk recovered: the SAME evidence writes successfully ⇒ it is extracted
/// exactly once and the measured count is 0.</item>
/// <item>Pass 3: the durable dedupe holds ⇒ no re-extraction.</item>
/// </list>
/// Finally the persisted signal's evidence must resolve from a FRESHLY hydrated store — the provenance
/// chain the whole invariant exists to protect.
/// </summary>
public sealed class CollectionPassRawEvidenceDurabilityTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Observed = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    private const string CompanyName = "Northwind Robotics";
    private const string RawText =
        "Northwind Robotics announced a major new customer win with a Fortune 100 partner today.";

    private readonly string _tempDir;
    private readonly string _evidenceRoot;

    public CollectionPassRawEvidenceDurabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "radar-raw-durability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _evidenceRoot = Path.Combine(_tempDir, "evidence-root");
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

    [Fact]
    public async Task FailedRawWrite_IsRetryableInTheSameProcess_ExtractedExactlyOnce_ThenDeduped()
    {
        // The failure double: the evidence ROOT path exists as a FILE, so Directory.CreateDirectory throws
        // and the write degrades gracefully. Deleting it "recovers the disk" for pass 2.
        await File.WriteAllTextAsync(_evidenceRoot, "not a directory");

        var store = new FileRawEvidenceStore(
            new FileRawEvidenceStoreOptions { RootDirectory = _evidenceRoot },
            NullLogger<FileRawEvidenceStore>.Instance);
        var log = new CapturingLogger<CollectionPass>();
        var extractor = new RecordingExtractor();
        var signals = new InMemorySignalRepository();
        var companies = new InMemoryCompanyRepository();
        var companyId = Guid.NewGuid();
        await companies.AddAsync(
            new CompanyBuilder().WithId(companyId).WithName(CompanyName).WithTicker("NWR").Build(), default);

        var pass = new CollectionPass(
            [new FixedCollector()],
            new CollectedEvidenceMapper(new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance),
            store,
            store,
            extractor,
            new CompanyResolver(companies, NullLogger<CompanyResolver>.Instance),
            new DeterministicSignalReviewer(
                new FixedTimeProvider(FixedNow), NullLogger<DeterministicSignalReviewer>.Instance),
            signals,
            new InMemorySignalReviewRepository(),
            new NullSignalFileStore(),
            companies,
            new CleanHealthValidator(),
            new FixedTimeProvider(FixedNow),
            log,
            new AllGenuineWeights());

        // ---- Pass 1: the write fails ⇒ excluded, counted once, retryable. ----
        var first = await pass.RunAsync(default);
        Assert.Equal(1, first.EvidenceCollected);
        Assert.Equal(0, first.EvidenceNew);
        Assert.Equal(1, first.RawEvidenceNotPersisted);
        Assert.Equal(0, first.SignalsExtracted);
        Assert.Empty(extractor.SeenEvidenceIds);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 of 1 collected item(s)", warning.Message);

        // ---- Pass 2: SAME process, SAME store instance, disk recovered ⇒ extracted exactly once. ----
        File.Delete(_evidenceRoot);
        var second = await pass.RunAsync(default);
        Assert.Equal(1, second.EvidenceNew);
        Assert.Equal(0, second.RawEvidenceNotPersisted);
        Assert.Equal(1, second.SignalsExtracted);
        var evidenceId = Assert.Single(extractor.SeenEvidenceIds);

        // ---- Pass 3: the durable dedupe holds ⇒ no re-extraction, measured 0. ----
        var third = await pass.RunAsync(default);
        Assert.Equal(0, third.EvidenceNew);
        Assert.Equal(0, third.RawEvidenceNotPersisted);
        Assert.Single(extractor.SeenEvidenceIds); // still exactly one extraction total

        // The persisted signal resolves its evidence from a FRESHLY hydrated store — provenance holds
        // across a process boundary, which is what "confirmed durable" has to mean.
        var signal = Assert.Single(await signals.GetByCompanyAsync(companyId, default));
        Assert.Equal(evidenceId, signal.EvidenceId);
        var fresh = new FileRawEvidenceStore(
            new FileRawEvidenceStoreOptions { RootDirectory = _evidenceRoot },
            NullLogger<FileRawEvidenceStore>.Instance);
        Assert.NotNull(await fresh.GetByIdAsync(signal.EvidenceId, default));
    }

    // ---------------------------------------------------------------- doubles

    private sealed class FixedCollector : IEvidenceCollector
    {
        public string CollectorName => "fixed";

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(new CollectionResult(
                [
                    new CollectedEvidence(
                        SourceType: EvidenceSourceType.LocalFile,
                        SourceName: "Northwind Newsroom",
                        SourceUrl: "https://example.com/nw",
                        Title: "Northwind Robotics customer win",
                        RawText: RawText,
                        PublishedAt: Observed,
                        CollectedAt: FixedNow,
                        Metadata: new Dictionary<string, string> { ["quality"] = "High" }),
                ],
                CollectionSummary.Empty));
    }

    /// <summary>Returns one valid signal per evidence and records every evidence id it was handed.</summary>
    private sealed class RecordingExtractor : ISignalExtractor
    {
        public List<Guid> SeenEvidenceIds { get; } = [];

        public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct)
        {
            SeenEvidenceIds.Add(evidence.Id);
            return Task.FromResult(new ExtractSignalsOutput(
                [
                    new ExtractedSignal(
                        CompanyMention: CompanyName,
                        SignalType: "CustomerWin",
                        Direction: "Positive",
                        Strength: 4,
                        Novelty: 4,
                        Confidence: 0.8m,
                        SupportingExcerpt: "major new customer win",
                        Reason: "Material customer win reported by the company newsroom."),
                ],
                "summary"));
        }
    }

    private sealed class NullSignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("(signal)"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    private sealed class CleanHealthValidator : ICollectionHealthValidator
    {
        public Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(CollectionHealthReport.Empty);
    }

    private sealed class AllGenuineWeights : Radar.Application.Scoring.IAttentionSourceWeights
    {
        public Radar.Application.Scoring.AttentionSourceResolution Resolve(string? sourceName) =>
            Radar.Application.Scoring.AttentionSourceResolution.Unclassified(1.0, sourceName ?? string.Empty);

        public string CanonicalDescriptor() => "test-all-genuine";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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
