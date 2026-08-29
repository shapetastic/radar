using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Prices;
using Radar.Domain.Companies;
using Radar.TestSupport;
using Radar.Application.Storage;

namespace Radar.Application.Tests.Prices;

public sealed class PriceHistoryAcquirerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static PriceHistoryAcquirer CreateAcquirer(
        ICompanyRepository repository, IPriceHistoryReader reader, IPriceHistoryStore store) =>
        new(
            repository,
            reader,
            store,
            new FixedTimeProvider(FixedNow),
            // Zero pace so the test never actually waits.
            new PriceAcquisitionOptions { InterRequestDelay = TimeSpan.Zero },
            NullLogger<PriceHistoryAcquirer>.Instance);

    [Fact]
    public async Task AcquireAsync_ReadsAndStoresEachNonBlankTicker_SkipsBlankTicker()
    {
        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithTicker("MRCY").Build(),
            new CompanyBuilder().WithTicker("AEHR").Build(),
            new CompanyBuilder().WithTicker("   ").Build(),
            new CompanyBuilder().WithTicker(null).Build());

        var reader = new RecordingReader(PriceHistoryReadResult.Success(
            [new PriceBar(new DateOnly(2026, 6, 9), 1m, 1m, 1m, 1m, 1m, 10)]));
        var store = new RecordingStore();

        var acquirer = CreateAcquirer(repo, reader, store);
        await acquirer.AcquireAsync(CancellationToken.None);

        // Read once per non-blank ticker only.
        Assert.Equal(["MRCY", "AEHR"], reader.ReadTickers);

        // Each Success is stored with the source + retrieval stamp.
        Assert.Equal(["MRCY", "AEHR"], store.Written.Select(h => h.Ticker).ToArray());
        Assert.All(store.Written, h => Assert.Equal("yahoo-chart-v8", h.Source));
        Assert.All(store.Written, h => Assert.Equal(FixedNow, h.RetrievedAtUtc));
    }

    /// <summary>
    /// Spec 201 §1: a fetched history whose store write degraded is COUNTED — one aggregated Warning for the
    /// price store, and "bars stored" in the summary counts only bars that reached disk. Mutation: revert
    /// the acquirer to discard the outcome and the Warning disappears while the summary claims 10 bars.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_FailedStoreWrite_IsCountedOnce_AndNotReportedAsStored()
    {
        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithTicker("MRCY").Build(),
            new CompanyBuilder().WithTicker("AEHR").Build());
        var reader = new RecordingReader(PriceHistoryReadResult.Success(
            [new PriceBar(new DateOnly(2026, 6, 9), 1m, 1m, 1m, 1m, 1m, 10)]));
        var store = new RecordingStore { FailWrites = true };
        var logger = new CapturingLogger<PriceHistoryAcquirer>();

        var acquirer = new PriceHistoryAcquirer(
            repo, reader, store, new FixedTimeProvider(FixedNow),
            new PriceAcquisitionOptions { InterRequestDelay = TimeSpan.Zero }, logger);
        await acquirer.AcquireAsync(CancellationToken.None);

        // Both were fetched and both writes were attempted; neither landed.
        Assert.Equal(2, store.Written.Count);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("2 of 2 fetched price histor", warning.Message);

        var summary = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("2/2 ticker(s) fetched", summary.Message);
        Assert.Contains("0 bar(s) stored", summary.Message);
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

    [Fact]
    public async Task AcquireAsync_PerTickerReadFailure_DoesNotAbortOthers_AndDoesNotThrow()
    {
        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithTicker("BADD").Build(),
            new CompanyBuilder().WithTicker("GOOD").Build());

        // BADD is Unreachable; GOOD succeeds. The failure must not abort the loop and must not store BADD.
        var reader = new RecordingReader(ticker => ticker == "BADD"
            ? PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Unreachable, "transport error")
            : PriceHistoryReadResult.Success(
                [new PriceBar(new DateOnly(2026, 6, 9), 1m, 1m, 1m, 1m, 1m, 10)]));
        var store = new RecordingStore();

        var acquirer = CreateAcquirer(repo, reader, store);

        // Does not throw.
        await acquirer.AcquireAsync(CancellationToken.None);

        Assert.Equal(["BADD", "GOOD"], reader.ReadTickers);
        // Only the successful ticker is stored; the failed one is skipped.
        var stored = Assert.Single(store.Written);
        Assert.Equal("GOOD", stored.Ticker);
    }

    [Fact]
    public async Task AcquireAsync_CallerCancellation_Propagates()
    {
        var repo = new FakeCompanyRepository(new CompanyBuilder().WithTicker("MRCY").Build());
        var reader = new RecordingReader(PriceHistoryReadResult.Success([]));
        var store = new RecordingStore();

        var acquirer = CreateAcquirer(repo, reader, store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquirer.AcquireAsync(cts.Token));
    }

    private sealed class RecordingReader : IPriceHistoryReader
    {
        private readonly Func<string, PriceHistoryReadResult> _route;

        public RecordingReader(PriceHistoryReadResult fixedResult) => _route = _ => fixedResult;

        public RecordingReader(Func<string, PriceHistoryReadResult> route) => _route = route;

        public List<string> ReadTickers { get; } = [];

        public string SourceName => "yahoo-chart-v8";

        public Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct)
        {
            ReadTickers.Add(ticker);
            return Task.FromResult(_route(ticker));
        }
    }

    private sealed class RecordingStore : IPriceHistoryStore
    {
        public List<PriceHistory> Written { get; } = [];

        /// <summary>Spec 201 §1: when set, every write reports <see cref="DurableWriteOutcome.Failed"/>.</summary>
        public bool FailWrites { get; set; }

        public Task<DurableWriteResult> WriteAsync(PriceHistory history, CancellationToken ct)
        {
            Written.Add(history);
            return Task.FromResult(DurableWriteResult.From($"data/prices/{history.Ticker}.json", !FailWrites));
        }

        public Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct) =>
            Task.FromResult<PriceHistory?>(null);
    }

    private sealed class FakeCompanyRepository(params Company[] companies) : ICompanyRepository
    {
        private readonly IReadOnlyList<Company> _companies = companies;

        public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(_companies);

        public Task AddAsync(Company company, CancellationToken ct) => throw new NotSupportedException();

        public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
