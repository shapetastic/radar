using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
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
        // Fixed field order (AD-3): str, nov, minconf, the spec-119 model identity, then the spec-160
        // comparability fields LAST (cmpscan = the scan's rule-STRUCTURE identity, cmpcap = the cap magnitude
        // by value, G29 like minconf) — new fields are always appended so the existing prefix stays
        // byte-stable. An unsupplied model identity hashes as an empty model= field rather than omitting the
        // field, so the grammar is constant.
        Assert.Equal(
            "directional-filing:str=8;nov=6;minconf=0.6;model=;cmpscan=cmpscan-v1;cmpcap=0.65",
            ScoringDescriptorFor(new DirectionalFilingSignalOptions()));

        Assert.Equal(
            "directional-filing:str=9;nov=4;minconf=0.75;model=openai:deepseek-ai/DeepSeek-V4-Flash;cmpscan=cmpscan-v1;cmpcap=0.5",
            ScoringDescriptorFor(new DirectionalFilingSignalOptions
            {
                Strength = 9,
                Novelty = 4,
                MinConfidence = 0.75m,
                ModelIdentity = "openai:deepseek-ai/DeepSeek-V4-Flash",
                ComparabilityConfidenceCap = 0.5m,
            }));
    }

    [Fact]
    public void ScoringDescriptor_ChangesWhenComparabilityCapChanges()
    {
        // Spec 160: the comparability cap bounds the confidence of emitted signals, so it is a comparability
        // input exactly like MinConfidence — two options differing only in the cap must produce different
        // descriptors (and hence different ScoringConfigVersions).
        Assert.NotEqual(
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { ComparabilityConfidenceCap = 0.65m }),
            ScoringDescriptorFor(new DirectionalFilingSignalOptions { ComparabilityConfidenceCap = 0.5m }));
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
            "directional-filing:str=8;nov=6;minconf=0.6;model=a%3Db%3Bc%2Cd%25e;cmpscan=cmpscan-v1;cmpcap=0.65",
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
    public async Task BelowMinConfidence_PersistsNeutralReadSignal_WithBelowConfidenceProvenance()
    {
        // Spec 204: a directional read below the gate is still a READ — it is persisted as a Neutral
        // GuidanceChange at the keyword-fallback magnitudes, with the model's own direction/confidence in
        // the metadata envelope and a Reason prefix naming the gate that suppressed the direction.
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        // Improving but confidence below the default 0.6 gate.
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.5m, "Weakly improving."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, analyzer.AnalyzeCount);

        Assert.Equal("GuidanceChange", produced.Signal.SignalType);
        Assert.Equal("Neutral", produced.Signal.Direction);
        // The keyword fallback's exact magnitudes — NOT the directional read's Strength 8 / Novelty 6.
        Assert.Equal(FilingReadSignalMetadata.Strength, produced.Signal.Strength);
        Assert.Equal(FilingReadSignalMetadata.Novelty, produced.Signal.Novelty);
        Assert.Equal(FilingReadSignalMetadata.Confidence, produced.Signal.Confidence);
        Assert.Equal(
            "AI earnings read: Improving 0.5 (below MinConfidence 0.6) — Weakly improving.",
            produced.Signal.Reason);

        // The provenance envelope carries the model's REAL read; the signal's Confidence stays 0.4.
        AssertReadMetadata(produced.Signal, "below-confidence", "Improving", "0.5");

        // The signal round-trips valid through the mapper (Neutral parses; the Title excerpt passes the
        // excerpt-in-evidence guard).
        var mapping = ExtractedSignalMapper.ToSignal(produced.Signal, evidence, AsOf);
        Assert.True(mapping.IsValid, string.Join("; ", mapping.Errors));
    }

    [Fact]
    public async Task ConfidentMixed_PersistsMixedReadSignal_AtKeywordMagnitudes()
    {
        // Spec 204, the headline row: a confident Mixed read is emitted as a Mixed GuidanceChange —
        // SignalDirection.Mixed scores 0 exactly like Neutral, so this is provenance, never a score move.
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Mixed, 0.95m, "Both up and down."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal("GuidanceChange", produced.Signal.SignalType);
        Assert.Equal("Mixed", produced.Signal.Direction);
        Assert.Equal(FilingReadSignalMetadata.Strength, produced.Signal.Strength);
        Assert.Equal(FilingReadSignalMetadata.Novelty, produced.Signal.Novelty);
        Assert.Equal(FilingReadSignalMetadata.Confidence, produced.Signal.Confidence);
        Assert.Equal("AI earnings read: Mixed 0.95 — Both up and down.", produced.Signal.Reason);
        AssertReadMetadata(produced.Signal, "mixed", "Mixed", "0.95");

        // A Mixed direction parses and maps like any other (the domain member existed; nothing produced it).
        var mapping = ExtractedSignalMapper.ToSignal(produced.Signal, evidence, AsOf);
        Assert.True(mapping.IsValid, string.Join("; ", mapping.Errors));
        Assert.Equal(Radar.Domain.Signals.SignalDirection.Mixed, mapping.Signal!.Direction);
    }

    [Fact]
    public async Task ConfidentUnknown_PersistsNeutralReadSignal_WithUnknownProvenance()
    {
        // Unknown at ANY confidence is a Neutral read signal whose cause is "unknown" — never
        // "below-confidence": an Unknown verdict never claimed a direction, so the gate has nothing to gate.
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Unknown, 0.95m, "Both up and down."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal("Neutral", produced.Signal.Direction);
        Assert.Equal("AI earnings read: Unknown 0.95 — Both up and down.", produced.Signal.Reason);
        AssertReadMetadata(produced.Signal, "unknown", "Unknown", "0.95");
    }

    [Fact]
    public async Task MixedBelowGate_PersistsNeutralReadSignal_AsBelowConfidence()
    {
        // A Mixed read that fails the confidence bar is not persisted as a Mixed DIRECTION: the gate saw it
        // first, so it lands on the below-confidence row (Neutral) like any other sub-gate read.
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Mixed, 0.45m, "Weakly two-sided."));

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal("Neutral", produced.Signal.Direction);
        Assert.Equal(
            "AI earnings read: Mixed 0.45 (below MinConfidence 0.6) — Weakly two-sided.",
            produced.Signal.Reason);
        AssertReadMetadata(produced.Signal, "below-confidence", "Mixed", "0.45");
    }

    /// <summary>Asserts the spec-204 provenance envelope through the SHARED reader (one parser, ever).</summary>
    private static void AssertReadMetadata(
        ExtractedSignal signal, string outcome, string direction, string confidenceG29, string model = "")
    {
        Assert.True(EvidenceMetadata.TryRead(signal.MetadataJson, out var metadata, out _));
        Assert.Equal(outcome, metadata[FilingReadSignalMetadata.OutcomeKey]);
        Assert.Equal(direction, metadata[FilingReadSignalMetadata.DirectionKey]);
        Assert.Equal(confidenceG29, metadata[FilingReadSignalMetadata.ConfidenceKey]);
        Assert.Equal(model, metadata[FilingReadSignalMetadata.ModelKey]);
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
    public async Task AnalyzerUnknown_PersistsNeutralReadSignal()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        // Spec 74 degrades a malformed/failed AI response to FilingSentiment.Unknown (never throws).
        // Spec 204: even that safe default is a READ verdict — persisted as a Neutral signal with the
        // "unknown" cause, so "the model could not read this" is distinguishable from "Radar never looked".
        var analyzer = new FakeFilingAnalyzer(FilingSentiment.Unknown);

        var result = await CreateSource(reader, analyzer).ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal("Neutral", produced.Signal.Direction);
        // FilingSentiment.Unknown carries confidence 0 and an EMPTY rationale — the prefix stands alone.
        Assert.Equal("AI earnings read: Unknown 0 — ", produced.Signal.Reason);
        AssertReadMetadata(produced.Signal, "unknown", "Unknown", "0");
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
    public async Task SuccessfulReadWithNoSignal_IsCachedAsNoSignalNamingTheCause_AndReplayIsFieldIdentical()
    {
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        // Mixed -> a successful read with no DIRECTIONAL signal; cached as NoDirectionalSignal naming the
        // cause (spec 204), while the read itself is emitted as a Mixed signal.
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Mixed, 0.95m, "Both up and down."));
        var cache = new FakeAnalyzedFilingCache();
        var source = CreateSource(reader, analyzer, cache: cache);

        var first = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        var fresh = Assert.Single(first);
        Assert.Equal(1, reader.ReadCount);
        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, entry.Outcome);
        // The ExtractedSignal is NOT stored on a no-signal record (IsConsistent keeps requiring
        // NoDirectionalSignal ⇒ Signal is null); the record carries the FACTS of the read instead.
        Assert.Null(entry.Signal);
        Assert.Equal(AnalyzedFilingRecord.CurrentCacheVersion, entry.CacheVersion);
        Assert.Equal(FilingNoSignalCause.Mixed, entry.NoSignalCause);
        Assert.Equal("Mixed", entry.ReadDirection);
        Assert.Equal(0.95m, entry.ReadConfidence);
        Assert.Equal("Both up and down.", entry.Rationale);

        // Second run: the no-signal cache hit replays the SAME read signal — field for field — with the
        // reader and analyzer untouched, reconstructed from the record through the same builder the fresh
        // path used.
        var second = await source.ProduceAsync([evidence], AsOf, CancellationToken.None);
        var replayed = Assert.Single(second);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, analyzer.AnalyzeCount);
        Assert.Equal(fresh.Signal, replayed.Signal); // record equality: every field, MetadataJson included.
        Assert.Same(evidence, replayed.Evidence);
    }

    [Fact]
    public async Task V3NoSignalCacheHit_BelowConfidenceCause_ReplaysTheReconstructedNeutralSignal()
    {
        // A PLANTED v3 no-signal record (no fresh pass in this process): the replay reconstructs the exact
        // §1 signal from the record's cause fields — the same builder the fresh path uses — with no fetch
        // and no model call.
        var accession = "0001049521-26-000011";
        var evidence = EarningsFiling(accession: accession);
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[accession] = new AnalyzedFilingRecord(
            accession,
            AnalyzedFilingOutcome.NoDirectionalSignal,
            null,
            null,
            AnalyzedFilingRecord.CurrentCacheVersion,
            CurrentDefaultPolicy,
            new ComparabilityMarkers([], []),
            FilingNoSignalCause.BelowConfidence,
            "Improving",
            0.45m,
            "Weakly improving.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("irrelevant"), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "n/a"));

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var replayed = Assert.Single(result);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, analyzer.AnalyzeCount);
        Assert.Equal("GuidanceChange", replayed.Signal.SignalType);
        Assert.Equal("Neutral", replayed.Signal.Direction);
        Assert.Equal(FilingReadSignalMetadata.Strength, replayed.Signal.Strength);
        Assert.Equal(FilingReadSignalMetadata.Novelty, replayed.Signal.Novelty);
        Assert.Equal(FilingReadSignalMetadata.Confidence, replayed.Signal.Confidence);
        Assert.Equal(
            "AI earnings read: Improving 0.45 (below MinConfidence 0.6) — Weakly improving.",
            replayed.Signal.Reason);
        AssertReadMetadata(replayed.Signal, "below-confidence", "Improving", "0.45");
    }

    [Fact]
    public async Task NoSignalCacheHit_WithNullCause_ReplaysNothing()
    {
        // Defensive: a v3-shaped record with NO cause (only reachable through a non-file cache — the file
        // cache already treats a v2 no-signal record as a version miss) replays nothing, the pre-204
        // behaviour, rather than fabricating a read that was never recorded.
        var accession = "0001049521-26-000011";
        var evidence = EarningsFiling(accession: accession);
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[accession] = CachedNoSignalRecord(accession, CurrentDefaultPolicy);
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("irrelevant"), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "n/a"));

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, reader.ReadCount); // still a HIT — no re-fetch, just no replayed signal.
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

        // Spec 204: the below-gate read now emits a Neutral read signal, but the DEBUG record is unchanged —
        // it keeps recording the model's raw verdict for the attempt.
        Assert.Single(result);
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

        // Spec 204: the Mixed/Unknown read now emits its own read signal; the DEBUG record is unchanged.
        Assert.Single(result);
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

    // ---- spec 160: comparability-aware confidence cap ------------------------------------------------------

    /// <summary>The current comparability policy string at the default cap (0.65).</summary>
    private static readonly string CurrentDefaultPolicy = EarningsComparabilityScan.Policy(0.65m);

    [Fact]
    public async Task ComparabilityCap_CassShapedFixture_CapsConfidence_NamesCapMarkers_AndCachesPolicy()
    {
        // The CASS 2026-07-29 failure class at excerpt level: a bullish headline GAAP doubling whose own body
        // declares the comparison dirty (prior-year securities loss, a bad-debt recovery that is a litigation
        // settlement payment, continuing-operations presentation). The analyzer stub reads it Positive 0.90;
        // the deterministic scan must bound the persisted confidence to the default cap 0.65.
        var evidence = EarningsFiling();
        var body = PlausibleBody(
            "Record net income of $10.6 million, up from $5.2 million a year ago. The prior-year quarter "
                + "included a $3.6 million securities loss, and the current quarter benefited from a bad debt "
                + "recovery representing the second annual payment of a litigation settlement. Results from "
                + "continuing operations improved.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Record net income doubled."));
        var cache = new FakeAnalyzedFilingCache();
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, cache: cache, debugSink: sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal(0.65m, produced.Signal.Confidence);
        // The Reason names the cap-triggering markers in scanner table order — and ONLY those: the
        // diagnostic-only "continuing operations" is recorded, never surfaced as a cap.
        Assert.Equal(
            "Record net income doubled. (comparability cap: matched 'litigation settlement', "
                + "'securities loss', 'bad debt recovery')",
            produced.Signal.Reason);
        Assert.DoesNotContain("continuing operations", produced.Signal.Reason, StringComparison.Ordinal);

        // The cache record carries the policy + both marker groups (the policy-mismatch miss rule needs them).
        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(AnalyzedFilingOutcome.DirectionalSignalProduced, entry.Outcome);
        Assert.Equal("cmpscan-v1;cap=0.65", entry.ComparabilityPolicy);
        Assert.Equal(CurrentDefaultPolicy, entry.ComparabilityPolicy);
        Assert.NotNull(entry.ComparabilityMarkers);
        Assert.Equal(
            new[] { "litigation settlement", "securities loss", "bad debt recovery" },
            entry.ComparabilityMarkers!.CapTriggering);
        Assert.Equal(new[] { "continuing operations" }, entry.ComparabilityMarkers.DiagnosticOnly);

        // The debug record preserves the model's RAW confidence and reports the capped value separately.
        var record = Assert.Single(sink.Records);
        Assert.Equal(FilingReadOutcome.DirectionalSignalProduced, record.Outcome);
        Assert.Equal(0.9m, record.Confidence);
        Assert.Equal(0.65m, record.CappedConfidence);
        Assert.NotNull(record.ComparabilityMarkers);
        Assert.Equal(
            new[] { "litigation settlement", "securities loss", "bad debt recovery" },
            record.ComparabilityMarkers!.CapTriggering);
    }

    [Fact]
    public async Task ComparabilityCap_DiagnosticOnlyMatches_DoNotCap_ButAreRecorded()
    {
        // Text whose ONLY matches are diagnostic-group phrases: the confidence is unchanged, the reason is
        // unannotated, and the matches are recorded in the diagnostic list (measurable but inert).
        var evidence = EarningsFiling();
        var body = PlausibleBody(
            "Following the sale of its legacy product line to a distributor, revenue from continuing "
                + "operations grew 12% on strong demand.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Revenue grew 12%."));
        var cache = new FakeAnalyzedFilingCache();

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal(0.9m, produced.Signal.Confidence);
        Assert.Equal("Revenue grew 12%.", produced.Signal.Reason);

        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(CurrentDefaultPolicy, entry.ComparabilityPolicy);
        Assert.NotNull(entry.ComparabilityMarkers);
        Assert.Empty(entry.ComparabilityMarkers!.CapTriggering);
        Assert.Equal(
            new[] { "continuing operations", "sale of its" },
            entry.ComparabilityMarkers.DiagnosticOnly);
    }

    [Fact]
    public async Task ComparabilityCap_CleanFixture_ConfidenceUnchanged_PolicyRecordsScannedClean()
    {
        // AGYS-shaped: no marker in the body at all. 0.90 stays 0.90 and the cache policy is NON-null with
        // both lists empty — "scanned clean" is distinct from "not scanned" (null policy, pre-160).
        var evidence = EarningsFiling();
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(
                PlausibleBody("Revenue rose 40% and the company raised guidance."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Revenue rose 40%; guidance raised."));
        var cache = new FakeAnalyzedFilingCache();
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, cache: cache, debugSink: sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal(0.9m, produced.Signal.Confidence);
        Assert.Equal("Revenue rose 40%; guidance raised.", produced.Signal.Reason);

        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(CurrentDefaultPolicy, entry.ComparabilityPolicy);
        Assert.NotNull(entry.ComparabilityMarkers);
        Assert.Empty(entry.ComparabilityMarkers!.CapTriggering);
        Assert.Empty(entry.ComparabilityMarkers.DiagnosticOnly);

        var record = Assert.Single(sink.Records);
        Assert.Null(record.CappedConfidence); // no cap applied.
        Assert.NotNull(record.ComparabilityMarkers); // ...but the clean scan IS recorded.
    }

    [Fact]
    public async Task ComparabilityCap_CapBelowGate_SuppressesSignal_AndCachesNoSignalWithPolicy()
    {
        // A cap configured BELOW MinConfidence (operator's choice) suppresses capped signals entirely: the
        // gate applies AFTER the cap, so the capped 0.5 fails the default 0.6 gate — the existing
        // no-directional-signal path (cached NoDirectionalSignal, debug record emitted).
        var evidence = EarningsFiling();
        var body = PlausibleBody("Net income rose sharply, aided by a one-time gain on sale of a facility.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Net income rose sharply."));
        var options = new DirectionalFilingSignalOptions { ComparabilityConfidenceCap = 0.5m };
        var cache = new FakeAnalyzedFilingCache();
        var sink = new SpyFilingReadDebugSink();

        var result = await CreateSource(reader, analyzer, options, cache, sink)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        // Spec 204: the suppressed DIRECTION is still a read — persisted as a Neutral signal carrying the
        // CAPPED confidence (the value the gate saw) in its prefix and envelope.
        var produced = Assert.Single(result);
        Assert.Equal("Neutral", produced.Signal.Direction);
        Assert.Equal(
            "AI earnings read: Improving 0.5 (below MinConfidence 0.6) — Net income rose sharply.",
            produced.Signal.Reason);
        AssertReadMetadata(produced.Signal, "below-confidence", "Improving", "0.5");

        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, entry.Outcome);
        Assert.Null(entry.Signal);
        Assert.Equal(EarningsComparabilityScan.Policy(0.5m), entry.ComparabilityPolicy);
        Assert.NotNull(entry.ComparabilityMarkers);
        Assert.Equal(new[] { "one-time", "gain on sale" }, entry.ComparabilityMarkers!.CapTriggering);
        Assert.Equal(FilingNoSignalCause.BelowConfidence, entry.NoSignalCause);
        Assert.Equal("Improving", entry.ReadDirection);
        Assert.Equal(0.5m, entry.ReadConfidence); // the CAPPED value the gate compared, not the raw 0.9.

        var record = Assert.Single(sink.Records);
        Assert.Equal(FilingReadOutcome.BelowConfidence, record.Outcome);
        Assert.Equal(0.9m, record.Confidence);       // the model's raw read (the debug store keeps the raw)...
        Assert.Equal(0.5m, record.CappedConfidence); // ...capped below the gate.
    }

    [Fact]
    public async Task ComparabilityCap_OffSwitchAtOne_IsByteIdenticalToPre160()
    {
        // 1.0 is the exact off-switch: min(conf, 1.0) is the identity, so even with cap-triggering markers in
        // the body the FULL signal is byte-identical to pre-spec-160 behaviour — no capped confidence, no
        // reason annotation, nothing else moved.
        var evidence = EarningsFiling();
        var body = PlausibleBody(
            "Net income doubled, including an impairment reversal and a litigation settlement recovery.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Net income doubled."));
        var options = new DirectionalFilingSignalOptions { ComparabilityConfidenceCap = 1.0m };
        var cache = new FakeAnalyzedFilingCache();

        var result = await CreateSource(reader, analyzer, options, cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        // The full pre-160 signal, field for field.
        Assert.Equal(evidence.SourceName, produced.Signal.CompanyMention);
        Assert.Equal("GuidanceChange", produced.Signal.SignalType);
        Assert.Equal("Positive", produced.Signal.Direction);
        Assert.Equal(8, produced.Signal.Strength);
        Assert.Equal(6, produced.Signal.Novelty);
        Assert.Equal(0.9m, produced.Signal.Confidence);
        Assert.Equal(evidence.Title, produced.Signal.SupportingExcerpt);
        Assert.Equal("Net income doubled.", produced.Signal.Reason);

        // The scan outcome is still recorded on the cache record (provenance), it just never caps.
        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(EarningsComparabilityScan.Policy(1.0m), entry.ComparabilityPolicy);
        Assert.NotNull(entry.ComparabilityMarkers);
        Assert.Equal(new[] { "impairment", "litigation settlement" }, entry.ComparabilityMarkers!.CapTriggering);
    }

    [Fact]
    public async Task ComparabilityCap_IsACeilingNotAFloor_ReadBelowCapStaysUnchanged()
    {
        // Gate ordering, part 1: read 0.62 with markers and cap 0.65 stays 0.62 — min(0.62, 0.65) = 0.62.
        // The cap did not move the number, so the reason is deliberately unannotated (the spec-149 rule:
        // name a transform only when it changed the result).
        var evidence = EarningsFiling();
        var body = PlausibleBody("Earnings improved despite a divestiture completed during the quarter.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.62m, "Earnings improved."));

        var result = await CreateSource(reader, analyzer)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal(0.62m, produced.Signal.Confidence);
        Assert.Equal("Earnings improved.", produced.Signal.Reason);
    }

    [Fact]
    public async Task ComparabilityCap_GateAppliesAfterCap_CappedReadBelowRaisedGateIsSuppressed()
    {
        // Gate ordering, part 2: read 0.90, markers, cap 0.65, gate raised to 0.7 — the CAPPED value fails the
        // gate, so the signal is suppressed even though the raw read cleared it.
        var evidence = EarningsFiling();
        var body = PlausibleBody("Record profit included a non-recurring legal settlement gain.");
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Record profit."));
        var options = new DirectionalFilingSignalOptions { MinConfidence = 0.7m };
        var cache = new FakeAnalyzedFilingCache();

        var result = await CreateSource(reader, analyzer, options, cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        // Spec 204: no DIRECTIONAL signal — the capped 0.65 fails the raised 0.7 gate — but the read itself
        // persists as a Neutral signal naming the below-confidence cause.
        var produced = Assert.Single(result);
        Assert.Equal("Neutral", produced.Signal.Direction);
        AssertReadMetadata(produced.Signal, "below-confidence", "Improving", "0.65");
        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, entry.Outcome);
        Assert.Equal(FilingNoSignalCause.BelowConfidence, entry.NoSignalCause);
    }

    [Fact]
    public async Task ComparabilityCap_MarkerBeyondAnalyzerTruncationPoint_StillCaps()
    {
        // The scan runs on the FULL stripped body, BEFORE the analyzer's MaxInputLength truncation (default
        // 12000): a cap-triggering marker placed past that point must still cap.
        var evidence = EarningsFiling();
        var body = PlausibleBody("Revenue rose 40% and the company raised guidance.")
            + new string('x', 13000)
            + " The quarter also reflected a litigation settlement.";
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(body, "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(
            new FilingSentiment(FilingDirection.Improving, 0.9m, "Revenue rose 40%."));

        var result = await CreateSource(reader, analyzer)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal(0.65m, produced.Signal.Confidence);
        Assert.Contains("comparability cap: matched 'litigation settlement'", produced.Signal.Reason, StringComparison.Ordinal);
    }

    // ---- spec 160: cache policy rules — the full outcome × cause matrix ------------------------------------

    /// <summary>A cached no-signal record stamped with <paramref name="policy"/> (null = pre-160 legacy).</summary>
    private static AnalyzedFilingRecord CachedNoSignalRecord(string accession, string? policy) =>
        new(
            accession,
            AnalyzedFilingOutcome.NoDirectionalSignal,
            null,
            null,
            AnalyzedFilingRecord.CurrentCacheVersion,
            policy,
            policy is null ? null : new ComparabilityMarkers([], []));

    [Fact]
    public async Task CachePolicy_NullPolicyRecord_IsAHit_ReplaysUnchanged()
    {
        // Heal forward: a pre-160 record (null policy) is a HIT — the accrued cache is never mass-invalidated.
        // The replayed signal is the stored one, untouched (no retro-capping of legacy reads).
        var accession = "0001049521-26-000011";
        var evidence = EarningsFiling(accession: accession);
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[accession] = CachedSignalRecord(accession); // ComparabilityPolicy defaults to null.
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("irrelevant"), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "n/a"));

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        var produced = Assert.Single(result);
        Assert.Equal(0, reader.ReadCount);   // no re-fetch...
        Assert.Equal(0, analyzer.AnalyzeCount);
        Assert.Equal(0.9m, produced.Signal.Confidence); // ...and the stored signal replays unchanged.
        Assert.Equal("cached rationale", produced.Signal.Reason);
    }

    [Fact]
    public async Task CachePolicy_MatchingPolicyRecord_IsAHit()
    {
        var accession = "0001049521-26-000011";
        var evidence = EarningsFiling(accession: accession);
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[accession] = CachedSignalRecord(accession) with
        {
            ComparabilityPolicy = CurrentDefaultPolicy,
            ComparabilityMarkers = new ComparabilityMarkers([], []),
        };
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("irrelevant"), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "n/a"));

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(0, reader.ReadCount); // a matching policy replays with no fetch.
    }

    public static TheoryData<string, string> PolicyMismatchCauses() => new()
    {
        // Cause: the operator tuned the cap (same scanner version, different cap magnitude).
        { "cap change", "cmpscan-v1;cap=0.5" },
        // Cause: the scanner rule tables changed (a cmpscan version bump, same cap magnitude).
        { "scanner-version change", "cmpscan-v0;cap=0.65" },
    };

    [Theory]
    [MemberData(nameof(PolicyMismatchCauses))]
    public async Task CachePolicy_MismatchedProducedSignalRecord_IsAMiss_ReanalyzedUnderCurrentPolicy(
        string cause, string storedPolicy)
    {
        // Outcome × cause matrix, DirectionalSignalProduced rows: a produced-signal record whose non-null
        // policy differs from the current one must be re-fetched and re-analyzed under the current policy.
        Assert.NotEqual(CurrentDefaultPolicy, storedPolicy); // guard the fixture itself.
        var accession = "0001049521-26-000011";
        var evidence = EarningsFiling(accession: accession);
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[accession] = CachedSignalRecord(accession) with
        {
            ComparabilityPolicy = storedPolicy,
            ComparabilityMarkers = new ComparabilityMarkers(["litigation settlement"], []),
        };
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(
                PlausibleBody("Strong results included a litigation settlement recovery."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Strong results."));

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);      // re-fetched (a genuine pass-2 miss for cause: {cause})...
        Assert.Equal(1, analyzer.AnalyzeCount); // ...and re-analyzed,
        var produced = Assert.Single(result);
        Assert.Equal(0.65m, produced.Signal.Confidence); // under the CURRENT policy (default cap 0.65).
        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(CurrentDefaultPolicy, entry.ComparabilityPolicy); // the record is re-stamped.
        Assert.NotNull(cause); // (theory label; keeps the cause visible in test output)
    }

    [Theory]
    [MemberData(nameof(PolicyMismatchCauses))]
    public async Task CachePolicy_MismatchedNoSignalRecord_IsAMiss_AndMayNowEmit(string cause, string storedPolicy)
    {
        // Outcome × cause matrix, NoDirectionalSignal rows — the cells a produced-signal-only suite would
        // silently miss: a read SUPPRESSED under an old policy (e.g. a lower cap) must be re-analyzed when the
        // policy changes, and may now emit under the current one.
        Assert.NotEqual(CurrentDefaultPolicy, storedPolicy);
        var accession = "0001049521-26-000011";
        var evidence = EarningsFiling(accession: accession);
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[accession] = CachedNoSignalRecord(accession, storedPolicy);
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(
                PlausibleBody("Strong results included a litigation settlement recovery."), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "Strong results."));

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);      // the stale no-signal verdict did NOT replay...
        Assert.Equal(1, analyzer.AnalyzeCount);
        var produced = Assert.Single(result);   // ...and the filing now emits under the current policy
        Assert.Equal(0.65m, produced.Signal.Confidence); // (capped at the current default 0.65).
        var entry = Assert.Single(cache.Entries.Values);
        Assert.Equal(AnalyzedFilingOutcome.DirectionalSignalProduced, entry.Outcome);
        Assert.Equal(CurrentDefaultPolicy, entry.ComparabilityPolicy);
        Assert.NotNull(cause);
    }

    [Fact]
    public async Task CachePolicy_NullPolicyNoSignalRecord_IsAHit_NothingRefetched()
    {
        // The legacy no-signal cell of the heal-forward rule: a pre-160 confirmed no-signal replays as
        // "nothing" with no fetch — legacy verdicts are honoured, not retro-scanned.
        var accession = "0001049521-26-000011";
        var evidence = EarningsFiling(accession: accession);
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[accession] = CachedNoSignalRecord(accession, policy: null);
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("irrelevant"), "EX-99.1", "ex991.htm"));
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Improving, 0.9m, "n/a"));

        var result = await CreateSource(reader, analyzer, cache: cache)
            .ProduceAsync([evidence], AsOf, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, analyzer.AnalyzeCount);
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
