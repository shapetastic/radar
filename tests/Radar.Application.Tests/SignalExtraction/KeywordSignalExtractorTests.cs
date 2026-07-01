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
    public async Task GovernmentContractCues_YieldGovernmentContractSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Acme awarded a defence contract by NASA");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Theory]
    [InlineData("Acme wins a DoD contract for radar systems.")]
    [InlineData("Acme signs a five-year deal with the Department of Defense.")]
    public async Task DefenseDepartmentCues_YieldGovernmentContractSignal(string rawText)
    {
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task SelectedByNasa_YieldsBothCustomerWinAndGovernmentContract_InEnumOrder()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Acme selected by NASA for a new mission");

        var output = await ExtractAsync(evidence);

        Assert.Equal(2, output.Signals.Count);
        // CustomerWin (enum 0) before GovernmentContract (enum 6).
        Assert.Equal(SignalType.CustomerWin.ToString(), output.Signals[0].SignalType);
        Assert.Equal(SignalType.GovernmentContract.ToString(), output.Signals[1].SignalType);
    }

    [Theory]
    [InlineData("Acme wins contract with a major retailer.")]
    [InlineData("Acme lands a major contract win with a global retailer.")]
    [InlineData("Acme renews agreement with a major retailer.")]
    [InlineData("Acme expands agreement with a major retailer.")]
    public async Task NewCustomerWinPhrases_YieldSingleCustomerWinSignal(string rawText)
    {
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CustomerWin.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Theory]
    [InlineData("Acme rolls out a refreshed analytics suite for customers.")]
    [InlineData("Acme debuts a new platform for enterprise customers.")]
    [InlineData("Acme announces general availability of its analytics suite.")]
    public async Task NewProductLaunchPhrases_YieldSingleProductLaunchSignal(string rawText)
    {
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.ProductLaunch.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Theory]
    [InlineData("Acme secures a new credit facility from its lenders.")]
    [InlineData("Acme completes a debt financing round.")]
    [InlineData("Acme issues a convertible note to investors.")]
    public async Task NewCapitalRaisePhrases_YieldSingleCapitalRaiseSignal(string rawText)
    {
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CapitalRaise.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Theory]
    [InlineData("Acme cuts outlook for the full year amid weak demand.")]
    [InlineData("Acme lowers outlook for the next quarter.")]
    public async Task NewNegativeGuidancePhrases_YieldNegativeGuidanceChange(string rawText)
    {
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GuidanceChange.ToString(), signal.SignalType);
        Assert.Equal("Negative", signal.Direction);
    }

    [Fact]
    public async Task MultiplePhrasesSameType_YieldSingleDedupedSignal_AndRoundTripValid()
    {
        var evidence = MakeEvidence(
            rawText: "Acme awarded a defence contract; the public procurement was a government grant.",
            title: "Acme wins NASA work",
            publishedAtUtc: PublishedAt);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);

        var result = ExtractedSignalMapper.ToSignal(signal, evidence, CreatedAt);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task ProjectWinsHeadline_YieldsCustomerWinSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "New Wastewater Project Wins Across India");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CustomerWin.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task ExceededOutlookHeadline_YieldsPositiveGuidanceChangeSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Reports First Quarter Results that Exceeded Outlook with Sales Growth of 17%");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GuidanceChange.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task LargestProductionOrderHeadline_YieldsCustomerWinSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Receives Largest Production Order for its Rugged Servers");

        var output = await ExtractAsync(evidence);

        Assert.Contains(
            output.Signals,
            s => s.SignalType == SignalType.CustomerWin.ToString());
    }

    [Fact]
    public async Task Series3Headline_DoesNotYieldCapitalRaiseSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Introduces the Series 3 pressure sensor");

        var output = await ExtractAsync(evidence);

        // "introduces" still legitimately fires a ProductLaunch; only CapitalRaise must be absent.
        Assert.DoesNotContain(
            output.Signals,
            s => s.SignalType == SignalType.CapitalRaise.ToString());
    }

    [Fact]
    public async Task BenefitsAwardHeadline_DoesNotYieldGovernmentContractSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Recognized with Top Benefits Award from Mployer");

        var output = await ExtractAsync(evidence);

        Assert.DoesNotContain(
            output.Signals,
            s => s.SignalType == SignalType.GovernmentContract.ToString());
    }

    [Fact]
    public async Task UnqualifiedDeployment_DoesNotYieldCustomerWinSignal()
    {
        var evidence = MakeEvidence(
            "Acme discusses its software deployment process at a conference.");

        var output = await ExtractAsync(evidence);

        Assert.DoesNotContain(
            output.Signals,
            s => s.SignalType == SignalType.CustomerWin.ToString());
    }

    [Fact]
    public async Task ResultsOfOperationsTitle_YieldsNeutralGuidanceChange_NoSpuriousPositiveTrajectory()
    {
        // Mirrors the SEC 8-K item 2.02 evidence text: raw code plus its official item title.
        var evidence = MakeEvidence(
            rawText: "8-K filing accession acc-1 filed 2026-06-02: Report. 8-K item codes: 2.02,9.01. "
                + "Items: Results of Operations and Financial Condition.",
            title: "8-K — Report (2026-06-02)");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GuidanceChange.ToString(), signal.SignalType);
        // Item code encodes the event type but not beat/miss: must be Neutral, never a Positive read.
        Assert.Equal("Neutral", signal.Direction);
        Assert.DoesNotContain(output.Signals, s => s.Direction == "Positive");
    }

    [Fact]
    public async Task MaterialDefinitiveAgreementTitle_YieldsPositiveStrategicPartnership()
    {
        var evidence = MakeEvidence(
            "Items: Entry into a Material Definitive Agreement.");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.StrategicPartnership.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task CompletionOfAcquisitionTitle_YieldsPositiveStrategicPartnership()
    {
        var evidence = MakeEvidence(
            "Items: Completion of Acquisition or Disposition of Assets.");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.StrategicPartnership.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task OfficerChangeTitle_YieldsNeutralExecutiveHire()
    {
        var evidence = MakeEvidence(
            "Items: Departure of Directors or Certain Officers; Election of Directors; Appointment of Certain Officers.");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.ExecutiveHire.ToString(), signal.SignalType);
        // Item 5.02 covers departures and appointments alike; the code cannot tell which.
        Assert.Equal("Neutral", signal.Direction);
    }

    [Theory]
    [InlineData("Items: Creation of a Direct Financial Obligation.")]
    [InlineData("Items: Unregistered Sales of Equity Securities.")]
    public async Task CapitalItemTitles_YieldNeutralCapitalRaise(string rawText)
    {
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CapitalRaise.ToString(), signal.SignalType);
        Assert.Equal("Neutral", signal.Direction);
    }

    [Fact]
    public async Task DirectionalGuidancePhrase_WithResultsOfOperations_KeepsDirectionalWinner()
    {
        // When a press release contains both a directional cue and the generic "results of operations"
        // phrase, first-match-per-type ordering must keep the directional (Positive) rule.
        var evidence = MakeEvidence(
            "Acme reports results of operations and raises guidance for the full year.");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GuidanceChange.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
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
