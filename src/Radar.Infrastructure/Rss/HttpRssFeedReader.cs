using System.ServiceModel.Syndication;
using System.Xml;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.Rss;

/// <summary>
/// Fetches and parses an RSS/Atom feed over HTTP using <c>SyndicationFeed</c>. A flaky or malformed
/// feed never crashes the run: non-success status, transport errors, cancellation of the request, and
/// malformed XML all degrade to an empty list with a warning. All HTTP/XML/Syndication code stays in
/// Infrastructure (AD-5).
/// </summary>
internal sealed class HttpRssFeedReader : IRssFeedReader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpRssFeedReader> _logger;

    public HttpRssFeedReader(HttpClient httpClient, ILogger<HttpRssFeedReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RssFeedItem>> ReadAsync(string feedUrl, CancellationToken ct)
    {
        Stream stream;
        try
        {
            using var response = await _httpClient
                .GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "RSS feed {FeedUrl} returned non-success status {StatusCode}; skipping.",
                    feedUrl,
                    (int)response.StatusCode);
                return [];
            }

            // Materialize the body before disposing the response so parsing can happen synchronously.
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            stream = new MemoryStream(bytes, writable: false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "RSS feed {FeedUrl} fetch failed; skipping.", feedUrl);
            return [];
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "RSS feed {FeedUrl} fetch timed out or was cancelled; skipping.", feedUrl);
            return [];
        }

        using (stream)
        {
            try
            {
                using var xmlReader = XmlReader.Create(stream);
                var feed = SyndicationFeed.Load(xmlReader);
                if (feed is null)
                {
                    _logger.LogWarning("RSS feed {FeedUrl} parsed to no feed; skipping.", feedUrl);
                    return [];
                }

                var items = new List<RssFeedItem>();
                foreach (var item in feed.Items)
                {
                    ct.ThrowIfCancellationRequested();

                    var link = item.Links.Count > 0 ? item.Links[0].Uri?.ToString() : null;
                    items.Add(new RssFeedItem(
                        Id: item.Id,
                        Title: item.Title?.Text ?? string.Empty,
                        Summary: item.Summary?.Text,
                        Link: link,
                        PublishedAt: item.PublishDate == default ? null : item.PublishDate));
                }

                return items;
            }
            catch (XmlException ex)
            {
                _logger.LogWarning(ex, "RSS feed {FeedUrl} returned malformed XML; skipping.", feedUrl);
                return [];
            }
        }
    }
}
