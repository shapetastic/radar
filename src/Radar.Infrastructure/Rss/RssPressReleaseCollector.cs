using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Domain.Companies;

namespace Radar.Infrastructure.Rss;

/// <summary>
/// Reads the per-company RSS source feeds configured on the <see cref="CollectionContext"/> and turns
/// each new feed item into a raw <see cref="CollectedEvidence"/> press release. Does not score,
/// resolve, or persist — it only answers "what new public information did we find?" A flaky feed
/// degrades to no evidence (the reader returns empty); a bad item is skipped. Company hints come only
/// from the configured feed→company binding — tickers are never invented (provenance is sacred).
/// </summary>
internal sealed class RssPressReleaseCollector : IEvidenceCollector
{
    private readonly IRssFeedReader _reader;
    private readonly ILogger<RssPressReleaseCollector> _logger;
    private readonly TimeProvider _timeProvider;

    public RssPressReleaseCollector(
        IRssFeedReader reader,
        ILogger<RssPressReleaseCollector> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _reader = reader;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public string CollectorName => "RssPressReleaseCollector";

    public string SourceType => "press_release";

    public async Task<IReadOnlyCollection<CollectedEvidence>> CollectAsync(
        CollectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Deterministic order: by CompanyId then feed Id, RSS feeds only.
        var feeds = context.SourceFeeds
            .Where(f => string.Equals(f.FeedType, "rss", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.CompanyId)
            .ThenBy(f => f.Id)
            .ToList();

        var companiesById = context.Companies.ToDictionary(c => c.Id);

        var results = new List<CollectedEvidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var feedsRead = 0;

        foreach (var feed in feeds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = await _reader.ReadAsync(feed.Url, cancellationToken).ConfigureAwait(false);
            feedsRead++;

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Title))
                {
                    continue;
                }

                // Dedupe within the batch by SourceUrl when present, else by Title.
                var dedupeKey = string.IsNullOrWhiteSpace(item.Link) ? item.Title : item.Link;
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["rssFeedUrl"] = feed.Url,
                    ["rssItemId"] = item.Id ?? item.Link ?? string.Empty,
                };

                results.Add(new CollectedEvidence(
                    SourceType: SourceType,
                    SourceName: feed.Name,
                    SourceUrl: item.Link,
                    Title: item.Title,
                    RawText: item.Summary ?? item.Title,
                    PublishedAt: item.PublishedAt,
                    CollectedAt: _timeProvider.GetUtcNow(),
                    Metadata: metadata)
                {
                    CompanyHints = BuildCompanyHints(feed.CompanyId, companiesById),
                });
            }
        }

        _logger.LogInformation(
            "RSS press-release collection complete: {FeedsRead} feed(s) read, {ItemsCollected} item(s) collected.",
            feedsRead,
            results.Count);

        return results;
    }

    private static IReadOnlyList<string> BuildCompanyHints(
        Guid companyId, IReadOnlyDictionary<Guid, Company> companiesById)
    {
        if (!companiesById.TryGetValue(companyId, out var company))
        {
            return [];
        }

        // High-confidence hint from the configured binding: prefer the ticker, fall back to the name.
        // Never invent a ticker.
        if (!string.IsNullOrWhiteSpace(company.Ticker))
        {
            return [company.Ticker];
        }

        return string.IsNullOrWhiteSpace(company.Name) ? [] : [company.Name];
    }
}
