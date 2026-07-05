using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;

namespace Radar.Application.Prices;

/// <summary>
/// The opt-in price-history acquisition step (AD-14). Enumerates the already-seeded watch universe
/// (<see cref="ICompanyRepository.GetAllAsync"/> → each <c>Company.Ticker</c>, blanks skipped), reads each
/// ticker's daily bars via <see cref="IPriceHistoryReader"/>, and on <c>Success</c> persists them via
/// <see cref="IPriceHistoryStore"/> — with a small polite inter-request pace between tickers (taken from the
/// injected <see cref="TimeProvider"/>, consistent with the news collector). A per-ticker read failure is
/// logged and does NOT abort the others; caller cancellation propagates.
/// <para>
/// This service has NO dependency on the evidence/signal/scoring types and produces NO <c>CollectedEvidence</c>
/// — it rides the dedicated price seam so a price bar can never become a signal or a scoring input.
/// </para>
/// </summary>
public sealed class PriceHistoryAcquirer : IPriceHistoryAcquirer
{
    private readonly ICompanyRepository _companies;
    private readonly IPriceHistoryReader _reader;
    private readonly IPriceHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly PriceAcquisitionOptions _options;
    private readonly ILogger<PriceHistoryAcquirer> _logger;

    public PriceHistoryAcquirer(
        ICompanyRepository companies,
        IPriceHistoryReader reader,
        IPriceHistoryStore store,
        TimeProvider timeProvider,
        PriceAcquisitionOptions options,
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

        // Distinct, non-blank tickers from the seeded repository (NOT by re-parsing companies.json).
        var tickers = companies
            .Select(c => c.Ticker)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fetched = 0;
        var barsStored = 0;
        var unreadable = 0;

        // Strictly sequential + paced: a small polite pace between reads (from the injected TimeProvider).
        var isFirstRequest = true;

        foreach (var ticker in tickers)
        {
            ct.ThrowIfCancellationRequested();

            if (!isFirstRequest && _options.InterRequestDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.InterRequestDelay, _timeProvider, ct).ConfigureAwait(false);
            }

            isFirstRequest = false;

            var result = await _reader.ReadDailyAsync(ticker, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                // A per-ticker failure must not abort the run; degrade to no reference data for this ticker.
                unreadable++;
                _logger.LogWarning(
                    "Price history for {Ticker} could not be read: {Detail}; skipping.",
                    ticker,
                    result.Detail ?? result.Outcome.ToString());
                continue;
            }

            var history = new PriceHistory(
                Ticker: ticker,
                Source: _options.Source,
                RetrievedAtUtc: _timeProvider.GetUtcNow(),
                Bars: result.Bars);

            await _store.WriteAsync(history, ct).ConfigureAwait(false);
            fetched++;
            barsStored += result.Bars.Count;
        }

        _logger.LogInformation(
            "Price acquisition complete: {Fetched} ticker(s) fetched, {BarsStored} bar(s) stored, "
                + "{Unreadable} source(s) unreadable.",
            fetched,
            barsStored,
            unreadable);
    }
}
