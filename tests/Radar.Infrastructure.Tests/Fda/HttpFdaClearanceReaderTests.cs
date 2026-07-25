using System.Net;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Fda;

namespace Radar.Infrastructure.Tests.Fda;

public sealed class HttpFdaClearanceReaderTests
{
    // A well-formed openFDA 510(k) response: two clearances plus the meta envelope.
    private const string Valid510k = """
        {
          "meta": { "results": { "total": 12 } },
          "results": [
            { "k_number": "K250001", "device_name": "Nerve repair conduit", "decision_date": "2026-05-12", "applicant": "Axogen" },
            { "k_number": "K250002", "device_name": "Surgical implant scaffold", "decision_date": "2026-03-01", "applicant": "Axogen" }
          ]
        }
        """;

    // A well-formed openFDA PMA response: PMA uses pma_number + trade_name (its device_name is null/absent).
    // supplement_number is present-and-EMPTY because this row represents an ORIGINAL approval — which is how
    // openFDA really returns one. This fixture predates the spec-135 materiality filter and originally OMITTED
    // the field; it counted as material only through the absent -> "" -> "original" fail-open path, so the
    // realistic value is what keeps this test measuring endpoint merging rather than that defect.
    private const string ValidPma = """
        {
          "meta": { "results": { "total": 3 } },
          "results": [
            { "pma_number": "P250010", "supplement_number": "", "supplement_type": "", "trade_name": "Organ perfusion module", "decision_date": "2026-04-20", "applicant": "TransMedics" }
          ]
        }
        """;

    // Rows carrying an unparseable/absent decision_date must be skipped, not coerced. Only the valid row counts.
    private const string UnparseableDates510k = """
        {
          "meta": { "results": { "total": 3 } },
          "results": [
            { "k_number": "K250001", "device_name": "Valid row", "decision_date": "2026-05-12" },
            { "k_number": "K250002", "device_name": "Bad date", "decision_date": "not-a-date" },
            { "k_number": "K250003", "device_name": "Absent date" }
          ]
        }
        """;

    // openFDA reports a genuinely empty search as HTTP 404 with this body (NOT an empty results array).
    private const string EmptySearch404 = """
        { "error": { "code": "NOT_FOUND", "message": "No matches found!" } }
        """;

    private const string NoResultsArray = """
        { "meta": { "results": { "total": 0 } } }
        """;

    // Spec 135 materiality filter: an ORIGINAL PMA (supplement_number is an EMPTY STRING — present, not absent)
    // and a 'Panel Track' supplement are material; 30-Day Notice / Real-Time Process / Special (Immediate Track)
    // are routine post-market paperwork and are excluded.
    private const string MixedSupplementsPma = """
        {
          "meta": { "results": { "total": 5 } },
          "results": [
            { "pma_number": "P180001", "supplement_number": "", "supplement_type": "", "trade_name": "Original approval", "decision_date": "2026-06-01" },
            { "pma_number": "P180001", "supplement_number": "S002", "supplement_type": "Panel Track", "trade_name": "New indication", "decision_date": "2026-05-01" },
            { "pma_number": "P180001", "supplement_number": "S031", "supplement_type": "30-Day Notice", "trade_name": "Sterilizer change", "decision_date": "2026-04-01" },
            { "pma_number": "P180001", "supplement_number": "S032", "supplement_type": "Real-Time Process", "trade_name": "Component change", "decision_date": "2026-03-01" },
            { "pma_number": "P180001", "supplement_number": "S033", "supplement_type": "Special (Immediate Track)", "trade_name": "Labeling change", "decision_date": "2026-02-01" }
          ]
        }
        """;

    // An FDA supplement category the reader has never seen: excluded FAIL-CLOSED (a new category must not
    // silently become bullish) and logged at Debug.
    private const string UnrecognisedSupplementPma = """
        {
          "meta": { "results": { "total": 1 } },
          "results": [
            { "pma_number": "P180001", "supplement_number": "S040", "supplement_type": "Brand New Track", "trade_name": "Unknown category", "decision_date": "2026-06-10" }
          ]
        }
        """;

