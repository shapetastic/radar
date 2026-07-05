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
        await new KeywordSignalExtractor(
            NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights())
            .ExtractAsync(evidence, CancellationToken.None);

    // Builds an extractor with the given insider-materiality weights (spec 96): the default tiers reproduce
    // the spec-93 table, while custom weights let a test exercise buy/sell asymmetry or a non-default cluster
    // boost with no code change.
    private static async Task<ExtractSignalsOutput> ExtractWithWeightsAsync(
        EvidenceItem evidence, InsiderMaterialityWeights weights) =>
        await new KeywordSignalExtractor(NullLogger<KeywordSignalExtractor>.Instance, weights)
            .ExtractAsync(evidence, CancellationToken.None);

    // Builds the nested metadata JSON shape written by the USASpending collector:
    // { "metadata": { … "awardAmount": "<value>" … }, "companyHints": [ … ] }, so the tests exercise
    // the real production parse path (root.metadata.awardAmount as a string).
    private static string GovMetadataJson(string awardAmount) =>
        $$"""
        { "metadata": { "quality": "High", "awardAmount": "{{awardAmount}}", "awardingAgency": "Department of Defense", "startDate": "2026-05-01" }, "companyHints": [ "ACME DEFENSE INC" ] }
        """;

    // USASpending-shaped GovernmentContract evidence with an optional MetadataJson carrying awardAmount.
    private static EvidenceItem MakeGovContractEvidence(
        string? metadataJson,
        string title = "Federal contract award W912DY23C0007 — Department of Defense → ACME DEFENSE INC",
        string rawText = "Federal contract award W912DY23C0007: Department of Defense awarded ACME DEFENSE INC.") =>
        new EvidenceBuilder()
            .WithTitle(title)
            .WithRawText(rawText)
            .WithCollectedAtUtc(CollectedAt)
            .WithMetadataJson(metadataJson)
            .Build();

    private static ExtractedSignal SingleGovContractSignal(ExtractSignalsOutput output)
    {
        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
        return signal;
    }

    [Fact]
    public async Task LargeAward_YieldsHighStrengthGovernmentContractSignal()
    {
        // ~$52M award (the large award from the Mercury Systems distortion example) => Strength 8.
        var evidence = MakeGovContractEvidence(GovMetadataJson("52000000.00"));

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(8, signal.Strength);
    }

    [Fact]
    public async Task VeryLargeAward_YieldsTopTierStrength()
    {
        // >= $100M => top tier Strength 9.
        var evidence = MakeGovContractEvidence(GovMetadataJson("250000000"));

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(9, signal.Strength);
    }

    [Fact]
    public async Task MidAward_YieldsBaselineStrength_NoRegression()
    {
        // $3.5M award => baseline Strength 6, equal to the old fixed value (no regression).
        var evidence = MakeGovContractEvidence(GovMetadataJson("3500000"));

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(6, signal.Strength);
    }

    [Fact]
    public async Task SmallAward_YieldsSubMaterialStrength()
    {
        // ~$500k routine DoD order => Strength 4 (above the materiality floor, modest contribution).
        var evidence = MakeGovContractEvidence(GovMetadataJson("508575.00"));

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(4, signal.Strength);
    }

    [Fact]
    public async Task TinyAward_YieldsFloorStrength_BelowMaterialityThreshold()
    {
        // < $100k routine order (the seeded ~$6,775 HHS order) => floor Strength 2, deliberately below
        // the reviewer's MinMaterialStrength (strict < 3) so the existing guardrail flags it.
        var evidence = MakeGovContractEvidence(GovMetadataJson("6775"));

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(2, signal.Strength);
        Assert.True(signal.Strength < 3);
    }

    [Fact]
    public async Task GovContract_NoAwardAmountInMetadata_FallsBackToFixedStrength()
    {
        // Gov evidence whose metadata lacks awardAmount => fixed rule Strength 6, no throw.
        var metadataWithoutAmount =
            """{ "metadata": { "quality": "High", "awardingAgency": "Department of Defense" }, "companyHints": [ ] }""";
        var evidence = MakeGovContractEvidence(metadataWithoutAmount);

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(6, signal.Strength);
    }

    [Fact]
    public async Task GovContract_NullMetadataJson_FallsBackToFixedStrength()
    {
        var evidence = MakeGovContractEvidence(metadataJson: null);

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(6, signal.Strength);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("0")]              // USASpending normalizes a missing/non-numeric "Award Amount" to 0m and
    [InlineData("0.00")]           // still serializes it — a "0" is a no-amount case, not a floor Strength 2.
    [InlineData("-508575.00")]     // A negative amount is not a usable award magnitude either.
    public async Task GovContract_UnusableAwardAmount_FallsBackToFixedStrength(string awardAmount)
    {
        var evidence = MakeGovContractEvidence(GovMetadataJson(awardAmount));

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(6, signal.Strength);
    }

    [Fact]
    public async Task GovContract_MalformedMetadataJson_FallsBackToFixedStrength()
    {
        var evidence = MakeGovContractEvidence(metadataJson: "{ this is not json");

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(6, signal.Strength);
    }

    [Fact]
    public async Task NonGovernmentContractSignal_IsUnaffectedByAwardAmountMetadata()
    {
        // A CustomerWin firing evidence that ALSO carries a (huge) awardAmount must keep its fixed
        // Strength 6 — amount metadata never touches a non-GovernmentContract signal.
        var evidence = new EvidenceBuilder()
            .WithTitle("Acme signs multi-year deal")
            .WithRawText("Acme signed a multi-year deal with a major enterprise customer.")
            .WithCollectedAtUtc(CollectedAt)
            .WithMetadataJson(GovMetadataJson("250000000"))
            .Build();

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CustomerWin.ToString(), signal.SignalType);
        Assert.Equal(6, signal.Strength);
    }

    [Fact]
    public async Task AmountBearingGovContract_Determinism_TwoCalls_YieldEqualSequences_IncludingStrength()
    {
        var evidence = MakeGovContractEvidence(GovMetadataJson("52000000.00"));

        var first = await ExtractAsync(evidence);
        var second = await ExtractAsync(evidence);

        var firstKeys = first.Signals
            .Select(s => (s.SignalType, s.Direction, s.Strength, s.SupportingExcerpt))
            .ToList();
        var secondKeys = second.Signals
            .Select(s => (s.SignalType, s.Direction, s.Strength, s.SupportingExcerpt))
            .ToList();

        Assert.Equal(firstKeys, secondKeys);
        Assert.All(firstKeys, k => Assert.Equal(8, k.Strength));
    }

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
    public async Task DemotedDebtCapitalRaisePhrases_YieldSingleNeutralCapitalRaiseSignal(string rawText)
    {
        // Spec 86: "credit facility" / "debt financing" / "convertible note" are debt/hybrid capital events
        // whose valence the code cannot read, so they were demoted from Positive to Neutral (contribute 0 to
        // Trajectory) rather than over-claim growth.
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CapitalRaise.ToString(), signal.SignalType);
        Assert.Equal("Neutral", signal.Direction);
    }

    [Theory]
    [InlineData("Boilerplate about the company.", "Eos Energy Announces Commencement of Rights Offering")]
    [InlineData("Announces Proposed Registered Direct Offering of Common Stock and Warrants.", "Untitled")]
    [InlineData("Company enters into an at-the-market offering program.", "Untitled")]
    [InlineData("ATM offering of up to $50 million.", "Untitled")]
    [InlineData("Files shelf registration statement.", "Untitled")]
    [InlineData("The company prices a shelf offering.", "Untitled")]
    [InlineData("The board announces a 1-for-10 reverse stock split.", "Untitled")]
    [InlineData("Issues warrants to purchase up to 5,000,000 shares.", "Untitled")]
    [InlineData("The transaction is dilutive to existing shareholders.", "Untitled")]
    public async Task DilutiveOfferingPhrases_YieldSingleNegativeCapitalRaiseSignal(string rawText, string title)
    {
        // Spec 86: dilutive / distress capital-structure events (rights / registered-direct / ATM / shelf /
        // warrant / reverse-split offerings, "dilutive") are Negative CapitalRaise signals so a diluted
        // company is no longer scored neutral-to-positive.
        var evidence = MakeEvidence(rawText, title: title);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CapitalRaise.ToString(), signal.SignalType);
        Assert.Equal("Negative", signal.Direction);
    }

    [Fact]
    public async Task MixedDilutiveAndRaisesHeadline_YieldsSingleNegativeCapitalRaise()
    {
        // Ordering guarantee: the Negative dilution cues are placed before the Positive "raises $" cue, so a
        // headline mixing both resolves to Negative via first-match-per-type.
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Announces Registered Direct Offering; raises $30 million");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CapitalRaise.ToString(), signal.SignalType);
        Assert.Equal("Negative", signal.Direction);
    }

    [Theory]
    [InlineData("Acme raises $12 million in a funding round.")]
    [InlineData("Acme closes its Series B.")]
    [InlineData("Acme completes a series seed round.")]
    public async Task VentureRaisePhrases_StillYieldSinglePositiveCapitalRaiseSignal(string rawText)
    {
        // Regression: the demotion of the debt cues must not over-reach — genuine growth-leaning venture
        // financing stays Positive.
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CapitalRaise.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Theory]
    [InlineData("Acme launches a new product offering for enterprises.")]
    [InlineData("Acme extends the standard warranty to five years.")]
    public async Task NonDilutiveOfferingPhrases_DoNotYieldCapitalRaiseSignal(string rawText)
    {
        // Scope guard: bare "offering" ("product offering") and "warranty" must NOT match the tightly-scoped
        // multi-word dilution cues ("...offering" phrases, "warrants to purchase").
        var evidence = MakeEvidence(rawText);

        var output = await ExtractAsync(evidence);

        Assert.DoesNotContain(
            output.Signals,
            s => s.SignalType == SignalType.CapitalRaise.ToString());
    }

    [Fact]
    public async Task DilutiveOfferingSignal_RoundTripsToValidSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: "Eos Energy Announces Commencement of Rights Offering",
            publishedAtUtc: PublishedAt);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.CapitalRaise.ToString(), signal.SignalType);
        Assert.Equal("Negative", signal.Direction);

        var result = ExtractedSignalMapper.ToSignal(signal, evidence, CreatedAt);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
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
    public async Task NonDodUsaSpendingAward_YieldsSingleGovernmentContractPositiveSignal_WithVerbatimExcerpt()
    {
        // Mirrors the real USASpending collector output for a non-DoD (HHS) award: previously this text
        // matched no GovernmentContract rule and produced zero signals. The "federal contract award" cue
        // now guarantees exactly one GovernmentContract Positive signal, regardless of awarding agency.
        var evidence = MakeEvidence(
            rawText: "Federal contract award 75D30122P12345 (generated_internal_id CONT_AWD_1, "
                + "recipient_id r-1): Department of Health and Human Services awarded AGILYSYS INC "
                + "$6,775 starting 2026-02-10. Description: software maintenance.",
            title: "Federal contract award 75D30122P12345 — Department of Health and Human Services "
                + "→ AGILYSYS INC ($6,775, 2026-02-10)");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
        var composed = (evidence.Title ?? string.Empty) + "\n" + (evidence.RawText ?? string.Empty);
        Assert.Contains(signal.SupportingExcerpt, composed, StringComparison.Ordinal);
        Assert.Equal(evidence.SourceName, signal.CompanyMention);
    }

    [Theory]
    [InlineData("Federal contract award GS-35F-0119Y — General Services Administration → AGILYSYS INC ($12,340, 2026-03-01)")]
    [InlineData("Federal contract award 36C10X22P0042 — Department of Veterans Affairs → CRYOPORT INC ($4,809, 2026-01-20)")]
    [InlineData("Federal contract award DE-AC02-06 — Department of Energy → ACME LABS INC ($88,000, 2026-04-15)")]
    public async Task UsaSpendingAward_IsAgencyIndependent_YieldsSingleGovernmentContractPositiveSignal(string title)
    {
        var evidence = MakeEvidence(
            rawText: "Boilerplate about the company.",
            title: title);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GovernmentContract.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task DodUsaSpendingAward_YieldsExactlyOneGovernmentContractSignal_NotTwo()
    {
        // Text contains BOTH "Federal contract award …" and "Department of Defense": first-match-per-type
        // dedupe must still emit exactly one GovernmentContract signal.
        var evidence = MakeEvidence(
            rawText: "Federal contract award W912DY23C0007 (generated_internal_id CONT_AWD_9, "
                + "recipient_id r-9): Department of Defense awarded ACME DEFENSE INC $52,000,000 "
                + "starting 2026-05-01. Description: radar systems.",
            title: "Federal contract award W912DY23C0007 — Department of Defense → ACME DEFENSE INC "
                + "($52,000,000, 2026-05-01)");

        var output = await ExtractAsync(evidence);

        var governmentContractSignals = output.Signals
            .Where(s => s.SignalType == SignalType.GovernmentContract.ToString())
            .ToList();

        var signal = Assert.Single(governmentContractSignals);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task UsaSpendingAwardSignal_RoundTripsToValidSignal()
    {
        var evidence = MakeEvidence(
            rawText: "Federal contract award 75D30122P12345 (generated_internal_id CONT_AWD_1, "
                + "recipient_id r-1): Department of Health and Human Services awarded AGILYSYS INC "
                + "$6,775 starting 2026-02-10. Description: software maintenance.",
            title: "Federal contract award 75D30122P12345 — Department of Health and Human Services "
                + "→ AGILYSYS INC ($6,775, 2026-02-10)",
            publishedAtUtc: PublishedAt);

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        var result = ExtractedSignalMapper.ToSignal(signal, evidence, CreatedAt);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task UsaSpendingAward_Determinism_TwoCalls_YieldEqualSequences()
    {
        var evidence = MakeEvidence(
            rawText: "Federal contract award 75D30122P12345 (generated_internal_id CONT_AWD_1, "
                + "recipient_id r-1): Department of Health and Human Services awarded AGILYSYS INC "
                + "$6,775 starting 2026-02-10. Description: software maintenance.",
            title: "Federal contract award 75D30122P12345 — Department of Health and Human Services "
                + "→ AGILYSYS INC ($6,775, 2026-02-10)");

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

    // --- InsiderBuying (SEC Form 4; spec 93) ---

    // Builds the nested metadata JSON shape written by the Form 4 collector:
    // { "metadata": { … "insiderNetValue": "<value>" … }, "companyHints": [ … ] }, so the tests exercise
    // the real production parse path (root.metadata.insiderNetValue as a string).
    private static string InsiderMetadataJson(string insiderNetValue) =>
        $$"""
        { "metadata": { "quality": "High", "insiderNetValue": "{{insiderNetValue}}", "form": "4" }, "companyHints": [ "MRCY" ] }
        """;

    private static EvidenceItem MakeInsiderEvidence(string phrase, string? metadataJson = null) =>
        new EvidenceBuilder()
            .WithTitle($"Form 4 — {phrase}: JANE DOE ({"2026-06-02"})")
            .WithRawText($"Form 4 accession 0001-26-1 filed 2026-06-02: {phrase} — JANE DOE.")
            .WithCollectedAtUtc(CollectedAt)
            .WithMetadataJson(metadataJson)
            .Build();

    [Fact]
    public async Task InsiderPurchasePhrase_YieldsPositiveInsiderBuying()
    {
        var evidence = MakeInsiderEvidence("insider open-market purchase");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.InsiderBuying.ToString(), signal.SignalType);
        Assert.Equal("Positive", signal.Direction);
    }

    [Fact]
    public async Task InsiderSalePhrase_YieldsNegativeInsiderBuying()
    {
        var evidence = MakeInsiderEvidence("insider open-market sale");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.InsiderBuying.ToString(), signal.SignalType);
        Assert.Equal("Negative", signal.Direction);
    }

    [Fact]
    public async Task InsiderRoutinePhrase_YieldsNeutralInsiderBuying_KeepsFixedStrength()
    {
        var evidence = MakeInsiderEvidence("insider stock transaction (routine)");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.InsiderBuying.ToString(), signal.SignalType);
        Assert.Equal("Neutral", signal.Direction);
        Assert.Equal(3, signal.Strength);
    }

    [Theory]
    [InlineData("5000000", 8)]
    [InlineData("1000000", 7)]
    [InlineData("250000", 6)]
    [InlineData("50000", 4)]
    [InlineData("10000", 2)]
    public async Task InsiderPurchase_StrengthScalesByNetValueTiers(string netValue, int expectedStrength)
    {
        var evidence = MakeInsiderEvidence("insider open-market purchase", InsiderMetadataJson(netValue));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal("Positive", signal.Direction);
        Assert.Equal(expectedStrength, signal.Strength);
    }

    [Theory]
    [InlineData("5000000", 8)]
    [InlineData("250000", 6)]
    [InlineData("10000", 2)]
    public async Task InsiderSale_StrengthScalesByNetValueTiers(string netValue, int expectedStrength)
    {
        var evidence = MakeInsiderEvidence("insider open-market sale", InsiderMetadataJson(netValue));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal("Negative", signal.Direction);
        Assert.Equal(expectedStrength, signal.Strength);
    }

    [Fact]
    public async Task InsiderPurchase_NoNetValueMetadata_KeepsFixedStrength()
    {
        var evidence = MakeInsiderEvidence("insider open-market purchase");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal("Positive", signal.Direction);
        Assert.Equal(6, signal.Strength);
    }

    [Fact]
    public async Task InsiderRoutine_WithNetValueMetadata_StillKeepsFixedNeutralStrength()
    {
        // A Neutral routine phrase never scales, even if an insiderNetValue somehow appears.
        var evidence = MakeInsiderEvidence("insider stock transaction (routine)", InsiderMetadataJson("5000000"));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal("Neutral", signal.Direction);
        Assert.Equal(3, signal.Strength);
    }

    [Fact]
    public async Task InsiderBuyingSignal_RoundTripsToValidSignal()
    {
        var evidence = MakeInsiderEvidence("insider open-market purchase", InsiderMetadataJson("5000000"));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        var result = ExtractedSignalMapper.ToSignal(signal, evidence, CreatedAt);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // Metadata JSON with both the net value and the multi-insider cluster flag (spec 93 cluster boost).
    private static string InsiderClusterMetadataJson(string insiderNetValue) =>
        $$"""
        { "metadata": { "quality": "High", "insiderNetValue": "{{insiderNetValue}}", "insiderCluster": "true", "form": "4" }, "companyHints": [ "MRCY" ] }
        """;

    [Theory]
    [InlineData("insider open-market purchase", "Positive")]
    [InlineData("insider open-market sale", "Negative")]
    public async Task InsiderCluster_AddsOneToTierStrength(string phrase, string expectedDirection)
    {
        // $1,000,000 -> base tier Strength 7; the cluster flag adds +1 -> 8.
        var evidence = MakeInsiderEvidence(phrase, InsiderClusterMetadataJson("1000000"));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(expectedDirection, signal.Direction);
        Assert.Equal(8, signal.Strength);
    }

    [Fact]
    public async Task InsiderCluster_TopTier_AppliesBoostAndNeverExceedsDomainMax()
    {
        // $5,000,000 -> base top-tier Strength 8; cluster +1 -> 9, and Math.Min(10, ...) keeps it <= 10.
        var evidence = MakeInsiderEvidence("insider open-market purchase", InsiderClusterMetadataJson("5000000"));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(9, signal.Strength);
        Assert.True(signal.Strength <= 10);
    }

    [Fact]
    public async Task InsiderDirectional_WithoutClusterFlag_KeepsPlainTierStrength()
    {
        // No insiderCluster flag: the plain $1,000,000 tier Strength 7 stands (no +1).
        var evidence = MakeInsiderEvidence("insider open-market purchase", InsiderMetadataJson("1000000"));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(7, signal.Strength);
    }

    [Fact]
    public async Task InsiderAsymmetricWeights_BuyOutScoresSellAtSameNetValue()
    {
        // Spec 96: separate buy/sell tables make a deliberate buy-vs-sell asymmetry expressible with no code
        // change. With buy strengths above sell strengths at the same $250,000 net value, a purchase must
        // out-score a sale of identical size.
        var asymmetric = new InsiderMaterialityWeights
        {
            BuyTiers =
            [
                new(250_000m, 8),
                new(decimal.MinValue, 2),
            ],
            SellTiers =
            [
                new(250_000m, 4),
                new(decimal.MinValue, 2),
            ],
        };

        var buy = await ExtractWithWeightsAsync(
            MakeInsiderEvidence("insider open-market purchase", InsiderMetadataJson("250000")), asymmetric);
        var sell = await ExtractWithWeightsAsync(
            MakeInsiderEvidence("insider open-market sale", InsiderMetadataJson("250000")), asymmetric);

        var buySignal = Assert.Single(buy.Signals);
        var sellSignal = Assert.Single(sell.Signals);
        Assert.Equal("Positive", buySignal.Direction);
        Assert.Equal("Negative", sellSignal.Direction);
        Assert.Equal(8, buySignal.Strength);
        Assert.Equal(4, sellSignal.Strength);
        Assert.True(buySignal.Strength > sellSignal.Strength);
    }

    [Fact]
    public async Task InsiderCustomClusterBoost_AppliesAndCapsAtDomainMax()
    {
        // Spec 96: the cluster boost is a config magnitude. With ClusterBoost = 2 the $1,000,000 base tier
        // Strength 7 becomes 9, and the $5,000,000 top-tier Strength 8 becomes Math.Min(10, 8 + 2) = 10.
        var boosted = new InsiderMaterialityWeights { ClusterBoost = 2 };

        var mid = await ExtractWithWeightsAsync(
            MakeInsiderEvidence("insider open-market purchase", InsiderClusterMetadataJson("1000000")), boosted);
        var top = await ExtractWithWeightsAsync(
            MakeInsiderEvidence("insider open-market purchase", InsiderClusterMetadataJson("5000000")), boosted);

        Assert.Equal(9, Assert.Single(mid.Signals).Strength);
        var topSignal = Assert.Single(top.Signals);
        Assert.Equal(10, topSignal.Strength);
        Assert.True(topSignal.Strength <= 10);
    }

    [Fact]
    public async Task InsiderRoutine_WithClusterFlag_IsUnaffected()
    {
        // A Neutral routine phrase never scales and never takes the cluster boost, even if both keys appear.
        var evidence = MakeInsiderEvidence("insider stock transaction (routine)", InsiderClusterMetadataJson("5000000"));

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal("Neutral", signal.Direction);
        Assert.Equal(3, signal.Strength);
    }

    [Fact]
    public async Task GovContract_WithInsiderClusterFlag_IsUnaffectedByClusterBoost()
    {
        // The cluster boost is gated strictly to InsiderBuying: a GovernmentContract signal carrying a stray
        // insiderCluster flag keeps its plain award-tier Strength (no +1).
        var metadata =
            """{ "metadata": { "quality": "High", "awardAmount": "1000000", "insiderCluster": "true" }, "companyHints": [ ] }""";
        var evidence = MakeGovContractEvidence(metadata);

        var output = await ExtractAsync(evidence);

        var signal = SingleGovContractSignal(output);
        Assert.Equal(6, signal.Strength);
    }

    // Builds NewsArticle-typed (third-party) evidence, mirroring GDELT news collector output (spec 67).
    // In GdeltNewsCollector the SourceName is the configured per-company feed name (feed.Name) — not a
    // publication masthead — and RawText is synthesized from real article metadata
    // ("<title> — <domain> (<seendate>). Source: <url>"), never a fabricated article body. The defaults
    // below mirror that shape so readers don't mistake these fields for a news outlet or article body.
    private static EvidenceItem MakeNewsEvidence(
        string title,
        string rawText = "Acme Investor News — finance.example (2026-02-10T00:00:00Z). Source: https://finance.example/acme",
        string sourceName = "Acme Investor News") =>
        new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithTitle(title)
            .WithSourceName(sourceName)
            .WithRawText(rawText)
            .WithCollectedAtUtc(CollectedAt)
            .Build();

    [Fact]
    public async Task NewsArticle_YieldsSingleNeutralMediaAttentionSignal_WithVerbatimExcerpt()
    {
        var evidence = MakeNewsEvidence(
            title: "Aehr Test Systems , Inc . ( AEHR ): Q3 wafer-level test order momentum");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.MediaAttention.ToString(), signal.SignalType);
        Assert.Equal("Neutral", signal.Direction);
        Assert.Equal(4, signal.Strength);
        Assert.Equal(4, signal.Novelty);
        Assert.Equal(0.5m, signal.Confidence);
        var composed = (evidence.Title ?? string.Empty) + "\n" + (evidence.RawText ?? string.Empty);
        // An empty excerpt is a substring of anything, so assert it is genuinely non-blank before Contains.
        Assert.False(string.IsNullOrWhiteSpace(signal.SupportingExcerpt));
        Assert.Contains(signal.SupportingExcerpt, composed, StringComparison.Ordinal);
        Assert.Equal(evidence.SourceName, signal.CompanyMention);
    }

    [Fact]
    public async Task NewsArticle_WithDirectionalCue_YieldsOnlyMediaAttention_NoDirectionalSignal()
    {
        // A headline that on a PressRelease would fire CustomerWin (wins contract) AND GovernmentContract
        // (US Navy is a defense/government cue). On NewsArticle evidence the keyword loop is suppressed, so
        // only the Neutral MediaAttention signal is emitted.
        var evidence = MakeNewsEvidence(title: "Acme wins contract with the US Navy");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.MediaAttention.ToString(), signal.SignalType);
        Assert.Equal("Neutral", signal.Direction);
        Assert.DoesNotContain(output.Signals, s => s.SignalType == SignalType.CustomerWin.ToString());
        Assert.DoesNotContain(output.Signals, s => s.SignalType == SignalType.GovernmentContract.ToString());
    }

    [Fact]
    public async Task NewsArticleMediaAttentionSignal_RoundTripsToValidSignal()
    {
        var evidence = MakeNewsEvidence(
            title: "Aehr Test Systems , Inc . ( AEHR ): Q3 wafer-level test order momentum",
            sourceName: "Yahoo Finance");

        var output = await ExtractAsync(evidence);

        var signal = Assert.Single(output.Signals);
        var result = ExtractedSignalMapper.ToSignal(signal, evidence, CreatedAt);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task PressRelease_WinsContract_StillYieldsCustomerWin_NeverMediaAttention()
    {
        // Regression: the same directional headline on a first-party PressRelease keeps its CustomerWin
        // signal and never produces a MediaAttention signal (the news branch is source-type gated).
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithTitle("Acme wins contract with a major retailer")
            .WithRawText("Acme wins contract with a major retailer this quarter.")
            .WithCollectedAtUtc(CollectedAt)
            .Build();

        var output = await ExtractAsync(evidence);

        Assert.Contains(output.Signals, s => s.SignalType == SignalType.CustomerWin.ToString());
        Assert.DoesNotContain(output.Signals, s => s.SignalType == SignalType.MediaAttention.ToString());
    }

    [Fact]
    public async Task NewsArticle_Determinism_TwoCalls_YieldEqualSequences()
    {
        var evidence = MakeNewsEvidence(
            title: "Acme secures largest production order to date, analysts say");

        var first = await ExtractAsync(evidence);
        var second = await ExtractAsync(evidence);

        var firstKeys = first.Signals
            .Select(s => (s.SignalType, s.Direction, s.Strength, s.Novelty, s.Confidence, s.SupportingExcerpt))
            .ToList();
        var secondKeys = second.Signals
            .Select(s => (s.SignalType, s.Direction, s.Strength, s.Novelty, s.Confidence, s.SupportingExcerpt))
            .ToList();

        Assert.Equal(firstKeys, secondKeys);
    }

    [Fact]
    public async Task NullEvidence_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new KeywordSignalExtractor(NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights())
                .ExtractAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        var evidence = MakeEvidence("Acme signs a multi-year deal.");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new KeywordSignalExtractor(NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights())
                .ExtractAsync(evidence, cts.Token));
    }
}
