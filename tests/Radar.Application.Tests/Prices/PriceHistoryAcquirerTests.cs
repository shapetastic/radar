using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Prices;
using Radar.Domain.Companies;
using Radar.TestSupport;

namespace Radar.Application.Tests.Prices;

public sealed class PriceHistoryAcquirerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static PriceBar Bar(int day) =>
        new(new DateOnly(2026, 6, day), 100m, 101m, 99m, 100m, 100m, 1000L);

    private static PriceHistoryAcquirer CreateAcquirer(
        ICompanyRepository companies, IPriceHistoryReader reader, IPriceHistoryStore store) =>
        new(
            companies,
            reader,
            store,
            new FakeTimeProvider(Now),
            new PriceHistoryAcquirerOptions { InterRequestDelay = TimeSpan.Zero },
            NullLogger<PriceHistoryAcquirer>.Instance);

    [Fact]
    public async Task AcquireAsync_ReadsAndStoresEachNonBlankTicker_SkipsBlank()
    {
        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithTicker("MRCY").Build(),
            new CompanyBuilder().WithTicker("AEHR").Build(),
            new CompanyBuilder().WithTicker("   ").Build(),   // blank ticker → skipped
            new CompanyBuilder().WithTicker(null).Build());    // null ticker → skipped

        var reader = new FakeReader(_ => PriceHistoryReadResult.Success([Bar(6)], "yahoo-chart-v8"));
        var store = new FakeStore();

        await CreateAcquirer(repo, reader, store).AcquireAsync(CancellationToken.None);

        // The reader was called exactly once per non-blank ticker.
        Assert.Equal(["MRCY", "AEHR"], reader.Tickers);

        // Each Success was stored, keyed by ticker.
        Assert.Equal(2, store.Written.Count);
        Assert.Contains(store.Written, h => h.Ticker == "MRCY");
        Assert.Contains(store.Written, h => h.Ticker == "AEHR");
    }

    [Fact]
    public async Task AcquireAsync_PerTickerFailure_DoesNotAbortOthers()
    {
        var repo = new FakeCompanyRepository(
            new CompanyBuilder().WithTicker("GOOD1").Build(),
            new CompanyBuilder().WithTicker("BADX").Build(),
            new CompanyBuilder().WithTicker("GOOD2").Build());

        var reader = new FakeReader(ticker => ticker == "BADX"
            ? PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Unreachable, "transport error")
            : PriceHistoryReadResult.Success([Bar(6)], "yahoo-chart-v8"));
        var store = new FakeStore();

        // Must not throw despite the middle ticker failing.
        await CreateAcquirer(repo, reader, store).AcquireAsync(CancellationToken.None);

        // All three were attempted; only the two successes were stored (the failure stored nothing).
        Assert.Equal(["GOOD1", "BADX", "GOOD2"], reader.Tickers);
        Assert.Equal(2, store.Written.Count);
        Assert.DoesNotContain(store.Written, h => h.Ticker == "BADX");
    }

    [Fact]
    public async Task AcquireAsync_CallerCancellation_Propagates()
    {
        var repo = new FakeCompanyRepository(new CompanyBuilder().WithTicker("MRCY").Build());
        var reader = new FakeReader(_ => PriceHistoryReadResult.Success([Bar(6)], "yahoo-chart-v8"));
        var store = new FakeStore();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateAcquirer(repo, reader, store).AcquireAsync(cts.Token));
    }

    private sealed class FakeReader(Func<string, PriceHistoryReadResult> route) : IPriceHistoryReader
    {
        private readonly Func<string, PriceHistoryReadResult> _route = route;

        public List<string> Tickers { get; } = [];

        public Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct)
        {
            Tickers.Add(ticker);
            return Task.FromResult(_route(ticker));
        }
    }

    private sealed class FakeStore : IPriceHistoryStore
    {
        public List<PriceHistory> Written { get; } = [];

        public Task<string> WriteAsync(PriceHistory history, CancellationToken ct)
        {
            Written.Add(history);
            return Task.FromResult($"{history.Ticker}.json");
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
        public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
