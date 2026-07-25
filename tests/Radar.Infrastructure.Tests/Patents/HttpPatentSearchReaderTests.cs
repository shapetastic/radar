using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Patents;

namespace Radar.Infrastructure.Tests.Patents;

/// <summary>
/// Reader-level guardrails for the USPTO ODP PFW Search transport. The fixtures are built from the LIVE
/// response captured on 2026-07-25 (spec 134) and are deliberately trimmed to the fields the reader reads —
/// the real payload also carries <c>eventDataBag</c>, <c>applicantBag</c>, <c>cpcClassificationBag</c> and
/// more, so a couple of fixtures keep extra properties to prove unknown members are tolerated. No fixture
/// carries an API key, and every <c>requestIdentifier</c> is redacted.
/// </summary>
public sealed class HttpPatentSearchReaderTests
{
    private const string ApiKeyEnvVar = "RADAR_TEST_PATENTSVIEW_KEY";

    /// <summary>The redacted stand-in for the live envelope's <c>requestIdentifier</c> guid.</summary>
    private const string RedactedRequestIdentifier = "00000000-0000-0000-0000-000000000000";

    /// <summary>The applicant seed as <c>data/companies.json</c> declares it for ERII.</summary>
    private const string SeedApplicant = "Energy Recovery, Inc.";

    // A well-formed ODP PFW Search response, trimmed from the live 2026-07-25 capture: two granted patents for
    // the applicant, each nesting its bibliographic fields under applicationMetaData, plus the live envelope
    // (count + requestIdentifier — there is NO "totalNumFound" on this API). The extra applicationMetaData
    // members and the row-level "eventDataBag" are unknown to the reader and must be ignored, not choked on.
    private const string ValidResults = """
        {
          "count": 11,
          "requestIdentifier": "00000000-0000-0000-0000-000000000000",
          "patentFileWrapperDataBag": [
            { "applicationNumberText": "18867137",
              "eventDataBag": [ { "eventCode": "PGM/" } ],
              "applicationMetaData": {
                "firstApplicantName": "Energy Recovery, Inc.",
                "inventionTitle": "Geothermal power generation systems with pressure exchangers",
                "grantDate": "2026-05-12",
                "patentNumber": "12624681",
                "filingDate": "2024-11-19",
                "earliestPublicationDate": "2025-07-03",
                "applicationStatusDescriptionText": "Patented Case" } },
            { "applicationNumberText": "18849446",
              "applicationMetaData": {
                "firstApplicantName": "Energy Recovery, Inc.",
                "inventionTitle": "Pressure exchangers with fouling and particle handling capabilities",
                "grantDate": "2025-09-02",
                "patentNumber": "12404877",
                "filingDate": "2024-09-20",
                "earliestPublicationDate": "2025-06-12",
                "applicationStatusDescriptionText": "Patented Case" } }
          ]
        }
        """;

    private const string EmptyResults = """
        { "count": 0, "requestIdentifier": "00000000-0000-0000-0000-000000000000", "patentFileWrapperDataBag": [] }
        """;

    // Rows carrying an unparseable/absent grantDate must be skipped, not coerced to a min-value date. On a live
    // NON-GRANTED row the grantDate and patentNumber keys are absent ENTIRELY (they are not null/empty). Only
    // the one row with a valid grant date counts.
    private const string UnparseableGrantDates = """
        {
          "count": 3,
          "patentFileWrapperDataBag": [
            { "applicationMetaData": { "firstApplicantName": "Energy Recovery, Inc.", "patentNumber": "12624681", "inventionTitle": "Valid row", "grantDate": "2026-05-12" } },
            { "applicationMetaData": { "firstApplicantName": "Energy Recovery, Inc.", "patentNumber": "12404877", "inventionTitle": "Bad date", "grantDate": "not-a-date" } },
            { "applicationMetaData": { "firstApplicantName": "Energy Recovery, Inc.", "inventionTitle": "Pending application — grantDate and patentNumber keys absent" } }
          ]
        }
        """;

