using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// POSTs a company's recipient query to the USASpending.gov <c>spending_by_award</c> endpoint
/// (<c>https://api.usaspending.gov/api/v2/search/spending_by_award/</c> — the trailing slash is
/// required) and parses <c>results[]</c> with <c>System.Text.Json</c>. A recipient with no awards, an
/// unreachable endpoint, the request's own timeout, and malformed/absent JSON are each reported as a typed
/// failure on the returned <see cref="UsaSpendingReadResult"/> (with a warning) rather than swallowed;
/// caller-requested cancellation still throws. <b>Provenance-critical:</b> after a 200 parse the
/// <c>messages[]</c> are inspected — an unsupported filter key is SILENTLY ignored by the API and the entire
/// national firehose is returned, so any <c>"... were not used ..."</c> warning yields
/// <see cref="UsaSpendingReadOutcome.FiltersIgnored"/> with zero items (never emitted as evidence). All
/// HTTP/JSON/USASpending code stays in Infrastructure (AD-5). No User-Agent or key is required by the API.
/// </summary>
internal sealed class HttpUsaSpendingAwardReader : IUsaSpendingAwardReader
{
    private const string Endpoint = "https://api.usaspending.gov/api/v2/search/spending_by_award/";

    // Whitelist of DISPLAY-NAME field strings (exact casing/spacing) the API returns. recipient_id and
    // generated_internal_id are absent unless explicitly requested here — do not remove them.
    private static readonly string[] Fields =
    [
        "Award ID",
        "Recipient Name",
        "Award Amount",
        "Awarding Agency",
        "Start Date",
        "End Date",
        "Last Modified Date",
        "Description",
        "recipient_id",
        "generated_internal_id",
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpUsaSpendingAwardReader> _logger;

    public HttpUsaSpendingAwardReader(HttpClient httpClient, ILogger<HttpUsaSpendingAwardReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<UsaSpendingReadResult> ReadAsync(UsaSpendingAwardQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var body = BuildRequestBody(query);

        var (failure, bytes) = await HttpOutcomeFetch.SendAsync<UsaSpendingReadResult, byte[]>(
            // USASpending is the one POST reader: the send delegate keeps PostAsJsonAsync (and its default
            // completion option) exactly as it was, while the outcome ladder is the shared one.
            send: c => _httpClient.PostAsJsonAsync(Endpoint, body, c),
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onStatus: null,
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "USASpending award search for '{SearchText}' returned non-success status {StatusCode}; skipping.",
                    query.SearchText,
                    status);
                return UsaSpendingReadResult.Failure(
                    UsaSpendingReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "USASpending award search for '{SearchText}' failed; skipping.", query.SearchText);
                return UsaSpendingReadResult.Failure(UsaSpendingReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
                _logger.LogWarning(
                    ex, "USASpending award search for '{SearchText}' timed out; skipping.", query.SearchText);
                return UsaSpendingReadResult.Failure(UsaSpendingReadOutcome.Timeout, "request timed out");
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

            // The endpoint always returns a JSON object. Valid JSON with any other root shape (array,
            // string, number, …) is a bad/changed response, not a quiet recipient: report it as Malformed
            // so the collector does not treat the source as silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "USASpending award search for '{SearchText}' returned JSON with an unexpected root kind "
                        + "{RootKind} (expected an object); skipping.",
                    query.SearchText,
                    document.RootElement.ValueKind);
                return UsaSpendingReadResult.Failure(
                    UsaSpendingReadOutcome.Malformed, "unexpected root JSON shape");
            }

            // PROVENANCE-CRITICAL: an unsupported filter key is silently ignored and the entire national
            // firehose is returned with a messages[] note. Treat any "were not used" warning as a hard
            // failure and emit NO awards — otherwise a typo ingests thousands of unrelated companies' awards.
            if (HasIgnoredFiltersWarning(document.RootElement))
            {
                _logger.LogWarning(
                    "USASpending award search for '{SearchText}' reported ignored filters (firehose guard); "
                        + "emitting no awards.",
                    query.SearchText);
                return UsaSpendingReadResult.Failure(
                    UsaSpendingReadOutcome.FiltersIgnored, "filters were not used (firehose guard)");
            }

            var items = ParseAwards(document.RootElement, ct);
            return UsaSpendingReadResult.Success(items);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "USASpending award search for '{SearchText}' returned malformed JSON; skipping.", query.SearchText);
            return UsaSpendingReadResult.Failure(UsaSpendingReadOutcome.Malformed, "malformed JSON");
        }
    }

    private static object BuildRequestBody(UsaSpendingAwardQuery query) => new
    {
        filters = new
        {
            award_type_codes = query.AwardTypeCodes,
            recipient_search_text = new[] { query.SearchText },
            time_period = new[]
            {
                new { start_date = query.StartDate, end_date = query.EndDate },
            },
        },
        fields = Fields,
        // Sort by recency (most-recently-modified first) so the collector's top-N-per-recipient cap keeps
        // the awards with recent activity — those whose Last Modified Date lands in the scoring window —
        // rather than the largest-but-stale multi-year vehicles.
        sort = "Last Modified Date",
        order = "desc",
        limit = query.Limit,
        page = 1,
    };

    private static bool HasIgnoredFiltersWarning(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind == JsonValueKind.String
                && message.GetString() is { } text
                && text.Contains("were not used", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps each <c>results[]</c> row to a <see cref="UsaSpendingAwardItem"/>. Rows missing the fields
    /// needed for provenance/dedupe (award id, generated internal id, recipient id) are skipped rather than
    /// throwing. An absent/empty <c>results</c> array yields no items (a recipient with no awards).
    /// </summary>
    private static IReadOnlyList<UsaSpendingAwardItem> ParseAwards(JsonElement root, CancellationToken ct)
    {
        var items = new List<UsaSpendingAwardItem>();

        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var row in results.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var awardId = GetString(row, "Award ID");
            var generatedInternalId = GetString(row, "generated_internal_id");
            var recipientId = GetString(row, "recipient_id");

            // Without these three the award cannot be attributed (recipient filter), linked (landing page),
            // or deduped safely, so skip rather than emit an unattributable/uncollidable row.
            if (string.IsNullOrWhiteSpace(awardId)
                || string.IsNullOrWhiteSpace(generatedInternalId)
                || string.IsNullOrWhiteSpace(recipientId))
            {
                continue;
            }

            items.Add(new UsaSpendingAwardItem(
                AwardId: awardId,
                RecipientName: GetString(row, "Recipient Name"),
                AwardAmount: GetDecimal(row, "Award Amount"),
                AwardingAgency: GetString(row, "Awarding Agency"),
                StartDate: GetString(row, "Start Date"),
                EndDate: NullIfBlank(GetString(row, "End Date")),
                LastModifiedDate: NullIfBlank(GetString(row, "Last Modified Date")),
                Description: NullIfBlank(GetString(row, "Description")),
                RecipientId: recipientId,
                GeneratedInternalId: generatedInternalId,
                AwardUrl: $"https://www.usaspending.gov/award/{generatedInternalId}"));
        }

        return items;
    }

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Parses the <c>Award Amount</c> defensively: a JSON number is read directly; a numeric string is
    /// parsed with the invariant culture; anything else (null, non-numeric) yields zero rather than throwing.
    /// </summary>
    private static decimal GetDecimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => 0m,
        };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
