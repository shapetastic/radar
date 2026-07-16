using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// SEC-flavoured face of the shared <see cref="HttpOutcomeFetch"/> ladder: a GET whose SEC-specific 403 branch
/// (SEC returns 403 when the mandatory <c>User-Agent</c> is missing/invalid) is wired into the shared primitive's
/// status hook, so the 4 SEC readers keep their exact <c>onForbidden</c> shape while the ladder itself lives in
/// one place. Everything else is the shared contract: non-success → HTTP error,
/// <see cref="HttpRequestException"/> → unreachable, <see cref="TaskCanceledException"/> → timeout, and genuine
/// caller-requested cancellation (<see cref="OperationCanceledException"/> when <c>ct.IsCancellationRequested</c>)
/// re-thrown, never mapped. On success returns <c>(null, body)</c>; on any failure the body is <c>default</c>
/// (null for a reference <c>TBody</c>), so callers MUST check <c>Failure</c> before using <c>Body</c>. It does NO
/// logging — logging lives in the caller-supplied projection lambdas so each reader keeps its exact log wording.
/// <c>TBody</c> lets the filing reader read bytes and the earnings reader read a string.
/// <para>
/// The optional <paramref name="onRateLimited"/> hook lets a reader that owns a bounded 429 backoff-retry (the
/// earnings-release reader) map HTTP 429 to a distinct failure. When it is <see langword="null"/> (the default,
/// used by the other 3 SEC readers) a 429 falls through to <paramref name="onHttpError"/> exactly as before, so
/// those readers are byte-for-byte unchanged.
/// </para>
/// </summary>
internal static class SecHttpFetch
{
    public static Task<(TFailure? Failure, TBody? Body)> GetAsync<TFailure, TBody>(
        HttpClient httpClient,
        string url,
        Func<HttpContent, CancellationToken, Task<TBody>> readBody,
        Func<TFailure> onForbidden,
        Func<int, TFailure> onHttpError,
        Func<HttpRequestException, TFailure> onUnreachable,
        Func<TaskCanceledException, TFailure> onTimeout,
        CancellationToken ct,
        Func<TFailure>? onRateLimited = null)
        where TFailure : class =>
        HttpOutcomeFetch.GetAsync(
            httpClient,
            url,
            readBody,
            // SEC's status specializations: 403 = missing/invalid User-Agent, mapped ahead of the generic
            // non-success branch. 429 = rate limited, mapped only when a caller supplies onRateLimited (so it
            // can own a bounded backoff-retry); otherwise 429 falls through to onHttpError as before. Every
            // other status falls through to onHttpError.
            onStatus: status => status == 403 ? onForbidden()
                : (status == 429 && onRateLimited is not null ? onRateLimited() : null),
            onHttpError,
            onUnreachable,
            onTimeout,
            ct);
}
