using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.Hiring;

/// <summary>
/// GETs a company's open roles from the keyless Lever postings API
/// (<c>https://api.lever.co/v0/postings/{boardToken}?mode=json</c>) and parses the top-level JSON array of
/// <c>{"text": &lt;title&gt;,…}</c> postings with <c>System.Text.Json</c>. An empty array is a valid
/// no-openings <see cref="JobBoardReadOutcome.Success"/> (zero roles), not an error; an unreachable
/// endpoint, the request's own timeout, a non-success status (incl. a 404 for a bad board token), and
/// malformed JSON / a non-array root are each reported as a typed failure on the returned
/// <see cref="JobBoardReadResult"/> (with a warning) rather than swallowed; caller-requested cancellation
/// still throws. All HTTP/JSON code stays in Infrastructure (AD-5). No User-Agent or key is required by
/// the API (verified by the 2026-07-06 reachability spike).
/// </summary>
internal sealed class LeverBoardReader : IJobBoardReader
{
    private const string EndpointFormat = "https://api.lever.co/v0/postings/{0}?mode=json";

    private readonly HttpClient _httpClient;
    private readonly ILogger<LeverBoardReader> _logger;

    public LeverBoardReader(HttpClient httpClient, ILogger<LeverBoardReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public string Platform => "lever";

    public string BoardUrl(string boardToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boardToken);
        return string.Format(
            CultureInfo.InvariantCulture, EndpointFormat, Uri.EscapeDataString(boardToken));
    }

    public async Task<JobBoardReadResult> ReadAsync(string boardToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boardToken);

        var requestUri = new Uri(BoardUrl(boardToken));

        byte[] bytes;
        try
        {
            using var response = await _httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Lever board '{BoardToken}' returned non-success status {StatusCode}; skipping.",
                    boardToken,
                    (int)response.StatusCode);
                return JobBoardReadResult.Failure(
                    JobBoardReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}");
            }

            // Materialize the body before disposing the response so parsing can happen synchronously.
            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex, "Lever board '{BoardToken}' could not be reached; skipping.", boardToken);
            return JobBoardReadResult.Failure(JobBoardReadOutcome.Unreachable, "transport error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; do not hide it as a failure result.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
            _logger.LogWarning(
                ex, "Lever board '{BoardToken}' timed out; skipping.", boardToken);
            return JobBoardReadResult.Failure(JobBoardReadOutcome.Timeout, "request timed out");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);

            // The postings endpoint (mode=json) returns a top-level JSON ARRAY. Valid JSON with any other
            // root shape (object, string, number, …) is a bad/changed response, not a quiet board: report it
            // as Malformed so the collector does not treat the source as silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Lever board '{BoardToken}' returned JSON with an unexpected root kind {RootKind} "
                        + "(expected an array); skipping.",
                    boardToken,
                    document.RootElement.ValueKind);
                return JobBoardReadResult.Failure(
                    JobBoardReadOutcome.Malformed, "unexpected root JSON shape");
            }

            return JobBoardReadResult.Success(ParsePostings(document.RootElement, ct));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "Lever board '{BoardToken}' returned malformed JSON; skipping.", boardToken);
            return JobBoardReadResult.Failure(JobBoardReadOutcome.Malformed, "malformed JSON");
        }
    }

    /// <summary>
    /// Maps the top-level posting array to the normalized <see cref="JobBoardResult"/>: every object entry
    /// counts as a parsed role (the authoritative <c>TotalRoles</c>), and its non-blank <c>text</c> joins
    /// the title list (a blank/absent text is skipped from titles only, never fabricated). An empty array
    /// yields a valid zero-role result.
    /// </summary>
    private static JobBoardResult ParsePostings(JsonElement postings, CancellationToken ct)
    {
        var totalRoles = 0;
        var titles = new List<string>();

        foreach (var row in postings.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            totalRoles++;

            if (row.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString() is { } title
                && !string.IsNullOrWhiteSpace(title))
            {
                titles.Add(title);
            }
        }

        return new JobBoardResult(totalRoles, titles);
    }
}
