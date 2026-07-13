namespace Radar.Infrastructure.Sources;

/// <summary>
/// Shared HTTP request + outcome-mapping ladder for every Infrastructure reader (RSS, SEC, GDELT, news search,
/// USASpending, job boards, prices). Performs the caller's request and maps its HTTP outcomes to the caller's own
/// typed failure via the supplied projections — an optional status hook first (e.g. SEC's 403 → forbidden with its
/// User-Agent guidance log, or a 429 → rate-limited), then non-success → HTTP error,
/// <see cref="HttpRequestException"/> → unreachable, <see cref="TaskCanceledException"/> → timeout. Genuine
/// caller-requested cancellation (<see cref="OperationCanceledException"/> when <c>ct.IsCancellationRequested</c>)
/// is re-thrown, never mapped — which is why the <c>when</c>-filtered catch MUST stay ahead of the
/// <see cref="TaskCanceledException"/> catch. On success returns <c>(null, body)</c>; on any failure the body is
/// <c>default</c> (null for a reference <c>TBody</c>), so callers MUST check <c>Failure</c> before using
/// <c>Body</c>. The helper itself does NO logging — logging lives in the caller-supplied projection lambdas so
/// each reader keeps its exact log wording. <c>TBody</c> lets one reader read bytes and another read a string.
/// <para>
/// Genuinely per-reader behavior stays with the caller: GDELT's bounded 429 retry loop, USASpending's POST (via
/// the <c>send</c> delegate on <see cref="SendAsync"/>), and the price reader's pre-request cancellation check are
/// caller hooks, never folded into this type.
/// </para>
/// </summary>
internal static class HttpOutcomeFetch
{
    /// <summary>
    /// The core ladder: the caller supplies the request itself (which is how USASpending keeps its
    /// <c>PostAsJsonAsync</c> POST, completion option and all). The response is disposed before the caller's
    /// mapping runs, so <paramref name="readBody"/> must materialize whatever the caller needs.
    /// </summary>
    public static async Task<(TFailure? Failure, TBody? Body)> SendAsync<TFailure, TBody>(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        Func<HttpContent, CancellationToken, Task<TBody>> readBody,
        Func<int, TFailure?>? onStatus,
        Func<int, TFailure> onHttpError,
        Func<HttpRequestException, TFailure> onUnreachable,
        Func<TaskCanceledException, TFailure> onTimeout,
        CancellationToken ct)
        where TFailure : class
    {
        try
        {
            using var response = await send(ct).ConfigureAwait(false);

            // The status hook runs BEFORE the generic non-success branch so a caller's distinct status
            // (403 → forbidden, 429 → rate limited) wins over the catch-all HTTP-error mapping.
            if (onStatus is not null && onStatus((int)response.StatusCode) is { } statusFailure)
                return (statusFailure, default);

            if (!response.IsSuccessStatusCode)
                return (onHttpError((int)response.StatusCode), default);

            // The body read happens INSIDE the try, so a timeout/transport failure mid-body maps through the
            // same catches rather than escaping as an unhandled exception.
            var body = await readBody(response.Content, ct).ConfigureAwait(false);
            return (null, body);
        }
        catch (HttpRequestException ex)
        {
            return (onUnreachable(ex), default);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine caller cancellation MUST re-throw, never map to a failure
        }
        catch (TaskCanceledException ex)
        {
            return (onTimeout(ex), default);
        }
    }

    /// <summary>
    /// Convenience over <see cref="SendAsync"/>: a GET with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// — the shape every GET reader uses. Takes the URL the caller already built, unparsed.
    /// </summary>
    public static Task<(TFailure? Failure, TBody? Body)> GetAsync<TFailure, TBody>(
        HttpClient httpClient,
        string url,
        Func<HttpContent, CancellationToken, Task<TBody>> readBody,
        Func<int, TFailure?>? onStatus,
        Func<int, TFailure> onHttpError,
        Func<HttpRequestException, TFailure> onUnreachable,
        Func<TaskCanceledException, TFailure> onTimeout,
        CancellationToken ct)
        where TFailure : class =>
        SendAsync(
            send: c => httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, c),
            readBody,
            onStatus,
            onHttpError,
            onUnreachable,
            onTimeout,
            ct);

    /// <summary>
    /// <see cref="Uri"/> overload of the GET convenience, for readers that already built a <see cref="Uri"/> —
    /// the URL is passed through as-is rather than re-parsed/rebuilt.
    /// </summary>
    public static Task<(TFailure? Failure, TBody? Body)> GetAsync<TFailure, TBody>(
        HttpClient httpClient,
        Uri requestUri,
        Func<HttpContent, CancellationToken, Task<TBody>> readBody,
        Func<int, TFailure?>? onStatus,
        Func<int, TFailure> onHttpError,
        Func<HttpRequestException, TFailure> onUnreachable,
        Func<TaskCanceledException, TFailure> onTimeout,
        CancellationToken ct)
        where TFailure : class =>
        SendAsync(
            send: c => httpClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, c),
            readBody,
            onStatus,
            onHttpError,
            onUnreachable,
            onTimeout,
            ct);
}
