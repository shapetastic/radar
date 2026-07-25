using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Infrastructure.Fda;

namespace Radar.Infrastructure.Tests.Fda;

/// <summary>
/// Regression lock for spec 135, driven end-to-end through the REAL <see cref="HttpFdaClearanceReader"/> + the
/// REAL <see cref="FdaClearanceCollector"/> over a fixture reproducing TransMedics' actual openFDA response
/// (live-verified 2026-07-25). Before the materiality filter these nine routine post-market supplements
/// produced a standing Positive <c>RegulatoryApproval</c> signal for a company that had cleared no regulatory
/// gate since 2021 — the exact defect this slice fixes.
/// </summary>
public sealed class FdaMaterialEventRegressionTests
{
    /// <summary>
    /// TransMedics' REAL in-window PMA set: 9 records, ZERO originals, ZERO Panel Track — 3× 30-Day Notice,
    /// 3× Special (Immediate Track), 2× Real-Time Process, 1× Normal 180 Day Track No User Fee. Every one is
    /// routine maintenance on devices approved in 2018/2021 (a sterilizer swap, a packaging supplier change, a
    /// labeling tweak). The <c>meta.results.total</c> 41 is the applicant's all-time PMA row count.
    /// </summary>
    private const string TmdxRealWindowPma = """
        {
          "meta": { "results": { "total": 41 } },
          "results": [
            { "pma_number": "P180001", "supplement_number": "S021", "supplement_type": "30-Day Notice", "supplement_reason": "Process Change - Manufacturer/Sterilizer/Packager/Supplier", "trade_name": "OCS Heart", "decision_date": "2026-06-18", "applicant": "TransMedics, Inc." },
            { "pma_number": "P180001", "supplement_number": "S022", "supplement_type": "30-Day Notice", "supplement_reason": "Process Change - Manufacturer/Sterilizer/Packager/Supplier", "trade_name": "OCS Heart", "decision_date": "2026-05-27", "applicant": "TransMedics, Inc." },
            { "pma_number": "P210031", "supplement_number": "S009", "supplement_type": "30-Day Notice", "supplement_reason": "Process Change - Manufacturer/Sterilizer/Packager/Supplier", "trade_name": "OCS Lung", "decision_date": "2026-04-09", "applicant": "TransMedics, Inc." },
            { "pma_number": "P180001", "supplement_number": "S023", "supplement_type": "Special (Immediate Track)", "supplement_reason": "Labeling Change - Other", "trade_name": "OCS Heart", "decision_date": "2026-03-31", "applicant": "TransMedics, Inc." },
            { "pma_number": "P210031", "supplement_number": "S010", "supplement_type": "Special (Immediate Track)", "supplement_reason": "Process Change - Other", "trade_name": "OCS Lung", "decision_date": "2026-03-05", "applicant": "TransMedics, Inc." },
            { "pma_number": "P210032", "supplement_number": "S007", "supplement_type": "Special (Immediate Track)", "supplement_reason": "Labeling Change - Other", "trade_name": "OCS Liver", "decision_date": "2026-02-11", "applicant": "TransMedics, Inc." },
            { "pma_number": "P180001", "supplement_number": "S024", "supplement_type": "Real-Time Process", "supplement_reason": "Change Design/Components/Specifications/Material", "trade_name": "OCS Heart", "decision_date": "2026-01-22", "applicant": "TransMedics, Inc." },
            { "pma_number": "P210031", "supplement_number": "S011", "supplement_type": "Real-Time Process", "supplement_reason": "Change Design/Components/Specifications/Material", "trade_name": "OCS Lung", "decision_date": "2025-12-04", "applicant": "TransMedics, Inc." },
            { "pma_number": "P210032", "supplement_number": "S008", "supplement_type": "Normal 180 Day Track No User Fee", "supplement_reason": "Labeling Change - PAS", "trade_name": "OCS Liver", "decision_date": "2025-10-16", "applicant": "TransMedics, Inc." }
          ]
        }
        """;

