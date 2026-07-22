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
        IAnalyzedFilingCache? cache = null,
        IFilingReadDebugSink? debugSink = null) =>
        new(
            reader,
            analyzer,
            cache ?? new FakeAnalyzedFilingCache(),
            options ?? new DirectionalFilingSignalOptions(),
            NullLogger<DirectionalFilingSignalSource>.Instance,
            debugSink);

    /// <summary>
    /// Pads <paramref name="lead"/> past the source's minimum-plausible-body guard (spec 114) so tests that
    /// exercise the analyzer/cache path are not diverted into the short-body non-authoritative path.
    /// </summary>
    private static string PlausibleBody(string lead) =>
        lead + " " + string.Concat(Enumerable.Repeat(
            "Full results of operations, margin detail and cash-flow discussion follow in the release body. ", 4));

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
        // Fixed field order (AD-3): str, nov, minconf, then the spec-119 model identity LAST. An unsupplied
        // model identity hashes as an empty model= field rather than omitting the field, so the grammar is
        // constant.
        Assert.Equal(
            "directional-filing:str=8;nov=6;minconf=0.6;model=",
            ScoringDescriptorFor(new DirectionalFilingSignalOptions()));

        Assert.Equal(
            "directional-filing:str=9;nov=4;minconf=0.75;model=openai:deepseek-ai/DeepSeek-V4-Flash",
            ScoringDescriptorFor(new DirectionalFilingSignalOptions
            {
                Strength = 9,
                Novelty = 4,
                MinConfidence = 0.75m,
                ModelIdentity = "openai:deepseek-ai/DeepSeek-V4-Flash",
            }));
    }

    [Fact]
    public void ScoringDescriptor_ChangesWhenModelIdentityChanges()
    {
        // Spec 119: the earnings-read model is a scoring-fingerprint input BY VALUE — it changes signal
        // DIRECTION, so swapping the model must re-stamp the descriptor (and hence ScoringConfigVersion).
        Assert.NotEqual(
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { ModelIdentity = "ollama:llama3.1" }),
            ScoringDescriptorFor(new DirectionalFilingSignalOptions
            {
                ModelIdentity = "openai:deepseek-ai/DeepSeek-V4-Flash",
            }));
    }

    [Fact]
    public void ScoringDescriptor_TrimsModelIdentity_AndEscapesReservedDelimiters()
    {
        // Surrounding whitespace is not an identity difference, so it is trimmed before hashing.
        Assert.Equal(
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { ModelIdentity = "ollama:llama3.1" }),
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { ModelIdentity = "  ollama:llama3.1  " }));

        // A reserved delimiter inside the identity is percent-escaped so it cannot forge an extra descriptor
        // field (injectivity, AD-3).
        Assert.Equal(
            "directional-filing:str=8;nov=6;minconf=0.6;model=a%3Db%3Bc%2Cd%25e",
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { ModelIdentity = "a=b;c,d%e" }));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue rose 40% and the company raised guidance."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue declined and guidance was cut."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue rose 40% and the company raised guidance."), "EX-99.1", "ex991.htm"));
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
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
        Assert.Equal(AnalyzedFilingRecord.CurrentCacheVersion, entry.CacheVersion);

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

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    [InlineData("Too short to be a real earnings release.")]
    public async Task SuccessWithEmptyOrShortBody_IsNotAnalyzedOrCached_AndRetriedNextRun(string body)
    {
        // Spec 114: a structurally-successful read whose fetched EX-99.1 body is empty/implausibly short is a
        // NON-authoritative read — never analyzed, never cached (caching it would freeze in a false no-signal
        // forever, the 2026-07-18 block-era poison), so a later healthy run re-attempts the filing.
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Would be improving."));
        var cache = new FakeAnalyzedFilingCache();
        var source = CreateSource(reader, analyzer, cache: cache);

        var first = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(first);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(0, analyzer.AnalyzeCount); // the degenerate body never reaches the AI.
        Assert.Empty(cache.Entries);            // and nothing is cached.

        // A later run re-attempts the same filing (nothing was frozen in).
        var second = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        Assert.Empty(second);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task ShortBodyRead_DoesNotFeedBreaker_AndResetsConsecutiveCount()
    {
        var candidates = Enumerable.Range(1, 4)
            .Select(n => EarningsFiling(
                accession: $"0001049521-26-00000{n}",
                publishedAt: new DateTimeOffset(2026, 6, 10 - n, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();

        // 429, then a short-body SUCCESS, then 429, then a real success. A short-body read is a non-429 outcome:
        // it must reset the consecutive-429 counter (it is not a rate limit), so with breaker 2 the two 429s are
        // NOT consecutive, the breaker must not trip, and the final filing still produces its signal. Only the
        // final (authoritative) read is cached.
        var reader = new FakeSecEarningsReleaseReader(
        [
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"),
            SecEarningsReleaseReadResult.Success("Tiny.", "EX-99.1", "ex991.htm"),
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"),
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"),
        ]);
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 2 };
        var cache = new FakeAnalyzedFilingCache();

        var result = await CreateSource(reader, analyzer, options, cache)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Single(result); // the final successful filing produced its signal.
        Assert.Equal(4, reader.ReadCount); // breaker never tripped: all four candidates attempted.
        var entry = Assert.Single(cache.Entries.Values); // only the authoritative read was cached.
        Assert.Equal("0001049521-26-000004", entry.Accession);
        Assert.Equal(AnalyzedFilingOutcome.DirectionalSignalProduced, entry.Outcome);
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"),
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
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"),
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

    // ---- spec 126: cap applies to NEW analyses (pass 2) only; all in-window cached signals replay (pass 1) ----

    /// <summary>
    /// Builds a cached <see cref="AnalyzedFilingRecord"/> for <paramref name="accession"/> carrying a
    /// directional (Positive GuidanceChange) signal, so a pass-1 cache hit replays a real signal.
    /// </summary>
    private static AnalyzedFilingRecord CachedSignalRecord(string accession) =>
        new(
            accession,
            AnalyzedFilingOutcome.DirectionalSignalProduced,
            new ExtractedSignal(
                CompanyMention: "Cached Co",
                SignalType: "GuidanceChange",
                Direction: "Positive",
                Strength: 8,
                Novelty: 6,
                Confidence: 0.9m,
                SupportingExcerpt: "cached excerpt",
                Reason: "cached rationale"),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            AnalyzedFilingRecord.CurrentCacheVersion);

    [Fact]
    public async Task CacheHits_DoNotConsumeTheCap_AllReplay_AndOnlyMissesCountAgainstCap()
    {
        // Three already-analyzed (cached) filings + two uncached, with the cap (1) set BELOW the cached count.
        // Pass 1 replays all three cached signals unbounded; pass 2 attempts only min(K=2, cap=1)=1 NEW read.
        // If cache hits consumed cap slots (the pre-spec-126 defect), zero new reads would happen.
        var cachedAccessions = new[]
        {
            "0001049521-26-000101",
            "0001049521-26-000102",
            "0001049521-26-000103",
        };
        var cached = cachedAccessions
            .Select((a, idx) => EarningsFiling(
                accession: a,
                publishedAt: new DateTimeOffset(2026, 6, 20 - idx, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();
        var uncached = new[]
        {
            EarningsFiling(accession: "0001049521-26-000201", publishedAt: new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000202", publishedAt: new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero)),
        };

        var cache = new FakeAnalyzedFilingCache();
        foreach (var a in cachedAccessions)
        {
            cache.Entries[a] = CachedSignalRecord(a);
        }

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxFilingsPerRun = 1 };

        var result = await CreateSource(reader, analyzer, options, cache)
            .ProduceAsync([.. cached, .. uncached], AsOf, CancellationToken.None);

        // min(K, cap) = 1 new read only — cache hits did not consume the cap.
        Assert.Equal(1, reader.ReadCount);
        // Three replayed cached signals + one newly-analyzed = four.
        Assert.Equal(4, result.Count);
        foreach (var ev in cached)
        {
            Assert.Contains(result, r => ReferenceEquals(r.Evidence, ev));
        }
    }

    [Fact]
    public async Task AllInWindowCachedSignals_Replay_NoNewestNTruncation()
    {
        // More cached DirectionalSignalProduced filings than MaxFilingsPerRun, and zero uncached: every cached
        // signal replays and the reader is never touched (no newest-N truncation of scoring contribution).
        var cachedAccessions = new[]
        {
            "0001049521-26-000101",
            "0001049521-26-000102",
            "0001049521-26-000103",
            "0001049521-26-000104",
        };
        var cached = cachedAccessions
            .Select((a, idx) => EarningsFiling(
                accession: a,
                publishedAt: new DateTimeOffset(2026, 6, 20 - idx, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();

        var cache = new FakeAnalyzedFilingCache();
        foreach (var a in cachedAccessions)
        {
            cache.Entries[a] = CachedSignalRecord(a);
        }

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxFilingsPerRun = 2 };

        var result = await CreateSource(reader, analyzer, options, cache)
            .ProduceAsync(cached, AsOf, CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, analyzer.AnalyzeCount);
        foreach (var ev in cached)
        {
            Assert.Contains(result, r => ReferenceEquals(r.Evidence, ev));
        }
    }

    [Fact]
    public async Task NewAnalysisCap_Enforced_NewestFirst_RemainderNotOutputOrCached()
    {
        // Empty cache, four uncached earnings filings, cap 2: exactly the two NEWEST are analyzed newest-first;
        // the un-analyzed remainder is neither emitted nor written to the cache (left for a later run).
        var candidates = new[]
        {
            EarningsFiling(accession: "0001049521-26-000001", publishedAt: new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000002", publishedAt: new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000003", publishedAt: new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000004", publishedAt: new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
        };

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxFilingsPerRun = 2 };
        var cache = new FakeAnalyzedFilingCache();

        var result = await CreateSource(reader, analyzer, options, cache)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Equal(2, reader.ReadCount);
        Assert.Equal(2, analyzer.AnalyzeCount);
        Assert.Equal(2, result.Count);
        Assert.Equal(
            new[] { "0001049521-26-000001", "0001049521-26-000002" },
            reader.Calls.Select(c => c.Accession).ToArray());

        // Only the two analyzed filings are cached; the remainder was never fetched, analyzed, or cached.
        Assert.Equal(2, cache.Entries.Count);
        Assert.Contains("0001049521-26-000001", cache.Entries.Keys);
        Assert.Contains("0001049521-26-000002", cache.Entries.Keys);
        Assert.DoesNotContain("0001049521-26-000003", cache.Entries.Keys);
        Assert.DoesNotContain("0001049521-26-000004", cache.Entries.Keys);
    }

    [Fact]
    public async Task Breaker_TripsInPass2_ButPass1CacheHitsStillReplay()
    {
        // Two cached directional signals + three uncached filings whose reads all return consecutive 429s, with
        // the breaker set to 2. Pass 1 replays both cached signals; pass 2 stops after two consecutive 429s.
        // The tripped breaker no longer drops the cached replays (the pre-spec-126 single loop would have).
        var cachedAccessions = new[] { "0001049521-26-000101", "0001049521-26-000102" };
        var cached = cachedAccessions
            .Select((a, idx) => EarningsFiling(
                accession: a,
                publishedAt: new DateTimeOffset(2026, 6, 20 - idx, 0, 0, 0, TimeSpan.Zero)))
            .ToArray();
        var uncached = new[]
        {
            EarningsFiling(accession: "0001049521-26-000201", publishedAt: new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000202", publishedAt: new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000203", publishedAt: new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero)),
        };

        var cache = new FakeAnalyzedFilingCache();
        foreach (var a in cachedAccessions)
        {
            cache.Entries[a] = CachedSignalRecord(a);
        }

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.RateLimited, "429"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxConsecutiveRateLimited = 2, MaxFilingsPerRun = 50 };

        var result = await CreateSource(reader, analyzer, options, cache)
            .ProduceAsync([.. cached, .. uncached], AsOf, CancellationToken.None);

        // Breaker tripped after two consecutive 429s in pass 2.
        Assert.Equal(2, reader.ReadCount);
        // Both pass-1 cache hits still replay despite the tripped breaker.
        Assert.Equal(2, result.Count);
        foreach (var ev in cached)
        {
            Assert.Contains(result, r => ReferenceEquals(r.Evidence, ev));
        }
    }

    [Fact]
    public async Task RegressionParity_EmptyCache_CapAtLeastEligible_AnalyzesEachOnce()
    {
        // With an empty cache and MaxFilingsPerRun >= eligible count, the two-pass structure reproduces the
        // pre-spec-126 behaviour exactly: every eligible filing is analyzed once and produces its signal.
        var candidates = new[]
        {
            EarningsFiling(accession: "0001049521-26-000001", publishedAt: new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000002", publishedAt: new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000003", publishedAt: new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero)),
        };

        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var options = new DirectionalFilingSignalOptions { MaxFilingsPerRun = 5 };

        var result = await CreateSource(reader, analyzer, options)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Equal(3, reader.ReadCount);
        Assert.Equal(3, analyzer.AnalyzeCount);
        Assert.Equal(3, result.Count);
    }

    // ---- spec 115: opt-in filing-read debug sink ----------------------------------------------------------

    [Fact]
    public async Task DebugSink_SignalProduced_WritesOneDirectionalSignalProducedRecord()
    {
        var evidence = EarningsFiling();
        var body = PlausibleBody("Revenue rose 40% and the company raised guidance.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Revenue rose 40%; guidance raised."));
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, debugSink: sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Single(result); // the signal itself is unchanged by the sink.
        var record = Assert.Single(sink.Records);
        Assert.Equal(FilingReadOutcome.DirectionalSignalProduced, record.Outcome);
        Assert.Equal("0001049521-26-000011", record.Accession);
        Assert.Equal(evidence.Id, record.EvidenceId);
        Assert.Equal("Improving", record.Direction); // the FilingDirection name, not the signal's Positive.
        Assert.Equal(0.9m, record.Confidence);
        Assert.Equal("Revenue rose 40%; guidance raised.", record.Rationale);
        Assert.Equal(body.Trim().Length, record.InputLength);
        Assert.StartsWith("Revenue rose 40%", record.InputHead, StringComparison.Ordinal);
        Assert.Equal(AsOf, record.AsOfUtc); // the pipeline's asOfUtc, never wall clock.
    }

    [Fact]
    public async Task DebugSink_BelowConfidence_WritesOneBelowConfidenceRecord()
    {
        var evidence = EarningsFiling();
        var body = PlausibleBody("Some earnings text.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.5m, "Weakly improving."));
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, debugSink: sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        var record = Assert.Single(sink.Records);
        Assert.Equal(FilingReadOutcome.BelowConfidence, record.Outcome);
        Assert.Equal(evidence.Id, record.EvidenceId);
        Assert.Equal("Improving", record.Direction);
        Assert.Equal(0.5m, record.Confidence);
        Assert.Equal("Weakly improving.", record.Rationale);
        Assert.Equal(body.Trim().Length, record.InputLength);
        Assert.Equal(AsOf, record.AsOfUtc);
    }

    [Theory]
    [InlineData(FilingDirection.Mixed)]
    [InlineData(FilingDirection.Unknown)]
    public async Task DebugSink_MixedOrUnknown_WritesOneNoDirectionalReadRecord(FilingDirection direction)
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(direction, 0.95m, "Both up and down."));
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, debugSink: sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        var record = Assert.Single(sink.Records);
        Assert.Equal(FilingReadOutcome.NoDirectionalRead, record.Outcome);
        Assert.Equal(direction.ToString(), record.Direction);
        Assert.Equal(0.95m, record.Confidence);
        Assert.Equal("Both up and down.", record.Rationale);
        Assert.Equal(AsOf, record.AsOfUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Too short to be a real earnings release.")]
    public async Task DebugSink_EmptyOrShortBody_WritesOneEmptyBodySkippedRecord_WithNullVerdictFields(string body)
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Would be improving."));
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, debugSink: sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, analyzer.AnalyzeCount); // no model call happened...
        var record = Assert.Single(sink.Records); // ...but the ATTEMPT is still recorded.
        Assert.Equal(FilingReadOutcome.EmptyBodySkipped, record.Outcome);
        Assert.Equal(evidence.Id, record.EvidenceId);
        Assert.Null(record.Direction);
        Assert.Null(record.Confidence);
        Assert.Null(record.Rationale);
        Assert.Equal(body.Trim().Length, record.InputLength);
        Assert.Equal(AsOf, record.AsOfUtc);
    }

    [Fact]
    public async Task DebugSink_InputHead_IsBoundedTo2000Chars()
    {
        // A 5000-char body: the record carries the FULL trimmed length but only a 2000-char head (a diagnostic
        // bound — deliberately not a scoring input).
        var body = new string('x', 5000);
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Mixed, 0.95m, "Mixed."));
        var sink = new SpyFilingReadDebugSink();

        await CreateSource(reader, analyzer, debugSink: sink).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.Equal(5000, record.InputLength);
        Assert.Equal(2000, record.InputHead.Length);
    }

    [Fact]
    public async Task DebugSink_CacheHit_EmitsNoRecord()
    {
        // A cache hit is a replay, not an analysis attempt — only the first (analyzing) run records.
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue rose 40%."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Revenue rose 40%."));
        var cache = new FakeAnalyzedFilingCache();
        var sink = new SpyFilingReadDebugSink();
        var source = CreateSource(reader, analyzer, cache: cache, debugSink: sink);

        await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        Assert.Single(sink.Records);

        var second = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        Assert.Single(second);        // the cache-hit replay still produces the signal...
        Assert.Single(sink.Records);  // ...but emits no second record.
    }

    [Theory]
    [InlineData("NoEarningsExhibit")]
    [InlineData("Unreachable")]
    [InlineData("RateLimited")]
    public async Task DebugSink_FetchFailure_EmitsNoRecord(string outcomeName)
    {
        // A fetch failure never reached analysis — no record (the filing is re-attempted next run anyway).
        var outcome = Enum.Parse<SecEarningsReleaseReadOutcome>(outcomeName);
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Failure(outcome, "reader failed"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Would be improving."));
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, debugSink: sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task DebugSink_ThrowingSink_DoesNotAbortBatch_OrChangeProducedSignals()
    {
        // Even a sink that throws on EVERY call must not abort the batch or change the signal set: both
        // filings still produce their signals and both are cached (the diagnostic is strictly best-effort).
        var candidates = new[]
        {
            EarningsFiling(accession: "0001049521-26-000001", publishedAt: new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero)),
            EarningsFiling(accession: "0001049521-26-000002", publishedAt: new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero)),
        };
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));
        var cache = new FakeAnalyzedFilingCache();
        var sink = new ThrowingFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, cache: cache, debugSink: sink)
            .ProduceAsync(candidates, AsOf, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, sink.Calls); // the sink WAS attempted for each analysis...
        Assert.Equal(2, cache.Entries.Count); // ...and neither the signals nor the caching changed.
    }

    [Fact]
    public async Task NullDebugSink_Default_BehaviourUnchanged()
    {
        // The default (feature off) is a null sink: the entire pre-spec-115 suite runs this way; this pins the
        // default explicitly — signal produced, no throw, nothing extra.
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Revenue up, guidance raised."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Improving."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Single(result);
    }

    private sealed class SpyFilingReadDebugSink : IFilingReadDebugSink
    {
        public List<FilingReadDebugRecord> Records { get; } = [];

        public Task RecordAsync(FilingReadDebugRecord record, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFilingReadDebugSink : IFilingReadDebugSink
    {
        public int Calls { get; private set; }

        public Task RecordAsync(FilingReadDebugRecord record, CancellationToken ct)
        {
            Calls++;
            throw new IOException("debug sink disk full");
        }
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
