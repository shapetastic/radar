using Radar.Application.Acquisitions;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 217 §2 — the <c>acq=</c> fingerprint segment. It is pinned HERE (its own contents) so
/// <see cref="SignalSourceDescriptorTests"/> and <see cref="ScoringConfigFingerprintTests"/> can compose it
/// from the real type and still describe production.
/// </summary>
public sealed class AcquisitionScoringIdentityTests
{
    [Fact]
    public void Segment_NamesBothVersions_InAFixedOrder()
    {
        Assert.Equal("acq=acqscan-v1;supersede=acq-supersede-v1;", AcquisitionScoringIdentity.Segment);
    }

    [Fact]
    public void Segment_IsComposedFromTheShippedConstants_NotACopyOfThem()
    {
        // A copied version literal is a fact with no owner (CLAUDE.md): a bump to either constant must move
        // the pins on its own, so the segment is asserted to CONTAIN the live values rather than a copy.
        Assert.Contains(
            AcquisitionAgreementScan.Version, AcquisitionScoringIdentity.Segment, StringComparison.Ordinal);
        Assert.Contains(
            CorporateActionSupersede.Version, AcquisitionScoringIdentity.Segment, StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_IsUnconditional_SoBothPinFamiliesSeeIt()
    {
        // There is no "disabled" form: the corporate-action supersede is pure assembly code that runs in
        // every composition, and only the DATA varies. That is exactly why BOTH the AI-ON and the AI-OFF pin
        // families moved for spec 217 (unlike 197/214/215/216, whose inputs ride news= or ai=).
        Assert.False(string.IsNullOrEmpty(AcquisitionScoringIdentity.Segment));
        Assert.EndsWith(";", AcquisitionScoringIdentity.Segment, StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_CarriesNoAcquisitionCONTENT()
    {
        // Hashing an OUTCOME would re-stamp every strategy the moment one company was taken over — a fact
        // about the world, not about the scoring hypothesis.
        foreach (var leak in new[] { "MarineMax", "HZO", "Safe Harbor", "53.00", "0001193125" })
        {
            Assert.DoesNotContain(leak, AcquisitionScoringIdentity.Segment, StringComparison.OrdinalIgnoreCase);
        }
    }
}
