using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.SignalExtraction;

public class KeywordSignalExtractorTests
{
    private static readonly DateTimeOffset PublishedAt =
        new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CollectedAt =
        new(2026, 1, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static EvidenceItem MakeEvidence(
        string rawText,
        string title = "Untitled",
        string sourceName = "Acme Newsroom",
        DateTimeOffset? publishedAtUtc = null) =>
        new EvidenceBuilder()
            .WithTitle(title)
            .WithSourceName(sourceName)
            .WithRawText(rawText)
            .WithPublishedAtUtc(publishedAtUtc)
            .WithCollectedAtUtc(CollectedAt)
            .Build();

    private static async Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence) =>
        await new KeywordSignalExtractor(NullLogger<KeywordSignalExtractor>.Instance).ExtractAsync(evidence, CancellationToken.None);

    [Fact]
    public async Task BodyWithCustomerWinPhrase_YieldsSinglePositiveSignal_WithVerbatimExcerpt()
    {
        var evidence = MakeEvidence(
            "Acme Corp signed a multi-year deal with a Fortune 500 enterprise customer this quarter.");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CustomerWin.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
        var composed = (evidence.Title ?? string.Empty) + "\n" + (evidence.RawText ?? string.Empty);
        Assert.Contains(signal.SupportingExcerpt, composed, StringComparison.Ordinal);
        Assert.Equal(evidence.SourceName, signal.CompanyMention);
    }

    [Fact]
    public async Task EventOnlyInTitle_YieldsExpectedSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Acme awarded contract by NASA");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
        Assert.Contains("awarded contract", signal.SupportingExcerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EventOnlyInBody_StillYieldsSignal_Regression()
    {
        var evidence = MakeEvidence(
            rawText: "Today Acme was awarded contract worth millions by a federal agency.",
            title: "Quarterly newsroom update");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        var composed = (evidence.Title ?? string.Empty) + "\n" + (evidence.RawText ?? string.Empty);
        Assert.Contains(signal.SupportingExcerpt, composed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SamePhraseInTitleAndBody_YieldsSingleSignal_WithExcerptFromComposedText()
    {
        var evidence = MakeEvidence(
            rawText: "Acme launches a new analytics platform for customers.",
            title: "Acme launches new platform");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.ProductLaunch.ToString(), signal.SignalType);

        var composed = (evidence.Title ?? string.Empty) + "\n" + (evidence.RawText ?? string.Empty);
        Assert.Contains(signal.SupportingExcerpt, composed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TitleSourcedExcerpts_RoundTripToValidSignals()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Acme awarded contract and launches new platform",
            publishedAtUtc: PublishedAt);

        var output = await ExtractAsync(evidence);

        Assert.NotEmpty(output.Signals);
        foreach (var extracted in output.Signals)
        {
            var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }
    }

    [Fact]
    public async Task BodyWithTwoTypes_YieldsTwoSignals_InStableEnumOrder()
    {
        var evidence = MakeEvidence(
            "Acme launches a new analytics platform and announces a partnership with Globex.");

        var output = await ExtractAsync(evidence);

        Assert.Equal(2, output.Signals.Count);
        // StrategicPartnership (enum 1) before ProductLaunch (enum 3).
        Assert.Equal(SignalType.StrategicPartnership.ToString(), output.Signals[0].SignalType);
        Assert.Equal(SignalType.ProductLaunch.ToString(), output.Signals[1].SignalType);
    }

    [Fact]
    public async Task BodyWithNoKnownPhrase_YieldsEmptySignals_AndNonNullSummary()
    {
        var evidence = MakeEvidence("Acme Corp held its annual shareholder meeting today.");

        var output = await ExtractAsync(evidence);

        Assert.Empty(output.Signals);
        Assert.NotNull(output.OverallSummary);
    }

    [Fact]
    public async Task TwoPhrasesSameType_YieldSingleDedupedSignal()
    {
        var evidence = MakeEvidence(
            "Acme raises guidance for the year and separately raises outlook for the next quarter.");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GuidanceChange.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task NegativePhrase_YieldsNegativeDirection()
    {
        var evidence = MakeEvidence("Acme cuts guidance for the full year amid weak demand.");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GuidanceChange.ToString(), signal.SignalType);
        Assert.Equal("Negative", signal.Direction);
    }

    [Fact]
    public async Task EachEmittedSignal_RoundTripsToValidSignal()
    {
        var evidence = MakeEvidence(
            "Acme launches a new platform and signs a multi-year deal with a major customer.",
            publishedAtUtc: PublishedAt);

        var output = await ExtractAsync(evidence);

        Assert.NotEmpty(output.Signals);
        foreach (var extracted in output.Signals)
        {
            var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(evidence.Id, result.Signal!.EvidenceId);
            Assert.Equal(SignalReviewStatus.Pending, result.Signal!.ReviewStatus);
        }
    }

    [Fact]
    public async Task Determinism_TwoCalls_YieldEqualSequences()
    {
        var evidence = MakeEvidence(
            "Acme launches a new platform, announces a partnership, and signs a multi-year deal.");

        var first = await ExtractAsync(evidence);
        var second = await ExtractAsync(evidence);

        var firstKeys = first.Signals
            .Select(s => (s.SignalType, s.Direction, s.SupportingExcerpt))
            .ToList();
        var secondKeys = second.Signals
            .Select(s => (s.SignalType, s.Direction, s.SupportingExcerpt))
            .ToList();

        Assert.Equal(firstKeys, secondKeys);
    }

    [Fact]
    public async Task NullEvidence_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new KeywordSignalExtractor(NullLogger<KeywordSignalExtractor>.Instance).ExtractAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        var evidence = MakeEvidence("Acme signs a multi-year deal.");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new KeywordSignalExtractor(NullLogger<KeywordSignalExtractor>.Instance).ExtractAsync(evidence, cts.Token));
    }
}
