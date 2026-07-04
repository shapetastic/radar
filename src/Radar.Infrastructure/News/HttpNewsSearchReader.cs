using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.News;

/// <summary>
/// GETs a company's news query from the keyless Google News RSS search endpoint
/// (<c>https://news.google.com/rss/search?q=&lt;phrase&gt;&amp;hl=en-US&amp;gl=US&amp;ceid=US:en</c>) and parses
/// the RSS 2.0 <c>&lt;item&gt;</c>s into <see cref="NewsArticleItem"/>s. A company with no recent coverage
/// (a valid <c>&lt;rss&gt;/&lt;channel&gt;</c> with zero <c>&lt;item&gt;</c>s), an unreachable endpoint, the
/// request's own timeout, malformed/unexpected XML, and an HTTP 429 rate-limit are each reported as a typed
/// failure on the returned <see cref="NewsSearchReadResult"/> (with a warning) rather than swallowed;
/// caller-requested cancellation still throws.
/// <para>
/// <b>Why parse the RSS by hand with <see cref="System.Xml.Linq"/> instead of reusing the shared
/// <c>HttpRssFeedReader</c>/<c>SyndicationFeed</c> helper:</b> Google News wraps the real third-party outlet
/// in a <c>&lt;source url="…"&gt;Publisher&lt;/source&gt;</c> element and appends <c>" - Publisher"</c> to the
/// title. The shared syndication helper does not cleanly expose that <c>&lt;source&gt;</c> publisher element —
/// which is exactly the distinct third-party source NAME that lifts <c>AttentionScore</c> — so this reader
/// walks the RSS 2.0 items directly to read <c>&lt;source&gt;</c> (falling back to the title suffix).
/// <see cref="XDocument.Parse(string)"/> over an in-memory string does no DTD/external-entity resolution by
/// default, so it is not XXE-exposed; an <see cref="XmlException"/> is still caught and mapped to
/// <see cref="NewsSearchReadOutcome.Malformed"/>.
/// </para>
/// <para>
/// <b>Rate-limit posture (verified from this environment):</b> unlike GDELT's per-IP DOC-API quota, Google
/// News RSS is NOT per-IP throttled — back-to-back keyless requests succeed with no key/User-Agent. A 429 is
/// therefore not expected, but it remains a distinct <see cref="NewsSearchReadOutcome.RateLimited"/> outcome
/// the reader returns immediately (no retry — collector-level pacing/sequencing is spec 81). All HTTP/XML/source
/// specifics stay in Infrastructure (AD-5). No provider SDK, no AI, no DB.
/// </para>
/// </summary>
internal sealed class HttpNewsSearchReader : INewsSearchReader
{
    private const string EndpointTemplate =
        "https://news.google.com/rss/search?q={0}&hl=en-US&gl=US&ceid=US:en";
    private const string TitleSuffixSeparator = " - ";
    private const int MinRecords = 1;
    private const int MaxRecords = 100;

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpNewsSearchReader> _logger;

