using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Filings;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Filings;

public sealed class DirectionalFilingSignalSourceTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private static DirectionalFilingSignalSource CreateSource(
        FakeSecEarningsReleaseReader reader,
        FakeFilingAnalyzer analyzer,
        DirectionalFilingSignalOptions? options = null) =>
        new(
            reader,
            analyzer,
            options ?? new DirectionalFilingSignalOptions(),
            NullLogger<DirectionalFilingSignalSource>.Instance);

    /// <summary>
    /// Builds an earnings-8-K <see cref="EvidenceItem"/> with a real index SourceUrl (carrying CIK +
    /// dashed accession), an item list containing 2.02, and MetadataJson shaped like the collector's.
    /// </summary>
    private static EvidenceItem EarningsFiling(
        string sourceName = "Mercury — SEC",
        string cikInUrl = "0001049521",
        string accession = "0001049521-26-000011",
        string form = "8-K",
        string? items = "2.02,9.01",
        string? titleItems = "2.02,9.01",
        bool includeItemsMetadata = true,
        bool includeAccessionMetadata = true,
        DateTimeOffset? publishedAt = null)
    {
        var accNoDashes = accession.Replace("-", string.Empty, StringComparison.Ordinal);
        var sourceUrl =
            $"https://www.sec.gov/Archives/edgar/data/{cikInUrl}/{accNoDashes}/{accession}-index.htm";

        var title = titleItems is null
            ? $"{form} — Report (2026-06-02)"
            : $"{form} — Report (2026-06-02) [items: {titleItems}] Items: Results of Operations and Financial Condition.";

        var rawText = $"{form} filing accession {accession} filed 2026-06-02: Report.";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["quality"] = "High",
            ["form"] = form,
        };
        if (includeAccessionMetadata)
        {
            metadata["accessionNumber"] = accession;
        }

        if (includeItemsMetadata && items is not null)
        {
            metadata["items"] = items;
        }

        var metadataJson = JsonSerializer.Serialize(
            new { metadata, companyHints = Array.Empty<string>() });

        return new EvidenceItem(
            Id: Guid.NewGuid(),
            SourceType: EvidenceSourceType.Filing,
            SourceName: sourceName,
            SourceUrl: sourceUrl,
            Title: title,
            Summary: null,
            RawText: rawText,
            ContentHash: Guid.NewGuid().ToString("N"),
            PublishedAtUtc: publishedAt ?? new DateTimeOffset(2026, 6, 2, 16, 30, 0, TimeSpan.Zero),
            CollectedAtUtc: AsOf,
            Quality: EvidenceQuality.High,
            MetadataJson: metadataJson);
    }

    [Fact]
    public async Task Improving_HighConfidence_ProducesOnePositiveGuidanceChange()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue rose 40% and the company raised guidance.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Revenue rose 40%; guidance raised."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Same(evidence, produced.Evidence);
        Assert.Equal("GuidanceChange", produced.Signal.SignalType);
        Assert.Equal("Positive", produced.Signal.Direction);
        Assert.Equal(0.9m, produced.Signal.Confidence);
        Assert.Equal("Revenue rose 40%; guidance raised.", produced.Signal.Reason);

        // The signal round-trips valid through the mapper: excerpt is a verbatim slice of the evidence and
        // the rationale rides Reason (provenance preserved).
        var mapping = ExtractedSignalMapper.ToSignal(produced.Signal, evidence, AsOf);
        Assert.True(mapping.IsValid, string.Join("; ", mapping.Errors));
        Assert.Equal(evidence.Id, mapping.Signal!.EvidenceId);
        Assert.Contains("Revenue rose 40%", mapping.Signal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deteriorating_HighConfidence_ProducesOneNegativeGuidanceChange()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue declined and guidance was cut.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Deteriorating, 0.85m, "Revenue declined; guidance cut."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal("GuidanceChange", produced.Signal.SignalType);
        Assert.Equal("Negative", produced.Signal.Direction);
        Assert.Equal(0.85m, produced.Signal.Confidence);

        var mapping = ExtractedSignalMapper.ToSignal(produced.Signal, evidence, AsOf);
        Assert.True(mapping.IsValid, string.Join("; ", mapping.Errors));
    }

    [Fact]
    public async Task BelowMinConfidence_ProducesNoSignal()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Some earnings text.", "EX-99.1", "ex991.htm"));
        // Improving but confidence below the default 0.6 gate.
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.5m, "Weakly improving."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        // The read + analysis both ran; only the signal is withheld.
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, analyzer.AnalyzeCount);
    }

    [Theory]
    [InlineData(FilingDirection.Mixed)]
    [InlineData(FilingDirection.Unknown)]
    public async Task MixedOrUnknown_ProducesNoSignal_RegardlessOfConfidence(FilingDirection direction)
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Some earnings text.", "EX-99.1", "ex991.htm"));
        // High confidence, but a non-directional read must never emit a signal.
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(direction, 0.95m, "Both up and down."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("NoEarningsExhibit")]
    [InlineData("Forbidden")]
    [InlineData("Timeout")]
    public async Task ReaderFailure_ProducesNoSignal_AndDoesNotCallAnalyzer(string outcomeName)
    {
        var outcome = Enum.Parse<SecEarningsReleaseReadOutcome>(outcomeName);
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Failure(outcome, "reader failed"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Would be improving."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(0, analyzer.AnalyzeCount);
    }

    [Fact]
    public async Task AnalyzerUnknown_ProducesNoSignal()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Some earnings text.", "EX-99.1", "ex991.htm"));
        // Spec 74 degrades a malformed/failed AI response to FilingSentiment.Unknown (never throws).
        var analyzer = new FakeFilingAnalyzer(FilingSentiment.Unknown);

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task NonEarningsFiling_IsNotFetched_ButEarningsFilingIs()
    {
        // A form 8-K WITHOUT item 2.02 (only 9.01) is not an earnings 8-K, so it is never read.
        var nonEarnings = EarningsFiling(
            sourceName: "NonEarnings — SEC",
            accession: "0001049521-26-000099",
            items: "9.01",
            titleItems: "9.01");
        var earnings = EarningsFiling();

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue up, guidance raised.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));

        var result = await CreateSource(reader, analyzer)
            .ProduceAsync([nonEarnings, earnings], AsOf, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, reader.ReadCount);
        // Only the earnings filing's accession was read.
        var read = Assert.Single(reader.Calls);
        Assert.Equal("0001049521-26-000011", read.Accession);
    }

    [Fact]
    public async Task PerRunCap_IsHonoured()
    {
        // Four earnings-8-K candidates but a cap of 2 -> at most 2 reads/analyses.
        var candidates = new[]
        {
            EarningsFiling(accession: "0001049521-26-000001", publishedAt: new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000002", publishedAt: new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000003", publishedAt: new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000004", publishedAt: new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
        };

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue up.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxFilingsPerRun = 2 };

        var result = await CreateSource(reader, analyzer, options)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Equal(2, reader.ReadCount);
        Assert.Equal(2, analyzer.AnalyzeCount);
        Assert.Equal(2, result.Count);

        // Newest observed first: the two most-recently published filings are the ones analyzed.
        Assert.Equal(
            new[] { "0001049521-26-000001", "0001049521-26-000002" },
            reader.Calls.Select(c => c.Accession).ToArray());
    }

    [Fact]
    public async Task AlreadyCancelledToken_Throws()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("text", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, cts.Token));
    }

    [Fact]
    public async Task CikAndAccession_MatchSourceUrlValues()
    {
        // CIK carries leading zeros in the URL; the parse strips them. Accession stays dashed.
        var evidence = EarningsFiling(cikInUrl: "0001049521", accession: "0001049521-26-000011");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue up.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));

        await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.Equal("1049521", call.Cik);
        Assert.Equal("0001049521-26-000011", call.Accession);
    }

    [Fact]
    public async Task UnparseableSourceUrl_IsSkipped_NeverGuessed()
    {
        var evidence = EarningsFiling() with { SourceUrl = "https://example.com/not-an-index" };
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue up.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task ItemsGate_FallsBackToTitle_WhenNoItemsMetadataKey()
    {
        // No discrete items metadata key, but the Title carries "[items: 2.02,...]" — still gated in.
        var evidence = EarningsFiling(includeItemsMetadata: false, titleItems: "2.02,9.01");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue up, guidance raised.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, reader.ReadCount);
    }

    private sealed class FakeSecEarningsReleaseReader(SecEarningsReleaseReadResult result)
        : ISecEarningsReleaseReader
    {
        public int ReadCount { get; private set; }

        public List<(string Cik, string Accession)> Calls { get; } = [];

        public Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            Calls.Add((cik, accession));
            return Task.FromResult(result);
        }
    }

    private sealed class FakeFilingAnalyzer(FilingSentiment sentiment) : IFilingAnalyzer
    {
        public int AnalyzeCount { get; private set; }

        public Task<FilingSentiment> AnalyzeAsync(string? earningsReleaseText, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AnalyzeCount++;
            return Task.FromResult(sentiment);
        }
    }
}
