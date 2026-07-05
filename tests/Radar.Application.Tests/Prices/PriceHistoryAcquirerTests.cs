using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Prices;
using Radar.Domain.Companies;

namespace Radar.Application.Tests.Prices;

public sealed class PriceHistoryAcquirerTests
{
    private static readonly PriceBar SampleBar = new(
        new DateOnly(2026, 6, 5), 120m, 125m, 119m, 124m, 123.75m, 1_000_000L);

    private static PriceHistoryAcquirer CreateAcquirer(
        FakeCompanyRepository companies,
        FakeReader reader,
        FakeStore store) =>
        new(
            companies,
            reader,
            store,
            TimeProvider.System,
            new PriceAcquisitionOptions { InterRequestDelay = TimeSpan.Zero, Source = "yahoo-chart-v8" },
            NullLogger<PriceHistoryAcquirer>.Instance);

    [Fact]
    public async Task AcquireAsync_ReadsAndStoresEachNonBlankTicker_SkipsBlank()
    {
        var companies = new FakeCompanyRepository(
            CompanyWith("MRCY"),
            CompanyWith("AEHR"),
            CompanyWith("   "),   // blank ticker → skipped
            CompanyWith(null));   // null ticker → skipped
        var reader = new FakeReader(_ => PriceHistoryReadResult.Success([SampleBar]));
        var store = new FakeStore();

        var acquirer = CreateAcquirer(companies, reader, store);
        await acquirer.AcquireAsync(CancellationToken.None);

        // The reader was called once per non-blank ticker only (blank/null skipped).
        Assert.Equal(2, reader.RequestedTickers.Count);
        Assert.Contains("MRCY", reader.RequestedTickers);
        Assert.Contains("AEHR", reader.RequestedTickers);
        // Each Success was persisted with the configured source.
        Assert.Equal(2, store.Written.Count);
        Assert.All(store.Written, h => Assert.Equal("yahoo-chart-v8", h.Source));
        Assert.All(store.Written, h => Assert.Single(h.Bars));
    }

    [Fact]
    public async Task AcquireAsync_PerTickerFailure_DoesNotAbortOthers_AndDoesNotThrow()
    {
        var companies = new FakeCompanyRepository(
            CompanyWith("FAIL"),
            CompanyWith("OK"));
        var reader = new FakeReader(ticker => ticker == "FAIL"
            ? PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Unreachable, "transport error")
            : PriceHistoryReadResult.Success([SampleBar]));
        var store = new FakeStore();

        var acquirer = CreateAcquirer(companies, reader, store);
        await acquirer.AcquireAsync(CancellationToken.None);

        // Both tickers were attempted; only the Success was stored.
        Assert.Equal(2, reader.RequestedTickers.Count);
        var stored = Assert.Single(store.Written);
        Assert.Equal("OK", stored.Ticker);
    }

    [Fact]
    public async Task AcquireAsync_CallerCancellation_Propagates()
    {
        var companies = new FakeCompanyRepository(CompanyWith("MRCY"));
        var reader = new FakeReader(_ => PriceHistoryReadResult.Success([SampleBar]));
        var store = new FakeStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var acquirer = CreateAcquirer(companies, reader, store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => acquirer.AcquireAsync(cts.Token));
    }

    private static Company CompanyWith(string? ticker) => new(
        Id: Guid.NewGuid(),
        Name: ticker ?? "Unnamed",
        LegalName: null,
        Ticker: ticker,
        Exchange: null,
        CountryCode: null,
        Sector: null,
        Industry: null,
        Status: CompanyStatus.Active,
        CreatedAtUtc: DateTimeOffset.UnixEpoch,
        UpdatedAtUtc: DateTimeOffset.UnixEpoch,
        Themes: []);

    private sealed class FakeCompanyRepository(params Company[] companies) : ICompanyRepository
    {
        private readonly IReadOnlyList<Company> _companies = companies;

        public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(_companies);

        public Task AddAsync(Company company, CancellationToken ct) => Task.CompletedTask;

        public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<Company?>(null);

        public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompanyAlias>>([]);

        public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CompanySourceFeed>>([]);
    }

    private sealed class FakeReader(Func<string, PriceHistoryReadResult> route) : IPriceHistoryReader
    {
        public List<string> RequestedTickers { get; } = [];

        public Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct)
        {
            RequestedTickers.Add(ticker);
            return Task.FromResult(route(ticker));
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
}