    // An ORIGINAL is identified by supplement_number being PRESENT, a STRING, and blank. A row that merely
    // OMITS the field, or carries it as null or a non-string, must NOT be read as an original: reading it
    // through a helper that returns "" for absent/null/non-string would collapse those cases into the
    // original marker and turn every supplement bullish if openFDA ever changed the field. Both rows below
    // carry a routine supplement_type, so the ONLY thing that could make them count is that fail-open path.
    private const string AbsentSupplementNumberPma = """
        {
          "meta": { "results": { "total": 1 } },
          "results": [
            { "pma_number": "P180001", "supplement_type": "30-Day Notice", "trade_name": "Sterilizer change", "decision_date": "2026-06-10" }
          ]
        }
        """;

    private const string NullSupplementNumberPma = """
        {
          "meta": { "results": { "total": 2 } },
          "results": [
            { "pma_number": "P180001", "supplement_number": null, "supplement_type": "Real-Time Process", "trade_name": "Component change", "decision_date": "2026-06-10" },
            { "pma_number": "P180001", "supplement_number": 21, "supplement_type": "30-Day Notice", "trade_name": "Numeric supplement number", "decision_date": "2026-06-09" }
          ]
        }
        """;

    // supplement_type is compared Ordinal, CASE-INSENSITIVE, TRIMMED.
    private const string CasedPanelTrackPma = """
        {
          "meta": { "results": { "total": 1 } },
          "results": [
            { "pma_number": "P180001", "supplement_number": "S002", "supplement_type": " panel track ", "trade_name": "New indication", "decision_date": "2026-06-10" }
          ]
        }
        """;

    // 510(k) rows are the marketing authorisation itself — ALL count, even if a supplement-ish field is present.
    private const string SupplementIsh510k = """
        {
          "meta": { "results": { "total": 3 } },
          "results": [
            { "k_number": "K250001", "device_name": "Alpha", "decision_date": "2026-05-12", "supplement_number": "S001", "supplement_type": "30-Day Notice" },
            { "k_number": "K250002", "device_name": "Beta", "decision_date": "2026-04-12", "supplement_number": "S002", "supplement_type": "Brand New Track" },
            { "k_number": "K250003", "device_name": "Gamma", "decision_date": "2026-03-12" }
          ]
        }
        """;

    private static readonly DateOnly DecisionFloor = new(2026, 1, 1);

    private static HttpFdaClearanceReader CreateReader(
        HttpMessageHandler handler,
        FdaCollectorOptions? options = null,
        ILogger<HttpFdaClearanceReader>? logger = null) =>
        new(
            new HttpClient(handler),
            logger ?? NullLogger<HttpFdaClearanceReader>.Instance,
            options ?? new FdaCollectorOptions());