    // ODP's firstApplicantName phrase match is TOKEN-based, so a live page mixes the seed's own spelling
    // variants with unrelated companies whose names merely contain the seed tokens (verified: 280 raw rows for
    // "Energy Recovery", 239 genuine). The last row has no applicant name at all and cannot be attributed.
    private const string MixedApplicantResults = """
        {
          "count": 7,
          "requestIdentifier": "00000000-0000-0000-0000-000000000000",
          "patentFileWrapperDataBag": [
            { "applicationMetaData": { "firstApplicantName": "Energy Recovery, Inc.", "patentNumber": "12624681", "inventionTitle": "Genuine — seed spelling", "grantDate": "2026-05-12" } },
            { "applicationMetaData": { "firstApplicantName": "ENERGY  RECOVERY, INC.", "patentNumber": "12404877", "inventionTitle": "Genuine — upper case, double space", "grantDate": "2025-09-02" } },
            { "applicationMetaData": { "firstApplicantName": "Energy Recovery Inc", "patentNumber": "12398799", "inventionTitle": "Genuine — no punctuation", "grantDate": "2025-08-19" } },
            { "applicationMetaData": { "firstApplicantName": "energy recovery, inc", "patentNumber": "12345678", "inventionTitle": "Genuine — lower case", "grantDate": "2025-07-01" } },
            { "applicationMetaData": { "firstApplicantName": "General Energy Recovery Inc.", "patentNumber": "99999991", "inventionTitle": "False positive — different company", "grantDate": "2026-04-02" } },
            { "applicationMetaData": { "firstApplicantName": "CORE Energy Recovery Solutions Inc.", "patentNumber": "99999992", "inventionTitle": "False positive — different company", "grantDate": "2026-03-11" } },
            { "applicationMetaData": { "patentNumber": "99999993", "inventionTitle": "Unattributable — no applicant name", "grantDate": "2026-02-02" } }
          ]
        }
        """;

    private const string NoPatentsArray = """
        { "count": 0, "requestIdentifier": "00000000-0000-0000-0000-000000000000" }
        """;

    private static readonly DateOnly GrantFloor = new(2026, 1, 1);

    private static HttpPatentSearchReader CreateReader(
        HttpMessageHandler handler, PatentCollectorOptions? options = null) =>
        new(
            new HttpClient(handler),
            NullLogger<HttpPatentSearchReader>.Instance,
            options ?? new PatentCollectorOptions { ApiKeyEnvVar = ApiKeyEnvVar });

    // Save/restore the env var around each test so state never leaks across tests. NEVER a real key.
    private static IDisposable WithApiKey(string? value) => new EnvVarScope(ApiKeyEnvVar, value);

