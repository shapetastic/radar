namespace Radar.Infrastructure.Sec;

/// <summary>
/// Shared SEC HTTP GET + outcome-mapping ladder for the SEC readers. Performs the request and maps SEC's
/// HTTP outcomes to the caller's own typed failure via the supplied projections — 403 → forbidden (with the
/// caller's User-Agent guidance log), non-success → HTTP error, <see cref="HttpRequestException"/> →
/// unreachable, <see cref="TaskCanceledException"/> → timeout. Genuine caller-requested cancellation
/// (<see cref="OperationCanceledException"/> when <c>ct.IsCancellationRequested</c>) is re-thrown, never
/// mapped. On success returns <c>(null, body)</c>; on any failure the body is <c>default</c> (null for a
/// reference <c>TBody</c>), so callers MUST check <c>Failure</c> before using <c>Body</c>. The helper itself does NO logging — logging lives in the
/// caller-supplied projection lambdas so each reader keeps its exact log wording. <c>TBody</c> lets the
/// filing reader read bytes and the earnings reader read a string.
/// </summary>
internal static class SecHttpFetch
{
    public static async Task<(TFailure? Failure, TBody? Body)> GetAsync<TFailure, TBody>(
        HttpClient httpClient,
        string url,
        Func<HttpContent, CancellationToken, Task<TBody>> readBody,
        Func<TFailure> onForbidden,
        Func<int, TFailure> onHttpError,
        Func<HttpRequestException, TFailure> onUnreachable,
        Func<TaskCanceledException, TFailure> onTimeout,
        CancellationToken ct)
        where TFailure : class
    {
        try
        {
            using var response = await httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 403)
                return (onForbidden(), default);

            if (!response.IsSuccessStatusCode)
                return (onHttpError((int)response.StatusCode), default);

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
}
