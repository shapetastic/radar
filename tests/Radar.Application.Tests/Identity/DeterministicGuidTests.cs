using Radar.Application.Identity;

namespace Radar.Application.Tests.Identity;

/// <summary>
/// The shared canonical-string → <see cref="Guid"/> step (spec 145). Its ALGORITHM is a compatibility
/// contract: every persisted alias/source-feed Id and every evidence Id derives from it, so a change to the
/// hash, the encoding, or the byte-to-Guid reinterpretation would orphan existing rows and files. These
/// tests pin the algorithm by VALUE, not merely by self-consistency — a self-consistent test would stay
/// green through exactly the change that breaks production.
/// </summary>
public sealed class DeterministicGuidTests
{
    [Fact]
    public void FromCanonicalString_IsDeterministic_AcrossCalls()
    {
        Assert.Equal(
            DeterministicGuid.FromCanonicalString("radar:test:alpha"),
            DeterministicGuid.FromCanonicalString("radar:test:alpha"));
    }

    [Fact]
    public void FromCanonicalString_DifferentInputs_DifferentGuids()
    {
        Assert.NotEqual(
            DeterministicGuid.FromCanonicalString("radar:test:alpha"),
            DeterministicGuid.FromCanonicalString("radar:test:beta"));
    }

    [Fact]
    public void FromCanonicalString_IsCaseAndWhitespaceSensitive_CanonicalisationIsTheCallersJob()
    {
        // Deliberate: normalisation (trim / lowercase / namespacing) belongs to each caller's canonical
        // form, because what counts as "the same" differs per family. This helper only hashes.
        Assert.NotEqual(
            DeterministicGuid.FromCanonicalString("Alpha"),
            DeterministicGuid.FromCanonicalString("alpha"));
        Assert.NotEqual(
            DeterministicGuid.FromCanonicalString("alpha"),
            DeterministicGuid.FromCanonicalString(" alpha"));
    }

    [Theory]
    // Pinned by value. These are UTF-8 → MD5 → new Guid(byte[]) — the exact algorithm
    // LocalFileCompanySeedSource has used since spec 23, extracted verbatim by spec 145.
    [InlineData("11111111-1111-1111-1111-111111111111|seed|acme", "afd89302-6928-848b-4b64-fcc4627bbaf9")]
    [InlineData(
        "11111111-1111-1111-1111-111111111111|feed|rss|https://example.com/acme.rss",
        "96141952-89e3-da03-def0-9ac53fd155af")]
    [InlineData("radar:evidence:0123456789abcdef", "0f16f496-ce53-cbc9-f09e-c3db28721c76")]
    public void FromCanonicalString_PinnedValues_DoNotMove(string canonical, string expected)
    {
        Assert.Equal(Guid.Parse(expected), DeterministicGuid.FromCanonicalString(canonical));
    }

    [Fact]
    public void FromCanonicalString_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DeterministicGuid.FromCanonicalString(null!));
    }

    [Fact]
    public void FromCanonicalString_EmptyString_IsAllowed_AndStable()
    {
        // Empty is a legitimate canonical string for this low-level helper; the callers that must reject
        // it (EvidenceIdentity) do so themselves, with their own domain reason.
        Assert.Equal(
            DeterministicGuid.FromCanonicalString(string.Empty),
            DeterministicGuid.FromCanonicalString(string.Empty));
    }
}
