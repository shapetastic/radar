using Radar.Application.Evidence;
using Radar.Application.Identity;

namespace Radar.Application.Tests.Evidence;

/// <summary>
/// Content-derived evidence identity (spec 145): the same content resolves to the same evidence id in
/// every process, forever, and to an id that can never be confused with another deterministic-Guid family.
/// </summary>
public sealed class EvidenceIdentityTests
{
    private const string Hash =
        "3b8f2a1c9d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8";

    [Fact]
    public void ForContentHash_SameHash_SameId_AcrossCalls()
    {
        Assert.Equal(EvidenceIdentity.ForContentHash(Hash), EvidenceIdentity.ForContentHash(Hash));
    }

    [Fact]
    public void ForContentHash_DifferentHash_DifferentId()
    {
        Assert.NotEqual(
            EvidenceIdentity.ForContentHash(Hash),
            EvidenceIdentity.ForContentHash(Hash[..^1] + "9"));
    }

    [Fact]
    public void ForContentHash_IsNamespaced_SoItCannotCollideWithTheRawHashOrTheSeedChildFamily()
    {
        // The namespace is the whole point: without it an evidence id would be derived from the same string
        // space as any other deterministic-Guid family, and a collision would silently merge two unrelated
        // records. Proving the namespaced id differs from the un-namespaced one proves the prefix is
        // actually folded in rather than decorative.
        Assert.NotEqual(
            EvidenceIdentity.ForContentHash(Hash),
            DeterministicGuid.FromCanonicalString(Hash));

        // …and it is not in the seed alias/feed family's string space either ("{companyId}|{kind}|{value}").
        Assert.NotEqual(
            EvidenceIdentity.ForContentHash(Hash),
            DeterministicGuid.FromCanonicalString($"{Guid.Empty}|seed|{Hash}"));
    }

    [Fact]
    public void ForContentHash_IsThePinnedComposition_NamespacePlusHash()
    {
        // Pins the canonical form itself. Changing the namespace token or the concatenation re-mints every
        // evidence id ever derived, so it is a persisted format constant, not an implementation detail.
        Assert.Equal(
            DeterministicGuid.FromCanonicalString("radar:evidence:" + Hash),
            EvidenceIdentity.ForContentHash(Hash));
    }

    [Fact]
    public void ForContentHash_PinnedValue_DoesNotMove()
    {
        Assert.Equal(
            Guid.Parse("0f16f496-ce53-cbc9-f09e-c3db28721c76"),
            EvidenceIdentity.ForContentHash("0123456789abcdef"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForContentHash_NullOrEmpty_Throws(string? contentHash)
    {
        // Evidence with no content hash could not dedupe, and giving every such item ONE shared identity
        // would merge unrelated items into a single record — the exact failure this slice exists to fix.
        Assert.ThrowsAny<ArgumentException>(() => EvidenceIdentity.ForContentHash(contentHash!));
    }
}
