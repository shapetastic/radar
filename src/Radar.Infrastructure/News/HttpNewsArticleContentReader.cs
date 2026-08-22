using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.News;

namespace Radar.Infrastructure.News;

/// <summary>
/// The safe, bounded, allowlist-gated publisher-content reader (spec 177 §6). Every attempt resolves to one
/// of the CLOSED <see cref="NewsArticleFetchOutcome"/>s and records the actual retrieval instant, hop count,
/// resolved URL, status, content type, truncation flag, extractor version and content hash — success or not.
/// <para>
/// Safety contract, enforced structurally:
/// <list type="bullet">
/// <item>no authentication, cookies, subscriptions, paywall bypass, browser automation or anti-bot
/// circumvention — the client is registered with cookies disabled and this class sends only the
/// contact-bearing User-Agent;</item>
/// <item>HTTP(S) only, default ports only, no embedded user-info; loopback / private / link-local /
/// CGNAT / multicast / otherwise non-public destinations are rejected BEFORE every request — the host is
/// resolved and every returned address inspected, and IP literals are validated directly — and the same
/// full check re-runs for EVERY redirect hop;</item>
/// <item>automatic redirects are disabled at the handler; at most <see cref="MaxRedirectHops"/> explicit
/// hops are followed, each re-validated (allowlist + safety + robots);</item>
/// <item><c>robots.txt</c> is honored (fetched through the same safety checks, decision cached per host for
/// the run; an unreachable robots endpoint fails CLOSED as disallowed);</item>
/// <item>requests are strictly sequential with per-host pacing; timeout and response bytes are bounded;
/// only supported textual content types are read;</item>
/// <item>extraction is the versioned deterministic <see cref="HtmlVisibleText"/> pass with the declared
/// <see cref="MaxExtractedChars"/> cap.</item>
/// </list>
/// Body text is returned only on <see cref="NewsArticleFetchOutcome.Fetched"/>, which structurally means an
/// allowlisted source — nothing else is ever requested, so a non-allowlisted body cannot even transiently
/// exist here.
/// </para>
/// </summary>
internal sealed class HttpNewsArticleContentReader : INewsArticleContentReader
{
    /// <summary>The explicit redirect-hop bound (spec 177 §6: "at most five explicit hops").</summary>
    internal const int MaxRedirectHops = 5;

    /// <summary>The declared character cap on extracted visible text (UTF-16 code units).</summary>
    internal const int MaxExtractedChars = 20_000;

    private static readonly string[] SupportedContentTypes =
    [
        "text/html",
        "application/xhtml+xml",
        "text/plain",
    ];

    private readonly HttpClient _httpClient;
    private readonly NewsArticleContentReaderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpNewsArticleContentReader> _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHostAddresses;

    // Strictly sequential: one fetch at a time per reader instance, with per-host last-request pacing.
    private readonly SemaphoreSlim _sequentialGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastRequestPerHost = new(StringComparer.OrdinalIgnoreCase);

    // Per-run robots decision cache: host → parsed disallow rules (null = robots absent ⇒ everything allowed).
    private readonly Dictionary<string, RobotsRules?> _robotsByHost = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The versioned retrieval-policy identity stamped on every result: fetch-policy version + extractor
    /// version + a digest of the sorted allowlist. Changing any of them creates a NEW policy identity on
    /// new observations; it never edits an existing record.
    /// </summary>
    private readonly string _retrievalPolicy;

    public HttpNewsArticleContentReader(
        HttpClient httpClient,
        NewsArticleContentReaderOptions options,
        TimeProvider timeProvider,
        ILogger<HttpNewsArticleContentReader> logger)
        : this(httpClient, options, timeProvider, logger, resolveHostAddresses: null)
    {
    }

    // The DNS seam is injectable for tests ONLY (network-free SSRF tests need to control what a hostname
    // resolves to); production always uses the real resolver.
    internal HttpNewsArticleContentReader(
        HttpClient httpClient,
        NewsArticleContentReaderOptions options,
        TimeProvider timeProvider,
        ILogger<HttpNewsArticleContentReader> logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHostAddresses)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _resolveHostAddresses = resolveHostAddresses
            ?? (static (host, ct) => Dns.GetHostAddressesAsync(host, ct));

