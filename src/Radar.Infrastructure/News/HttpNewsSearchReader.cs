using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

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
    // Single source of truth for the endpoint. The English/US locale params are appended only when the query
    // asks for English-only coverage (see BuildRequestUri) — do NOT bake them into the base template.
    private const string SearchEndpointTemplate = "https://news.google.com/rss/search?q={0}";
    private const string EnglishUsLocaleParams = "&hl=en-US&gl=US&ceid=US:en";
    private const string TitleSuffixSeparator = " - ";
    private const int MinRecords = 1;
    private const int MaxRecords = 100;

    /// <summary>
    /// THE canonical bound on the retained <c>&lt;description&gt;</c> payload (spec 177 §3): 16 KiB of
    /// UTF-8 BYTES — bytes, not UTF-16 code units, so "16 KiB" means what it says on disk — truncated on a
    /// character boundary (never mid surrogate pair) with the cut recorded explicitly on
    /// <see cref="NewsArticleItem.DescriptionTruncated"/>.
    /// </summary>
    internal const int MaxDescriptionUtf8Bytes = 16 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpNewsSearchReader> _logger;
    private readonly TimeProvider _timeProvider;

    public HttpNewsSearchReader(
        HttpClient httpClient, ILogger<HttpNewsSearchReader> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<NewsSearchReadResult> ReadAsync(NewsSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var requestUri = BuildRequestUri(query);

        var (failure, body) = await HttpOutcomeFetch.GetAsync<NewsSearchReadResult, string>(
            _httpClient,
            requestUri,
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsStringAsync(c),
            onStatus: status =>
            {
                if (status != 429)
                {
                    return null;
                }

                // A 429 is not expected (Google News RSS is not per-IP throttled), but it is a distinct
                // outcome. No retry here — collector-level pacing/sequencing is spec 81; degrade to no evidence.
                _logger.LogWarning(
                    "News search for '{QueryPhrase}' returned HTTP 429 (rate limited); skipping.",
                    query.QueryPhrase);
                return NewsSearchReadResult.Failure(
                    NewsSearchReadOutcome.RateLimited, "HTTP 429 (rate limited)");
            },
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "News search for '{QueryPhrase}' returned non-success status {StatusCode}; skipping.",
                    query.QueryPhrase,
                    status);
                return NewsSearchReadResult.Failure(
                    NewsSearchReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "News search for '{QueryPhrase}' failed; skipping.", query.QueryPhrase);
                return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
                _logger.LogWarning(
                    ex, "News search for '{QueryPhrase}' timed out; skipping.", query.QueryPhrase);
                return NewsSearchReadResult.Failure(NewsSearchReadOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        // Non-null once we are past the failure guard above: the fetch only defaults the body on failure.
        return Parse(body!, query, ct);
    }

    /// <summary>
    /// Builds the Google News RSS search GET URL: the phrase is trimmed and URL-encoded into the <c>q=</c>
    /// parameter; when <see cref="NewsSearchQuery.EnglishOnly"/> is set the <c>hl=en-US&amp;gl=US&amp;ceid=US:en</c>
    /// locale params are appended to pin English/US coverage (mirroring how the GDELT reader honors its own
    /// <c>EnglishOnly</c> flag). When it is clear the params are omitted, so Google News applies its default
    /// locale rather than the flag silently doing nothing.
    /// </summary>
    private static Uri BuildRequestUri(NewsSearchQuery query)
    {
        var phrase = Uri.EscapeDataString(query.QueryPhrase.Trim());
        var url = string.Format(CultureInfo.InvariantCulture, SearchEndpointTemplate, phrase);
        if (query.EnglishOnly)
        {
            url += EnglishUsLocaleParams;
        }

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

        // One retrieval instant per response (all items of one read were observed together), from the
        // injected TimeProvider — never an inline clock (AD-3).
        var retrievedAt = _timeProvider.GetUtcNow();

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
            var (descriptionRaw, descriptionText, descriptionTruncated) =
                ParseDescription(element);

            items.Add(new NewsArticleItem(
                Url: url.Trim(),
                Title: title,
                SourceName: ResolveSourceName(element, title),
                PublishedAt: ParsePubDate(GetChildValue(element, "pubDate")),
                DescriptionRaw: descriptionRaw,
                DescriptionText: descriptionText,
                DescriptionTruncated: descriptionTruncated,
                PublisherSiteUrl: ResolvePublisherSiteUrl(element),
                RetrievedAt: retrievedAt));

            if (items.Count >= maxRecords)
            {
                break;
            }
        }

        return NewsSearchReadResult.Success(items);
    }

    /// <summary>
    /// The spec-177 description payload: the exact <c>&lt;description&gt;</c> element content the feed
    /// supplied (typically escaped HTML), bounded to <see cref="MaxDescriptionUtf8Bytes"/> UTF-8 bytes, plus
    /// its deterministic plain-text rendering through the ONE shared <see cref="HtmlVisibleText"/> helper.
    /// An absent or whitespace-only element degrades to <c>(null, null, false)</c> — never a copied
    /// headline; a rendering that comes out empty (markup with no visible text) leaves the raw payload but a
    /// <c>null</c> text, so "the feed said nothing readable" is a recorded fact rather than an empty string.
    /// </summary>
    private static (string? Raw, string? Text, bool Truncated) ParseDescription(XElement item)
    {
        var value = item.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "description", StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null, false);
        }

        var raw = BoundUtf8(value, MaxDescriptionUtf8Bytes, out var truncated);
        var text = HtmlVisibleText.ToPlainText(raw);
        return (raw, text.Length == 0 ? null : text, truncated);
    }

    /// <summary>
    /// The <c>&lt;source url&gt;</c> attribute as publisher-SITE provenance — kept only when it is an
    /// absolute HTTP(S) URL, else <c>null</c> (a relative/garbage value is not provenance). It is never a
    /// claimed canonical article URL: <c>&lt;link&gt;</c> stays the Google News landing URL.
    /// </summary>
    private static string? ResolvePublisherSiteUrl(XElement item)
    {
        var url = item.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "source", StringComparison.Ordinal))
            ?.Attribute("url")
            ?.Value
            .Trim();

        return !string.IsNullOrEmpty(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? url
                : null;
    }

    /// <summary>
    /// Truncates <paramref name="value"/> to the longest prefix whose UTF-8 encoding fits in
    /// <paramref name="maxBytes"/>, never splitting a surrogate pair. Deterministic and encoding-honest —
    /// the bound is declared in bytes, so it is enforced in bytes.
    /// </summary>
    private static string BoundUtf8(string value, int maxBytes, out bool truncated)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        var bytes = 0;
        var i = 0;
        while (i < value.Length)
        {
            var charCount = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
            var step = Encoding.UTF8.GetByteCount(value, i, charCount);
            if (bytes + step > maxBytes)
            {
                break;
            }

            bytes += step;
            i += charCount;
        }

        return value[..i];
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
