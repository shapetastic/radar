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
        ISecEarningsReleaseReader reader,
        IFilingAnalyzer analyzer,
        DirectionalFilingSignalOptions? options = null,
        IAnalyzedFilingCache? cache = null) =>
        new(
            reader,
            analyzer,
            cache ?? new FakeAnalyzedFilingCache(),
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

    // A descriptor-only source: the reader/analyzer are never touched by ScoringDescriptor(), so any fakes do.
    private static string ScoringDescriptorFor(DirectionalFilingSignalOptions options) =>
        CreateSource(
            new FakeSecEarningsReleaseReader(
                SecEarningsReleaseReadResult.Success("body", "EX-99.1", "ex991.htm")),
            new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "rationale")),
            options).ScoringDescriptor();

    [Fact]
    public void ScoringDescriptor_EncodesPerSignalMagnitudes_InCanonicalForm()
    {
        Assert.Equal(
            "directional-filing:str=8;nov=6;minconf=0.6",
            ScoringDescriptorFor(new DirectionalFilingSignalOptions()));

        Assert.Equal(
            "directional-filing:str=9;nov=4;minconf=0.75",
            ScoringDescriptorFor(new DirectionalFilingSignalOptions
            {
                Strength = 9,
                Novelty = 4,
                MinConfidence = 0.75m,
            }));
    }

    [Fact]
    public void ScoringDescriptor_ExcludesMaxFilingsPerRun()
    {
        // MaxFilingsPerRun is an operational cost cap, not a per-signal magnitude — changing it must NOT change
        // the descriptor (so tuning it does not falsely re-stamp otherwise-comparable runs).
        Assert.Equal(
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { MaxFilingsPerRun = 5 }),
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { MaxFilingsPerRun = 50 }));
    }

    [Fact]
    public void ScoringDescriptor_ExcludesMaxConsecutiveRateLimited()
    {
        // The per-run 429 circuit breaker is operational scaffolding — changing it must NOT change the descriptor.
        Assert.Equal(
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 2 }),
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 0 }));
    }

    [Fact]
    public void ScoringDescriptor_ChangesWhenStrengthChanges()
    {
        // A per-signal magnitude is folded by value — a Strength change must re-stamp the descriptor.
        Assert.NotEqual(
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { Strength = 6 }),
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { Strength = 9 }));
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
        // Spec 112: the confident directional read carries the recalibrated default Strength 8 (exceeds the
        // keyword max of 6) so it can materially move the thesis.
        Assert.Equal(8, produced.Signal.Strength);
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
        // Symmetry proof (spec 112): a confident deteriorating read carries the SAME recalibrated Strength 8
        // as the improving read above — a confident guidance cut bites as hard as a raise lifts.
        Assert.Equal(8, produced.Signal.Strength);
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

    [Fact]
    public async Task CacheHit_ReplaysFieldIdenticalSignal_WithNoSecondFetchOrAi()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Revenue rose 40% and the company raised guidance.", "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Revenue rose 40%; guidance raised."));
        var cache = new FakeAnalyzedFilingCache();
        var source = CreateSource(reader, analyzer, cache: cache);

        // First run: fetch + analyze, produce, and populate the cache.
        var first = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        var firstProduced = Assert.Single(first);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, analyzer.AnalyzeCount);
        Assert.Single(cache.Entries);

        // Second run on the SAME source + cache: a cache hit replays a field-identical signal with no further
        // fetch or AI call.
        var second = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        var secondProduced = Assert.Single(second);

        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, analyzer.AnalyzeCount);

        Assert.Same(evidence, secondProduced.Evidence);
        Assert.Equal(firstProduced.Signal.SignalType, secondProduced.Signal.SignalType);
        Assert.Equal(firstProduced.Signal.Direction, secondProduced.Signal.Direction);
        Assert.Equal(firstProduced.Signal.Strength, secondProduced.Signal.Strength);
        Assert.Equal(firstProduced.Signal.Novelty, secondProduced.Signal.Novelty);
        Assert.Equal(firstProduced.Signal.Confidence, secondProduced.Signal.Confidence);
        Assert.Equal(firstProduced.Signal.SupportingExcerpt, secondProduced.Signal.SupportingExcerpt);
        Assert.Equal(firstProduced.Signal.Reason, secondProduced.Signal.Reason);
        Assert.Equal(firstProduced.Signal.CompanyMention, secondProduced.Signal.CompanyMention);
    }

    [Fact]
    public async Task SuccessfulReadWithNoSignal_IsCachedAsNoSignal_AndNotRefetched()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success("Some earnings text.", "EX-99.1", "ex991.htm"));
        // Mixed -> a successful read but no directional signal; must be cached as NoDirectionalSignal.
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Mixed, 0.95m, "Both up and down."));
        var cache = new FakeAnalyzedFilingCache();
        var source = CreateSource(reader, analyzer, cache: cache);

        var first = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        Assert.Empty(first);
        Assert.Equal(1, reader.ReadCount);
        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, entry.Outcome);
        Assert.Null(entry.Signal);

        // Second run: the no-signal cache hit means the reader is NOT called again and nothing is emitted.
        var second = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        Assert.Empty(second);
        Assert.Equal(1, reader.ReadCount);
    }

    [Theory]
    [InlineData("RateLimited")]
    [InlineData("Unreachable")]
    public async Task FailedRead_IsNotCached_AndIsRetriedNextRun(string outcomeName)
    {
        var outcome = Enum.Parse<SecEarningsReleaseReadOutcome>(outcomeName);
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Failure(outcome, "reader failed"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Would be improving."));
        // Disable the breaker so RateLimited alone does not stop the (single-candidate) run.
        var options = new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 0 };
        var cache = new FakeAnalyzedFilingCache();
        var source = CreateSource(reader, analyzer, options, cache);

        var first = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        Assert.Empty(first);
        Assert.Equal(1, reader.ReadCount);
        Assert.Empty(cache.Entries); // a failed read is never cached.

        // Second run retries the same filing (cache still empty).
        var second = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        Assert.Empty(second);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task CircuitBreaker_StopsAfterConsecutiveRateLimits()
    {
        var candidates = Enumerable.Range(1, 5)
            .Select(n => EarningsFiling(
                accession: $"0001049521-26-00000{n}",
                publishedAt: new DateTimeOffset(2026, 6, 10 - n, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "n/a"));
        var options = new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 2 };

        var result = await CreateSource(reader, analyzer, options)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(2, reader.ReadCount); // stopped after 2 consecutive 429s.
    }

    [Fact]
    public async Task CircuitBreaker_SuccessResetsConsecutiveCount()
    {
        var candidates = Enumerable.Range(1, 3)
            .Select(n => EarningsFiling(
                accession: $"0001049521-26-00000{n}",
                publishedAt: new DateTimeOffset(2026, 6, 10 - n, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();

        // First filing succeeds (resets the counter), then the next two 429 -> with breaker 2 they still trip.
        var reader = new FakeSecEarningsReleaseReader(
        [
            SecEarningsReleaseReadResult.Success("Revenue up, guidance raised.", "EX-99.1", "ex991.htm"),
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"),
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"),
        ]);
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 2 };

        var result = await CreateSource(reader, analyzer, options)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Single(result); // the first (successful) filing produced a signal.
        Assert.Equal(3, reader.ReadCount); // success + two 429s (which then trip the breaker).
    }

    [Fact]
    public async Task CircuitBreaker_NonRateLimitedFailure_ResetsConsecutiveCount()
    {
        var candidates = Enumerable.Range(1, 4)
            .Select(n => EarningsFiling(
                accession: $"0001049521-26-00000{n}",
                publishedAt: new DateTimeOffset(2026, 6, 10 - n, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();

        // 429, then a NON-429 failure, then 429, then a success. With breaker 2 the two 429s are NOT consecutive
        // (the Unreachable read between them resets the counter), so the breaker must not trip and every
        // candidate is attempted — the final success still produces its signal.
        var reader = new FakeSecEarningsReleaseReader(
        [
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"),
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.Unreachable, "boom"),
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"),
            SecEarningsReleaseReadResult.Success("Revenue up, guidance raised.", "EX-99.1", "ex991.htm"),
        ]);
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 2 };

        var result = await CreateSource(reader, analyzer, options)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Single(result); // the final successful filing produced a signal.
        Assert.Equal(4, reader.ReadCount); // breaker never tripped: all four candidates attempted.
    }

    [Fact]
    public async Task CircuitBreaker_Disabled_AttemptsAllCandidates()
    {
        var candidates = Enumerable.Range(1, 5)
            .Select(n => EarningsFiling(
                accession: $"0001049521-26-00000{n}",
                publishedAt: new DateTimeOffset(2026, 6, 10 - n, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "n/a"));
        var options = new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 0 };

        var result = await CreateSource(reader, analyzer, options)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(5, reader.ReadCount); // breaker disabled -> every candidate attempted.
    }

    private sealed class FakeSecEarningsReleaseReader : ISecEarningsReleaseReader
    {
        private readonly Queue<SecEarningsReleaseReadResult> _scripted;
        private readonly SecEarningsReleaseReadResult? _constant;

        public FakeSecEarningsReleaseReader(SecEarningsReleaseReadResult result)
        {
            _constant = result;
            _scripted = new Queue<SecEarningsReleaseReadResult>();
        }

        public FakeSecEarningsReleaseReader(IEnumerable<SecEarningsReleaseReadResult> scripted)
        {
            _constant = null;
            _scripted = new Queue<SecEarningsReleaseReadResult>(scripted);
        }

        public int ReadCount { get; private set; }

        public List<(string Cik, string Accession)> Calls { get; } = [];

        public Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            Calls.Add((cik, accession));
            var result = _constant ?? _scripted.Dequeue();
            return Task.FromResult(result);
        }
    }

    /// <summary>In-memory <see cref="IAnalyzedFilingCache"/> keyed by accession for the cache-behaviour tests.</summary>
    private sealed class FakeAnalyzedFilingCache : IAnalyzedFilingCache
    {
        public Dictionary<string, AnalyzedFilingRecord> Entries { get; } = new(StringComparer.Ordinal);

        public Task<AnalyzedFilingRecord?> TryGetAsync(string accession, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Entries.TryGetValue(accession, out var record) ? record : null);
        }

        public Task PutAsync(AnalyzedFilingRecord record, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Entries[record.Accession] = record;
            return Task.CompletedTask;
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
