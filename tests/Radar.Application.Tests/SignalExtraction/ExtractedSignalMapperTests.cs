using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.SignalExtraction;

public class ExtractedSignalMapperTests
{
    private static readonly DateTimeOffset PublishedAt =
        new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CollectedAt =
        new(2026, 1, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private const string RawText =
        "Acme Corp announced that it signed a major new customer this quarter, " +
        "expanding its enterprise footprint significantly.";

    private static EvidenceItem MakeEvidence(DateTimeOffset? publishedAtUtc = null) =>
        new EvidenceBuilder()
            .WithTitle("Acme signs new customer")
            .WithSummary("A new enterprise customer win.")
            .WithRawText(RawText)
            .WithPublishedAtUtc(publishedAtUtc)
            .WithCollectedAtUtc(CollectedAt)
            .Build();

    private static ExtractedSignal MakeExtracted(
        string companyMention = "Acme Corp",
        string signalType = "CustomerWin",
        string direction = "Positive",
        int strength = 7,
        int novelty = 6,
        decimal confidence = 0.8m,
        string supportingExcerpt = "signed a major new customer this quarter",
        string reason = "Customer win indicates traction.") =>
        new(
            CompanyMention: companyMention,
            SignalType: signalType,
            Direction: direction,
            Strength: strength,
            Novelty: novelty,
            Confidence: confidence,
            SupportingExcerpt: supportingExcerpt,
            Reason: reason);

    [Fact]
    public void ValidExtractedSignal_MapsToSignal()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted();

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        var signal = result.Signal!;
        Assert.Equal(evidence.Id, signal.EvidenceId);
        Assert.Null(signal.CompanyId);
        Assert.Equal(SignalType.CustomerWin, signal.Type);
        Assert.Equal(SignalDirection.Positive, signal.Direction);
        Assert.Equal(SignalReviewStatus.Pending, signal.ReviewStatus);
        Assert.Equal(CreatedAt, signal.CreatedAtUtc);
        Assert.Equal(PublishedAt, signal.ObservedAtUtc);
        Assert.Equal("Acme Corp", signal.CompanyMention);
    }

    [Fact]
    public void NullPublishedAt_UsesCollectedAtForObservedAt()
    {
        var evidence = MakeEvidence(publishedAtUtc: null);
        var extracted = MakeExtracted();

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.True(result.IsValid);
        Assert.Equal(CollectedAt, result.Signal!.ObservedAtUtc);
    }

    [Fact]
    public void EnumParsing_IsCaseInsensitive()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(signalType: "customerwin", direction: "POSITIVE");

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.True(result.IsValid);
        Assert.Equal(SignalType.CustomerWin, result.Signal!.Type);
        Assert.Equal(SignalDirection.Positive, result.Signal!.Direction);
    }

    [Fact]
    public void UnknownSignalType_IsInvalid()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(signalType: "TotallyMadeUp");

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void NumericSignalType_IsRejected()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(signalType: "999");

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void UnknownDirection_IsInvalid()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(direction: "Sideways");

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ExcerptNotInEvidence_IsInvalid()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(
            supportingExcerpt: "fabricated quote that is not in the body at all");

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.Contains(result.Errors, e => e.Contains("not found in evidence"));
    }

    [Fact]
    public void ExcerptDifferingOnlyInWhitespaceAndCasing_IsValid()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(
            supportingExcerpt: "SIGNED a   major\tnew\ncustomer this QUARTER");

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceCompanyMention_IsInvalid(string mention)
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(companyMention: mention);

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void OutOfRangeStrength_IsInvalid(int strength)
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(strength: strength);

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void OutOfRangeNovelty_IsInvalid(int novelty)
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(novelty: novelty);

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void OutOfRangeConfidence_IsInvalid()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted(confidence: 1.5m);

        var result = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt);

        Assert.False(result.IsValid);
        Assert.Null(result.Signal);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void SameInputsAndClock_ProduceEqualFieldsIgnoringId()
    {
        var evidence = MakeEvidence(PublishedAt);
        var extracted = MakeExtracted();

        var first = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt).Signal!;
        var second = ExtractedSignalMapper.ToSignal(extracted, evidence, CreatedAt).Signal!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first with { Id = Guid.Empty }, second with { Id = Guid.Empty });
    }

    [Fact]
    public void NullExtracted_Throws()
    {
        var evidence = MakeEvidence(PublishedAt);
        Assert.Throws<ArgumentNullException>(
            () => ExtractedSignalMapper.ToSignal(null!, evidence, CreatedAt));
    }

    [Fact]
    public void NullEvidence_Throws()
    {
        var extracted = MakeExtracted();
        Assert.Throws<ArgumentNullException>(
            () => ExtractedSignalMapper.ToSignal(extracted, null!, CreatedAt));
    }
}
