using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Filings;

/// <summary>
/// Spec 204: the ONE key-set / envelope / magnitude definition for a persisted non-directional AI earnings
/// read. The load-bearing test here is the MAGNITUDE PIN: the read signal's Strength/Novelty/Confidence must
/// equal what the keyword fallback ("results of operations") actually emits — the extractor's rule values
/// are private, so the equality is pinned through its PUBLIC surface. If either side moves, this fails and
/// the drift is a conscious decision instead of a silent score change.
/// </summary>
public sealed class FilingReadSignalMetadataTests
{
    // ---- the magnitude pin -----------------------------------------------------------------------------

    [Fact]
    public async Task Magnitudes_EqualTheKeywordFallbacks_ThroughTheExtractorsPublicSurface()
    {
        // Evidence whose searchable text matches ONLY the "results of operations" GuidanceChange rule —
        // the spec-57 deterministic Neutral every earnings 8-K gets.
        var evidence = new EvidenceBuilder()
            .WithTitle("8-K — Report")
            .WithRawText("Acme announced results of operations for the quarter.")
            .WithCollectedAtUtc(new DateTimeOffset(2026, 1, 16, 12, 0, 0, TimeSpan.Zero))
            .Build();

        var output = await new KeywordSignalExtractor(
                NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights())
            .ExtractAsync(evidence, CancellationToken.None);

        var fallback = Assert.Single(output.Signals);
        Assert.Equal(SignalType.GuidanceChange.ToString(), fallback.SignalType);
        Assert.Equal("Neutral", fallback.Direction);

        // THE PIN: the spec-204 read signal's declared magnitudes are the keyword fallback's, exactly —
        // NOT the directional read's DirectionalFilingSignalOptions.Strength/Novelty. This is what makes
        // "the score does not move" provable rather than asserted.
        Assert.Equal(FilingReadSignalMetadata.Strength, fallback.Strength);
        Assert.Equal(FilingReadSignalMetadata.Novelty, fallback.Novelty);
        Assert.Equal(FilingReadSignalMetadata.Confidence, fallback.Confidence);
    }

    // ---- the envelope ----------------------------------------------------------------------------------

    [Fact]
    public void Compose_WritesTheFourKeys_ThroughTheSharedEnvelope_WithG29Confidence()
    {
        var json = FilingReadSignalMetadata.Compose(
            FilingNoSignalCause.Mixed, "Mixed", 0.85m, "openai:deepseek-ai/DeepSeek-V4-Flash");

        Assert.True(EvidenceMetadata.TryRead(json, out var metadata, out var hints));
        Assert.Empty(hints); // a signal carries no collector company hints (the news-metadata precedent).
        Assert.Equal("mixed", metadata[FilingReadSignalMetadata.OutcomeKey]);
        Assert.Equal("Mixed", metadata[FilingReadSignalMetadata.DirectionKey]);
        Assert.Equal("0.85", metadata[FilingReadSignalMetadata.ConfidenceKey]);
        Assert.Equal("openai:deepseek-ai/DeepSeek-V4-Flash", metadata[FilingReadSignalMetadata.ModelKey]);
    }

    [Theory]
    [InlineData(FilingNoSignalCause.Mixed, "mixed")]
    [InlineData(FilingNoSignalCause.Unknown, "unknown")]
    [InlineData(FilingNoSignalCause.BelowConfidence, "below-confidence")]
    public void OutcomeTokenFor_MapsEveryPersistableCause(FilingNoSignalCause cause, string expected) =>
        Assert.Equal(expected, FilingReadSignalMetadata.OutcomeTokenFor(cause));

    [Fact]
    public void OutcomeTokenFor_EmptyBody_Throws()
    {
        // EmptyBody is vocabulary, never a persisted read: no model call happened, so composing an envelope
        // for it would fabricate a read.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FilingReadSignalMetadata.OutcomeTokenFor(FilingNoSignalCause.EmptyBody));
    }

    [Fact]
    public void Compose_ConfidenceIsG29Invariant_TrailingZerosNormalised()
    {
        // G29 is the decimal round-trip format the scoring descriptor already uses: 0.30m renders "0.3",
        // 0m renders "0" — deterministic, culture-independent (AD-3).
        var json = FilingReadSignalMetadata.Compose(FilingNoSignalCause.Unknown, "Unknown", 0.30m, "");
        Assert.True(EvidenceMetadata.TryRead(json, out var metadata, out _));
        Assert.Equal("0.3", metadata[FilingReadSignalMetadata.ConfidenceKey]);
    }

    // ---- the predicate the supersede routes through -----------------------------------------------------

    [Fact]
    public void CarriesReadOutcome_TrueForAComposedEnvelope()
    {
        var json = FilingReadSignalMetadata.Compose(FilingNoSignalCause.Unknown, "Unknown", 0.3m, "m");
        Assert.True(FilingReadSignalMetadata.CarriesReadOutcome(json));
    }

    [Theory]
    [InlineData(null)]                                                          // not recorded
    [InlineData("")]                                                            // blank
    [InlineData("   ")]                                                         // whitespace
    [InlineData("{ not json")]                                                  // unreadable
    [InlineData("""{ "metadata": { "quality": "High" }, "companyHints": [] }""")] // unrelated bag
    [InlineData("""{ "metadata": { "filingReadOutcome": " " }, "companyHints": [] }""")] // blank value records nothing
    public void CarriesReadOutcome_FalseForAbsentUnreadableOrUnrelatedEnvelopes(string? metadataJson) =>
        Assert.False(FilingReadSignalMetadata.CarriesReadOutcome(metadataJson));

    [Fact]
    public void IsFilingReadSignal_ReadsTheSignalsOwnEnvelope()
    {
        var read = new SignalBuilder()
            .WithType(SignalType.GuidanceChange)
            .WithMetadataJson(FilingReadSignalMetadata.Compose(
                FilingNoSignalCause.BelowConfidence, "Improving", 0.5m, "m"))
            .Build();
        var plain = new SignalBuilder().WithType(SignalType.GuidanceChange).Build();

        Assert.True(FilingReadSignalMetadata.IsFilingReadSignal(read));
        Assert.False(FilingReadSignalMetadata.IsFilingReadSignal(plain));
    }
}
