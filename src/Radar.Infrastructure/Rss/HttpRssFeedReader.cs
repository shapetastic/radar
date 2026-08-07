using System.ServiceModel.Syndication;
using System.Xml;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Rss;

/// <summary>
/// Fetches and parses an RSS/Atom feed over HTTP using <c>SyndicationFeed</c>. A flaky or malformed
/// feed never crashes the run: non-success status, transport errors, the request's own timeout, and
/// malformed XML are each reported as a typed failure on the returned <see cref="RssFeedReadResult"/>
/// (with a warning) rather than swallowed; caller-requested cancellation still throws. All
/// HTTP/XML/Syndication code stays in Infrastructure (AD-5).
/// <para>
/// One narrow, logged tolerance applies before parsing: leading BOM/whitespace emitted before the XML
/// declaration is skipped (see <c>LeadingNoiseLength</c>). It moves an offset only — never rewrites the body —
/// and never converts genuinely broken XML into a success.
/// </para>
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

    public async Task<RssFeedReadResult> ReadAsync(string feedUrl, CancellationToken ct)
    {
        var (failure, bytes) = await HttpOutcomeFetch.GetAsync<RssFeedReadResult, byte[]>(
            _httpClient,
            feedUrl,
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onStatus: null,
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "RSS feed {FeedUrl} returned non-success status {StatusCode}; skipping.",
                    feedUrl,
                    status);
                return RssFeedReadResult.Failure(
                    RssFeedReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(ex, "RSS feed {FeedUrl} fetch failed; skipping.", feedUrl);
                return RssFeedReadResult.Failure(RssFeedReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
                _logger.LogWarning(ex, "RSS feed {FeedUrl} fetch timed out; skipping.", feedUrl);
                return RssFeedReadResult.Failure(RssFeedReadOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        // Non-null once we are past the failure guard above: the fetch only defaults the body on failure.
        var offset = LeadingNoiseLength(bytes!);
        if (offset > 0)
        {
            // Never silent: a feed needing this tolerance has a server-side stray-output defect, so say so
            // (Debug — it is a benign, per-feed fact, not an operator action).
            _logger.LogDebug(
                "RSS feed {FeedUrl} began with {SkippedByteCount} byte(s) of BOM/whitespace before its XML "
                    + "declaration; skipping them so the declaration is the first node.",
                feedUrl,
                offset);
        }

        Stream stream = new MemoryStream(bytes!, offset, bytes!.Length - offset, writable: false);

        using (stream)
        {
            try
            {
                // Feeds are untrusted external XML: disable DTD processing and external resolvers to
                // avoid XXE and entity-expansion attacks rather than relying on framework defaults.
                var xmlSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                };
                using var xmlReader = XmlReader.Create(stream, xmlSettings);
                var feed = SyndicationFeed.Load(xmlReader);
                if (feed is null)
                {
                    _logger.LogWarning("RSS feed {FeedUrl} parsed to no feed; skipping.", feedUrl);
                    return RssFeedReadResult.Failure(RssFeedReadOutcome.Malformed, "malformed XML");
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
                        PublishedAt: item.PublishDate == default ? null : item.PublishDate,
                        Content: ExtractContent(item)));
                }

                return RssFeedReadResult.Success(items);
            }
            catch (XmlException ex)
            {
                _logger.LogWarning(ex, "RSS feed {FeedUrl} returned malformed XML; skipping.", feedUrl);
                return RssFeedReadResult.Failure(RssFeedReadOutcome.Malformed, "malformed XML");
            }
        }
    }

    /// <summary>
    /// Returns how many LEADING bytes must be skipped for the XML declaration to be the document's first
    /// node — a UTF-8 BOM followed by ASCII whitespace (space/tab/CR/LF). Some real feeds (measured:
    /// <c>https://www.idt.net/feed/</c>) emit stray blank lines before <c>&lt;?xml ...?&gt;</c> from a
    /// server-side plugin bug; <see cref="XmlReader"/> correctly rejects that ("the XML declaration must be
    /// the first node"), which discarded 360 KB of otherwise well-formed RSS.
    /// <para>
    /// Deliberately narrow, so this tolerates a known server defect without becoming a general repair pass:
    /// it only reports an OFFSET (nothing is rewritten, re-encoded or normalized, so parsing sees
    /// byte-identical content from the first non-whitespace byte onward), and it returns <c>0</c> — leaving
    /// the body completely untouched — whenever no whitespace was actually found. That last rule is why the
    /// BOM alone never counts: a lone BOM is legal, and it is <see cref="XmlReader"/>'s own encoding
    /// autodetection input, so stripping it would change how a document is decoded rather than fix anything.
    /// A genuinely malformed feed still reaches the <see cref="XmlException"/> catch and is still reported as
    /// <see cref="RssFeedReadOutcome.Malformed"/>.
    /// </para>
    /// </summary>
    private static int LeadingNoiseLength(byte[] bytes)
    {
        // Order matters: a UTF-8 BOM may precede the stray whitespace, so step over it before scanning.
        var afterBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

        var offset = afterBom;
        while (offset < bytes.Length && bytes[offset] is 0x20 or 0x09 or 0x0A or 0x0D)
        {
            offset++;
        }

        return offset == afterBom ? 0 : offset;
    }

    /// <summary>
    /// Returns the full item body when the feed supplies it: the RSS <c>content:encoded</c> element
    /// first, then the Atom/syndication <c>content</c> when it is plain text, else <c>null</c>. Raw and
    /// un-normalized — an unreadable extension never throws.
    /// </summary>
    private static string? ExtractContent(SyndicationItem item)
    {
        try
        {
            var encoded = item.ElementExtensions
                .ReadElementExtensions<string>("encoded", "http://purl.org/rss/1.0/modules/content/");
            foreach (var value in encoded)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or FormatException)
        {
            // A missing or unreadable content:encoded extension just yields null; never throw.
        }

        return item.Content is TextSyndicationContent text && !string.IsNullOrWhiteSpace(text.Text)
            ? text.Text
            : null;
    }
}