    [Fact]
    public async Task ReadAsync_ValidResults_ParsesGrantsCountAndTitles()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result!.GrantCount);
        // The envelope total (count) is kept as the API-reported cross-check.
        Assert.Equal(11, result.Result.ApiReportedTotal);

        var first = result.Result.Grants[0];
        Assert.Equal("12624681", first.PatentId);
        Assert.Equal("Geothermal power generation systems with pressure exchangers", first.Title);
        Assert.Equal(new DateOnly(2026, 5, 12), first.GrantDate);
    }

    [Fact]
    public async Task ReadAsync_EmptyPatentsArray_ReturnsSuccessWithZeroGrants()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyResults));

        var result = await reader.ReadAsync("Nobody, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.GrantCount);
        Assert.Empty(result.Result.Grants);
    }

    [Fact]
    public async Task ReadAsync_RowsWithUnparseableGrantDate_AreSkipped()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, UnparseableGrantDates));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        // Only the single row with a valid grantDate survives; the bad/absent dates are dropped, not coerced.
        var grant = Assert.Single(result.Result!.Grants);
        Assert.Equal(1, result.Result.GrantCount);
        Assert.Equal("12624681", grant.PatentId);
        Assert.Equal(new DateOnly(2026, 5, 12), grant.GrantDate);
    }

    [Fact]
    public async Task ReadAsync_MissingPatentsArray_ReturnsMalformed()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, NoPatentsArray));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Malformed, result.Outcome);
        Assert.Null(result.Result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_UnexpectedRootShape_ReturnsMalformed(string body)
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformed()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not { json"));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_BlankApiKey_ReturnsMissingApiKeyWithNoHttpCall()
    {
        // A blank/absent configured key must degrade with NO HTTP call — assert the handler is never invoked.
        using var _ = WithApiKey(null);
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.MissingApiKey, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task ReadAsync_NotFound_ReturnsSuccessWithZeroGrants()
    {
        // LIVE-VERIFIED (spec 134): ODP answers a query that matches nothing with HTTP 404 and an EMPTY body.
        // That is its empty-result response, NOT an error — before this was handled, every applicant with no
        // grants in the window (most of the seed set) reported a source failure instead of an honest zero.
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.NotFound, string.Empty));

        var result = await reader.ReadAsync("Nobody, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Detail);
        Assert.NotNull(result.Result);
        Assert.Equal(0, result.Result!.GrantCount);
        Assert.Equal(0, result.Result.ApiReportedTotal);
        Assert.Empty(result.Result.Grants);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "400")]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503")]
    public async Task ReadAsync_NonSuccessStatusOtherThan404_ReturnsHttpError(
        HttpStatusCode status, string expectedInDetail)
    {
        // The 404-means-empty special case must NOT over-broaden: a malformed query (400), a bad/absent key
        // (401) and a server fault (5xx) are all still real failures.
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(status, "error"));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.HttpError, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Contains(expectedInDetail, result.Detail);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeout()
    {
        using var _ = WithApiKey("test-key");
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachable()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(SeedApplicant, GrantFloor, cts.Token));
    }

    [Fact]
    public async Task ReadAsync_SetsXApiKeyHeaderFromEnvVar()
    {
        using var _ = WithApiKey("secret-value-123");
        var handler = new HeaderCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.True(handler.CapturedHeaders!.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("secret-value-123", Assert.Single(values!));
    }

    [Fact]
    public void QueryUrl_ReturnsOdpSearchEndpoint()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        // The request is a POST with the query in the body, so the provenance link is the constant search
        // endpoint (default host + fixed path), not a per-assignee GET URL.
        var url = reader.QueryUrl(SeedApplicant, GrantFloor);

        Assert.Equal("https://api.uspto.gov/api/v1/patent/applications/search", url);
    }

    [Fact]
    public async Task ReadAsync_PostsToDefaultBaseUrlAndPath()
    {
        using var _ = WithApiKey("test-key");
        var handler = new RequestCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.CapturedMethod);
        Assert.Equal(
            "https://api.uspto.gov/api/v1/patent/applications/search",
            handler.CapturedUri!.ToString());
    }

    [Fact]
    public async Task ReadAsync_HonoursBaseUrlOverride()
    {
        using var _ = WithApiKey("test-key");
        var handler = new RequestCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(
            handler,
            new PatentCollectorOptions { ApiKeyEnvVar = ApiKeyEnvVar, BaseUrl = "https://odp.example.test" });

        await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        Assert.Equal(
            "https://odp.example.test/api/v1/patent/applications/search",
            handler.CapturedUri!.ToString());
    }

    [Fact]
    public async Task ReadAsync_BaseUrlWithTrailingSlash_YieldsSingleSlashEndpoint()
    {
        using var _ = WithApiKey("test-key");
        var handler = new RequestCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(
            handler,
            new PatentCollectorOptions { ApiKeyEnvVar = ApiKeyEnvVar, BaseUrl = "https://odp.example.test/" });

        await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        // A trailing slash on BaseUrl must NOT produce a double-slash endpoint.
        Assert.Equal(
            "https://odp.example.test/api/v1/patent/applications/search",
            handler.CapturedUri!.ToString());
    }

    [Fact]
    public async Task ReadAsync_PostBody_PinsTheLiveVerifiedRequestShape()
    {
        using var _ = WithApiKey("test-key");
        var handler = new BodyCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        await reader.ReadAsync(SeedApplicant, GrantFloor, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.CapturedBody!);
        var root = document.RootElement;

        // The whole body, pinned against the shape verified live on 2026-07-25.
        Assert.Equal(
            new[] { "fields", "pagination", "q", "rangeFilters", "sort" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            "applicationMetaData.firstApplicantName:\"Energy Recovery, Inc.\"",
            root.GetProperty("q").GetString());

        // BOTH range bounds. ODP rejects a one-sided rangeFilters (valueFrom with no valueTo) with HTTP 400
        // UNCONDITIONALLY, so a one-sided body would make every read fail — assert the element carries exactly
        // field + valueFrom + valueTo, and that valueTo is the far-future ceiling constant (the reader has only
        // a floor, so it must not invent a "today" bound and must stay clock-free).
        var range = Assert.Single(root.GetProperty("rangeFilters").EnumerateArray());
        Assert.Equal(
            new[] { "field", "valueFrom", "valueTo" },
            range.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("applicationMetaData.grantDate", range.GetProperty("field").GetString());
        Assert.Equal("2026-01-01", range.GetProperty("valueFrom").GetString());
        Assert.Equal("9999-12-31", range.GetProperty("valueTo").GetString());

        // The accepted fields projection.
        Assert.Equal(
            new[]
            {
                "applicationMetaData.patentNumber",
                "applicationMetaData.inventionTitle",
                "applicationMetaData.grantDate",
                "applicationMetaData.firstApplicantName",
            },
            root.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToArray());

        // Newest grants first.
        var sort = Assert.Single(root.GetProperty("sort").EnumerateArray());
        Assert.Equal("applicationMetaData.grantDate", sort.GetProperty("field").GetString());
        Assert.Equal("Desc", sort.GetProperty("order").GetString());

        // One bounded page; 100 is a hard ceiling (limit: 200 is rejected).
        var pagination = root.GetProperty("pagination");
        Assert.Equal(0, pagination.GetProperty("offset").GetInt32());
        Assert.Equal(100, pagination.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ReadAsync_AssigneeNameWithQuote_ProducesWellFormedQuery()
    {
        using var _ = WithApiKey("test-key");
        var handler = new BodyCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        // An assignee name containing a double-quote must be escaped so the quoted OpenSearch phrase stays
        // well-formed (a bare embedded quote would break out of the phrase and malform the query).
        await reader.ReadAsync("Acme \"Rocket\" Corp.", GrantFloor, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.CapturedBody!);
        var q = document.RootElement.GetProperty("q").GetString();
        Assert.Equal(
            "applicationMetaData.firstApplicantName:\"Acme \\\"Rocket\\\" Corp.\"",
            q);
    }

    [Theory]
    [InlineData("Energy Recovery, Inc.")] // the shipped seed token
    [InlineData("Energy Recovery")]       // the shorter form used in the live 2026-07-25 verification
    public async Task ReadAsync_FiltersRowsByNormalizedApplicantName(string seed)
    {
        // ODP's firstApplicantName match is token-based: unrelated companies whose names merely CONTAIN the
        // seed tokens come back too (live: 280 raw rows for "Energy Recovery", 239 genuine). The reader
        // normalizes (upper-case + strip every non-alphanumeric) and prefix-matches, so the false positives
        // drop out while the seed's own punctuation/whitespace spelling variants are all retained.
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, MixedApplicantResults));

        var result = await reader.ReadAsync(seed, GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);

        var patentIds = result.Result!.Grants.Select(g => g.PatentId).ToArray();

        // Every genuine spelling variant is kept…
        Assert.Equal(new[] { "12624681", "12404877", "12398799", "12345678" }, patentIds);

        // …and the token-match false positives / unattributable rows are excluded.
        Assert.DoesNotContain("99999991", patentIds); // General Energy Recovery Inc.
        Assert.DoesNotContain("99999992", patentIds); // CORE Energy Recovery Solutions Inc.
        Assert.DoesNotContain("99999993", patentIds); // no firstApplicantName at all

        // The EMITTED count is the POST-normalization count; the envelope's own "count" (7, pre-normalization)
        // stays a provenance-only cross-check and is NOT what the evidence reports.
        Assert.Equal(4, result.Result.GrantCount);
        Assert.Equal(4, result.Result.Grants.Count);
        Assert.Equal(7, result.Result.ApiReportedTotal);
    }

    [Theory]
    [InlineData("Mercury Systems, Inc.")]
    [InlineData("Mercury Systems Inc.")]
    [InlineData("MERCURY  SYSTEMS, INC.")] // double space, as filed
    [InlineData("MERCURY SYSTEMS, INC")]
    public async Task ReadAsync_NormalizedMatching_KeepsEveryLiveSpellingVariant(string filedApplicantName)
    {
        // The four spellings one company was live-verified to file under must all normalize onto the single
        // seed token — strict equality would silently drop most of a company's own rows.
        using var _ = WithApiKey("test-key");
        var body = $$"""
            {
              "count": 1,
              "requestIdentifier": "{{RedactedRequestIdentifier}}",
              "patentFileWrapperDataBag": [
                { "applicationMetaData": {
                    "firstApplicantName": {{JsonSerializer.Serialize(filedApplicantName)}},
                    "patentNumber": "11111111",
                    "inventionTitle": "Secure processing module",
                    "grantDate": "2026-05-12" } }
              ]
            }
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        var grant = Assert.Single(result.Result!.Grants);
        Assert.Equal("11111111", grant.PatentId);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CountingStubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class HeaderCapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public System.Net.Http.Headers.HttpRequestHeaders? CapturedHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedHeaders = request.Headers;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RequestCapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? CapturedMethod { get; private set; }

        public Uri? CapturedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedMethod = request.Method;
            CapturedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class BodyCapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    // Sets an environment variable for the scope of a test and restores its prior value on dispose, so a test
    // never leaks env state into another test. Never carries a real key.
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