    [Fact]
    public async Task ReadAsync_ValidResults_MergesBothEndpointsWithCountsNamesDatesAndTracks()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, Valid510k),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Result);
        // Two 510(k) + one PMA merged.
        Assert.Equal(3, result.Result!.ClearanceCount);
        Assert.Equal(12, result.Result.ReportedTotal510k);
        Assert.Equal(3, result.Result.ReportedTotalPma);

        var first = result.Result.Clearances[0];
        Assert.Equal("K250001", first.SubmissionNumber);
        Assert.Equal("Nerve repair conduit", first.DeviceName);
        Assert.Equal(new DateOnly(2026, 5, 12), first.DecisionDate);
        Assert.Equal("510(k)", first.Track);

        // The PMA row: pma_number as the submission number, trade_name as the device name, PMA track.
        var pma = result.Result.Clearances[^1];
        Assert.Equal("P250010", pma.SubmissionNumber);
        Assert.Equal("Organ perfusion module", pma.DeviceName);
        Assert.Equal(new DateOnly(2026, 4, 20), pma.DecisionDate);
        Assert.Equal("PMA", pma.Track);
    }

    [Fact]
    public async Task ReadAsync_BothEndpointsEmptySearch404_ReturnsSuccessWithZeroClearances()
    {
        // openFDA's documented empty-search 404 is a valid no-recent-clearances result, not an error.
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.NotFound, EmptySearch404),
            pma: (HttpStatusCode.NotFound, EmptySearch404)));

        var result = await reader.ReadAsync("Nobody Devices", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.ClearanceCount);
        Assert.Empty(result.Result.Clearances);
        Assert.Equal(0, result.Result.ReportedTotal510k);
        Assert.Equal(0, result.Result.ReportedTotalPma);
    }

    [Fact]
    public async Task ReadAsync_OneEndpoint404_OtherHasResults_MergesTheNon404Endpoint()
    {
        // A 404 on 510(k) contributes 0; the PMA endpoint's clearances still come through.
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.NotFound, EmptySearch404),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        var clearance = Assert.Single(result.Result!.Clearances);
        Assert.Equal("P250010", clearance.SubmissionNumber);
        Assert.Equal(0, result.Result.ReportedTotal510k);
        Assert.Equal(3, result.Result.ReportedTotalPma);
    }

    [Fact]
    public async Task ReadAsync_RowsWithUnparseableDecisionDate_AreSkipped()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, UnparseableDates510k),
            pma: (HttpStatusCode.NotFound, EmptySearch404)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        // Only the single row with a valid decision_date survives; the bad/absent dates are dropped, not coerced.
        var clearance = Assert.Single(result.Result!.Clearances);
        Assert.Equal(1, result.Result.ClearanceCount);
        Assert.Equal("K250001", clearance.SubmissionNumber);
        Assert.Equal(new DateOnly(2026, 5, 12), clearance.DecisionDate);
    }

    [Fact]
    public async Task ReadAsync_PmaSupplements_CountsOnlyOriginalsAndPanelTrack()
    {
        // Spec 135: of the five PMA rows only the original and the Panel Track are material regulatory events;
        // the 30-Day Notice / Real-Time Process / Special (Immediate Track) rows are routine post-market
        // paperwork on an already-approved device.
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.NotFound, EmptySearch404),
            pma: (HttpStatusCode.OK, MixedSupplementsPma)));

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Result!.ClearanceCount);
        Assert.Equal(3, result.Result.ExcludedSupplementCount);
        Assert.Equal(
            ["Original approval", "New indication"],
            result.Result.Clearances.Select(c => c.DeviceName));
        // The raw API total stays PRE-filter provenance — the materiality filter must never shrink it.
        Assert.Equal(5, result.Result.ReportedTotalPma);
    }

    [Theory]
    [InlineData(nameof(AbsentSupplementNumberPma))]
    [InlineData(nameof(NullSupplementNumberPma))]
    public async Task ReadAsync_SupplementNumberAbsentOrNotAString_IsNotReadAsAnOriginal(string fixtureName)
    {
        var pma = fixtureName == nameof(AbsentSupplementNumberPma)
            ? AbsentSupplementNumberPma
            : NullSupplementNumberPma;

        var reader = CreateReader(
            new RoutingHandler(
                k510: (HttpStatusCode.NotFound, EmptySearch404),
                pma: (HttpStatusCode.OK, pma)));

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        // Fail CLOSED: these rows carry routine supplement types, so counting any of them could only happen
        // via the absent/null/non-string -> "" -> "original" collapse.
        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.ClearanceCount);
        Assert.Empty(result.Result.Clearances);
    }

    [Fact]
    public async Task ReadAsync_UnrecognisedSupplementType_IsExcludedFailClosedAndLoggedAtDebug()
    {
        var logger = new CapturingLogger<HttpFdaClearanceReader>();
        var reader = CreateReader(
            new RoutingHandler(
                k510: (HttpStatusCode.NotFound, EmptySearch404),
                pma: (HttpStatusCode.OK, UnrecognisedSupplementPma)),
            logger: logger);

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        // FAIL CLOSED: a supplement category the reader has never seen must not silently become bullish.
        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.ClearanceCount);
        Assert.Empty(result.Result.Clearances);
        Assert.Equal(1, result.Result.ExcludedSupplementCount);

        // ...but it IS surfaced at Debug so a genuinely material new type can be spotted and added.
        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains("Brand New Track", debug.Message, StringComparison.Ordinal);
        Assert.Contains("TransMedics", debug.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RecognisedRoutineSupplementType_IsExcludedWithoutADebugLog()
    {
        var logger = new CapturingLogger<HttpFdaClearanceReader>();
        var reader = CreateReader(
            new RoutingHandler(
                k510: (HttpStatusCode.NotFound, EmptySearch404),
                pma: (HttpStatusCode.OK, MixedSupplementsPma)),
            logger: logger);

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        Assert.Equal(3, result.Result!.ExcludedSupplementCount);
        // The five pinned routine types are known — excluding them is expected, not noteworthy.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task ReadAsync_PanelTrackSupplementType_IsMatchedCaseInsensitivelyAndTrimmed()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.NotFound, EmptySearch404),
            pma: (HttpStatusCode.OK, CasedPanelTrackPma)));

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        Assert.Equal(1, result.Result!.ClearanceCount);
        Assert.Equal(0, result.Result.ExcludedSupplementCount);
        Assert.Equal("New indication", Assert.Single(result.Result.Clearances).DeviceName);
    }

    [Fact]
    public async Task ReadAsync_510kRows_AllCountRegardlessOfSupplementFields()
    {
        // A 510(k) IS the marketing authorisation — there is no sub-classification, so no row is filtered even
        // when it carries a supplement-ish field.
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, SupplementIsh510k),
            pma: (HttpStatusCode.NotFound, EmptySearch404)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(3, result.Result!.ClearanceCount);
        Assert.Equal(0, result.Result.ExcludedSupplementCount);
        Assert.Equal(["K250001", "K250002", "K250003"], result.Result.Clearances.Select(c => c.SubmissionNumber));
    }

    [Fact]
    public async Task ReadAsync_ReportedTotalFallback_IsThePreFilterParsedRowCount()
    {
        // No meta envelope: the fallback is the PRE-filter parsed row count (raw API provenance), NOT the
        // post-filter material count.
        const string NoMetaMixedPma = """
            {
              "results": [
                { "pma_number": "P180001", "supplement_number": "", "supplement_type": "", "trade_name": "Original approval", "decision_date": "2026-06-01" },
                { "pma_number": "P180001", "supplement_number": "S031", "supplement_type": "30-Day Notice", "trade_name": "Sterilizer change", "decision_date": "2026-04-01" },
                { "pma_number": "P180001", "supplement_number": "S032", "supplement_type": "Real-Time Process", "trade_name": "Component change", "decision_date": "2026-03-01" }
              ]
            }
            """;

        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.NotFound, EmptySearch404),
            pma: (HttpStatusCode.OK, NoMetaMixedPma)));

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        Assert.Equal(1, result.Result!.ClearanceCount);
        Assert.Equal(2, result.Result.ExcludedSupplementCount);
        Assert.Equal(3, result.Result.ReportedTotalPma);
    }

    [Fact]
    public async Task ReadAsync_MissingResultsArray_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, NoResultsArray),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Malformed, result.Outcome);
        Assert.Null(result.Result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_UnexpectedRootShape_ReturnsMalformed(string body)
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, body),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, "this is not { json"),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessNon404Status_ReturnsHttpError()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.Forbidden, "forbidden"),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.HttpError, result.Outcome);
        Assert.Contains("403", result.Detail);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeout()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachable()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, Valid510k),
            pma: (HttpStatusCode.OK, ValidPma)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync("Axogen", DecisionFloor, cts.Token));
    }

    [Fact]
    public void QueryUrl_EncodesApplicantAndDecisionFloor_ReturnsThe510kEndpoint()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, Valid510k),
            pma: (HttpStatusCode.OK, ValidPma)));

        var url = reader.QueryUrl("TransMedics", DecisionFloor);

        Assert.StartsWith("https://api.fda.gov/device/510k.json?search=", url, StringComparison.Ordinal);
        Assert.Contains("&limit=", url, StringComparison.Ordinal);
        // The search expression is URL-encoded, so raw spaces never appear.
        Assert.DoesNotContain(' ', url);
        var decoded = Uri.UnescapeDataString(url);
        Assert.Contains("applicant:TransMedics", decoded, StringComparison.Ordinal);
        Assert.Contains("2026-01-01", decoded, StringComparison.Ordinal);
        Assert.Contains("9999-12-31", decoded, StringComparison.Ordinal);
    }

    // Routes to the 510(k) or PMA canned response by the request URL host path, so a single reader call that
    // hits both endpoints gets the right body for each.
    private sealed class RoutingHandler(
        (HttpStatusCode Status, string Body) k510, (HttpStatusCode Status, string Body) pma) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var (status, body) = url.Contains("/device/pma.json", StringComparison.Ordinal) ? pma : k510;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