    // TransMedics has no in-window 510(k) at all: openFDA answers an empty search with a 404.
    private const string EmptySearch404 = """
        { "error": { "code": "NOT_FOUND", "message": "No matches found!" } }
        """;

    // A genuine gate: the same applicant's ORIGINAL PMA approval (supplement_number is an EMPTY STRING).
    private const string TmdxOriginalApprovalPma = """
        {
          "meta": { "results": { "total": 42 } },
          "results": [
            { "pma_number": "P250099", "supplement_number": "", "supplement_type": "", "trade_name": "OCS Kidney", "decision_date": "2026-07-01", "applicant": "TransMedics, Inc." }
          ]
        }
        """;

    private static readonly DateTimeOffset FixedNow = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid TmdxId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task TmdxRealWindow_IsAllRoutinePaperwork_SoTheCollectorEmitsNoEvidence()
    {
        var (reader, collector) = CreateStack(pmaBody: (HttpStatusCode.OK, TmdxRealWindowPma));

        // The reader sees nine well-formed PMA rows and reports ZERO material regulatory events.
        var read = await reader.ReadAsync("TransMedics", new DateOnly(2025, 7, 23), CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, read.Outcome);
        Assert.Equal(0, read.Result!.ClearanceCount);
        Assert.Empty(read.Result.Clearances);
        Assert.Equal(9, read.Result.ExcludedSupplementCount);
        // The raw API total survives as PRE-filter provenance.
        Assert.Equal(41, read.Result.ReportedTotalPma);

        // ...so the collector contributes NOTHING — no standing Positive RegulatoryApproval signal.
        var result = await collector.CollectAsync(CreateContext(), CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, result.Summary.ItemsCollected);
        // A quiet applicant is a SUCCESS, not a source failure.
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
        Assert.Empty(result.Summary.Failures);
    }

    [Fact]
    public async Task AnOriginalApprovalInTheSameWindow_StillProducesEvidence()
    {
        // The counter-case that proves the filter is a filter, not a mute: a real gate still fires.
        var (_, collector) = CreateStack(pmaBody: (HttpStatusCode.OK, TmdxOriginalApprovalPma));

        var result = await collector.CollectAsync(CreateContext(), CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Contains("FDA clearance or approval (recent)", item.Title, StringComparison.Ordinal);
        Assert.Equal("1", item.Metadata["clearanceCount"]);
        Assert.Equal("0", item.Metadata["excludedSupplementCount"]);
        Assert.Equal("42", item.Metadata["reportedTotalPmaPreFilter"]);
        Assert.Equal(["TMDX"], item.CompanyHints);
    }

    private static (HttpFdaClearanceReader Reader, FdaClearanceCollector Collector) CreateStack(
        (HttpStatusCode Status, string Body) pmaBody)
    {
        var options = new FdaCollectorOptions();
        var reader = new HttpFdaClearanceReader(
            new HttpClient(new RoutingHandler((HttpStatusCode.NotFound, EmptySearch404), pmaBody)),
            NullLogger<HttpFdaClearanceReader>.Instance,
            options);

        var collector = new FdaClearanceCollector(
            reader,
            NullLogger<FdaClearanceCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options);

        return (reader, collector);
    }

    private static CollectionContext CreateContext()
    {
        var company = new Company(
            Id: TmdxId,
            Name: "TransMedics",
            LegalName: null,
            Ticker: "TMDX",
            Exchange: null,
            CountryCode: null,
            Sector: null,
            Industry: null,
            Status: CompanyStatus.Active,
            CreatedAtUtc: FixedNow,
            UpdatedAtUtc: FixedNow,
            Themes: []);

        var feed = new CompanySourceFeed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            TmdxId,
            "fda",
            "TransMedics — Recent FDA device clearances (openFDA)",
            "applicant=TransMedics",
            FixedNow);

        return new CollectionContext([company], [feed]);
    }

    // Routes to the 510(k) or PMA canned response by request path, so one reader call that hits both endpoints
    // gets the right body for each.
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
