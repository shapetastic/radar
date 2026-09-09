using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Domain.Filings;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// Spec 215 §1 — the analyzer's reported-metrics half: the typed response's <c>reportedMetrics</c> list is
/// verified against the TRUNCATED body and returned beside the unchanged directional read; with extraction
/// disabled the pre-215 instruction is sent and nothing is examined; a response that cannot be trusted
/// examines nothing.
/// </summary>
public sealed class ChatFilingAnalyzerReportedMetricsTests
{
    private const string Body =
        "Argan, Inc. Reports Second Quarter Fiscal 2027 Results. Revenues for the second quarter of "
        + "fiscal 2027 were $384.0 million, up 68% from "
        + "$227.0 million in the prior-year quarter. Project backlog of $2.518 billion as of July 31, 2026.";

    private const string Response =
        """
        {
          "direction": "Improving",
          "confidence": 0.85,
          "rationale": "Revenue rose 68% and backlog stands at $2.5B.",
          "reportedMetrics": [
            { "metric": "Revenue", "value": "384.0", "unit": "million", "period": "second quarter of fiscal 2027",
              "priorValue": "227.0", "priorPeriod": "prior-year quarter",
              "quote": "Revenues for the second quarter of fiscal 2027 were $384.0 million, up 68% from $227.0 million in the prior-year quarter." },
            { "metric": "Backlog", "value": "2.518", "unit": "billion", "period": "as of July 31, 2026",
              "quote": "Project backlog of $2.518 billion as of July 31, 2026." },
            { "metric": "Guidance", "value": "1.0", "unit": "billion", "period": "fiscal 2027", "quote": "Project backlog of $2.518 billion" },
            { "metric": "GrossMargin", "value": "24%", "unit": "", "period": "second quarter", "quote": "gross margin of 24%" }
          ]
        }
        """;

    private static ChatFilingAnalyzer Build(FakeChatClient client, int maxInputLength = 12000, bool extract = true) =>
        new(
            client,
            new FilingAnalyzerOptions { MaxInputLength = maxInputLength, ExtractReportedMetrics = extract },
            NullLogger<ChatFilingAnalyzer>.Instance);

    [Fact]
    public async Task VerifiedMetrics_RideTheRead_BesideTheUnchangedSentiment_WithEveryDropCounted()
    {
        var client = new FakeChatClient(Response);

        var read = await Build(client).AnalyzeAsync(Body, CancellationToken.None);

        Assert.Equal(FilingDirection.Improving, read.Sentiment.Direction);
        Assert.Equal(0.85m, read.Sentiment.Confidence);
        var metrics = Assert.IsType<VerifiedReportedMetrics>(read.ReportedMetrics);
        Assert.Equal(
            [ReportedMetric.Revenue, ReportedMetric.Backlog],
            metrics.Verified.Select(m => m.Metric).ToList());
        Assert.Equal("227.0", metrics.Verified[0].PriorValue);
        Assert.Equal(1, metrics.DroppedUnrecognised); // Guidance
        Assert.Equal(1, metrics.DroppedUnverified); // the margin quote is not in the body
        Assert.Equal(0, metrics.DroppedDuplicate);

        // The system message the model was sent carries the reported-metrics paragraph.
        Assert.Equal(ChatFilingAnalyzer.SystemInstruction, client.CapturedMessages[0].Text);
        Assert.Contains("reportedMetrics", client.CapturedMessages[0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuotePastTheInputCap_IsDroppedUnverified_BecauseTheModelNeverSawIt()
    {
        var client = new FakeChatClient(Response);

        // The cap cuts the body before the backlog sentence.
        var read = await Build(
                client, maxInputLength: Body.IndexOf("Project backlog", StringComparison.Ordinal))
            .AnalyzeAsync(Body, CancellationToken.None);

        var metrics = Assert.IsType<VerifiedReportedMetrics>(read.ReportedMetrics);
        Assert.Equal([ReportedMetric.Revenue], metrics.Verified.Select(m => m.Metric).ToList());
        Assert.Equal(2, metrics.DroppedUnverified);
    }

    [Fact]
    public async Task ExtractionDisabled_SendsThePre215Instruction_AndExaminesNothing()
    {
        var client = new FakeChatClient(Response);

        var read = await Build(client, extract: false).AnalyzeAsync(Body, CancellationToken.None);

        Assert.Equal(FilingDirection.Improving, read.Sentiment.Direction);
        Assert.Null(read.ReportedMetrics);
        Assert.Equal(ChatFilingAnalyzer.SentimentInstruction, client.CapturedMessages[0].Text);
        Assert.DoesNotContain("reportedMetrics", client.CapturedMessages[0].Text!, StringComparison.Ordinal);
        Assert.Equal(ChatRole.System, client.CapturedMessages[0].Role);
    }

    [Fact]
    public async Task AResponseWithNoMetricList_ExaminesAnEmptyList_WithMeasuredZeroDrops()
    {
        var client = new FakeChatClient(
            """{"direction":"Mixed","confidence":0.7,"rationale":"Two-sided."}""");

        var read = await Build(client).AnalyzeAsync(Body, CancellationToken.None);

        Assert.Equal(FilingDirection.Mixed, read.Sentiment.Direction);
        var metrics = Assert.IsType<VerifiedReportedMetrics>(read.ReportedMetrics);
        Assert.Empty(metrics.Verified);
        Assert.Equal(0, metrics.DroppedUnrecognised + metrics.DroppedUnverified + metrics.DroppedDuplicate);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"direction":"Sideways","confidence":0.5,"rationale":"x","reportedMetrics":[]}""")]
    [InlineData("""{"direction":99,"confidence":0.5,"rationale":"x"}""")]
    public async Task AnUntrustedResponse_IsUnknown_WithNothingExamined(string response)
    {
        var read = await Build(new FakeChatClient(response)).AnalyzeAsync(Body, CancellationToken.None);

        Assert.Equal(FilingDirection.Unknown, read.Sentiment.Direction);
        Assert.Equal(string.Empty, read.Sentiment.Rationale);
        Assert.Null(read.ReportedMetrics);
    }

    [Fact]
    public async Task AGenuineUnknownRead_KeepsItsRationale_AndStillExaminesMetrics()
    {
        var client = new FakeChatClient(
            """{"direction":"Unknown","confidence":0.3,"rationale":"Boilerplate only.","reportedMetrics":[]}""");

        var read = await Build(client).AnalyzeAsync(Body, CancellationToken.None);

        Assert.Equal(FilingDirection.Unknown, read.Sentiment.Direction);
        Assert.Equal(0m, read.Sentiment.Confidence);
        Assert.Equal("Boilerplate only.", read.Sentiment.Rationale);
        Assert.NotNull(read.ReportedMetrics);
    }

    [Fact]
    public async Task EmptyInput_AndAFailedCall_ExamineNothing()
    {
        Assert.Null((await Build(new FakeChatClient(Response)).AnalyzeAsync("  ", CancellationToken.None)).ReportedMetrics);
        Assert.Null(
            (await Build(new FakeChatClient(Response, throwOnCall: new InvalidOperationException("down")))
                .AnalyzeAsync(Body, CancellationToken.None)).ReportedMetrics);
    }
}