        var domainDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                ",",
                options.AllowedDomains
                    .Select(d => d.Trim().ToLowerInvariant())
                    .Order(StringComparer.Ordinal)))))[..12];
        _retrievalPolicy = string.Create(
            CultureInfo.InvariantCulture,
            $"news-fetch-v1;extractor={HtmlVisibleText.Version};domains=sha256:{domainDigest}");
    }

    public async Task<NewsArticleFetchResult> FetchAsync(string url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        await _sequentialGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The whole attempt (every hop, including robots) shares one bounded deadline.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_options.Timeout);
            try
            {
                return await FetchCoreAsync(url, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The attempt's own deadline elapsed — a typed outcome, not a run cancellation.
                return Result(NewsArticleFetchOutcome.Timeout, hops: 0, resolvedUrl: null);
            }
        }
        finally
        {
            _sequentialGate.Release();
        }
    }

    private async Task<NewsArticleFetchResult> FetchCoreAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var current))
        {
            return Result(NewsArticleFetchOutcome.UnresolvedLandingUrl, 0, null);
        }

        for (var hops = 0; hops <= MaxRedirectHops; hops++)
        {
            ct.ThrowIfCancellationRequested();

            // EVERY hop — the landing URL and every redirect target alike — passes the full gate chain
            // before any request is issued to it. Order: URL shape and any locally-decidable unsafety
            // (scheme, user-info, non-default port, IP literals, obvious internal host names) FIRST — an
            // unambiguously unsafe destination reads UnsafeUrl whether or not it is allowlisted; then the
            // allowlist, BEFORE DNS, so a non-allowlisted host is never even resolved; then the DNS-address
            // publicness check; then robots.
            var gate = await ValidateDestinationAsync(current, ct).ConfigureAwait(false);
            if (gate is { } refused)
            {
                return Result(refused, hops, null);
            }

            if (!await IsAllowedByRobotsAsync(current, ct).ConfigureAwait(false))
            {
                return Result(NewsArticleFetchOutcome.RobotsDisallowed, hops, null);
            }

            await PacePerHostAsync(current.Host, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Article fetch for {Url} failed at the transport layer.", current);
                return Result(NewsArticleFetchOutcome.HttpError, hops, current.AbsoluteUri);
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (IsRedirect(response))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return Result(NewsArticleFetchOutcome.HttpError, hops, current.AbsoluteUri, status);
                    }

                    if (hops == MaxRedirectHops)
                    {
                        return Result(NewsArticleFetchOutcome.RedirectLimit, hops, current.AbsoluteUri, status);
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue; // the next loop iteration re-validates the target in full
                }

                return await ReadResponseAsync(current, response, hops, ct).ConfigureAwait(false);
            }
        }

        // Unreachable: the loop returns RedirectLimit at the boundary; kept for the compiler.
        return Result(NewsArticleFetchOutcome.RedirectLimit, MaxRedirectHops, null);
    }

    private async Task<NewsArticleFetchResult> ReadResponseAsync(
        Uri finalUrl, HttpResponseMessage response, int hops, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var resolved = finalUrl.AbsoluteUri;

        // 401/402/403: access requires credentials/subscription. Radar never authenticates or circumvents,
        // so the durable outcome is Paywalled. 429 is its own outcome; anything else non-success HttpError.
        if (status is 401 or 402 or 403)
        {
            return Result(NewsArticleFetchOutcome.Paywalled, hops, resolved, status);
        }

        if (status == 429)
        {
            return Result(NewsArticleFetchOutcome.RateLimited, hops, resolved, status);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result(NewsArticleFetchOutcome.HttpError, hops, resolved, status);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant();
        if (contentType is null || !SupportedContentTypes.Contains(contentType))
        {
            return Result(
                NewsArticleFetchOutcome.UnsupportedContentType, hops, resolved, status, contentType);
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > _options.MaxResponseBytes)
        {
            return Result(NewsArticleFetchOutcome.TooLarge, hops, resolved, status, contentType);
        }

        // Bounded read: never trust the declared length — read at most maxBytes + 1 and classify overflow.
        byte[] body;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > _options.MaxResponseBytes)
                {
                    return Result(NewsArticleFetchOutcome.TooLarge, hops, resolved, status, contentType);
                }

                buffer.Write(chunk, 0, read);
            }

            body = buffer.ToArray();
        }

        var html = Encoding.UTF8.GetString(body);
        var text = HtmlVisibleText.Extract(html, MaxExtractedChars, out var truncated);
        if (text.Length == 0)
        {
            return Result(
                NewsArticleFetchOutcome.ExtractionEmpty, hops, resolved, status, contentType,
                extractorVersion: HtmlVisibleText.Version);
        }

        var contentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        // Fetched ⇒ allowlisted by construction (nothing else is ever requested), so retaining the body is
        // covered by the operator's explicit allowlist permission.
        return new NewsArticleFetchResult(
            Outcome: NewsArticleFetchOutcome.Fetched,
            RetrievedAtUtc: _timeProvider.GetUtcNow(),
            RedirectHops: hops,
            ResolvedUrl: resolved,
            HttpStatus: status,
            ContentType: contentType,
            Truncated: truncated,
            ExtractorVersion: HtmlVisibleText.Version,
            ContentHash: contentHash,
            BodyText: text,
            RetrievalPolicy: _retrievalPolicy);
    }

    // ---------------------------------------------------------------------------------------------------
    // Safety gates
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole per-hop destination gate: <c>UnsafeUrl</c> for an unsafe shape/host/address,
    /// <c>DomainNotAllowed</c> for a safe-but-unlisted host, <c>HttpError</c> for a failed resolution,
    /// <c>null</c> when the destination may be requested.
    /// </summary>
    private async Task<NewsArticleFetchOutcome?> ValidateDestinationAsync(Uri uri, CancellationToken ct)
    {
        if (!IsSafeUrlShape(uri))
        {
            return NewsArticleFetchOutcome.UnsafeUrl;
        }

        // An IP-literal destination is decidable without DNS: a non-public literal is UNSAFE regardless of
        // the allowlist (an allowlist cannot make loopback public).
        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            if (!IsPublicAddress(literal))
            {
                return NewsArticleFetchOutcome.UnsafeUrl;
            }

            return IsAllowlisted(uri.Host) ? null : NewsArticleFetchOutcome.DomainNotAllowed;
        }

        // Obvious internal name shapes are unsafe without resolving anything.
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return NewsArticleFetchOutcome.UnsafeUrl;
        }

        // Allowlist BEFORE DNS: a non-allowlisted host is never even resolved.
        if (!IsAllowlisted(host))
        {
            return NewsArticleFetchOutcome.DomainNotAllowed;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _resolveHostAddresses(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return NewsArticleFetchOutcome.HttpError;
        }

        if (addresses.Length == 0)
        {
            return NewsArticleFetchOutcome.HttpError;
        }

        // ANY non-public resolved address rejects the request: a name resolving to one public and one
        // private address is an attack shape (DNS rebinding), not a tie to break.
        return addresses.All(IsPublicAddress) ? null : NewsArticleFetchOutcome.UnsafeUrl;
    }

    /// <summary>HTTP(S) only, no embedded user-info, default port only (a non-standard port is a classic SSRF pivot).</summary>
    private static bool IsSafeUrlShape(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort;

    /// <summary>Exact host match or subdomain-suffix match against the operator allowlist, case-insensitive.</summary>
    private bool IsAllowlisted(string host)
    {
        foreach (var domain in _options.AllowedDomains)
        {
            var allowed = domain.Trim().TrimStart('.');
            if (allowed.Length == 0)
            {
                continue;
            }

            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Public-internet routability check for one address: rejects loopback, RFC1918 private, link-local
    /// (v4 169.254/16 and v6 fe80::/10), CGNAT 100.64/10, unique-local fc00::/7, multicast, unspecified,
    /// broadcast and the 0.0.0.0/8 "this network" block. IPv4-mapped IPv6 addresses are unwrapped first so
    /// <c>::ffff:127.0.0.1</c> cannot smuggle a loopback through the v6 path.
    /// </summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => false,                                  // "this network"
                10 => false,                                 // RFC1918
                100 when b[1] >= 64 && b[1] <= 127 => false, // CGNAT 100.64/10
                127 => false,                                // loopback
                169 when b[1] == 254 => false,               // link-local
                172 when b[1] >= 16 && b[1] <= 31 => false,  // RFC1918
                192 when b[1] == 168 => false,               // RFC1918
                >= 224 => false,                             // multicast + reserved + broadcast
                _ => true,
            };
        }

        return !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && !address.IsIPv6UniqueLocal
            && !address.IsIPv6SiteLocal;
    }

    // ---------------------------------------------------------------------------------------------------
    // robots.txt
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Per-run cached robots decision for one URL. The robots fetch itself goes through the SAME transport
    /// (shape/allowlist/address checks already passed for this host) and pacing. Fail-closed posture:
    /// 2xx ⇒ parse; 404/410 ⇒ no robots ⇒ allowed; anything else (5xx, transport failure) ⇒ DISALLOWED —
    /// "cannot tell whether we may fetch" must never read as permission.
    /// </summary>
    private async Task<bool> IsAllowedByRobotsAsync(Uri url, CancellationToken ct)
    {
        if (!_robotsByHost.TryGetValue(url.Host, out var rules))
        {
            rules = await FetchRobotsAsync(url, ct).ConfigureAwait(false);
            _robotsByHost[url.Host] = rules;
        }

        // null ⇒ robots absent ⇒ allowed; Unavailable ⇒ fail closed.
        if (rules is null)
        {
            return true;
        }

        return !rules.Unavailable && rules.Allows(url.AbsolutePath, ProductToken());
    }

    private async Task<RobotsRules?> FetchRobotsAsync(Uri url, CancellationToken ct)
    {
        var robotsUri = new Uri($"{url.Scheme}://{url.Host}/robots.txt");
        await PacePerHostAsync(url.Host, ct).ConfigureAwait(false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, robotsUri);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return null; // no robots ⇒ everything allowed (the standard reading)
            }

            if (!response.IsSuccessStatusCode)
            {
                return RobotsRules.CreateUnavailable();
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return RobotsRules.Parse(text);
        }
        catch (HttpRequestException)
        {
            return RobotsRules.CreateUnavailable();
        }
    }

    /// <summary>The UA product token robots groups are matched against (the first word of the configured UA).</summary>
    private string ProductToken()
    {
        var ua = _options.UserAgent.Trim();
        var end = ua.IndexOfAny([' ', '/']);
        return end < 0 ? ua : ua[..end];
    }

    // ---------------------------------------------------------------------------------------------------
    // Pacing + result plumbing
    // ---------------------------------------------------------------------------------------------------

    private async Task PacePerHostAsync(string host, CancellationToken ct)
    {
        if (_lastRequestPerHost.TryGetValue(host, out var last))
        {
            var wait = _options.PerHostInterval - (_timeProvider.GetUtcNow() - last);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _timeProvider, ct).ConfigureAwait(false);
            }
        }

        _lastRequestPerHost[host] = _timeProvider.GetUtcNow();
    }

    private NewsArticleFetchResult Result(
        NewsArticleFetchOutcome outcome,
        int hops,
        string? resolvedUrl,
        int? status = null,
        string? contentType = null,
        string? extractorVersion = null) =>
        new(
            Outcome: outcome,
            RetrievedAtUtc: _timeProvider.GetUtcNow(),
            RedirectHops: hops,
            ResolvedUrl: resolvedUrl,
            HttpStatus: status,
            ContentType: contentType,
            Truncated: false,
            ExtractorVersion: extractorVersion,
            ContentHash: null,
            BodyText: null,
            RetrievalPolicy: _retrievalPolicy);

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is 301 or 302 or 303 or 307 or 308;

    /// <summary>
    /// A minimal deterministic robots.txt reading: groups selected by exact product-token match (falling
    /// back to <c>*</c>), longest-path rule wins, <c>Allow</c> beats <c>Disallow</c> at equal length, an
    /// empty <c>Disallow:</c> allows everything. Deliberately small — Radar honors the widely-understood
    /// core rather than emulating any one crawler's extensions.
    /// </summary>
    private sealed class RobotsRules
    {
        private readonly List<(string Agent, string Path, bool Allow)> _rules = [];

        public bool Unavailable { get; private init; }

        public static RobotsRules CreateUnavailable() => new() { Unavailable = true };

        public static RobotsRules Parse(string text)
        {
            var rules = new RobotsRules();
            var currentAgents = new List<string>();
            var lastLineWasAgent = false;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                var comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line[..comment].Trim();
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var field = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    if (!lastLineWasAgent)
                    {
                        currentAgents = [];
                    }

                    currentAgents.Add(value);
                    lastLineWasAgent = true;
                    continue;
                }

                lastLineWasAgent = false;
                var isAllow = field.Equals("Allow", StringComparison.OrdinalIgnoreCase);
                if (!isAllow && !field.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var agent in currentAgents)
                {
                    rules._rules.Add((agent, value, isAllow));
                }
            }

            return rules;
        }

        public bool Allows(string path, string productToken)
        {
            // Prefer the group addressed to our product token; fall back to '*'.
            var scoped = _rules
                .Where(r => r.Agent.Equals(productToken, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (scoped.Count == 0)
            {
                scoped = _rules.Where(r => r.Agent == "*").ToList();
            }

            var decision = true;
            var matchedLength = -1;
            foreach (var (_, rulePath, allow) in scoped)
            {
                if (rulePath.Length == 0)
                {
                    continue; // "Disallow:" (empty) allows everything — no match to record
                }

                if (path.StartsWith(rulePath, StringComparison.Ordinal)
                    && (rulePath.Length > matchedLength
                        || (rulePath.Length == matchedLength && allow)))
                {
                    matchedLength = rulePath.Length;
                    decision = allow;
                }
            }

            return decision;
        }
    }
}
