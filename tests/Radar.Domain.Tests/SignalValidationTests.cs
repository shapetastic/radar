using System.Globalization;
using Radar.Domain.Signals;
using Radar.Domain.Validation;
using Radar.TestSupport;

namespace Radar.Domain.Tests;

public class SignalValidationTests
{
    private static Signal CreateValidSignal(
        int strength = 5,
        int novelty = 5,
        decimal confidence = 0.5m,
        string supportingExcerpt = "An excerpt from the source evidence.",
        Guid? evidenceId = null) =>
        new SignalBuilder()
            .WithStrength(strength)
            .WithNovelty(novelty)
            .WithConfidence(confidence)
            .WithSupportingExcerpt(supportingExcerpt)
            .WithEvidenceId(evidenceId ?? Guid.NewGuid())
            .Build();

    [Fact]
    public void ValidSignal_HasNoErrors_AndIsValid()
    {
        var signal = CreateValidSignal();

        Assert.Empty(SignalValidation.Validate(signal));
        Assert.True(SignalValidation.IsValid(signal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Strength_OutOfRange_ProducesError(int strength)
    {
        var signal = CreateValidSignal(strength: strength);

        Assert.Contains("Strength must be between 1 and 10.", SignalValidation.Validate(signal));
        Assert.False(SignalValidation.IsValid(signal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Novelty_OutOfRange_ProducesError(int novelty)
    {
        var signal = CreateValidSignal(novelty: novelty);

        Assert.Contains("Novelty must be between 1 and 10.", SignalValidation.Validate(signal));
        Assert.False(SignalValidation.IsValid(signal));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    public void Confidence_OutOfRange_ProducesError(string confidence)
    {
        var signal = CreateValidSignal(
            confidence: decimal.Parse(confidence, CultureInfo.InvariantCulture));

        Assert.Contains("Confidence must be between 0 and 1.", SignalValidation.Validate(signal));
        Assert.False(SignalValidation.IsValid(signal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SupportingExcerpt_EmptyOrWhitespace_ProducesError(string excerpt)
    {
        var signal = CreateValidSignal(supportingExcerpt: excerpt);

        Assert.Contains("Supporting excerpt must not be empty.", SignalValidation.Validate(signal));
        Assert.False(SignalValidation.IsValid(signal));
    }

    [Fact]
    public void EvidenceId_Empty_ProducesMustReferenceEvidenceError()
    {
        var signal = CreateValidSignal(evidenceId: Guid.Empty);

        Assert.Contains("Every signal must reference evidence.", SignalValidation.Validate(signal));
        Assert.False(SignalValidation.IsValid(signal));
    }
}
