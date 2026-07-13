using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Hiring;

/// <summary>
/// GETs a company's open roles from the keyless Greenhouse job-board API
/// (<c>https://boards-api.greenhouse.io/v1/boards/{boardToken}/jobs</c>) and parses the
/// <c>{"jobs":[{"title":…},…]}</c> object with <c>System.Text.Json</c>. A board with an empty <c>jobs</c>
/// array is a valid no-openings <see cref="JobBoardReadOutcome.Success"/> (zero roles), not an error; an
/// unreachable endpoint, the request's own timeout, a non-success status (incl. a 404 for a bad board
/// token), and malformed/absent JSON are each reported as a typed failure on the returned
/// <see cref="JobBoardReadResult"/> (with a warning) rather than swallowed; caller-requested cancellation
/// still throws. <c>TotalRoles</c> is the count of parsed job entries — <c>meta.total</c> is deliberately
/// NOT trusted (it may be a server-side/paginated figure). All HTTP/JSON code stays in Infrastructure
/// (AD-5). No User-Agent or key is required by the API (verified by the 2026-07-06 reachability spike).
/// </summary>
internal sealed class GreenhouseBoardReader : IJobBoardReader
{
    private const string EndpointFormat = "https://boards-api.greenhouse.io/v1/boards/{0}/jobs";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GreenhouseBoardReader> _logger;

    public GreenhouseBoardReader(HttpClient httpClient, ILogger<GreenhouseBoardReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public string Platform => "greenhouse";

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

        var (failure, bytes) = await HttpOutcomeFetch.GetAsync<JobBoardReadResult, byte[]>(
            _httpClient,
            requestUri,
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onStatus: null,
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "Greenhouse board '{BoardToken}' returned non-success status {StatusCode}; skipping.",
                    boardToken,
                    status);
                return JobBoardReadResult.Failure(
                    JobBoardReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "Greenhouse board '{BoardToken}' could not be reached; skipping.", boardToken);
                return JobBoardReadResult.Failure(JobBoardReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
                _logger.LogWarning(
                    ex, "Greenhouse board '{BoardToken}' timed out; skipping.", boardToken);
                return JobBoardReadResult.Failure(JobBoardReadOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        try
        {
            // Non-null once we are past the failure guard above: the fetch only defaults the body on failure.
            using var document = JsonDocument.Parse(bytes!);

            // The board endpoint returns a JSON object. Valid JSON with any other root shape (array, string,
            // number, …) is a bad/changed response, not a quiet board: report it as Malformed so the collector
            // does not treat the source as silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "Greenhouse board '{BoardToken}' returned JSON with an unexpected root kind {RootKind} "
                        + "(expected an object); skipping.",
                    boardToken,
                    document.RootElement.ValueKind);
                return JobBoardReadResult.Failure(
                    JobBoardReadOutcome.Malformed, "unexpected root JSON shape");
            }

            // A missing/non-array `jobs` is a bad/changed payload, not a no-openings board (an empty board
            // still returns "jobs": []): report it as Malformed rather than a silent zero-role success.
            if (!document.RootElement.TryGetProperty("jobs", out var jobs)
                || jobs.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Greenhouse board '{BoardToken}' returned JSON without the expected 'jobs' array; skipping.",
                    boardToken);
                return JobBoardReadResult.Failure(
                    JobBoardReadOutcome.Malformed, "missing 'jobs' array");
            }

            return JobBoardReadResult.Success(ParseJobs(jobs, ct));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "Greenhouse board '{BoardToken}' returned malformed JSON; skipping.", boardToken);
            return JobBoardReadResult.Failure(JobBoardReadOutcome.Malformed, "malformed JSON");
        }
    }

    /// <summary>
    /// Maps the <c>jobs[]</c> array to the normalized <see cref="JobBoardResult"/>: every object entry
    /// counts as a parsed role (the authoritative <c>TotalRoles</c>), and its non-blank <c>title</c> joins
    /// the title list (a blank/absent title is skipped from titles only, never fabricated). An empty array
    /// yields a valid zero-role result.
    /// </summary>
    private static JobBoardResult ParseJobs(JsonElement jobs, CancellationToken ct)
    {
        var totalRoles = 0;
        var titles = new List<string>();

        foreach (var row in jobs.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            totalRoles++;

            if (row.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String
                && title.GetString() is { } text
                && !string.IsNullOrWhiteSpace(text))
            {
                titles.Add(text);
            }
        }

        return new JobBoardResult(totalRoles, titles);
    }
}
