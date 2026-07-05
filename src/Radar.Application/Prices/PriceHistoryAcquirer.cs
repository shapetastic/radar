using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;

namespace Radar.Application.Prices;

/// <summary>
/// The opt-in price-history acquisition step (AD-14): enumerates the seeded watch universe
/// (<see cref="ICompanyRepository.GetAllAsync"/>), and for each non-blank <c>Ticker</c> reads its daily bars via
/// <see cref="IPriceHistoryReader"/> and, on success, persists them via <see cref="IPriceHistoryStore"/>. It has
/// NO dependency on any evidence/signal/scoring type and produces no <c>CollectedEvidence</c> — price rides a
/// seam structurally separate from the evidence → signal → score pipeline, and this step runs OUTSIDE
/// <c>IRadarPipeline</c>.
/// <para>
/// Tickers are processed strictly sequentially with a small polite inter-request pace (from options, timed by
/// the injected <see cref="TimeProvider"/>, mirroring the News collector). A per-ticker read/store failure is
/// logged and does not abort the others; only caller cancellation propagates. A per-run summary is logged
/// (tickers fetched / bars stored / sources unreadable).
/// </para>
/// </summary>
public sealed class PriceHistoryAcquirer : IPriceHistoryAcquirer
{
    private readonly ICompanyRepository _companyRepository;
    private readonly IPriceHistoryReader _reader;
    private readonly IPriceHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly PriceAcquisitionOptions _options;
    private readonly ILogger<PriceHistoryAcquirer> _logger;

    public PriceHistoryAcquirer(
        ICompanyRepository companyRepository,
        IPriceHistoryReader reader,
        IPriceHistoryStore store,
        TimeProvider timeProvider,
        PriceAcquisitionOptions options,
        ILogger<PriceHistoryAcquirer> logger)
    {
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _companyRepository = companyRepository;
        _reader = reader;
        _store = store;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task AcquireAsync(CancellationToken ct)
    {
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);

        var tickersChecked = 0;
        var tickersFetched = 0;
        var sourcesUnreadable = 0;
        var barsStored = 0;

        // Strictly sequential (never fanned out) + paced: a small polite pace between reads AFTER the first.
        var isFirstRequest = true;

        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();

            var ticker = company.Ticker?.Trim();
            if (string.IsNullOrEmpty(ticker))
            {
                // A company without a ticker has no price series to fetch — skip rather than fabricate one.
                continue;
            }

            tickersChecked++;

            // PACE: before each request AFTER the first, wait so successive tickers stay polite.
            if (!isFirstRequest && _options.InterRequestDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.InterRequestDelay, _timeProvider, ct).ConfigureAwait(false);
            }

            isFirstRequest = false;

            var result = await _reader.ReadDailyAsync(ticker, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                sourcesUnreadable++;
                _logger.LogWarning(
                    "Price history for '{Ticker}' could not be read: {Detail}; skipping.",
                    ticker,
                    result.Detail ?? result.Outcome.ToString());
                continue;
            }

            var history = new PriceHistory(
                Ticker: ticker,
                Source: _reader.SourceName,
                RetrievedAtUtc: _timeProvider.GetUtcNow(),
                Bars: result.Bars);

            await _store.WriteAsync(history, ct).ConfigureAwait(false);
            tickersFetched++;
            barsStored += result.Bars.Count;
        }

        _logger.LogInformation(
            "Price history acquisition complete: {TickersFetched}/{TickersChecked} ticker(s) fetched, "
                + "{BarsStored} bar(s) stored, {SourcesUnreadable} source(s) unreadable.",
            tickersFetched,
            tickersChecked,
            barsStored,
            sourcesUnreadable);
    }
}
