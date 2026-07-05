using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;

namespace Radar.Application.Prices;

/// <summary>
/// The opt-in daily price-history acquisition step (AD-14). Enumerates the already-seeded watch universe
/// (<see cref="ICompanyRepository.GetAllAsync"/>), and for each non-blank <c>Ticker</c> reads its daily bars
/// via <see cref="IPriceHistoryReader"/> and persists each <c>Success</c> via <see cref="IPriceHistoryStore"/>.
/// Reads are strictly sequential with a small polite inter-request pace (taken from the injected
/// <see cref="TimeProvider"/>, consistent with the news collector). A per-ticker read failure is logged and
/// does NOT abort the others; caller cancellation propagates.
/// <para>
/// This service has NO dependency on any evidence/signal/scoring type and produces NO
/// <c>CollectedEvidence</c>: price rides a dedicated seam that is structurally incapable of becoming a signal
/// or a scoring input. It runs OUTSIDE <c>IRadarPipeline</c> (invoked separately from the Worker after
/// seeding).
/// </para>
/// </summary>
public sealed class PriceHistoryAcquirer : IPriceHistoryAcquirer
{
    private readonly ICompanyRepository _companies;
    private readonly IPriceHistoryReader _reader;
    private readonly IPriceHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly PriceHistoryAcquirerOptions _options;
    private readonly ILogger<PriceHistoryAcquirer> _logger;

    public PriceHistoryAcquirer(
        ICompanyRepository companies,
        IPriceHistoryReader reader,
        IPriceHistoryStore store,
        TimeProvider timeProvider,
        PriceHistoryAcquirerOptions options,
        ILogger<PriceHistoryAcquirer> logger)
    {
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _companies = companies;
        _reader = reader;
        _store = store;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task AcquireAsync(CancellationToken ct)
    {
        var companies = await _companies.GetAllAsync(ct).ConfigureAwait(false);

        var tickersChecked = 0;
        var tickersStored = 0;
        var barsStored = 0;
        var tickersUnreadable = 0;

        // Strictly sequential + paced: a small polite pause between reads (after the first).
        var isFirstRequest = true;

        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();

            var ticker = company.Ticker;
            if (string.IsNullOrWhiteSpace(ticker))
            {
                // A company with no ticker has no price series to fetch; skip (not an error).
                continue;
            }

            ticker = ticker.Trim();
            tickersChecked++;

            // PACE: before each read AFTER the first, wait so successive tickers stay polite.
            if (!isFirstRequest)
            {
                await Task.Delay(_options.InterRequestDelay, _timeProvider, ct).ConfigureAwait(false);
            }

            isFirstRequest = false;

            var result = await _reader.ReadDailyAsync(ticker, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                // A per-ticker failure must not abort the run — log and continue to the next ticker.
                tickersUnreadable++;
                _logger.LogWarning(
                    "Price history for '{Ticker}' could not be read ({Outcome}): {Detail}; skipping.",
                    ticker,
                    result.Outcome,
                    result.Detail);
                continue;
            }

            var history = new PriceHistory(
                Ticker: ticker,
                Source: result.Source ?? string.Empty,
                RetrievedAtUtc: _timeProvider.GetUtcNow(),
                Bars: result.Bars);

            await _store.WriteAsync(history, ct).ConfigureAwait(false);
            tickersStored++;
            barsStored += result.Bars.Count;
        }

        _logger.LogInformation(
            "Price-history acquisition complete: {TickersChecked} ticker(s) checked, {TickersStored} stored "
                + "({BarsStored} bar(s)), {TickersUnreadable} unreadable.",
            tickersChecked,
            tickersStored,
            barsStored,
            tickersUnreadable);
    }
}