    public HttpNewsSearchReader(HttpClient httpClient, ILogger<HttpNewsSearchReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<NewsSearchReadResult> ReadAsync(NewsSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var requestUri = BuildRequestUri(query);

        string body;
        try
        {
            using var response = await _httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                // A 429 is not expected (Google News RSS is not per-IP throttled), but it is a distinct
                // outcome. No retry here — collector-level pacing/sequencing is spec 81; degrade to no evidence.
                _logger.LogWarning(
                    "News search for '{QueryPhrase}' returned HTTP 429 (rate limited); skipping.",
                    query.QueryPhrase);
                return NewsSearchReadResult.Failure(
                    NewsSearchReadOutcome.RateLimited, "HTTP 429 (rate limited)");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "News search for '{QueryPhrase}' returned non-success status {StatusCode}; skipping.",
                    query.QueryPhrase,
                    (int)response.StatusCode);
                return NewsSearchReadResult.Failure(
                    NewsSearchReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}");
            }

            // Materialize the body before disposing the response so parsing can happen synchronously.
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex, "News search for '{QueryPhrase}' failed; skipping.", query.QueryPhrase);
            return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Unreachable, "transport error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; do not hide it as a failure.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
            _logger.LogWarning(
                ex, "News search for '{QueryPhrase}' timed out; skipping.", query.QueryPhrase);
            return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Timeout, "request timed out");
        }

        return Parse(body, query, ct);
    }

    /// <summary>
    /// Builds the Google News RSS search GET URL: the phrase is trimmed and URL-encoded into the <c>q=</c>
    /// parameter; the <c>hl=en-US&amp;gl=US&amp;ceid=US:en</c> locale params (already in the template) pin
    /// English/US coverage, so <see cref="NewsSearchQuery.EnglishOnly"/> needs no extra term.
    /// </summary>
    private static Uri BuildRequestUri(NewsSearchQuery query)
    {
        var phrase = Uri.EscapeDataString(query.QueryPhrase.Trim());
        var url = string.Format(CultureInfo.InvariantCulture, EndpointTemplate, phrase);
        return new Uri(url);
    }

    /// <summary>
    /// Parses an RSS 2.0 body into items. An empty/non-XML body or a document whose root is not <c>&lt;rss&gt;</c>
    /// with a <c>&lt;channel&gt;</c> is <see cref="NewsSearchReadOutcome.Malformed"/> (a bad/changed response, not
    /// a quiet company). A valid <c>&lt;rss&gt;/&lt;channel&gt;</c> with ZERO <c>&lt;item&gt;</c>s is
    /// <see cref="NewsSearchReadOutcome.Success"/> with zero items (a quiet company, not an error).
    /// </summary>
    private NewsSearchReadResult Parse(string body, NewsSearchQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning(
                "News search for '{QueryPhrase}' returned an empty body; skipping.", query.QueryPhrase);
            return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Malformed, "empty body");
        }

        XDocument document;
        try
        {
            // Parse over the in-memory string: XDocument.Parse does no DTD/external-entity resolution (not
            // XXE-exposed). Any structural break surfaces as an XmlException, mapped to Malformed below.
            document = XDocument.Parse(body);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(
                ex, "News search for '{QueryPhrase}' returned malformed XML; skipping.", query.QueryPhrase);
            return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Malformed, "malformed XML");
        }

        var rss = document.Root;
        if (rss is null || !string.Equals(rss.Name.LocalName, "rss", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "News search for '{QueryPhrase}' returned XML with an unexpected root '{Root}' "
                    + "(expected <rss>); skipping.",
                query.QueryPhrase,
                rss?.Name.LocalName ?? "(none)");
            return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Malformed, "unexpected root XML shape");
        }

        var channel = rss.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, "channel", StringComparison.Ordinal));
        if (channel is null)
        {
            _logger.LogWarning(
                "News search for '{QueryPhrase}' returned an <rss> document with no <channel>; skipping.",
                query.QueryPhrase);
            return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Malformed, "missing <channel>");
        }

        var maxRecords = Math.Clamp(query.MaxRecords, MinRecords, MaxRecords);
        var items = new List<NewsArticleItem>();

        foreach (var element in channel.Elements())
        {
            ct.ThrowIfCancellationRequested();

            if (!string.Equals(element.Name.LocalName, "item", StringComparison.Ordinal))
            {
                continue;
            }

            var url = GetChildValue(element, "link");
            if (string.IsNullOrWhiteSpace(url))
            {
                // No landing page → unattributable/undedupable; skip rather than fabricate provenance.
                continue;
            }

            var title = GetChildValue(element, "title");

            items.Add(new NewsArticleItem(
                Url: url.Trim(),
                Title: title,
                SourceName: ResolveSourceName(element, title),
                PublishedAt: ParsePubDate(GetChildValue(element, "pubDate"))));

            if (items.Count >= maxRecords)
            {
                break;
            }
        }

        return NewsSearchReadResult.Success(items);
    }

    /// <summary>
    /// The third-party outlet name: prefer the item's <c>&lt;source&gt;</c> element text (Google News wraps the
    /// real publisher there); if absent, fall back to the <c>" - Publisher"</c> suffix Google News appends to
    /// the title; if neither is present, the empty string.
    /// </summary>
    private static string ResolveSourceName(XElement item, string title)
    {
        var source = item.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "source", StringComparison.Ordinal))
            ?.Value
            .Trim();

        if (!string.IsNullOrWhiteSpace(source))
        {
            return source;
        }

        var separatorIndex = title.LastIndexOf(TitleSuffixSeparator, StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            var suffix = title[(separatorIndex + TitleSuffixSeparator.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                return suffix;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Parses an RSS 2.0 <c>&lt;pubDate&gt;</c> (RFC 1123, e.g. <c>Thu, 02 Jul 2026 12:40:51 GMT</c>) to a UTC
    /// instant, invariant culture. Returns <see langword="null"/> for an absent/unparseable value rather than
    /// throwing (spec 81's collector falls back to <c>CollectedAt</c>).
    /// </summary>
    private static DateTimeOffset? ParsePubDate(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string GetChildValue(XElement parent, string localName) =>
        parent.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value
        ?? string.Empty;
}
